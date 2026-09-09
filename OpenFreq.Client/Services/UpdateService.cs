using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Updates;
using OpenFreqClient.Views.Util;

namespace OpenFreqClient.Services;

public sealed class UpdateService : IUpdateService
{
    private const string AppName = "Client";

    private static string ExeFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "OpenFreq.Client.exe" : "OpenFreq.Client";

    private readonly UpdateChecker _checker;
    private readonly SelfUpdateStager _stager;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _checker = new UpdateChecker(logger);
        _stager = new SelfUpdateStager(logger);
    }

    public async Task RunStartupCheckAsync(bool autoUpdateEnabled)
    {
        try
        {
            var info = await _checker.CheckForUpdateAsync(AppName, OpenFreqVersion.Current);
            if (info == null) return;

            if (autoUpdateEnabled)
            {
                var staged = await _stager.DownloadAndStageAsync(info, AppDataPaths.ClientUpdateStagingDirectory,
                    ExeFileName);
                if (staged != null)
                    _logger.LogInformation("Update {Version} downloaded and staged for next launch", info.Version);
                return;
            }

            var accepted = await ConfirmationDialogService.ShowAsync("Update available",
                $"OpenFreq {info.Version} is available. Update now?", "Later", "Update");
            if (!accepted) return;

            var stagedExePath = await _stager.DownloadAndStageAsync(info, AppDataPaths.ClientUpdateStagingDirectory,
                ExeFileName);
            if (stagedExePath == null)
            {
                await ConfirmationDialogService.ShowMessageAsync("Update failed",
                    "Could not download the update. Please try again later, or update manually from GitHub.");
                return;
            }

            var currentExePath = Environment.ProcessPath;
            if (currentExePath == null) return;

            SelfUpdateStager.WriteAppliedMarker(AppDataPaths.ClientUpdateStagingDirectory, info.Version,
                info.ReleaseNotes);
            SelfUpdateLauncher.LaunchApplyAndRestart(currentExePath, stagedExePath, Environment.ProcessId,
                Environment.GetCommandLineArgs().Skip(1).ToArray());

            // Clean shutdown (flushes settings) instead of a hard kill -- the armed helper script
            // is already waiting on this process's PID and won't touch anything until it exits.
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
        }
    }

    public async Task ShowWhatsNewIfPendingAsync()
    {
        var marker = SelfUpdateStager.TakeAppliedMarker(AppDataPaths.ClientUpdateStagingDirectory);
        if (marker == null) return;

        await ConfirmationDialogService.ShowMessageAsync($"Updated to {marker.Version}",
            string.IsNullOrWhiteSpace(marker.ReleaseNotes) ? "No release notes provided." : marker.ReleaseNotes);
    }
}
