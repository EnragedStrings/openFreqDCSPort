using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace OpenFreq.Server;

/// <summary>
/// Manages UDP audio streams with backpressure control to prevent buffer buildup
/// and compounding delays
/// </summary>
public class UdpStreamManager
{
    private readonly ILogger<UdpStreamManager> _logger;
    private readonly ConcurrentDictionary<string, ClientStreamState> _clientStates = new();
    
    // Configuration
    private const int MAX_PENDING_SENDS = 3;  // Maximum queued sends per client
    private const int SEND_BUFFER_SIZE = 8192; // Small buffer = less queuing
    private const int RECEIVE_BUFFER_SIZE = 65535;
    
    private static readonly Action<ILogger, string, int, Exception?> _logPacketsDropped =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(100, nameof(SendPacketAsync)),
            "Dropped {DroppedCount} packets for client {ClientId} due to send backpressure");

    public UdpStreamManager(ILogger<UdpStreamManager> logger)
    {
        _logger = logger;
    }

    public UdpClient CreateUdpClient(string clientId, int port)
    {
        var udpClient = new UdpClient();
        // Enable address reuse to handle TIME_WAIT state
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        
        // Bind to the port
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        
        // Small send buffer prevents OS-level queuing
        // Old packets will be dropped instead of queued, preventing delay buildup
        udpClient.Client.SendBufferSize = SEND_BUFFER_SIZE;
        udpClient.Client.ReceiveBufferSize = RECEIVE_BUFFER_SIZE;
        
        // Don't fragment packets
        udpClient.DontFragment = true;

        var state = new ClientStreamState
        {
            ClientId = clientId,
            UdpClient = udpClient
        };
        
        _clientStates[clientId] = state;
        
        _logger.LogInformation("Created UDP client for {ClientId} on port {Port} with SendBuffer={SendBufferSize}", 
            clientId, port, SEND_BUFFER_SIZE);
        
        return udpClient;
    }

    /// <summary>
    /// Send packet with backpressure control
    /// Returns false if packet was dropped due to congestion
    /// </summary>
    public bool SendPacketAsync(string clientId, UdpClient udpClient, byte[] packet, IPEndPoint remoteEndPoint)
    {
        if (!_clientStates.TryGetValue(clientId, out var state))
            return false;

        // Check if we're already sending too many packets
        var pendingSends = Interlocked.Increment(ref state.PendingSends);
        
        if (pendingSends > MAX_PENDING_SENDS)
        {
            // Drop this packet to prevent buffer buildup
            Interlocked.Decrement(ref state.PendingSends);
            var dropped = Interlocked.Increment(ref state.DroppedPackets);
            
            // Log every 10 drops to avoid log spam
            if (dropped % 10 == 0)
            {
                _logPacketsDropped(_logger, clientId, dropped, null);
            }
            
            return false;
        }

        // Send packet without blocking the caller
        _ = SendWithBackpressureAsync(state, udpClient, packet, remoteEndPoint);
        
        return true;
    }

    private async Task SendWithBackpressureAsync(
        ClientStreamState state, 
        UdpClient udpClient, 
        byte[] packet, 
        IPEndPoint remoteEndPoint)
    {
        try
        {
            // Use a timeout to prevent indefinite blocking
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            
            await udpClient.SendAsync(packet, packet.Length, remoteEndPoint)
                .WaitAsync(cts.Token);
            
            state.LastSendTime = DateTime.UtcNow;
            Interlocked.Increment(ref state.SentPackets);
        }
        catch (OperationCanceledException)
        {
            // Send timed out - this client is too slow
            Interlocked.Increment(ref state.DroppedPackets);
            
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Send timeout for client {ClientId}", state.ClientId);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Send error for client {ClientId}", state.ClientId);
        }
        finally
        {
            Interlocked.Decrement(ref state.PendingSends);
        }
    }

    public ClientStreamStats GetStats(string clientId)
    {
        if (_clientStates.TryGetValue(clientId, out var state))
        {
            return new ClientStreamStats
            {
                ClientId = clientId,
                SentPackets = state.SentPackets,
                DroppedPackets = state.DroppedPackets,
                PendingSends = state.PendingSends,
                LastSendTime = state.LastSendTime
            };
        }

        return new ClientStreamStats { ClientId = clientId };
    }

    public void RemoveClient(string clientId)
    {
        if (_clientStates.TryRemove(clientId, out var state))
        {
            _logger.LogInformation(
                "Removed client {ClientId} - Sent: {Sent}, Dropped: {Dropped}", 
                clientId, state.SentPackets, state.DroppedPackets);
        }
    }

    private class ClientStreamState
    {
        public string ClientId { get; set; } = string.Empty;
        public UdpClient UdpClient { get; set; } = null!;
        public int PendingSends;
        public int SentPackets;
        public int DroppedPackets;
        public DateTime LastSendTime = DateTime.UtcNow;
    }
}

public class ClientStreamStats
{
    public string ClientId { get; set; } = string.Empty;
    public int SentPackets { get; set; }
    public int DroppedPackets { get; set; }
    public int PendingSends { get; set; }
    public DateTime LastSendTime { get; set; }
}