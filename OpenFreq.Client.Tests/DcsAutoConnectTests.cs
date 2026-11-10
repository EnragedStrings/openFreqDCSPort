using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common;
using OpenFreqClient.Models;
using OpenFreqClient.Services.AutoConnect;

namespace OpenFreq.Client.Tests;

// Scenario numbers (T1..T14) refer to docs/autoconnect/AUTOCONNECT_DESIGN.md section 13 and
// docs/autoconnect/TEST_CHECKLIST.md.

public class AutoConnectProtocolTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static string Request(string action = "connect", string token = Token, int v = 1,
        string target = """{"host":"voice.example.com","port":9937,"sid":"example-main","auth":1}""",
        string type = "openfreq.dcs.autoconnect", string seq = "17") =>
        $$"""{"type":"{{type}}","v":{{v}},"token":"{{token}}","seq":{{seq}},"action":"{{action}}","session":{"id":"9f3c00aa","dcsServer":"203.0.113.5:10308","since":1762700000},"target":{{target}},"reason":"Disconnected","code":0}""";

    private static AutoConnectRequest? Parse(string json, out string error)
    {
        AutoConnectProtocol.TryParse(Encoding.UTF8.GetBytes(json), Token, out var request, out error);
        return request;
    }

    [Fact]
    public void ValidConnect_IsParsed()
    {
        var request = Parse(Request(), out _);
        Assert.NotNull(request);
        Assert.Equal(AutoConnectAction.Connect, request!.Action);
        Assert.Equal(17, request.Seq);
        Assert.Equal("9f3c00aa", request.SessionId);
        Assert.Equal("203.0.113.5:10308", request.DcsServer);
        Assert.Equal(new AutoConnectTarget("voice.example.com", 9937, "example-main", true), request.Target);
    }

    [Fact]
    public void LuaDoubleNumbers_AreAccepted_ButNotFractions()
    {
        Assert.NotNull(Parse(Request(seq: "17.0", target: """{"host":"a.example.com","port":9987.0}"""), out _));
        Assert.Null(Parse(Request(target: """{"host":"a.example.com","port":9987.5}"""), out _));
    }

    [Theory] // T10
    [InlineData("wrong-token")]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdeF")]
    public void WrongToken_IsRejected(string token)
    {
        Assert.Null(Parse(Request(token: token), out var error));
        Assert.Equal("token", error);
    }

    [Fact]
    public void WrongVersionOrType_IsRejected()
    {
        Assert.Null(Parse(Request(v: 2), out var error));
        Assert.Equal("unsupported version", error);
        Assert.Null(Parse(Request(type: "srs"), out _));
        Assert.Null(Parse(Request(action: "launch"), out _));
    }

    [Theory] // T10
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"openfreq.dcs.autoconnect\"")]
    public void Malformed_IsRejected(string json) => Assert.Null(Parse(json, out _));

    [Fact]
    public void Oversized_IsRejected()
    {
        var big = Request(target: "{\"host\":\"a.example.com\",\"port\":9987,\"x\":\"" + new string('x', 2100) + "\"}");
        Assert.Null(Parse(big, out var error));
        Assert.Equal("size", error);
    }

    [Theory] // T10
    [InlineData("""{"host":"Voice.Example.com","port":9987}""")]
    [InlineData("""{"host":"localhost","port":9987}""")]
    [InlineData("""{"host":"::1","port":9987}""")]
    [InlineData("""{"host":"a.com:9987","port":9987}""")]
    [InlineData("""{"host":"a.com\" & calc","port":9987}""")]
    [InlineData("""{"host":"a.com","port":80}""")]
    [InlineData("""{"host":"a.com","port":70000}""")]
    [InlineData("""{"host":"a.com","port":"9987"}""")]
    [InlineData("""{"host":"a.com","port":9987,"sid":"bad sid"}""")]
    [InlineData("""{"host":"a.com","port":9987,"auth":2}""")]
    [InlineData("""{"port":9987}""")]
    public void InvalidTarget_IsRejected(string target) => Assert.Null(Parse(Request(target: target), out _));

    [Fact] // T11: a password in the request is never read
    public void PasswordInRequest_IsIgnored()
    {
        var request = Parse(Request(target: """{"host":"a.example.com","port":9987,"pw":"secret","auth":0}"""), out _);
        Assert.NotNull(request);
        Assert.DoesNotContain("secret", request!.ToString());
    }

    [Fact]
    public void Leave_CarriesReason()
    {
        var request = Parse(Request(action: "leave", target: "null"), out _);
        Assert.Equal(AutoConnectAction.Leave, request!.Action);
        Assert.Equal("Disconnected", request.Reason);
        Assert.Null(request.Target);
    }

    [Fact]
    public void Ack_HasDesignShape()
    {
        using var doc = JsonDocument.Parse(AutoConnectProtocol.BuildAck(17, AutoConnectAckState.PendingConsent, "x"));
        Assert.Equal("openfreq.dcs.ack", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(17, doc.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal("pending-consent", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void NewToken_Is128BitHex()
    {
        var token = AutoConnectProtocol.NewToken();
        Assert.Matches("^[0-9a-f]{32}$", token);
        Assert.NotEqual(token, AutoConnectProtocol.NewToken());
    }
}

public class AutoConnectTargetValidatorTests
{
    [Theory]
    [InlineData("voice.example.com", true)]
    [InlineData("opmtnsen.com", true)]
    [InlineData("203.0.113.5", true)]
    [InlineData("a-b.c-d.example", true)]
    [InlineData("localhost", false)]
    [InlineData("Voice.example.com", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3.256", false)]
    [InlineData("01.2.3.4", false)]
    [InlineData("-a.com", false)]
    [InlineData("a-.com", false)]
    [InlineData("a..com", false)]
    [InlineData("a_b.com", false)]
    [InlineData("user@a.com", false)]
    [InlineData("a.com/x", false)]
    [InlineData("::1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Host(string? host, bool valid) => Assert.Equal(valid, AutoConnectTargetValidator.IsValidHost(host));

    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    [InlineData(9987, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void Port(int port, bool valid) => Assert.Equal(valid, AutoConnectTargetValidator.IsValidPort(port));

    [Theory] // T10: private-address rule (section 7.1)
    [InlineData("8.8.8.8", "203.0.113.5", true)]
    [InlineData("192.168.1.1", "203.0.113.5", false)]
    [InlineData("10.0.0.5", "203.0.113.5", false)]
    [InlineData("172.16.0.1", "203.0.113.5", false)]
    [InlineData("100.64.0.1", "203.0.113.5", false)]
    [InlineData("192.168.1.10", "192.168.1.2", true)]
    [InlineData("10.0.0.5", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "203.0.113.5", false)]
    [InlineData("127.0.0.1", "192.168.1.2", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("169.254.1.1", "169.254.1.2", false)]
    [InlineData("224.0.0.1", "203.0.113.5", false)]
    [InlineData("0.0.0.0", "203.0.113.5", false)]
    [InlineData("255.255.255.255", "192.168.1.2", false)]
    [InlineData("192.168.1.1", "", false)]
    public void ResolvedAddressRule(string target, string dcs, bool allowed)
    {
        IReadOnlyList<IPAddress> dcsAddresses = dcs.Length == 0 ? [] : [IPAddress.Parse(dcs)];
        var reason = AutoConnectTargetValidator.CheckResolved([IPAddress.Parse(target)], dcsAddresses);
        Assert.Equal(allowed, reason == null);
    }

    [Fact]
    public void MixedDnsAnswer_IsRejected() =>
        Assert.NotNull(AutoConnectTargetValidator.CheckResolved(
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("192.168.0.1")], [IPAddress.Parse("203.0.113.5")]));

    [Fact]
    public void Unresolvable_IsRejected() =>
        Assert.NotNull(AutoConnectTargetValidator.CheckResolved([], [IPAddress.Parse("203.0.113.5")]));

    [Theory] // T4
    [InlineData("voice.example.com:9987", "VOICE.example.com:9987", true)]
    [InlineData("voice.example.com", "voice.example.com:9987", true)]
    [InlineData("voice.example.com:9937", "voice.example.com:9987", false)]
    [InlineData("a.example.com:9987", "b.example.com:9987", false)]
    [InlineData("", "a.example.com:9987", false)]
    public void SameAddress(string a, string b, bool same) =>
        Assert.Equal(same, AutoConnectTargetValidator.SameAddress(a, b));

    [Theory]
    [InlineData("203.0.113.5:10308", "203.0.113.5")]
    [InlineData("dcs.example.com", "dcs.example.com")]
    [InlineData("[::1]:10308", "::1")]
    [InlineData("", "")]
    public void DcsServerHost(string input, string host) =>
        Assert.Equal(host, AutoConnectTargetValidator.DcsServerHost(input));
}

public class DcsAutoConnectEndpointTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static byte[] Datagram(long seq, string token = Token) => Encoding.UTF8.GetBytes(
        """{"type":"openfreq.dcs.autoconnect","v":1,"token":""" + "\"" + token + "\",\"seq\":" + seq +
        ""","action":"connect","session":{"id":"abc123","dcsServer":"203.0.113.5:10308"},"target":{"host":"voice.example.com","port":9987}}""");

    [Fact] // T10: real loopback socket, token check, ack, duplicate seq
    public async Task ValidRequest_IsAcked_DuplicatesAreReAckedNotReprocessed_WrongTokenGetsNoAnswer()
    {
        var calls = 0;
        using var endpoint = new DcsAutoConnectEndpoint(Token, _ =>
        {
            calls++;
            return AutoConnectAckState.Connecting;
        }, NullLogger.Instance, port: 0);
        Assert.True(endpoint.Start());

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, endpoint.BoundPort);

        await client.SendAsync(Datagram(5), target);
        var ack = await ReceiveAsync(client);
        Assert.NotNull(ack);
        using (var doc = JsonDocument.Parse(ack!))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("seq").GetInt64());
            Assert.Equal("connecting", doc.RootElement.GetProperty("state").GetString());
        }

        await client.SendAsync(Datagram(5), target);
        Assert.NotNull(await ReceiveAsync(client));
        Assert.Equal(1, calls);

        await client.SendAsync(Datagram(6, "ffffffffffffffffffffffffffffffff"), target);
        Assert.Null(await ReceiveAsync(client, 300));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NonLoopbackSender_IsDropped()
    {
        using var endpoint = new DcsAutoConnectEndpoint(Token, _ => AutoConnectAckState.Connected, NullLogger.Instance, 0);
        Assert.Null(endpoint.Process(Datagram(1), new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5000)));
        Assert.NotNull(endpoint.Process(Datagram(1), new IPEndPoint(IPAddress.Loopback, 5000)));
    }

    [Fact]
    public void PortInUse_ReportsListenerError()
    {
        using var blocker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)blocker.Client.LocalEndPoint!).Port;
        using var endpoint = new DcsAutoConnectEndpoint(Token, _ => AutoConnectAckState.Connected, NullLogger.Instance, port);
        Assert.False(endpoint.Start());
        Assert.Contains(port.ToString(), endpoint.ListenerError);
    }

    private static async Task<byte[]?> ReceiveAsync(UdpClient client, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return (await client.ReceiveAsync(cts.Token)).Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Policy and state machine tests with a fake host, resolver and clock.</summary>
public class DcsAutoConnectControllerTests
{
    private const string Session = "s1";
    private static readonly AutoConnectTarget Target = new("voice.example.com", 9937, "example-main", false);

    private static AutoConnectRequest Connect(AutoConnectTarget? target = null, string session = Session, long seq = 1,
        string dcsServer = "203.0.113.5:10308") =>
        new(seq, AutoConnectAction.Connect, session, dcsServer, target ?? Target);

    private sealed class Harness
    {
        public readonly FakeHost Host = new();
        public readonly ManualDelay Delay = new();
        public readonly FakeResolver Resolver = new();
        public readonly DcsAutoConnectController Controller;

        public Harness(bool launchedByDcs = false)
        {
            Controller = new DcsAutoConnectController(Host, Resolver, NullLogger.Instance, delay: Delay.Delay,
                random: () => 0.5, launchedByDcs: launchedByDcs);
        }
    }

    [Fact] // T1
    public void NotConnected_ConnectsWithSessionSnapshot_SavedSettingsUntouched()
    {
        var h = new Harness();
        Assert.Equal(AutoConnectAckState.Connecting, h.Controller.HandleConnect(Connect()));
        var call = Assert.Single(h.Host.Connects);
        Assert.Equal("voice.example.com:9937", call.OpenFreqServerAddress);
        Assert.Equal("", call.OpenFreqPassword);
        Assert.Equal("Pilot", call.DisplayName);
        Assert.Equal("saved.example.com:9987", h.Host.Saved.OpenFreqServerAddress);
        Assert.Equal("savedpw", h.Host.Saved.OpenFreqPassword);
        Assert.Empty(h.Host.Remembered);
        Assert.Equal(AutoConnectPhase.AutoActive, h.Controller.Phase);
    }

    [Fact] // section 15.8
    public void NotInDcsMode_IsIgnored()
    {
        var h = new Harness();
        h.Host.IsDcsMode = false;
        Assert.Equal(AutoConnectAckState.Ignored, h.Controller.HandleConnect(Connect()));
        Assert.Empty(h.Host.Connects);
        Assert.Equal(0, h.Host.ConsentPrompts);
    }

    [Fact] // T4
    public void AlreadyOnSameServer_IsNoOp()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        Assert.Equal(AutoConnectAckState.Connected,
            h.Controller.HandleConnect(Connect(Target with { Host = "voice.example.com" }, seq: 2)));
        Assert.Single(h.Host.Connects);

        // Manual connection to the same server (case variant): no-op, no prompt.
        var m = new Harness();
        m.Host.SetConnected("VOICE.example.com:9937", automatic: false);
        Assert.Equal(AutoConnectAckState.Connected, m.Controller.HandleConnect(Connect()));
        Assert.Empty(m.Host.Connects);
        Assert.Equal(0, m.Host.SwitchPrompts);
    }

    [Fact] // T4: same server by resolved IP
    public void SameResolvedIp_IsNoOp()
    {
        var h = new Harness();
        h.Host.Policy = h.Host.Policy with { AllowAutomaticServerSwitching = true };
        h.Resolver.Map["203.0.113.9"] = "203.0.113.9";
        h.Resolver.Map["voice.example.com"] = "203.0.113.9";
        h.Host.SetConnected("203.0.113.9:9937", automatic: false);
        h.Controller.HandleConnect(Connect());
        Assert.Empty(h.Host.Connects);
        Assert.Equal(0, h.Host.Disconnects);
        Assert.Equal(0, h.Host.SwitchPrompts);
    }

    [Fact] // T6: default policy prompts; "Stay" is remembered for the session
    public void DifferentServer_Default_PromptsAndRemembersStay()
    {
        var h = new Harness();
        h.Host.SetConnected("other.example.com:9987", automatic: true);
        h.Host.SwitchAnswers.Enqueue(false);
        Assert.Equal(AutoConnectAckState.PendingConsent, h.Controller.HandleConnect(Connect()));
        Assert.Equal(1, h.Host.SwitchPrompts);
        Assert.Empty(h.Host.Connects);
        Assert.Equal(AutoConnectAckState.Ignored, h.Controller.HandleConnect(Connect(seq: 2)));
        Assert.Equal(1, h.Host.SwitchPrompts);
    }

    [Fact] // T6
    public void DifferentServer_SwitchAccepted_DisconnectsThenConnects()
    {
        var h = new Harness();
        h.Host.SetConnected("other.example.com:9987", automatic: false);
        h.Host.SwitchAnswers.Enqueue(true);
        h.Controller.HandleConnect(Connect());
        Assert.Equal(1, h.Host.Disconnects);
        Assert.Equal("voice.example.com:9937", Assert.Single(h.Host.Connects).OpenFreqServerAddress);
    }

    [Fact] // T6
    public void DifferentServer_AutomaticSwitchingAllowed_SwitchesAutoConnectionSilently()
    {
        var h = new Harness();
        h.Host.Policy = h.Host.Policy with { AllowAutomaticServerSwitching = true };
        h.Host.SetConnected("other.example.com:9987", automatic: true);
        Assert.Equal(AutoConnectAckState.Connecting, h.Controller.HandleConnect(Connect()));
        Assert.Equal(0, h.Host.SwitchPrompts);
        Assert.Single(h.Host.Connects);
    }

    [Fact] // T6: a manual connection is always protected by a prompt
    public void DifferentServer_ManualConnection_AlwaysPrompts()
    {
        var h = new Harness();
        h.Host.Policy = h.Host.Policy with { AllowAutomaticServerSwitching = true };
        h.Host.SetConnected("other.example.com:9987", automatic: false);
        h.Host.SwitchAnswers.Enqueue(false);
        Assert.Equal(AutoConnectAckState.PendingConsent, h.Controller.HandleConnect(Connect()));
        Assert.Equal(1, h.Host.SwitchPrompts);
        Assert.Equal(0, h.Host.Disconnects);
        Assert.Empty(h.Host.Connects);
    }

    [Fact] // section 7.4
    public void Consent_FirstAdvert_Always_EnablesAndConnects()
    {
        var h = new Harness();
        h.Host.Policy = new AutoConnectPolicy();
        h.Host.ConsentAnswers.Enqueue(new AutoConnectConsentResult(AutoConnectConsentAnswer.Always, true));
        h.Controller.HandleConnect(Connect());
        Assert.Equal(1, h.Host.ConsentPrompts);
        Assert.True(h.Host.Policy.AutoConnectEnabled);
        Assert.True(h.Host.Policy.AutoLaunchEnabled);
        Assert.True(h.Host.Policy.ConsentAsked);
        Assert.Single(h.Host.Connects);
    }

    [Fact]
    public void Consent_ConnectOnce_ConnectsWithoutEnabling()
    {
        var h = new Harness();
        h.Host.Policy = new AutoConnectPolicy();
        h.Host.ConsentAnswers.Enqueue(new AutoConnectConsentResult(AutoConnectConsentAnswer.ConnectOnce, false));
        h.Controller.HandleConnect(Connect());
        Assert.False(h.Host.Policy.AutoConnectEnabled);
        Assert.False(h.Host.Policy.AutoLaunchEnabled);
        Assert.True(h.Host.Policy.ConsentAsked);
        Assert.Single(h.Host.Connects);
    }

    [Fact]
    public void Consent_No_IsNotAskedAgain()
    {
        var h = new Harness();
        h.Host.Policy = new AutoConnectPolicy();
        h.Host.ConsentAnswers.Enqueue(new AutoConnectConsentResult(AutoConnectConsentAnswer.No, true));
        Assert.Equal(AutoConnectAckState.PendingConsent, h.Controller.HandleConnect(Connect()));
        Assert.False(h.Host.Policy.AutoConnectEnabled);
        Assert.False(h.Host.Policy.AutoLaunchEnabled);
        Assert.Empty(h.Host.Connects);
        Assert.Equal(AutoConnectAckState.Ignored, h.Controller.HandleConnect(Connect(session: "s2")));
        Assert.Equal(1, h.Host.ConsentPrompts);
        Assert.Contains(h.Host.Statuses, s => s.Contains("auto-connect is off"));
    }

    [Fact] // T10: private address from a public DCS server
    public void PrivateTargetFromPublicServer_IsRejected()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect(new AutoConnectTarget("192.168.1.1", 9987, null, false)));
        Assert.Empty(h.Host.Connects);
        h.Controller.HandleConnect(Connect(new AutoConnectTarget("192.168.1.1", 9987, null, false), session: "lan",
            dcsServer: "192.168.1.2:10308"));
        Assert.Single(h.Host.Connects);
    }

    [Fact] // T11
    public void PasswordPrompt_RememberStoresOnlyAfterSuccess_SavedPasswordUntouched()
    {
        var h = new Harness();
        h.Host.PasswordAnswers.Enqueue(new AutoConnectPasswordResult("typed", true));
        h.Controller.HandleConnect(Connect(Target with { Auth = true }));
        Assert.Equal("typed", Assert.Single(h.Host.Connects).OpenFreqPassword);
        Assert.Equal("typed", h.Host.Remembered["voice.example.com:9937"]);
        Assert.Equal("savedpw", h.Host.Saved.OpenFreqPassword);
        Assert.Equal("saved.example.com:9987", h.Host.Saved.OpenFreqServerAddress);
    }

    [Fact] // T11
    public void PasswordOrder_RememberedThenSavedForSameAddress()
    {
        var h = new Harness();
        h.Host.Remembered["voice.example.com:9937"] = "remembered";
        h.Controller.HandleConnect(Connect(Target with { Auth = true }));
        Assert.Equal("remembered", h.Host.Connects[0].OpenFreqPassword);
        Assert.Equal(0, h.Host.PasswordPrompts);

        var s = new Harness();
        s.Host.Saved.OpenFreqServerAddress = "Voice.Example.com:9937";
        s.Controller.HandleConnect(Connect(Target with { Auth = true }));
        Assert.Equal("savedpw", s.Host.Connects[0].OpenFreqPassword);
        Assert.Equal(0, s.Host.PasswordPrompts);
    }

    [Fact] // T11: wrong password prompts and stops; never loops
    public void WrongPassword_PromptsOnce_CancelStopsRetries()
    {
        var h = new Harness();
        h.Host.Remembered["voice.example.com:9937"] = "stale";
        h.Host.ConnectBehaviour = _ => throw new AuthenticationException("Bad password");
        h.Controller.HandleConnect(Connect(Target with { Auth = true }));
        Assert.Equal(1, h.Host.PasswordPrompts);
        Assert.True(h.Host.LastPromptWasRetry);
        Assert.Single(h.Host.Connects);
        Assert.Empty(h.Delay.Pending);
        Assert.Equal("stale", h.Host.Remembered["voice.example.com:9937"]);
        h.Host.RaiseLost();
        Assert.Empty(h.Delay.Pending);
    }

    [Fact] // T8
    public void Leave_DisconnectsAfterGrace()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        h.Controller.HandleLeave(Session);
        Assert.Equal(AutoConnectPhase.LeavePending, h.Controller.Phase);
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(h.Delay.Pending).Duration);
        Assert.Equal(0, h.Host.Disconnects);
        h.Delay.FireNext();
        Assert.Equal(1, h.Host.Disconnects);
        Assert.Equal(AutoConnectPhase.Idle, h.Controller.Phase);
        Assert.False(h.Host.CloseRequested);
    }

    [Fact] // T8
    public void Leave_SettingOff_KeepsConnection()
    {
        var h = new Harness();
        h.Host.Policy = h.Host.Policy with { DisconnectOnServerLeave = false };
        h.Controller.HandleConnect(Connect());
        h.Controller.HandleLeave(Session);
        Assert.Empty(h.Delay.Pending);
        Assert.Equal(0, h.Host.Disconnects);
    }

    [Fact] // T8: a leave never disconnects a manual connection
    public void Leave_ManualConnection_IsNeverDisconnected()
    {
        var h = new Harness();
        h.Host.SetConnected("voice.example.com:9937", automatic: false);
        h.Controller.HandleConnect(Connect());
        h.Controller.HandleLeave(Session);
        h.Delay.FireAll();
        Assert.Equal(0, h.Host.Disconnects);
    }

    [Fact] // T8: quick rejoin (new DCS session, same OpenFreq server) keeps the connection
    public void RejoinWithinGrace_KeepsConnection()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        h.Controller.HandleLeave(Session);
        Assert.Equal(AutoConnectAckState.Connected, h.Controller.HandleConnect(Connect(session: "s2")));
        h.Delay.FireAll();
        Assert.Equal(0, h.Host.Disconnects);
        Assert.Single(h.Host.Connects);
        Assert.Equal(AutoConnectPhase.AutoActive, h.Controller.Phase);
    }

    [Fact] // T9: slot change, respawn, loading screens, mission rotation send nothing; a
           // re-sent advert in the same session is a no-op
    public void SameSessionRepeatedAdvert_IsNoOp()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        h.Controller.HandleConnect(Connect(seq: 2));
        h.Controller.HandleConnect(Connect(seq: 3));
        Assert.Single(h.Host.Connects);
        Assert.Equal(0, h.Host.Disconnects);
        Assert.Empty(h.Delay.Pending);
    }

    [Fact] // T8: DCS crashed / was killed
    public void DcsExited_ActsAsLeave()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        Assert.True(h.Controller.HasSession);
        h.Controller.HandleDcsExited();
        Assert.False(h.Controller.HasSession);
        h.Delay.FireNext();
        Assert.Equal(1, h.Host.Disconnects);
    }

    [Fact] // T7: exponential back-off 5, 10, 20, 40, 60, 60 s while still in the DCS server
    public void ConnectionLost_RetriesWithExponentialBackoff()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        h.Host.ConnectBehaviour = _ => throw new TimeoutException();
        h.Host.RaiseLost();
        var seen = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var pending = Assert.Single(h.Delay.Pending);
            seen.Add(pending.Duration.TotalSeconds);
            h.Delay.FireNext();
        }

        Assert.Equal(new double[] { 5, 10, 20, 40, 60, 60 }, seen);
        Assert.Equal(7, h.Host.Connects.Count);

        h.Host.ConnectBehaviour = null;
        h.Delay.FireNext();
        Assert.Equal(ConnectionState.Authenticated, h.Host.ConnectionState);
        Assert.Empty(h.Delay.Pending);
    }

    [Fact]
    public void Backoff_HasTwentyPercentJitter()
    {
        var low = new DcsAutoConnectController(new FakeHost(), new FakeResolver(), NullLogger.Instance, random: () => 0);
        var high = new DcsAutoConnectController(new FakeHost(), new FakeResolver(), NullLogger.Instance, random: () => 1);
        Assert.Equal(4, low.BackoffDelay(0).TotalSeconds, 3);
        Assert.Equal(6, high.BackoffDelay(0).TotalSeconds, 3);
        Assert.Equal(72, high.BackoffDelay(10).TotalSeconds, 3);
    }

    [Fact] // T7: stops on user disconnect
    public void UserDisconnect_StopsReconnects()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        h.Host.RaiseUserDisconnect();
        Assert.Empty(h.Delay.Pending);
        Assert.Equal(AutoConnectAckState.Ignored, h.Controller.HandleConnect(Connect(seq: 2)));
        Assert.Single(h.Host.Connects);
    }

    [Fact] // T7: stops when the session ends; off when AutoReconnectEnabled is false
    public void Backoff_CancelledByLeave_AndOffWhenDisabled()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        h.Host.RaiseLost();
        Assert.Single(h.Delay.Pending);
        h.Controller.HandleLeave(Session);
        h.Delay.FireAll();
        Assert.Single(h.Host.Connects);

        var d = new Harness();
        d.Host.Policy = d.Host.Policy with { AutoReconnectEnabled = false };
        d.Controller.HandleConnect(Connect());
        d.Host.RaiseLost();
        Assert.Empty(d.Delay.Pending);
    }

    [Fact] // T7: version mismatch stops retries
    public void VersionMismatch_StopsRetries()
    {
        var h = new Harness();
        h.Host.ConnectBehaviour = _ => throw new VersionMismatchException("1.0.0", "2.0.0");
        h.Controller.HandleConnect(Connect());
        Assert.Empty(h.Delay.Pending);
        h.Host.RaiseLost();
        Assert.Empty(h.Delay.Pending);
    }

    [Fact] // section 8.4
    public void CloseAppOnServerLeave_OnlyWhenEnabledAndStartedByDcs()
    {
        var h = new Harness(launchedByDcs: true);
        h.Host.Policy = h.Host.Policy with { CloseAppOnServerLeave = true };
        h.Controller.HandleConnect(Connect());
        h.Controller.HandleLeave(Session);
        h.Delay.FireAll();
        Assert.True(h.Host.CloseRequested);

        var notByDcs = new Harness(launchedByDcs: false);
        notByDcs.Host.Policy = notByDcs.Host.Policy with { CloseAppOnServerLeave = true };
        notByDcs.Controller.HandleConnect(Connect());
        notByDcs.Controller.HandleLeave(Session);
        notByDcs.Delay.FireAll();
        Assert.False(notByDcs.Host.CloseRequested);

        var off = new Harness(launchedByDcs: true);
        off.Controller.HandleConnect(Connect());
        off.Controller.HandleLeave(Session);
        off.Delay.FireAll();
        Assert.False(off.Host.CloseRequested);
    }

    [Fact]
    public void LeaveForUnknownSession_IsIgnored()
    {
        var h = new Harness();
        h.Controller.HandleConnect(Connect());
        h.Controller.HandleLeave("other");
        Assert.Empty(h.Delay.Pending);
    }

    // ------------------------------------------------------------------------------------------

    internal sealed class FakeResolver : IAutoConnectResolver
    {
        public readonly Dictionary<string, string> Map = new()
        {
            ["voice.example.com"] = "198.51.100.7",
            ["other.example.com"] = "198.51.100.8",
        };

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(host, out var ip) && !Map.ContainsKey(host))
                return Task.FromResult<IReadOnlyList<IPAddress>>([ip]);
            return Task.FromResult<IReadOnlyList<IPAddress>>(
                Map.TryGetValue(host, out var mapped) ? [IPAddress.Parse(mapped)] : []);
        }
    }

    internal sealed class ManualDelay
    {
        public sealed record Entry(TimeSpan Duration, TaskCompletionSource Tcs);

        public readonly List<Entry> Pending = [];

        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource();
            var entry = new Entry(duration, tcs);
            Pending.Add(entry);
            ct.Register(() =>
            {
                Pending.Remove(entry);
                tcs.TrySetCanceled();
            });
            return tcs.Task;
        }

        public void FireNext()
        {
            var entry = Pending[0];
            Pending.RemoveAt(0);
            // On a pool thread (default scheduler) the awaiting workflow continues inline, so it
            // has finished (with the synchronous fakes) by the time Wait returns.
            Task.Run(() => entry.Tcs.TrySetResult()).Wait();
        }

        public void FireAll()
        {
            while (Pending.Count > 0) FireNext();
        }
    }

    internal sealed class FakeHost : IAutoConnectHost
    {
        public bool IsDcsMode { get; set; } = true;
        public AutoConnectPolicy Policy { get; set; } = new(AutoConnectEnabled: true, ConsentAsked: true);
        public readonly OpenFreqSettings Saved = new()
        {
            OpenFreqServerAddress = "saved.example.com:9987", OpenFreqPassword = "savedpw", DisplayName = "Pilot",
        };
        public readonly Dictionary<string, string> Remembered = new();
        public readonly List<OpenFreqSettings> Connects = [];
        public readonly Queue<bool> SwitchAnswers = new();
        public readonly Queue<AutoConnectConsentResult> ConsentAnswers = new();
        public readonly Queue<AutoConnectPasswordResult?> PasswordAnswers = new();
        public readonly List<string> Statuses = [];
        public Func<OpenFreqSettings, Task>? ConnectBehaviour { get; set; }
        public int Disconnects, SwitchPrompts, ConsentPrompts, PasswordPrompts;
        public bool LastPromptWasRetry, CloseRequested;

        public ConnectionState ConnectionState { get; private set; } = ConnectionState.Disconnected;
        public string? CurrentServerAddress { get; private set; }
        public bool CurrentConnectionIsAutomatic { get; private set; }
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public void SetConnected(string address, bool automatic)
        {
            CurrentServerAddress = address;
            CurrentConnectionIsAutomatic = automatic;
            ConnectionState = ConnectionState.Authenticated;
        }

        public void RaiseLost()
        {
            ConnectionState = ConnectionState.Disconnected;
            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, DisconnectReason.ConnectionLost));
        }

        public void RaiseUserDisconnect()
        {
            ConnectionState = ConnectionState.Disconnected;
            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, DisconnectReason.UserRequested));
        }

        public AutoConnectPolicy GetPolicy() => Policy;

        public void ApplyConsent(bool? autoConnectEnabled, bool? autoLaunchEnabled) =>
            Policy = Policy with
            {
                ConsentAsked = true,
                AutoConnectEnabled = autoConnectEnabled ?? Policy.AutoConnectEnabled,
                AutoLaunchEnabled = autoLaunchEnabled ?? Policy.AutoLaunchEnabled,
            };

        public string? GetRememberedPassword(string address) => Remembered.GetValueOrDefault(address);
        public void RememberPassword(string address, string password) => Remembered[address] = password;

        public OpenFreqSettings GetSettingsSnapshot() => new()
        {
            OpenFreqServerAddress = Saved.OpenFreqServerAddress,
            OpenFreqPassword = Saved.OpenFreqPassword,
            DisplayName = Saved.DisplayName,
        };

        public async Task ConnectAutomaticAsync(OpenFreqSettings sessionSnapshot, TimeSpan timeout)
        {
            Connects.Add(sessionSnapshot);
            CurrentServerAddress = sessionSnapshot.OpenFreqServerAddress;
            CurrentConnectionIsAutomatic = true;
            if (ConnectBehaviour != null)
            {
                ConnectionState = ConnectionState.Disconnected;
                ConnectionStateChanged?.Invoke(this,
                    new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, DisconnectReason.ConnectionFailed));
                await ConnectBehaviour(sessionSnapshot);
            }

            ConnectionState = ConnectionState.Authenticated;
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Authenticated));
        }

        public Task DisconnectAsync()
        {
            Disconnects++;
            ConnectionState = ConnectionState.Disconnected;
            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, DisconnectReason.UserRequested));
            return Task.CompletedTask;
        }

        public Task<AutoConnectConsentResult> PromptConsentAsync(string dcsServer, AutoConnectTarget target)
        {
            ConsentPrompts++;
            return Task.FromResult(ConsentAnswers.Count > 0
                ? ConsentAnswers.Dequeue()
                : new AutoConnectConsentResult(AutoConnectConsentAnswer.No, false));
        }

        public Task<bool> PromptSwitchAsync(string currentAddress, AutoConnectTarget target, string dcsServer)
        {
            SwitchPrompts++;
            return Task.FromResult(SwitchAnswers.Count > 0 && SwitchAnswers.Dequeue());
        }

        public Task<AutoConnectPasswordResult?> PromptPasswordAsync(AutoConnectTarget target, bool previousAttemptFailed)
        {
            PasswordPrompts++;
            LastPromptWasRetry = previousAttemptFailed;
            return Task.FromResult(PasswordAnswers.Count > 0 ? PasswordAnswers.Dequeue() : null);
        }

        public void ShowStatus(string message) => Statuses.Add(message);
        public void RequestClose() => CloseRequested = true;
    }
}
