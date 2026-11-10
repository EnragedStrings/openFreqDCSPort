using System.Text.Json.Serialization;
using OpenFreqAudio;

namespace OpenFreq.Common.Jamming;

/// <summary>Defaults of the auto-targeting brain. All are estimates (research-02 §6.6, research-03 §6).</summary>
public static class AutoTargetingDefaults
{
    /// <summary>Intercept receiver sensitivity, dBm.</summary>
    public const double DetectionSensitivityDbm = -110.0;

    /// <summary>Half-life of the activity score, s.</summary>
    public const double ActivityHalfLifeSeconds = 60.0;

    /// <summary>Silence after which a target is dropped (and a candidate forgotten), s.</summary>
    public const double DropAfterSeconds = 30.0;

    /// <summary>Search / DF / set-on time between first hearing a frequency and jamming it, s.</summary>
    public const double ReacquireDelaySeconds = 10.0;

    /// <summary>Score credit per key-down on top of the keyed seconds, so short calls count too.</summary>
    public const double KeyBonusSeconds = 1.0;

    /// <summary>A busier candidate takes a spot from an active target only when its score is this many times
    /// the target's (hysteresis against flapping).</summary>
    public const double PreemptRatio = 1.25;

    /// <summary>Transmitter power assumed when a transmission does not say, W: 100 W HF, 10 W VHF/UHF
    /// (typical airborne ARC-164/186/210 and manpack sets).</summary>
    public static double TransmitterPowerW(int frequencyKhz) => frequencyKhz < 30_000 ? 100.0 : 10.0;
}

/// <summary>What an auto jammer is doing, for the UI.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AutoJammerState>))]
public enum AutoJammerState
{
    /// <summary>Listening; no auto target held.</summary>
    Searching,

    /// <summary>Holding at least one auto target.</summary>
    Jamming
}

/// <summary>One detected key-down on a frequency: from <see cref="Start"/> until <see cref="End"/> (null = still
/// keyed). Only transmissions the jammer's intercept receiver can hear are passed in (<see cref="JammerDetection"/>).</summary>
public readonly record struct DetectedKeying(int FrequencyKhz, DateTimeOffset Start, DateTimeOffset? End = null);

/// <summary>The intercept model: can a jammer's receiver hear a transmitter (research-01 §1 link budget)?</summary>
public static class JammerDetection
{
    /// <summary>Received power at the jammer's intercept receiver (0 dBi transmit and receive antennas), dBm, or
    /// null when the path is beyond the 4/3-earth radio horizon of the two antenna heights.</summary>
    public static double? ReceivedDbm(double txPowerW, int frequencyKhz, double distanceKm, double txHeightM,
        double rxHeightM)
    {
        if (distanceKm > JammingMath.RadioHorizonKm(txHeightM, rxHeightM)) return null;
        return JammingMath.WattsToDbm(txPowerW) -
               JammingMath.FreeSpacePathLossDb(Math.Max(distanceKm, 0.001), frequencyKhz / 1000.0);
    }

    /// <summary>The jammer's antenna altitude MSL, m (configured altitude, else mast height over sea level).</summary>
    public static double AntennaAltitudeMeters(JammerDefinition resolved) =>
        resolved.AltitudeMeters ?? resolved.HeightAglMeters ?? 0.0;

    /// <summary>
    /// Whether a resolved jammer at a geodetic position hears a transmitter at (lat, lon, alt MSL): the frequency
    /// is one it may pick (<see cref="CanTarget"/>), the transmitter is inside the radio horizon, the received power
    /// is at or above the detection sensitivity, and <paramref name="lineOfSight"/> is not false (null = unknown:
    /// horizon only). A jammer without a latitude/longitude cannot locate anything and detects nothing.
    /// </summary>
    public static bool IsDetected(JammerDefinition resolved, int frequencyKhz, double txLatitude,
        double txLongitude, double txAltitudeMeters, double? txPowerW = null, bool? lineOfSight = null)
    {
        if (resolved.Latitude is not { } lat || resolved.Longitude is not { } lon) return false;
        if (!CanTarget(resolved, frequencyKhz)) return false;
        if (lineOfSight == false) return false;

        var rxAlt = AntennaAltitudeMeters(resolved);
        var distanceKm = FreeSpacePathModel.GreatCircleDistanceMeters(lat, lon, rxAlt, txLatitude, txLongitude,
            txAltitudeMeters) / 1000.0;
        var received = ReceivedDbm(txPowerW ?? AutoTargetingDefaults.TransmitterPowerW(frequencyKhz), frequencyKhz,
            distanceKm, txAltitudeMeters, rxAlt);
        var sensitivity = resolved.Targeting.DetectionSensitivityDbm ?? AutoTargetingDefaults.DetectionSensitivityDbm;
        return received is { } dbm && dbm >= sensitivity;
    }

    /// <summary>Whether the auto brain may jam <paramref name="frequencyKhz"/>: inside the tuning range and the
    /// auto bands (if any), and not protected (the protected list, plus guard 121.5/243.0 MHz unless
    /// <see cref="JammerTargeting.ProtectGuard"/> is off).</summary>
    public static bool CanTarget(JammerDefinition resolved, int frequencyKhz)
    {
        var t = resolved.Targeting;
        if (frequencyKhz < (resolved.MinFrequencyKhz ?? 0) || frequencyKhz > (resolved.MaxFrequencyKhz ?? int.MaxValue))
            return false;
        if (t.AutoBands is { Count: > 0 } bands &&
            !bands.Any(b => frequencyKhz >= Math.Min(b.StartKhz, b.EndKhz) && frequencyKhz <= Math.Max(b.StartKhz, b.EndKhz)))
            return false;
        return !IsProtected(t, frequencyKhz);
    }

    /// <summary>On the protected list (or guard, when protected), within half a spot bandwidth.</summary>
    public static bool IsProtected(JammerTargeting targeting, int frequencyKhz)
    {
        var half = (targeting.SpotBandwidthKhz > 0 ? targeting.SpotBandwidthKhz : 25.0) / 2.0;
        if ((targeting.ProtectedFrequenciesKhz ?? []).Any(p => Math.Abs(p - frequencyKhz) < half)) return true;
        return targeting.ProtectGuard && JammingMath.GuardFrequenciesKhz.Any(g => Math.Abs(g - frequencyKhz) < half);
    }
}

/// <summary>
/// The auto-targeting brain of one jammer (docs/jamming/JAMMING_DESIGN.md, research-03 §6 state machine:
/// search → detect → jam with look-through → drop / follow). Pure and deterministic: driven only by
/// <see cref="Update"/> with the time and the detected keyings, so tests replay it with a fake clock.
/// <list type="bullet">
/// <item>Activity score per frequency = keyed seconds + <see cref="AutoTargetingDefaults.KeyBonusSeconds"/> per
/// key-down, decayed with the configured half-life.</item>
/// <item>A frequency becomes a candidate <c>reacquireDelaySeconds</c> after it was first heard (search, DF,
/// set-on), as long as it was heard within <c>dropAfterSeconds</c>.</item>
/// <item>Free spots go to the highest-scoring candidates. A full jammer swaps a target for a candidate heard within
/// the reacquire delay when the target has been silent for the reacquire delay and the candidate was heard more
/// recently (the net moved), or when the candidate scores <see cref="AutoTargetingDefaults.PreemptRatio"/>× the
/// weakest target. A swapped-out frequency stays known while heard within <c>dropAfterSeconds</c> and can be
/// re-jammed without a new reacquire delay.</item>
/// <item>While jammed, a frequency is only heard in the jammer's look-through windows (every
/// <c>lookThroughPeriodMs</c> for <c>lookThroughDuty</c> of it; period 0 = a separate monitoring receiver that
/// hears continuously). Silent for <c>dropAfterSeconds</c> → dropped; it must then be reacquired.</item>
/// <item>Protected and guard frequencies, and frequencies outside the tuning range / auto bands, are never
/// picked.</item>
/// </list>
/// </summary>
public sealed class AutoTargetingBrain
{
    private sealed class Track
    {
        public double Score;
        public DateTimeOffset? FirstHeard;
        public DateTimeOffset LastHeard = DateTimeOffset.MinValue;
    }

    private const int MaxLookWindowsPerUpdate = 10_000;

    private readonly Dictionary<int, Track> _tracks = [];
    private readonly List<int> _targets = [];
    private readonly DateTimeOffset _epoch;
    private DateTimeOffset _last;

    public AutoTargetingBrain(DateTimeOffset start)
    {
        _epoch = start;
        _last = start;
    }

    /// <summary>Current auto targets, kHz, in the order they were acquired.</summary>
    public IReadOnlyList<int> TargetsKhz => _targets;

    public AutoJammerState State => _targets.Count > 0 ? AutoJammerState.Jamming : AutoJammerState.Searching;

    /// <summary>Activity score of a frequency (0 if unknown), for diagnostics and tests.</summary>
    public double ScoreOf(int frequencyKhz) => _tracks.TryGetValue(frequencyKhz, out var t) ? t.Score : 0.0;

    /// <summary>Forgets everything (jammer switched off / killed) and restarts the search at <paramref name="now"/>.</summary>
    public void Reset(DateTimeOffset now)
    {
        _tracks.Clear();
        _targets.Clear();
        _last = now;
    }

    /// <summary>Advances the brain from the previous update to <paramref name="now"/>. <paramref name="detected"/>
    /// holds every detected keying overlapping that interval (earlier parts are ignored).</summary>
    public void Update(JammerDefinition resolved, DateTimeOffset now, IReadOnlyCollection<DetectedKeying> detected)
    {
        if (now <= _last) return;
        var t = resolved.Targeting;
        var halfLife = Math.Max(1e-3, t.ActivityHalfLifeSeconds ?? AutoTargetingDefaults.ActivityHalfLifeSeconds);
        var drop = TimeSpan.FromSeconds(Math.Max(0, t.DropAfterSeconds ?? AutoTargetingDefaults.DropAfterSeconds));
        var reacquire = TimeSpan.FromSeconds(Math.Max(0,
            t.ReacquireDelaySeconds ?? AutoTargetingDefaults.ReacquireDelaySeconds));

        var decay = Math.Pow(0.5, (now - _last).TotalSeconds / halfLife);
        foreach (var track in _tracks.Values) track.Score *= decay;

        foreach (var group in detected.GroupBy(d => d.FrequencyKhz))
        {
            if (!JammerDetection.CanTarget(resolved, group.Key)) continue;
            Observe(resolved, group.Key, group.ToList(), now);
        }

        // Silence: drop targets, forget candidates (they must be reacquired).
        foreach (var (freq, track) in _tracks)
        {
            if (track.FirstHeard == null || now - track.LastHeard < drop) continue;
            track.FirstHeard = null;
            _targets.Remove(freq);
        }

        foreach (var freq in _tracks.Where(kv => kv.Value.FirstHeard == null && kv.Value.Score < 0.01)
                     .Select(kv => kv.Key).ToList())
            _tracks.Remove(freq);
        _targets.RemoveAll(f => !JammerDetection.CanTarget(resolved, f));

        Select(resolved, now, reacquire, drop);
        _last = now;
    }

    private void Observe(JammerDefinition resolved, int freq, List<DetectedKeying> keyings, DateTimeOffset now)
    {
        // Clip to (last, now] and merge overlaps (two transmitters keying at once count once).
        var clipped = keyings
            .Select(k => (Start: k.Start > _last ? k.Start : _last, End: (k.End ?? now) < now ? k.End ?? now : now,
                IsNew: k.Start > _last && k.Start <= now))
            .Where(k => k.End >= k.Start && (k.End > k.Start || k.IsNew))
            .OrderBy(k => k.Start)
            .ToList();
        if (clipped.Count == 0) return;

        double keyedSeconds = 0;
        DateTimeOffset? runStart = null, runEnd = null;
        foreach (var k in clipped)
        {
            if (runEnd is { } re && k.Start <= re)
            {
                if (k.End > re) runEnd = k.End;
                continue;
            }

            if (runStart != null) keyedSeconds += (runEnd!.Value - runStart.Value).TotalSeconds;
            runStart = k.Start;
            runEnd = k.End;
        }

        keyedSeconds += (runEnd!.Value - runStart!.Value).TotalSeconds;
        var newKeys = clipped.Count(k => k.IsNew);

        if (!_tracks.TryGetValue(freq, out var track)) _tracks[freq] = track = new Track();
        track.Score += keyedSeconds + AutoTargetingDefaults.KeyBonusSeconds * newKeys;

        var heardAt = _targets.Contains(freq) ? HeardInLookThrough(resolved, clipped, now) : clipped.Max(k => k.End);
        if (heardAt is not { } heard) return;
        if (heard > track.LastHeard) track.LastHeard = heard;
        track.FirstHeard ??= clipped[0].Start;
    }

    /// <summary>When a jammed frequency was last heard through the look-through windows in (last, now], or null.</summary>
    private DateTimeOffset? HeardInLookThrough(JammerDefinition resolved,
        List<(DateTimeOffset Start, DateTimeOffset End, bool IsNew)> clipped, DateTimeOffset now)
    {
        var periodS = (resolved.LookThroughPeriodMs ?? 0) / 1000.0;
        var duty = Math.Clamp(resolved.LookThroughDuty ?? 0, 0, 1);
        if (periodS <= 0 || (now - _last).TotalSeconds / periodS > MaxLookWindowsPerUpdate)
            return clipped.Max(k => k.End); // continuous monitoring
        if (duty <= 0) return null; // radiating all the time: deaf on its own targets

        // The jammer is silent in the last `duty` of each period (JammerWaveform's gating).
        var window = TimeSpan.FromSeconds(periodS * duty);
        var period = TimeSpan.FromSeconds(periodS);
        DateTimeOffset? heard = null;
        var k0 = (long)Math.Floor((_last - _epoch).TotalSeconds / periodS);
        var k1 = (long)Math.Floor((now - _epoch).TotalSeconds / periodS);
        for (var k = k0; k <= k1; k++)
        {
            var wEnd = _epoch + period * (k + 1);
            var wStart = wEnd - window;
            var lo = wStart > _last ? wStart : _last;
            var hi = wEnd < now ? wEnd : now;
            if (hi < lo) continue;
            foreach (var key in clipped)
            {
                if (key.Start > hi || key.End < lo) continue;
                var at = key.End < hi ? key.End : hi;
                if (heard == null || at > heard) heard = at;
            }
        }

        return heard;
    }

    private void Select(JammerDefinition resolved, DateTimeOffset now, TimeSpan reacquire, TimeSpan drop)
    {
        var capacity = ScriptedTargeting.AutoCapacity(resolved);
        while (_targets.Count > capacity)
            _targets.Remove(_targets.OrderBy(f => _tracks[f].Score).ThenBy(f => f).First());

        var candidates = _tracks
            .Where(kv => !_targets.Contains(kv.Key) && kv.Value.FirstHeard is { } first &&
                         now - first >= reacquire && now - kv.Value.LastHeard < drop)
            .OrderByDescending(kv => kv.Value.Score).ThenBy(kv => kv.Key)
            .ToList();

        foreach (var (freq, track) in candidates)
        {
            if (_targets.Count < capacity)
            {
                _targets.Add(freq);
                continue;
            }

            // Only a net that is active now takes a spot from a held target (no flapping back to an old net).
            if (now - track.LastHeard >= reacquire) continue;

            // The net moved: a target silent for the reacquire delay yields to a candidate heard after it.
            int? victim = _targets
                .Where(f => now - _tracks[f].LastHeard >= reacquire && _tracks[f].LastHeard < track.LastHeard)
                .OrderBy(f => _tracks[f].LastHeard).ThenBy(f => f)
                .Select(f => (int?)f).FirstOrDefault();

            // A much busier net takes the weakest spot.
            victim ??= _targets
                .Where(f => track.Score > _tracks[f].Score * AutoTargetingDefaults.PreemptRatio)
                .OrderBy(f => _tracks[f].Score).ThenBy(f => f)
                .Select(f => (int?)f).FirstOrDefault();

            if (victim is { } v) _targets[_targets.IndexOf(v)] = freq;
        }
    }
}
