using OpenFreq.Common.Jamming;
using OpenFreqAudio.Jamming;

namespace OpenFreq.Common.Tests.Jamming;

public class MissionJammerTagTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ural-375 #001")]
    [InlineData("jam:r934b")] // prefix is case-sensitive
    [InlineData("JAM:")]
    [InlineData("JAM:;mode=auto")]
    public void NonTagsAreNull(string? name) => Assert.Null(MissionJammerTag.Parse(name));

    [Fact]
    public void PresetWithKeys()
    {
        var tag = MissionJammerTag.Parse(
            "JAM:R934B;mode=auto;spot=251.0,243.000;band=225-250,300-320.5;autoband=225-400;protect=243;" +
            "power=250;gain=6;tech=tone;resp=1;name=Sinitsa 1")!;

        Assert.Equal("r934b", tag.PresetId);
        Assert.Null(tag.TemplateId);
        Assert.Empty(tag.Warnings);
        Assert.Equal(JammerTargetingMode.Auto, tag.Mode);

        var d = tag.BuildDefinition(null);
        Assert.Equal("r934b", d.PresetId);
        Assert.Equal([251_000, 243_000], d.Targeting.SpotFrequenciesKhz);
        Assert.Equal(2, d.Targeting.Bands.Count);
        Assert.Equal(300_000, d.Targeting.Bands[1].StartKhz);
        Assert.Equal(320_500, d.Targeting.Bands[1].EndKhz);
        Assert.Equal(400_000, d.Targeting.AutoBands[0].EndKhz);
        Assert.Equal([243_000], d.Targeting.ProtectedFrequenciesKhz);
        Assert.Equal(250, d.TxPowerW);
        Assert.Equal(6, d.AntennaGainDbi);
        Assert.Equal(JammerTechnique.Tone, d.Technique);
        Assert.True(d.Responsive);
        Assert.Equal("Sinitsa 1", d.Name);
        Assert.Equal(JammerTargetingMode.Auto, d.Targeting.Mode);
    }

    [Fact]
    public void DcsSuffixAfterSpaceOrHashIsIgnored()
    {
        Assert.Equal([251_000], MissionJammerTag.Parse("JAM:r934b;spot=251 #001")!.BuildDefinition(null)
            .Targeting.SpotFrequenciesKhz);
        Assert.Equal("r934b", MissionJammerTag.Parse("JAM:r934b#002")!.PresetId);
    }

    [Fact]
    public void SectorKeysSetThePattern()
    {
        var d = MissionJammerTag.Parse("JAM:r934b;az=-90;bw=60")!.BuildDefinition(null);
        Assert.Equal(JammerAntennaPattern.Sector, d.Pattern);
        Assert.Equal(270, d.AzimuthDeg);
        Assert.Equal(60, d.BeamwidthDeg);
    }

    [Fact]
    public void BadValuesAndUnknownKeysAreWarningsNotFailures()
    {
        var tag = MissionJammerTag.Parse("JAM:r934b;mode=loud;spot=abc;band=400-225;power=-5;colour=red;oops")!;
        Assert.Equal(6, tag.Warnings.Count);
        var d = tag.BuildDefinition(null);
        Assert.Empty(d.Targeting.SpotFrequenciesKhz);
        Assert.Empty(d.Targeting.Bands);
        Assert.Null(d.TxPowerW);
        Assert.Equal(JammerTargetingMode.Scripted, d.Targeting.Mode);
    }

    [Fact]
    public void TemplateIsCopiedAndOnlyTheNamedFieldsChange()
    {
        var template = new JammerDefinition
        {
            Id = "sinitsa", PresetId = "r934b", Template = true, TxPowerW = 400, Latitude = 1, Longitude = 2,
            Targeting = new JammerTargeting
            {
                Mode = JammerTargetingMode.Auto, AutoBands = [new JammerBand(225_000, 400_000)],
                ProtectedFrequenciesKhz = [251_000]
            }
        };
        var tag = MissionJammerTag.Parse("JAM:@sinitsa;spot=260")!;
        Assert.Equal("sinitsa", tag.TemplateId);

        var d = tag.BuildDefinition(template);
        Assert.False(d.Template);
        Assert.Equal("r934b", d.PresetId);
        Assert.Equal(400, d.TxPowerW);
        Assert.Equal(JammerTargetingMode.Auto, d.Targeting.Mode); // spot= alone does not reset auto mode
        Assert.Equal([260_000], d.Targeting.SpotFrequenciesKhz);
        Assert.Equal([251_000], d.Targeting.ProtectedFrequenciesKhz);
        Assert.Single(d.Targeting.AutoBands);
        Assert.True(template.Template); // the template itself is untouched
        Assert.Empty(template.Targeting.SpotFrequenciesKhz);
    }

    [Fact]
    public void CustomPrefix()
    {
        Assert.Equal("leer2", MissionJammerTag.Parse("EW-leer2", "EW-")!.PresetId);
        Assert.Null(MissionJammerTag.Parse("JAM:leer2", "EW-"));
    }
}
