using System.Collections.Concurrent;

namespace OpenFreq.Server;

/// <summary>
/// </summary>
public class FrequencyChannelManager
{
    private readonly ConcurrentDictionary<double, ConcurrentDictionary<string, byte>> _channels = new();

    public bool JoinChannel(double frequencyMhz, string clientId)
    {
        var channelClients = _channels.GetOrAdd(frequencyMhz, _ => new ConcurrentDictionary<string, byte>());
        return channelClients.TryAdd(clientId, 0);
    }

    public bool LeaveChannel(double frequencyMhz, string clientId)
    {
        if (_channels.TryGetValue(frequencyMhz, out var clients))
        {
            var removed = clients.TryRemove(clientId, out _);
            
            // Clean up empty channels
            if (clients.IsEmpty)
            {
                _channels.TryRemove(frequencyMhz, out _);
            }

            return removed;
        }

        return false;
    }

    public void LeaveAllChannels(string clientId)
    {
        // Use ToList() to avoid modification during enumeration
        var frequencies = _channels.Keys.ToList();

        foreach (var frequency in frequencies)
        {
            if (_channels.TryGetValue(frequency, out var clients))
            {
                if (clients.TryRemove(clientId, out _))
                {
                    // Clean up empty channels
                    if (clients.IsEmpty)
                    {
                        _channels.TryRemove(frequency, out _);
                    }
                }
            }
        }
    }

    /// <summary>
    /// </summary>
    public string[] GetClientsInChannel(double frequencyMhz)
    {
        if (_channels.TryGetValue(frequencyMhz, out var clients))
        {
            return clients.Keys.ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Gets first channel for a client (for backward compatibility)
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
    /// Gets all channels a client is in
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
    /// </summary>
    public int GetChannelCount(double frequencyMhz)
    {
        if (_channels.TryGetValue(frequencyMhz, out var clients))
        {
            return clients.Count;
        }

        return 0;
    }
}