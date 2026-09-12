using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;

namespace OpenFreqClient.ViewModels;

/// <summary>
/// Backs UpdateDialog.axaml -- shown by UpdateService.RunStartupCheckAsync when a newer release is
/// found and background auto-update is off. Two visual states on one dialog instance/one
/// DialogHost.Show call, so the same window carries the user from "here's what's new, update?"
/// straight into a live download progress bar without closing and reopening anything:
///   Confirm -> WaitForDecisionAsync() resolves true (Update clicked, dialog stays open) or false
///   (Later/closed, dialog closes itself). The caller drives the transition to Downloading and
///   reports progress via ReportProgress; it alone decides when to finally close the dialog once
///   staging finishes (or fails).
/// </summary>
public sealed partial class UpdateDialogViewModel : ViewModelBase
{
    public enum Stage
    {
        Confirm,
        Downloading
    }

    private readonly TaskCompletionSource<bool> _decision = new();

    public string Version { get; }
    public string ReleaseNotes { get; }

    private Stage _currentStage = Stage.Confirm;
    public Stage CurrentStage
    {
        get => _currentStage;
        private set
        {
            if (!SetProperty(ref _currentStage, value)) return;
            OnPropertyChanged(nameof(IsConfirmStage));
            OnPropertyChanged(nameof(IsDownloadingStage));
        }
    }

    public bool IsConfirmStage => CurrentStage == Stage.Confirm;
    public bool IsDownloadingStage => CurrentStage == Stage.Downloading;

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public ICommand LaterCommand { get; }
    public ICommand UpdateCommand { get; }

    public UpdateDialogViewModel(string version, string releaseNotes)
    {
        Version = version;
        ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? "No release notes provided." : releaseNotes;

        LaterCommand = new RelayCommand(() =>
        {
            _decision.TrySetResult(false);
            DialogHost.Close("MainDialogHost", false);
        });

        UpdateCommand = new RelayCommand(() =>
        {
            CurrentStage = Stage.Downloading;
            _decision.TrySetResult(true);
        });
    }

    /// <summary>Resolves once the user picks Later (false) or Update (true). Picking Update does
    /// NOT close the dialog -- the caller owns closing it once the download actually finishes.
    /// </summary>
    public Task<bool> WaitForDecisionAsync() => _decision.Task;

    /// <summary>Wired as an IProgress&lt;double&gt; callback (0-1) during the download.</summary>
    public void ReportProgress(double value)
    {
        IsIndeterminate = false;
        DownloadProgress = Math.Clamp(value, 0.0, 1.0);
    }
}
