using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Services.Interfaces;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Services.AutoConnect;

/// <summary>
/// Wires DCS multiplayer auto-connect into the running app: a fresh token per run, the
/// loopback endpoint, the policy controller, client.json for the DCS hook (rewritten on start
/// and whenever AutoConnectEnabled/AutoLaunchEnabled change) and a DCS process watch that
/// treats DCS exiting as leaving the server. Only the default profile takes part (a --profile
/// instance would fight over the port and client.json).
/// </summary>
public sealed class DcsAutoConnectService : ILifecycleService, IDisposable
{
    private static readonly string[] DcsProcessNames = ["DCS", "DCS_mt", "DCS_server"];
    private static readonly TimeSpan DcsWatchInterval = TimeSpan.FromSeconds(5);

    private readonly MainWindowViewModel _main;
    private readonly SettingsViewModel _settings;
    private readonly IOpenFreqService _openFreqService;
    private readonly DcsExportInstaller _installer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DcsAutoConnectService> _logger;
    private DcsAutoConnectHost? _host;
    private DcsAutoConnectController? _controller;
    private DcsAutoConnectEndpoint? _endpoint;
    private Timer? _dcsWatch;
    private string _token = "";
    private bool _sawDcs;
    private readonly object _writeLock = new();

    public DcsAutoConnectService(MainWindowViewModel main, SettingsViewModel settings,
        IOpenFreqService openFreqService, DcsExportInstaller installer, ILoggerFactory loggerFactory)
    {
        _main = main;
        _settings = settings;
        _openFreqService = openFreqService;
        _installer = installer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DcsAutoConnectService>();
    }

    public void Start()
    {
        if (_endpoint != null)
            return;
        if (AppDataPaths.ProfileSuffix.Length > 0)
        {
            _logger.LogInformation("DCS auto-connect is disabled for profile instances ({Profile})",
                AppDataPaths.ProfileSuffix);
            _settings.AutoConnectStatus = "DCS auto-connect is only available in the default profile.";
            return;
        }

        _token = AutoConnectProtocol.NewToken();
        _host = new DcsAutoConnectHost(_main, _settings, _openFreqService, _loggerFactory.CreateLogger("DcsAutoConnect"));
        _controller = new DcsAutoConnectController(_host, new DnsAutoConnectResolver(),
            _loggerFactory.CreateLogger<DcsAutoConnectController>(), launchedByDcs: Program.LaunchedByDcs);
        _endpoint = new DcsAutoConnectEndpoint(_token, _controller.Handle,
            _loggerFactory.CreateLogger<DcsAutoConnectEndpoint>());
        if (!_endpoint.Start())
        {
            _settings.AutoConnectStatus = _endpoint.ListenerError ?? "DCS auto-connect is unavailable.";
            return;
        }

        WriteClientConfig();
        _settings.PropertyChanged += OnSettingsChanged;
        _dcsWatch = new Timer(_ => WatchDcs(), null, DcsWatchInterval, DcsWatchInterval);
    }

    public void Stop()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _dcsWatch?.Dispose();
        _dcsWatch = null;
        _endpoint?.Dispose();
        _endpoint = null;
        _controller?.Dispose();
        _controller = null;
        _host?.Dispose();
        _host = null;
    }

    public void Dispose() => Stop();

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.AutoConnectEnabled) or nameof(SettingsViewModel.AutoLaunchEnabled))
            WriteClientConfig();
    }

    private void WriteClientConfig()
    {
        if (_endpoint == null)
            return;
        var port = _endpoint.BoundPort;
        // File I/O off the UI thread; the settings are read when the write runs, under one lock,
        // so the last write always carries the current values.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_writeLock)
            {
                var config = new AutoConnectClientConfig(port, _token, Environment.ProcessPath,
                    _settings.AutoConnectEnabled, _settings.AutoLaunchEnabled);
                _installer.WriteAutoConnectClientConfig(config);
                _logger.LogInformation(
                    "DCS auto-connect client.json written (auto-connect {AutoConnect}, auto-launch {AutoLaunch})",
                    config.AutoConnect, config.AutoLaunch);
            }
        });
    }

    private void WatchDcs()
    {
        var controller = _controller;
        if (controller == null)
            return;
        if (!controller.HasSession)
        {
            _sawDcs = false;
            return;
        }

        var running = IsDcsRunning();
        if (running)
        {
            _sawDcs = true;
            return;
        }

        if (_sawDcs)
        {
            _sawDcs = false;
            Dispatcher.UIThread.Post(controller.HandleDcsExited);
        }
    }

    private static bool IsDcsRunning()
    {
        foreach (var name in DcsProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            var any = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (any) return true;
        }

        return false;
    }
}
