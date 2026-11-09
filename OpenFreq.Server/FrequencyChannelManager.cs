using OpenFreq.Common;

namespace OpenFreqServer;

/// <summary>
/// The outcome of resolving one inbound audio packet against channel membership.
/// </summary>
/// <param name="Valid">Requested frequencies the sender is actually joined to.</param>
/// <param name="Rejected">Requested frequencies the sender is not joined to.</param>
/// <param name="Recipients">Deduplicated clients to relay to, excluding the sender. Includes
/// silent observers: they receive audio exactly like members, they just aren't listed.</param>
public readonly record struct RelayTargets(int[] Valid, int[] Rejected, string[] Recipients);

// Can a brother get some sum types/tagged unions?
public abstract record JoinChannelResult;
public record ChannelJoined : JoinChannelResult;
public record AlreadyInChannel : JoinChannelResult;
public record ChannelFull : JoinChannelResult;

/// <summary>One frequency's headline state, for the server's own status views.
/// <paramref name="ClientCount"/> excludes silent observers.</summary>
public readonly record struct ChannelSummary(int FrequencyKhz, int ClientCount, bool IsTransmitting);

/// <summary>
/// Manages frequency channels and tracks peer state for broadcasting.
/// </summary>
/// <remarks>
/// This is the single source of truth for which client is on which frequency: one table, every
/// read and write under one lock, so a join, a leave and the capacity check are each atomic
/// relative to one another and there is no second copy (the session used to keep one) to drift
/// out of agreement with it.
///
/// Each member also carries whether it joined as a silent observer (GCI "monitor all" scanner,
/// see JoinChannelMessage.IsObserver). Observers are routed audio like any member, but never
/// appear in a peer list, never count toward capacity and are never refused by it. The flag
/// lives beside PeerData rather than in it: PeerData is the wire type sent to other clients.
/// </remarks>
public class FrequencyChannelManager
{
    private readonly record struct Member(PeerData Peer, bool IsObserver);

    private readonly Dictionary<int, Dictionary<string, Member>> _channels = new();

    /// <summary>
    /// Join a channel with initial peer data, admitting the client only if the channel has
    /// room for them. Silent observers bypass the capacity check and are not counted by it.
    /// A client already on the channel gets <see cref="AlreadyInChannel"/> regardless of
    /// capacity, and keeps the role (observer or not) it originally joined with.
    /// </summary>
    public JoinChannelResult JoinChannel(
        int frequencyKhz,
        string clientId,
        string displayName,
        bool is3d = false,
        int maxClientsPerChannel = int.MaxValue,
        bool isObserver = false)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(frequencyKhz, out var members))
            {
                members = [];
                _channels[frequencyKhz] = members;
            }

            if (members.ContainsKey(clientId)) return new AlreadyInChannel();

            if (!isObserver && CountNonObservers(members) >= maxClientsPerChannel)
            {
                // Don't strand a channel we created above just to reject the join.
                if (members.Count == 0) _channels.Remove(frequencyKhz);
                return new ChannelFull();
            }

            members[clientId] = new Member(
                new PeerData(clientId, displayName, PeerData.PeerStatus.Receiving, is3d), isObserver);
            return new ChannelJoined();
        }
    }

    /// <summary>
    /// Leave a specific channel.
    /// </summary>
    public bool LeaveChannel(int frequencyKhz, string clientId) => LeaveChannel(frequencyKhz, clientId, out _);

    /// <summary>
    /// Leave a specific channel, reporting whether the client had joined it as a silent
    /// observer (so the caller knows not to announce the departure).
    /// </summary>
    public bool LeaveChannel(int frequencyKhz, string clientId, out bool wasObserver)
    {
        lock (_channels)
        {
            wasObserver = false;
            if (!_channels.TryGetValue(frequencyKhz, out var members)) return false;
            if (!members.Remove(clientId, out var member)) return false;

            wasObserver = member.IsObserver;
            if (members.Count == 0) _channels.Remove(frequencyKhz);
            return true;
        }
    }

    /// <summary>
    /// Leave all channels for a client, atomically.
    /// </summary>
    /// <returns>Every frequency the client was removed from, and whether it was a silent
    /// observer there.</returns>
    public List<(int FrequencyKhz, bool WasObserver)> LeaveAllChannels(string clientId)
    {
        lock (_channels)
        {
            var left = new List<(int, bool)>();

            // Materialize the keys: the loop removes emptied channels from _channels.
            foreach (var frequency in _channels.Keys.ToList())
            {
                var members = _channels[frequency];
                if (!members.Remove(clientId, out var member)) continue;

                left.Add((frequency, member.IsObserver));
                if (members.Count == 0) _channels.Remove(frequency);
            }

            return left;
        }
    }

    /// <summary>True if this client is on this frequency as a silent observer.</summary>
    public bool IsObserver(int frequencyKhz, string clientId)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(frequencyKhz, out var members) &&
                   members.TryGetValue(clientId, out var member) &&
                   member.IsObserver;
        }
    }

    /// <summary>
    /// Update the display name for a peer across all channels they're in
    /// </summary>
    public void UpdateDisplayName(string clientId, string newDisplayName)
    {
        lock (_channels)
        {
            foreach (var members in _channels.Values)
            {
                if (!members.TryGetValue(clientId, out var current)) continue;

                // Replace rather than mutate: getters hand these instances out, so the
                // snapshots callers are already holding must not change under them.
                members[clientId] = current with
                {
                    Peer = new PeerData(current.Peer.Id, newDisplayName, current.Peer.Status, current.Peer.Is3d)
                };
            }
        }
    }

    /// <summary>
    /// Update the last-known 3D mode for a peer across every channel they're in.
    /// </summary>
    public void UpdateIs3d(string clientId, bool is3d)
    {
        lock (_channels)
        {
            foreach (var members in _channels.Values)
            {
                if (!members.TryGetValue(clientId, out var current)) continue;

                members[clientId] = current with
                {
                    Peer = new PeerData(current.Peer.Id, current.Peer.Name, current.Peer.Status, is3d)
                };
            }
        }
    }

    /// <summary>
    /// Every client ID on a channel, silent observers included -- this is the routing view
    /// (who receives audio and channel broadcasts), never a view to show other clients.
    /// </summary>
    public string[] GetClientsInChannel(int frequencyKhz)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(frequencyKhz, out var members)
                ? members.Keys.ToArray()
                : [];
        }
    }

    /// <summary>
    /// Whether a client is routable on any frequency at all.
    /// </summary>
    public bool IsInAnyChannel(string clientId)
    {
        lock (_channels)
        {
            foreach (var members in _channels.Values)
            {
                if (members.ContainsKey(clientId)) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Record a client's transmit state and 3D mode on one frequency, and report the other
    /// clients there — the peers that need to be told about the change.
    /// </summary>
    /// <returns>
    /// The other clients on the frequency, or null if this client is not on it. Empty means
    /// they are talking to nobody, which is not the same as not being tuned.
    /// </returns>
    public string[]? SetTransmissionState(int frequencyKhz, string clientId, bool transmitting, bool is3d)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(frequencyKhz, out var members) ||
                !members.TryGetValue(clientId, out var current))
            {
                return null;
            }

            members[clientId] = current with
            {
                Peer = new PeerData(
                    current.Peer.Id,
                    current.Peer.Name,
                    transmitting ? PeerData.PeerStatus.Transmitting : PeerData.PeerStatus.Receiving,
                    is3d)
            };

            return members.Keys.Where(id => id != clientId).ToArray();
        }
    }

    /// <summary>
    /// Every frequency a client is on (silent-observer ones included -- this is the operator's
    /// view), paired with its peer entry there.
    /// </summary>
    public List<(int FrequencyKhz, PeerData Peer)> GetClientChannelStates(string clientId)
    {
        lock (_channels)
        {
            var result = new List<(int, PeerData)>();

            foreach (var (frequency, members) in _channels)
            {
                if (members.TryGetValue(clientId, out var member)) result.Add((frequency, member.Peer));
            }

            return result;
        }
    }

    /// <summary>
    /// Every active frequency with its (non-observer) client count and whether anyone is
    /// talking on it, ordered by frequency. A frequency with only observers on it is listed
    /// with a count of zero.
    /// </summary>
    public List<ChannelSummary> GetChannelSummaries()
    {
        lock (_channels)
        {
            var summaries = new List<ChannelSummary>(_channels.Count);

            foreach (var (frequency, members) in _channels)
            {
                var transmitting = false;
                foreach (var member in members.Values)
                {
                    if (member.Peer.Status != PeerData.PeerStatus.Transmitting) continue;
                    transmitting = true;
                    break;
                }

                summaries.Add(new ChannelSummary(frequency, CountNonObservers(members), transmitting));
            }

            summaries.Sort((a, b) => a.FrequencyKhz.CompareTo(b.FrequencyKhz));
            return summaries;
        }
    }

    /// <summary>
    /// Total number of client/frequency pairs currently transmitting. A client talking on
    /// two frequencies counts twice.
    /// </summary>
    public int CountTransmitting()
    {
        lock (_channels)
        {
            var count = 0;

            foreach (var members in _channels.Values)
            {
                foreach (var member in members.Values)
                {
                    if (member.Peer.Status == PeerData.PeerStatus.Transmitting) count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Resolves one inbound audio packet against channel membership:
    /// which of the requested frequencies the sender may actually transmit on, which it
    /// may not, and the deduplicated set of clients that should receive the audio (silent
    /// observers included).
    /// </summary>
    public RelayTargets ResolveRelay(string senderClientId, IReadOnlyList<int> requestedKhz)
    {
        lock (_channels)
        {
            List<int> valid = [];
            List<int> rejected = [];
            HashSet<string> recipients = [];

            foreach (var frequencyKhz in requestedKhz)
            {
                if (!_channels.TryGetValue(frequencyKhz, out var members) ||
                    !members.ContainsKey(senderClientId))
                {
                    rejected.Add(frequencyKhz);
                    continue;
                }

                valid.Add(frequencyKhz);

                foreach (var clientId in members.Keys)
                {
                    if (clientId != senderClientId) recipients.Add(clientId);
                }
            }

            return new RelayTargets([.. valid], [.. rejected], [.. recipients]);
        }
    }

    /// <summary>
    /// Get all peer data in a specific channel, excluding silent observers -- this is the view
    /// shown to other clients (and to an observer itself, so a scanner can see who's really
    /// talking), never revealing that an observer is present.
    /// </summary>
    public List<PeerData> GetPeersInChannel(int frequencyKhz)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(frequencyKhz, out var members)) return [];

            return members.Values.Where(m => !m.IsObserver).Select(m => m.Peer).ToList();
        }
    }

    /// <summary>
    /// Get the complete channel state across all frequencies, excluding silent observers (see
    /// GetPeersInChannel). A frequency with only observers on it reports as empty rather than
    /// being omitted, so callers that key off "is anyone real here" see it consistently.
    /// </summary>
    public SortedDictionary<int, List<PeerData>> GetAllChannelStates()
    {
        lock (_channels)
        {
            var result = new SortedDictionary<int, List<PeerData>>();

            foreach (var (frequency, members) in _channels)
            {
                result[frequency] = members.Values
                    .Where(m => !m.IsObserver)
                    .Select(m => m.Peer)
                    .OrderBy(p => p.Name)
                    .ToList();
            }

            return result;
        }
    }

    /// <summary>
    /// Get all channels a client is in, ordered by frequency.
    /// </summary>
    /// <param name="clientId">The client to look up.</param>
    /// <param name="includeObserved">Whether to include frequencies the client is only silently
    /// observing. Pass false for anything shown to other people, which must not reveal that an
    /// observer is listening.</param>
    public int[] GetClientChannels(string clientId, bool includeObserved = true)
    {
        lock (_channels)
        {
            List<int> channels = [];

            foreach (var (frequency, members) in _channels)
            {
                if (members.TryGetValue(clientId, out var member) && (includeObserved || !member.IsObserver))
                {
                    channels.Add(frequency);
                }
            }

            channels.Sort();
            return [.. channels];
        }
    }

    /// <summary>
    /// Get the number of real (non-observer) peers in a channel -- the same count capacity
    /// enforcement (MaxClientsPerChannel) uses, so a silent scanner is never an occupant.
    /// </summary>
    public int GetChannelCount(int frequencyKhz)
    {
        lock (_channels)
        {
            return _channels.TryGetValue(frequencyKhz, out var members) ? CountNonObservers(members) : 0;
        }
    }

    private static int CountNonObservers(Dictionary<string, Member> members)
    {
        var count = 0;
        foreach (var member in members.Values)
        {
            if (!member.IsObserver) count++;
        }

        return count;
    }
}
