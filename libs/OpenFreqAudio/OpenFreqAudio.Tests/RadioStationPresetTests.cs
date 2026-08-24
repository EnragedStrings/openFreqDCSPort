using OpenFreqAudio;

namespace OpenFreqAudio.Tests;

/// <summary>
/// Covers GetPresetByDcsUnit -- the DCS-side counterpart to GetPresetByBmsAircraftNctr. F-16
/// reuses the real AN/ARC-210-sourced FighterF16 preset; every other DCS airframe (including the
/// A-10C II, pending real per-aircraft figures) intentionally falls back to FighterGeneric today.
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
    [InlineData("FA-18C_hornet")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherAircraftFallBackToGeneric(string? dcsUnit)
    {
        Assert.Equal(RadioStationPresets.FighterGeneric, RadioStationPresets.GetPresetByDcsUnit(dcsUnit));
    }
}
