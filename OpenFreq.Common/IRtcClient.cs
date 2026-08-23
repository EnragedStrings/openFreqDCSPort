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

    string ServerIp { get; }
    string? MyPeerId { get; }
    bool IsConnected { get; }
    bool IsAuthenticated { get; }

    Task ConnectAsync(TimeSpan? connectTimeout = null);
    Task DisconnectAsync();
    Task JoinFrequencyAsync(int frequencyKhz);
    Task LeaveFrequencyAsync(int frequencyKhz);
    Task StartTransmissionAsync(int frequencyKhz);
    Task StopTransmissionAsync(int frequencyKhz);
    Task SendModeUpdateAsync();
    Task SetDisplayNameAsync(string displayName);

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
    IRtcClient Create(ILoggerFactory loggerFactory, string serverIp, string password, string? displayName);
}

/// <summary>Default factory producing the real network client.</summary>
public sealed class OpenFreqRtcClientFactory : IRtcClientFactory
{
    public IRtcClient Create(ILoggerFactory loggerFactory, string serverIp, string password, string? displayName)
        => new OpenFreqRtcClient(loggerFactory, serverIp, password, displayName);
}
