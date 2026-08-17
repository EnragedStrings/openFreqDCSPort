using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
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

    private static IEnumerable<string> GetDcsSavedGamesDirectories()
    {
        var savedGamesRoot = Path.Combine(
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
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        try
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            shortcut.IconLocation = exePath + ",0";
            shortcut.Description = "OpenFreq DCS Client";
            shortcut.Save();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to create Start Menu shortcut");
        }
    }

    [GeneratedRegex("(?ms)^-- OpenFreqDCS BEGIN\\r?\\n.*?^-- OpenFreqDCS END\\r?\\n?")]
    private static partial Regex OpenFreqHookBlockRegex();
#endif
}
