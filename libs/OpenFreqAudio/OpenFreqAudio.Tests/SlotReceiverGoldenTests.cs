namespace OpenFreqAudio.Tests;

/// <summary>
/// Pins <see cref="SlotReceiver"/>'s output on seeded noise, so changes to the receive chain (jamming, hop
/// gating) can prove they leave the no-jammer path alone. The numbers were recorded from the receive mix as it
/// was extracted from RadioPlayback's DSP callback, checked sample-for-sample against the original code.
/// They are block RMS values rather than hashes: Math.Cos/Log differ in the last bit between platforms' math
/// libraries, so a tolerance of 1e-4 relative is used.
/// </summary>
public class SlotReceiverGoldenTests
{
    internal const int Total = SlotHarness.Fs; // one second
    internal const int Blocks = 10;

    internal static SlotHarness Build(string scenario)
    {
        switch (scenario)
        {
            case "am-strong":
            {
                var h = new SlotHarness(251000, noiseSeed: 11);
                h.Add(30, ppm: 0.5, voice: SlotHarness.Voice);
                return h;
            }
            case "am-weak-squelch":
            {
                var h = new SlotHarness(127000, noiseSeed: 12);
                h.Add(5, ppm: -1.0, voice: SlotHarness.Voice);
                return h;
            }
            case "am-two-beat":
            {
                var h = new SlotHarness(251000, noiseSeed: 13);
                h.Add(25, voice: SlotHarness.Voice);
                h.Add(18, txKhz: 251001, voice: n => SlotHarness.Voice(n + 7777));
                return h;
            }
            case "fm-capture":
            {
                var h = new SlotHarness(40000, noiseSeed: 14);
                h.Add(20, ppm: 3, voice: SlotHarness.Voice);
                h.Add(17, ppm: -6, voice: n => SlotHarness.Voice(n + 5000));
                return h;
            }
            case "fm-weak":
            {
                var h = new SlotHarness(40000, noiseSeed: 15);
                h.Add(6, voice: SlotHarness.Voice);
                return h;
            }
            case "noise-only":
                return new SlotHarness(127000, noiseSeed: 16);
            case "noise-open-squelch":
                return new SlotHarness(127000, noiseSeed: 17, squelchLevel: 0f);
            case "short-stream":
            {
                var h = new SlotHarness(251000, noiseSeed: 18);
                var tx = h.Add(20, voice: SlotHarness.Voice);
                return h.WithShort(tx, 100);
            }
            case "ky-matched":
            {
                var h = new SlotHarness(251000, noiseSeed: 19);
                h.Slot.Enc = true;
                h.Slot.EncKey = 3;
                h.Slot.CryptoCapable = true;
                var tx = h.Add(25, voice: SlotHarness.Voice);
                tx.Carrier.Enc = true;
                tx.Carrier.EncKey = 3;
                return h;
            }
            case "hq-mismatch":
            {
                var h = new SlotHarness(251000, noiseSeed: 20);
                h.Slot.HqOn = true;
                h.Add(25, voice: SlotHarness.Voice);
                return h;
            }
            default: throw new ArgumentException(scenario);
        }
    }

    internal static readonly string[] Scenarios =
    [
        "am-strong", "am-weak-squelch", "am-two-beat", "fm-capture", "fm-weak", "noise-only",
        "noise-open-squelch", "short-stream", "ky-matched", "hq-mismatch",
    ];

    internal static double[] BlockRms(float[] output)
    {
        var r = new double[Blocks];
        int len = output.Length / Blocks;
        for (int b = 0; b < Blocks; ++b)
            r[b] = SlotHarness.Rms(output.AsSpan(b * len, len));
        return r;
    }

    // Recorded from the extracted receiver (identical to the pre-extraction DSP callback, bit for bit).
    private static readonly Dictionary<string, double[]> Golden = new()
    {
        ["am-strong"] = [0.3394160065493325, 0.09516622707456338, 0.13466868979804297, 0.16195033498414452, 0.06419078419851258, 0.18143259631664144, 0.0958938339384189, 0.13414304015746106, 0.16228941645567557, 0.06498068207760313],
        ["am-weak-squelch"] = [0.1797988356517961, 0.17941284869385846, 0.1855494282174819, 0.19334450813999482, 0.1756127515042855, 0.20303933969979632, 0.17774597080079016, 0.17981915435037138, 0.19471092115887456, 0.16985757060332177],
        ["am-two-beat"] = [0.35904575539816036, 0.2273958593336849, 0.23604586578855888, 0.24166596670279736, 0.22619952508479388, 0.24625025473738169, 0.2284079902376957, 0.23553064712941013, 0.24035325046643288, 0.22627037136616815],
        ["fm-capture"] = [0.21023975772913947, 0.0951823981492859, 0.13488414917248315, 0.16162975172067523, 0.063942716381055, 0.18146231252476802, 0.09526657431492086, 0.13462760812422817, 0.16210488438954176, 0.06341996986824228],
        ["fm-weak"] = [0.2188063791527954, 0.18536438428847266, 0.1936571090040466, 0.19803155218833324, 0.18045255292206067, 0.21372395729368923, 0.18025680609244482, 0.1980909026559879, 0.21091534071773133, 0.19475735755604343],
        ["noise-only"] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ["noise-open-squelch"] = [0.27640747342863237, 0.25246304603389097, 0.23559520752735577, 0.25325706769626966, 0.2537072689084699, 0.24078133766904267, 0.25057969925623186, 0.24628518217141723, 0.2481600584333341, 0.2594602048604105],
        ["short-stream"] = [0.20478793152100336, 0.09574637364341733, 0.12660080884419211, 0.1476965671651118, 0.07467849149613448, 0.17038861784000517, 0.09378297518404591, 0.12645146970962137, 0.148926541689771, 0.07432098751532147],
        ["ky-matched"] = [0.229365649593455, 0.09777575116982519, 0.13523369469983915, 0.1634313814012911, 0.06739689290061987, 0.18067540498154686, 0.09929625838964776, 0.13505084956300353, 0.16506910113729498, 0.06971350666364076],
        ["hq-mismatch"] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
    };

    public static IEnumerable<object[]> ScenarioData() => Scenarios.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(ScenarioData))]
    public void OutputMatchesGolden(string scenario)
    {
        var h = Build(scenario);
        // Odd block sizes, like BASS hands us, so callback boundaries move around.
        var output = new float[Total];
        int at = 0, i = 0;
        int[] sizes = [480, 441, 512, 256, 960];
        while (at < Total)
        {
            int len = Math.Min(sizes[i++ % sizes.Length], Total - at);
            h.Step(output.AsSpan(at, len));
            at += len;
        }

        var actual = BlockRms(output);
        Assert.True(Golden.ContainsKey(scenario),
            $"no golden for {scenario}: [{string.Join(", ", actual.Select(v => v.ToString("R")))}]");
        var expected = Golden[scenario];
        for (int b = 0; b < Blocks; ++b)
            Assert.True(Math.Abs(actual[b] - expected[b]) <= 1e-4 * Math.Abs(expected[b]) + 1e-7,
                $"{scenario} block {b}: expected {expected[b]:R}, got {actual[b]:R}");
    }
}

internal static class SlotHarnessExtensions
{
    public static SlotHarness WithShort(this SlotHarness h, SlotHarness.Transmitter tx, int shortBy)
    {
        h.Transmitters.Remove(tx);
        h.Transmitters.Add(new SlotHarness.Transmitter
        {
            TxKhz = tx.TxKhz, SnrDb = tx.SnrDb, TuneOffsetPpm = tx.TuneOffsetPpm, Voice = tx.Voice, ShortBy = shortBy,
        });
        return h;
    }
}
