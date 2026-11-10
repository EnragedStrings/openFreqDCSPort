using System;
using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Common;
using OpenFreq.Common.Eccm;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreq.Client.Services.Eccm;

/// <summary>A channel card's ECCM settings changed (manual entry, or the cockpit export).</summary>
public sealed record ChannelEccmUpdateMessage(Guid ChannelId, int FrequencyKhz, EccmSlotSettings Settings);

/// <summary>A slot's resolved ECCM state, for the channel card (mode, net, readiness).</summary>
public sealed record EccmSlotStatusMessage(Guid SlotId, EccmResolution Resolution);

public interface IEccmService : ILifecycleService, IDisposable
{
    /// <summary>The server's comm plan, or null when it has none.</summary>
    EccmCommPlan? CommPlan { get; }

    /// <summary>Resolve and apply one slot's settings; returns the resolution (also sent as
    /// <see cref="EccmSlotStatusMessage"/>).</summary>
    EccmResolution Apply(Guid slotId, int frequencyKhz, EccmSlotSettings settings);
}

/// <summary>
/// Client side of HAVE QUICK / SINCGARS (docs/jamming/HAVE_QUICK.md): holds the server's comm plan (the "fill"),
/// resolves each radio slot's settings against it (<see cref="EccmResolver"/>) and hands the result to
/// OpenFreqService (TX stamp, TRANSEC gate, jamming hopset). Re-resolves every slot when the plan changes.
/// </summary>
public sealed class EccmService : IEccmService
{
    private readonly IOpenFreqService _openFreq;
    private readonly ILogger<EccmService> _logger;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<Guid, (int FrequencyKhz, EccmSlotSettings Settings)> _slots = new();
    private volatile EccmCommPlan? _plan;
    private bool _started;

    public EccmService(IOpenFreqService openFreq, ILogger<EccmService> logger)
        : this(openFreq, logger, () => DateTime.UtcNow)
    {
    }

    internal EccmService(IOpenFreqService openFreq, ILogger<EccmService> logger, Func<DateTime> clock)
    {
        _openFreq = openFreq;
        _logger = logger;
        _clock = clock;
        _openFreq.EccmCommPlanReceived += OnCommPlanReceived;
    }

    public EccmCommPlan? CommPlan => _plan;

    public void Start()
    {
        if (_started) return;
        _started = true;
        WeakReferenceMessenger.Default.Register<ChannelEccmUpdateMessage>(this,
            (_, m) => Apply(m.ChannelId, m.FrequencyKhz, m.Settings));
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        WeakReferenceMessenger.Default.Unregister<ChannelEccmUpdateMessage>(this);
    }

    public void Dispose()
    {
        Stop();
        _openFreq.EccmCommPlanReceived -= OnCommPlanReceived;
    }

    public EccmResolution Apply(Guid slotId, int frequencyKhz, EccmSlotSettings settings)
    {
        if (settings.Mode == OpenFreqAudio.Eccm.HopMode.None) _slots.TryRemove(slotId, out _);
        else _slots[slotId] = (frequencyKhz, settings);

        var resolution = EccmResolver.Resolve(settings, _plan, _clock());
        _openFreq.SetSlotEccm(frequencyKhz, slotId, resolution);
        WeakReferenceMessenger.Default.Send(new EccmSlotStatusMessage(slotId, resolution));
        if (resolution.IsHopping)
            _logger.LogInformation("ECCM slot {SlotId} on {FrequencyKhz}: {Description} [{Readiness}]", slotId,
                frequencyKhz, resolution.Description, resolution.IsReady ? "ready" : resolution.Readiness);
        return resolution;
    }

    internal void OnCommPlanReceived(object? sender, EccmCommPlanUpdateMessage message)
    {
        _plan = message.Plan;
        _logger.LogInformation("ECCM comm plan received: {Summary}", message.Plan == null
            ? "none"
            : $"mission date {message.Plan.MissionDate}, {message.Plan.Mwod.Count} WOD(s), hop rate {message.Plan.HopRateHz}/s");
        foreach (var (slotId, (frequencyKhz, settings)) in _slots)
            Apply(slotId, frequencyKhz, settings);
    }
}
