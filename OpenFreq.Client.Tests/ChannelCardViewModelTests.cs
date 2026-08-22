using CommunityToolkit.Mvvm.Messaging;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for <see cref="ChannelCardViewModel"/> (without actually displaying it).
/// </summary>
public class ChannelCardViewModelTests
{
    private const int Freq = 251_000;

    private static ChannelCardViewModel CreateVm(int frequencyKhz = Freq)
        => new(
            Substitute.For<IOpenFreqService>(),
            Substitute.For<IHotkeyService>(),
            name: "Ch1",
            frequencyKhz: frequencyKhz,
            isInEditMode: false,
            radioStationData: ServiceHarness.NewRadioStation(),
            parentLocationViewModel: null!,
            settings: null!);

    [Fact]
    public void SignalStrengthMessage_MatchingFrequency_UpdatesProperties()
    {
        var vm = CreateVm();

        WeakReferenceMessenger.Default.Send(
            new SignalStrengthTracker.SignalStrengthUpdateMessage(Freq, strengthPercent: 0.75f, snrDb: 12.5f,
                receivedDb: -75f));

        Assert.Equal(0.75, vm.SignalStrengthPercent, precision: 3);
        Assert.Equal(12.5, vm.SignalStrengthSnrDb, precision: 3);
        Assert.Equal(-75, vm.SignalStrengthReceivedDb, precision: 3);
    }

    [Fact]
    public void SignalStrengthMessage_DifferentFrequency_Ignored()
    {
        var vm = CreateVm(Freq);

        WeakReferenceMessenger.Default.Send(
            new SignalStrengthTracker.SignalStrengthUpdateMessage(Freq + 1000, strengthPercent: 0.9f, snrDb: 20f,
                receivedDb: -60f));

        Assert.Equal(0, vm.SignalStrengthPercent);
        Assert.Equal(0, vm.SignalStrengthSnrDb);
        Assert.Equal(0, vm.SignalStrengthReceivedDb);
    }

    [Fact]
    public void EnablingManualSatcomMode_SetsVirtualFrequencyAndDisplayText()
    {
        var vm = CreateVm();

        vm.IsManualSatcomMode = true;

        Assert.Equal(ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(1), vm.FrequencyKhz);
        Assert.Equal("SATCOM VOICE", vm.FrequencyDisplayText);
        Assert.Equal("SATCOM CH 1", vm.SatcomSubtitleText);
    }

    [Fact]
    public void DisablingManualSatcomMode_RestoresPriorFrequency()
    {
        var vm = CreateVm(Freq);

        vm.IsManualSatcomMode = true;
        vm.IsManualSatcomMode = false;

        Assert.Equal(Freq, vm.FrequencyKhz);
        Assert.Equal($"{Freq / 1000d:F3} MHz", vm.FrequencyDisplayText);
    }

    [Fact]
    public void NextSatcomChannel_WrapsFrom6To1()
    {
        var vm = CreateVm();
        vm.IsManualSatcomMode = true;
        vm.ManualSatcomChannel = 6;

        vm.NextSatcomChannelCommand.Execute(null);

        Assert.Equal(1, vm.ManualSatcomChannel);
        Assert.Equal(ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(1), vm.FrequencyKhz);
    }

    [Fact]
    public void PreviousSatcomChannel_WrapsFrom1To6()
    {
        var vm = CreateVm();
        vm.IsManualSatcomMode = true;
        vm.ManualSatcomChannel = 1;

        vm.PreviousSatcomChannelCommand.Execute(null);

        Assert.Equal(6, vm.ManualSatcomChannel);
        Assert.Equal(ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(6), vm.FrequencyKhz);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void GetSatcomVirtualFrequencyKhz_ProducesDistinctFrequenciesPerChannel(int channel)
    {
        var freq = ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(channel);

        Assert.Equal(999_000 + channel, freq);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public void GetSatcomVirtualFrequencyKhz_OutOfRangeFallsBackToChannel1(int channel)
    {
        Assert.Equal(ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(1),
            ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz(channel));
    }
}
