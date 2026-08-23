using System.Text;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Binary layout for SRS's UDP voice packet -- PROJECT_OBSERVED, copied field-for-field from the
/// real, MIT-licensed ciribob/DCS-SimpleRadioStandalone source
/// (Common/Models/UDPVoicePacket.cs, read directly 2026-08-22). All multi-byte fields are
/// little-endian (BitConverter default on the platforms SRS ships for).
///
/// Layout: [u16 PacketLength][u16 AudioPart1Length][u16 FrequencyPartLength]
///         [AudioPart1Length bytes: Opus payload]
///         [repeated: f64 Frequency (Hz) + byte Modulation + byte Encryption]
///         [u32 UnitId][u64 PacketNumber][byte RetransmissionCount]
///         [22 ASCII bytes: TransmissionGuid][22 ASCII bytes: ClientGuid]
/// </summary>
public sealed class SrsVoicePacket
{
    public const int GuidLength = 22;
    private const int FrequencySegmentLength = sizeof(double) + sizeof(byte) + sizeof(byte);
    private const int PacketHeaderLength = sizeof(ushort) * 3;
    private const int FixedSegmentLength = sizeof(uint) + sizeof(ulong) + sizeof(byte) + GuidLength + GuidLength;

    public byte[] AudioPart1 { get; init; } = [];
    public double[] Frequencies { get; init; } = [];
    public byte[] Modulations { get; init; } = [];
    public byte[] Encryptions { get; init; } = [];
    public uint UnitId { get; init; }
    public ulong PacketNumber { get; init; }
    public byte RetransmissionCount { get; init; }

    /// <summary>The GUID of the transmission itself (relay/loop-prevention identifier), not the
    /// sending client -- see the real class's doc comment. Passed through unchanged when we
    /// forward audio between two SRS peers directly (not applicable to the bridge's own
    /// encode path, which always mints a fresh one).</summary>
    public string TransmissionGuid { get; init; } = "";

    /// <summary>The sending client's own GUID.</summary>
    public string ClientGuid { get; init; } = "";

    public static SrsVoicePacket? TryDecode(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (packet.Length < PacketHeaderLength + FixedSegmentLength) return null;

            var audioPart1Length = BitConverter.ToUInt16(packet.Slice(2, 2));
            var frequencyPartLength = BitConverter.ToUInt16(packet.Slice(4, 2));
            var freqCount = frequencyPartLength / FrequencySegmentLength;

            var audioOffset = PacketHeaderLength;
            var audio = packet.Slice(audioOffset, audioPart1Length).ToArray();

            var frequencies = new double[freqCount];
            var modulations = new byte[freqCount];
            var encryptions = new byte[freqCount];
            var freqOffset = PacketHeaderLength + audioPart1Length;
            for (var i = 0; i < freqCount; i++)
            {
                frequencies[i] = BitConverter.ToDouble(packet.Slice(freqOffset, 8));
                modulations[i] = packet[freqOffset + 8];
                encryptions[i] = packet[freqOffset + 9];
                freqOffset += FrequencySegmentLength;
            }

            var fixedOffset = PacketHeaderLength + audioPart1Length + frequencyPartLength;
            var unitId = BitConverter.ToUInt32(packet.Slice(fixedOffset, 4));
            var packetNumber = BitConverter.ToUInt64(packet.Slice(fixedOffset + 4, 8));

            var transmissionGuid = Encoding.ASCII.GetString(packet.Slice(packet.Length - (GuidLength + GuidLength), GuidLength));
            var retransmissionCount = packet[packet.Length - (GuidLength + GuidLength + 1)];
            var clientGuid = Encoding.ASCII.GetString(packet.Slice(packet.Length - GuidLength, GuidLength));

            return new SrsVoicePacket
            {
                AudioPart1 = audio,
                Frequencies = frequencies,
                Modulations = modulations,
                Encryptions = encryptions,
                UnitId = unitId,
                PacketNumber = packetNumber,
                RetransmissionCount = retransmissionCount,
                TransmissionGuid = transmissionGuid,
                ClientGuid = clientGuid
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            // Malformed/truncated packet -- caller treats null as "ignore".
            return null;
        }
    }

    public byte[] Encode()
    {
        var frequencyPartLength = Frequencies.Length * FrequencySegmentLength;
        var totalLength = PacketHeaderLength + AudioPart1.Length + frequencyPartLength + FixedSegmentLength;

        var buffer = new byte[totalLength];

        BitConverter.GetBytes((ushort)totalLength).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)AudioPart1.Length).CopyTo(buffer, 2);
        BitConverter.GetBytes((ushort)frequencyPartLength).CopyTo(buffer, 4);

        AudioPart1.CopyTo(buffer, PacketHeaderLength);

        var freqOffset = PacketHeaderLength + AudioPart1.Length;
        for (var i = 0; i < Frequencies.Length; i++)
        {
            BitConverter.GetBytes(Frequencies[i]).CopyTo(buffer, freqOffset);
            buffer[freqOffset + 8] = Modulations.Length > i ? Modulations[i] : (byte)SrsModulation.AM;
            buffer[freqOffset + 9] = Encryptions.Length > i ? Encryptions[i] : (byte)0;
            freqOffset += FrequencySegmentLength;
        }

        var fixedOffset = PacketHeaderLength + AudioPart1.Length + frequencyPartLength;
        BitConverter.GetBytes(UnitId).CopyTo(buffer, fixedOffset);
        BitConverter.GetBytes(PacketNumber).CopyTo(buffer, fixedOffset + 4);

        buffer[totalLength - (GuidLength + GuidLength + 1)] = RetransmissionCount;
        Encoding.ASCII.GetBytes(TransmissionGuid.PadRight(GuidLength)[..GuidLength])
            .CopyTo(buffer, totalLength - (GuidLength + GuidLength));
        Encoding.ASCII.GetBytes(ClientGuid.PadRight(GuidLength)[..GuidLength])
            .CopyTo(buffer, totalLength - GuidLength);

        return buffer;
    }
}
