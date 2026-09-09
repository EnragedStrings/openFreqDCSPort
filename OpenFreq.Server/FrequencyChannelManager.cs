using System.Collections.Concurrent;
using OpenFreq.Common;

namespace OpenFreqServer;

/// <summary>
/// Manages frequency channels and tracks peer state for broadcasting
/// </summary>
public class FrequencyChannelManager
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, PeerData>> _channels = new();

    /// <summary>Client IDs that joined a given frequency as a silent observer (see
    /// JoinChannelMessage.IsObserver) -- kept separate from PeerData itself, which is the wire type
    /// sent to other clients, so an observer can never leak into anyone else's peer list just by
    /// being present in _channels (which it must be, to receive routed audio).</summary>
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _observerIds = new();

    /// <summary>
    /// Join a channel with initial peer data
    /// </summary>
    public bool JoinChannel(int frequencyKhz, string clientId, string displayName, bool is3d = false,
        bool isObserver = false)
    {
        var channelPeers = _channels.GetOrAdd(frequencyKhz, _ => new ConcurrentDictionary<string, PeerData>());
        var peerData = new PeerData(clientId, displayName, PeerData.PeerStatus.Receiving, is3d);
        var added = channelPeers.TryAdd(clientId, peerData);

        if (isObserver)
        {
            var observers = _observerIds.GetOrAdd(frequencyKhz, _ => new ConcurrentDictionary<string, byte>());
            observers[clientId] = 0;
        }

        return added;
    }

    /// <summary>
    /// Leave a specific channel
    /// </summary>
    public bool LeaveChannel(int frequencyKhz, string clientId)
    {
        if (_channels.TryGetValue(frequencyKhz, out var peers))
        {
            var removed = peers.TryRemove(clientId, out _);

            // Clean up empty channels
            if (peers.IsEmpty)
            {
                _channels.TryRemove(frequencyKhz, out _);
            }

            if (_observerIds.TryGetValue(frequencyKhz, out var observers))
            {
                observers.TryRemove(clientId, out _);
                if (observers.IsEmpty)
                    _observerIds.TryRemove(frequencyKhz, out _);
            }

            return removed;
        }

        return false;
    }

    /// <summary>
    /// Leave all channels for a client
    /// </summary>
    public void LeaveAllChannels(string clientId)
    {
        // Use ToList() to avoid modification during enumeration
        var frequencies = _channels.Keys.ToList();

        foreach (var frequency in frequencies)
        {
            if (_channels.TryGetValue(frequency, out var peers))
            {
                if (peers.TryRemove(clientId, out _))
                {
                    // Clean up empty channels
                    if (peers.IsEmpty)
                    {
                        _channels.TryRemove(frequency, out _);
                    }
                }
            }

            if (_observerIds.TryGetValue(frequency, out var observers))
            {
                observers.TryRemove(clientId, out _);
                if (observers.IsEmpty)
                    _observerIds.TryRemove(frequency, out _);
            }
        }
    }

    /// <summary>True if this client joined this frequency as a silent observer.</summary>
    public bool IsObserver(int frequencyKhz, string clientId) =>
        _observerIds.TryGetValue(frequencyKhz, out var observers) && observers.ContainsKey(clientId);

    /// <summary>
    /// Update the display name for a peer across all channels they're in
    /// </summary>
    public void UpdateDisplayName(string clientId, string newDisplayName)
    {
        foreach (var (frequency, peers) in _channels)
        {
            if (peers.TryGetValue(clientId, out var currentPeerData))
            {
                var updatedPeerData = new PeerData(
                    currentPeerData.Id,
                    newDisplayName,
                    currentPeerData.Status,
                    currentPeerData.Is3d);

                peers.TryUpdate(clientId, updatedPeerData, currentPeerData);
            }
        }
    }

    /// <summary>
    /// Update the last-known 3D mode for a peer on a specific frequency
    /// </summary>
    public void UpdateIs3d(int frequencyKhz, string clientId, bool is3d)
    {
        if (_channels.TryGetValue(frequencyKhz, out var peers) &&
            peers.TryGetValue(clientId, out var current))
        {
            var updated = new PeerData(current.Id, current.Name, current.Status, is3d);
            peers.TryUpdate(clientId, updated, current);
        }
    }

    /// <summary>
    /// Get all client IDs in a channel (for backward compatibility)
    /// </summary>
    public string[] GetClientsInChannel(int frequencyKhz)
    {
        if (_channels.TryGetValue(frequencyKhz, out var peers))
        {
            return peers.Keys.ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Get all peer data in a specific channel, excluding silent observers -- this is the view
    /// shown to other clients (and to an observer itself, so a scanner can see who's really
    /// talking), never revealing that an observer is present.
    /// </summary>
    public List<PeerData> GetPeersInChannel(int frequencyKhz)
    {
        if (!_channels.TryGetValue(frequencyKhz, out var peers))
            return new List<PeerData>();

        _observerIds.TryGetValue(frequencyKhz, out var observers);
        return peers.Values.Where(p => observers == null || !observers.ContainsKey(p.Id)).ToList();
    }

    /// <summary>
    /// Get the complete channel state across all frequencies, excluding silent observers (see
    /// GetPeersInChannel). A frequency with only observers on it reports as empty rather than
    /// being omitted, so callers that key off "is anyone real here" see it consistently.
    /// </summary>
    public SortedDictionary<int, List<PeerData>> GetAllChannelStates()
    {
        var result = new SortedDictionary<int, List<PeerData>>();

        foreach (var (frequency, peers) in _channels)
        {
            _observerIds.TryGetValue(frequency, out var observers);
            result[frequency] = peers.Values
                .Where(p => observers == null || !observers.ContainsKey(p.Id))
                .OrderBy(p => p.Name)
                .ToList();
        }

        return result;
    }

    /// <summary>
    /// Get first channel for a client (for backward compatibility)
    /// </summary>
    public double GetClientChannel(string clientId)
    {
        foreach (var kvp in _channels)
        {
            if (kvp.Value.ContainsKey(clientId))
            {
                return kvp.Key;
            }
        }

        return -1;
    }

    /// <summary>
    /// Get all channels a client is in
    /// </summary>
    public List<double> GetClientChannels(string clientId)
    {
        var channels = new List<double>();

        foreach (var kvp in _channels)
        {
            if (kvp.Value.ContainsKey(clientId))
            {
                channels.Add(kvp.Key);
            }
        }

        return channels;
    }

    /// <summary>
    /// Get the number of real (non-observer) peers in a channel -- used both for capacity
    /// enforcement (MaxClientsPerChannel) and operator-facing stats, neither of which should count
    /// a silent scanner as an occupant.
    /// </summary>
    public int GetChannelCount(int frequencyKhz)
    {
        if (!_channels.TryGetValue(frequencyKhz, out var peers))
            return 0;

        if (!_observerIds.TryGetValue(frequencyKhz, out var observers) || observers.IsEmpty)
            return peers.Count;

        return peers.Keys.Count(id => !observers.ContainsKey(id));
    }
}
