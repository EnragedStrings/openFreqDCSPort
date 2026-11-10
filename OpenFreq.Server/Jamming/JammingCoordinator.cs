using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Jamming;
using OpenFreq.Common.Satcom;

namespace OpenFreqServer.Jamming;

/// <summary>
/// Owns the server's jammer set (config now; the DCS mission hook in phase 6) and turns it into the
/// <see cref="JammerCatalogUpdateMessage"/> SignalingServer broadcasts. A jammer radiates on its configured spots and
/// bands (<see cref="ScriptedTargeting"/>) while it is enabled, alive and inside its schedule; an auto-mode jammer
/// also radiates on the spots its <see cref="AutoTargetingBrain"/> picked from the transmissions it can hear
/// (reported here by SignalingServer, with the transmitter positions). The clock is injectable so schedules and the
/// brain are testable.
/// </summary>
public sealed class JammingCoordinator
{
    /// <summary>A "transmitting" state not refreshed for this long is treated as ended (clients re-send it
    /// ~3×/s as a heartbeat; this catches a lost key-up).</summary>
    public static readonly TimeSpan TransmissionStaleAfter = TimeSpan.FromSeconds(5);

    /// <summary>Asks whether the terrain between a jammer and a transmitter is clear: (jammer id, transmitter id,
    /// jammer lat/lon/alt, transmitter lat/lon/alt) → true clear, false blocked, null unknown. Must not block.</summary>
    public delegate bool? LineOfSightProvider(string jammerId, string transmitterId, double jammerLat,
        double jammerLon, double jammerAlt, double txLat, double txLon, double txAlt);

    private sealed class Keying
    {
        public required string TransmitterId;
        public required int FrequencyKhz;
        public required DateTimeOffset Start;
        public DateTimeOffset? End;
        public DateTimeOffset Refreshed;
    }

    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<JammerDefinition> _jammers = [];
    private readonly Dictionary<string, AutoTargetingBrain> _brains = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tx, int Khz), Keying> _active = [];
    private readonly List<Keying> _ended = [];
    private readonly Dictionary<string, (double Lat, double Lon, double Alt)> _positions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _goneTransmitters = new(StringComparer.Ordinal);
    private DateTimeOffset _start;
    private string? _lastPublishedSignature;
    private IReadOnlyList<SatcomJammerSource> _satcomSources = [];

    public JammingCoordinator(JammingServerConfig config, ILogger logger, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
        _start = _clock();
        Load(config.Jammers);
    }

    /// <summary>Periodic re-broadcast interval.</summary>
    public TimeSpan BroadcastInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Terrain line of sight for the auto brain's detection (null = horizon only).</summary>
    public LineOfSightProvider? LineOfSight { get; set; }

    /// <summary>Resolved jammers currently known.</summary>
    public IReadOnlyList<JammerDefinition> Jammers
    {
        get
        {
            lock (_lock) return _jammers.ToList();
        }
    }

    /// <summary>The radiating, geodetically located jammers as of the last <see cref="Snapshot"/> (refreshed every
    /// broadcast tick), for the SATCOM link engine (phase 4). Jammers placed only by DCS/BMS coordinates are not
    /// included: the server cannot place them relative to a satellite.</summary>
    public IReadOnlyList<SatcomJammerSource> SatcomSources => Volatile.Read(ref _satcomSources);

    /// <summary>Replaces the jammer set with <paramref name="definitions"/> (resolved against their presets) and
    /// restarts schedules and auto brains.</summary>
    public void Load(IEnumerable<JammerDefinition> definitions)
    {
        int count;
        lock (_lock)
        {
            _jammers.Clear();
            _brains.Clear();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            _start = _clock();
            foreach (var definition in definitions)
            {
                index++;
                var resolved = JammerPresets.Resolve(definition);
                if (string.IsNullOrWhiteSpace(resolved.Id))
                    resolved.Id = $"jammer-{index}";
                if (!ids.Add(resolved.Id))
                {
                    _logger.LogWarning("Jamming: duplicate jammer id {Id} ignored", resolved.Id);
                    continue;
                }

                if (definition.PresetId != null && JammerPresets.Find(definition.PresetId) == null)
                    _logger.LogWarning("Jamming: jammer {Id} names unknown preset {Preset}; using generic values",
                        resolved.Id, definition.PresetId);
                if (resolved.Latitude == null && resolved.DcsPosition == null && resolved.BmsPosition == null)
                    _logger.LogWarning("Jamming: jammer {Id} has no position; no client can place it", resolved.Id);

                if (resolved.Targeting.Mode == JammerTargetingMode.Auto)
                {
                    if (resolved.Latitude == null || resolved.Longitude == null)
                        _logger.LogWarning(
                            "Jamming: auto jammer {Id} has no latitude/longitude; it cannot locate transmitters " +
                            "and will detect nothing", resolved.Id);
                    _brains[resolved.Id] = new AutoTargetingBrain(_start);
                }

                _jammers.Add(resolved);
            }

            _lastPublishedSignature = null;
            _satcomSources = [];
            count = _jammers.Count;
        }

        _logger.LogInformation("Jamming: {Count} jammer(s) loaded", count);
    }

    /// <summary>Switches one jammer on or off (config change, or its unit died). Returns false if unknown.</summary>
    public bool SetJammerState(string id, bool? enabled = null, bool? alive = null)
    {
        lock (_lock)
        {
            var jammer = _jammers.FirstOrDefault(j => j.Id == id);
            if (jammer == null) return false;
            if (enabled != null) jammer.Enabled = enabled.Value;
            if (alive != null) jammer.Alive = alive.Value;
            return true;
        }
    }

    // ------------------------------------------------------------------ transmissions (auto brain input)

    /// <summary>A transmitter keyed (or re-sent its heartbeat) or unkeyed on a frequency. A position, when given,
    /// becomes the transmitter's current position.</summary>
    public void ReportTransmission(string transmitterId, int frequencyKhz, bool transmitting,
        double? latitude = null, double? longitude = null, double? altitudeMeters = null)
    {
        var now = _clock();
        lock (_lock)
        {
            if (latitude is { } lat && longitude is { } lon)
                _positions[transmitterId] = (lat, lon, altitudeMeters ?? 0.0);

            var key = (transmitterId, frequencyKhz);
            if (transmitting)
            {
                if (_active.TryGetValue(key, out var keying))
                    keying.Refreshed = now;
                else
                    _active[key] = new Keying
                    {
                        TransmitterId = transmitterId, FrequencyKhz = frequencyKhz, Start = now, Refreshed = now
                    };
            }
            else if (_active.Remove(key, out var keying))
            {
                keying.End = now;
                _ended.Add(keying);
            }
        }
    }

    /// <summary>A transmitter's latest position (DCS presence), used for its current and future keyings.</summary>
    public void ReportTransmitterPosition(string transmitterId, double latitude, double longitude,
        double altitudeMeters)
    {
        lock (_lock) _positions[transmitterId] = (latitude, longitude, altitudeMeters);
    }

    /// <summary>A transmitter disconnected: its keyings end now and its position is forgotten.</summary>
    public void ReportTransmitterGone(string transmitterId)
    {
        var now = _clock();
        lock (_lock)
        {
            foreach (var key in _active.Keys.Where(k => k.Tx == transmitterId).ToList())
            {
                _active.Remove(key, out var keying);
                keying!.End = now;
                _ended.Add(keying);
            }

            // Kept until the ended keyings are consumed by the next update, then dropped there.
            _goneTransmitters.Add(transmitterId);
        }
    }

    // ------------------------------------------------------------------ catalog

    /// <summary>The catalog as of now: every jammer with its live state (advances the auto brains first).</summary>
    public List<JammerCatalogEntryDto> Snapshot()
    {
        lock (_lock)
        {
            var now = _clock();
            var elapsed = now - _start;
            ExpireStaleKeyings(now);

            var entries = _jammers.Select(j =>
            {
                var on = j.Enabled && j.Alive && (j.Schedule?.IsOn(elapsed) ?? true);
                _brains.TryGetValue(j.Id, out var brain);
                if (brain != null)
                {
                    if (on) brain.Update(j, now, Detected(j));
                    else brain.Reset(now);
                }

                var emissions = on ? ScriptedTargeting.Emissions(j, brain?.TargetsKhz) : [];
                return new JammerCatalogEntryDto
                {
                    Definition = j.Clone(),
                    Active = emissions.Count > 0,
                    Emissions = emissions,
                    AutoState = brain?.State,
                    AutoTargetsKhz = brain?.TargetsKhz.ToList()
                };
            }).ToList();

            Volatile.Write(ref _satcomSources, entries
                .Where(e => e.Active && e.Definition.Latitude != null && e.Definition.Longitude != null)
                .Select(e => new SatcomJammerSource(e.Definition, e.Definition.Latitude!.Value,
                    e.Definition.Longitude!.Value, JammerDetection.AntennaAltitudeMeters(e.Definition), e.Emissions))
                .ToList());

            // Ended keyings have now been seen by every brain.
            _ended.Clear();
            foreach (var tx in _goneTransmitters) _positions.Remove(tx);
            _goneTransmitters.Clear();
            return entries;
        }
    }

    private void ExpireStaleKeyings(DateTimeOffset now)
    {
        foreach (var (key, keying) in _active.Where(kv => now - kv.Value.Refreshed > TransmissionStaleAfter).ToList())
        {
            _active.Remove(key);
            keying.End = keying.Refreshed;
            _ended.Add(keying);
        }
    }

    /// <summary>The keyings <paramref name="jammer"/> can hear: located transmitters inside its tuning range /
    /// auto bands, not protected, above its detection sensitivity within the radio horizon, and not blocked by
    /// terrain (when the line-of-sight provider knows).</summary>
    private List<DetectedKeying> Detected(JammerDefinition jammer)
    {
        var result = new List<DetectedKeying>();
        if (jammer.Latitude is not { } jLat || jammer.Longitude is not { } jLon) return result;
        var jAlt = JammerDetection.AntennaAltitudeMeters(jammer);

        foreach (var keying in _active.Values.Concat(_ended))
        {
            if (!_positions.TryGetValue(keying.TransmitterId, out var p)) continue;
            if (!JammerDetection.CanTarget(jammer, keying.FrequencyKhz)) continue;
            var los = LineOfSight?.Invoke(jammer.Id, keying.TransmitterId, jLat, jLon, jAlt, p.Lat, p.Lon, p.Alt);
            if (!JammerDetection.IsDetected(jammer, keying.FrequencyKhz, p.Lat, p.Lon, p.Alt, lineOfSight: los))
                continue;
            result.Add(new DetectedKeying(keying.FrequencyKhz, keying.Start, keying.End));
        }

        return result;
    }

    /// <summary>
    /// The snapshot to broadcast now, or null when nothing changed since the last one and
    /// <paramref name="periodicDue"/> is false. Marks the returned snapshot as published.
    /// </summary>
    public List<JammerCatalogEntryDto>? NextBroadcast(bool periodicDue)
    {
        var snapshot = Snapshot();
        var signature = Signature(snapshot);
        lock (_lock)
        {
            if (!periodicDue && signature == _lastPublishedSignature) return null;
            _lastPublishedSignature = signature;
        }

        return snapshot;
    }

    private static string Signature(List<JammerCatalogEntryDto> snapshot)
    {
        var sb = new StringBuilder();
        foreach (var entry in snapshot)
        {
            var d = entry.Definition;
            sb.Append(d.Id).Append('|').Append(entry.Active).Append('|').Append(d.Enabled).Append('|')
                .Append(d.Alive).Append('|').Append(entry.AutoState);
            foreach (var e in entry.Emissions)
                sb.Append('|').Append(e.CenterKhz.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(e.BandwidthKhz.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(e.PowerFraction.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(';');
        }

        return sb.ToString();
    }
}
