namespace OpenFreqServer.Satcom;

/// <summary>One held DAMA channel slot.</summary>
public sealed class DamaSlotHolder
{
    public required string ClientId;
    public required int SlotIndex;
    public int Priority;
    public long AssignedAtMs;
}

/// <summary>
/// Server-authoritative capacity/slot assignment for one net. 5 kHz and 25 kHz legacy DAMA are
/// distinct waveforms with their own framing -- <see cref="Dama5kHzScheduler"/> and
/// <see cref="Dama25kHzScheduler"/> are separate implementations, not one class parameterized by
/// bandwidth. Both support a generic (non-classified) priority abstraction: when at capacity, a
/// strictly higher-priority request preempts the lowest-priority currently-held slot rather than
/// being denied outright -- capacity itself is never exceeded.
/// </summary>
public interface IDamaFrameScheduler
{
    /// <summary>Nominal channel-access frame period, seconds.</summary>
    double FramePeriodSeconds { get; }

    /// <summary>Simultaneous COM/traffic slots this net can support. GAMEPLAY_CONFIG -- real
    /// capacity depends on baseband rate/channel configuration not modeled at this fidelity.</summary>
    int CapacityPerFrame { get; }

    long CurrentFrameIndex(long nowMs);

    /// <summary>Attempts to grant a slot to <paramref name="clientId"/>. Returns the slot index on
    /// success. If the net is at capacity and <paramref name="priority"/> is strictly higher than
    /// the lowest-priority currently-held slot, that lower-priority holder is evicted (its
    /// clientId is returned via <paramref name="evictedClientId"/>) and the new slot is granted.
    /// Returns null (no eviction) if at capacity with no lower-priority holder to preempt.</summary>
    int? TryAssignSlot(string clientId, int priority, long nowMs, out string? evictedClientId);

    void ReleaseSlot(string clientId);

    IReadOnlyCollection<DamaSlotHolder> ActiveHolders { get; }
}

public abstract class DamaFrameSchedulerBase : IDamaFrameScheduler
{
    private readonly Dictionary<string, DamaSlotHolder> _holders = new();

    public abstract double FramePeriodSeconds { get; }
    public abstract int CapacityPerFrame { get; }

    public long CurrentFrameIndex(long nowMs) => (long)(nowMs / (FramePeriodSeconds * 1000.0));

    public IReadOnlyCollection<DamaSlotHolder> ActiveHolders => _holders.Values;

    public int? TryAssignSlot(string clientId, int priority, long nowMs, out string? evictedClientId)
    {
        evictedClientId = null;

        if (_holders.TryGetValue(clientId, out var existing))
            return existing.SlotIndex;

        var usedSlots = _holders.Values.Select(h => h.SlotIndex).ToHashSet();
        for (var slot = 0; slot < CapacityPerFrame; slot++)
        {
            if (usedSlots.Contains(slot)) continue;
            Grant(clientId, slot, priority, nowMs);
            return slot;
        }

        if (_holders.Count == 0) return null;
        var lowest = _holders.Values.OrderBy(h => h.Priority).ThenBy(h => h.AssignedAtMs).First();
        if (priority <= lowest.Priority)
            return null; // at capacity, nothing lower-priority to preempt -- genuine denial

        var slotToReuse = lowest.SlotIndex;
        _holders.Remove(lowest.ClientId);
        evictedClientId = lowest.ClientId;
        Grant(clientId, slotToReuse, priority, nowMs);
        return slotToReuse;
    }

    private void Grant(string clientId, int slot, int priority, long nowMs) =>
        _holders[clientId] = new DamaSlotHolder { ClientId = clientId, SlotIndex = slot, Priority = priority, AssignedAtMs = nowMs };

    public void ReleaseSlot(string clientId) => _holders.Remove(clientId);
}

/// <summary>Legacy 5 kHz DAMA: ~8.96 second frame divided into Framing/Orderwire (FOW), Response
/// Orderwire (ROW), and Communication (COM) segments. SOURCE_EXACT frame period (FM 6-02.90); COM
/// slot capacity is CALIBRATED_APPROXIMATION (depends on the specific baseband rate configured for
/// the net, which real doctrine allows to vary -- not modeled at that fidelity here).</summary>
public sealed class Dama5kHzScheduler : DamaFrameSchedulerBase
{
    public const double FramePeriodSecondsConst = 8.96;
    public override double FramePeriodSeconds => FramePeriodSecondsConst;
    public override int CapacityPerFrame { get; } = 4;

    public Dama5kHzScheduler() { }
    public Dama5kHzScheduler(int capacityPerFrame) => CapacityPerFrame = capacityPerFrame;
}

/// <summary>Legacy 25 kHz DAMA: wider channel, shorter nominal access cycle and higher slot count
/// than 5 kHz DAMA, with its own Automatic/Distributed-Control access flow rather than the 5 kHz
/// FOW/ROW/COM frame. CALIBRATED_APPROXIMATION -- the exact public-domain timing for 25 kHz DAMA's
/// access cycle is not available at circuit-implementation fidelity; this uses a distinct,
/// documented approximation rather than reusing the 5 kHz frame period at a different bandwidth.</summary>
public sealed class Dama25kHzScheduler : DamaFrameSchedulerBase
{
    public const double FramePeriodSecondsConst = 2.0;
    public override double FramePeriodSeconds => FramePeriodSecondsConst;
    public override int CapacityPerFrame { get; } = 8;

    public Dama25kHzScheduler() { }
    public Dama25kHzScheduler(int capacityPerFrame) => CapacityPerFrame = capacityPerFrame;
}
