using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Jamming;
using OpenFreqServer.Json;

namespace OpenFreqServer.Jamming;

/// <summary>One datagram from the DCS server hook (docs/jamming/MISSION_JAMMERS.md §4).</summary>
public sealed class MissionJammerReport
{
    [JsonPropertyName("v")] public int Version { get; set; }
    [JsonPropertyName("boot")] public string Boot { get; set; } = string.Empty;
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("mission")] public string Mission { get; set; } = string.Empty;
    [JsonPropertyName("units")] public List<MissionUnitReport> Units { get; set; } = [];
}

/// <summary>One tagged unit in a <see cref="MissionJammerReport"/>.</summary>
public sealed class MissionUnitReport
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("group")] public string? Group { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("side")] public int? Side { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("alive")] public bool Alive { get; set; }
    [JsonPropertyName("lat")] public double? Lat { get; set; }
    [JsonPropertyName("lon")] public double? Lon { get; set; }
    [JsonPropertyName("alt")] public double? Alt { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("z")] public double? Z { get; set; }
}

/// <summary>
/// Pure parts of the mission-jammer intake: datagram authentication (<c>OFJ1 &lt;hmac&gt; &lt;json&gt;</c>,
/// HMAC-SHA256 over the JSON bytes), replay protection, and turning unit reports into jammer definitions.
/// </summary>
public sealed class MissionJammerProtocol
{
    public const string Magic = "OFJ1 ";
    public const int MaxDatagramBytes = 60_000;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(300);

    private readonly byte[] _key;
    private string? _boot;
    private long _lastSeq;

    public MissionJammerProtocol(string secret)
    {
        _key = Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>Why the last <see cref="TryAccept"/> returned null.</summary>
    public string? LastRejection { get; private set; }

    /// <summary>The report in <paramref name="datagram"/> if it is authentic, fresh and not a replay, else null
    /// (see <see cref="LastRejection"/>).</summary>
    public MissionJammerReport? TryAccept(ReadOnlySpan<byte> datagram, DateTimeOffset now)
    {
        LastRejection = null;
        if (datagram.Length > MaxDatagramBytes) return Reject("oversized");
        var magic = Encoding.ASCII.GetBytes(Magic);
        if (datagram.Length < magic.Length + 64 + 2 || !datagram[..magic.Length].SequenceEqual(magic))
            return Reject("not an OFJ1 datagram");

        var hexPart = datagram.Slice(magic.Length, 64);
        if (datagram[magic.Length + 64] != (byte)' ') return Reject("malformed");
        var json = datagram[(magic.Length + 65)..];

        Span<byte> claimed = stackalloc byte[32];
        if (!TryHex(hexPart, claimed)) return Reject("malformed HMAC");
        Span<byte> actual = stackalloc byte[32];
        HMACSHA256.HashData(_key, json, actual);
        if (!CryptographicOperations.FixedTimeEquals(claimed, actual)) return Reject("bad HMAC");

        MissionJammerReport? report;
        try
        {
            report = JsonSerializer.Deserialize(json, ServerJsonContext.Default.MissionJammerReport);
        }
        catch (JsonException)
        {
            return Reject("invalid JSON");
        }

        if (report is not { Version: 1 }) return Reject("unsupported version");
        if (string.IsNullOrEmpty(report.Boot) || report.Boot.Length > 64) return Reject("bad boot id");
        var sent = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(report.Ts, 0, 253402300799));
        if ((now - sent).Duration() > MaxClockSkew) return Reject("timestamp outside ±300 s (clock skew?)");
        if (report.Boot == _boot && report.Seq <= _lastSeq) return Reject("replayed sequence number");

        _boot = report.Boot;
        _lastSeq = report.Seq;
        return report;
    }

    private MissionJammerReport? Reject(string why)
    {
        LastRejection = why;
        return null;
    }

    private static bool TryHex(ReadOnlySpan<byte> hex, Span<byte> into)
    {
        for (var i = 0; i < into.Length; i++)
        {
            var hi = Nibble(hex[2 * i]);
            var lo = Nibble(hex[2 * i + 1]);
            if (hi < 0 || lo < 0) return false;
            into[i] = (byte)(hi << 4 | lo);
        }

        return true;

        static int Nibble(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            _ => -1
        };
    }

    /// <summary>
    /// The jammer definitions for a report's units (§1): the tag from the unit name, else the group name; a
    /// preset or config template (<paramref name="findConfigJammer"/>) as the base; id <c>dcs:&lt;unit name&gt;</c>;
    /// the unit's position, with the preset's mast height added for ground units, ships and statics. Units whose
    /// tag names an unknown preset or template are skipped. Problems go to <paramref name="warnings"/>.
    /// </summary>
    public static List<JammerDefinition> BuildDefinitions(MissionJammerReport report, string prefix, int maxJammers,
        Func<string, JammerDefinition?> findConfigJammer, ICollection<string> warnings)
    {
        var result = new List<JammerDefinition>();
        if (string.IsNullOrEmpty(report.Mission)) return result;

        foreach (var unit in report.Units)
        {
            if (result.Count >= Math.Max(0, maxJammers))
            {
                warnings.Add($"more than {maxJammers} mission jammers; the rest are ignored");
                break;
            }

            if (string.IsNullOrWhiteSpace(unit.Name)) continue;
            var tag = MissionJammerTag.Parse(unit.Name, prefix) ?? MissionJammerTag.Parse(unit.Group, prefix);
            if (tag == null)
            {
                warnings.Add($"{unit.Name}: no '{prefix}' tag in the unit or group name");
                continue;
            }

            foreach (var w in tag.Warnings) warnings.Add($"{unit.Name}: {w}");

            JammerDefinition? template = null;
            if (tag.TemplateId != null)
            {
                template = findConfigJammer(tag.TemplateId);
                if (template == null)
                {
                    warnings.Add($"{unit.Name}: no server-config jammer '{tag.TemplateId}'");
                    continue;
                }
            }
            else if (JammerPresets.Find(tag.PresetId) == null)
            {
                warnings.Add($"{unit.Name}: unknown preset '{tag.PresetId}'");
                continue;
            }

            var d = tag.BuildDefinition(template);
            d.Id = "dcs:" + unit.Name;
            d.Name ??= unit.Name;
            d.Alive = unit.Alive;
            d.Coalition = unit.Side switch { 1 => "red", 2 => "blue", 0 => "neutral", _ => d.Coalition };

            var airborne = string.Equals(unit.Kind, "air", StringComparison.OrdinalIgnoreCase);
            var mast = airborne ? 0.0 : JammerPresets.Resolve(d).HeightAglMeters ?? 0.0;
            if (unit.Lat is { } lat && unit.Lon is { } lon && double.IsFinite(lat) && double.IsFinite(lon))
            {
                d.Latitude = lat;
                d.Longitude = lon;
                d.AltitudeMeters = (unit.Alt ?? 0.0) + mast;
            }

            if (unit.X is { } x && unit.Z is { } z && double.IsFinite(x) && double.IsFinite(z))
                d.DcsPosition = new JammerVector3(x, (unit.Y ?? unit.Alt ?? 0.0) + mast, z);
            else if (template?.DcsPosition == null)
                d.DcsPosition = null;

            if (d.Latitude == null && d.DcsPosition == null && unit.Alive)
                warnings.Add($"{unit.Name}: alive but no position reported");
            result.Add(d);
        }

        return result;
    }
}

/// <summary>
/// UDP listener for the DCS server hook's mission-jammer datagrams. Authenticated datagrams replace the
/// coordinator's mission jammers; staleness is handled by <see cref="JammingCoordinator.MissionStaleAfter"/>.
/// </summary>
public sealed class MissionJammerIntake : IDisposable
{
    public const int MinSecretLength = 16;

    private readonly DcsJammerHookConfig _config;
    private readonly JammingCoordinator _coordinator;
    private readonly ILogger _logger;
    private readonly MissionJammerProtocol _protocol;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private Task? _loop;
    private string? _lastMission;
    private HashSet<string> _lastWarnings = [];
    private string? _lastRejection;
    private DateTimeOffset _lastRejectionLogged;

    public MissionJammerIntake(DcsJammerHookConfig config, JammingCoordinator coordinator, ILogger logger)
    {
        _config = config;
        _coordinator = coordinator;
        _logger = logger;
        _protocol = new MissionJammerProtocol(config.Secret ?? string.Empty);
    }

    /// <summary>Starts listening. Returns false (and logs why) when the config cannot be used.</summary>
    public bool Start()
    {
        if ((_config.Secret?.Length ?? 0) < MinSecretLength)
        {
            _logger.LogError("Jamming: dcsHook.secret must be at least {Min} characters; mission-jammer intake " +
                             "not started", MinSecretLength);
            return false;
        }

        if (!IPAddress.TryParse(_config.BindAddress, out var address) || _config.Port is < 1 or > 65535)
        {
            _logger.LogError("Jamming: invalid dcsHook.bindAddress/port {Address}:{Port}; intake not started",
                _config.BindAddress, _config.Port);
            return false;
        }

        _udp = new UdpClient(new IPEndPoint(address, _config.Port));
        _coordinator.MissionStaleAfter = TimeSpan.FromSeconds(Math.Max(5.0, _config.StaleAfterSeconds));
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("Jamming: mission-jammer intake listening on UDP {Address}:{Port}", address,
            _config.Port);
        return true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                Handle(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port unreachable from an earlier send on Windows; harmless for a receive-only socket.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Jamming: mission-jammer intake error");
            }
        }
    }

    /// <summary>Processes one datagram (public for tests).</summary>
    public void Handle(byte[] datagram, IPEndPoint? from = null)
    {
        var report = _protocol.TryAccept(datagram, DateTimeOffset.UtcNow);
        if (report == null)
        {
            var why = _protocol.LastRejection;
            var now = DateTimeOffset.UtcNow;
            if (why != _lastRejection || now - _lastRejectionLogged > TimeSpan.FromMinutes(1))
            {
                _logger.LogWarning("Jamming: rejected a mission-jammer datagram from {From}: {Why}", from, why);
                _lastRejection = why;
                _lastRejectionLogged = now;
            }

            return;
        }

        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var definitions = MissionJammerProtocol.BuildDefinitions(report, _config.Prefix, _config.MaxJammers,
            _coordinator.FindConfigJammer, warnings);

        if (report.Mission != _lastMission)
        {
            _logger.LogInformation("Jamming: mission {Mission}: {Count} mission jammer(s) ({Alive} alive)",
                string.IsNullOrEmpty(report.Mission) ? "(none)" : report.Mission, definitions.Count,
                definitions.Count(d => d.Alive));
            _lastMission = report.Mission;
        }

        foreach (var w in warnings.Where(w => !_lastWarnings.Contains(w)))
            _logger.LogWarning("Jamming: mission jammer {Warning}", w);
        _lastWarnings = warnings;

        if (definitions.Count == 0 && string.IsNullOrEmpty(report.Mission))
            _coordinator.ClearMissionJammers();
        else
            _coordinator.SetMissionJammers(definitions);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // cancelled
        }

        _cts.Dispose();
    }
}
