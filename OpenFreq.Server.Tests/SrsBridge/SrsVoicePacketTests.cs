using OpenFreqServer.SrsBridge;

namespace OpenFreq.Server.Tests.SrsBridge;

public class SrsVoicePacketTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsAllFields()
    {
        var packet = new SrsVoicePacket
        {
            AudioPart1 = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
            Frequencies = [305_000_000.0, 251_000_000.0],
            Modulations = [(byte)SrsModulation.AM, (byte)SrsModulation.FM],
            Encryptions = [0, 3],
            UnitId = 16777217,
            PacketNumber = 42,
            RetransmissionCount = 0,
            TransmissionGuid = SrsGuid.NewGuid(),
            ClientGuid = SrsGuid.NewGuid()
        };

        var bytes = packet.Encode();
        var decoded = SrsVoicePacket.TryDecode(bytes);

        Assert.NotNull(decoded);
        Assert.Equal(packet.AudioPart1, decoded!.AudioPart1);
        Assert.Equal(packet.Frequencies, decoded.Frequencies);
        Assert.Equal(packet.Modulations, decoded.Modulations);
        Assert.Equal(packet.Encryptions, decoded.Encryptions);
        Assert.Equal(packet.UnitId, decoded.UnitId);
        Assert.Equal(packet.PacketNumber, decoded.PacketNumber);
        Assert.Equal(packet.TransmissionGuid, decoded.TransmissionGuid);
        Assert.Equal(packet.ClientGuid, decoded.ClientGuid);
    }

    [Fact]
    public void EncodeThenDecode_SingleFrequencyNoEncryption()
    {
        var packet = new SrsVoicePacket
        {
            AudioPart1 = [0xAA, 0xBB],
            Frequencies = [133_000_000.0],
            Modulations = [(byte)SrsModulation.AM],
            Encryptions = [0],
            ClientGuid = SrsGuid.NewGuid(),
            TransmissionGuid = SrsGuid.NewGuid()
        };

        var decoded = SrsVoicePacket.TryDecode(packet.Encode());

        Assert.NotNull(decoded);
        Assert.Single(decoded!.Frequencies);
        Assert.Equal(133_000_000.0, decoded.Frequencies[0]);
        Assert.Equal(0, decoded.Encryptions[0]);
    }

    [Fact]
    public void TryDecode_TooShortPacket_ReturnsNull()
    {
        Assert.Null(SrsVoicePacket.TryDecode(new byte[10]));
    }

    [Fact]
    public void ClientGuid_MatchesRealSrsLength()
    {
        // Real SRS GUIDs are always exactly 22 ASCII chars -- callers (e.g. the UDP receive loop's
        // client-guid lookup) rely on this being stable.
        Assert.Equal(22, SrsGuid.NewGuid().Length);
        Assert.Equal(22, SrsGuid.FromOpenFreqId("some-openfreq-peer-id").Length);
    }

    [Fact]
    public void FromOpenFreqId_IsDeterministic()
    {
        var a = SrsGuid.FromOpenFreqId("peer-42");
        var b = SrsGuid.FromOpenFreqId("peer-42");
        var c = SrsGuid.FromOpenFreqId("peer-43");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
