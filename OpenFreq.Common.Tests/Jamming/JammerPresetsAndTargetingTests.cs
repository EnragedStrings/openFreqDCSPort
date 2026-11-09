using System.Text.Json;
using OpenFreq.Common.Jamming;
using OpenFreq.Common.Signaling;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Tests.Jamming;

public class JammerPresetsTests
{
    [Fact]
    public void AllNineteenResearchPresetsExist()
    {
        string[] ids =
        [
            "r934b", "r934um2", "r330b", "r378", "pelena6", "leer2", "palantin", "tirada_uhf", "il22pp", "mi8ppa",
            "ec130h", "ea37b", "ea6b_usq113", "ea18g_lb", "quickfix", "tlq17a", "ulq19", "mlq34", "generic_manpack"
        ];
        Assert.Equal(19, JammerPresets.All.Count);
        Assert.Equal(ids.Order(), JammerPresets.All.Select(p => p.Id).Order());
        Assert.All(JammerPresets.All, p => Assert.True(p.MinFrequencyKhz < p.MaxFrequencyKhz));
    }

    [Theory]
    [InlineData("r934b", 33)]
    [InlineData("r330b", 34)]
    [InlineData("r378", 30)]
    [InlineData("pelena6", 18)]
    [InlineData("leer2", 24)]
    [InlineData("palantin", 36)]
    [InlineData("tirada_uhf", 50)]
    [InlineData("il22pp", 37)]
    [InlineData("mi8ppa", 30)]
    [InlineData("ec130h", 43)]
    [InlineData("ea37b", 45)]
    [InlineData("ea6b_usq113", 38)]
    [InlineData("ea18g_lb", 38)]
    [InlineData("quickfix", 28)]
    [InlineData("tlq17a", 27)]
    [InlineData("ulq19", 24)]
    [InlineData("mlq34", 39)]
    [InlineData("generic_manpack", 15)]
    [InlineData("r934um2", 34)]
    public void SpotEirp_MatchesResearchTableWithinHalfADb(string id, double eirpDbw)
    {
        Assert.InRange(JammerPresets.Find(id)!.SpotEirpDbw, eirpDbw - 0.6, eirpDbw + 0.6);
    }

    [Fact]
    public void EveryPresetIsFlaggedEstimated_BecauseAntennaGainIsAlwaysAnEstimate()
    {
        Assert.All(JammerPresets.All, p =>
        {
            Assert.True(p.Estimated);
            Assert.Contains("AntennaGainDbi", p.EstimatedFigures);
        });
        // sourced transmitter powers are not flagged
        Assert.DoesNotContain("TxPowerW", JammerPresets.Find("r330b")!.EstimatedFigures);
        Assert.Contains("TxPowerW", JammerPresets.Find("ec130h")!.EstimatedFigures);
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndUnknownIsNull()
    {
        Assert.NotNull(JammerPresets.Find("R934B"));
        Assert.Null(JammerPresets.Find("nope"));
        Assert.Null(JammerPresets.Find(null));
    }

    [Fact]
    public void Resolve_FillsFromPreset_AndConfigOverridesWin()
    {
        var resolved = JammerPresets.Resolve(new JammerDefinition
        {
            Id = "j1", PresetId = "r934b", TxPowerW = 200, Pattern = JammerAntennaPattern.Sector, AzimuthDeg = 270
        });

        Assert.Equal(200, resolved.TxPowerW);
        Assert.Equal(8, resolved.AntennaGainDbi);
        Assert.Equal(2, resolved.FeederLossDb);
        Assert.Equal(3, resolved.PowerAmplifiers);
        Assert.Equal(100_000, resolved.MinFrequencyKhz);
        Assert.Equal(400_000, resolved.MaxFrequencyKhz);
        Assert.Equal(JammerAntennaPattern.Sector, resolved.Pattern);
        Assert.Equal(270, resolved.AzimuthDeg);
        Assert.Equal(JammerPresets.DefaultBeamwidthDeg, resolved.BeamwidthDeg);
        Assert.Equal(JammerTechnique.Noise, resolved.Technique);
        Assert.Equal(12, resolved.HeightAglMeters);
        Assert.Equal("j1", resolved.Name);
        Assert.False(resolved.Responsive);
    }

    [Fact]
    public void Resolve_UnknownPreset_UsesGeneric_AndDoesNotMutateInput()
    {
        var input = new JammerDefinition { Id = "x", PresetId = "nope" };
        var resolved = JammerPresets.Resolve(input);
        Assert.Equal(JammerPresets.Generic.TxPowerW, resolved.TxPowerW);
        Assert.Null(input.TxPowerW);
    }
}

public class ScriptedTargetingTests
{
    private static JammerDefinition Resolve(Action<JammerTargeting> target, string preset = "r934b",
        int? pas = null)
    {
        var def = new JammerDefinition { Id = "j", PresetId = preset, PowerAmplifiers = pas };
        target(def.Targeting);
        return JammerPresets.Resolve(def);
    }

    [Fact]
    public void Spots_UpToPaCount_EachGetFullPower()
    {
        var e = ScriptedTargeting.Emissions(Resolve(t => t.SpotFrequenciesKhz = [251_000, 260_000]));
        Assert.Equal(2, e.Count);
        Assert.All(e, x =>
        {
            Assert.Equal(25.0, x.BandwidthKhz);
            Assert.Equal(1.0, x.PowerFraction);
        });
    }

    [Fact]
    public void Spots_BeyondMaxSpots_AreDropped_AndShareThePas()
    {
        var e = ScriptedTargeting.Emissions(Resolve(t => t.SpotFrequenciesKhz = [251_000, 252_000, 253_000, 254_000],
            pas: 1));
        Assert.Equal(3, e.Count); // r934b holds 3 spots
        Assert.All(e, x => Assert.Equal(1.0 / 3.0, x.PowerFraction, 6));
    }

    [Fact]
    public void Spots_OutsideTuningRange_OrProtected_AreDropped()
    {
        var e = ScriptedTargeting.Emissions(Resolve(t =>
        {
            t.SpotFrequenciesKhz = [50_000, 251_000, 262_000];
            t.ProtectedFrequenciesKhz = [262_000];
        }));
        Assert.Equal([251_000.0], e.Select(x => x.CenterKhz));
    }

    [Fact]
    public void ExplicitSpotOnGuard_IsJammed()
    {
        var e = ScriptedTargeting.Emissions(Resolve(t => t.SpotFrequenciesKhz = [243_000]));
        Assert.Single(e);
    }

    [Fact]
    public void Band_IsClippedToRange_AndGuardIsNotchedByDefault()
    {
        var e = ScriptedTargeting.Emissions(Resolve(t => t.Bands = [new JammerBand(225_000, 450_000)]));
        Assert.Equal(2, e.Count);
        Assert.Equal(225_000, e[0].StartKhz, 6);
        Assert.Equal(242_987.5, e[0].EndKhz, 6);
        Assert.Equal(243_012.5, e[1].StartKhz, 6);
        Assert.Equal(400_000, e[1].EndKhz, 6);
        Assert.Equal(1.0, e.Sum(x => x.PowerFraction), 6);
        Assert.Equal(0.0, JammingMath.InBandFraction(e[0].CenterKhz, e[0].BandwidthKhz, 243_000, 16)
                          + JammingMath.InBandFraction(e[1].CenterKhz, e[1].BandwidthKhz, 243_000, 16), 6);
    }

    [Fact]
    public void Band_GuardProtectionOff_CoversGuard()
    {
        var e = ScriptedTargeting.Emissions(Resolve(t =>
        {
            t.Bands = [new JammerBand(225_000, 260_000)];
            t.ProtectGuard = false;
        }));
        Assert.Single(e);
        Assert.Equal(35_000, e[0].BandwidthKhz, 6);
    }

    [Fact]
    public void AutoMode_RadiatesNothingInPhase1()
    {
        var e = ScriptedTargeting.Emissions(Resolve(t =>
        {
            t.Mode = JammerTargetingMode.Auto;
            t.SpotFrequenciesKhz = [251_000];
        }));
        Assert.Empty(e);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)] // start delay 10
    [InlineData(10, true)]
    [InlineData(39.9, true)] // on 30
    [InlineData(45, false)] // off 20
    [InlineData(60, true)]
    [InlineData(200, false)] // stop after 100
    public void Schedule_DelayCycleAndStop(double t, bool expected)
    {
        var schedule = new JammerSchedule
            { StartDelaySeconds = 10, OnSeconds = 30, OffSeconds = 20, StopAfterSeconds = 100 };
        Assert.True(new JammerSchedule().IsOn(TimeSpan.FromSeconds(t)));
        Assert.Equal(expected, schedule.IsOn(TimeSpan.FromSeconds(t)));
    }
}

public class JammerCatalogMessageTests
{
    [Fact]
    public void CatalogUpdate_RoundTripsThroughSourceGeneratedJson()
    {
        var def = JammerPresets.Resolve(new JammerDefinition
        {
            Id = "sam", PresetId = "r934b", Latitude = 42.1, Longitude = 41.9,
            DcsPosition = new JammerVector3(-1000, 300, 2000), Technique = JammerTechnique.SteppedTones,
            Pattern = JammerAntennaPattern.Sector,
            Targeting = new JammerTargeting { SpotFrequenciesKhz = [251_000], Bands = [new JammerBand(30_000, 31_000)] }
        });
        var message = SignalingMessageFactory.CreateJammerCatalogUpdate(
        [
            new JammerCatalogEntryDto
            {
                Definition = def, Active = true, Emissions = [new JammerEmission(251_000, 25, 1)]
            }
        ]);

        Assert.Equal(SignalingMessageTypes.JammerCatalogUpdate, message.Type);
        var json = JsonSerializer.Serialize(message, OpenFreqJsonContext.Default.SignalingMessage);
        Assert.Contains("\"technique\":\"SteppedTones\"", json);
        Assert.Contains("\"pattern\":\"Sector\"", json);

        var back = JsonSerializer.Deserialize(json, OpenFreqJsonContext.Default.SignalingMessage)!;
        var payload = SignalingMessageFactory.DeserializePayload<JammerCatalogUpdateMessage>(back.Payload)!;
        var entry = Assert.Single(payload.Jammers);
        Assert.True(entry.Active);
        Assert.Equal(251_000, Assert.Single(entry.Emissions).CenterKhz);
        Assert.Equal("sam", entry.Definition.Id);
        Assert.Equal(JammerTechnique.SteppedTones, entry.Definition.Technique);
        Assert.Equal(JammerAntennaPattern.Sector, entry.Definition.Pattern);
        Assert.Equal(-1000, entry.Definition.DcsPosition!.X);
        Assert.Equal(500, entry.Definition.TxPowerW);
        Assert.Equal(30_000, entry.Definition.Targeting.Bands[0].StartKhz);
    }
}
