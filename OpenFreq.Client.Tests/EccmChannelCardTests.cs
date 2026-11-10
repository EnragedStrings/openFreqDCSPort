using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using OpenFreqClient.Json;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Services.Eccm;
using OpenFreq.Common.Eccm;
using OpenFreqAudio.Eccm;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>HAVE QUICK / SINCGARS on the channel card and in the DCS export packet (docs/jamming/HAVE_QUICK.md).</summary>
public class EccmChannelCardTests
{
    private const int Freq = 251_000;

    private static ChannelCardViewModel CreateVm() => new(
        Substitute.For<IOpenFreqService>(),
        Substitute.For<IHotkeyService>(),
        name: "Ch1",
        frequencyKhz: Freq,
        isInEditMode: false,
        radioStationData: ServiceHarness.NewRadioStation(),
        parentLocationViewModel: null!,
        settings: null!);

    private sealed class Recorder : IDisposable
    {
        public List<ChannelEccmUpdateMessage> Eccm { get; } = [];
        public List<ChannelUpdatedMessage> Updated { get; } = [];
        private readonly Guid _id;

        public Recorder(Guid id)
        {
            _id = id;
            WeakReferenceMessenger.Default.Register<ChannelEccmUpdateMessage>(this, (_, m) =>
            {
                if (m.ChannelId == _id) Eccm.Add(m);
            });
            WeakReferenceMessenger.Default.Register<ChannelUpdatedMessage>(this, (_, m) =>
            {
                if (m.ChannelId == _id) Updated.Add(m);
            });
        }

        public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    [Fact]
    public void ManualHaveQuick_MovesTheCardToTheNetsChannel_AndBack()
    {
        var vm = CreateVm();
        using var rec = new Recorder(vm.Id);

        vm.EccmMode = HopMode.HaveQuick;
        vm.EccmNet = "A12.325";

        Assert.Equal(237_325, vm.FrequencyKhz);
        Assert.True(vm.HqOn);
        var last = rec.Eccm[^1];
        Assert.Equal(237_325, last.FrequencyKhz);
        Assert.Equal(HopMode.HaveQuick, last.Settings.Mode);
        Assert.Equal("A12.325", last.Settings.Net);
        Assert.Contains(rec.Updated, u => u.NewFrequencyKhz == 237_325 && u.OldFrequencyKhz == Freq);

        vm.EccmManualKey = "300.050 251.000 277.775 312.600 225.025 399.975";
        Assert.Equal("300.050 251.000 277.775 312.600 225.025 399.975", rec.Eccm[^1].Settings.ManualWod);
        Assert.Null(rec.Eccm[^1].Settings.ManualSincgarsKey);

        vm.EccmMode = HopMode.None;
        Assert.Equal(Freq, vm.FrequencyKhz);
        Assert.False(vm.HqOn);
    }

    [Fact]
    public void TodControls_FeedTheSettings()
    {
        var vm = CreateVm();
        using var rec = new Recorder(vm.Id);
        vm.EccmMode = HopMode.Sincgars;
        vm.EccmTodValid = false;
        Assert.False(rec.Eccm[^1].Settings.TodValid);
        vm.EccmTodOffsetSeconds = 2.5;
        vm.ReceiveTod();
        Assert.True(rec.Eccm[^1].Settings.TodValid);
        Assert.Equal(0, rec.Eccm[^1].Settings.TodOffsetMs);
    }

    [Fact]
    public void StatusMessage_ShowsNetAndReadiness()
    {
        var vm = CreateVm();
        var resolution = EccmResolver.Resolve(new EccmSlotSettings { Mode = HopMode.HaveQuick, Net = "A12.325" }, null,
            DateTime.UtcNow);
        WeakReferenceMessenger.Default.Send(new EccmSlotStatusMessage(vm.Id, resolution));
        Assert.Equal("HQ A12.325", vm.EccmBadgeText);
        Assert.Equal("NO WOD", vm.EccmReadinessText);
        Assert.Contains("NATO", vm.EccmDescription);

        WeakReferenceMessenger.Default.Send(new EccmSlotStatusMessage(vm.Id, EccmResolution.Off));
        Assert.False(vm.HasEccmBadge);
    }

    [Fact]
    public void CockpitEccm_DrivesTheCard_UntilItStopsReporting()
    {
        var vm = CreateVm();
        using var rec = new Recorder(vm.Id);
        var cockpit = new DcsEccmState { Mode = "hq2", Net = "A20.400", Status = "NO TIME", Source = "cockpit" };

        vm.ApplyCockpitEccm(cockpit.ToSettings());
        Assert.Equal(EccmSource.Cockpit, vm.EccmSource);
        Assert.False(vm.ShowManualEccmControls);
        Assert.Equal("NO TIME", rec.Eccm[^1].Settings.CockpitStatus);
        var sends = rec.Eccm.Count;
        vm.ApplyCockpitEccm(cockpit.ToSettings()); // unchanged: nothing re-sent
        Assert.Equal(sends, rec.Eccm.Count);

        vm.ApplyCockpitEccm(null);
        Assert.Equal(HopMode.None, vm.EccmMode);
        Assert.Equal(EccmSource.Manual, vm.EccmSource);
    }

    [Theory]
    [InlineData("hq1", HopMode.HaveQuick)]
    [InlineData("hq2", HopMode.HaveQuick)]
    [InlineData("sg", HopMode.Sincgars)]
    [InlineData("sg_cue", HopMode.Sincgars)]
    [InlineData("off", HopMode.None)]
    public void DcsEccmState_MapsModes(string mode, HopMode expected) =>
        Assert.Equal(expected, new DcsEccmState { Mode = mode }.HopMode);

    [Fact]
    public void DcsPacket_ParsesTheEccmField()
    {
        const string json = """
            {"schema":"openfreq.dcs.export","version":1,"radios":[
              {"slot":1,"name":"ARC-210","frequencyHz":251000000,"hqOn":true,
               "eccm":{"mode":"hq2","net":"A12.300","status":"","eccmChannel":1,"source":"cockpit"}},
              {"slot":2,"name":"ARC-164 UHF","frequencyHz":243000000,"hqOn":true,
               "eccm":{"mode":"hq2","net":"A12.325","source":"knob"}},
              {"slot":3,"name":"ARC-186 VHF FM","frequencyHz":30000000}
            ]}
            """;
        var packet = JsonSerializer.Deserialize(json, ClientJsonContext.Default.DcsExportPacket)!;
        var arc210 = packet.Radios[0].Eccm!;
        Assert.Equal("A12.300", arc210.Net);
        var s = arc210.ToSettings();
        Assert.Equal(EccmSource.Cockpit, s.Source);
        Assert.Equal("", s.CockpitStatus);
        Assert.True(EccmResolver.Resolve(s, null, DateTime.UtcNow).IsReady); // ready cockpit, default fill

        var knob = packet.Radios[1].Eccm!.ToSettings();
        Assert.Equal(EccmSource.Knob, knob.Source);
        Assert.Null(knob.CockpitStatus);
        Assert.Null(packet.Radios[2].Eccm);

        var clone = packet.Radios[0].Clone();
        Assert.Equal(packet.Radios[0].Eccm, clone.Eccm);
        Assert.NotSame(packet.Radios[0].Eccm, clone.Eccm);
    }
}
