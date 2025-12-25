using System.Collections.Generic;

namespace OpenFreqClient.Models;

public class AppConfiguration
{
    public OpenFreqSettings Settings { get; set; } = new();
    public List<ChannelData> Channels { get; set; } = new();
}

public class ChannelData
{
    public string? Name { get; set; }
    public double FrequencyMhz { get; set; }  // KHz
    public Channel.ChannelType Type { get; set; }
    public string HotkeyCode { get; set; } = "VcUndefined";
    public bool Enabled { get; set; }
}
