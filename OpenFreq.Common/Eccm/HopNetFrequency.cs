using OpenFreqAudio.Eccm;

namespace OpenFreq.Common.Eccm;

/// <summary>
/// The channel a hopping net is routed on. OpenFreq relays audio by frequency, while a hopping radio has no
/// single frequency: everybody on a net must meet on one channel whatever they had dialled before going AJ. A HAVE
/// QUICK net maps into the UHF band (A12.325 → 237.325 MHz: 225 MHz + net/25 × 25 kHz), a SINCGARS net into
/// 30–88 MHz by a hash of its name. Single-channel radios that happen to sit on that channel never hear the net
/// (TransecGate), and the jamming model uses the net's hopset, not this channel.
/// </summary>
public static class HopNetFrequency
{
    public static int? Khz(HopMode mode, string? net)
    {
        switch (mode)
        {
            case HopMode.HaveQuick:
            {
                var parsed = HaveQuickNet.TryParse(net);
                if (parsed == null) return null;
                var index = parsed.Value.Value / 25 % 7000;
                return Hopset.UhfStartKhz + index * 25;
            }
            case HopMode.Sincgars:
            {
                var normalized = EccmResolver.NormalizeSincgarsNet(net);
                if (normalized.Length == 0) return null;
                var index = (int)(HopSequence.Hash("SG|" + normalized) % 2320UL);
                return Hopset.SincgarsStartKhz + index * 25;
            }
            default:
                return null;
        }
    }
}
