using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Common;
using OpenFreq.Common.Jamming;
using OpenFreq.Utilities;
using OpenFreqAudio;
using OpenFreqAudio.Jamming;
using OpenFreqClient.Services.Audio;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreq.Client.Services.Jamming;

/// <summary>What the channel card shows for a slot's jamming.</summary>
public enum JammingBadge
{
    None,

    /// <summary>The jammer is audible but the wanted signal still gets through.</summary>
    Interference,

    /// <summary>The jammer is at or above what it takes to deny the link.</summary>
    Jammed
}

/// <summary>Per-slot jamming summary, sent to the UI (WeakReferenceMessenger) whenever it changes.</summary>
/// <param name="MaxJnrDb">Strongest jammer's in-passband J/N, dB; null when no jammer reaches this slot.</param>
/// <param name="HopHitFraction">For a hopping (HAVE QUICK / SINCGARS) slot, the fraction of hops (or of each dwell,
/// follower) the strongest jammer hits; 1 for single channel.</param>
public sealed record JammingStatusMessage(
    int FrequencyKhz,
    Guid SlotId,
    double? MaxJnrDb,
    int JammerCount,
    string? DominantJammerName,
    bool IsFm,
    double HopHitFraction = 1.0);

public interface IJammingService : ILifecycleService, IDisposable
{
    /// <summary>The last catalog received from the server.</summary>
    IReadOnlyList<JammerCatalogEntryDto> Catalog { get; }

    /// <summary>The latest per-slot summary, or null when the slot isn't tuned / never computed.</summary>
    JammingStatusMessage? GetSlotStatus(int frequencyKhz, Guid slotId);
}

/// <summary>
/// Turns the server's jammer catalog into per-slot <see cref="JammerContribution"/>s for playback (docs/jamming).
/// At ~10 Hz, for every tuned slot that is not in SATCOM mode, and for every active jammer emission:
/// <code>
/// J   = EIRP(bearing, pattern, PA share) − L_path        L_path from the same propagation as signals
/// JNR = J + 10·log10(in-band fraction) − rxSensitivity   the same reference as AudioParams.ReceivedSnrDb
/// </code>
/// Path loss per (jammer, slot) is cached and refreshed at the DCS LOS cadence; the rest is cheap.
/// </summary>
public sealed class JammingService : IJammingService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan PathRefreshInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Below this JNR a jammer is not handed to playback at all (inaudible, saves DSP).</summary>
    public const double MinContributionJnrDb = -30.0;

    /// <summary>Badge: a jammer this far below the noise floor is not worth showing.</summary>
    public const double AudibleJnrDb = -3.0;

    /// <summary>Badge without a signal to compare against: this strong denies all but nearby talkers.</summary>
    public const double StrongJnrDb = 20.0;

    private const double ProbeEirpDbm = 30.0; // 1 W probe through the signal calculators

    private readonly IOpenFreqService _openFreq;
    private readonly IDcsExportService _dcs;
    private readonly ILogger<JammingService> _logger;
    private readonly Func<DateTime> _clock;

    private volatile IReadOnlyList<JammerCatalogEntryDto> _catalog = [];
    private readonly ConcurrentDictionary<(string JammerId, int FrequencyKhz, Guid SlotId), (DateTime At, PathResult? Path)>
        _paths = new();
    private readonly ConcurrentDictionary<(int FrequencyKhz, Guid SlotId), JammingStatusMessage> _status = new();
    private readonly HashSet<(int FrequencyKhz, Guid SlotId)> _slotsWithInterference = [];
    private readonly Lock _tickLock = new();
    private CancellationTokenSource? _cts;

    /// <summary>Loss and geometry from one jammer to one receiver slot.</summary>
    private sealed record PathResult(double LossDb, double BearingFromJammerDeg, double DistanceMeters);

    public JammingService(IOpenFreqService openFreq, IDcsExportService dcs, ILogger<JammingService> logger)
        : this(openFreq, dcs, logger, () => DateTime.UtcNow)
    {
    }

    internal JammingService(IOpenFreqService openFreq, IDcsExportService dcs, ILogger<JammingService> logger,
        Func<DateTime> clock)
    {
        _openFreq = openFreq;
        _dcs = dcs;
        _logger = logger;
        _clock = clock;
        _openFreq.JammerCatalogReceived += OnJammerCatalogReceived;
    }

    public IReadOnlyList<JammerCatalogEntryDto> Catalog => _catalog;

    public JammingStatusMessage? GetSlotStatus(int frequencyKhz, Guid slotId) =>
        _status.TryGetValue((frequencyKhz, slotId), out var status) ? status : null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        ApplyCatalog([]);
        Tick();
    }

    public void Dispose()
    {
        Stop();
        _openFreq.JammerCatalogReceived -= OnJammerCatalogReceived;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Jamming: tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void OnJammerCatalogReceived(object? sender, JammerCatalogEventArgs e) => ApplyCatalog(e.Message.Jammers);

    /// <summary>Replaces the catalog (positions may have changed, so cached paths go too).</summary>
    internal void ApplyCatalog(IReadOnlyList<JammerCatalogEntryDto> catalog)
    {
        _catalog = catalog;
        _paths.Clear();
    }

    /// <summary>One physics step: recompute and push every slot's contributions.</summary>
    internal void Tick()
    {
        lock (_tickLock)
        {
            var now = _clock();
            var catalog = _catalog;
            var slots = _openFreq.GetJammingReceiverSlots();
            var seen = new HashSet<(int, Guid)>();

            foreach (var slot in slots)
            {
                var key = (slot.FrequencyKhz, slot.SlotId);
                seen.Add(key);

                var contributions = slot.IsSatcomActive || !slot.IsEnabled
                    ? []
                    : ComputeContributions(slot, catalog, now);

                if (contributions.Count > 0 || _slotsWithInterference.Contains(key))
                    _openFreq.SetSlotInterference(slot.FrequencyKhz, slot.SlotId, contributions);
                if (contributions.Count > 0) _slotsWithInterference.Add(key);
                else _slotsWithInterference.Remove(key);

                PublishStatus(slot, contributions, catalog);
            }

            // Slots that went away: drop their state and clear the badge.
            foreach (var key in _status.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _status.TryRemove(key, out _);
                _slotsWithInterference.Remove(key);
                WeakReferenceMessenger.Default.Send(new JammingStatusMessage(key.FrequencyKhz, key.SlotId, null, 0,
                    null, false));
            }

            foreach (var key in _paths.Keys.Where(k => !seen.Contains((k.FrequencyKhz, k.SlotId))).ToList())
                _paths.TryRemove(key, out _);
        }
    }

    private List<JammerContribution> ComputeContributions(JammingReceiverSlot slot,
        IReadOnlyList<JammerCatalogEntryDto> catalog, DateTime now)
    {
        var result = new List<JammerContribution>();
        if (catalog.Count == 0) return result;

        var channelClass = JammingMath.ClassifyReceiver(slot.FrequencyKhz);
        var rxBwKhz = JammingMath.ReceiverBandwidthKhz(channelClass);

        var hopping = slot.Eccm is { State: { } hopState, Hopset: { Count: > 0 } hs } ? (hopState, hs) : default;

        foreach (var entry in catalog)
        {
            if (!entry.Active || entry.Emissions.Count == 0) continue;
            var jammer = entry.Definition;

            if (hopping.hs != null)
            {
                if (HoppingContribution(slot, jammer, entry.Emissions, hopping.hopState, hopping.hs, rxBwKhz, now) is
                    { } hc)
                    result.Add(hc);
                continue;
            }

            // Only bother with path loss when some emission actually lands in the passband.
            var inBand = entry.Emissions
                .Select(e => (Emission: e,
                    Fraction: JammingMath.InBandFraction(e.CenterKhz, e.BandwidthKhz, slot.FrequencyKhz, rxBwKhz)))
                .Where(x => x.Fraction > 0)
                .ToList();
            if (inBand.Count == 0) continue;

            var path = GetPath(jammer, slot, now);
            if (path == null) continue;

            // Power-sum the emissions of this jammer that land here; the strongest sets the carrier offset.
            double linear = 0, bestJnr = double.NegativeInfinity;
            JammerEmission? best = null;
            foreach (var (emission, fraction) in inBand)
            {
                var eirp = JammingMath.EirpDbm(jammer, path.BearingFromJammerDeg, emission.PowerFraction);
                var jnr = eirp - path.LossDb + 10.0 * Math.Log10(fraction) - slot.ReceiverSensitivityDbm;
                linear += Math.Pow(10.0, jnr / 10.0);
                if (jnr > bestJnr)
                {
                    bestJnr = jnr;
                    best = emission;
                }
            }

            var totalJnr = 10.0 * Math.Log10(linear);
            if (best == null || !double.IsFinite(totalJnr) || totalJnr < MinContributionJnrDb) continue;

            var offsetHz = JammingMath.InBandCenterOffsetKhz(best.CenterKhz, best.BandwidthKhz, slot.FrequencyKhz,
                rxBwKhz) * 1000.0;
            // A narrow (spot) emission carries its transmitter's frequency error; a barrage slice has no carrier.
            if (best.BandwidthKhz <= 2 * rxBwKhz)
                offsetHz += JammingMath.CarrierToleranceOffsetHz(jammer.Id, jammer.CarrierToleranceHz ?? 0);

            result.Add(new JammerContribution(
                jammer.Id,
                (float)totalJnr,
                jammer.Technique ?? JammerTechnique.Noise,
                (float)offsetHz,
                WaveformParams(jammer),
                jammer.Responsive ?? false,
                jammer.ReactionMs ?? 0f));
        }

        return result;
    }

    /// <summary>
    /// A jammer against a hopping (HAVE QUICK / SINCGARS) slot (research-02 §6.2–6.3, docs/jamming/HAVE_QUICK.md).
    /// Static techniques (spot, barrage, partial-band, swept slices): μ = |hopset ∩ jammed spectrum| / N from the
    /// slot's actual hopset (FMT list, lockouts), and the per-hop J/N is the jammer's in-channel power on a hopset
    /// channel it covers. A follower (responsive jammer whose band reaches the hopset) puts its whole emission on
    /// each hop it detects, T_delay late: f_hit = max(0, 1 − T_delay·R_h), T_delay = reaction + excess path / c.
    /// The transmitter's position isn't known here, so the excess path assumes it is next to the receiver
    /// (d_TJ ≈ d_JR, d_TR ≈ 0: 2·d_JR / c), which is the jammer's worst case.
    /// </summary>
    private JammerContribution? HoppingContribution(JammingReceiverSlot slot, JammerDefinition jammer,
        IReadOnlyList<JammerEmission> emissions, OpenFreqAudio.Eccm.HopSlotState hop, OpenFreq.Common.Eccm.Hopset hopset,
        double rxBwKhz, DateTime now)
    {
        // Emissions that land on at least one hopset channel.
        var hitting = emissions.Where(e => hopset.ChannelsHitBy(e.CenterKhz, e.BandwidthKhz) > 0).ToList();
        if (hitting.Count == 0) return null;

        var path = GetPath(jammer, slot, now);
        if (path == null) return null;

        bool follower = jammer.Responsive ?? false;
        double bestJnr = double.NegativeInfinity;
        JammerEmission? best = null;
        foreach (var e in hitting)
        {
            var eirp = JammingMath.EirpDbm(jammer, path.BearingFromJammerDeg, e.PowerFraction);
            // In-channel share: a follower re-tunes its whole emission onto the hop; a static emission has
            // rxBw / B_J of its power in any one channel it covers (all of a narrow spot).
            var fraction = follower ? 1.0 : Math.Min(1.0, rxBwKhz / Math.Max(1e-6, e.BandwidthKhz));
            var jnr = eirp - path.LossDb + 10.0 * Math.Log10(fraction) - slot.ReceiverSensitivityDbm;
            if (jnr > bestJnr)
            {
                bestJnr = jnr;
                best = e;
            }
        }
        if (best == null || !double.IsFinite(bestJnr) || bestJnr < MinContributionJnrDb) return null;

        double hitFraction;
        if (follower)
        {
            var excessPathMs = 2.0 * path.DistanceMeters / 299_792_458.0 * 1000.0;
            hitFraction = JammingMath.FollowerHitFraction((jammer.ReactionMs ?? 0f) + excessPathMs,
                hop.EffectiveHopRateHz);
        }
        else
        {
            hitFraction = hopset.HitFraction(hitting.Select(e => (e.CenterKhz, e.BandwidthKhz)));
        }
        if (hitFraction <= 0) return null;

        var offsetHz = best.BandwidthKhz <= 2 * rxBwKhz
            ? JammingMath.CarrierToleranceOffsetHz(jammer.Id, jammer.CarrierToleranceHz ?? 0)
            : 0.0;

        return new JammerContribution(
            jammer.Id,
            (float)bestJnr,
            jammer.Technique ?? JammerTechnique.Noise,
            (float)offsetHz,
            WaveformParams(jammer),
            follower,
            jammer.ReactionMs ?? 0f,
            (float)hitFraction,
            HopFollower: follower);
    }

    private static JammerWaveformParams WaveformParams(JammerDefinition j) => new()
    {
        ToneOffsetsHz = j.ToneOffsetsHz ?? [],
        StepIntervalMs = j.StepIntervalMs ?? 150f,
        SweepSpanHz = j.SweepSpanHz ?? 2000f,
        SweepRateHz = j.SweepRateHz ?? 4f,
        PulseRateHz = j.PulseRateHz ?? 50f,
        PulseDuty = j.PulseDuty ?? 0.1f,
        LookThroughPeriodMs = j.LookThroughPeriodMs ?? 0f,
        LookThroughDuty = j.LookThroughDuty ?? 0f,
        Seed = JammingMath.StableSeed(j.Id)
    };

    private void PublishStatus(JammingReceiverSlot slot, List<JammerContribution> contributions,
        IReadOnlyList<JammerCatalogEntryDto> catalog)
    {
        var isFm = JammingMath.ClassifyReceiver(slot.FrequencyKhz) == ReceiverChannelClass.Fm25;
        var strongest = contributions.MaxBy(c => c.JnrDb);
        var name = strongest == null
            ? null
            : catalog.FirstOrDefault(c => c.Definition.Id == strongest.JammerId)?.Definition.Name ?? strongest.JammerId;
        var status = new JammingStatusMessage(slot.FrequencyKhz, slot.SlotId, strongest?.JnrDb, contributions.Count,
            name, isFm, strongest?.HopHitFraction ?? 1.0);

        var key = (slot.FrequencyKhz, slot.SlotId);
        if (_status.TryGetValue(key, out var previous) && !Differs(previous, status)) return;
        _status[key] = status;
        WeakReferenceMessenger.Default.Send(status);
    }

    private static bool Differs(JammingStatusMessage a, JammingStatusMessage b) =>
        a.JammerCount != b.JammerCount || a.DominantJammerName != b.DominantJammerName ||
        (a.MaxJnrDb == null) != (b.MaxJnrDb == null) ||
        (a.MaxJnrDb is { } x && b.MaxJnrDb is { } y && Math.Abs(x - y) >= 0.5) ||
        Math.Abs(a.HopHitFraction - b.HopHitFraction) >= 0.005;

    // ------------------------------------------------------------------ badge

    /// <summary>
    /// The channel-card badge for a slot. With a wanted signal present, J/S = JNR − SNR decides: at or above the kill
    /// threshold (AM 0 dB, FM +3 dB) it is JAMMED, else INTERFERENCE. Without one, a jammer ≥ <see cref="StrongJnrDb"/>
    /// over the noise is JAMMED. Anything below <see cref="AudibleJnrDb"/> shows nothing.
    /// A hopping radio loses only <paramref name="hopHitFraction"/> of its hops (research-02 §6.4, Miller–Licklider):
    /// under 1 % it shows nothing (≈ one tick every few seconds), under 50 % at most INTERFERENCE (chopped but
    /// readable).
    /// </summary>
    public static JammingBadge ClassifyBadge(double? maxJnrDb, double? signalSnrDb, bool isFm,
        double hopHitFraction = 1.0)
    {
        if (maxJnrDb is not { } jnr || jnr < AudibleJnrDb) return JammingBadge.None;
        if (hopHitFraction < MinAudibleHopFraction) return JammingBadge.None;
        var kill = JammingMath.KillThresholdDb(isFm ? VoiceModulation.Fm : VoiceModulation.Am);
        var badge = signalSnrDb is { } snr
            ? jnr - snr >= kill ? JammingBadge.Jammed : JammingBadge.Interference
            : jnr >= StrongJnrDb ? JammingBadge.Jammed : JammingBadge.Interference;
        return badge == JammingBadge.Jammed && hopHitFraction < DeniedHopFraction ? JammingBadge.Interference : badge;
    }

    /// <summary>Hop hit fraction below which a hopping radio's badge stays blank.</summary>
    public const double MinAudibleHopFraction = 0.01;

    /// <summary>Hop hit fraction from which a hopping radio can show JAMMED (half the hops lost).</summary>
    public const double DeniedHopFraction = 0.5;

    // ------------------------------------------------------------------ propagation

    private PathResult? GetPath(JammerDefinition jammer, JammingReceiverSlot slot, DateTime now)
    {
        var key = (jammer.Id, slot.FrequencyKhz, slot.SlotId);
        if (_paths.TryGetValue(key, out var cached) && now - cached.At < PathRefreshInterval)
            return cached.Path;

        PathResult? path;
        try
        {
            path = ComputePath(jammer, slot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jamming: path from {Jammer} failed", jammer.Id);
            path = null;
        }

        _paths[key] = (now, path);
        return path;
    }

    private PathResult? ComputePath(JammerDefinition jammer, JammingReceiverSlot slot) => slot.StationType switch
    {
        RadioStationData.RadioStationType.DCS => DcsPath(jammer, slot),
        _ => HeightmapPath(jammer, slot)
    };

    /// <summary>DCS receiver: DCS-local geometry (the jammer's own, or LL→LO through DCS), the terrain calculator
    /// when a heightmap is loaded else free space, then horizon and DCS LOS exactly as for signals. Falls back to
    /// geodetic free space while no DCS-local position is available.</summary>
    private PathResult? DcsPath(JammerDefinition jammer, JammingReceiverSlot slot)
    {
        var rx = slot.DcsLocalPosition;
        var tx = JammerDcsPosition(jammer);

        if (rx != null && tx != null)
        {
            var dx = tx.X - rx.X;
            var dy = tx.Y - rx.Y;
            var dz = tx.Z - rx.Z;
            var distance = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy + dz * dz));

            double loss;
            var calculator = _openFreq.SignalCalculator;
            if (calculator != null && slot.HeightmapPosition is { } rxH)
            {
                var (hx, hy, ha) = DcsHeightmapConverter.ToHeightmap(tx, _dcs.HeightmapInfo);
                var probe = calculator.CalculateAudioParams(hx, hy, ha, rxH.X, rxH.Y, rxH.Z, slot.FrequencyKhz,
                    txPowerWatts: 1.0, receiverSensitivityDbm: 0, txAltitudeIsMSL: true, rxAltitudeIsMSL: true);
                if (!float.IsFinite(probe.ReceivedDb)) return null;
                loss = ProbeEirpDbm - probe.ReceivedDb;
            }
            else
            {
                loss = FreeSpaceLossDb(slot.FrequencyKhz, distance);
            }

            var horizon = HorizonLoss(distance, tx.Y, rx.Y);
            if (horizon == null) return null;
            loss += horizon.Value;

            var los = _dcs.RequestLineOfSight($"jam:{jammer.Id}", tx);
            if (los is { TerrainAvailable: true })
            {
                var l = Math.Clamp(los.Loss, 0.0, 1.0);
                if (l >= 0.99) return null; // blocked, same rule as signals
                loss += l * 60.0;
            }

            return new PathResult(loss, JammingMath.BearingDeg(tx.Z, tx.X, rx.Z, rx.X), distance);
        }

        return GeodeticPath(jammer, slot);
    }

    /// <summary>Lat/lon free space + horizon, for receivers with a geodetic fix but no DCS-local jammer position.</summary>
    private static PathResult? GeodeticPath(JammerDefinition jammer, JammingReceiverSlot slot)
    {
        if (slot.Geodetic is not { } rx || jammer.Latitude is not { } lat || jammer.Longitude is not { } lon)
            return null;

        var alt = jammer.AltitudeMeters ?? jammer.HeightAglMeters ?? 10.0;
        var distance = Math.Max(1.0, FreeSpacePathModel.GreatCircleDistanceMeters(lat, lon, alt, rx.LatitudeDeg,
            rx.LongitudeDeg, rx.AltitudeMeters));
        var horizon = HorizonLoss(distance, alt, rx.AltitudeMeters);
        if (horizon == null) return null;

        return new PathResult(FreeSpaceLossDb(slot.FrequencyKhz, distance) + horizon.Value,
            JammingMath.GeodeticBearingDeg(lat, lon, rx.LatitudeDeg, rx.LongitudeDeg), distance);
    }

    /// <summary>BMS / stationary / ACMI receivers: both ends in the heightmap frame. A DCS-local jammer is placed
    /// with the DCS heightmap origin when one is known, a BMS theater jammer with the BMS converter. Terrain
    /// calculator when loaded, else free space + horizon.</summary>
    private PathResult? HeightmapPath(JammerDefinition jammer, JammingReceiverSlot slot)
    {
        if (slot.HeightmapPosition is not { } rx) return null;
        var calculator = _openFreq.SignalCalculator;

        double tx, ty, talt, north, east;
        if (slot.StationType != RadioStationData.RadioStationType.BMS && jammer.DcsPosition is { } dcsPos &&
            _dcs.HeightmapInfo != null)
        {
            (tx, ty, talt) = DcsHeightmapConverter.ToHeightmap(new DcsVector3(dcsPos.X, dcsPos.Y, dcsPos.Z),
                _dcs.HeightmapInfo);
            // DCS heightmap: x = DCS x (north), y = DCS z (east)
            north = rx.X - tx;
            east = rx.Y - ty;
        }
        else if (jammer.BmsPosition is { } bms)
        {
            (tx, ty, talt) = BmsHeightmapConverter.ToHeightmap(bms.X, bms.Y, bms.Z);
            if (bms.Z <= 0)
            {
                var ground = calculator?.SampleElevation(tx, ty) ?? 0.0;
                talt = (double.IsFinite(ground) ? ground : 0.0) + (jammer.HeightAglMeters ?? 10.0);
            }

            // BMS (Falcon convention): x north, y east; the converter flips y.
            north = rx.X - tx;
            east = -(rx.Y - ty);
        }
        else
        {
            return null;
        }

        var dxy = Math.Sqrt((rx.X - tx) * (rx.X - tx) + (rx.Y - ty) * (rx.Y - ty));
        var distance = Math.Max(1.0, Math.Sqrt(dxy * dxy + (rx.Z - talt) * (rx.Z - talt)));

        double loss;
        if (calculator != null)
        {
            var probe = calculator.CalculateAudioParams(tx, ty, talt, rx.X, rx.Y, rx.Z, slot.FrequencyKhz,
                txPowerWatts: 1.0, receiverSensitivityDbm: 0, txAltitudeIsMSL: true, rxAltitudeIsMSL: true);
            if (!float.IsFinite(probe.ReceivedDb)) return null;
            loss = ProbeEirpDbm - probe.ReceivedDb;
        }
        else
        {
            loss = FreeSpaceLossDb(slot.FrequencyKhz, distance);
        }

        var horizon = HorizonLoss(distance, talt, rx.Z);
        if (horizon == null) return null;

        return new PathResult(loss + horizon.Value, JammingMath.BearingDeg(0, 0, east, north), distance);
    }

    /// <summary>The jammer's DCS-local position: configured, or converted from lat/lon by DCS (cached there).</summary>
    private DcsVector3? JammerDcsPosition(JammerDefinition jammer)
    {
        if (jammer.DcsPosition is { } p) return new DcsVector3(p.X, p.Y, p.Z);
        if (jammer.Latitude is { } lat && jammer.Longitude is { } lon)
            return _dcs.ConvertLatLonToLocal($"jam:{jammer.Id}", lat, lon, jammer.AltitudeMeters,
                jammer.HeightAglMeters ?? 10.0);
        return null;
    }

    private static double FreeSpaceLossDb(int frequencyKhz, double distanceMeters) =>
        ProbeEirpDbm - FreeSpacePathModel.CreateBaseAudioParams(frequencyKhz, 1.0, 0, distanceMeters, 0).ReceivedDb;

    /// <summary>The signals' distance/horizon roll-off (FreeSpacePathModel.ApplyHorizonLoss), in dB; null when the
    /// horizon blocks the path outright.</summary>
    private static double? HorizonLoss(double distanceMeters, double txAltitudeMeters, double rxAltitudeMeters)
    {
        var probe = new AudioParams { ReceivedDb = 0f, ReceivedSnrDb = 1000f };
        FreeSpacePathModel.ApplyHorizonLoss(probe, distanceMeters, txAltitudeMeters, rxAltitudeMeters);
        return probe.SignalBlocked ? null : -probe.ReceivedDb;
    }
}
