using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Updates;

namespace OpenFreqServer;

/// <summary>
/// Server half of the auto-update feature. Unlike the client, the server never restarts itself
/// while any client is connected -- see the class's own RunAsync for the full sequence. A found
/// update is always logged (surfaced through the normal ILogger pipeline, which already reaches
/// both the console/file logs and the TUI's log pane) regardless of ServerConfig.AutoUpdateEnabled;
/// only *applying* it is gated on that setting.
/// </summary>
public sealed class ServerUpdateCoordinator
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);

    private readonly ServerConfig _config;
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly CancellationTokenSource _shutdownCts;
    private readonly ILogger _logger;
    private readonly UpdateChecker _checker;
    private readonly SelfUpdateStager _stager;
    private readonly string _stagingDir;

    /// <summary>Set once an update has been staged and the server has gone idle -- Program.cs
    /// checks this after its normal graceful-teardown sequence completes, to decide whether to
    /// just exit or also arm a SelfUpdateLauncher swap-and-relaunch before exiting.</summary>
    public string? StagedExePathReadyToApply { get; private set; }
    public string? AppliedVersion { get; private set; }
    public string? AppliedReleaseNotes { get; private set; }

    public ServerUpdateCoordinator(ServerConfig config, ConcurrentDictionary<string, ClientSession> clients,
        CancellationTokenSource shutdownCts, ILogger logger, string stagingDir)
    {
        _config = config;
        _clients = clients;
        _shutdownCts = shutdownCts;
        _logger = logger;
        _checker = new UpdateChecker(logger);
        _stager = new SelfUpdateStager(logger);
        _stagingDir = stagingDir;
    }

    private static string ExeFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "OpenFreq.Server.exe" : "OpenFreq.Server";

    /// <summary>Runs until shutdown is requested (for any reason -- Ctrl+C, SIGTERM, or this
    /// coordinator's own idle-triggered restart). Checks immediately, then on CheckInterval.
    /// Never throws.</summary>
    public async Task RunAsync()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                await CheckOnceAsync();

                if (StagedExePathReadyToApply != null)
                    return; // idle-triggered restart already armed -- nothing left to do here

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _config.UpdateCheckIntervalMinutes)),
                        _shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update coordinator loop stopped unexpectedly");
        }
    }

    private async Task CheckOnceAsync()
    {
        UpdateInfo? info;
        try
        {
            info = await _checker.CheckForUpdateAsync("Server", OpenFreqVersion.Current, _shutdownCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed");
            return;
        }

        if (info == null) return;

        _logger.LogInformation(
            "A new OpenFreq Server version is available: {Version}. {Guidance}", info.Version,
            _config.AutoUpdateEnabled
                ? "Auto-update is enabled -- it will be applied automatically once no clients are connected."
                : "Set \"autoUpdateEnabled\": true in OpenFreq.Server.json to apply it automatically, or update manually from GitHub.");

        if (!_config.AutoUpdateEnabled) return;

        var exePath = await _stager.DownloadAndStageAsync(info, _stagingDir, ExeFileName, _shutdownCts.Token);
        if (exePath == null) return;

        await WaitForIdleThenArmRestartAsync(exePath, info.Version, info.ReleaseNotes);
    }

    private async Task WaitForIdleThenArmRestartAsync(string stagedExePath, string version, string releaseNotes)
    {
        _logger.LogInformation(
            "Update {Version} downloaded and staged -- waiting for zero connected clients before restarting to apply it",
            version);

        while (!_shutdownCts.IsCancellationRequested)
        {
            if (_clients.IsEmpty)
            {
                _logger.LogInformation("No clients connected -- restarting now to apply update {Version}", version);
                StagedExePathReadyToApply = stagedExePath;
                AppliedVersion = version;
                AppliedReleaseNotes = releaseNotes;
                await _shutdownCts.CancelAsync();
                return;
            }

            try
            {
                await Task.Delay(IdlePollInterval, _shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
