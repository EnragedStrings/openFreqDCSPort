using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Client.Models;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Client.Services.Jamming;
using OpenFreq.Common;
using OpenFreq.Common.Jamming;
using OpenFreqAudio;
using OpenFreqAudio.Jamming;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// JammingService against a real OpenFreqService (ServiceHarness): a placed jammer reaches playback as a
/// JammerContribution with the expected J/N and carrier offset; out-of-band, LOS-blocked and SATCOM slots get none.
/// </summary>
public class JammingServiceTests
{
    private const int Freq = 251_000;
    private static readonly Guid Slot = Guid.NewGuid();

    private DateTime _now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<(int Freq, Guid Slot, IReadOnlyList<JammerContribution> List)> _pushed = [];

    private async Task<(ServiceHarness H, JammingService J)> DcsReceiverAsync()
    {
        var h = new ServiceHarness();
        await h.InitializeAsync(IOpenFreqService.Mode.DCS);
        h.Client.IsAuthenticated.Returns(true);
        h.Dcs.State.Returns(ServiceState.Connected);
        h.Dcs.Position.Returns(new DcsVector3(0, 3000, 0)); // FL100 at the map origin
        h.Dcs.Latitude.Returns(42.0);
        h.Dcs.Longitude.Returns(42.0);
        h.Dcs.AltitudeMsl.Returns(3000.0);

        var station = new RadioStationData
        {
            Type = RadioStationData.RadioStationType.DCS, Preset = RadioStationPresets.FighterF16, Ppm = 0
        };
        await h.Service.JoinFrequencyAsync(Freq, Slot, station);

        h.Playback.When(p => p.SetSlotInterference(Arg.Any<int>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<JammerContribution>>()))
            .Do(ci => _pushed.Add((ci.ArgAt<int>(0), ci.ArgAt<Guid>(1),
                ci.ArgAt<IReadOnlyList<JammerContribution>>(2))));

        return (h, new JammingService(h.Service, h.Dcs, NullLogger<JammingService>.Instance, () => _now));
    }

    private static JammerCatalogEntryDto Entry(JammerDefinition definition)
    {
        var resolved = JammerPresets.Resolve(definition);
        return new JammerCatalogEntryDto
        {
            Definition = resolved, Active = true, Emissions = ScriptedTargeting.Emissions(resolved)
        };
    }

    /// <summary>R-934B, omni, 100 km north of the receiver, spotting <paramref name="spotKhz"/>.</summary>
    private static JammerDefinition R934b(int spotKhz = Freq) => new()
    {
        Id = "sinitsa-1", PresetId = "r934b", DcsPosition = new JammerVector3(100_000, 12, 0),
        Targeting = new JammerTargeting { SpotFrequenciesKhz = [spotKhz] }
    };

    private static void SendCatalog(ServiceHarness h, params JammerCatalogEntryDto[] entries) =>
        h.Client.JammerCatalogReceived += Raise.EventWith(h.Client,
            new JammerCatalogEventArgs(new JammerCatalogUpdateMessage { Jammers = [.. entries] }));

    private static double ExpectedJnr(double distanceMeters)
    {
        // EIRP 500 W + 8 dBi − 2 dB, free space, 16 kHz of the 25 kHz spot, F-16 UHF sensitivity
        var eirp = 10 * Math.Log10(500 * 1000) + 8 - 2;
        var fspl = 20 * Math.Log10(4 * Math.PI * distanceMeters * Freq * 1000.0 / 299_792_458.0);
        var rxSens = RadioStationPresets.FighterF16.GetRxSensitivity(BackgroundNoiseGenerator.RadioType.UHF_AM);
        return eirp - fspl + 10 * Math.Log10(16.0 / 25.0) - rxSens;
    }

    [Fact]
    public async Task PlacedJammer_ReachesPlayback_WithExpectedJnrAndOffset()
    {
        var (h, jamming) = await DcsReceiverAsync();
        SendCatalog(h, Entry(R934b()));

        jamming.Tick();

        var (freq, slot, list) = Assert.Single(_pushed);
        Assert.Equal(Freq, freq);
        Assert.Equal(Slot, slot);
        var c = Assert.Single(list);
        Assert.Equal("sinitsa-1", c.JammerId);
        var distance = Math.Sqrt(100_000.0 * 100_000.0 + 2988.0 * 2988.0);
        Assert.Equal(ExpectedJnr(distance), c.JnrDb, 1);
        Assert.Equal(JammingMath.CarrierToleranceOffsetHz("sinitsa-1", 300), c.CarrierOffsetHz, 1);
        Assert.Equal(JammerTechnique.Noise, c.Technique);
        Assert.Equal(JammingMath.StableSeed("sinitsa-1"), c.Params.Seed);
        Assert.Equal(1f, c.HopHitFraction);

        var status = jamming.GetSlotStatus(Freq, Slot)!;
        Assert.Equal(1, status.JammerCount);
        Assert.Equal(c.JnrDb, status.MaxJnrDb!.Value, 3);
        h.Dcs.Received().RequestLineOfSight("jam:sinitsa-1", Arg.Any<DcsVector3>());
    }

    [Fact]
    public async Task OutOfBandJammer_GivesNothing_AndClearsAnEarlierContribution()
    {
        var (h, jamming) = await DcsReceiverAsync();
        SendCatalog(h, Entry(R934b(spotKhz: 260_000)));
        jamming.Tick();
        Assert.Empty(_pushed); // nothing was ever on this slot: no call at all

        SendCatalog(h, Entry(R934b()));
        jamming.Tick();
        Assert.Single(_pushed[^1].List);

        SendCatalog(h, Entry(R934b(spotKhz: 251_025))); // adjacent channel
        jamming.Tick();
        Assert.Empty(_pushed[^1].List);
    }

    [Fact]
    public async Task LosBlocked_GivesEmptyList_PartialLosAttenuates()
    {
        var (h, jamming) = await DcsReceiverAsync();
        SendCatalog(h, Entry(R934b()));
        jamming.Tick();
        var clear = Assert.Single(_pushed[^1].List).JnrDb;

        h.Dcs.RequestLineOfSight("jam:sinitsa-1", Arg.Any<DcsVector3>())
            .Returns(new DcsLineOfSightResult { TerrainAvailable = true, Loss = 0.25, ReceivedUtc = _now });
        _now += JammingService.PathRefreshInterval;
        jamming.Tick();
        Assert.Equal(clear - 15.0, Assert.Single(_pushed[^1].List).JnrDb, 1);

        h.Dcs.RequestLineOfSight("jam:sinitsa-1", Arg.Any<DcsVector3>())
            .Returns(new DcsLineOfSightResult { TerrainAvailable = true, Loss = 1.0, ReceivedUtc = _now });
        _now += JammingService.PathRefreshInterval;
        jamming.Tick();
        Assert.Empty(_pushed[^1].List);
    }

    [Fact]
    public async Task SatcomSlot_IsSkipped()
    {
        var (h, jamming) = await DcsReceiverAsync();
        SendCatalog(h, Entry(R934b()));
        jamming.Tick();
        Assert.Single(_pushed[^1].List);

        h.Service.SetSatcomState(Freq, Slot, isActive: true, frameErrorRate: 0, burstSeverity: 0);
        jamming.Tick();
        Assert.Empty(_pushed[^1].List);
        _pushed.Clear();
        jamming.Tick();
        Assert.Empty(_pushed); // stays off without re-sending empties
    }

    [Fact]
    public async Task InactiveJammer_GivesNothing()
    {
        var (h, jamming) = await DcsReceiverAsync();
        var entry = Entry(R934b());
        entry.Active = false;
        entry.Emissions = [];
        SendCatalog(h, entry);
        jamming.Tick();
        Assert.Empty(_pushed);
    }

    [Fact]
    public async Task LatLonJammer_UsesDcsConversionWhenAvailable_ElseGeodeticFreeSpace()
    {
        var (h, jamming) = await DcsReceiverAsync();
        var def = new JammerDefinition
        {
            Id = "geo", PresetId = "r934b", Latitude = 42.9, Longitude = 42.0,
            Targeting = new JammerTargeting { SpotFrequenciesKhz = [Freq] }
        };
        SendCatalog(h, Entry(def));

        // no LL->LO answer yet: great-circle free space from the receiver's own lat/lon
        jamming.Tick();
        var geodetic = Assert.Single(_pushed[^1].List).JnrDb;
        var gcDistance = FreeSpacePathModel.GreatCircleDistanceMeters(42.9, 42.0, 12, 42.0, 42.0, 3000);
        Assert.Equal(ExpectedJnr(gcDistance), geodetic, 1);
        h.Dcs.Received().ConvertLatLonToLocal("jam:geo", 42.9, 42.0, null, 12.0);

        // DCS answered: DCS-local geometry from now on
        h.Dcs.ConvertLatLonToLocal("jam:geo", 42.9, 42.0, null, 12.0).Returns(new DcsVector3(50_000, 12, 0));
        _now += JammingService.PathRefreshInterval;
        jamming.Tick();
        var local = Assert.Single(_pushed[^1].List).JnrDb;
        Assert.Equal(ExpectedJnr(Math.Sqrt(50_000.0 * 50_000.0 + 2988.0 * 2988.0)), local, 1);
    }

    [Fact]
    public async Task BarrageOverTheReceiver_IsDilutedByItsWidth()
    {
        var (h, jamming) = await DcsReceiverAsync();
        var def = R934b();
        def.Targeting = new JammerTargeting { Bands = [new JammerBand(250_000, 260_000)] };
        SendCatalog(h, Entry(def));
        jamming.Tick();

        var c = Assert.Single(_pushed[^1].List);
        var distance = Math.Sqrt(100_000.0 * 100_000.0 + 2988.0 * 2988.0);
        // spot keeps 16/25 of its power in the passband; a 10 MHz barrage keeps 16/10000
        Assert.Equal(ExpectedJnr(distance) - 10 * Math.Log10(10_000.0 / 25.0), c.JnrDb, 1);
        Assert.Equal(0f, c.CarrierOffsetHz, 3); // no carrier for a barrage slice centred on the passband
    }

    [Theory]
    [InlineData(null, null, false, JammingBadge.None)]
    [InlineData(-10.0, null, false, JammingBadge.None)]
    [InlineData(5.0, null, false, JammingBadge.Interference)]
    [InlineData(25.0, null, false, JammingBadge.Jammed)]
    [InlineData(20.0, 25.0, false, JammingBadge.Interference)] // J/S −5: gets through
    [InlineData(25.0, 25.0, false, JammingBadge.Jammed)] // J/S 0 on AM: denied
    [InlineData(27.0, 25.0, true, JammingBadge.Interference)] // J/S +2 on FM: below capture
    [InlineData(28.0, 25.0, true, JammingBadge.Jammed)]
    public void ClassifyBadge_UsesJsAgainstTheSignal(double? jnr, double? snr, bool fm, JammingBadge expected)
    {
        Assert.Equal(expected, JammingService.ClassifyBadge(jnr, snr, fm));
    }

    [Fact]
    public void ChannelCard_ShowsBadgeFromJammingStatus()
    {
        var vm = new ChannelCardViewModel(Substitute.For<IOpenFreqService>(), Substitute.For<IHotkeyService>(),
            "Ch1", Freq, false, ServiceHarness.NewRadioStation(), null!, null!);
        Assert.Null(vm.JammingText);

        WeakReferenceMessenger.Default.Send(new JammingStatusMessage(Freq, vm.Id, 30, 1, "sinitsa-1", false));
        Assert.Equal("JAMMED", vm.JammingText);

        // a strong talker arrives: J/S 30 − 40 = −10, audible but not denied
        vm.SignalStrengthSnrDb = 40;
        vm.TransmissionStatus = Channel.ChannelTransmissionStatus.Receiving;
        Assert.Equal("INTERFERENCE", vm.JammingText);

        WeakReferenceMessenger.Default.Send(new JammingStatusMessage(Freq, vm.Id, null, 0, null, false));
        Assert.Null(vm.JammingText);
        Assert.False(vm.HasJammingText);
        vm.Dispose();
    }
}
