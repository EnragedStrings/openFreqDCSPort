using System;
using System.IO;

namespace OpenFreqClient.Services;

internal static class AppDataPaths
{
    private const string AppDirectoryName = "OpenFreq";

    public static string RoamingDirectory => GetSpecialFolderPath(
        Environment.SpecialFolder.ApplicationData,
        Path.Combine(GetExecutableDirectory(), "appdata"));

    public static string LocalDirectory => GetSpecialFolderPath(
        Environment.SpecialFolder.LocalApplicationData,
        RoamingDirectory);

    public static string ClientConfigPath => Path.Combine(RoamingDirectory, "OpenFreq.Client.json");

    public static string ClientLogDirectory => Path.Combine(LocalDirectory, "Logs");

    public static string ClientRecordingDirectory => Path.Combine(LocalDirectory, "Recordings");

    private static string GetSpecialFolderPath(Environment.SpecialFolder folder, string fallback)
    {
        var root = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(root)
            ? fallback
            : Path.Combine(root, AppDirectoryName);
    }

    private static string GetExecutableDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
