using OpenFreqAudio.Satcom;
using SatcomAudioTool;

// Offline SATCOM audio comparison tool -- renders a WAV file (or a synthesized test tone, if no
// input is given) through the exact same SatcomVocoder pipeline used by (eventually) the live
// audio path, at each of the design brief's "Example Quality Targets" tiers. Developer tool, not
// exposed to players -- see docs/SATCOM_SIMULATION.md.
//
// Usage:
//   SatcomAudioTool <input.wav> [outputDir]
//   SatcomAudioTool --tone [outputDir]      (synthesized "radio check"-style test tone, no WAV needed)
//
// Produces (in outputDir, default "./satcom-out"):
//   RAW.wav            -- input, resampled to 48kHz mono, unprocessed
//   VOCODER_GOOD.wav   -- vocoder coloration only, zero channel errors (the "perfect link" sound)
//   SATCOM_80.wav .. SATCOM_10.wav, SATCOM_0.wav -- at each quality tier below

const int NetworkSampleRate = 48000;
const double FrameDurationSeconds = 0.0225; // MELP-class 2400bps frame duration, see SatcomProfile.Digital2400

var outputDir = "satcom-out";
short[] rawSamples48K;

if (args.Length > 0 && args[0] == "--tone")
{
    outputDir = args.Length > 1 ? args[1] : outputDir;
    rawSamples48K = GenerateTestTone(NetworkSampleRate);
    Console.WriteLine("Using synthesized test tone (no input WAV given).");
}
else if (args.Length > 0)
{
    var inputPath = args[0];
    outputDir = args.Length > 1 ? args[1] : outputDir;
    Console.WriteLine($"Reading {inputPath} ...");
    var wav = WavFile.Read(inputPath);
    var mono = WavFile.ToMono(wav);
    rawSamples48K = ResampleLinear(mono, wav.SampleRate, NetworkSampleRate);
    Console.WriteLine($"  {wav.SampleRate} Hz, {wav.Channels} ch -> resampled to {NetworkSampleRate} Hz mono, " +
                       $"{rawSamples48K.Length} samples ({rawSamples48K.Length / (double)NetworkSampleRate:F2}s)");
}
else
{
    Console.WriteLine("Usage: SatcomAudioTool <input.wav> [outputDir]");
    Console.WriteLine("       SatcomAudioTool --tone [outputDir]");
    return 1;
}

Directory.CreateDirectory(outputDir);

WavFile.Write(Path.Combine(outputDir, "RAW.wav"), NetworkSampleRate, rawSamples48K);
Console.WriteLine("Wrote RAW.wav");

// Quality tiers per the design brief's "Example Quality Targets": these percentages are
// debugging conditions, not literal engineering C/N values -- each maps to an illustrative
// (frameErrorRate, burstSeverity) pair chosen to produce the described perceptual behavior, not
// derived from SatcomLinkBudget for a specific real geometry (a developer wanting THAT should
// use SatcomLinkCalculator directly, e.g. via the debug telemetry fields, not this tool).
var tiers = new (string Label, string FileName, double Fer, double Burst)[]
{
    ("100% (VOCODER_GOOD -- vocoder coloration only, no channel errors)", "VOCODER_GOOD.wav", 0.0, 0.0),
    ("80%", "SATCOM_80.wav", 0.003, 0.05),
    ("60%", "SATCOM_60.wav", 0.02, 0.15),
    ("40%", "SATCOM_40.wav", 0.10, 0.35),
    ("20%", "SATCOM_20.wav", 0.30, 0.55),
    ("10%", "SATCOM_10.wav", 0.55, 0.75),
    ("0% (SATCOM_0 -- no decoded audio)", "SATCOM_0.wav", 1.0, 1.0),
};

foreach (var (label, fileName, fer, burst) in tiers)
{
    var vocoder = new SatcomVocoder(NetworkSampleRate, FrameDurationSeconds, seed: 12345);
    var processed = vocoder.ProcessBuffer(rawSamples48K, fer, burst);
    WavFile.Write(Path.Combine(outputDir, fileName), NetworkSampleRate, processed);
    Console.WriteLine($"Wrote {fileName,-18} [{label}] frameErrorRate={fer:F3} burstSeverity={burst:F2} " +
                       $"syncLost={vocoder.DecoderState.SyncLost}");
}

Console.WriteLine();
Console.WriteLine($"Done. Compare the files in '{outputDir}' -- RAW.wav is the unprocessed input, " +
                   "VOCODER_GOOD.wav is what a perfect SATCOM link should sound like (compressed/synthetic but " +
                   "clearly intelligible), and SATCOM_80..SATCOM_0 show progressively worse channel conditions.");
return 0;

static short[] GenerateTestTone(int sampleRate)
{
    // A short spoken-cadence-ish amplitude envelope over a couple of tones, standing in for
    // "Hawg One, Darkstar, radio check" when no real voice sample is available -- not actual
    // speech, but exercises voiced/unvoiced-ish transitions (envelope on/off) and pitch tracking.
    var durationSeconds = 3.0;
    var n = (int)(sampleRate * durationSeconds);
    var samples = new short[n];
    var rng = new Random(1);
    for (var i = 0; i < n; i++)
    {
        var t = i / (double)sampleRate;
        // Amplitude envelope: a few word-like bursts separated by silence.
        var wordPhase = t % 0.6;
        var envelope = wordPhase < 0.4 ? Math.Sin(Math.PI * wordPhase / 0.4) : 0.0;
        var pitchHz = 140 + 20 * Math.Sin(2 * Math.PI * 0.5 * t); // gentle pitch inflection
        var voiced = Math.Sin(2 * Math.PI * pitchHz * t);
        var noise = (rng.NextDouble() * 2 - 1) * 0.15; // light "cockpit noise" texture
        samples[i] = (short)Math.Clamp((voiced * 0.8 + noise) * envelope * 9000, short.MinValue, short.MaxValue);
    }
    return samples;
}

static short[] ResampleLinear(short[] input, int inputRate, int outputRate)
{
    if (inputRate == outputRate) return input;
    var outLength = (int)((long)input.Length * outputRate / inputRate);
    var output = new short[outLength];
    for (var i = 0; i < outLength; i++)
    {
        var srcPos = i * (double)inputRate / outputRate;
        var i0 = (int)srcPos;
        var frac = srcPos - i0;
        var s0 = input[Math.Min(i0, input.Length - 1)];
        var s1 = input[Math.Min(i0 + 1, input.Length - 1)];
        output[i] = (short)(s0 + (s1 - s0) * frac);
    }
    return output;
}
