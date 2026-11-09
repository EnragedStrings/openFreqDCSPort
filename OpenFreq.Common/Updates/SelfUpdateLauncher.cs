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
///
/// The script never relaunches into a silent no-op: it gives the exiting process
/// <see cref="ExitGraceSeconds"/> to finish its shutdown and then ends it, retries the copy for
/// <see cref="CopyRetrySeconds"/> (antivirus scans, a slow handle release), and if the executable
/// still can't be replaced (another instance of the same executable running) it records why in
/// <c>statusDirectory</c> (<see cref="SelfUpdateStager.FailedMarkerFileName"/>, and drops the
/// "just applied" marker) so the relaunched old version can say so instead of offering the same
/// update again in a loop.
/// </summary>
public static class SelfUpdateLauncher
{
    public const int ExitGraceSeconds = 20;
    public const int CopyRetrySeconds = 30;

    public static void LaunchApplyAndRestart(string currentExePath, string stagedExePath, int waitForPid,
        IReadOnlyList<string> relaunchArgs, string? statusDirectory = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            LaunchWindows(currentExePath, stagedExePath, waitForPid, relaunchArgs, statusDirectory ?? "");
        else
            LaunchUnix(currentExePath, stagedExePath, waitForPid, relaunchArgs, statusDirectory ?? "");
    }

    /// <summary>Other running processes started from <paramref name="exePath"/> (not
    /// <paramref name="selfPid"/>). Each holds the executable open, so the swap can't succeed
    /// until they exit -- callers check this before arming a swap.</summary>
    public static IReadOnlyList<int> FindOtherInstances(string exePath, int selfPid)
    {
        var others = new List<int>();
        var full = Path.GetFullPath(exePath);
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (p)
            {
                try
                {
                    if (p.Id == selfPid || p.HasExited) continue;
                    var path = p.MainModule?.FileName;
                    if (path != null && string.Equals(Path.GetFullPath(path), full, comparison))
                        others.Add(p.Id);
                }
                catch
                {
                    // Exited meanwhile, or not ours to inspect.
                }
            }
        }

        return others;
    }

    public static string BuildWindowsScript(string currentExePath, string stagedExePath, int waitForPid,
        IReadOnlyList<string> relaunchArgs, string statusDirectory, int exitGraceSeconds = ExitGraceSeconds,
        int copyRetrySeconds = CopyRetrySeconds)
    {
        var argsLiteral = string.Join(", ", relaunchArgs.Select(a => "'" + a.Replace("'", "''") + "'"));

        // Plain (non-interpolated) raw string + token replacement -- PowerShell's own {} braces
        // (try/catch) would otherwise collide with C# interpolated-string brace escaping.
        const string template = """
            $p = Get-Process -Id __PID__ -ErrorAction SilentlyContinue
            if ($p -and -not $p.WaitForExit(__GRACE_MS__)) {
                Stop-Process -Id __PID__ -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 1
            }
            Start-Sleep -Milliseconds 500
            $copied = $false
            $reason = ''
            $deadline = (Get-Date).AddSeconds(__RETRY_S__)
            while (-not $copied) {
                try {
                    Copy-Item -LiteralPath '__STAGED__' -Destination '__CURRENT__' -Force -ErrorAction Stop
                    $copied = $true
                } catch {
                    $reason = $_.Exception.Message
                    if ((Get-Date) -gt $deadline) { break }
                    Start-Sleep -Milliseconds 500
                }
            }
            if (-not $copied -and '__STATUS__' -ne '') {
                Remove-Item -LiteralPath (Join-Path '__STATUS__' '__APPLIED__') -Force -ErrorAction SilentlyContinue
                Set-Content -LiteralPath (Join-Path '__STATUS__' '__FAILED__') -Value $reason -Encoding UTF8
            }
            Start-Process -FilePath '__CURRENT__' -ArgumentList @(__ARGS__)
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force
            """;

        return template
            .Replace("__PID__", waitForPid.ToString())
            .Replace("__GRACE_MS__", (exitGraceSeconds * 1000).ToString())
            .Replace("__RETRY_S__", copyRetrySeconds.ToString())
            .Replace("__STAGED__", stagedExePath.Replace("'", "''"))
            .Replace("__CURRENT__", currentExePath.Replace("'", "''"))
            .Replace("__STATUS__", statusDirectory.Replace("'", "''"))
            .Replace("__APPLIED__", SelfUpdateStager.AppliedMarkerFileName)
            .Replace("__FAILED__", SelfUpdateStager.FailedMarkerFileName)
            .Replace("__ARGS__", argsLiteral);
    }

    [SupportedOSPlatform("windows")]
    private static void LaunchWindows(string currentExePath, string stagedExePath, int waitForPid,
        IReadOnlyList<string> relaunchArgs, string statusDirectory)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"openfreq-update-{Guid.NewGuid():N}.ps1");
        var script = BuildWindowsScript(currentExePath, stagedExePath, waitForPid, relaunchArgs, statusDirectory);

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
        IReadOnlyList<string> relaunchArgs, string statusDirectory)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"openfreq-update-{Guid.NewGuid():N}.sh");
        static string Q(string v) => v.Replace("'", "'\\''");
        var argsLiteral = string.Join(" ", relaunchArgs.Select(a => "'" + Q(a) + "'"));
        var staged = Q(stagedExePath);
        var current = Q(currentExePath);
        var status = Q(statusDirectory);
        var grace = ExitGraceSeconds * 2;
        var retry = CopyRetrySeconds * 2;
        var applied = SelfUpdateStager.AppliedMarkerFileName;
        var failed = SelfUpdateStager.FailedMarkerFileName;

        var script = $"""
            #!/bin/bash
            n=0
            while kill -0 {waitForPid} 2>/dev/null && [ $n -lt {grace} ]; do sleep 0.5; n=$((n+1)); done
            kill -9 {waitForPid} 2>/dev/null
            sleep 0.5
            err=$(mktemp)
            n=0
            until cp -f '{staged}' '{current}' 2>"$err"; do
                n=$((n+1))
                if [ $n -ge {retry} ]; then
                    if [ -n '{status}' ]; then
                        rm -f '{status}/{applied}'
                        cp "$err" '{status}/{failed}'
                    fi
                    break
                fi
                sleep 0.5
            done
            rm -f "$err"
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
