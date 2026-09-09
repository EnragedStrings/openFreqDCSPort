using System.Threading.Tasks;

namespace OpenFreqClient.Services;

/// <summary>
/// Orchestrates the client half of the auto-update feature -- see OpenFreq.Common.Updates for the
/// shared check/download/relaunch engine this drives. Program.cs handles the "apply a
/// previously-staged update" half before any of this even runs -- see
/// Program.TryApplyStagedUpdate.
/// </summary>
public interface IUpdateService
{
    /// <summary>Runs once per launch, after the main window is visible: checks GitHub for a newer
    /// release and, per <paramref name="autoUpdateEnabled"/> (OpenFreqSettings.AutoUpdateEnabled),
    /// either silently downloads/stages it in the background (applied on the *next* launch, see
    /// Program.TryApplyStagedUpdate) or prompts the user to update right now. Never throws --
    /// a failed check just means no update happens this session.</summary>
    Task RunStartupCheckAsync(bool autoUpdateEnabled);

    /// <summary>If the current launch is the one that just applied a background-staged update,
    /// shows a "here's what changed" popup with the new version's release notes and clears the
    /// marker. No-op otherwise. Call once, after the main window is visible.</summary>
    Task ShowWhatsNewIfPendingAsync();
}
