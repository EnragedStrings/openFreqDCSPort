using System.Security.Cryptography;
using System.Text;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// SRS represents client/transmission GUIDs on the wire as a 22-character, URL-safe base64
/// encoding of a 16-byte GUID (its own "ShortGuid" helper -- PROJECT_OBSERVED, same encoding
/// scheme as the widely-used CSharpVitamins.ShortGuid package SRS's source references). Needed
/// whenever this bridge originates a voice packet on behalf of an OpenFreq peer (who has no SRS
/// GUID of its own) so the 22-byte GUID fields in <see cref="SrsVoicePacket"/> are always
/// well-formed instead of padded/truncated garbage.
/// </summary>
public static class SrsGuid
{
    public static string NewGuid() => Encode(Guid.NewGuid());

    /// <summary>Deterministically derives a stable 22-char SRS-shaped GUID from an arbitrary
    /// OpenFreq id (e.g. a peer id), so the same OpenFreq peer always maps to the same SRS GUID
    /// across packets rather than a fresh random one each time.</summary>
    public static string FromOpenFreqId(string openFreqId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(openFreqId));
        return Encode(new Guid(hash));
    }

    private static string Encode(Guid guid) =>
        Convert.ToBase64String(guid.ToByteArray())
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
