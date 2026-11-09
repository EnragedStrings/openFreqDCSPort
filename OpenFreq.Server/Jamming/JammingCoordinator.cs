using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Jamming;

namespace OpenFreqServer.Jamming;

/// <summary>
/// Owns the server's jammer set (config now; the DCS mission hook in phase 6) and turns it into the
/// <see cref="JammerCatalogUpdateMessage"/> SignalingServer broadcasts. Phase 1 is scripted targeting only: a
/// jammer radiates on its configured spots and bands (<see cref="ScriptedTargeting"/>) while it is enabled, alive
/// and inside its schedule. The clock is injectable so schedules are testable.
/// </summary>
public sealed class JammingCoordinator
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<JammerDefinition> _jammers = [];
    private DateTimeOffset _start;
    private string? _lastPublishedSignature;

    public JammingCoordinator(JammingServerConfig config, ILogger logger, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
        _start = _clock();
        Load(config.Jammers);
    }

    /// <summary>Periodic re-broadcast interval.</summary>
    public TimeSpan BroadcastInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Resolved jammers currently known.</summary>
    public IReadOnlyList<JammerDefinition> Jammers
    {
        get
        {
            lock (_lock) return _jammers.ToList();
        }
    }

    /// <summary>Replaces the jammer set with <paramref name="definitions"/> (resolved against their presets) and
    /// restarts schedules.</summary>
    public void Load(IEnumerable<JammerDefinition> definitions)
    {
        int count;
        lock (_lock)
        {
            _jammers.Clear();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
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

                _jammers.Add(resolved);
            }

            _start = _clock();
            _lastPublishedSignature = null;
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

    /// <summary>The catalog as of now: every jammer with its live state.</summary>
    public List<JammerCatalogEntryDto> Snapshot()
    {
        lock (_lock)
        {
            var elapsed = _clock() - _start;
            return _jammers.Select(j =>
            {
                var on = j.Enabled && j.Alive && (j.Schedule?.IsOn(elapsed) ?? true);
                var emissions = on ? ScriptedTargeting.Emissions(j) : [];
                return new JammerCatalogEntryDto
                {
                    Definition = j.Clone(),
                    Active = emissions.Count > 0,
                    Emissions = emissions
                };
            }).ToList();
        }
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
                .Append(d.Alive);
            foreach (var e in entry.Emissions)
                sb.Append('|').Append(e.CenterKhz.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(e.BandwidthKhz.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(e.PowerFraction.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(';');
        }

        return sb.ToString();
    }
}
