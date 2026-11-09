using System.Diagnostics;
using OpenFreq.Common.Updates;

namespace OpenFreq.Common.Tests.Updates;

/// <summary>
/// The Windows swap script (SelfUpdateLauncher.BuildWindowsScript) run for real in powershell.exe against throwaway
/// files: a successful swap, and a swap blocked by another process holding the executable open (the "update installs
/// but boots the old version and offers the update again" loop), which must leave a failure note instead of a silent
/// relaunch of the old version. Windows only; the "executable" is a .cmd that exits at once when relaunched.
/// </summary>
public class SelfUpdateLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfreq-launcher-tests-" + Guid.NewGuid().ToString("N"));

    public SelfUpdateLauncherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a relaunched .cmd may still be closing */ }
    }

    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        return p.Id;
    }

    private (string current, string staged, string status) Setup()
    {
        var current = Path.Combine(_dir, "app.cmd");
        var staged = Path.Combine(_dir, "staged.cmd");
        var status = Path.Combine(_dir, "status");
        Directory.CreateDirectory(status);
        File.WriteAllText(current, "@exit /b 0\r\nREM old\r\n");
        File.WriteAllText(staged, "@exit /b 0\r\nREM new\r\n");
        File.WriteAllText(Path.Combine(status, SelfUpdateStager.AppliedMarkerFileName), "{}");
        return (current, staged, status);
    }

    private void RunScript(string script)
    {
        var path = Path.Combine(_dir, "update.ps1");
        File.WriteAllText(path, script);
        using var p = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        Assert.True(p.WaitForExit(60_000), "update script did not finish");
    }

    [Fact]
    public void Swap_ReplacesExecutable_AndKeepsAppliedMarker()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (current, staged, status) = Setup();

        RunScript(SelfUpdateLauncher.BuildWindowsScript(current, staged, DeadPid(), [], status, 2, 2));

        Assert.Contains("REM new", File.ReadAllText(current));
        Assert.False(File.Exists(Path.Combine(status, SelfUpdateStager.FailedMarkerFileName)));
        Assert.True(File.Exists(Path.Combine(status, SelfUpdateStager.AppliedMarkerFileName)));
    }

    [Fact]
    public void ExecutableHeldOpenByAnotherInstance_RecordsFailure_InsteadOfClaimingUpdate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (current, staged, status) = Setup();

        // Another running copy keeps the executable open for reading, as Windows does for a running .exe.
        using (new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
            RunScript(SelfUpdateLauncher.BuildWindowsScript(current, staged, DeadPid(), [], status, 2, 2));

        Assert.Contains("REM old", File.ReadAllText(current));
        Assert.False(File.Exists(Path.Combine(status, SelfUpdateStager.AppliedMarkerFileName)));
        var reason = SelfUpdateStager.TakeFailedReason(status);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Null(SelfUpdateStager.TakeFailedReason(status)); // read once
    }

    [Fact]
    public void FindOtherInstances_SeesAnotherProcessOfTheSameExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(_dir, "pingcopy.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var other = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            Assert.Contains(other.Id, SelfUpdateLauncher.FindOtherInstances(exe, Environment.ProcessId));
            Assert.Empty(SelfUpdateLauncher.FindOtherInstances(exe, other.Id));
        }
        finally
        {
            other.Kill();
            other.WaitForExit();
        }
    }
}
