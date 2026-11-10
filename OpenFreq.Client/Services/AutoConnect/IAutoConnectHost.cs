using System;
using System.Threading.Tasks;
using OpenFreq.Common;
using OpenFreqClient.Models;

namespace OpenFreqClient.Services.AutoConnect;

/// <summary>The user's auto-connect settings (see OpenFreqSettings), read fresh for every
/// decision so a change in the Settings page applies immediately.</summary>
public sealed record AutoConnectPolicy(
    bool AutoConnectEnabled = false,
    bool AutoLaunchEnabled = false,
    bool AutoReconnectEnabled = true,
    bool DisconnectOnServerLeave = true,
    bool AllowAutomaticServerSwitching = false,
    bool CloseAppOnServerLeave = false,
    bool ConsentAsked = false);

public enum AutoConnectConsentAnswer
{
    No,
    ConnectOnce,
    Always,
}

public sealed record AutoConnectConsentResult(AutoConnectConsentAnswer Answer, bool EnableAutoLaunch);

public sealed record AutoConnectPasswordResult(string Password, bool Remember);

/// <summary>
/// Everything <see cref="DcsAutoConnectController"/> needs from the app: settings, the existing
/// connection path (IOpenFreqService Initialize/ConnectAsync/DisconnectAsync through
/// MainWindowViewModel) and the UI. Implemented by DcsAutoConnectHost; faked in tests.
/// </summary>
public interface IAutoConnectHost
{
    bool IsDcsMode { get; }

    AutoConnectPolicy GetPolicy();

    /// <summary>Records the consent dialog's outcome. Null leaves a setting unchanged.</summary>
    void ApplyConsent(bool? autoConnectEnabled, bool? autoLaunchEnabled);

    string? GetRememberedPassword(string address);

    /// <summary>Only ever called after the user ticked "Remember" in the password prompt.</summary>
    void RememberPassword(string address, string password);

    /// <summary>A fresh copy of the saved settings. Changing it never changes the saved ones.</summary>
    OpenFreqSettings GetSettingsSnapshot();

    ConnectionState ConnectionState { get; }

    /// <summary>"host:port" of the current (or last attempted) OpenFreq connection.</summary>
    string? CurrentServerAddress { get; }

    /// <summary>True when the current connection was made by auto-connect, false when the user
    /// connected manually (or BMS did).</summary>
    bool CurrentConnectionIsAutomatic { get; }

    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Connects with a per-session settings snapshot; throws like
    /// IOpenFreqService.ConnectAsync (AuthenticationException for a bad password,
    /// VersionMismatchException, TimeoutException, ...).</summary>
    Task ConnectAutomaticAsync(OpenFreqSettings sessionSnapshot, TimeSpan timeout);

    Task DisconnectAsync();

    Task<AutoConnectConsentResult> PromptConsentAsync(string dcsServer, AutoConnectTarget target);

    Task<bool> PromptSwitchAsync(string currentAddress, AutoConnectTarget target, string dcsServer);

    /// <summary>Null when the user cancelled.</summary>
    Task<AutoConnectPasswordResult?> PromptPasswordAsync(AutoConnectTarget target, bool previousAttemptFailed);

    void ShowStatus(string message);

    /// <summary>Graceful app shutdown (CloseAppOnServerLeave).</summary>
    void RequestClose();
}
