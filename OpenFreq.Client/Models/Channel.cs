namespace OpenFreqClient.Models;

public class Channel
{
    /// <summary>
    /// Frequency in Hz (SI unit)
    /// Example: 305.0 MHz = 305,000,000 Hz
    /// </summary>
    public double FrequencyMhz { get; set; }
    
    public string? Name  { get; set; }
    public float RxDb {get; set;}
    public ChannelType Type  { get; set; }
    public ChannelStatus Status { get; set; } = ChannelStatus.Disconnected;

    public bool Enabled { get; set; } = true;

    public enum ChannelType
    {
        UHF, VHF, Custom
    }

    public enum ChannelStatus
    {
        Connected,
        Disconnected,
        Receiving,
        Transmitting
    }
}