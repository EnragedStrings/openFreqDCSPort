using System;

namespace OpenFreq.Client.Models.Dcs;

public class DcsRadioState
{
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public long FrequencyHz { get; set; }
    public long SecondaryFrequencyHz { get; set; }
    public DcsRadioModulation Modulation { get; set; } = DcsRadioModulation.Disabled;
    public double Volume { get; set; } = 1.0d;
    public bool IsOn { get; set; }
    public bool Ptt { get; set; }

    /// <summary>KY-58/COMSEC encryption engaged for this radio.</summary>
    public bool Enc { get; set; }
    /// <summary>Encryption key channel (1-6). 0 = none/not applicable.</summary>
    public int EncKey { get; set; }
    /// <summary>HAVE QUICK frequency-hopping engaged for this radio.</summary>
    public bool HqOn { get; set; }

    /// <summary>Cockpit squelch switch state: true = normal/closed squelch, false = the pilot
    /// has manually opened squelch to monitor weak/garbled signals.</summary>
    public bool SquelchOn { get; set; } = true;

    /// <summary>ARC-186 momentary TONE switch held: keys the radio and transmits an attention
    /// tone instead of mic audio. Always false for the other radios.</summary>
    public bool ToneOn { get; set; }

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
            Ptt = Ptt,
            Enc = Enc,
            EncKey = EncKey,
            HqOn = HqOn,
            SquelchOn = SquelchOn,
            ToneOn = ToneOn
        };
    }
}
