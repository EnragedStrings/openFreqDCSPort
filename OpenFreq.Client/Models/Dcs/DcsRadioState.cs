using System;

namespace OpenFreq.Client.Models.Dcs;

public class DcsRadioState
{
    public DcsRadioSlot Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public long FrequencyHz { get; set; }
    public long SecondaryFrequencyHz { get; set; }
    public DcsRadioModulation Modulation { get; set; } = DcsRadioModulation.Disabled;
    public double Volume { get; set; } = 1.0d;
    public bool IsOn { get; set; }
    public bool Ptt { get; set; }

    public int FrequencyKhz => (int)Math.Round(FrequencyHz / 1000d);
    public int SecondaryFrequencyKhz => (int)Math.Round(SecondaryFrequencyHz / 1000d);

    public DcsRadioState Clone()
    {
        return new DcsRadioState
        {
            Slot = Slot,
            Name = Name,
            FrequencyHz = FrequencyHz,
            SecondaryFrequencyHz = SecondaryFrequencyHz,
            Modulation = Modulation,
            Volume = Volume,
            IsOn = IsOn,
            Ptt = Ptt
        };
    }
}
