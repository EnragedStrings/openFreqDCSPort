using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DialogHostAvalonia;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;
using OpenFreqClient.Views;

namespace OpenFreqClient.Services.AutoConnect;

/// <summary>
/// Binds <see cref="DcsAutoConnectController"/> to the real app: settings from
/// <see cref="SettingsViewModel"/>, connections through <see cref="MainWindowViewModel"/>'s
/// shared connect path, prompts in the main window's dialog host. Everything UI-related is
/// marshalled to the UI thread; nothing here steals focus from a running DCS.
/// </summary>
public sealed class DcsAutoConnectHost : IAutoConnectHost, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly SettingsViewModel _settings;
    private readonly IOpenFreqService _openFreqService;
    private readonly ILogger _logger;

    public DcsAutoConnectHost(MainWindowViewModel main, SettingsViewModel settings, IOpenFreqService openFreqService,
        ILogger logger)
    {
        _main = main;
        _settings = settings;
        _openFreqService = openFreqService;
        _logger = logger;
        _openFreqService.ConnectionStateChanged += OnServiceConnectionStateChanged;
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public bool IsDcsMode => _settings.ConnectionMode == IOpenFreqService.Mode.DCS;

    public ConnectionState ConnectionState => _main.OpenFreqConnectionState;

    public string? CurrentServerAddress => _main.CurrentServerAddress;

    public bool CurrentConnectionIsAutomatic => _main.CurrentConnectionIsAutomatic;

    public AutoConnectPolicy GetPolicy() => new(
        _settings.AutoConnectEnabled,
        _settings.AutoLaunchEnabled,
        _settings.AutoReconnectEnabled,
        _settings.DisconnectOnServerLeave,
        _settings.AllowAutomaticServerSwitching,
        _settings.CloseAppOnServerLeave,
        _settings.AutoConnectConsentAsked);

    public void ApplyConsent(bool? autoConnectEnabled, bool? autoLaunchEnabled) => Dispatcher.UIThread.Post(() =>
    {
        _settings.AutoConnectConsentAsked = true;
        if (autoConnectEnabled.HasValue) _settings.AutoConnectEnabled = autoConnectEnabled.Value;
        if (autoLaunchEnabled.HasValue) _settings.AutoLaunchEnabled = autoLaunchEnabled.Value;
    });

    public string? GetRememberedPassword(string address) => _settings.GetAutoConnectServerPassword(address);

    public void RememberPassword(string address, string password) =>
        Dispatcher.UIThread.Post(() => _settings.RememberAutoConnectServerPassword(address, password));

    public OpenFreqSettings GetSettingsSnapshot() => OnUiThread(_settings.GetSettings);

    public Task ConnectAutomaticAsync(OpenFreqSettings sessionSnapshot, TimeSpan timeout) =>
        Dispatcher.UIThread.InvokeAsync(() => _main.ConnectAutomaticAsync(sessionSnapshot, timeout));

    public Task DisconnectAsync() => Dispatcher.UIThread.InvokeAsync(_main.DisconnectFromServerAsync);

    public async Task<AutoConnectConsentResult> PromptConsentAsync(string dcsServer, AutoConnectTarget target)
    {
        var vm = new AutoConnectDialogViewModel(
            "DCS server offers OpenFreq voice",
            $"The DCS server you joined ({Describe(dcsServer)}) offers OpenFreq at {target.Address}.\n\n" +
            "Connect automatically whenever a DCS server you join offers OpenFreq? " +
            "Your saved server address and password are not changed. You can change this later under Settings > DCS multiplayer.",
            primaryText: "Always", primaryResult: "always",
            secondaryText: "Connect now", secondaryResult: "once",
            cancelText: "No", cancelResult: "no",
            checkboxText: "Also start OpenFreq automatically when DCS needs it");
        var result = await ShowAsync(vm);
        var answer = result switch
        {
            "always" => AutoConnectConsentAnswer.Always,
            "once" => AutoConnectConsentAnswer.ConnectOnce,
            _ => AutoConnectConsentAnswer.No,
        };
        return new AutoConnectConsentResult(answer, vm.IsChecked);
    }

    public async Task<bool> PromptSwitchAsync(string currentAddress, AutoConnectTarget target, string dcsServer)
    {
        var vm = new AutoConnectDialogViewModel(
            "Switch OpenFreq server?",
            $"You are connected to {currentAddress}. The DCS server you joined ({Describe(dcsServer)}) offers " +
            $"OpenFreq at {target.Address}.\n\nSwitch to {target.Address}?",
            primaryText: "Switch", primaryResult: "switch",
            cancelText: "Stay", cancelResult: "stay");
        try
        {
            return await ShowAsync(vm) == "switch";
        }
        catch (InvalidOperationException)
        {
            return false; // another dialog is open: stay
        }
    }

    public async Task<AutoConnectPasswordResult?> PromptPasswordAsync(AutoConnectTarget target, bool previousAttemptFailed)
    {
        var vm = new AutoConnectDialogViewModel(
            "OpenFreq server password",
            (previousAttemptFailed ? $"{target.Address} rejected the password. " : $"{target.Address} needs a password. ") +
            "Enter it to connect (offered by the DCS server you joined).",
            primaryText: "Connect", primaryResult: "ok",
            cancelText: "Cancel", cancelResult: "cancel",
            checkboxText: "Remember for this server",
            showPassword: true);
        try
        {
            return await ShowAsync(vm) == "ok" ? new AutoConnectPasswordResult(vm.Password, vm.IsChecked) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void ShowStatus(string message)
    {
        _logger.LogInformation("DCS auto-connect: {Status}", message);
        Dispatcher.UIThread.Post(() => _settings.AutoConnectStatus = message);
    }

    public void RequestClose() => Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    });

    public void Dispose() => _openFreqService.ConnectionStateChanged -= OnServiceConnectionStateChanged;

    private void OnServiceConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e) =>
        ConnectionStateChanged?.Invoke(this, e);

    /// <summary>Shows a prompt in the main window. The window is not brought to the front (that
    /// would pull the user out of a full-screen DCS); the DCS hook prints a one-line local hint
    /// instead. Throws InvalidOperationException if another dialog is already open.</summary>
    private static Task<string?> ShowAsync(AutoConnectDialogViewModel vm) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var result = await DialogHost.Show(new AutoConnectDialog { DataContext = vm }, "MainDialogHost");
            return result as string;
        });

    private static string Describe(string dcsServer) => string.IsNullOrWhiteSpace(dcsServer) ? "unknown address" : dcsServer;

    private static T OnUiThread<T>(Func<T> func) =>
        Dispatcher.UIThread.CheckAccess() ? func() : Dispatcher.UIThread.Invoke(func);
}
