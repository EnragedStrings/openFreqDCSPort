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

    /// <summary>DCS OBSERVED BEHAVIOR: true when the ARC-210's channel knob reads "Channel 31"
    /// AND its secondary selector reads "PRST" (see OpenFreqDCS.lua's buildA10C2Radios). This is
    /// the raw cockpit-argument match only -- it does NOT mean SATCOM is usable yet. The 3-second
    /// acquisition/login delay is tracked client-side by SatcomAcquisitionStateMachine, fed this
    /// field every DCS export frame. Always false for radios other than the ARC-210.</summary>
    public bool SatcomSelected { get; set; }

    /// <summary>DCS OBSERVED BEHAVIOR: true whenever the ARC-210's channel knob is anywhere in
    /// the configured Channel 31-40 DAMA ANDVT VOICE band (OpenFreqDCSConfig.a10c2.satcom.
    /// channelBandMinValue/channelBandMaxValue), regardless of the secondary selector. Broader
    /// than <see cref="SatcomSelected"/> (which requires exactly Channel 31 + PRST, the login
    /// trigger) -- this is the band-sustain signal: once logged in, staying anywhere in this band
    /// (and powered) keeps SATCOM active without re-running the login sequence; leaving it logs
    /// out. Always false for radios other than the ARC-210.</summary>
    public bool SatcomBandActive { get; set; }

    /// <summary>PROJECT_OBSERVED: the ARC-210's SATCOM channel/net pushbutton (argument 561)
    /// advances a virtual channel number 1-6 each press, wrapping 6 back to 1, independent of the
    /// 31-40 knob band above (see OpenFreqDCS.lua's buildA10C2Radios). Two stations must match
    /// BOTH SatcomBandActive AND this channel number to be on the same virtual SATCOM link -- see
    /// ChannelCardListViewModel.GetSatcomVirtualFrequencyKhz. Always 1 (the DCS-observed default)
    /// for radios other than the ARC-210.</summary>
    public int SatcomChannel { get; set; } = 1;

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
            ToneOn = ToneOn,
            SatcomSelected = SatcomSelected,
            SatcomBandActive = SatcomBandActive,
            SatcomChannel = SatcomChannel
        };
    }
}
