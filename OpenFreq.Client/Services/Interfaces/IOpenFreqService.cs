using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreqClient.Services.Interfaces;

/// <summary>
/// Service for managing OpenFreq server connection and audio transmission
/// </summary>
public interface IOpenFreqService : IDisposable
{
    // Connection state
    bool IsConnected { get; }
    bool IsAuthenticated { get; }
    string? PeerId { get; }
    int RecordingDeviceIndex { get; set; }
    int PlaybackDeviceIndex { get; set; }
    int AudioParamsUpdateFrequency { get; set; }
    bool SidetoneEnabled { get; set; }
    bool MicNormalizationEnabled { get; set; }

    /// <summary>Live mirror of OpenFreqSettings.ShareTranscripts -- see its own doc comment.
    /// Read by RecordProcedure to decide whether to buffer raw mic PCM for local speech-to-text.
    /// </summary>
    bool ShareTranscripts { get; set; }
    bool InputMeterEnabled { get; set; }
    double InputGain { get; set; }
    bool DcsLineOfSightEnabled { get; set; }
    double SidetoneVolume { get; set; }
    double MasterVolume { get; set; }
    double AmbientNoiseVolume { get; set; }

    /// <summary>Where the combined capture mix goes when recording.</summary>
    enum CaptureSink
    {
        /// <summary>Write to an Ogg/Vorbis file.</summary>
        File,
        /// <summary>Stream to a separate playback device (e.g. a virtual audio cable).</summary>
        Device
    }

    /// <summary>Selects the capture output: file or playback device.</summary>
    CaptureSink Sink { get; set; }
    /// <summary>BASS device index the capture mix is streamed to when <see cref="Sink"/> is Device.</summary>
    int MonitorDeviceIndex { get; set; }
    /// <summary>When true, capture auto-starts on entering game mode (flight) and auto-stops on leaving it.</summary>
    bool AutoRecordInGameMode { get; set; }
    /// <summary>When true, own voice in the capture gets the full radio FX (AGC/squelch/SFX); when false it stays clean.</summary>
    bool ApplyOwnVoiceSfx { get; set; }
    /// <summary>Directory recordings are written to. Created if missing. Blank → "recordings" next to the executable.</summary>
    string RecordingPath { get; set; }
    /// <summary>True while a capture (file or device) is in progress.</summary>
    bool IsRecording { get; }
    /// <summary>Start a capture now (manual or auto) using the selected sink. No-op if already capturing or not initialized.</summary>
    void StartRecording();
    /// <summary>Stop the current capture now. No-op if idle. Always honoured (manual override).</summary>
    void StopRecording();
    /// <summary>Raised when capture starts (true) or stops (false).</summary>
    event EventHandler<bool>? RecordingStateChanged;

    Mode OwnPositionMode { get; }

    // Events
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<string>? StatusMessageReceived;
    /// <summary>
    /// Fired when the audio playback subsystem (RadioPlayback/BASS) encounters a user-facing error
    /// such as device switch failure or playback loss. Message is human-readable, no stack trace.
    /// </summary>
    event EventHandler<string>? AudioPlaybackErrorOccurred;
    event EventHandler<MicLevelChangedEventArgs>? MicLevelChanged;
    event EventHandler<FrequencyConnectionStatusEventArgs>? FrequencyConnectionStatusChanged;
    event EventHandler<FrequencyTransmissionStatusEventArgs>? FrequencyTransmissionStatusChanged;

    /// <summary>Fired whenever an incoming transmission on a frequency is found to be blocked by
    /// this client's own terrestrial signal model (DCS terrain LOS, or resulting signal too weak
    /// -- see AudioParams.BlockedReason), and again with Reason=None once audio successfully gets
    /// through again -- lets the UI show WHY a channel showing "someone is transmitting" isn't
    /// actually audible, e.g. "LOS BLOCKED", instead of silently dropping the audio with no
    /// explanation. Frequency-level, same granularity as FrequencyTransmissionStatusChanged (not
    /// per-peer) -- if multiple peers share a frequency, this reflects whichever one's packet was
    /// last evaluated.</summary>
    event EventHandler<SignalBlockedStatusEventArgs>? SignalBlockedStatusChanged;

    public event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    public event EventHandler<PeerEventArgs>? PeerJoined;
    public event EventHandler<PeerEventArgs>? PeerLeft;
    public event EventHandler<AllPeersStatusEventArgs>? AllPeersStatusChanged;
    event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;

    /// <summary>Fires whenever the set of frequencies (and who's really on them) the GCI "monitor
    /// all" scanner is currently silently observing changes -- see MonitorAllFrequenciesEnabled and
    /// StartScanningFrequencyAsync. Always a subset of what AllPeersStatusChanged reports, scoped to
    /// just the scanner's own ephemeral joins.</summary>
    event EventHandler<AllPeersStatusEventArgs>? ScannedTransmissionsChanged;

    /// <summary>Server-authoritative SATCOM link state for one (channel, net) session --
    /// satellite assignment, quality, DAMA state, and a precomputed frame-disposition batch. See
    /// docs/SATCOM_SIMULATION.md.</summary>
    event EventHandler<SatcomLinkStateEventArgs>? SatcomLinkStateReceived;

    /// <summary>Low-rate broadcast of the server's SATCOM satellite catalog positions, for
    /// client-side az/el display and the local terrain-LOS ray.</summary>
    event EventHandler<SatelliteEphemerisEventArgs>? SatelliteEphemerisReceived;

    // Methods
    Task Initialize(OpenFreqClient.Models.OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex);
    Task ConnectAsync(TimeSpan? connectTimeout = null);
    Task DisconnectAsync();
    bool IsFrequencyJoined(int frequencyKhz, Guid slotId);
    Task JoinFrequencyAsync(int frequencyKhz, Guid slotId, RadioStationData radioStationData);
    Task LeaveFrequencyAsync(int frequencyKhz, Guid slotId);

    /// <summary>GCI-only "monitor all frequencies" scanner: when true, every frequency with real
    /// (non-observer) activity server-wide that the caller hasn't manually joined itself is
    /// automatically, silently joined and played (through the normal encryption simulation --
    /// KY-58 noise for anything encrypted, clear otherwise) as a scanner feed. See
    /// ScannedTransmissionsChanged.</summary>
    bool MonitorAllFrequenciesEnabled { get; set; }

    /// <summary>Silently (observer) joins one frequency for the scanner. Returns Guid.Empty if
    /// already tuned via any slot. Exposed mainly for tests -- normal use is via
    /// MonitorAllFrequenciesEnabled's automatic reconciliation.</summary>
    Task<Guid> StartScanningFrequencyAsync(int frequencyKhz);
    Task StopScanningFrequencyAsync(int frequencyKhz);
    Task StartTransmissionAsync(int frequencyKhz, Guid slotId, List<int> mutedFrequencies);
    Task StopTransmissionAsync(int frequencyKhz);

    /// <summary>Like <see cref="StartTransmissionAsync"/>, but the mic buffer is replaced with a
    /// synthesized attention tone for as long as this is the only active transmission (e.g. the
    /// ARC-186 TONE switch). If a real voice transmission is active at the same time, that one
    /// wins for this shared audio cycle — see OpenFreqService.RecordProcedure.</summary>
    Task StartToneTransmissionAsync(int frequencyKhz, Guid slotId, List<int> mutedFrequencies);
    Task StopToneTransmissionAsync(int frequencyKhz);
    Task UpdateDisplayNameAsync(string newDisplayName);

    void SetVolume(int frequencyKhz, Guid slotId, float volumeValue);
    void SetPan(int frequencyKhz, Guid slotId, int pan);

    void EnableFrequency(int frequencyKhz, Guid slotId);
    void DisableFrequency(int frequencyKhz, Guid slotId);

    void SetSquelch(int frequencyKhz, Guid slotId, bool isSquelchClosed);

    /// <summary>Route a receiving slot through the SATCOM digital vocoder pipeline instead of the
    /// normal AM/FM path, or back to normal when <paramref name="isActive"/> is false. See
    /// OpenFreqAudio.RadioPlayback.SetSatcomState and docs/SATCOM_SIMULATION.md.</summary>
    void SetSatcomState(int frequencyKhz, Guid slotId, bool isActive, double frameErrorRate, double burstSeverity,
        double frameDurationSeconds = 0.0225, double propagationLatencySeconds = 0.0);

    /// <summary>Reports this client's SATCOM geometry/state for one net to the server. See
    /// docs/SATCOM_SIMULATION.md's server-authoritative architecture.</summary>
    Task SendSatcomGeometryUpdateAsync(SatcomGeometryUpdateMessage message);

    /// <summary>
    /// Configure KY-58/COMSEC encryption and HAVE QUICK state for a tuned radio slot. Applies to
    /// both what this slot transmits (attached to outgoing <see cref="OpenFreq.Common.FrequencyTransmission"/>)
    /// and how it receives (gates/decodes incoming transmissions per the TRANSEC/COMSEC layering).
    /// </summary>
    void SetEncryption(int frequencyKhz, Guid slotId, bool enc, int encKey, bool hqOn, bool cryptoCapable);

    public enum OpenFreqStatus
    {
        Connected,
        Disconnected,
        Authenticated,
        Connecting
    }

    public OpenFreqStatus Status { get; }

    public void LoadHeightmap(string path, int width = 32768, int height = 32768, int bytesPerSample = 2,
        double? cellSizeMeters = null);

    /// <summary>
    /// Samples terrain elevation (meters MSL) at BMS heightmap coordinates. Null if no heightmap loaded.
    /// </summary>
    public double? SampleTerrainElevationMeters(double xMeters, double yMeters);

    public void SetOwnPositionMode(Mode newMode);

    public enum Mode
    {
        GCI,
        BMS,
        DCS
    }
}

public class MicLevelChangedEventArgs(double rms, double peak) : EventArgs
{
    public double Rms { get; } = rms;
    public double Peak { get; } = peak;
}
