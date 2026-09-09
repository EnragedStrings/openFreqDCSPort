using System.Numerics;
using Microsoft.Extensions.Logging;
using OpenFreqAudio;

namespace OpenFreq.Common;

public class SatcomLinkStateEventArgs(SatcomLinkStateMessage message) : EventArgs
{
    public SatcomLinkStateMessage Message { get; } = message;
}

public class SatelliteEphemerisEventArgs(List<SatcomSatelliteInfoDto> satellites) : EventArgs
{
    public List<SatcomSatelliteInfoDto> Satellites { get; } = satellites;
}

public class DcsLosOracleRequestEventArgs(DcsLosOracleRequestMessage message) : EventArgs
{
    public DcsLosOracleRequestMessage Message { get; } = message;
}

/// <summary>A transcript this client was allowed to see -- see TranscriptDeliveryMessage's own doc
/// comment for the capability/LOS-gating contract that determines who receives one.</summary>
public class TranscriptReceivedEventArgs(TranscriptDeliveryMessage message) : EventArgs
{
    public TranscriptDeliveryMessage Message { get; } = message;
}

/// <summary>
/// Abstraction over <see cref="OpenFreqRtcClient"/> covering the surface used by the client
/// service layer. Exists so the network client can be replaced with a fake in tests — the real
/// client opens WebSocket + UDP sockets and cannot run in a unit-test host.
/// </summary>
public interface IRtcClient : IDisposable
{
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<AuthenticationEventArgs>? Authenticated;
    event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    event EventHandler<FrequencyLeftEventArgs>? FrequencyLeft;
    event EventHandler<PeerEventArgs>? PeerJoined;
    event EventHandler<PeerEventArgs>? PeerLeft;
    event EventHandler<TransmissionStateEventArgs>? TransmissionStateChanged;
    event EventHandler<PeerTransmissionEventArgs>? PeerTransmissionStateChanged;
    event EventHandler<AudioDataEventArgs>? AudioDataReceived;
    event EventHandler<AllPeersStatusEventArgs>? AllPeersStatusUpdateReceived;
    event EventHandler<ServerSettingsEventArgs>? ServerSettingsChanged;
    event EventHandler<ErrorEventArgs>? ErrorOccurred;
    event EventHandler<SatcomLinkStateEventArgs>? SatcomLinkStateReceived;
    event EventHandler<SatelliteEphemerisEventArgs>? SatelliteEphemerisReceived;

    /// <summary>The server is asking this client to referee a remote terrain-LOS check via its own
    /// live DCS instance -- see DcsLosOracleRequestMessage's own doc comment. Answer with
    /// SendDcsLosOracleResponseAsync, echoing the same RequestId.</summary>
    event EventHandler<DcsLosOracleRequestEventArgs>? DcsLosOracleRequestReceived;

    /// <summary>A transcript the server has relayed to this client -- only ever fires for a client
    /// that declared AuthenticateMessage.WantsTranscripts at connect. See
    /// TranscriptDeliveryMessage's own doc comment for the delivery/gating contract.</summary>
    event EventHandler<TranscriptReceivedEventArgs>? TranscriptReceived;

    string ServerIp { get; }
    string? MyPeerId { get; }
    bool IsConnected { get; }
    bool IsAuthenticated { get; }

    Task ConnectAsync(TimeSpan? connectTimeout = null);
    Task DisconnectAsync();

    /// <summary>Joins a frequency channel. <paramref name="lat"/>/<paramref name="lon"/>/
    /// <paramref name="alt"/> are optional -- see JoinChannelMessage's own doc comment; only
    /// meaningful to a client that also wants position-gated transcript delivery.
    /// <paramref name="isObserver"/> joins silently -- see JoinChannelMessage.IsObserver's own doc
    /// comment; used by a GCI "monitor all frequencies" scanner, never by a normal pilot join.</summary>
    Task JoinFrequencyAsync(int frequencyKhz, double? lat = null, double? lon = null, double? alt = null,
        bool isObserver = false);
    Task LeaveFrequencyAsync(int frequencyKhz);

    /// <summary>Starts transmitting on a frequency, minting and returning a new TransmissionId for
    /// this PTT session (see AudioTransmissionMessage.TransmissionId) -- callers that plan to later
    /// call SendTranscriptAsync for this session should hold onto the returned id.
    /// <paramref name="lat"/>/<paramref name="lon"/>/<paramref name="alt"/> are this transmitter's
    /// own position, if known -- see AudioTransmissionMessage's own doc comment.</summary>
    Task<Guid> StartTransmissionAsync(int frequencyKhz, double? lat = null, double? lon = null, double? alt = null);
    Task StopTransmissionAsync(int frequencyKhz);
    Task SendModeUpdateAsync();
    Task SetDisplayNameAsync(string displayName);

    /// <summary>Reports this client's own local speech-to-text result for one completed PTT
    /// session -- see TransmissionTranscriptMessage's own doc comment. transmissionId must be one
    /// previously returned by StartTransmissionAsync. words' StartSec/EndSec are relative to that
    /// transmission's own start (its key-down moment).</summary>
    Task SendTranscriptAsync(Guid transmissionId, List<TranscriptWordDto> words, string? language = null);

    /// <summary>Reports this client's SATCOM geometry/state for one net -- the server is
    /// authoritative for satellite assignment/link-budget/DAMA; see docs/SATCOM_SIMULATION.md.</summary>
    Task SendSatcomGeometryUpdateAsync(SatcomGeometryUpdateMessage message);

    /// <summary>Reports this DCS-mode client's rough position/theater, low-rate, independent of
    /// SATCOM state -- see DcsPresenceUpdateMessage's own doc comment.</summary>
    Task SendDcsPresenceUpdateAsync(DcsPresenceUpdateMessage message);

    /// <summary>Answers a DcsLosOracleRequestMessage previously raised via
    /// DcsLosOracleRequestReceived.</summary>
    Task SendDcsLosOracleResponseAsync(DcsLosOracleResponseMessage message);

    void MarkTransmitStartTime();

    void SendAudio(
        Memory<short> pcmData,
        List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity,
            Vector3? dcsPosition, AmbientNoiseType ambientNoiseType, bool enc, int encKey, bool hqOn,
            double? latitudeDeg, double? longitudeDeg, double? altitudeMeters)> frequencies);
}

/// <summary>
/// Creates <see cref="IRtcClient"/> instances. The default implementation
/// (<see cref="OpenFreqRtcClientFactory"/>) constructs the real <see cref="OpenFreqRtcClient"/>;
/// tests substitute a fake factory.
/// </summary>
public interface IRtcClientFactory
{
    IRtcClient Create(ILoggerFactory loggerFactory, string serverIp, string password, string? displayName,
        bool wantsTranscripts = false);
}

/// <summary>Default factory producing the real network client.</summary>
public sealed class OpenFreqRtcClientFactory : IRtcClientFactory
{
    public IRtcClient Create(ILoggerFactory loggerFactory, string serverIp, string password, string? displayName,
        bool wantsTranscripts = false)
        => new OpenFreqRtcClient(loggerFactory, serverIp, password, displayName, wantsTranscripts);
}
