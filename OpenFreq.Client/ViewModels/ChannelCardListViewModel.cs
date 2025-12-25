using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
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
    
    [ObservableProperty]
    private ObservableCollection<ChannelCardViewModel> _channels = new();

    public ChannelCardListViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, ILogger<AcmiClientService> logger)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _acmiClientService = acmiClientService;
        _logger = logger;
        
        // Subscribe to hotkey events
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;
        
        // Subscribe to frequency status changes
        _openFreqService.FrequencyStatusChanged += OnFrequencyStatusChanged;
        
        // Subscribe to connection state for auto-join
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;
        
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

    private void OnChannelDeleteRequested(object recipient, ChannelDeleteRequestedMessage message)
    {
        var vm = Channels.FirstOrDefault(c => c?.Id == message.ChannelId);
        
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
            _openFreqService.JoinFrequencyAsync(message.FrequencyMhz).Wait();
        }
        else if (_openFreqService.IsAuthenticated && !message.Enabled)
        {
            _openFreqService.LeaveFrequencyAsync(message.FrequencyMhz).Wait();
        }
        
        // dont care for the rest
    }

    public ChannelCardViewModel CreateChannel(double frequencyMhz, string name, Channel.ChannelType channelType)
    {
        var channel = new ChannelCardViewModel(_hotkeyService);
        channel.Name = name;
        channel.Type = channelType;
        channel.FrequencyMhz = frequencyMhz;
        
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
                _openFreqService.LeaveFrequencyAsync(message.OldFrequencyMhz).Wait();
            }
            
            // Always join the new frequency
            _openFreqService.JoinFrequencyAsync(message.NewFrequencyMhz).Wait();
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
        var channel = Channels.FirstOrDefault(c => c.FrequencyMhz == e.FrequencyMhz);
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
            JoinAllChannelsAsync().Wait();
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

    public async Task JoinFrequencyAsync(int frequency)
    {
        if (!_openFreqService.IsAuthenticated) return;
        
        try
        {
            await _openFreqService.LeaveFrequencyAsync(frequency);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to join frequency {frequency}: {ex.Message}");
        }
    }
    
    public async Task LeaveFrequencyAsync(int frequency)
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
        WeakReferenceMessenger.Default.Unregister<ChannelUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StartTransmissionMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StopTransmissionMessage>(this);
    }
}