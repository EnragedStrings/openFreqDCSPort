using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;

namespace OpenFreqClient.Services.AutoConnect;

public sealed record AutoConnectTimings
{
    public TimeSpan LeaveGrace { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BackoffInitial { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(60);
    public double BackoffJitter { get; init; } = 0.2;
}

public enum AutoConnectPhase
{
    Idle,
    AutoActive,
    Backoff,
    LeavePending,
}

/// <summary>
/// Policy and state machine for DCS auto-connect (docs/autoconnect/AUTOCONNECT_DESIGN.md
/// sections 5.3, 7.3-7.5 and 9). Thread-safe: every state change happens under one lock, and
/// the async workflows (prompts, connect, grace, backoff) re-check the session after each await.
///
/// Session boundaries come only from the hook's connect/leave messages (onNetConnect /
/// onNetDisconnect in DCS) and from DCS exiting -- slot changes, respawns, loading screens and
/// mission rotation produce no message, so they never touch the connection.
/// </summary>
public sealed class DcsAutoConnectController : IDisposable
{
    private readonly object _gate = new();
    private readonly IAutoConnectHost _host;
    private readonly IAutoConnectResolver _resolver;
    private readonly ILogger _logger;
    private readonly AutoConnectTimings _timings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _random;
    private readonly bool _launchedByDcs;
    private readonly List<Task> _work = [];

    // Current DCS session (one join of one DCS server).
    private string? _sessionId;
    private string _dcsServer = "";
    private bool _sessionLeft;
    private bool _consentOnceForSession;
    private bool _userStoppedSession;
    private bool _stopRetries;
    private readonly HashSet<string> _declinedSwitch = new(StringComparer.OrdinalIgnoreCase);

    // The OpenFreq connection auto-connect made (never a manual one).
    private bool _ownsConnection;
    private AutoConnectTarget? _ownedTarget;
    private string _ownedPassword = "";
    private bool _connecting;
    private bool _selfDisconnecting;

    // At most one connect/switch/consent workflow at a time.
    private int _workId;
    private int _activeWorkId;
    private AutoConnectRequest? _deferred;
    private bool _promptOpen;

    private CancellationTokenSource? _graceCts;
    private CancellationTokenSource? _backoffCts;
    private int _backoffAttempt;
    private bool _disposed;

    public DcsAutoConnectController(IAutoConnectHost host, IAutoConnectResolver resolver, ILogger logger,
        AutoConnectTimings? timings = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? random = null, bool launchedByDcs = false)
    {
        _host = host;
        _resolver = resolver;
        _logger = logger;
        _timings = timings ?? new AutoConnectTimings();
        _delay = delay ?? Task.Delay;
        _random = random ?? Random.Shared.NextDouble;
        _launchedByDcs = launchedByDcs;
        _host.ConnectionStateChanged += OnHostConnectionStateChanged;
    }

    public AutoConnectPhase Phase
    {
        get
        {
            lock (_gate)
            {
                if (_graceCts != null) return AutoConnectPhase.LeavePending;
                if (_backoffCts != null) return AutoConnectPhase.Backoff;
                if (_ownsConnection && _sessionId != null) return AutoConnectPhase.AutoActive;
                return AutoConnectPhase.Idle;
            }
        }
    }

    /// <summary>True while a DCS session is known and not yet over (drives the DCS process watch).</summary>
    public bool HasSession
    {
        get
        {
            lock (_gate) return _sessionId != null && !_sessionLeft;
        }
    }

    /// <summary>Back-off delay before retry number <paramref name="attempt"/> (0-based):
    /// 5, 10, 20, 40, 60, 60, ... seconds, each with +/-20% jitter.</summary>
    public TimeSpan BackoffDelay(int attempt)
    {
        var baseSeconds = Math.Min(_timings.BackoffInitial.TotalSeconds * Math.Pow(2, Math.Min(attempt, 16)),
            _timings.BackoffMax.TotalSeconds);
        var jitter = 1 + (_random() * 2 - 1) * _timings.BackoffJitter;
        return TimeSpan.FromSeconds(baseSeconds * jitter);
    }

    public AutoConnectAckState Handle(AutoConnectRequest request)
    {
        switch (request.Action)
        {
            case AutoConnectAction.Connect when request.Target != null:
                return HandleConnect(request);
            case AutoConnectAction.Leave:
                HandleLeave(request.SessionId, request.Reason, request.Code);
                return AutoConnectAckState.Ignored;
            case AutoConnectAction.Ping:
                return _host.ConnectionState switch
                {
                    ConnectionState.Authenticated => AutoConnectAckState.Connected,
                    ConnectionState.Disconnected => AutoConnectAckState.Ignored,
                    _ => AutoConnectAckState.Connecting,
                };
            default:
                return AutoConnectAckState.Rejected;
        }
    }

    public AutoConnectAckState HandleConnect(AutoConnectRequest request)
    {
        // Workflows are started after the lock is released, so nothing they do synchronously
        // (prompts, connect) ever runs under the lock.
        Func<Task>? start = null;
        var ack = Decide(request, ref start);
        if (start != null)
            Track(start());
        return ack;
    }

    private AutoConnectAckState Decide(AutoConnectRequest request, ref Func<Task>? start)
    {
        var target = request.Target!;
        lock (_gate)
        {
            if (_disposed)
                return AutoConnectAckState.Ignored;

            if (!_host.IsDcsMode)
            {
                _logger.LogInformation("DCS auto-connect: offer of {Address} ignored (not in DCS mode)", target.Address);
                return AutoConnectAckState.Ignored;
            }

            var previousSessionEnded = false;
            if (_sessionId != request.SessionId)
            {
                // A new DCS session. If the previous one ended (or its leave was lost), the
                // connection it made may be reused or switched without asking (see below).
                previousSessionEnded = _sessionId != null;
                CancelGraceLocked();
                CancelBackoffLocked();
                _sessionId = request.SessionId;
                _dcsServer = request.DcsServer;
                _sessionLeft = false;
                _consentOnceForSession = false;
                _userStoppedSession = false;
                _stopRetries = false;
                _declinedSwitch.Clear();
                _logger.LogInformation("DCS auto-connect: session {Session} on DCS server {DcsServer}",
                    request.SessionId, request.DcsServer);
            }
            else if (_sessionLeft)
            {
                return AutoConnectAckState.Ignored;
            }

            if (_userStoppedSession)
                return AutoConnectAckState.Ignored;

            var state = _host.ConnectionState;
            if (state != ConnectionState.Disconnected &&
                AutoConnectTargetValidator.SameAddress(_host.CurrentServerAddress, target.Address))
            {
                // Already on (or connecting to) this server: no-op. A rejoin within the grace
                // period keeps the auto-made connection; a manual one stays manual.
                if (_host.CurrentConnectionIsAutomatic && !_ownsConnection)
                {
                    _ownsConnection = true;
                    _ownedTarget = target;
                }

                return state == ConnectionState.Authenticated
                    ? AutoConnectAckState.Connected
                    : AutoConnectAckState.Connecting;
            }

            if (_activeWorkId != 0)
            {
                // Re-evaluated when the running workflow ends (it may belong to an earlier
                // session, or be a prompt the user has not answered yet).
                _deferred = request;
                return _promptOpen ? AutoConnectAckState.PendingConsent : AutoConnectAckState.Connecting;
            }

            var policy = _host.GetPolicy();
            if (!policy.AutoConnectEnabled && !_consentOnceForSession)
            {
                if (!policy.ConsentAsked)
                {
                    var id = BeginWorkLocked(prompt: true);
                    start = () => ConsentWorkflowAsync(id, request);
                    return AutoConnectAckState.PendingConsent;
                }

                _host.ShowStatus($"DCS server offers OpenFreq at {target.Address} (auto-connect is off in Settings)");
                return AutoConnectAckState.Ignored;
            }

            if (state == ConnectionState.Disconnected)
            {
                var id = BeginWorkLocked(prompt: false);
                start = () => ConnectWorkflowAsync(id, request.SessionId, target, switchFrom: null, promptForSwitch: false);
                return AutoConnectAckState.Connecting;
            }

            // Connected (or connecting) to a different server: switching policy (section 7.5).
            if (_declinedSwitch.Contains(target.Address))
                return AutoConnectAckState.Ignored;

            var automatic = _host.CurrentConnectionIsAutomatic;
            var silent = automatic && (policy.AllowAutomaticServerSwitching ||
                                       (previousSessionEnded && policy.DisconnectOnServerLeave));
            var workId = BeginWorkLocked(prompt: !silent);
            var from = _host.CurrentServerAddress ?? "";
            start = () => ConnectWorkflowAsync(workId, request.SessionId, target, from, promptForSwitch: !silent);
            return silent ? AutoConnectAckState.Connecting : AutoConnectAckState.PendingConsent;
        }
    }

    public void HandleLeave(string sessionId, string? reason = null, int? code = null)
    {
        lock (_gate)
        {
            if (_disposed || sessionId != _sessionId || _sessionLeft)
                return;

            _sessionLeft = true;
            CancelBackoffLocked();
            _logger.LogInformation("DCS auto-connect: left DCS server (session {Session}, {Reason} {Code})",
                sessionId, reason, code);

            var policy = _host.GetPolicy();
            var disconnect = policy.DisconnectOnServerLeave && _ownsConnection;
            var closeApp = policy.CloseAppOnServerLeave && _launchedByDcs;
            if (!disconnect && !closeApp)
            {
                _sessionId = null;
                return;
            }

            var cts = new CancellationTokenSource();
            _graceCts = cts;
            Track(LeaveGraceWorkflowAsync(sessionId, cts, disconnect, closeApp));
        }
    }

    /// <summary>DCS exited without onNetDisconnect (crash, task kill): same as a leave.</summary>
    public void HandleDcsExited()
    {
        string? session;
        lock (_gate)
        {
            if (_sessionLeft) return;
            session = _sessionId;
        }

        if (session != null)
        {
            _logger.LogInformation("DCS auto-connect: DCS is no longer running");
            HandleLeave(session, "DCS exited");
        }
    }

    /// <summary>Completes when every workflow started so far has finished (tests).</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate) return Task.WhenAll(_work.ToArray());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CancelGraceLocked();
            CancelBackoffLocked();
        }

        _host.ConnectionStateChanged -= OnHostConnectionStateChanged;
    }

    // ---------------------------------------------------------------------------------------
    // Workflows
    // ---------------------------------------------------------------------------------------

    private async Task ConsentWorkflowAsync(int workId, AutoConnectRequest request)
    {
        AutoConnectConsentResult result;
        try
        {
            result = await _host.PromptConsentAsync(request.DcsServer, request.Target!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DCS auto-connect: consent prompt failed");
            EndWork(workId);
            return;
        }

        bool proceed;
        lock (_gate)
        {
            _host.ApplyConsent(
                result.Answer == AutoConnectConsentAnswer.Always ? true : null,
                result.Answer != AutoConnectConsentAnswer.No && result.EnableAutoLaunch ? true : null);
            _logger.LogInformation("DCS auto-connect: consent answer {Answer} (auto-launch {Launch})",
                result.Answer, result.EnableAutoLaunch);
            EndWorkLocked(workId);
            proceed = result.Answer != AutoConnectConsentAnswer.No && IsCurrentLocked(request.SessionId);
            // Covers "Always" too: the settings change may reach the host asynchronously.
            if (proceed)
                _consentOnceForSession = true;
        }

        if (proceed)
            HandleConnect(request);
        ReplayDeferred();
    }

    private async Task ConnectWorkflowAsync(int workId, string sessionId, AutoConnectTarget target,
        string? switchFrom, bool promptForSwitch)
    {
        try
        {
            string dcsServer;
            lock (_gate) dcsServer = _dcsServer;

            var rejection = await CheckAddressAsync(target, dcsServer).ConfigureAwait(false);
            if (rejection != null)
            {
                _logger.LogWarning("DCS auto-connect: rejected {Address}: {Reason}", target.Address, rejection);
                _host.ShowStatus($"DCS auto-connect: ignored {target.Address} ({rejection})");
                return;
            }

            if (switchFrom != null && await SameResolvedServerAsync(switchFrom, target).ConfigureAwait(false))
            {
                _logger.LogInformation("DCS auto-connect: {Address} is the server already in use ({Current})",
                    target.Address, switchFrom);
                return;
            }

            if (switchFrom != null && promptForSwitch)
            {
                var accepted = await _host.PromptSwitchAsync(switchFrom, target, dcsServer).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!IsCurrentLocked(sessionId)) return;
                    if (!accepted)
                    {
                        // "Stay" is remembered for this DCS session: no re-prompt.
                        _declinedSwitch.Add(target.Address);
                        _logger.LogInformation("DCS auto-connect: user stayed on {Current}", switchFrom);
                        return;
                    }
                }
            }

            var password = await ResolvePasswordAsync(target).ConfigureAwait(false);
            if (password == null)
            {
                _host.ShowStatus($"DCS auto-connect: no password entered for {target.Address}");
                return;
            }

            lock (_gate)
            {
                if (!IsCurrentLocked(sessionId)) return;
            }

            if (switchFrom != null && _host.ConnectionState != ConnectionState.Disconnected)
                await DisconnectSelfAsync().ConfigureAwait(false);

            await ConnectOnceAsync(sessionId, target, password.Password, password.Remember).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DCS auto-connect: connect workflow failed");
        }
        finally
        {
            EndWork(workId);
        }
    }

    private async Task ConnectOnceAsync(string sessionId, AutoConnectTarget target, string password, bool remember)
    {
        var snapshot = _host.GetSettingsSnapshot();
        snapshot.OpenFreqServerAddress = target.Address;
        snapshot.OpenFreqPassword = password;

        lock (_gate)
        {
            if (!IsCurrentLocked(sessionId)) return;
            _ownsConnection = true;
            _ownedTarget = target;
            _ownedPassword = password;
            _connecting = true;
        }

        try
        {
            _logger.LogInformation("DCS auto-connect: connecting to {Address}", target.Address);
            await _host.ConnectAutomaticAsync(snapshot, _timings.ConnectTimeout).ConfigureAwait(false);
            lock (_gate)
            {
                _connecting = false;
                _backoffAttempt = 0;
            }

            if (remember)
                _host.RememberPassword(target.Address, password);
            _host.ShowStatus($"Connected via DCS auto-connect: {target.Address}");
        }
        catch (AuthenticationException)
        {
            lock (_gate)
            {
                _connecting = false;
                _ownsConnection = false;
            }

            _logger.LogWarning("DCS auto-connect: {Address} rejected the password", target.Address);
            // Never loops: each further attempt needs the user to type a password.
            var retry = await _host.PromptPasswordAsync(target, previousAttemptFailed: true).ConfigureAwait(false);
            if (retry == null)
            {
                lock (_gate) _stopRetries = true;
                _host.ShowStatus($"DCS auto-connect stopped: wrong password for {target.Address}");
                return;
            }

            await ConnectOnceAsync(sessionId, target, retry.Password, retry.Remember).ConfigureAwait(false);
        }
        catch (VersionMismatchException ex)
        {
            lock (_gate)
            {
                _connecting = false;
                _ownsConnection = false;
                _stopRetries = true;
            }

            _host.ShowStatus($"DCS auto-connect stopped: version mismatch (client {ex.ClientVersion}, server {ex.ServerVersion})");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DCS auto-connect: connecting to {Address} failed: {Message}", target.Address, ex.Message);
            lock (_gate)
            {
                _connecting = false;
                ScheduleBackoffLocked();
            }
        }
    }

    private async Task BackoffWorkflowAsync(string sessionId, CancellationTokenSource cts, TimeSpan delay)
    {
        try
        {
            await _delay(delay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        AutoConnectTarget target;
        string password;
        int workId;
        lock (_gate)
        {
            if (!ReferenceEquals(_backoffCts, cts) || cts.IsCancellationRequested) return;
            _backoffCts = null;
            cts.Dispose();
            if (!IsCurrentLocked(sessionId) || _ownedTarget == null || !_ownsConnection || _activeWorkId != 0 ||
                _host.ConnectionState != ConnectionState.Disconnected)
                return;
            target = _ownedTarget;
            password = _ownedPassword;
            workId = BeginWorkLocked(prompt: false);
        }

        try
        {
            _logger.LogInformation("DCS auto-connect: reconnect attempt to {Address}", target.Address);
            await ConnectOnceAsync(sessionId, target, password, remember: false).ConfigureAwait(false);
        }
        finally
        {
            EndWork(workId);
        }
    }

    private async Task LeaveGraceWorkflowAsync(string sessionId, CancellationTokenSource cts, bool disconnect,
        bool closeApp)
    {
        try
        {
            await _delay(_timings.LeaveGrace, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool doDisconnect;
        lock (_gate)
        {
            if (!ReferenceEquals(_graceCts, cts) || cts.IsCancellationRequested) return;
            _graceCts = null;
            cts.Dispose();
            if (_sessionId != sessionId) return; // rejoined meanwhile
            _sessionId = null;
            doDisconnect = disconnect && _ownsConnection && _host.CurrentConnectionIsAutomatic &&
                           _host.ConnectionState != ConnectionState.Disconnected;
            _ownsConnection = false;
            _ownedTarget = null;
        }

        if (doDisconnect)
        {
            _logger.LogInformation("DCS auto-connect: disconnecting after leaving the DCS server");
            await DisconnectSelfAsync().ConfigureAwait(false);
            _host.ShowStatus("Disconnected: left the DCS server");
        }

        if (closeApp)
        {
            _logger.LogInformation("DCS auto-connect: closing (CloseAppOnServerLeave, started by DCS)");
            _host.RequestClose();
        }
    }

    private async Task DisconnectSelfAsync()
    {
        lock (_gate)
        {
            _selfDisconnecting = true;
            _ownsConnection = false;
        }

        try
        {
            await _host.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _selfDisconnecting = false;
        }
    }

    private void OnHostConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (e.State == ConnectionState.Authenticated)
            {
                if (!_host.CurrentConnectionIsAutomatic)
                {
                    // The user connected manually: never touch that connection.
                    _ownsConnection = false;
                    CancelBackoffLocked();
                }
                else
                {
                    _backoffAttempt = 0;
                }

                return;
            }

            if (e.State != ConnectionState.Disconnected || _selfDisconnecting || _connecting || !_ownsConnection)
                return;

            if (e.Reason == DisconnectReason.UserRequested)
            {
                _logger.LogInformation("DCS auto-connect: user disconnected; no automatic reconnect this session");
                _ownsConnection = false;
                _userStoppedSession = true;
                CancelBackoffLocked();
                CancelGraceLocked();
                return;
            }

            if (_backoffCts == null && _activeWorkId == 0)
                ScheduleBackoffLocked();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private void ScheduleBackoffLocked()
    {
        if (_disposed || _stopRetries || _userStoppedSession || _sessionId == null || _sessionLeft ||
            !_ownsConnection || _ownedTarget == null || _backoffCts != null || !_host.GetPolicy().AutoReconnectEnabled)
            return;

        var delay = BackoffDelay(_backoffAttempt++);
        _logger.LogInformation("DCS auto-connect: retrying {Address} in {Delay:F0} s", _ownedTarget.Address,
            delay.TotalSeconds);
        var cts = new CancellationTokenSource();
        _backoffCts = cts;
        Track(BackoffWorkflowAsync(_sessionId, cts, delay));
    }

    private async Task<string?> CheckAddressAsync(AutoConnectTarget target, string dcsServer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var targetAddresses = await _resolver.ResolveAsync(target.Host, timeout.Token).ConfigureAwait(false);
        var dcsHost = AutoConnectTargetValidator.DcsServerHost(dcsServer);
        IReadOnlyList<IPAddress> dcsAddresses = dcsHost.Length == 0
            ? []
            : await _resolver.ResolveAsync(dcsHost, timeout.Token).ConfigureAwait(false);
        return AutoConnectTargetValidator.CheckResolved(targetAddresses, dcsAddresses);
    }

    private async Task<bool> SameResolvedServerAsync(string currentAddress, AutoConnectTarget target)
    {
        var current = AutoConnectTargetValidator.Normalize(currentAddress);
        if (current == null || current.Value.Port != target.Port)
            return false;
        if (string.Equals(current.Value.Host, target.Host, StringComparison.OrdinalIgnoreCase))
            return true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var a = await _resolver.ResolveAsync(current.Value.Host, timeout.Token).ConfigureAwait(false);
        var b = await _resolver.ResolveAsync(target.Host, timeout.Token).ConfigureAwait(false);
        return a.Any(x => b.Any(y => x.Equals(y)));
    }

    /// <summary>Password order (section 7.3): remembered for this server, then the saved password
    /// if the saved address is this server, then a prompt when the server needs one. Never from
    /// the advert.</summary>
    private async Task<AutoConnectPasswordResult?> ResolvePasswordAsync(AutoConnectTarget target)
    {
        var remembered = _host.GetRememberedPassword(target.Address);
        if (remembered != null)
            return new AutoConnectPasswordResult(remembered, false);

        var saved = _host.GetSettingsSnapshot();
        if (AutoConnectTargetValidator.SameAddress(saved.OpenFreqServerAddress, target.Address) &&
            !string.IsNullOrEmpty(saved.OpenFreqPassword))
            return new AutoConnectPasswordResult(saved.OpenFreqPassword, false);

        if (!target.Auth)
            return new AutoConnectPasswordResult("", false);

        return await _host.PromptPasswordAsync(target, previousAttemptFailed: false).ConfigureAwait(false);
    }

    private bool IsCurrentLocked(string sessionId) => !_disposed && _sessionId == sessionId && !_sessionLeft;

    private int BeginWorkLocked(bool prompt)
    {
        _activeWorkId = ++_workId;
        _promptOpen = prompt;
        return _activeWorkId;
    }

    private void EndWork(int workId)
    {
        lock (_gate) EndWorkLocked(workId);
        ReplayDeferred();
    }

    /// <summary>Handles the newest request that arrived while a workflow was running, if its
    /// session is still current. Already-connected targets make this a no-op.</summary>
    private void ReplayDeferred()
    {
        AutoConnectRequest? request;
        lock (_gate)
        {
            request = _deferred;
            if (request == null || _activeWorkId != 0) return;
            _deferred = null;
            if (!IsCurrentLocked(request.SessionId)) return;
        }

        HandleConnect(request);
    }

    private void EndWorkLocked(int workId)
    {
        if (_activeWorkId != workId) return;
        _activeWorkId = 0;
        _promptOpen = false;
    }

    private void CancelGraceLocked()
    {
        _graceCts?.Cancel();
        _graceCts = null;
    }

    private void CancelBackoffLocked()
    {
        _backoffCts?.Cancel();
        _backoffCts = null;
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _work.RemoveAll(t => t.IsCompleted);
            _work.Add(task);
        }

        task.ContinueWith(t => _logger.LogWarning(t.Exception, "DCS auto-connect workflow faulted"),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
