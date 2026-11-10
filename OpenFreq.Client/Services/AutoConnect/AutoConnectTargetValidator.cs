using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenFreqClient.Services.AutoConnect;

public interface IAutoConnectResolver
{
    /// <summary>All addresses <paramref name="host"/> resolves to (an IP literal resolves to
    /// itself). Empty when it cannot be resolved.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsAutoConnectResolver : IAutoConnectResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];
        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return [];
        }
    }
}

public enum AutoConnectAddressClass
{
    Public,
    Private,
    Loopback,
    LinkLocal,
    Multicast,
    Reserved,
}

/// <summary>
/// The app-side (authoritative) checks on an advertised OpenFreq server, see
/// docs/autoconnect/AUTOCONNECT_DESIGN.md section 7.1. Same syntactic rules as the Lua hooks.
/// </summary>
public static class AutoConnectTargetValidator
{
    public const int DefaultOpenFreqPort = 9987;

    /// <summary>Lowercase IPv4 literal, or an RFC 1123 hostname with at least one dot, labels of
    /// [a-z0-9-]{1,63} without leading/trailing hyphen, at most 253 characters. No IPv6
    /// (Util.ResolveAddress splits on ':'), no '@', '/', '%', ':', spaces or quotes.</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253)
            return false;
        foreach (var c in host)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
                return false;
        }

        var labels = host.Split('.');
        if (labels.Length == 4 && labels.All(l => l.Length > 0 && l.All(char.IsAsciiDigit)))
            return labels.All(l => l.Length <= 3 && (l.Length == 1 || l[0] != '0') && int.Parse(l) <= 255);

        if (labels.Length < 2)
            return false;
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-')
                return false;
        }

        // A numeric-only top label ("1.2.3", "10.0.0.256") is never a real hostname.
        return !labels[^1].All(char.IsAsciiDigit);
    }

    public static bool IsValidPort(int port) => port is >= 1024 and <= 65535;

    public static bool IsValidSid(string? sid) =>
        !string.IsNullOrEmpty(sid) && sid.Length <= 32 &&
        sid.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static AutoConnectAddressClass Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => AutoConnectAddressClass.Reserved,
                127 => AutoConnectAddressClass.Loopback,
                10 => AutoConnectAddressClass.Private,
                100 when b[1] is >= 64 and <= 127 => AutoConnectAddressClass.Private, // CGNAT
                169 when b[1] == 254 => AutoConnectAddressClass.LinkLocal,
                172 when b[1] is >= 16 and <= 31 => AutoConnectAddressClass.Private,
                192 when b[1] == 168 => AutoConnectAddressClass.Private,
                >= 224 and <= 239 => AutoConnectAddressClass.Multicast,
                >= 240 => AutoConnectAddressClass.Reserved,
                _ => AutoConnectAddressClass.Public,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback)) return AutoConnectAddressClass.Loopback;
            if (address.Equals(IPAddress.IPv6Any)) return AutoConnectAddressClass.Reserved;
            if (address.IsIPv6LinkLocal) return AutoConnectAddressClass.LinkLocal;
            if (address.IsIPv6Multicast) return AutoConnectAddressClass.Multicast;
            var first = address.GetAddressBytes()[0];
            if ((first & 0xFE) == 0xFC) return AutoConnectAddressClass.Private; // fc00::/7 ULA
            return AutoConnectAddressClass.Public;
        }

        return AutoConnectAddressClass.Reserved;
    }

    /// <summary>Null when the advertised server may be used, otherwise the reason it may not.
    /// Every resolved address must pass (so a DNS answer mixing a public and a private address
    /// is rejected). Private (RFC 1918 / CGNAT / ULA) targets are allowed only when the DCS
    /// server itself is private or loopback (LAN play); loopback targets only when the DCS
    /// server is loopback (both on this PC). Link-local, multicast and reserved: never.</summary>
    public static string? CheckResolved(IReadOnlyList<IPAddress> target, IReadOnlyList<IPAddress> dcsServer)
    {
        if (target.Count == 0)
            return "the advertised host could not be resolved";

        var dcsClasses = dcsServer.Select(Classify).ToList();
        var dcsIsLoopback = dcsClasses.Count > 0 && dcsClasses.All(c => c == AutoConnectAddressClass.Loopback);
        var dcsIsLan = dcsClasses.Count > 0 &&
                       dcsClasses.All(c => c is AutoConnectAddressClass.Private or AutoConnectAddressClass.Loopback);

        foreach (var address in target)
        {
            switch (Classify(address))
            {
                case AutoConnectAddressClass.Public:
                    break;
                case AutoConnectAddressClass.Private when dcsIsLan:
                    break;
                case AutoConnectAddressClass.Private:
                    return $"a public DCS server advertised a private address ({address})";
                case AutoConnectAddressClass.Loopback when dcsIsLoopback:
                    break;
                case AutoConnectAddressClass.Loopback:
                    return $"the advertised address is this PC ({address})";
                default:
                    return $"the advertised address is not usable ({address})";
            }
        }

        return null;
    }

    /// <summary>Host part of the DCS server address the hook reported ("203.0.113.5:10308",
    /// "host", "[::1]:10308"). Empty when unknown.</summary>
    public static string DcsServerHost(string? dcsServer)
    {
        if (string.IsNullOrWhiteSpace(dcsServer))
            return "";
        var value = dcsServer.Trim();
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            return end > 1 ? value[1..end] : "";
        }

        var colon = value.IndexOf(':');
        if (colon >= 0 && value.IndexOf(':', colon + 1) < 0)
            return value[..colon];
        return value;
    }

    /// <summary>Normalised (lowercase host, port) of a "host[:port]" address, default port 9987.
    /// Null when unparsable.</summary>
    public static (string Host, int Port)? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        var parts = address.Trim().Split(':');
        if (parts.Length > 2 || parts[0].Length == 0)
            return null;
        var port = DefaultOpenFreqPort;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is < 1 or > 65535))
            return null;
        return (parts[0].ToLowerInvariant(), port);
    }

    /// <summary>Case-insensitive host plus port; a missing port means 9987.</summary>
    public static bool SameAddress(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        return x != null && y != null && x.Value == y.Value;
    }
}
