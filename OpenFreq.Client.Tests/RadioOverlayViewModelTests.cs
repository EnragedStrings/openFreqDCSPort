using OpenFreq.Client.Models;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for <see cref="RadioOverlayViewModel"/>'s live flatten/filter projection over
/// ChannelCardListViewModel.AllLocations -- see the radio overlay plan for why it flattens across
/// every location rather than just SelectedLocation.
/// </summary>
public class RadioOverlayViewModelTests
{
    [Fact]
    public void Constructor_OnlyIncludesConnectedChannelsAcrossAllLocations()
    {
        var channelList = VmFactory.ChannelCardList();
        var location1 = channelList.CreateLocation("Loc1", RadioStationPresets.FighterF16,
            RadioStationData.RadioStationType.STATIONARY);
        var location2 = channelList.CreateLocation("Loc2", RadioStationPresets.FighterF16,
            RadioStationData.RadioStationType.STATIONARY);

        var connected1 = location1.CreateChannel(251_000, "Connected1", isInEditMode: false);
        connected1.ConnectionStatus = Channel.ChannelConnectionStatus.Connected;

        var disconnected = location1.CreateChannel(252_000, "Disconnected", isInEditMode: false);
        // Stays Disconnected (the default).

        var connected2 = location2.CreateChannel(253_000, "Connected2", isInEditMode: false);
        connected2.ConnectionStatus = Channel.ChannelConnectionStatus.Connected;

        var overlay = new RadioOverlayViewModel(channelList);

        Assert.Contains(connected1, overlay.DisplayedChannels);
        Assert.Contains(connected2, overlay.DisplayedChannels);
        Assert.DoesNotContain(disconnected, overlay.DisplayedChannels);
        Assert.Equal(2, overlay.DisplayedChannels.Count);
    }

    [Fact]
    public void AddingLocation_UpdatesDisplayedChannels()
    {
        var channelList = VmFactory.ChannelCardList();
        var overlay = new RadioOverlayViewModel(channelList);

        var location = channelList.CreateLocation("Loc1", RadioStationPresets.FighterF16,
            RadioStationData.RadioStationType.STATIONARY);
        var channel = location.CreateChannel(251_000, "Ch1", isInEditMode: false);
        channel.ConnectionStatus = Channel.ChannelConnectionStatus.Connected;

        Assert.Contains(channel, overlay.DisplayedChannels);
    }

    [Fact]
    public void RemovingLocation_RemovesItsChannelsFromDisplayedChannels()
    {
        var channelList = VmFactory.ChannelCardList();
        var location = channelList.CreateLocation("Loc1", RadioStationPresets.FighterF16,
            RadioStationData.RadioStationType.STATIONARY);
        var channel = location.CreateChannel(251_000, "Ch1", isInEditMode: false);
        channel.ConnectionStatus = Channel.ChannelConnectionStatus.Connected;

        var overlay = new RadioOverlayViewModel(channelList);
        Assert.Contains(channel, overlay.DisplayedChannels);

        channelList.AllLocations.Remove(location);

        Assert.DoesNotContain(channel, overlay.DisplayedChannels);
    }

    [Fact]
    public void ChannelConnecting_AddsItToDisplayedChannels()
    {
        var channelList = VmFactory.ChannelCardList();
        var location = channelList.CreateLocation("Loc1", RadioStationPresets.FighterF16,
            RadioStationData.RadioStationType.STATIONARY);
        var channel = location.CreateChannel(251_000, "Ch1", isInEditMode: false);

        var overlay = new RadioOverlayViewModel(channelList);
        Assert.DoesNotContain(channel, overlay.DisplayedChannels);

        channel.ConnectionStatus = Channel.ChannelConnectionStatus.Connected;

        Assert.Contains(channel, overlay.DisplayedChannels);
    }

    [Fact]
    public void ChannelDisconnecting_RemovesItFromDisplayedChannels()
    {
        var channelList = VmFactory.ChannelCardList();
        var location = channelList.CreateLocation("Loc1", RadioStationPresets.FighterF16,
            RadioStationData.RadioStationType.STATIONARY);
        var channel = location.CreateChannel(251_000, "Ch1", isInEditMode: false);
        channel.ConnectionStatus = Channel.ChannelConnectionStatus.Connected;

        var overlay = new RadioOverlayViewModel(channelList);
        Assert.Contains(channel, overlay.DisplayedChannels);

        channel.ConnectionStatus = Channel.ChannelConnectionStatus.Disconnected;

        Assert.DoesNotContain(channel, overlay.DisplayedChannels);
    }
}
