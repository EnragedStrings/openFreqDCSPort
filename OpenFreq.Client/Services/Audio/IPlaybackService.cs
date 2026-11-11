using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenFreqAudio;
using OpenFreqAudio.Jamming;

namespace OpenFreqClient.Services.Audio;

/// <summary>
/// Abstraction over <see cref="RadioPlayback"/> covering the surface used by
/// <see cref="OpenFreqClient.Services.OpenFreqService"/>. Exists so the BASS-backed playback engine
/// (which needs a real audio device) can be replaced with a fake in tests.
/// </summary>
public interface IPlaybackService
{
    event Action<string>? UserFacingError;

    bool SidetoneEnabled { get; set; }
    float SidetoneVolume { get; set; }
    float MasterVolume { get; set; }
    float AmbientNoiseVolume { get; set; }
    float OwnVoiceSfxVolume { get; set; }
    MaskProfile OwnVoiceMaskProfile { get; set; }
    AmbientNoiseType OwnVoiceAmbient { get; set; }
    int OwnVoiceFrequencyKhz { get; set; }
    bool IsRecording { get; }
    bool IsMonitoring { get; }
    bool IsCapturing { get; }
    bool IsFeedingHudTape { get; }
    bool WantsOwnVoice { get; }

    /// <summary>Hot mic on the HUD Recorder feed (see RadioPlayback.HotMicEnabled).</summary>
    bool HotMicEnabled { get; set; }

    /// <summary>The HUD feed runs with the hot mic on.</summary>
    bool WantsHotMic { get; }

    void Initialize();
    void EnsureMasterStreamRunning();
    void ChangeOutputDevice(int newDeviceIndex);
    Task StopAll();

    void TuneFrequency(int frequencyKhz, Guid slotId);
    void UntuneFrequency(int frequencyKhz, Guid slotId);
    void SetSquelchLevel(int frequencyKhz, Guid slotId, float squelchLevel);
    void SetFrequencyVolume(int frequencyKhz, Guid slotId, float volume);
    void SetFrequencyPan(int frequencyKhz, Guid slotId, int pan);
    void SetFrequencySpatial(int frequencyKhz, Guid slotId, bool enabled, float azimuthDeg, float elevationDeg);
    void SetSlotEncryption(int frequencyKhz, Guid slotId, bool enc, int encKey, bool hqOn, bool cryptoCapable);
    void SetSatcomState(int frequencyKhz, Guid slotId, bool isActive, double frameErrorRate, double burstSeverity,
        double frameDurationSeconds = 0.0225, double propagationLatencySeconds = 0.0);
    void SetEncryptionToneAssets(byte[]? txWavBytes, byte[]? rxWavBytes);
    void PlayTxTone();

    /// <summary>Ownship mic click (see RadioPlayback.PlayMicClick).</summary>
    bool MicClickEnabled { get; set; }
    float MicClickVolume { get; set; }
    void SetMicClickAssets(byte[]? keyDownWavBytes, byte[]? keyUpWavBytes);
    void PlayMicClick(bool keyDown);

    /// <summary>Replaces the jammers this slot currently receives (empty = none). See docs/jamming.</summary>
    void SetSlotInterference(int frequencyKhz, Guid slotId, IReadOnlyList<JammerContribution> contributions);

    /// <summary>Sets a slot's HAVE QUICK / SINCGARS net (null = single channel). See docs/jamming/HAVE_QUICK.md.</summary>
    void SetSlotHopNet(int frequencyKhz, Guid slotId, OpenFreqAudio.Eccm.HopSlotState? hopNet) { }

    void AddTransmittingFrequencies(IEnumerable<int> frequencies);
    void RemoveTransmittingFrequencies(IEnumerable<int> frequencies);

    void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams);
    bool PushAudioData(string streamId, Memory<short> audioData, AmbientNoiseType ambientNoise = AmbientNoiseType.None,
        bool enc = false, int encKey = 0, bool hqOn = false, OpenFreqAudio.Eccm.HopNetIdentity? eccm = null);
    void UpdateStreamParams(string streamId, AudioParams newParams);
    void ClearStreamBuffer(string streamId);
    Task StopStream(string streamId);
    List<string> GetActiveStreams();
    bool IsStreamActive(string id);

    void StartRecording(string filePath);
    void StopRecording();
    void StartMonitor(int deviceIndex);
    void StopMonitor();
    void StartHudTapeFeed();
    void StopHudTapeFeed();
    void ClearSidetone();

    void PushSidetone(ReadOnlySpan<float> samples);
    void PushOwnVoiceForRecording(ReadOnlySpan<float> samples);
    void PushHotMicForFeed(ReadOnlySpan<float> samples);
}

/// <summary>Adapter forwarding <see cref="IPlaybackService"/> to a real <see cref="RadioPlayback"/>.</summary>
public sealed class RadioPlaybackAdapter : IPlaybackService
{
    private readonly RadioPlayback _inner;

    public RadioPlaybackAdapter(RadioPlayback inner)
    {
        _inner = inner;
        _inner.UserFacingError += msg => UserFacingError?.Invoke(msg);
    }

    public event Action<string>? UserFacingError;

    public bool SidetoneEnabled { get => _inner.SidetoneEnabled; set => _inner.SidetoneEnabled = value; }
    public float SidetoneVolume { get => _inner.SidetoneVolume; set => _inner.SidetoneVolume = value; }
    public float MasterVolume { get => _inner.MasterVolume; set => _inner.MasterVolume = value; }
    public float AmbientNoiseVolume { get => _inner.AmbientNoiseVolume; set => _inner.AmbientNoiseVolume = value; }
    public float OwnVoiceSfxVolume { get => _inner.OwnVoiceSfxVolume; set => _inner.OwnVoiceSfxVolume = value; }
    public MaskProfile OwnVoiceMaskProfile { get => _inner.OwnVoiceMaskProfile; set => _inner.OwnVoiceMaskProfile = value; }
    public AmbientNoiseType OwnVoiceAmbient { get => _inner.OwnVoiceAmbient; set => _inner.OwnVoiceAmbient = value; }
    public int OwnVoiceFrequencyKhz { get => _inner.OwnVoiceFrequencyKhz; set => _inner.OwnVoiceFrequencyKhz = value; }
    public bool IsRecording => _inner.IsRecording;
    public bool IsMonitoring => _inner.IsMonitoring;
    public bool IsCapturing => _inner.IsCapturing;
    public bool IsFeedingHudTape => _inner.IsFeedingHudTape;
    public bool WantsOwnVoice => _inner.WantsOwnVoice;

    public void Initialize() => _inner.Initialize();
    public void EnsureMasterStreamRunning() => _inner.EnsureMasterStreamRunning();
    public void ChangeOutputDevice(int newDeviceIndex) => _inner.ChangeOutputDevice(newDeviceIndex);
    public Task StopAll() => _inner.StopAll();

    public void TuneFrequency(int frequencyKhz, Guid slotId) => _inner.TuneFrequency(frequencyKhz, slotId);
    public void UntuneFrequency(int frequencyKhz, Guid slotId) => _inner.UntuneFrequency(frequencyKhz, slotId);
    public void SetSquelchLevel(int frequencyKhz, Guid slotId, float squelchLevel)
        => _inner.SetSquelchLevel(frequencyKhz, slotId, squelchLevel);
    public void SetFrequencyVolume(int frequencyKhz, Guid slotId, float volume)
        => _inner.SetFrequencyVolume(frequencyKhz, slotId, volume);
    public void SetFrequencyPan(int frequencyKhz, Guid slotId, int pan)
        => _inner.SetFrequencyPan(frequencyKhz, slotId, pan);
    public void SetFrequencySpatial(int frequencyKhz, Guid slotId, bool enabled, float azimuthDeg, float elevationDeg)
        => _inner.SetFrequencySpatial(frequencyKhz, slotId, enabled, azimuthDeg, elevationDeg);
    public void SetSlotEncryption(int frequencyKhz, Guid slotId, bool enc, int encKey, bool hqOn, bool cryptoCapable)
        => _inner.SetSlotEncryption(frequencyKhz, slotId, enc, encKey, hqOn, cryptoCapable);
    public void SetSatcomState(int frequencyKhz, Guid slotId, bool isActive, double frameErrorRate,
        double burstSeverity, double frameDurationSeconds = 0.0225, double propagationLatencySeconds = 0.0)
        => _inner.SetSatcomState(frequencyKhz, slotId, isActive, frameErrorRate, burstSeverity, frameDurationSeconds,
            propagationLatencySeconds);
    public void SetEncryptionToneAssets(byte[]? txWavBytes, byte[]? rxWavBytes)
        => _inner.SetEncryptionToneAssets(txWavBytes, rxWavBytes);
    public void PlayTxTone() => _inner.PlayTxTone();
    public bool MicClickEnabled { get => _inner.MicClickEnabled; set => _inner.MicClickEnabled = value; }
    public float MicClickVolume { get => _inner.MicClickVolume; set => _inner.MicClickVolume = value; }
    public void SetMicClickAssets(byte[]? keyDownWavBytes, byte[]? keyUpWavBytes)
        => _inner.SetMicClickAssets(keyDownWavBytes, keyUpWavBytes);
    public void PlayMicClick(bool keyDown) => _inner.PlayMicClick(keyDown);
    public void SetSlotInterference(int frequencyKhz, Guid slotId, IReadOnlyList<JammerContribution> contributions)
        => _inner.SetSlotInterference(frequencyKhz, slotId, contributions);
    public void SetSlotHopNet(int frequencyKhz, Guid slotId, OpenFreqAudio.Eccm.HopSlotState? hopNet)
        => _inner.SetSlotHopNet(frequencyKhz, slotId, hopNet);

    public void AddTransmittingFrequencies(IEnumerable<int> frequencies)
        => _inner.AddTransmittingFrequencies(frequencies);
    public void RemoveTransmittingFrequencies(IEnumerable<int> frequencies)
        => _inner.RemoveTransmittingFrequencies(frequencies);

    public void StartPushStream(string streamId, int sampleRate, int channels, AudioParams audioParams)
        => _inner.StartPushStream(streamId, sampleRate, channels, audioParams);
    public bool PushAudioData(string streamId, Memory<short> audioData, AmbientNoiseType ambientNoise = AmbientNoiseType.None,
        bool enc = false, int encKey = 0, bool hqOn = false, OpenFreqAudio.Eccm.HopNetIdentity? eccm = null)
        => _inner.PushAudioData(streamId, audioData, ambientNoise, enc, encKey, hqOn, eccm);
    public void UpdateStreamParams(string streamId, AudioParams newParams)
        => _inner.UpdateStreamParams(streamId, newParams);
    public void ClearStreamBuffer(string streamId) => _inner.ClearStreamBuffer(streamId);
    public Task StopStream(string streamId) => _inner.StopStream(streamId);
    public List<string> GetActiveStreams() => _inner.GetActiveStreams();
    public bool IsStreamActive(string id) => _inner.IsStreamActive(id);

    public void StartRecording(string filePath) => _inner.StartRecording(filePath);
    public void StopRecording() => _inner.StopRecording();
    public void StartMonitor(int deviceIndex) => _inner.StartMonitor(deviceIndex);
    public void StopMonitor() => _inner.StopMonitor();
    public void StartHudTapeFeed() => _inner.StartHudTapeFeed();
    public void StopHudTapeFeed() => _inner.StopHudTapeFeed();
    public void ClearSidetone() => _inner.ClearSidetone();

    public void PushSidetone(ReadOnlySpan<float> samples) => _inner.PushSidetone(samples);
    public void PushOwnVoiceForRecording(ReadOnlySpan<float> samples) => _inner.PushOwnVoiceForRecording(samples);
    public bool HotMicEnabled { get => _inner.HotMicEnabled; set => _inner.HotMicEnabled = value; }
    public bool WantsHotMic => _inner.WantsHotMic;
    public void PushHotMicForFeed(ReadOnlySpan<float> samples) => _inner.PushHotMicForFeed(samples);
}

/// <summary>
/// Creates <see cref="IPlaybackService"/> instances. Default implementation constructs the real
/// BASS-backed <see cref="RadioPlayback"/>; tests substitute a fake factory.
/// </summary>
public interface IPlaybackServiceFactory
{
    IPlaybackService Create(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory, int deviceIndex);
}

/// <summary>Default factory producing the real BASS playback service.</summary>
public sealed class RadioPlaybackServiceFactory : IPlaybackServiceFactory
{
    public IPlaybackService Create(Microsoft.Extensions.Logging.ILoggerFactory loggerFactory, int deviceIndex)
        => new RadioPlaybackAdapter(new RadioPlayback(loggerFactory, deviceIndex));
}
