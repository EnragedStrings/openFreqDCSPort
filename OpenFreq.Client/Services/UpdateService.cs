using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DialogHostAvalonia;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Updates;
using OpenFreqClient.ViewModels;
using OpenFreqClient.Views;
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

            // One dialog instance carries the whole flow: changelog + Later/Update, then (without
            // closing) a live progress bar for the download -- see UpdateDialogViewModel's own doc
            // comment for why this needs a TaskCompletionSource instead of the simple
            // ConfirmationDialogService await-a-bool pattern used everywhere else.
            var dialogViewModel = new UpdateDialogViewModel(info.Version, info.ReleaseNotes);
            var dialogTask = DialogHost.Show(new UpdateDialog { DataContext = dialogViewModel }, "MainDialogHost");

            var wantsUpdate = await dialogViewModel.WaitForDecisionAsync();
            if (!wantsUpdate)
            {
                await dialogTask;
                return;
            }

            // Every running copy of this executable holds it open: the swap can only fail while
            // another one runs, and the old version would come straight back offering the update.
            var currentExePath = Environment.ProcessPath;
            var others = currentExePath == null
                ? []
                : SelfUpdateLauncher.FindOtherInstances(currentExePath, Environment.ProcessId);
            if (others.Count > 0)
            {
                DialogHost.Close("MainDialogHost", false);
                await dialogTask;
                _logger.LogWarning("Update {Version} postponed: {Count} other instance(s) of {Exe} running ({Pids})",
                    info.Version, others.Count, currentExePath, string.Join(", ", others));
                await ConfirmationDialogService.ShowMessageAsync("Close other OpenFreq windows",
                    $"{others.Count} other OpenFreq window(s) are running from this program file, so it can't be " +
                    "replaced. Close every other OpenFreq window (check the system tray too), then restart OpenFreq " +
                    "and choose Update again.");
                return;
            }

            var progress = new Progress<double>(dialogViewModel.ReportProgress);
            var stagedExePath = await _stager.DownloadAndStageAsync(info, AppDataPaths.ClientUpdateStagingDirectory,
                ExeFileName, progress);

            DialogHost.Close("MainDialogHost", stagedExePath != null);
            await dialogTask;

            if (stagedExePath == null)
            {
                await ConfirmationDialogService.ShowMessageAsync("Update failed",
                    "Could not download the update. Please try again later, or update manually from GitHub.");
                return;
            }

            if (currentExePath == null) return;

            // Applied right now: drop the "staged for next launch" marker the download wrote, or the
            // relaunched new version would apply the same update again (a second restart).
            SelfUpdateStager.TakeStagedMarker(AppDataPaths.ClientUpdateStagingDirectory);
            SelfUpdateStager.WriteAppliedMarker(AppDataPaths.ClientUpdateStagingDirectory, info.Version,
                info.ReleaseNotes);
            SelfUpdateLauncher.LaunchApplyAndRestart(currentExePath, stagedExePath, Environment.ProcessId,
                Environment.GetCommandLineArgs().Skip(1).ToArray(), AppDataPaths.ClientUpdateStagingDirectory);

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
        var failure = SelfUpdateStager.TakeFailedReason(AppDataPaths.ClientUpdateStagingDirectory);
        if (failure != null)
        {
            _logger.LogWarning("The last update could not replace the executable: {Reason}", failure);
            await ConfirmationDialogService.ShowMessageAsync("Update not applied",
                "OpenFreq could not replace its program file, so this is still the old version. This usually " +
                "means another OpenFreq window was still open. Close every OpenFreq window (check the system " +
                "tray too), start OpenFreq again and choose Update." +
                (failure.Length > 0 ? $"\n\nDetails: {failure}" : ""));
            return;
        }

        var marker = SelfUpdateStager.TakeAppliedMarker(AppDataPaths.ClientUpdateStagingDirectory);
        if (marker == null) return;

        await ConfirmationDialogService.ShowMessageAsync($"Updated to {marker.Version}",
            string.IsNullOrWhiteSpace(marker.ReleaseNotes) ? "No release notes provided." : marker.ReleaseNotes);
    }
}
