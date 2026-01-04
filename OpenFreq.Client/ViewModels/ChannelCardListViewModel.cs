using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardListViewModel : ViewModelBase, IDisposable
{
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly ILogger<AcmiClientService> _logger;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;

    private readonly Lock _channelImportLock = new();

    [ObservableProperty] public partial ObservableCollection<ChannelCardViewModel> Channels { get; set; } = [];

    public ChannelCardListViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, ILogger<AcmiClientService> logger,
        IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _acmiClientService = acmiClientService;
        _logger = logger;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _falconSharedMemoryService = falconSharedMemoryService;

        // Subscribe to hotkey events
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;

        // Subscribe to frequency status changes
        _openFreqService.FrequencyStatusChanged += OnFrequencyStatusChanged;

        // Subscribe to connection state for auto-join
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;

        // Subscribe to BMS Frequency update messages
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged += OnFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged += OnPttChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;

        // Subscribe to channel updates for binding changes
        WeakReferenceMessenger.Default.Register<ChannelUpdatedMessage>(this, OnChannelUpdated);

        WeakReferenceMessenger.Default.Register<ChannelEnabledDisabledMessage>(this, OnChannelEnabledDisabled);

        WeakReferenceMessenger.Default.Register<ChannelDeleteRequestedMessage>(this, OnChannelDeleteRequested);


        // Subscribe to transmission messages
        WeakReferenceMessenger.Default.Register<StartTransmissionMessage>(this,
            async (r, m) => await HandleStartTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<StopTransmissionMessage>(this,
            async (r, m) => await HandleStopTransmissionAsync(m));
    }

    private void OnConnectionParametersChanged(object? sender,
        ConnectionParametersChangedEventArgs e)
    {
        ImportBmsRadioChannels();
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        _logger.LogDebug($"FalconSharedMemoryServiceOnFlyingStateChanged: {e.OldFlyingState} -> {e.NewFlyingState}");
        if (!e.OldFlyingState && e.NewFlyingState)
        {
            _hotkeyService.Pause();
        }
        else if (e.OldFlyingState && !e.NewFlyingState)
        {
            _hotkeyService.Resume();
        }
    }

    private void ImportBmsRadioChannels(bool clearExisting = true)
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_channelImportLock)
            {
                if (clearExisting)
                {
                    foreach (var channel in Channels)
                    {
                        if (channel.Status != Channel.ChannelStatus.Disconnected)
                        {
                            _openFreqService.LeaveFrequencyAsync(channel.FrequencyMhz)
                                .Wait(TimeSpan.FromMilliseconds(100));
                        }
                    }

                    Channels.Clear();
                }

                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel != null && !Channels.Any(c =>
                            Math.Abs(c.FrequencyMhz - falconChannel.Frequency / 1000d) < 0.1d))
                    {
                        CreateChannel(falconChannel.Frequency / 1000d, "BMS Channel " + type,
                            Channel.ToChannelType(type),
                            false);
                    }
                }

                if (_openFreqService.IsAuthenticated)
                {
                    JoinAllChannelsAsync().Wait(TimeSpan.FromSeconds(2));
                }
            }
        });
    }

    private void OnPttChanged(object? sender, RadioPttChangedEventArgs e)
    {
        var channel = Channels.FirstOrDefault(c => c.Type == Channel.ToChannelType(e.RadioType));
        if (channel == null || channel.Status == Channel.ChannelStatus.Disconnected) return;
        switch (e)
        {
            case { OldPtt: false, NewPtt: true }:
                _openFreqService.StartTransmissionAsync(channel.FrequencyMhz).Wait();
                break;
            case { OldPtt: true, NewPtt: false }:
                _openFreqService.StopTransmissionAsync(channel.FrequencyMhz).Wait();
                break;
        }
    }

    private void OnFrequencyChanged(object? sender, RadioFrequencyChangedEventArgs e)
    {
        _logger.LogDebug($"FalconRadioSharedMemoryServiceOnFrequencyChanged: {e.OldFrequency} -> {e.NewFrequency}");
        lock (_channelImportLock)
        {
            var oldChannel =
                Channels.FirstOrDefault(c => Math.Abs(c.FrequencyMhz - (double)e.OldFrequency / 1000) < 0.01);
            if (oldChannel != null)
            {
                oldChannel.FrequencyMhz = (double)e.NewFrequency / 1000;
                OnChannelUpdated(this,
                    new ChannelUpdatedMessage(oldChannel.Id, e.OldFrequency / 1000d, e.NewFrequency / 1000d,
                        oldChannel.Type, Channel.ToChannelType(e.RadioType), oldChannel.Status, oldChannel.HotKey,
                        oldChannel.HotKey));
            }
            else
            {
                var newChannel = CreateChannel(e.NewFrequency / 1000d, "BMS Channel",
                    Channel.ToChannelType(e.RadioType),
                    false);
                JoinFrequencyAsync(newChannel.FrequencyMhz).Wait(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    private void OnChannelDeleteRequested(object recipient, ChannelDeleteRequestedMessage message)
    {
        var vm = Channels.FirstOrDefault(c => c.Id == message.ChannelId);

        if (vm != null)
        {
            Channels.Remove(vm);
            vm.Dispose();
        }

        if (_openFreqService.IsAuthenticated)
        {
            _openFreqService.LeaveFrequencyAsync(message.FrequencyMhz);
        }
    }

    private void OnChannelEnabledDisabled(object recipient, ChannelEnabledDisabledMessage message)
    {
        if (_openFreqService.IsAuthenticated && message.Enabled)
        {
            _openFreqService.JoinFrequencyAsync(message.FrequencyMhz).Wait(TimeSpan.FromMilliseconds(100));
        }
        else if (_openFreqService.IsAuthenticated && !message.Enabled)
        {
            _openFreqService.LeaveFrequencyAsync(message.FrequencyMhz).Wait(TimeSpan.FromMilliseconds(100));
        }

        // dont care for the rest
    }

    public ChannelCardViewModel CreateChannel(double frequencyMhz, string name, Channel.ChannelType channelType,
        bool isInEditMode = true)
    {
        var channel = new ChannelCardViewModel(_hotkeyService);
        channel.Name = name;
        channel.Type = channelType;
        channel.FrequencyMhz = frequencyMhz;
        channel.IsEditing = isInEditMode;

        Channels.Add(channel);
        return channel;
    }

    public ChannelCardViewModel CreateChannel(Channel channel)
    {
        var viewModel = new ChannelCardViewModel(_hotkeyService, channel);
        Channels.Add(viewModel);
        return viewModel;
    }

    private void OnChannelUpdated(object recipient, ChannelUpdatedMessage message)
    {
        if (message.NeedsReconnect && _openFreqService.IsAuthenticated)
        {
            // Only leave old frequency if the channel was previously connected
            if (message.OldStatus != Channel.ChannelStatus.Disconnected)
            {
                _openFreqService.LeaveFrequencyAsync(message.OldFrequencyMhz).Wait(TimeSpan.FromMilliseconds(100));
            }

            // Always join the new frequency
            _openFreqService.JoinFrequencyAsync(message.NewFrequencyMhz).Wait(TimeSpan.FromMilliseconds(100));
        }
    }

    private async void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        try
        {
            foreach (var channelId in e.ChannelIds)
            {
                var channel = Channels.FirstOrDefault(c => c.Id == channelId);
                if (channel != null && channel.Status != Channel.ChannelStatus.Disconnected && !channel.IsEditing)
                {
                    await _openFreqService.StartTransmissionAsync(channel.FrequencyMhz);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey press: {ex.Message}");
        }
    }

    private async void OnHotkeyReleased(object? sender, HotkeyReleasedEventArgs e)
    {
        try
        {
            foreach (var channelId in e.ChannelIds)
            {
                var channel = Channels.FirstOrDefault(c => c.Id == channelId);
                if (channel != null && channel.Status != Channel.ChannelStatus.Disconnected)
                {
                    await _openFreqService.StopTransmissionAsync(channel.FrequencyMhz);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey release: {ex.Message}");
        }
    }

    private void OnFrequencyStatusChanged(object? sender, FrequencyStatusEventArgs e)
    {
        var channel = Channels.FirstOrDefault(c => Math.Abs(c.FrequencyMhz - e.FrequencyMhz) < 0.01);
        if (channel != null)
        {
            channel.Status = e.Status;
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        if (state == ConnectionState.Authenticated)
        {
            // Auto-join all channels when authenticated
            JoinAllChannelsAsync().Wait(TimeSpan.FromMilliseconds(500));
        }
        else if (state == ConnectionState.Disconnected)
        {
            // Reset all channel status on disconnect
            foreach (var channel in Channels)
            {
                channel.Status = Channel.ChannelStatus.Disconnected;
            }
        }
    }

    private async Task HandleStartTransmissionAsync(StartTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StartTransmissionAsync(msg.FrequencyMhz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start transmission: {ex.Message}");
        }
    }

    private async Task HandleStopTransmissionAsync(StopTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StopTransmissionAsync(msg.FrequencyMhz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop transmission: {ex.Message}");
        }
    }

    public async Task JoinFrequencyAsync(double frequencyMhz)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.JoinFrequencyAsync(frequencyMhz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to join frequency {frequencyMhz}: {ex.Message}");
        }
    }

    public async Task LeaveFrequencyAsync(double frequency)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.LeaveFrequencyAsync(frequency);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to leave frequency {frequency}: {ex.Message}");
        }
    }

    public async Task JoinAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            await _openFreqService.JoinFrequencyAsync(channel.FrequencyMhz);
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            await _openFreqService.LeaveFrequencyAsync(channel.FrequencyMhz);
        }
    }

    public void Dispose()
    {
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.HotkeyReleased -= OnHotkeyReleased;
        _openFreqService.FrequencyStatusChanged -= OnFrequencyStatusChanged;
        _openFreqService.ConnectionStateChanged -= OnConnectionStateChanged;
        _falconRadioSharedMemoryService.ConnectionParametersChanged -= OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged -= OnFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged -= OnPttChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        WeakReferenceMessenger.Default.Unregister<ChannelUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StartTransmissionMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StopTransmissionMessage>(this);
    }
}