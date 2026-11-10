using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenFreqClient.Services.AutoConnect;

public enum AutoConnectAction
{
    Connect,
    Leave,
    Ping,
}

public enum AutoConnectAckState
{
    Connected,
    Connecting,
    PendingConsent,
    Ignored,
    Rejected,
}

/// <summary>An OpenFreq server offered by a DCS server advert. <see cref="Address"/> is the
/// "host:port" form the connection path (Util.ResolveAddress) understands.</summary>
public sealed record AutoConnectTarget(string Host, int Port, string? Sid, bool Auth)
{
    public string Address => $"{Host}:{Port}";
}

public sealed record AutoConnectRequest(
    long Seq,
    AutoConnectAction Action,
    string SessionId,
    string DcsServer,
    AutoConnectTarget? Target,
    string? Reason = null,
    int? Code = null);

/// <summary>
/// Wire format between the DCS client hook (OpenFreqAutoConnectGameGUI.lua) and the app, see
/// docs/autoconnect/AUTOCONNECT_DESIGN.md section 6.2. One JSON object per UDP datagram:
/// <code>{"type":"openfreq.dcs.autoconnect","v":1,"token":"..","seq":1,"action":"connect",
///  "session":{"id":"..","dcsServer":"host:port","since":0},
///  "target":{"host":"..","port":9987,"sid":"..","auth":1}}</code>
/// Everything in it is untrusted: the target came from a DCS server's chat line. A password is
/// never read from it, even if present.
/// </summary>
public static partial class AutoConnectProtocol
{
    public const string RequestType = "openfreq.dcs.autoconnect";
    public const string AckType = "openfreq.dcs.ack";
    public const int Version = 1;
    public const int MaxDatagramBytes = 2048;
    public const int DefaultPort = 34323;

    public static string ToWire(AutoConnectAckState state) => state switch
    {
        AutoConnectAckState.Connected => "connected",
        AutoConnectAckState.Connecting => "connecting",
        AutoConnectAckState.PendingConsent => "pending-consent",
        AutoConnectAckState.Ignored => "ignored",
        _ => "rejected",
    };

    public static bool TryParse(ReadOnlySpan<byte> datagram, string expectedToken, out AutoConnectRequest? request,
        out string error)
    {
        request = null;
        if (datagram.Length == 0 || datagram.Length > MaxDatagramBytes)
        {
            error = "size";
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(datagram, new JsonReaderOptions { MaxDepth = 4 });
            using var doc = JsonDocument.ParseValue(ref reader);
            return TryParse(doc.RootElement, expectedToken, out request, out error);
        }
        catch (JsonException)
        {
            error = "malformed JSON";
            return false;
        }
    }

    private static bool TryParse(JsonElement root, string expectedToken, out AutoConnectRequest? request,
        out string error)
    {
        request = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetString(root, "type", out var type) || type != RequestType)
        {
            error = "type";
            return false;
        }

        if (!TryGetInt(root, "v", out var version) || version != Version)
        {
            error = "unsupported version";
            return false;
        }

        // Token before anything else that could vary in cost or log content.
        if (!TryGetString(root, "token", out var token) || !TokenEquals(token!, expectedToken))
        {
            error = "token";
            return false;
        }

        if (!TryGetLong(root, "seq", out var seq) || seq < 0)
        {
            error = "seq";
            return false;
        }

        if (!TryGetString(root, "action", out var actionText))
        {
            error = "action";
            return false;
        }

        AutoConnectAction action;
        switch (actionText)
        {
            case "connect": action = AutoConnectAction.Connect; break;
            case "leave": action = AutoConnectAction.Leave; break;
            case "ping": action = AutoConnectAction.Ping; break;
            default:
                error = "action";
                return false;
        }

        var sessionId = "";
        var dcsServer = "";
        if (root.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetString(session, "id", out var id) || !SessionIdRegex().IsMatch(id!))
            {
                error = "session.id";
                return false;
            }

            sessionId = id!;
            if (TryGetString(session, "dcsServer", out var server))
            {
                if (server!.Length > 255 || server.AsSpan().IndexOfAnyInRange('\0', '\x1f') >= 0)
                {
                    error = "session.dcsServer";
                    return false;
                }

                dcsServer = server;
            }
        }
        else if (action != AutoConnectAction.Ping)
        {
            error = "session";
            return false;
        }

        AutoConnectTarget? target = null;
        string? reason = null;
        int? code = null;
        if (action == AutoConnectAction.Connect)
        {
            if (!root.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.Object)
            {
                error = "target";
                return false;
            }

            if (!TryGetString(t, "host", out var host) || !AutoConnectTargetValidator.IsValidHost(host))
            {
                error = "target.host";
                return false;
            }

            if (!TryGetInt(t, "port", out var port) || !AutoConnectTargetValidator.IsValidPort(port))
            {
                error = "target.port";
                return false;
            }

            string? sid = null;
            if (t.TryGetProperty("sid", out var sidElement) && sidElement.ValueKind != JsonValueKind.Null)
            {
                if (sidElement.ValueKind != JsonValueKind.String ||
                    !AutoConnectTargetValidator.IsValidSid(sidElement.GetString()))
                {
                    error = "target.sid";
                    return false;
                }

                sid = sidElement.GetString();
            }

            var auth = false;
            if (t.TryGetProperty("auth", out var authElement))
            {
                if (authElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    auth = authElement.GetBoolean();
                else if (TryGetInt(t, "auth", out var authInt) && authInt is 0 or 1)
                    auth = authInt == 1;
                else if (authElement.ValueKind != JsonValueKind.Null)
                {
                    error = "target.auth";
                    return false;
                }
            }

            // Deliberately no "pw": passwords are never taken from an advert (design decision 4).
            target = new AutoConnectTarget(host!, port, sid, auth);
        }
        else if (action == AutoConnectAction.Leave)
        {
            if (TryGetString(root, "reason", out var r))
                reason = Sanitize(r!, 100);
            if (TryGetInt(root, "code", out var c))
                code = c;
        }

        request = new AutoConnectRequest(seq, action, sessionId, dcsServer, target, reason, code);
        error = "";
        return true;
    }

    public static byte[] BuildAck(long seq, AutoConnectAckState state, string? message = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", AckType);
            writer.WriteNumber("v", Version);
            writer.WriteNumber("seq", seq);
            writer.WriteString("state", ToWire(state));
            if (!string.IsNullOrEmpty(message))
                writer.WriteString("msg", Sanitize(message, 200));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>128-bit random lowercase hex, written to client.json for the hook.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool TokenEquals(string actual, string expected)
    {
        var a = Encoding.UTF8.GetBytes(actual);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && b.Length > 0 && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Sanitize(string value, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (builder.Length >= maxLength) break;
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return value != null;
    }

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
            return false;
        if (property.TryGetInt64(out value))
            return true;
        // Lua 5.1 numbers are doubles; accept 9987.0 but never 9987.5.
        if (property.TryGetDouble(out var d) && d == Math.Floor(d) && d is >= long.MinValue and <= long.MaxValue)
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!TryGetLong(element, name, out var l) || l is < int.MinValue or > int.MaxValue)
            return false;
        value = (int)l;
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$")]
    private static partial Regex SessionIdRegex();
}
