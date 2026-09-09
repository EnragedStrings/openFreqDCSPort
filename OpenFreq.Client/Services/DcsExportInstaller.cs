using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OpenFreqClient.Services;

public sealed partial class DcsExportInstaller(ILogger<DcsExportInstaller> logger)
{
    private const string ResourceRoot = "DCS/OpenFreqDCS/";
    private const string ExportToken = "Mods\\Services\\OpenFreqDCS\\Scripts\\OpenFreqDCS.lua";

    private const string ExportHook = """
                                      -- OpenFreqDCS BEGIN
                                      pcall(function()
                                          local okLfs, lfs = pcall(require, "lfs")
                                          local writeDir = ""
                                          if okLfs and lfs and type(lfs.writedir) == "function" then
                                              writeDir = lfs.writedir()
                                          end

                                          local path = writeDir .. [[Mods\Services\OpenFreqDCS\Scripts\OpenFreqDCS.lua]]
                                          local ok, err = pcall(dofile, path)
                                          if not ok then
                                              if log and type(log.write) == "function" and log.ERROR then
                                                  pcall(log.write, "OpenFreqDCS", log.ERROR, tostring(err))
                                              end
                                              if writeDir ~= "" then
                                                  local file = io.open(writeDir .. [[Logs\OpenFreqDCS.log]], "ab")
                                                  if file then
                                                      file:write(os.date("!%Y-%m-%dT%H:%M:%SZ "), "loader error: ", tostring(err), "\r\n")
                                                      file:close()
                                                  end
                                              end
                                          end
                                      end)
                                      -- OpenFreqDCS END
                                      """;

    public void EnsureInstalled()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            EnsureStartMenuShortcut();

            foreach (var savedGamesDirectory in GetDcsSavedGamesDirectories())
            {
                EnsureDcsExportInstalled(savedGamesDirectory);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check or install OpenFreq DCS export");
        }
#endif
    }

#if WINDOWS
    private void EnsureDcsExportInstalled(string savedGamesDirectory)
    {
        var serviceRoot = Path.Combine(savedGamesDirectory, "Mods", "Services", "OpenFreqDCS");
        Directory.CreateDirectory(serviceRoot);

        ExtractExportResources(serviceRoot);
        EnsureExportHook(Path.Combine(savedGamesDirectory, "Scripts", "Export.lua"));

        logger.LogInformation("OpenFreq DCS export is configured in {SavedGamesDirectory}", savedGamesDirectory);
    }

    private void ExtractExportResources(string serviceRoot)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var normalizedName = resourceName.Replace('\\', '/');
            var rootIndex = normalizedName.IndexOf(ResourceRoot, StringComparison.OrdinalIgnoreCase);
            if (rootIndex < 0)
                continue;

            var relativePath = normalizedName[(rootIndex + ResourceRoot.Length)..];
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var targetPath = Path.Combine(
                serviceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            var isConfigFile = relativePath.Equals(
                "Scripts/OpenFreqDCSConfig.lua",
                StringComparison.OrdinalIgnoreCase);

            if (isConfigFile && File.Exists(targetPath))
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                continue;

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var newBytes = memory.ToArray();

            if (File.Exists(targetPath))
            {
                var existingBytes = File.ReadAllBytes(targetPath);
                if (existingBytes.SequenceEqual(newBytes))
                    continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllBytes(targetPath, newBytes);
            logger.LogInformation("Installed DCS export file {TargetPath}", targetPath);
        }
    }

    /// <summary>"Saved Games" (FOLDERID_SavedGames) -- not one of the values in .NET's
    /// Environment.SpecialFolder enum, so unlike Documents/AppData it isn't resolvable via
    /// Environment.GetFolderPath at all. It's still a real, independently relocatable Windows
    /// known folder: a user can move it off the profile root entirely (right-click -> Properties
    /// -> Location -> Move), which many PC gamers do to keep game saves on a separate drive.
    /// DCS itself resolves the real (possibly relocated) location via the proper Windows API, so
    /// assuming it's always "%USERPROFILE%\Saved Games" silently installs into a folder DCS never
    /// reads from whenever a user has relocated it -- exactly the failure mode reported by a user
    /// whose DCS log showed it loading from "D:\Users\...\Saved Games" while this user's profile
    /// itself was elsewhere. SHGetKnownFolderPath is the correct, direct way to ask Windows for
    /// this folder's real location.</summary>
    private static readonly Guid FolderIdSavedGames = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static string? TryGetRealSavedGamesPath()
    {
        var folderId = FolderIdSavedGames;
        if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPtr) != 0 || pathPtr == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    private static IEnumerable<string> GetDcsSavedGamesDirectories()
    {
        var savedGamesRoot = TryGetRealSavedGamesPath() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games");

        if (!Directory.Exists(savedGamesRoot))
            Directory.CreateDirectory(savedGamesRoot);

        var targets = Directory.EnumerateDirectories(savedGamesRoot, "DCS*")
            .Where(path => Path.GetFileName(path).StartsWith("DCS", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            var defaultTarget = Path.Combine(savedGamesRoot, "DCS");
            Directory.CreateDirectory(defaultTarget);
            targets.Add(defaultTarget);
        }

        return targets;
    }

    private static void EnsureExportHook(string exportLuaPath)
    {
        var scriptsDirectory = Path.GetDirectoryName(exportLuaPath);
        if (!string.IsNullOrWhiteSpace(scriptsDirectory))
            Directory.CreateDirectory(scriptsDirectory);

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var hook = ExportHook.Trim() + "\r\n";

        if (!File.Exists(exportLuaPath))
        {
            File.WriteAllText(exportLuaPath, hook, utf8NoBom);
            return;
        }

        var content = File.ReadAllText(exportLuaPath);
        content = OpenFreqHookBlockRegex().Replace(content, string.Empty);

        if (content.Contains(ExportToken, StringComparison.OrdinalIgnoreCase))
        {
            var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(line => !line.Contains(ExportToken, StringComparison.OrdinalIgnoreCase));
            content = string.Join("\r\n", lines);
        }

        content = content.TrimEnd('\r', '\n');
        if (content.Length > 0)
            content += "\r\n\r\n";

        File.WriteAllText(exportLuaPath, content + hook, utf8NoBom);
    }

    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    private void EnsureStartMenuShortcut()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return;

        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        if (string.IsNullOrWhiteSpace(startMenu))
            return;

        var programsDirectory = Path.Combine(startMenu, "Programs");
        Directory.CreateDirectory(programsDirectory);

        var shortcutPath = Path.Combine(programsDirectory, "OpenFreq DCS Client.lnk");

        try
        {
            // Strongly-typed COM interop (IShellLinkW/IPersistFile), not late-bound "dynamic
            // WScript.Shell" Automation: the dynamic/IDispatch path goes through the CLR's DLR
            // COM binder, which crashes with an unmanaged access violation (uncatchable by a
            // normal try/catch) in a PublishTrimmed=true self-contained single-file publish --
            // exactly the build this project ships as the "portable" release. This interop shape
            // uses a fixed, compile-time-known vtable, so it has no such dependency.
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory);
            link.SetIconLocation(exePath, 0);
            link.SetDescription("OpenFreq DCS Client");
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to create Start Menu shortcut");
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath,
            IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [GeneratedRegex("(?ms)^-- OpenFreqDCS BEGIN\\r?\\n.*?^-- OpenFreqDCS END\\r?\\n?")]
    private static partial Regex OpenFreqHookBlockRegex();
#endif
}
