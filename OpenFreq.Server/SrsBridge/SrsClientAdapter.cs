using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Concentus.Enums;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Translation layer for one connected SRS client. Owns the raw TCP connection (SRS's
/// newline-delimited JSON sync protocol) and a "shadow" <see cref="IRtcClient"/> -- a real,
/// in-process loopback OpenFreq client connection, driven by SRS's sync messages instead of a
/// human/UI. This is the architectural core of the bridge: the shadow client really does
/// authenticate and join/leave frequencies through the normal OpenFreq signaling path, so
/// <c>ClientSession</c>/<c>FrequencyChannelManager</c>/<c>AudioStreamServer</c> need no SRS-aware
/// changes at all -- see docs/SRS_BRIDGE.md.
/// </summary>
public sealed class SrsClientAdapter : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly SrsBridgeServer _bridge;
    private readonly ServerConfig _config;
    private readonly IRtcClientFactory _rtcClientFactory;
    private readonly ILogger<SrsClientAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private NetworkStream? _stream;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private IRtcClient? _shadowClient;
    private readonly HashSet<int> _joinedFrequenciesKhz = new();

    // Reasonable default absent real telemetry -- matches OpenFreqService's own default TX power
    // (see OpenFreq.Client's ISignalCalculator) so an SRS-originated transmission isn't treated
    // as artificially weak/strong relative to a normal OpenFreq client's.
    private const double DefaultTxPowerWatts = 10.0;

    // Opus is a self-describing bitstream (frame size/bandwidth are in the packet itself), so
    // these persistent per-client codec instances just need matching sample rates on each side --
    // they don't need to negotiate SRS's own internal frame cadence (40ms) vs OpenFreq's (20ms).
    //
    // _incomingDecoder is NOT readonly: it gets replaced with a fresh instance at the start of
    // each new SRS talk-spurt (see NoteVoiceActivity). Opus decoders carry predictive internal
    // state (pitch/LPC history) across calls for quality during a continuous stream, but that
    // same state goes stale across a silence gap between separate transmissions -- decoding a
    // new talk-spurt's first frames against leftover context from the end of the previous one
    // produces audible artifacts right from the start, which is exactly the "second transmission
    // sounds bad from the first word" symptom (as opposed to a gradually-building problem, which
    // would point at something else). A fresh decoder has no such stale context.
#pragma warning disable CS0618 // constructor is obsolete on Linux per Concentus's own factory note; matches RTPAudioSender's existing usage
    private OpusDecoder _incomingDecoder = new(OpenFreqRtcClient.SAMPLE_RATE, 1);
    private readonly OpusEncoder _outgoingEncoder;
#pragma warning restore CS0618
    private ulong _outgoingPacketNumber;

    // Tracks SRS's own PacketNumber sequence purely to drop stale/duplicate/out-of-order packets
    // (decoding them out of sequence would corrupt the Opus decoder's predictive state). NOT used
    // for proactive loss-concealment injection -- SRS likely uses Opus DTX (skips sending during
    // natural speech pauses), so a PacketNumber gap doesn't reliably mean "lost packet, please
    // conceal" versus "deliberate silence, do nothing", and guessing wrong means synthesizing
    // audible artifacts into what should just be quiet.
    private ulong? _lastIncomingPacketNumber;
    private byte? _lastLoggedIncomingEncryption;

    // SRS has no explicit PTT start/stop signaling on the wire -- a talk spurt is inferred from
    // voice packets actually arriving on a frequency, and considered over once none have arrived
    // for TransmissionTimeout. Drives IRtcClient.Start/StopTransmissionAsync so real OpenFreq
    // clients' peer-transmitting UI indicator lights up for a bridged SRS transmission (SendAudio
    // alone only moves audio bytes -- it never signals "someone is transmitting").
    private static readonly TimeSpan TransmissionTimeout = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<int, DateTime> _lastVoiceAtUtcByFreq = new();
    private readonly HashSet<int> _transmittingFrequencies = new();
    private readonly Lock _transmissionStateLock = new();
    private Timer? _transmissionTimeoutTimer;

    // Drives the new-talk-spurt reset decision in HandleIncomingVoiceAsync directly from elapsed
    // time, independent of _transmittingFrequencies' timer-polled membership (see that method's
    // comment for why the two must not be conflated).
    private DateTime? _lastAnyIncomingVoiceUtc;

    // Real SRS clients have been observed taking up to ~30s after TCP registration before sending
    // their first UDP packet (ping or voice) to us -- until then VoipEndPoint is unknown and any
    // OpenFreq->SRS audio has nowhere to go. Rather than silently dropping everything spoken
    // during that window (which sounded like a "glitch" -- entire sentences vanishing), every
    // outbound packet goes through this single queue, drained by one dedicated background task in
    // strict FIFO order. That single-consumer design is deliberate, not incidental: an earlier
    // version sent live packets immediately once the endpoint was known while separately
    // fire-and-forgetting the flush of older buffered ones, and those two independent sends could
    // interleave on the wire -- fresh audio arriving before older buffered audio finished sending,
    // which is exactly what an "echoey"/garbled OpenFreq->SRS symptom looks like. Routing
    // everything through one ordered queue makes that race structurally impossible.
    private readonly Queue<byte[]> _outboundQueue = new();
    private const int MaxPendingOutboundPackets = 150; // ~3s at a 20ms cadence
    private readonly Lock _outboundLock = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);
    private IPEndPoint? _voipEndPoint;
    private Task? _outboundSenderTask;
    private CancellationTokenSource? _outboundCts;

    /// <summary>Learned from this SRS client's own UDP traffic (ping or voice packets) -- where
    /// to send audio destined for it. Null until its first UDP packet arrives.</summary>
    public IPEndPoint? VoipEndPoint
    {
        get { lock (_outboundLock) return _voipEndPoint; }
        set
        {
            lock (_outboundLock) { _voipEndPoint = value; }
            if (value != null)
                _outboundSignal.Release(); // wake the sender loop in case it was waiting on an endpoint
        }
    }

    /// <summary>Enqueues a packet for the single ordered sender loop (see _outboundQueue's doc
    /// comment for why this must never send directly).</summary>
    private void EnqueueOutbound(byte[] packetBytes)
    {
        lock (_outboundLock)
        {
            if (_outboundQueue.Count >= MaxPendingOutboundPackets)
                _outboundQueue.Dequeue(); // drop oldest -- bounded backlog
            _outboundQueue.Enqueue(packetBytes);
        }

        _outboundSignal.Release();
    }

    /// <summary>The only place that actually calls SendVoiceAsync for this client -- drains
    /// _outboundQueue strictly in order, waiting whenever the endpoint isn't known yet or the
    /// queue is empty.</summary>
    private async Task OutboundSenderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _outboundSignal.WaitAsync(ct);

                while (true)
                {
                    byte[]? next;
                    IPEndPoint? endpoint;
                    lock (_outboundLock)
                    {
                        endpoint = _voipEndPoint;
                        next = endpoint != null && _outboundQueue.Count > 0 ? _outboundQueue.Dequeue() : null;
                    }

                    if (next == null) break; // nothing sendable right now -- wait for the next signal
                    await _bridge.SendVoiceAsync(next, endpoint!);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Null until the first message with a Client payload arrives (SRS registers
    /// whichever GUID shows up first, not only on SYNC -- mirrors real ServerSync).</summary>
    public string? ClientGuid { get; private set; }

    public SrsClient? LastKnownState { get; private set; }

    private static readonly Action<ILogger, string, string, Exception?> LogSrsClientRegistered =
        LoggerMessage.Define<string, string>(
            LogLevel.Information, new EventId(1, nameof(HandleMessageAsync)),
            "SRS client {Name} ({Guid}) registered");

    private static readonly Action<ILogger, string, string, int, Exception?> LogSrsClientDisconnected =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information, new EventId(2, nameof(RunAsync)),
            "SRS client {Name} ({Guid}) disconnected ({JoinedFrequencies} frequency(ies) left)");

    public SrsClientAdapter(TcpClient tcpClient, SrsBridgeServer bridge, ServerConfig config,
        IRtcClientFactory rtcClientFactory, ILoggerFactory loggerFactory)
    {
        _tcpClient = tcpClient;
        _bridge = bridge;
        _config = config;
        _rtcClientFactory = rtcClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SrsClientAdapter>();

        // Same voice-tuned settings as RTPAudioSender's own Opus encoder (used for the identical
        // purpose: real-time voice over an unreliable link) -- Concentus's untouched constructor
        // defaults are meant for generic use, not specifically tuned for this.
#pragma warning disable CS0618
        _outgoingEncoder = new OpusEncoder(OpenFreqRtcClient.SAMPLE_RATE, 1, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = 24000,
            Complexity = 8,
            SignalType = OpusSignal.OPUS_SIGNAL_VOICE,
            UseInbandFEC = true,
            PacketLossPercent = 15
        };
#pragma warning restore CS0618
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _stream = _tcpClient.GetStream();
            _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            using var reader = new StreamReader(_stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break; // remote closed
                if (string.IsNullOrWhiteSpace(line)) continue;

                await HandleLineAsync(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down or connection torn down deliberately.
        }
        catch (IOException)
        {
            // TCP reset / network loss -- normal disconnect path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SRS client connection error ({Guid})", ClientGuid ?? "unregistered");
        }
        finally
        {
            if (ClientGuid != null)
                LogSrsClientDisconnected(_logger, LastKnownState?.Name ?? "Unnamed", ClientGuid,
                    _joinedFrequenciesKhz.Count, null);
            await DisposeAsync();
        }
    }

    private async Task HandleLineAsync(string line)
    {
        SrsNetworkMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(line, SrsJsonContext.Default.SrsNetworkMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Malformed SRS message, ignoring: {Line}", line);
            return;
        }

        if (message == null) return;

        await HandleMessageAsync(message);
    }

    private async Task HandleMessageAsync(SrsNetworkMessage message)
    {
        // Real SRS registers whichever ClientGuid shows up first, on ANY message type (except the
        // gateway-only ones this bridge doesn't support) -- see ServerSync.HandleConnectedClient.
        if (ClientGuid == null && message.Client is { ClientGuid.Length: > 0 } incomingClient)
        {
            if (!await TryRegisterAsync(incomingClient, message.Version))
                return; // version rejected/malformed -- connection already being torn down
        }

        switch (message.MsgType)
        {
            case SrsNetworkMessage.MessageType.SYNC:
                await HandleSyncAsync(message);
                break;
            case SrsNetworkMessage.MessageType.UPDATE:
            case SrsNetworkMessage.MessageType.RADIO_UPDATE:
                await HandleClientUpdateAsync(message);
                break;
            case SrsNetworkMessage.MessageType.PING:
                break; // keepalive only, matches real server's no-op
            case SrsNetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD:
                await HandleExternalAwacsPasswordAsync(message);
                break;
            case SrsNetworkMessage.MessageType.EXTERNAL_AWACS_MODE_DISCONNECT:
                await HandleExternalAwacsDisconnectAsync();
                break;
            default:
                _logger.LogDebug("Unhandled SRS message type {Type} from {Guid}", message.MsgType, ClientGuid);
                break;
        }
    }

    private async Task<bool> TryRegisterAsync(SrsClient incomingClient, string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            _logger.LogWarning("Disconnecting unversioned SRS client from {Endpoint}",
                _tcpClient.Client.RemoteEndPoint);
            await CloseAsync();
            return false;
        }

        if (Version.TryParse(version, out var clientVersion) &&
            Version.TryParse(_config.SrsBridge.MinimumClientVersion, out var minVersion) &&
            clientVersion < minVersion)
        {
            _logger.LogWarning(
                "Disconnecting unsupported SRS client version {ClientVersion} (minimum {MinVersion})",
                clientVersion, minVersion);
            await SendAsync(new SrsNetworkMessage { MsgType = SrsNetworkMessage.MessageType.VERSION_MISMATCH });
            await CloseAsync();
            return false;
        }

        ClientGuid = incomingClient.ClientGuid;
        LastKnownState = incomingClient;

        _shadowClient = _rtcClientFactory.Create(_loggerFactory, $"127.0.0.1:{_config.WebSocketPort}",
            _config.ServerPassword ?? "", DisplayNameFor(incomingClient));

        try
        {
            await _shadowClient.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shadow OpenFreq connection failed for SRS client {Guid}", ClientGuid);
            await CloseAsync();
            return false;
        }

        // OpenFreq peers' transmissions arrive here already Opus-decoded to PCM -- this is the
        // entire OpenFreq->SRS leg (re-encode + SRS packet framing, no DSP applied yet; see
        // docs/SRS_BRIDGE.md phase 3 for where server-side degradation/encryption/SATCOM effects
        // get layered in before this reaches a real SRS ear).
        _shadowClient.AudioDataReceived += OnShadowAudioReceived;

        _transmissionTimeoutTimer = new Timer(_ => CheckTransmissionTimeouts(), null, TransmissionTimeout, TransmissionTimeout);

        _outboundCts = new CancellationTokenSource();
        _outboundSenderTask = Task.Run(() => OutboundSenderLoopAsync(_outboundCts.Token));

        // Excludes this SRS player's own shadow session from the fake-OpenFreq-roster entries
        // synthesized for SRS clients -- otherwise every bridged SRS player would see a ghost
        // "OpenFreq" copy of themselves.
        if (_shadowClient.MyPeerId != null)
            _bridge.RegisterShadowPeer(_shadowClient.MyPeerId);

        _bridge.RegisterClient(ClientGuid, this);
        LogSrsClientRegistered(_logger, incomingClient.Name, ClientGuid, null);
        return true;
    }

    private static string DisplayNameFor(SrsClient client) =>
        string.IsNullOrWhiteSpace(client.Name) ? "SRS Player" : $"{client.Name} (SRS)";

    private async Task HandleSyncAsync(SrsNetworkMessage message)
    {
        if (ClientGuid == null || message.Client == null) return;

        await ApplyRadioDiffAsync(message.Client);
        MergeIntoLastKnownState(message.Client);

        // Reply to this session only: full current roster + minimal server settings.
        var reply = new SrsNetworkMessage
        {
            MsgType = SrsNetworkMessage.MessageType.SYNC,
            Clients = _bridge.GetRosterSnapshot(),
            ServerSettings = SrsServerSettings.Build(_config.SrsBridge),
            Version = _config.SrsBridge.ReportedSrsVersion
        };
        await SendAsync(reply);

        // Tell the other SRS peers this client (and its radios) now exist.
        await _bridge.MulticastAsync(new SrsNetworkMessage
        {
            MsgType = SrsNetworkMessage.MessageType.RADIO_UPDATE,
            Client = LastKnownState
        }, excludeGuid: ClientGuid);
    }

    private async Task HandleClientUpdateAsync(SrsNetworkMessage message)
    {
        if (ClientGuid == null || message.Client == null) return;

        await ApplyRadioDiffAsync(message.Client);
        MergeIntoLastKnownState(message.Client);

        await _bridge.MulticastAsync(new SrsNetworkMessage
        {
            MsgType = message.MsgType,
            Client = LastKnownState
        }, excludeGuid: ClientGuid);
    }

    /// <summary>Updates <see cref="LastKnownState"/> from an incoming SYNC/UPDATE/RADIO_UPDATE
    /// message, preserving the previously known RadioInfo when this message doesn't carry one.
    /// Real SRS clients send plain UPDATE messages (coalition/position/metadata changes) with
    /// RadioInfo left null -- it's only populated on RADIO_UPDATE, when radios actually changed.
    /// Overwriting LastKnownState wholesale on every message was clobbering this client's radio
    /// state with null on every intervening UPDATE, which fed straight into
    /// ApplyRadioDiffAsync as "no radios tuned" (dropping every joined frequency) and, separately,
    /// into the roster snapshot broadcast to other SRS clients (making their radios flicker away
    /// too) until the next RADIO_UPDATE arrived a few seconds later.</summary>
    private void MergeIntoLastKnownState(SrsClient client)
    {
        if (client.RadioInfo == null && LastKnownState != null)
            client.RadioInfo = LastKnownState.RadioInfo;

        LastKnownState = client;
    }

    /// <summary>External AWACS Mode login: assigns a coalition purely from a password match, no
    /// live DCS export required -- lets an SRS client (or a tester) connect and pick radios
    /// manually. Mirrors real ServerSync.HandleExternalAWACSModePassword.</summary>
    private async Task HandleExternalAwacsPasswordAsync(SrsNetworkMessage message)
    {
        if (ClientGuid == null || message.Client == null) return;

        var eam = _config.SrsBridge;
        var coalition = 0;
        if (eam.ExternalAwacsModeEnabled && !string.IsNullOrWhiteSpace(message.ExternalAWACSModePassword))
        {
            if (message.ExternalAWACSModePassword == eam.ExternalAwacsModeBluePassword) coalition = 2;
            else if (message.ExternalAWACSModePassword == eam.ExternalAwacsModeRedPassword) coalition = 1;
        }

        if (LastKnownState != null)
        {
            LastKnownState.Coalition = coalition;
            LastKnownState.Name = message.Client.Name;
        }

        _logger.LogInformation("SRS client {Guid} EAM login: coalition={Coalition} (0=failed/disabled)",
            ClientGuid, coalition);

        // Coalition 0 signals failed auth (or EAM disabled) back to the client on this session only.
        await SendAsync(new SrsNetworkMessage
        {
            MsgType = SrsNetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD,
            Client = new SrsClient { Coalition = coalition }
        });

        await _bridge.MulticastAsync(new SrsNetworkMessage
        {
            MsgType = SrsNetworkMessage.MessageType.UPDATE,
            Client = LastKnownState
        }, excludeGuid: ClientGuid);
    }

    /// <summary>Voluntary EAM logout (stays connected, drops back to spectator) -- distinct from a
    /// full TCP disconnect. Mirrors real ServerSync.HandleExternalAWACSModeDisconnect.</summary>
    private async Task HandleExternalAwacsDisconnectAsync()
    {
        if (ClientGuid == null || LastKnownState == null) return;

        LastKnownState.Coalition = 0;
        LastKnownState.Name = "";

        await _bridge.MulticastAsync(new SrsNetworkMessage
        {
            MsgType = SrsNetworkMessage.MessageType.RADIO_UPDATE,
            Client = LastKnownState
        }, excludeGuid: ClientGuid);
    }

    /// <summary>Joins/leaves the shadow client's frequencies to match this SRS client's currently
    /// tuned radios. Intercom-modulation slots never map to a real OpenFreq channel.</summary>
    private async Task ApplyRadioDiffAsync(SrsClient client)
    {
        if (_shadowClient == null) return;

        // The shadow client can be transiently unauthenticated mid-reconnect (its underlying
        // OpenFreq WebSocket dropped and is being re-established -- see OpenFreqRtcClient's own
        // reconnect logic). JoinFrequencyAsync/LeaveFrequencyAsync throw in that state; letting
        // that propagate would hit RunAsync's catch-all and tear down this entire SRS client's
        // TCP session over what is normally a few-second hiccup. Skip this round instead --
        // OpenFreqRtcClient's own ReconnectAsync re-joins whatever it had joined before the drop
        // from its own internal state once it's back, and the next SRS update after that will
        // pick up any diff since (radios don't change while the pilot isn't touching them).
        if (!_shadowClient.IsAuthenticated)
        {
            _logger.LogDebug("Shadow client for {Guid} not authenticated yet, skipping radio diff", ClientGuid);
            return;
        }

        // A null RadioInfo means this specific message didn't carry radio state at all (real SRS
        // sends plain UPDATE messages for non-radio changes -- coalition, position, etc. -- with
        // RadioInfo left unset). That is NOT the same as "all radios untuned": treating it as such
        // was leaving every joined frequency on every such message, only to rejoin them all again
        // once the next RADIO_UPDATE arrived seconds later. Nothing to diff here -- leave
        // _joinedFrequenciesKhz exactly as it is.
        if (client.RadioInfo == null) return;

        var wantedKhz = new HashSet<int>();
        foreach (var radio in client.RadioInfo.Radios)
        {
            if (!radio.IsTuned || radio.Modulation == SrsModulation.INTERCOM) continue;
            wantedKhz.Add((int)Math.Round(radio.Freq / 1000.0));
        }

        foreach (var khz in wantedKhz.Except(_joinedFrequenciesKhz).ToList())
        {
            await _shadowClient.JoinFrequencyAsync(khz);
            _joinedFrequenciesKhz.Add(khz);
        }

        foreach (var khz in _joinedFrequenciesKhz.Except(wantedKhz).ToList())
        {
            await _shadowClient.LeaveFrequencyAsync(khz);
            _joinedFrequenciesKhz.Remove(khz);
        }
    }

    /// <summary>SRS->OpenFreq leg: decode this client's incoming Opus voice payload and hand the
    /// PCM to the shadow client, which re-encodes and relays it through the normal OpenFreq audio
    /// path (AudioStreamServer) -- every OpenFreq listener on a matching frequency receives it and
    /// applies its own local DSP exactly as if a real OpenFreq client had transmitted.</summary>
    public Task HandleIncomingVoiceAsync(SrsVoicePacket packet)
    {
        if (_shadowClient == null || packet.AudioPart1.Length == 0) return Task.CompletedTask;

        if (_lastIncomingPacketNumber is { } last && packet.PacketNumber <= last)
        {
            // Stale/duplicate/out-of-order -- decoding it now would feed the predictive
            // decoder frames out of sequence and corrupt its state worse than dropping it.
            _logger.LogDebug("Dropping out-of-order SRS voice packet from {Guid} (got {Got}, last {Last})",
                ClientGuid, packet.PacketNumber, last);
            return Task.CompletedTask;
        }

        _lastIncomingPacketNumber = packet.PacketNumber;

        // Whether this packet starts a new talk-spurt is decided HERE, synchronously, from a
        // single timestamp -- deliberately NOT derived from _transmittingFrequencies (which is
        // only shrunk by CheckTransmissionTimeouts, a background timer polling every
        // TransmissionTimeout). That timer's cadence is unsynchronized with when the user
        // actually stops talking: if a new transmission's first packet arrives before the timer
        // has caught up and removed the frequency from the set, a set-membership-based check
        // would wrongly conclude "still mid-transmission" and skip resetting state entirely --
        // silently, for that one attempt, then resolve itself once CheckTransmissionTimeouts
        // finally runs. That is exactly the intermittent "works twice, then the next one doesn't" pattern
        // this was tracked down from. A direct elapsed-time check has no such race.
        var now = DateTime.UtcNow;
        var isNewTalkSpurt = _lastAnyIncomingVoiceUtc == null || now - _lastAnyIncomingVoiceUtc.Value > TransmissionTimeout;
        _lastAnyIncomingVoiceUtc = now;

        if (isNewTalkSpurt)
        {
            // Clear whatever's still queued from the previous one (see
            // IRtcClient.MarkTransmitStartTime's own doc comment: "clear any stale samples ...
            // ensure packet timestamps reflect the gap"). Without this, each new SRS
            // transmission's audio queues up behind leftover backlog from the last one instead
            // of starting fresh -- and since the sender's per-call frequency metadata is a single
            // shared field, not attached to the specific samples it described, backlogged audio
            // that finally drains later gets tagged with whatever frequency is CURRENT at that
            // later point, not the one it was actually spoken on (observed as old audio from one
            // radio bleeding into whatever radio is transmitted on next).
            _shadowClient.MarkTransmitStartTime();

            // Fresh decoder for the new talk-spurt -- see _incomingDecoder's doc comment for why
            // reusing the previous one produces artifacts specifically at the start of each new
            // transmission (stale predictive state left over from the end of the last one).
#pragma warning disable CS0618
            var oldDecoder = _incomingDecoder;
            _incomingDecoder = new OpusDecoder(OpenFreqRtcClient.SAMPLE_RATE, 1);
#pragma warning restore CS0618
            oldDecoder.Dispose();
        }

        NoteVoiceActivity(packet.Frequencies, packet.Modulations, now);
        DecodeAndForward(packet.AudioPart1, packet.Frequencies, packet.Modulations, packet.Encryptions);
        return Task.CompletedTask;
    }

    /// <summary>Marks each non-intercom/disabled frequency in this packet as actively transmitting,
    /// starting an OpenFreq-side transmission signal (peer UI indicator) the first time a frequency
    /// goes from quiet to active. CheckTransmissionTimeouts ends it once packets stop arriving.
    /// Purely for the UI indicator now -- see HandleIncomingVoiceAsync for the actual new-talk-spurt
    /// reset decision, which no longer depends on this set's timer-polled membership.</summary>
    private void NoteVoiceActivity(double[] freqHz, byte[] modulations, DateTime now)
    {
        if (_shadowClient == null) return;

        List<int>? newlyTransmitting = null;

        lock (_transmissionStateLock)
        {
            for (var i = 0; i < freqHz.Length; i++)
            {
                var modulation = (SrsModulation)modulations[i];
                if (modulation is SrsModulation.INTERCOM or SrsModulation.DISABLED) continue;

                var khz = (int)Math.Round(freqHz[i] / 1000.0);
                _lastVoiceAtUtcByFreq[khz] = now;

                if (_transmittingFrequencies.Add(khz))
                    (newlyTransmitting ??= []).Add(khz);
            }
        }

        if (newlyTransmitting == null) return;

        foreach (var khz in newlyTransmitting)
            _ = _shadowClient.StartTransmissionAsync(khz, is3d: false);
    }

    /// <summary>Runs on a periodic timer: any frequency with no voice packets for
    /// TransmissionTimeout is considered to have stopped transmitting (SRS has no explicit PTT
    /// release message on the wire).</summary>
    private void CheckTransmissionTimeouts()
    {
        if (_shadowClient == null) return;

        var cutoff = DateTime.UtcNow - TransmissionTimeout;
        List<int>? stopped = null;

        lock (_transmissionStateLock)
        {
            foreach (var khz in _transmittingFrequencies.ToList())
            {
                if (_lastVoiceAtUtcByFreq.TryGetValue(khz, out var lastAt) && lastAt > cutoff) continue;
                _transmittingFrequencies.Remove(khz);
                (stopped ??= []).Add(khz);
            }
        }

        if (stopped == null) return;
        foreach (var khz in stopped)
            _ = _shadowClient.StopTransmissionAsync(khz, is3d: false);
    }

    /// <summary>Decodes one Opus payload and forwards the PCM to the shadow client on the given
    /// frequencies.</summary>
    private void DecodeAndForward(ReadOnlySpan<byte> opusPayload, double[] freqHz, byte[] modulations,
        byte[] encryptions)
    {
        if (_shadowClient == null) return;

        // 120ms at 48kHz is Opus's largest standard frame -- always enough headroom regardless of
        // what frame duration the sending SRS client chose (self-describing per packet).
        var pcm = new short[OpenFreqRtcClient.SAMPLE_RATE / 1000 * 120];
        int decoded;
        try
        {
            decoded = _incomingDecoder.Decode(opusPayload, pcm, pcm.Length, false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to decode SRS voice payload from {Guid}", ClientGuid);
            return;
        }

        if (decoded <= 0)
        {
            _logger.LogDebug("SRS voice payload from {Guid} decoded to 0 samples, dropping", ClientGuid);
            return;
        }

        var frequencies = new List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position,
            Vector3? velocity, Vector3? dcsPosition, AmbientNoiseType ambientNoiseType,
            bool enc, int encKey, bool hqOn)>();

        for (var i = 0; i < freqHz.Length; i++)
        {
            var modulation = (SrsModulation)modulations[i];
            if (modulation == SrsModulation.INTERCOM || modulation == SrsModulation.DISABLED) continue;

            var encryption = encryptions[i];
            if (encryption != _lastLoggedIncomingEncryption)
            {
                _logger.LogInformation(
                    "SRS voice packet from {Guid}: raw encryption byte={Encryption} on {Khz} kHz (0=unencrypted)",
                    ClientGuid, encryption, (int)Math.Round(freqHz[i] / 1000.0));
                _lastLoggedIncomingEncryption = encryption;
            }

            frequencies.Add((
                frequencyKhz: (int)Math.Round(freqHz[i] / 1000.0),
                txPowerWatts: DefaultTxPowerWatts,
                ppm: 0.0,
                // geodetic->local-grid mapping not wired yet, see phase 4
                position: null, velocity: null, dcsPosition: null,
                ambientNoiseType: AmbientNoiseType.None,
                enc: encryption != 0, encKey: encryption,
                hqOn: modulation == SrsModulation.HAVEQUICK));
        }

        if (frequencies.Count > 0)
        {
            _shadowClient.SendAudio(new Memory<short>(pcm, 0, decoded), frequencies, in3d: false);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "SRS voice payload from {Guid} carried {RawFrequencyCount} frequency(ies) but all were filtered out (intercom/disabled)",
                ClientGuid, freqHz.Length);
        }
    }

    /// <summary>Modulation to report on an outgoing SRS voice packet for one frequency. Real SRS
    /// clients independently check the incoming packet's modulation against their own tuned
    /// radio's configured modulation before playing it (mirroring the server's own
    /// CanHearTransmission logic) -- reporting the wrong one (e.g. a generic frequency-band guess
    /// that disagrees with how this specific aircraft's radio is actually configured) causes the
    /// client to silently reject audio it otherwise received fine, which looks exactly like "sent
    /// but never heard". Prefer this SRS client's own reported radio modulation for that
    /// frequency; only fall back to the frequency-band heuristic if it has no matching radio
    /// (e.g. hasn't sent a RADIO_UPDATE yet).</summary>
    private SrsModulation ResolveOutgoingModulation(int khz)
    {
        var radios = LastKnownState?.RadioInfo?.Radios;
        if (radios != null)
        {
            foreach (var radio in radios)
            {
                if (radio.IsTuned && (int)Math.Round(radio.Freq / 1000.0) == khz)
                    return radio.Modulation;
            }
        }

        return FastPathAudioSim.GetBandConfig(khz).Modulation == ModulationType.FM
            ? SrsModulation.FM
            : SrsModulation.AM;
    }

    // One SrsAudioProcessor per source OpenFreq peer, kept for the life of that pairing so its
    // RadioEffect fading timers and KY-58 phase machine carry continuous state across packets
    // (see SrsAudioProcessor's own doc comment). Keyed by peer id, same as _outgoingTransmissions.
    private readonly Dictionary<string, SrsAudioProcessor> _audioProcessorsByPeer = new();
    private readonly Lock _audioProcessorsLock = new();

    private SrsAudioProcessor GetOrCreateAudioProcessor(string peerId)
    {
        lock (_audioProcessorsLock)
        {
            if (_audioProcessorsByPeer.TryGetValue(peerId, out var existing)) return existing;
            var created = new SrsAudioProcessor(_logger);
            _audioProcessorsByPeer[peerId] = created;
            return created;
        }
    }

    /// <summary>This SRS receiver's own enc/encKey for whichever of its radios is tuned to
    /// <paramref name="khz"/> -- i.e. the "slot" side of the KY-58 match, mirroring
    /// ResolveOutgoingModulation's same radio lookup. No matching radio (hasn't sent a
    /// RADIO_UPDATE yet) is treated as an unencrypted, non-crypto-capable receiver.</summary>
    private (bool Enc, int EncKey) ResolveSlotEncryption(int khz)
    {
        var radios = LastKnownState?.RadioInfo?.Radios;
        if (radios != null)
        {
            foreach (var radio in radios)
            {
                if (radio.IsTuned && (int)Math.Round(radio.Freq / 1000.0) == khz)
                    return (radio.Enc, radio.EncKey);
            }
        }

        return (false, 0);
    }

    // Per-source-peer TransmissionGuid stability. SRS's own doc comment on this field calls it
    // "used for transmission relay" -- i.e. it identifies one continuous PTT transmission, not
    // one packet. A fresh random GUID on every single 20ms packet (the original version of this
    // code) tells the receiving SRS client each packet is the start of a brand-new stream, which
    // is exactly the kind of thing that would make it discard/reset per-packet decode context --
    // producing a persistent unclear/warbly "echoey" quality throughout, not just at boundaries.
    // Reuses the same silence-timeout talk-spurt-boundary approach as the SRS->OpenFreq leg
    // (NoteVoiceActivity/CheckTransmissionTimeouts) for consistency.
    private readonly Dictionary<string, (string TransmissionGuid, DateTime LastPacketUtc)> _outgoingTransmissions = new();
    private readonly Lock _outgoingTransmissionsLock = new();

    private string ResolveOutgoingTransmissionGuid(string peerId, out bool isNewTalkSpurt)
    {
        var now = DateTime.UtcNow;
        lock (_outgoingTransmissionsLock)
        {
            if (_outgoingTransmissions.TryGetValue(peerId, out var existing) &&
                now - existing.LastPacketUtc <= TransmissionTimeout)
            {
                _outgoingTransmissions[peerId] = (existing.TransmissionGuid, now);
                isNewTalkSpurt = false;
                return existing.TransmissionGuid;
            }

            var fresh = SrsGuid.NewGuid();
            _outgoingTransmissions[peerId] = (fresh, now);
            isNewTalkSpurt = true;
            return fresh;
        }
    }

    /// <summary>OpenFreq->SRS leg: re-encode PCM from an OpenFreq peer's transmission into an SRS
    /// voice packet and send it to this SRS client's learned UDP endpoint.</summary>
    private void OnShadowAudioReceived(object? sender, AudioDataEventArgs e)
    {
        var srcFrequencies = e.Metadata.Frequencies;

        // Resolved once up front (not just at packet-build time below) so a new talk spurt can
        // reset the audio processor's COMSEC phase machine before DSP runs on this buffer -- see
        // SrsAudioProcessor.ResetForNewTalkSpurt's doc comment for why that reset is needed at all.
        var transmissionGuid = ResolveOutgoingTransmissionGuid(e.PeerId, out var isNewTalkSpurt);

        // DSP is keyed off the first frequency this transmission is on -- a real transmission is
        // essentially always on exactly one at a time (multi-frequency-simultaneous is an edge
        // case the underlying wire format allows but this pass doesn't special-case), matching how
        // the rest of this method already treats the PCM as one shared buffer for the whole packet.
        var processedPcm = e.AudioData.Span;
        if (srcFrequencies.Count > 0)
        {
            var primary = srcFrequencies[0];
            var (slotEnc, slotEncKey) = ResolveSlotEncryption(primary.Khz);
            var processor = GetOrCreateAudioProcessor(e.PeerId);
            if (isNewTalkSpurt) processor.ResetForNewTalkSpurt();
            processedPcm = processor.Process(e.AudioData.Span, slotEnc, slotEncKey, primary.Enc, primary.EncKey,
                primary.AmbientNoiseType);
        }

        var encoded = new byte[processedPcm.Length * 2];
        int encodedLength;
        try
        {
            encodedLength = _outgoingEncoder.Encode(processedPcm, processedPcm.Length, encoded, encoded.Length);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to encode outgoing SRS voice packet for {Guid}", ClientGuid);
            return;
        }

        if (encodedLength <= 0) return;

        var frequencies = new double[srcFrequencies.Count];
        var modulations = new byte[srcFrequencies.Count];
        var encryptions = new byte[srcFrequencies.Count];
        for (var i = 0; i < srcFrequencies.Count; i++)
        {
            var src = srcFrequencies[i];
            frequencies[i] = src.Khz * 1000.0;
            modulations[i] = (byte)ResolveOutgoingModulation(src.Khz);
            encryptions[i] = src.Enc ? (byte)src.EncKey : (byte)0;
        }

        var packet = new SrsVoicePacket
        {
            AudioPart1 = encoded[..encodedLength],
            Frequencies = frequencies,
            Modulations = modulations,
            Encryptions = encryptions,
            UnitId = 0,
            PacketNumber = _outgoingPacketNumber++,
            RetransmissionCount = 0,
            TransmissionGuid = transmissionGuid,
            ClientGuid = SrsGuid.FromOpenFreqId(e.PeerId)
        };

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Sending {Bytes}-byte SRS voice packet to {Guid} at {Endpoint} ({FreqCount} frequency(ies))",
                encodedLength, ClientGuid, VoipEndPoint, srcFrequencies.Count);

        EnqueueOutbound(packet.Encode());
    }

    private async Task SendAsync(SrsNetworkMessage message)
    {
        if (_writer == null) return;

        var json = JsonSerializer.Serialize(message, SrsJsonContext.Default.SrsNetworkMessage);

        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();
        }
        catch (IOException)
        {
            // Connection gone -- the read loop will notice and clean up.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Called by <see cref="SrsBridgeServer"/> to relay a message from another SRS
    /// client's session onto this one's wire.</summary>
    public Task DeliverAsync(SrsNetworkMessage message) => SendAsync(message);

    private async Task CloseAsync()
    {
        try { _tcpClient.Close(); }
        catch { /* already gone */ }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transmissionTimeoutTimer != null)
            await _transmissionTimeoutTimer.DisposeAsync();

        if (_outboundCts != null)
        {
            await _outboundCts.CancelAsync();
            _outboundSignal.Release(); // wake the loop so it observes cancellation promptly
            if (_outboundSenderTask != null)
            {
                try { await _outboundSenderTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* best-effort */ }
            }
            _outboundCts.Dispose();
        }

        _outboundSignal.Dispose();

        if (_shadowClient?.MyPeerId != null)
            _bridge.UnregisterShadowPeer(_shadowClient.MyPeerId);

        if (ClientGuid != null)
        {
            _bridge.UnregisterClient(ClientGuid);
            await _bridge.MulticastAsync(new SrsNetworkMessage
            {
                MsgType = SrsNetworkMessage.MessageType.CLIENT_DISCONNECT,
                Client = LastKnownState
            }, excludeGuid: ClientGuid);
        }

        if (_shadowClient != null)
        {
            try { await _shadowClient.DisconnectAsync(); }
            catch { /* best-effort */ }
            _shadowClient.Dispose();
        }

        _writer?.Dispose();
        _stream?.Dispose();
        _tcpClient.Dispose();
        _writeLock.Dispose();
        _incomingDecoder.Dispose();
        _outgoingEncoder.Dispose();
    }
}
