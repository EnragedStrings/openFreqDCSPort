using OpenFreqClient.Models;

namespace OpenFreq.Client.Tests;

/// <summary>
/// The "Apply SFX to my voice" checkbox became the "SFX on my voice" slider (upstream 4abdbd2).
/// Settings files written before that must still load, and keep meaning what they meant.
/// </summary>
public class OwnVoiceSfxSettingsMigrationTests
{
    private static OpenFreqSettings Load(string settingsJson) =>
        OpenFreqClient.Json.Json.Instance.Deserialize<AppConfiguration>(
            "{ \"Settings\": " + settingsJson + " }")!.Settings;

    [Fact]
    public void OldFile_SfxOff_LoadsAsSliderAtZero()
    {
        Assert.Equal(0.0, Load("{ \"ApplyOwnVoiceSfx\": false }").OwnVoiceSfxVolume);
    }

    [Fact]
    public void OldFile_SfxOn_LoadsAsFullSlider()
    {
        Assert.Equal(1.0, Load("{ \"ApplyOwnVoiceSfx\": true }").OwnVoiceSfxVolume);
    }

    [Fact]
    public void NewFile_KeepsItsSliderValue()
    {
        Assert.Equal(0.35, Load("{ \"OwnVoiceSfxVolume\": 0.35 }").OwnVoiceSfxVolume);
    }

    [Fact]
    public void NoSetting_DefaultsToFullSlider()
    {
        Assert.Equal(1.0, Load("{ }").OwnVoiceSfxVolume);
    }

    [Fact]
    public void Saving_WritesOnlyTheSlider()
    {
        var json = OpenFreqClient.Json.Json.Instance.Serialize(
            new AppConfiguration { Settings = new OpenFreqSettings { OwnVoiceSfxVolume = 0.5 } });

        Assert.Contains("\"OwnVoiceSfxVolume\": 0.5", json);
        Assert.DoesNotContain("ApplyOwnVoiceSfx", json);
    }
}
