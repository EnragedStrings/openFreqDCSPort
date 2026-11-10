using System.Text.Json.Serialization;

namespace OpenFreqAudio.Eccm;

/// <summary>Frequency-hopping (TRANSEC) mode of a radio. See docs/jamming/HAVE_QUICK.md.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HopMode>))]
public enum HopMode
{
    /// <summary>Single channel (no hopping).</summary>
    None = 0,

    /// <summary>HAVE QUICK I/II, 225–400 MHz UHF AM.</summary>
    HaveQuick = 1,

    /// <summary>SINCGARS, 30–88 MHz VHF FM.</summary>
    Sincgars = 2
}

/// <summary>
/// A hopping radio's net identity as it travels with every transmission (<c>FrequencyTransmission.Eccm</c>).
/// Never carries key material: <see cref="KeyFingerprint"/> is a one-way hash of the WOD (HAVE QUICK, for the
/// operating day) or the TRANSEC key and lockouts (SINCGARS).
/// </summary>
public sealed class HopNetIdentity
{
    [JsonPropertyName("mode")]
    public HopMode Mode { get; set; }

    /// <summary>Net number, normalized ("A12.300" for HAVE QUICK, "F001"-style or digits for SINCGARS).</summary>
    [JsonPropertyName("net")]
    public string Net { get; set; } = "";

    /// <summary>One-way fingerprint of the fill (WOD for the day / TRANSEC key + lockouts), 16 hex digits.
    /// Empty = no fill loaded: the radio can't hop with anybody.</summary>
    [JsonPropertyName("key")]
    public string KeyFingerprint { get; set; } = "";

    /// <summary>The radio has a time of day (GPS, TOD receive, cockpit). False = "NO TIME": it can't hop.</summary>
    [JsonPropertyName("todOk")]
    public bool TodValid { get; set; }

    /// <summary>The radio's TOD clock minus net time (UTC), milliseconds. 0 for a GPS/TOD-synced radio.</summary>
    [JsonPropertyName("tod")]
    public long TodOffsetMs { get; set; }

    public HopNetIdentity()
    {
    }

    public HopNetIdentity(HopMode mode, string net, string keyFingerprint, bool todValid, long todOffsetMs = 0)
    {
        Mode = mode;
        Net = net;
        KeyFingerprint = keyFingerprint;
        TodValid = todValid;
        TodOffsetMs = todOffsetMs;
    }

    /// <summary>Hopping at all (and so taking part in the TRANSEC match).</summary>
    [JsonIgnore]
    public bool IsHopping => Mode != HopMode.None;

    /// <summary>Has what it takes to hop: a fill and a time.</summary>
    [JsonIgnore]
    public bool IsReady => IsHopping && TodValid && !string.IsNullOrEmpty(KeyFingerprint) && !string.IsNullOrEmpty(Net);

    /// <summary>The hop-sequence seed: everybody on the same net with the same fill hops the same sequence.</summary>
    [JsonIgnore]
    public ulong Seed => HopSequence.Hash($"{Mode}|{Net}|{KeyFingerprint}");

    public override string ToString() =>
        $"{Mode} {Net} key={(KeyFingerprint.Length > 4 ? KeyFingerprint[..4] + "…" : KeyFingerprint)} tod={(TodValid ? TodOffsetMs + "ms" : "none")}";
}

/// <summary>
/// A receiving slot's hopping configuration: its identity plus the receiver-side parameters that never go on the
/// wire (hop rate, sync window).
/// </summary>
/// <param name="Identity">The slot's net identity (also stamped on what this slot transmits).</param>
/// <param name="HopRateHz">Hops per second (HAVE QUICK default 100, an estimate; SINCGARS ≈100).</param>
/// <param name="SyncWindowMs">How far apart two TOD clocks may be and still hop together, ms.</param>
public sealed record HopSlotState(HopNetIdentity Identity, double HopRateHz, double SyncWindowMs)
{
    /// <summary>Default HAVE QUICK hop rate, hops/s (research-02 §1.2, [EST]).</summary>
    public const double DefaultHopRateHz = 100.0;

    /// <summary>Default HAVE QUICK TOD sync window, ms ([EST]: a few dwells; TOD transfer is near exact).</summary>
    public const double DefaultHaveQuickSyncWindowMs = 50.0;

    /// <summary>Default SINCGARS sync window, ms (±4 s commonly cited, research-02 §2).</summary>
    public const double DefaultSincgarsSyncWindowMs = 4000.0;

    /// <summary>Hop rate clamped to something the DSP can run (1..1000 hops/s).</summary>
    public double EffectiveHopRateHz =>
        double.IsFinite(HopRateHz) && HopRateHz > 0 ? Math.Clamp(HopRateHz, 1.0, 1000.0) : DefaultHopRateHz;
}

/// <summary>
/// The TRANSEC gate: which transmitters a receiving slot can hear at all, before COMSEC.
/// <list type="bullet">
/// <item>Hopping receiver and hopping transmitter: heard only when mode, net and fill fingerprint match, both have
/// a valid TOD and the two TOD clocks are within the receiver's sync window. Anything else hops elsewhere and is
/// not heard.</item>
/// <item>Hopping vs single-channel (either way): not heard. (A hopper lands on a single-channel radio's frequency
/// on ~1/N of hops, a ~10 ms blip every ~20 s on a combat hopset; not modelled.)</item>
/// <item>Neither hopping: the legacy HAVE QUICK flag must match (an "HQ on" radio without a net identity, from an
/// older client or SRS, only hears other such radios), which reduces to plain single-channel when both are off.</item>
/// </list>
/// </summary>
public static class TransecGate
{
    public static bool CanHear(HopSlotState? receiver, bool receiverHqOn, HopNetIdentity? transmitter,
        bool transmitterHqOn)
    {
        var rx = receiver?.Identity;
        bool rxHopping = rx is { IsHopping: true };
        bool txHopping = transmitter is { IsHopping: true };
        if (rxHopping && txHopping) return Matches(receiver!, transmitter!);
        if (rxHopping || txHopping) return false;
        return receiverHqOn == transmitterHqOn;
    }

    /// <summary>Two hopping radios hop together.</summary>
    public static bool Matches(HopSlotState receiver, HopNetIdentity transmitter)
    {
        var rx = receiver.Identity;
        if (!rx.IsReady || !transmitter.IsReady) return false;
        if (rx.Mode != transmitter.Mode) return false;
        if (!string.Equals(rx.Net, transmitter.Net, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(rx.KeyFingerprint, transmitter.KeyFingerprint, StringComparison.OrdinalIgnoreCase))
            return false;
        var window = double.IsFinite(receiver.SyncWindowMs) ? Math.Max(0.0, receiver.SyncWindowMs) : 0.0;
        return Math.Abs((double)rx.TodOffsetMs - transmitter.TodOffsetMs) <= window;
    }
}

/// <summary>
/// The deterministic hop sequence: stateless hashes of (net seed, hop index), so every radio on a net — and every
/// listener's jammer model — derives the same channel and the same jammed/clean decision for the same hop.
/// </summary>
public static class HopSequence
{
    /// <summary>Stable 64-bit FNV-1a of a string (string.GetHashCode is randomized per process).</summary>
    public static ulong Hash(string text)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (char ch in text)
        {
            h ^= ch;
            h *= 0x100000001B3UL;
        }
        return h;
    }

    /// <summary>SplitMix64 of (seed, hop, stream): a well-mixed 64-bit value per hop.</summary>
    public static ulong Mix(ulong seed, long hopIndex, ulong stream = 0)
    {
        ulong z = seed ^ ((ulong)hopIndex * 0x9E3779B97F4A7C15UL) ^ (stream * 0xD1B54A32D192ED03UL);
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform on [0, 1) for this hop and stream.</summary>
    public static double Uniform(ulong seed, long hopIndex, ulong stream = 0) =>
        (Mix(seed, hopIndex, stream) >> 11) * (1.0 / (1UL << 53));

    /// <summary>The hopset channel index (0..N-1) the net is on during hop <paramref name="hopIndex"/>.</summary>
    public static int ChannelIndex(ulong seed, long hopIndex, int channelCount) =>
        channelCount <= 1 ? 0 : (int)(Mix(seed, hopIndex) % (ulong)channelCount);

    /// <summary>The hop index at a net time (seconds since the Unix epoch, plus the radio's TOD offset).</summary>
    public static long HopIndexAt(double netTimeSeconds, double hopRateHz) =>
        (long)Math.Floor(netTimeSeconds * hopRateHz);
}
