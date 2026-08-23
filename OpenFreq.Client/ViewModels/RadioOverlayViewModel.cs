using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenFreqClient.Models;

namespace OpenFreqClient.ViewModels;

/// <summary>Live, flattened projection of every connected channel across all locations (not just
/// ChannelCardListViewModel.SelectedLocation, which tracks UI editing focus rather than what the
/// user is actually tuned into -- see the radio overlay plan) for RadioOverlayWindow to bind to.
/// Purely event-driven (no polling): every field the overlay needs (Name, FrequencyDisplayText,
/// TransmissionStatus) is already [ObservableProperty]-backed on the same ChannelCardViewModel
/// instances placed into DisplayedChannels, so further changes flow through normal Avalonia
/// binding once a channel is in the collection -- this VM only needs to track membership
/// (add/remove) as locations/channels come and go or connect/disconnect.</summary>
public partial class RadioOverlayViewModel : ViewModelBase
{
    private readonly ChannelCardListViewModel _channelList;

    [ObservableProperty]
    public partial ObservableCollection<ChannelCardViewModel> DisplayedChannels { get; set; } = [];

    public RadioOverlayViewModel(ChannelCardListViewModel channelList)
    {
        _channelList = channelList;

        foreach (var location in _channelList.AllLocations)
            TrackLocation(location);

        RebuildDisplayedChannels();

        _channelList.AllLocations.CollectionChanged += OnAllLocationsChanged;
    }

    private void OnAllLocationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (LocationViewModel location in e.OldItems)
                UntrackLocation(location);

        if (e.NewItems != null)
            foreach (LocationViewModel location in e.NewItems)
                TrackLocation(location);

        PostRebuildDisplayedChannels();
    }

    private void TrackLocation(LocationViewModel location)
    {
        location.Channels.CollectionChanged += OnLocationChannelsChanged;
        foreach (var channel in location.Channels)
            TrackChannel(channel);
    }

    private void UntrackLocation(LocationViewModel location)
    {
        location.Channels.CollectionChanged -= OnLocationChannelsChanged;
        foreach (var channel in location.Channels)
            UntrackChannel(channel);
    }

    private void OnLocationChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ChannelCardViewModel channel in e.OldItems)
                UntrackChannel(channel);

        if (e.NewItems != null)
            foreach (ChannelCardViewModel channel in e.NewItems)
                TrackChannel(channel);

        PostRebuildDisplayedChannels();
    }

    private void TrackChannel(ChannelCardViewModel channel) =>
        channel.PropertyChanged += OnChannelPropertyChanged;

    private void UntrackChannel(ChannelCardViewModel channel) =>
        channel.PropertyChanged -= OnChannelPropertyChanged;

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelCardViewModel.ConnectionStatus)) return;
        PostRebuildDisplayedChannels();
    }

    /// <summary>Mirrors ChannelCardListViewModel.ApplyDcsPttHotkeysOnUiThread's CheckAccess-gated
    /// pattern -- runs the rebuild synchronously when already on the UI thread (or when no
    /// dispatcher loop owns this thread at all, e.g. a unit test host), and marshals via Post only
    /// when actually called from a background thread.</summary>
    private void PostRebuildDisplayedChannels()
    {
        if (Dispatcher.UIThread.CheckAccess())
            RebuildDisplayedChannels();
        else
            Dispatcher.UIThread.Post(RebuildDisplayedChannels);
    }

    private void RebuildDisplayedChannels()
    {
        var connected = _channelList.AllLocations
            .SelectMany(l => l.Channels)
            .Where(c => c.ConnectionStatus != Channel.ChannelConnectionStatus.Disconnected)
            .ToList();

        for (var i = DisplayedChannels.Count - 1; i >= 0; i--)
            if (!connected.Contains(DisplayedChannels[i]))
                DisplayedChannels.RemoveAt(i);

        foreach (var channel in connected)
            if (!DisplayedChannels.Contains(channel))
                DisplayedChannels.Add(channel);
    }

    public void Dispose()
    {
        _channelList.AllLocations.CollectionChanged -= OnAllLocationsChanged;
        foreach (var location in _channelList.AllLocations)
            UntrackLocation(location);
    }
}
