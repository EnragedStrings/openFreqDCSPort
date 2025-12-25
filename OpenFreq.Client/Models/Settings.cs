namespace OpenFreqClient.Models;

public class OpenFreqSettings
{
    public string OpenFreqServerAddress { get; set; } = "";
    public string OpenFreqPassword { get; set; } = "";

    public Mode ConnectionMode { get; set; } = Mode.BMS;
    public string TacviewServerAddress { get; set; } = "";
    public string TacviewServerPassword { get; set; } = "";
    
    public string HeightmapPath { get; set; } = "";

    public string InputDeviceName { get; set; } = "";
    public string OutputDeviceName { get; set; } = "";
    
    public enum Mode
    {
        GCI,
        BMS
    }
}