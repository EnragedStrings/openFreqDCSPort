using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenFreqClient.Services.AutoConnect;

/// <summary>
/// Loopback-only UDP endpoint (127.0.0.1:34323) the DCS client hook talks to. Drops anything
/// from a non-loopback source, oversized, malformed, with the wrong version or without the
/// current token -- silently, so a wrong token gets no answer. Every valid request is acked to
/// the sender; a repeated (session, seq) is re-acked with the first answer and not processed
/// again (the hook re-sends until acked).
/// </summary>
public sealed class DcsAutoConnectEndpoint : IDisposable
{
    private const int SeenCapacity = 256;

    private readonly string _token;
    private readonly Func<AutoConnectRequest, AutoConnectAckState> _handler;
    private readonly ILogger _logger;
    private readonly int _port;
    private readonly Dictionary<string, AutoConnectAckState> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _droppedLogged;

    public DcsAutoConnectEndpoint(string token, Func<AutoConnectRequest, AutoConnectAckState> handler,
        ILogger logger, int port = AutoConnectProtocol.DefaultPort)
    {
        _token = token;
        _handler = handler;
        _logger = logger;
        _port = port;
    }

    public int BoundPort { get; private set; }

    public string? ListenerError { get; private set; }

    public bool Start()
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
            BoundPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _logger.LogInformation("DCS auto-connect endpoint listening on 127.0.0.1:{Port}", BoundPort);
            return true;
        }
        catch (Exception ex)
        {
            _udp?.Dispose();
            _udp = null;
            ListenerError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                ? $"DCS auto-connect is unavailable: UDP port {_port} is already in use by another program."
                : $"DCS auto-connect is unavailable: the listener on UDP port {_port} failed to start ({ex.Message}).";
            _logger.LogError(ex, "Failed to start the DCS auto-connect endpoint on UDP {Port}", _port);
            return false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // ICMP port unreachable from a previous ack on Windows (WSAECONNRESET): ignore.
                continue;
            }

            try
            {
                var reply = Process(result.Buffer, result.RemoteEndPoint);
                if (reply != null)
                    await _udp.SendAsync(reply, result.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "DCS auto-connect: failed to handle a datagram");
            }
        }
    }

    /// <summary>Returns the ack datagram, or null to stay silent.</summary>
    internal byte[]? Process(byte[] datagram, IPEndPoint remote)
    {
        if (!IPAddress.IsLoopback(remote.Address))
        {
            LogDropped("non-loopback sender " + remote.Address);
            return null;
        }

        if (!AutoConnectProtocol.TryParse(datagram, _token, out var request, out var error))
        {
            LogDropped(error);
            return null;
        }

        var key = request!.SessionId + "/" + request.Seq;
        AutoConnectAckState state;
        lock (_seen)
        {
            if (_seen.TryGetValue(key, out state))
                return AutoConnectProtocol.BuildAck(request.Seq, state);
        }

        try
        {
            state = _handler(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DCS auto-connect: request handler failed");
            state = AutoConnectAckState.Rejected;
        }

        lock (_seen)
        {
            if (_seen.TryAdd(key, state))
            {
                _seenOrder.Enqueue(key);
                while (_seenOrder.Count > SeenCapacity)
                    _seen.Remove(_seenOrder.Dequeue());
            }
        }

        return AutoConnectProtocol.BuildAck(request.Seq, state);
    }

    private void LogDropped(string reason)
    {
        // Bounded: a misbehaving local sender must not flood the log.
        if (Interlocked.Increment(ref _droppedLogged) <= 20)
            _logger.LogWarning("DCS auto-connect: dropped a datagram ({Reason})", reason);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // shutting down
        }

        _cts?.Dispose();
        _cts = null;
    }
}
