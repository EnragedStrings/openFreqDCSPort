using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenFreq.Common.Updates;

/// <summary>
/// Arms the actual executable swap: writes a small OS-appropriate helper script that waits for
/// the current process to exit, replaces its executable with the staged one, and relaunches it.
/// This only ARMS the swap -- the caller is responsible for actually exiting the current process
/// cleanly afterward (flushing settings/config first via its own normal shutdown path), not this
/// method killing anything. Safe to call before exiting because the helper script itself waits
/// for the given PID before touching any files -- a running single-file app's own executable
/// can't be overwritten until the process holding it has fully exited.
/// </summary>
public static class SelfUpdateLauncher
{
    public static void LaunchApplyAndRestart(string currentExePath, string stagedExePath, int waitForPid,
        IReadOnlyList<string> relaunchArgs)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            LaunchWindows(currentExePath, stagedExePath, waitForPid, relaunchArgs);
        else
            LaunchUnix(currentExePath, stagedExePath, waitForPid, relaunchArgs);
    }

    [SupportedOSPlatform("windows")]
    private static void LaunchWindows(string currentExePath, string stagedExePath, int waitForPid,
        IReadOnlyList<string> relaunchArgs)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"openfreq-update-{Guid.NewGuid():N}.ps1");
        var argsLiteral = string.Join(", ", relaunchArgs.Select(a => "'" + a.Replace("'", "''") + "'"));
        var staged = stagedExePath.Replace("'", "''");
        var current = currentExePath.Replace("'", "''");

        // Plain (non-interpolated) raw string + token replacement -- PowerShell's own {} braces
        // (try/catch) would otherwise collide with C# interpolated-string brace escaping.
        const string template = """
            try {
                Wait-Process -Id __PID__ -Timeout 30 -ErrorAction SilentlyContinue
            } catch {}
            Start-Sleep -Milliseconds 500
            Copy-Item -LiteralPath '__STAGED__' -Destination '__CURRENT__' -Force
            Start-Process -FilePath '__CURRENT__' -ArgumentList @(__ARGS__)
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force
            """;

        var script = template
            .Replace("__PID__", waitForPid.ToString())
            .Replace("__STAGED__", staged)
            .Replace("__CURRENT__", current)
            .Replace("__ARGS__", argsLiteral);

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    [UnsupportedOSPlatform("windows")]
    private static void LaunchUnix(string currentExePath, string stagedExePath, int waitForPid,
        IReadOnlyList<string> relaunchArgs)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"openfreq-update-{Guid.NewGuid():N}.sh");
        var argsLiteral = string.Join(" ", relaunchArgs.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
        var staged = stagedExePath.Replace("'", "'\\''");
        var current = currentExePath.Replace("'", "'\\''");

        var script = $"""
            #!/bin/bash
            while kill -0 {waitForPid} 2>/dev/null; do sleep 0.5; done
            sleep 0.5
            cp -f '{staged}' '{current}'
            chmod +x '{current}'
            nohup '{current}' {argsLiteral} >/dev/null 2>&1 &
            rm -- "$0"
            """;

        File.WriteAllText(scriptPath, script);
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
