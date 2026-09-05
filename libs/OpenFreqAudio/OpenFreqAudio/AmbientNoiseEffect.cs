// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;

namespace OpenFreqAudio;

/// <summary>The acoustic environment of the transmitting station.</summary>
public enum AmbientNoiseType
{
    /// <summary>No ambient layer — clean demodulated audio only.</summary>
    None,

    /// <summary>F-16 cockpit: inverter whine, engine roar, oxygen-mask muffle.</summary>
    AirF16,

    /// <summary>F-15 cockpit: twin-engine roar, crisp analog feel, lighter mask muffle.</summary>
    AirF15,

    /// <summary>Generic jet cockpit: ECS compressor whine, engine roar, oxygen-mask muffle.</summary>
    AirGeneric,

    /// <summary>A-10C cockpit: dominant twin 400Hz inverter whine, light engine roar, oxygen-mask muffle.</summary>
    AirA10,

    /// <summary>UH-60L cockpit: main/tail rotor blade-passage thump, engine/gearbox roar, headset muffle.</summary>
    AirUH60,

    /// <summary>Ground vehicle (APC, HMMWV, tank).</summary>
    Ground,

    /// <summary>Stationary ground station or AWACS.</summary>
    Stationary,
}

/// <summary>
/// Ambient SFX applied in two passes around the RF fading stage.
/// PreFade — transmitter-side acoustics (cockpit noise, mask muffling) on clean PCM.
/// PostFade — receiver-side IF filtering after demodulation.
/// Not thread-safe; each RadioEffect owns its own instance and replaces it atomically.
/// Always operates on mono audio.
/// </summary>
internal interface IAmbientNoiseEffect
{
    /// <summary>
    /// Transmitter-side acoustics. Called on clean mono PCM before RF fading.
    /// <paramref name="volume"/> (0..1) wet/dry-blends the effect against the
    /// clean input: 0 bypasses the effect entirely, 1 applies it at full strength.
    /// </summary>
    void ApplyPreFade(float[] buffer, int offset, int frames, float volume);
}

// ---------------------------------------------------------------------------
//  Factory
// ---------------------------------------------------------------------------

internal static class AmbientNoiseEffectFactory
{
    /// <summary>
    /// Create an ambient noise effect with configurable strength.
    /// </summary>
    /// <param name="strength">Effect intensity multiplier (0.0 = no effect, 1.0 = full strength, > 1.0 = boost).</param>
    public static IAmbientNoiseEffect Create(AmbientNoiseType type, int sampleRate, float strength = 5.0f)
        => type switch
        {
            AmbientNoiseType.AirF16     => new AirF16AmbientEffect(sampleRate, strength),
            AmbientNoiseType.AirF15     => new AirF15AmbientEffect(sampleRate, strength),
            AmbientNoiseType.AirGeneric => new AirGenericAmbientEffect(sampleRate, strength),
            AmbientNoiseType.AirA10     => new AirA10AmbientEffect(sampleRate, strength),
            AmbientNoiseType.AirUH60    => new AirUH60AmbientEffect(sampleRate, strength),
            AmbientNoiseType.Ground     => new GroundAmbientEffect(sampleRate, strength),
            AmbientNoiseType.Stationary => new StationaryAmbientEffect(sampleRate, strength),
            _                           => NullAmbientEffect.Instance,
        };
}

// ---------------------------------------------------------------------------
//  None — no-op
// ---------------------------------------------------------------------------

internal sealed class NullAmbientEffect : IAmbientNoiseEffect
{
    public static readonly NullAmbientEffect Instance = new();
    private NullAmbientEffect() { }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume) { }
}

// ---------------------------------------------------------------------------
//  Shared DSP helpers
// ---------------------------------------------------------------------------

internal static class AmbientDsp
{
    /// <summary>
    /// Transmitter ALC: clean linear makeup gain followed by a soft-knee peak
    /// limiter, no "crunch".
    /// Output is bounded to ±1.
    /// </summary>
    public static float SoftAlc(float x, float makeup, float knee)
    {
        x *= makeup;
        float a = MathF.Abs(x);
        if (a <= knee) return x;
        float over = (a - knee) / (1f - knee);
        return MathF.Sign(x) * (knee + (1f - knee) * MathF.Tanh(over));
    }
}

// ---------------------------------------------------------------------------
//  AirF16 — F-16 cockpit
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  airframe AM: two beating oscillators (55 Hz + 73 Hz)
///   → [PreFade]  inverter whine: 400 Hz + odd harmonics, FM wobble (0.8 Hz, ±3 Hz)
///   → [PreFade]  engine roar: noise → one-pole LPF 350 Hz (additive)
///   → [PreFade]  oxygen-mask two-pole LPF 1300 Hz + nasal cavity blend
///   → RF fading  (handled by RadioEffect)
/// </summary>
internal sealed class AirF16AmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly float _strength;

    // Oxygen-mask + comms band-limit — two cascaded one-pole stages.
    // Cutoff sits above the 1–3 kHz presence band so voice stays intelligible;
    // darker than the F-15 (2000 Hz) for the mask-muffled character.
    private const float MuffleCutoff = 1900f;
    private readonly float _muffleA;
    private float _muffleLP1;
    private float _muffleLP2;

    // Transmitter ALC — clean makeup gain + soft-knee peak limiter (see
    // AmbientDsp.SoftAlc). Loud, constant level without broadband crunch.
    private const float AlcMakeup = 2.0f;
    private const float AlcKnee   = 0.70f;

    // Inverter whine — 400 Hz near-square-wave: fundamental + 3rd + 5th harmonics.
    // FM wobble via 0.8 Hz LFO simulates power-supply frequency drift.
    private const float WhineFreq        = 400f;
    private const float WhineLevel       = 0.006f;
    private const float WhineH3Level     = 0.0025f; // 1200 Hz
    private const float WhineH5Level     = 0.001f;  // 2000 Hz
    private const float WhineWobbleRate  = 0.8f;    // Hz
    private const float WhineWobbleDepth = 3.0f;    // ±Hz drift
    private double _whinePhase;
    private double _whineWobblePhase;

    // Engine roar — LCG noise → one-pole LPF 350 Hz, additive acoustic bleed
    private const float RoarLevel = 0.020f;
    private readonly float _roarLpA;
    private float _roarLpState;
    private uint  _noiseState = 0x9E3779B9u;

    // Airframe AM — two independent oscillators beating for organic lope
    private const float RumbleFreq1  = 55f;
    private const float RumbleFreq2  = 73f;
    private const float RumbleDepth1 = 0.10f;
    private const float RumbleDepth2 = 0.10f;
    private double _rumblePhase1;
    private double _rumblePhase2;

    public AirF16AmbientEffect(int sampleRate, float strength)
    {
        _sampleRate = sampleRate;
        _strength   = strength;

        _muffleA = MathF.Exp(-2f * MathF.PI * MuffleCutoff / sampleRate);
        _roarLpA = MathF.Exp(-2f * MathF.PI * 350f / sampleRate);
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume)
    {
        double wobbleInc  = 2.0 * Math.PI * WhineWobbleRate / _sampleRate;
        double rumbleInc1 = 2.0 * Math.PI * RumbleFreq1 / _sampleRate;
        double rumbleInc2 = 2.0 * Math.PI * RumbleFreq2 / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Whine: wobble LFO shifts instantaneous phase increment for FM effect
            double wobble    = Math.Sin(_whineWobblePhase) * WhineWobbleDepth;
            double whineInc  = 2.0 * Math.PI * (WhineFreq + wobble) / _sampleRate;
            _whineWobblePhase += wobbleInc;
            if (_whineWobblePhase > Math.PI * 2) _whineWobblePhase -= Math.PI * 2;

            float whineSample = ((float)Math.Sin(_whinePhase)      * WhineLevel
                              +  (float)Math.Sin(_whinePhase * 3.0) * WhineH3Level
                              +  (float)Math.Sin(_whinePhase * 5.0) * WhineH5Level) * _strength;
            _whinePhase += whineInc;
            if (_whinePhase > Math.PI * 2) _whinePhase -= Math.PI * 2;

            // Airframe AM: two oscillators beating for organic lope
            // AM depth is NOT scaled - it's part of the platform character
            float rumbleGain = 1f
                + (float)Math.Sin(_rumblePhase1) * RumbleDepth1
                + (float)Math.Sin(_rumblePhase2) * RumbleDepth2;
            _rumblePhase1 += rumbleInc1;
            _rumblePhase2 += rumbleInc2;
            if (_rumblePhase1 > Math.PI * 2) _rumblePhase1 -= Math.PI * 2;
            if (_rumblePhase2 > Math.PI * 2) _rumblePhase2 -= Math.PI * 2;

            // Engine roar: Knuth LCG → one-pole LPF
            _noiseState  = _noiseState * 1664525u + 1013904223u;
            float roar   = _roarLpA * _roarLpState + (1f - _roarLpA) * ((int)_noiseState * (1f / 2147483648f));
            _roarLpState = roar;

            int idx = offset + frame;
            float dry = buffer[idx];
            float x = dry;

            x *= rumbleGain;                    // airframe vibration AM-modulates mic pickup
            x += whineSample;                   // electrical wiring bleed
            x += roar * RoarLevel * _strength;  // structure-borne acoustic roar

            // Transmitter ALC: clean makeup gain + peak limiter → loud, no crunch
            x = AmbientDsp.SoftAlc(x, AlcMakeup, AlcKnee);

            // Oxygen-mask two-pole LPF + nasal cavity blend
            float lp1 = _muffleA * _muffleLP1 + (1f - _muffleA) * x;
            _muffleLP1 = lp1;
            float lp2 = _muffleA * _muffleLP2 + (1f - _muffleA) * lp1;
            _muffleLP2 = lp2;

            float wet = lp2 * 0.55f + x * 0.45f;
            buffer[idx] = dry + volume * (wet - dry);
        }
    }
}

// ---------------------------------------------------------------------------
//  AirF15 — F-15 cockpit
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  airframe AM: two beating oscillators (50 Hz + 68 Hz)
///   → [PreFade]  engine roar: noise → one-pole LPF 400 Hz (twin P&W F100, additive)
///   → [PreFade]  oxygen-mask single-pole LPF 2000 Hz + partial dry blend
///   → RF fading  (handled by RadioEffect)
/// </summary>
internal sealed class AirF15AmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly float _strength;

    // Single-pole mask muffle — higher cutoff than F-16 for a crisper, more analog feel
    private const float MuffleCutoff = 2000f;
    private readonly float _muffleA;
    private float _muffleLP;

    // Airframe AM — twin-engine F100 beat signature
    private const float RumbleFreq1  = 50f;
    private const float RumbleFreq2  = 68f;
    private const float RumbleDepth1 = 0.09f;
    private const float RumbleDepth2 = 0.07f;
    private double _rumblePhase1;
    private double _rumblePhase2;

    // Engine roar — noise → one-pole LPF 400 Hz
    private const float RoarLevel = 0.025f;
    private readonly float _roarLpA;
    private float _roarLpState;
    private uint _noiseState = 0x9E3779B9u;

    // Analog preamp soft-clip — tanh saturation mimicking 80s transistor input stage.
    // Drive = 2.5 gives noticeable flat-topping on peaks without hard clipping.
    // Normalised so unity input → unity output; gain is introduced by the drive pushing
    // typical speech levels (0.3–0.7) into the nonlinear region.
    private const float SatDrive = 2.5f;
    private static readonly float SatNorm = 1.0f / MathF.Tanh(SatDrive);

    public AirF15AmbientEffect(int sampleRate, float strength)
    {
        _sampleRate = sampleRate;
        _strength   = strength;

        _muffleA = MathF.Exp(-2f * MathF.PI * MuffleCutoff / sampleRate);
        _roarLpA = MathF.Exp(-2f * MathF.PI * 400f / sampleRate);
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume)
    {
        double rumbleInc1 = 2.0 * Math.PI * RumbleFreq1 / _sampleRate;
        double rumbleInc2 = 2.0 * Math.PI * RumbleFreq2 / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Airframe AM: twin-engine beat
            float rumbleGain = 1f
                + (float)Math.Sin(_rumblePhase1) * RumbleDepth1
                + (float)Math.Sin(_rumblePhase2) * RumbleDepth2;
            _rumblePhase1 += rumbleInc1;
            _rumblePhase2 += rumbleInc2;
            if (_rumblePhase1 > Math.PI * 2) _rumblePhase1 -= Math.PI * 2;
            if (_rumblePhase2 > Math.PI * 2) _rumblePhase2 -= Math.PI * 2;

            // Engine roar
            _noiseState  = _noiseState * 1664525u + 1013904223u;
            float roar   = _roarLpA * _roarLpState + (1f - _roarLpA) * ((int)_noiseState * (1f / 2147483648f));
            _roarLpState = roar;

            int idx = offset + frame;
            float dry = buffer[idx];
            float x = dry;

            x *= rumbleGain;
            x += roar * RoarLevel * _strength;

            // Analog preamp saturation: soft-clips peaks, adds odd harmonics for 80s crunch
            x = MathF.Tanh(x * SatDrive) * SatNorm;

            // Single-pole mask muffle: softer coloring than F-16, more dry signal let through
            float lp = _muffleA * _muffleLP + (1f - _muffleA) * x;
            _muffleLP = lp;

            float wet = lp * 0.40f + x * 0.60f;
            buffer[idx] = dry + volume * (wet - dry);
        }
    }
}

// ---------------------------------------------------------------------------
//  AirGeneric — generic jet cockpit
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  airframe AM: two beating oscillators (57 Hz + 76 Hz)
///   → [PreFade]  engine whine: N1 fan series (~480 Hz) + N2 core series (~750 Hz),
///                each with independent FM wobble so they beat and interact organically
///   → [PreFade]  engine roar: noise → one-pole LPF 350 Hz (additive)
///   → [PreFade]  oxygen-mask two-pole LPF 1300 Hz + nasal cavity blend
///   → RF fading  (handled by RadioEffect)
///
/// Two independent rotating-stage series (N1 fan + N2 core compressor) with different
/// wobble rates create the characteristic multi-tone jet whine heard inside a cockpit.
/// Overall level is kept below the F-16's inverter whine.
/// </summary>
internal sealed class AirGenericAmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly float _strength;

    // Oxygen-mask + comms band-limit — two-pole LPF, same cutoff as F-16 for
    // consistent muffling character while keeping 1–3 kHz presence intelligible.
    private const float MuffleCutoff = 1900f;
    private readonly float _muffleA;
    private float _muffleLP1;
    private float _muffleLP2;

    // Transmitter ALC — clean makeup gain + soft-knee peak limiter (matches F-16).
    // Loud, constant level without broadband crunch. See AmbientDsp.SoftAlc.
    private const float AlcMakeup = 2.0f;
    private const float AlcKnee   = 0.70f;

    // N1 fan stage — lower harmonic series, slow wobble (fan RPM variation)
    private const float N1Freq        = 480f;
    private const float N1Level       = 0.0022f; //  480 Hz
    private const float N1H2Level     = 0.0014f; //  960 Hz
    private const float N1H3Level     = 0.0007f; // 1440 Hz
    private const float N1H4Level     = 0.0003f; // 1920 Hz
    private const float N1WobbleRate  = 0.5f;    // Hz — slow fan RPM swell
    private const float N1WobbleDepth = 4.0f;    // ±Hz
    private double _n1Phase;
    private double _n1WobblePhase;

    // N2 core compressor — higher harmonic series, slightly faster wobble (core RPM variation)
    private const float N2Freq        = 750f;
    private const float N2Level       = 0.0018f; //  750 Hz
    private const float N2H2Level     = 0.001f;  // 1500 Hz
    private const float N2H3Level     = 0.0004f; // 2250 Hz
    private const float N2WobbleRate  = 0.7f;    // Hz — core RPM drifts faster than fan
    private const float N2WobbleDepth = 7.0f;    // ±Hz
    private double _n2Phase;
    private double _n2WobblePhase;

    // Airframe AM — two oscillators beating for organic lope
    private const float RumbleFreq1  = 57f;
    private const float RumbleFreq2  = 76f;
    private const float RumbleDepth1 = 0.09f;
    private const float RumbleDepth2 = 0.09f;
    private double _rumblePhase1;
    private double _rumblePhase2;

    // Engine roar — LCG noise → one-pole LPF 350 Hz
    private const float RoarLevel = 0.018f;
    private readonly float _roarLpA;
    private float _roarLpState;
    private uint _noiseState = 0x9E3779B9u;

    public AirGenericAmbientEffect(int sampleRate, float strength)
    {
        _sampleRate = sampleRate;
        _strength   = strength;

        _muffleA = MathF.Exp(-2f * MathF.PI * MuffleCutoff / sampleRate);
        _roarLpA = MathF.Exp(-2f * MathF.PI * 350f / sampleRate);
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume)
    {
        double n1WobbleInc = 2.0 * Math.PI * N1WobbleRate / _sampleRate;
        double n2WobbleInc = 2.0 * Math.PI * N2WobbleRate / _sampleRate;
        double rumbleInc1  = 2.0 * Math.PI * RumbleFreq1  / _sampleRate;
        double rumbleInc2  = 2.0 * Math.PI * RumbleFreq2  / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // N1 fan: independent FM wobble → harmonic series
            double n1Wobble = Math.Sin(_n1WobblePhase) * N1WobbleDepth;
            _n1WobblePhase += n1WobbleInc;
            if (_n1WobblePhase > Math.PI * 2) _n1WobblePhase -= Math.PI * 2;

            double n1Inc = 2.0 * Math.PI * (N1Freq + n1Wobble) / _sampleRate;
            float n1Sample = ((float)Math.Sin(_n1Phase)       * N1Level
                           +  (float)Math.Sin(_n1Phase * 2.0) * N1H2Level
                           +  (float)Math.Sin(_n1Phase * 3.0) * N1H3Level
                           +  (float)Math.Sin(_n1Phase * 4.0) * N1H4Level) * _strength;
            _n1Phase += n1Inc;
            if (_n1Phase > Math.PI * 2) _n1Phase -= Math.PI * 2;

            // N2 core: independent FM wobble → harmonic series
            double n2Wobble = Math.Sin(_n2WobblePhase) * N2WobbleDepth;
            _n2WobblePhase += n2WobbleInc;
            if (_n2WobblePhase > Math.PI * 2) _n2WobblePhase -= Math.PI * 2;

            double n2Inc = 2.0 * Math.PI * (N2Freq + n2Wobble) / _sampleRate;
            float n2Sample = ((float)Math.Sin(_n2Phase)       * N2Level
                           +  (float)Math.Sin(_n2Phase * 2.0) * N2H2Level
                           +  (float)Math.Sin(_n2Phase * 3.0) * N2H3Level) * _strength;
            _n2Phase += n2Inc;
            if (_n2Phase > Math.PI * 2) _n2Phase -= Math.PI * 2;

            // Airframe AM
            float rumbleGain = 1f
                + (float)Math.Sin(_rumblePhase1) * RumbleDepth1
                + (float)Math.Sin(_rumblePhase2) * RumbleDepth2;
            _rumblePhase1 += rumbleInc1;
            _rumblePhase2 += rumbleInc2;
            if (_rumblePhase1 > Math.PI * 2) _rumblePhase1 -= Math.PI * 2;
            if (_rumblePhase2 > Math.PI * 2) _rumblePhase2 -= Math.PI * 2;

            // Engine roar
            _noiseState  = _noiseState * 1664525u + 1013904223u;
            float roar   = _roarLpA * _roarLpState + (1f - _roarLpA) * ((int)_noiseState * (1f / 2147483648f));
            _roarLpState = roar;

            int idx = offset + frame;
            float dry = buffer[idx];
            float x = dry;

            x *= rumbleGain;
            x += n1Sample + n2Sample;
            x += roar * RoarLevel * _strength;

            // Transmitter ALC: clean makeup gain + peak limiter → loud, no crunch
            x = AmbientDsp.SoftAlc(x, AlcMakeup, AlcKnee);

            // Oxygen-mask two-pole LPF + nasal cavity blend
            float lp1 = _muffleA * _muffleLP1 + (1f - _muffleA) * x;
            _muffleLP1 = lp1;
            float lp2 = _muffleA * _muffleLP2 + (1f - _muffleA) * lp1;
            _muffleLP2 = lp2;

            float wet = lp2 * 0.55f + x * 0.45f;
            buffer[idx] = dry + volume * (wet - dry);
        }
    }
}

// ---------------------------------------------------------------------------
//  AirA10 — A-10C cockpit
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain -- the same generic-jet-cockpit base as <see cref="AirGenericAmbientEffect"/>
/// (airframe AM, N1/N2 engine whine, engine roar, oxygen mask), plus a distinct A-10-specific
/// inverter-whine layer on top:
///   clean PCM
///   → [PreFade]  airframe AM: two beating oscillators (57 Hz + 76 Hz)
///   → [PreFade]  engine whine: N1 fan series (~480 Hz) + N2 core series (~750 Hz)
///   → [PreFade]  engine roar: noise → one-pole LPF 350 Hz (additive)
///   → [PreFade]  twin 400Hz inverter whine: two independent oscillators (~399/~398Hz) beating,
///                soft-saturated for a buzzier, less "pure-tone" character, slowly wobbling in
///                pitch so it doesn't sit at a razor-locked frequency
///   → [PreFade]  HF avionics/gyro whine cluster (~4.7-4.8kHz), light
///   → [PreFade]  oxygen-mask two-pole LPF 1900 Hz + nasal cavity blend
///   → RF fading  (handled by RadioEffect)
///
/// Tuned against a pair of reference A-10C cockpit recordings: the ~398-400Hz tone is by far the
/// loudest single feature in the spectrum, and it beats slowly at ~1.8Hz. The twin-TF34 airframe
/// runs two independent 400Hz AC inverters a couple Hz apart, which produces exactly that slow
/// beat as a byproduct of summing two real oscillators rather than needing an explicit LFO. A
/// literal from-measurement reconstruction (near-pure fundamental + faint harmonics) read as a
/// clean lab test tone rather than a real electrical hum, so the harmonics here are pushed well
/// above their measured levels and soft-clipped for grit; the whole layer sits underneath the
/// airframe/engine base from AirGeneric instead of replacing it.
/// </summary>
internal sealed class AirA10AmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly float _strength;

    // Oxygen-mask + comms band-limit -- same cutoff as the other fighter effects; A-10C pilots
    // fly the same HGU-55/MBU-12 helmet-and-mask combo.
    private const float MuffleCutoff = 1900f;
    private readonly float _muffleA;
    private float _muffleLP1;
    private float _muffleLP2;

    private const float AlcMakeup = 2.0f;
    private const float AlcKnee   = 0.70f;

    // Airframe AM -- two independent oscillators beating, same as AirGeneric
    private const float RumbleFreq1  = 57f;
    private const float RumbleFreq2  = 76f;
    private const float RumbleDepth1 = 0.09f;
    private const float RumbleDepth2 = 0.09f;
    private double _rumblePhase1;
    private double _rumblePhase2;

    // Engine whine -- N1 fan + N2 core series, same as AirGeneric
    private const float N1Freq = 480f, N1Level = 0.0022f, N1H2Level = 0.0014f, N1H3Level = 0.0007f, N1H4Level = 0.0003f;
    private const float N1WobbleRate = 0.5f, N1WobbleDepth = 4.0f;
    private double _n1Phase, _n1WobblePhase;
    private const float N2Freq = 750f, N2Level = 0.0018f, N2H2Level = 0.001f, N2H3Level = 0.0004f;
    private const float N2WobbleRate = 0.7f, N2WobbleDepth = 7.0f;
    private double _n2Phase, _n2WobblePhase;

    // Engine roar -- noise → one-pole LPF 350Hz, same level as AirGeneric. This is the base
    // engine bed the inverter whine sits on top of, not the other way around.
    private const float RoarLevel = 0.018f;
    private readonly float _roarLpA;
    private float _roarLpState;
    private uint _noiseState = 0x9E3779B9u;

    // Twin 400Hz inverter whine -- two real oscillators a couple Hz apart so the ~1.8Hz beat
    // measured in the reference recordings falls out of the superposition for free. Harmonics are
    // deliberately richer than measured (a near-pure reconstruction sounded like a lab test tone,
    // not an electrical hum), and the pair is soft-saturated afterward for grit and to naturally
    // tame the peak. A slow, small wobble on each oscillator keeps the tone from sitting at a
    // razor-locked frequency, same idea as the F-16 inverter's FM wobble.
    private const float InvFreqA   = 399.3f;
    private const float InvFreqB   = 397.5f;
    private const float InvLevel   = 0.020f; // fundamental -- an accent layer now, not the dominant element
    private const float InvH2Ratio = 0.35f;
    private const float InvH3Ratio = 0.20f;
    private const float InvH4Ratio = 0.10f;
    private const float InvH5Ratio = 0.15f;
    private const float InvWobbleRate  = 0.35f; // Hz
    private const float InvWobbleDepth = 1.2f;  // ±Hz drift
    private const float InvSatDrive = 1.6f;
    private static readonly float InvSatNorm = 1.0f / MathF.Tanh(InvSatDrive);
    private double _invPhaseA, _invPhaseB;
    private double _invWobblePhaseA, _invWobblePhaseB;

    // High-pitched gyro/avionics-cooling whine cluster -- a tight pair of tones near 4.7-4.8kHz,
    // present in both reference recordings, well below the inverter.
    private const float HfFreqA = 4760f;
    private const float HfFreqB = 4784f;
    private const float HfLevel = 0.0025f;
    private double _hfPhaseA;
    private double _hfPhaseB;

    public AirA10AmbientEffect(int sampleRate, float strength)
    {
        _sampleRate = sampleRate;
        _strength   = strength;

        _muffleA = MathF.Exp(-2f * MathF.PI * MuffleCutoff / sampleRate);
        _roarLpA = MathF.Exp(-2f * MathF.PI * 350f / sampleRate);
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume)
    {
        double rumbleInc1 = 2.0 * Math.PI * RumbleFreq1 / _sampleRate;
        double rumbleInc2 = 2.0 * Math.PI * RumbleFreq2 / _sampleRate;
        double n1WobbleInc = 2.0 * Math.PI * N1WobbleRate / _sampleRate;
        double n2WobbleInc = 2.0 * Math.PI * N2WobbleRate / _sampleRate;
        double invWobbleInc = 2.0 * Math.PI * InvWobbleRate / _sampleRate;
        double hfIncA = 2.0 * Math.PI * HfFreqA / _sampleRate;
        double hfIncB = 2.0 * Math.PI * HfFreqB / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Airframe AM
            float rumbleGain = 1f
                + (float)Math.Sin(_rumblePhase1) * RumbleDepth1
                + (float)Math.Sin(_rumblePhase2) * RumbleDepth2;
            _rumblePhase1 += rumbleInc1; if (_rumblePhase1 > Math.PI * 2) _rumblePhase1 -= Math.PI * 2;
            _rumblePhase2 += rumbleInc2; if (_rumblePhase2 > Math.PI * 2) _rumblePhase2 -= Math.PI * 2;

            // N1 fan
            double n1Wobble = Math.Sin(_n1WobblePhase) * N1WobbleDepth;
            _n1WobblePhase += n1WobbleInc; if (_n1WobblePhase > Math.PI * 2) _n1WobblePhase -= Math.PI * 2;
            double n1Inc = 2.0 * Math.PI * (N1Freq + n1Wobble) / _sampleRate;
            float n1Sample = ((float)Math.Sin(_n1Phase) * N1Level
                           + (float)Math.Sin(_n1Phase * 2.0) * N1H2Level
                           + (float)Math.Sin(_n1Phase * 3.0) * N1H3Level
                           + (float)Math.Sin(_n1Phase * 4.0) * N1H4Level) * _strength;
            _n1Phase += n1Inc; if (_n1Phase > Math.PI * 2) _n1Phase -= Math.PI * 2;

            // N2 core
            double n2Wobble = Math.Sin(_n2WobblePhase) * N2WobbleDepth;
            _n2WobblePhase += n2WobbleInc; if (_n2WobblePhase > Math.PI * 2) _n2WobblePhase -= Math.PI * 2;
            double n2Inc = 2.0 * Math.PI * (N2Freq + n2Wobble) / _sampleRate;
            float n2Sample = ((float)Math.Sin(_n2Phase) * N2Level
                           + (float)Math.Sin(_n2Phase * 2.0) * N2H2Level
                           + (float)Math.Sin(_n2Phase * 3.0) * N2H3Level) * _strength;
            _n2Phase += n2Inc; if (_n2Phase > Math.PI * 2) _n2Phase -= Math.PI * 2;

            // Engine roar: Knuth LCG → one-pole LPF
            _noiseState  = _noiseState * 1664525u + 1013904223u;
            float roar   = _roarLpA * _roarLpState + (1f - _roarLpA) * ((int)_noiseState * (1f / 2147483648f));
            _roarLpState = roar;

            // Twin inverters: independent slow wobble per oscillator keeps the beat from sounding
            // metronomically exact, then a rich harmonic stack is summed and soft-saturated.
            double invWobbleA = Math.Sin(_invWobblePhaseA) * InvWobbleDepth;
            double invWobbleB = Math.Sin(_invWobblePhaseB * 1.3) * InvWobbleDepth;
            _invWobblePhaseA += invWobbleInc; if (_invWobblePhaseA > Math.PI * 2) _invWobblePhaseA -= Math.PI * 2;
            _invWobblePhaseB += invWobbleInc; if (_invWobblePhaseB > Math.PI * 2) _invWobblePhaseB -= Math.PI * 2;
            double invIncA = 2.0 * Math.PI * (InvFreqA + invWobbleA) / _sampleRate;
            double invIncB = 2.0 * Math.PI * (InvFreqB + invWobbleB) / _sampleRate;

            float invRaw = ((float)Math.Sin(_invPhaseA)       + (float)Math.Sin(_invPhaseB))       * 0.5f
                         + ((float)Math.Sin(_invPhaseA * 2.0) + (float)Math.Sin(_invPhaseB * 2.0)) * 0.5f * InvH2Ratio
                         + ((float)Math.Sin(_invPhaseA * 3.0) + (float)Math.Sin(_invPhaseB * 3.0)) * 0.5f * InvH3Ratio
                         + ((float)Math.Sin(_invPhaseA * 4.0) + (float)Math.Sin(_invPhaseB * 4.0)) * 0.5f * InvH4Ratio
                         + ((float)Math.Sin(_invPhaseA * 5.0) + (float)Math.Sin(_invPhaseB * 5.0)) * 0.5f * InvH5Ratio;
            float invSample = MathF.Tanh(invRaw * InvSatDrive) * InvSatNorm * InvLevel * _strength;
            _invPhaseA += invIncA; if (_invPhaseA > Math.PI * 2) _invPhaseA -= Math.PI * 2;
            _invPhaseB += invIncB; if (_invPhaseB > Math.PI * 2) _invPhaseB -= Math.PI * 2;

            float hfSample = ((float)Math.Sin(_hfPhaseA) + (float)Math.Sin(_hfPhaseB)) * 0.5f * HfLevel * _strength;
            _hfPhaseA += hfIncA; if (_hfPhaseA > Math.PI * 2) _hfPhaseA -= Math.PI * 2;
            _hfPhaseB += hfIncB; if (_hfPhaseB > Math.PI * 2) _hfPhaseB -= Math.PI * 2;

            int idx = offset + frame;
            float dry = buffer[idx];
            float x = dry;

            x *= rumbleGain;                    // airframe vibration AM-modulates mic pickup
            x += n1Sample + n2Sample;           // jet engine whine
            x += roar * RoarLevel * _strength;  // structure-borne engine roar
            x += invSample;                     // twin 400Hz inverter whine (accent layer)
            x += hfSample;                      // avionics/gyro HF whine cluster

            // Transmitter ALC: clean makeup gain + peak limiter → loud, no crunch
            x = AmbientDsp.SoftAlc(x, AlcMakeup, AlcKnee);

            // Oxygen-mask two-pole LPF + nasal cavity blend
            float lp1 = _muffleA * _muffleLP1 + (1f - _muffleA) * x;
            _muffleLP1 = lp1;
            float lp2 = _muffleA * _muffleLP2 + (1f - _muffleA) * lp1;
            _muffleLP2 = lp2;

            float wet = lp2 * 0.55f + x * 0.45f;
            buffer[idx] = dry + volume * (wet - dry);
        }
    }
}

// ---------------------------------------------------------------------------
//  AirUH60 — UH-60L Black Hawk cockpit
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  main-rotor AM: 17.2Hz blade-passage thump (4 blades @ ~258 RPM), shaped for a
///                punchier "wop" than a plain sine, plus a lighter ~86.5Hz tail-rotor flutter
///   → [PreFade]  blade-slap ring: a ~350Hz structural resonance re-triggered once per main-rotor
///                blade pass and left to decay, rather than a fixed continuous tone
///   → [PreFade]  main gearbox whine: ~990Hz gear-mesh tone
///   → [PreFade]  engine/transmission roar: noise → one-pole LPF (T700 turboshaft + gearbox)
///   → [PreFade]  headset/boom-mic band-limit LPF 2400 Hz + partial dry blend
///   → RF fading  (handled by RadioEffect)
///
/// Tuned against a pair of reference UH-60L cockpit recordings. The main rotor's 17.2Hz blade-
/// passage rate (4 blades, ~258 RPM) is the strongest low-frequency feature; a ~350Hz resonance is
/// nearly as strong and rings on every blade pass rather than sounding continuously, which is why
/// it's modeled as a re-triggered decaying oscillator instead of a steady tone. A secondary
/// ~86.5Hz component matches the tail rotor (geared ~5.03:1 off the main rotor). Because the
/// PostFade stage high-passes at 300Hz, the rotor rate itself can't survive as an additive tone --
/// it only reaches the transmitted audio as amplitude modulation of in-band content, which is
/// physically consistent with how rotor thump actually reaches a radio downlink.
/// </summary>
internal sealed class AirUH60AmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly float _strength;

    // Headset/boom-mic band-limit -- higher cutoff and lighter blend than a jet oxygen mask;
    // UH-60 crews fly HGU-56 helmets with a boom mic, not a full mask.
    private const float MuffleCutoff = 2400f;
    private readonly float _muffleA;
    private float _muffleLP;

    private const float AlcMakeup = 1.8f;
    private const float AlcKnee   = 0.70f;

    // Main rotor: 4 blades @ ~258 RPM → 17.2Hz blade-passage frequency (measured 17.2-17.6Hz).
    // AM depth is NOT scaled by strength -- it's part of the platform character, same convention
    // as the jet/ground rumble.
    private const float MainRotorBpf     = 17.2f;
    private const float MainRotorAmDepth = 0.30f;
    private double _mainRotorPhase;

    // Tail rotor: geared ~5.03:1 off the main rotor → ~86.5Hz, matching the secondary ~80-88Hz
    // peaks in the reference recordings. Lighter, higher-pitched flutter on top of the main thump.
    private const float TailRotorBpf     = MainRotorBpf * 5.03f;
    private const float TailRotorAmDepth = 0.06f;
    private double _tailRotorPhase;

    // Blade-slap ring: each main-rotor blade pass excites a ~350Hz structural resonance (measured
    // 351.6Hz, nearly as strong as the 17Hz fundamental) that rings and decays before the next
    // blade pass. Re-triggered once per revolution rather than a fixed tone so it stays locked to
    // blade rate regardless of strength.
    private const float RingFreq  = 350f;
    private const float RingLevel = 0.045f;
    private readonly int _bladePassSamples;
    private double _ringPhase;
    private float _ringAmp;
    private readonly float _ringDecayPerSample;
    private int _samplesToNextBladePass;

    // Engine/transmission roar -- T700 turboshaft + main gearbox, broadband noise → one-pole LPF.
    // 480Hz cutoff so energy survives the PostFade 300Hz high-pass, same reasoning as
    // GroundAmbientEffect's engine roar.
    private const float RoarLevel = 0.028f;
    private readonly float _roarLpA;
    private float _roarLpState;
    private uint _roarNoiseState = 0x9E3779B9u;

    // Main gearbox whine -- gear-mesh tone, clearly present in both reference recordings near 1kHz.
    private const float GearboxFreq  = 990f;
    private const float GearboxLevel = 0.006f;
    private double _gearboxPhase;

    public AirUH60AmbientEffect(int sampleRate, float strength)
    {
        _sampleRate = sampleRate;
        _strength   = strength;

        _muffleA = MathF.Exp(-2f * MathF.PI * MuffleCutoff / sampleRate);
        _roarLpA = MathF.Exp(-2f * MathF.PI * 480f / sampleRate);

        _bladePassSamples = Math.Max(1, (int)(sampleRate / MainRotorBpf));
        // Ring decays to ~1% amplitude over one blade-pass period so it doesn't build up.
        _ringDecayPerSample = MathF.Exp(MathF.Log(0.01f) / _bladePassSamples);
        _samplesToNextBladePass = _bladePassSamples;
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume)
    {
        double mainInc     = 2.0 * Math.PI * MainRotorBpf / _sampleRate;
        double tailInc     = 2.0 * Math.PI * TailRotorBpf / _sampleRate;
        double ringInc     = 2.0 * Math.PI * RingFreq     / _sampleRate;
        double gearboxInc  = 2.0 * Math.PI * GearboxFreq  / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Re-trigger the blade-slap resonance once per main-rotor blade pass.
            if (--_samplesToNextBladePass <= 0)
            {
                _ringAmp = 1f;
                _ringPhase = 0.0;
                _samplesToNextBladePass = _bladePassSamples;
            }

            float ringSample = (float)Math.Sin(_ringPhase) * _ringAmp * RingLevel * _strength;
            _ringPhase += ringInc;
            if (_ringPhase > Math.PI * 2) _ringPhase -= Math.PI * 2;
            _ringAmp *= _ringDecayPerSample;

            // Rotor thump: main + tail rotor AM. The main term is sharpened (sin raised to a
            // fractional power, sign-preserved) for a punchier "wop" than a plain sine would give.
            float mainWave    = (float)Math.Sin(_mainRotorPhase);
            float shapedMain  = MathF.Sign(mainWave) * MathF.Pow(MathF.Abs(mainWave), 0.6f);
            float tailWave    = (float)Math.Sin(_tailRotorPhase);
            float thrumGain   = 1f + shapedMain * MainRotorAmDepth + tailWave * TailRotorAmDepth;
            _mainRotorPhase += mainInc;
            _tailRotorPhase += tailInc;
            if (_mainRotorPhase > Math.PI * 2) _mainRotorPhase -= Math.PI * 2;
            if (_tailRotorPhase > Math.PI * 2) _tailRotorPhase -= Math.PI * 2;

            // Engine/gearbox roar: Knuth LCG → one-pole LPF
            _roarNoiseState = _roarNoiseState * 1664525u + 1013904223u;
            float roar      = _roarLpA * _roarLpState + (1f - _roarLpA) * ((int)_roarNoiseState * (1f / 2147483648f));
            _roarLpState    = roar;

            float gearboxSample = (float)Math.Sin(_gearboxPhase) * GearboxLevel * _strength;
            _gearboxPhase += gearboxInc;
            if (_gearboxPhase > Math.PI * 2) _gearboxPhase -= Math.PI * 2;

            int idx = offset + frame;
            float dry = buffer[idx];
            float x = dry;

            x *= thrumGain;                            // rotor vibration AM-modulates mic pickup
            x += ringSample;                            // blade-slap structural resonance
            x += roar * RoarLevel * _strength;          // engine + transmission roar
            x += gearboxSample;                         // main gearbox gear-mesh whine

            x = AmbientDsp.SoftAlc(x, AlcMakeup, AlcKnee);

            // Headset/boom-mic band-limit, lighter blend than the jet oxygen mask
            float lp = _muffleA * _muffleLP + (1f - _muffleA) * x;
            _muffleLP = lp;

            float wet = lp * 0.45f + x * 0.55f;
            buffer[idx] = dry + volume * (wet - dry);
        }
    }
}

// ---------------------------------------------------------------------------
//  Ground — ground vehicle (APC / HMMWV / tank)
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  diesel AM: two oscillators (45 Hz firing + 67 Hz hull structural)
///   → [PreFade]  engine roar: noise → two-pole LPF 500 Hz (kept above 300 Hz PostFade cutoff)
///   → [PreFade]  drivetrain whine: bandpass noise 350–900 Hz (dominant in-band character)
///   → [PreFade]  chassis clatter: noise → LPF 450 Hz (track/suspension impacts)
///   → [PreFade]  crew compartment coloration: LPF 2000 Hz (sealed metal hull)
///   → RF fading  (handled by RadioEffect)
/// </summary>
internal sealed class GroundAmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly float _strength;

    // Diesel AM — firing frequency + hull resonance beating for organic lope.
    // Higher depths than jet: no acoustic isolation between engine and crew.
    private const float ThrumFreq1  = 45f;
    private const float ThrumFreq2  = 67f;
    private const float ThrumDepth1 = 0.25f;
    private const float ThrumDepth2 = 0.13f;
    private double _thrumPhase1;
    private double _thrumPhase2;

    // Engine roar — noise → two-pole LPF 500 Hz. Two poles for heavier diesel tilt;
    // 500 Hz cutoff ensures energy survives the PostFade 300 Hz high-pass.
    private const float RoarLevel = 0.040f;
    private readonly float _roarLpA;
    private float _roarLpState1;
    private float _roarLpState2;
    private uint  _roarNoiseState = 0xDEADBEEFu;

    // Drivetrain whine — bandpass noise 350–900 Hz via LP subtraction.
    // Gearbox/differential grind; sits squarely inside the radio passband.
    private const float DrivetrainLevel = 0.018f;
    private readonly float _driveHpA;  // LP 350 Hz (subtract to remove low end)
    private readonly float _driveLpA;  // LP 900 Hz
    private float _driveHpState;
    private float _driveLpState;
    private uint  _driveNoiseState = 0xFEDCBA98u;

    // Chassis clatter — noise → LP 450 Hz. Track/suspension impact noise.
    private const float ClatterLevel = 0.014f;
    private readonly float _clatterLpA;
    private float _clatterLpState;
    private uint  _clatterNoiseState = 0xCAFEBABEu;

    // Crew compartment coloration — LP 2000 Hz, boxy sealed-hull character
    private const float CompartmentCutoff = 2000f;
    private readonly float _compartmentA;
    private float _compartmentLP;

    public GroundAmbientEffect(int sampleRate, float strength)
    {
        _sampleRate = sampleRate;
        _strength   = strength;

        _roarLpA    = MathF.Exp(-2f * MathF.PI * 500f / sampleRate);
        _driveHpA   = MathF.Exp(-2f * MathF.PI * 350f / sampleRate);
        _driveLpA   = MathF.Exp(-2f * MathF.PI * 900f / sampleRate);
        _clatterLpA = MathF.Exp(-2f * MathF.PI * 450f / sampleRate);

        _compartmentA = MathF.Exp(-2f * MathF.PI * CompartmentCutoff / sampleRate);
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume)
    {
        double thrumInc1 = 2.0 * Math.PI * ThrumFreq1 / _sampleRate;
        double thrumInc2 = 2.0 * Math.PI * ThrumFreq2 / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Diesel AM: two oscillators beating
            // AM depth is NOT scaled - it's part of the platform character
            float thrumGain = 1f
                + (float)Math.Sin(_thrumPhase1) * ThrumDepth1
                + (float)Math.Sin(_thrumPhase2) * ThrumDepth2;
            _thrumPhase1 += thrumInc1;
            _thrumPhase2 += thrumInc2;
            if (_thrumPhase1 > Math.PI * 2) _thrumPhase1 -= Math.PI * 2;
            if (_thrumPhase2 > Math.PI * 2) _thrumPhase2 -= Math.PI * 2;

            // Engine roar: noise → two-pole LPF 500 Hz
            _roarNoiseState = _roarNoiseState * 1664525u + 1013904223u;
            float rawRoar   = (int)_roarNoiseState * (1f / 2147483648f);
            float roarLp1   = _roarLpA * _roarLpState1 + (1f - _roarLpA) * rawRoar;
            float roarLp2   = _roarLpA * _roarLpState2 + (1f - _roarLpA) * roarLp1;
            _roarLpState1   = roarLp1;
            _roarLpState2   = roarLp2;

            // Drivetrain whine: bandpass via LP900 − LP350
            _driveNoiseState = _driveNoiseState * 1664525u + 1013904223u;
            float rawDrive   = (int)_driveNoiseState * (1f / 2147483648f);
            float driveLp    = _driveLpA * _driveLpState + (1f - _driveLpA) * rawDrive;
            float driveHp    = _driveHpA * _driveHpState + (1f - _driveHpA) * rawDrive;
            _driveLpState    = driveLp;
            _driveHpState    = driveHp;

            // Chassis clatter: noise → LP 450 Hz
            _clatterNoiseState = _clatterNoiseState * 1664525u + 1013904223u;
            float clatterLp    = _clatterLpA * _clatterLpState + (1f - _clatterLpA) * ((int)_clatterNoiseState * (1f / 2147483648f));
            _clatterLpState    = clatterLp;

            int idx = offset + frame;
            float dry = buffer[idx];
            float x = dry;

            x *= thrumGain;                                       // engine vibration AM-modulates mic pickup
            x += roarLp2   * RoarLevel * _strength;               // acoustic engine roar
            x += (driveLp - driveHp) * DrivetrainLevel * _strength; // drivetrain/gearbox grind
            x += clatterLp * ClatterLevel * _strength;            // track and chassis clatter

            float lp = _compartmentA * _compartmentLP + (1f - _compartmentA) * x;
            _compartmentLP = lp;

            buffer[idx] = dry + volume * (lp - dry);
        }
    }
}

// ---------------------------------------------------------------------------
//  Stationary — ground station / AWACS / GCI bunker
// ---------------------------------------------------------------------------

/// <summary>
/// Signal chain:
///   clean PCM
///   → [PreFade]  mains hum: 50 Hz + 100 Hz (2nd) + 150 Hz (3rd), electrical bleed
///   → [PreFade]  HVAC turbulence: noise → LPF 220 Hz, slow duct-pressure AM (0.25 Hz)
///   → [PreFade]  electronics hiss: noise → bandpass 300–1500 Hz (rack equipment + fans)
///   → [PreFade]  room coloration: LPF 3500 Hz (open desk mic in enclosed ops room)
///   → RF fading  (handled by RadioEffect)
/// </summary>
internal sealed class StationaryAmbientEffect : IAmbientNoiseEffect
{
    private readonly int _sampleRate;
    private readonly float _strength;

    // Mains hum — 50 Hz fundamental + 2nd harmonic (rectifier ripple) + 3rd (odd distortion)
    private const float MainsFreq        = 50f;
    private const float MainsFundamental = 0.004f;
    private const float MainsH2Level     = 0.003f; // 100 Hz
    private const float MainsH3Level     = 0.002f; // 150 Hz
    private double _mainsPhase;

    // HVAC — broadband turbulence noise, LPF 220 Hz + 0.25 Hz AM for duct pressure swells
    private const float HvacLevel       = 0.010f;
    private const float HvacDuctAMRate  = 0.25f;
    private const float HvacDuctAMDepth = 0.30f;
    private readonly float _hvacLpA;
    private float _hvacLpState;
    private uint  _hvacNoiseState = 0x13579BDFu;
    private double _hvacAMPhase;

    // Electronics hiss — rack equipment + fans, bandpass 300–1500 Hz via LP subtraction
    private const float HissLevel = 0.003f;
    private readonly float _hissLpHighA; // LP 1500 Hz
    private readonly float _hissLpLowA;  // LP 300 Hz (subtract to strip low end)
    private float _hissLpHighState;
    private float _hissLpLowState;
    private uint  _hissNoiseState = 0x2468ACEFu;

    // Room coloration — LP 3500 Hz, mild HF absorption of a furnished ops room
    private const float RoomCutoff = 3500f;
    private readonly float _roomA;
    private float _roomLP;

    public StationaryAmbientEffect(int sampleRate, float strength)
    {
        _sampleRate = sampleRate;
        _strength   = strength;

        _hvacLpA     = MathF.Exp(-2f * MathF.PI * 220f  / sampleRate);
        _hissLpHighA = MathF.Exp(-2f * MathF.PI * 1500f / sampleRate);
        _hissLpLowA  = MathF.Exp(-2f * MathF.PI * 300f  / sampleRate);
        _roomA       = MathF.Exp(-2f * MathF.PI * RoomCutoff / sampleRate);
    }

    public void ApplyPreFade(float[] buffer, int offset, int frames, float volume)
    {
        double mainsInc  = 2.0 * Math.PI * MainsFreq      / _sampleRate;
        double hvacAMInc = 2.0 * Math.PI * HvacDuctAMRate / _sampleRate;

        for (int frame = 0; frame < frames; frame++)
        {
            // Mains hum: harmonics share the same phase reference
            float mainsSample = ((float)Math.Sin(_mainsPhase)       * MainsFundamental
                              +  (float)Math.Sin(_mainsPhase * 2.0) * MainsH2Level
                              +  (float)Math.Sin(_mainsPhase * 3.0) * MainsH3Level) * _strength;
            _mainsPhase += mainsInc;
            if (_mainsPhase > Math.PI * 2) _mainsPhase -= Math.PI * 2;

            // HVAC: noise → LP 220 Hz, slow AM for duct pressure fluctuations
            _hvacNoiseState = _hvacNoiseState * 1664525u + 1013904223u;
            float hvacLp    = _hvacLpA * _hvacLpState + (1f - _hvacLpA) * ((int)_hvacNoiseState * (1f / 2147483648f));
            _hvacLpState    = hvacLp;
            // AM depth is NOT scaled - it's part of the HVAC character
            float hvacAM    = 1f + (float)Math.Sin(_hvacAMPhase) * HvacDuctAMDepth;
            _hvacAMPhase   += hvacAMInc;
            if (_hvacAMPhase > Math.PI * 2) _hvacAMPhase -= Math.PI * 2;

            // Electronics hiss: bandpass via LP1500 − LP300
            _hissNoiseState  = _hissNoiseState * 1664525u + 1013904223u;
            float rawHiss    = (int)_hissNoiseState * (1f / 2147483648f);
            float hissHigh   = _hissLpHighA * _hissLpHighState + (1f - _hissLpHighA) * rawHiss;
            float hissLow    = _hissLpLowA  * _hissLpLowState  + (1f - _hissLpLowA)  * rawHiss;
            _hissLpHighState = hissHigh;
            _hissLpLowState  = hissLow;

            int idx = offset + frame;
            float dry = buffer[idx];
            float x = dry;

            x += mainsSample;
            x += hvacLp * HvacLevel * _strength * hvacAM;
            x += (hissHigh - hissLow) * HissLevel * _strength;

            float lp = _roomA * _roomLP + (1f - _roomA) * x;
            _roomLP = lp;

            buffer[idx] = dry + volume * (lp - dry);
        }
    }
}
