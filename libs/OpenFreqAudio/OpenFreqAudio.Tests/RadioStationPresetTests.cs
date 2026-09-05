using OpenFreqAudio;

namespace OpenFreqAudio.Tests;

/// <summary>
/// Covers GetPresetByDcsUnit -- the DCS-side counterpart to GetPresetByBmsAircraftNctr. F-16,
/// A-10 and UH-60 each get a dedicated preset (mainly for their aircraft-specific
/// AmbientNoiseType); every other DCS airframe intentionally falls back to FighterGeneric today.
/// See RadioStationPreset.cs's own doc comment on GetPresetByDcsUnit.
/// </summary>
public class RadioStationPresetTests
{
    [Theory]
    [InlineData("F-16C_50")]
    [InlineData("F-16C_52")]
    [InlineData("F-16D_50")]
    [InlineData("F-16A")]
    public void F16VariantsGetTheF16Preset(string dcsUnit)
    {
        Assert.Equal(RadioStationPresets.FighterF16, RadioStationPresets.GetPresetByDcsUnit(dcsUnit));
    }

    [Theory]
    [InlineData("A-10C_2")]
    [InlineData("A-10A")]
    public void A10VariantsGetTheA10Preset(string dcsUnit)
    {
        Assert.Equal(RadioStationPresets.AttackA10, RadioStationPresets.GetPresetByDcsUnit(dcsUnit));
    }

    [Theory]
    [InlineData("UH-60L")]
    [InlineData("UH-60L_DAP")]
    public void UH60VariantsGetTheUH60Preset(string dcsUnit)
    {
        Assert.Equal(RadioStationPresets.HelicopterUH60, RadioStationPresets.GetPresetByDcsUnit(dcsUnit));
    }

    [Theory]
    [InlineData("FA-18C_hornet")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherAircraftFallBackToGeneric(string? dcsUnit)
    {
        Assert.Equal(RadioStationPresets.FighterGeneric, RadioStationPresets.GetPresetByDcsUnit(dcsUnit));
    }
}
