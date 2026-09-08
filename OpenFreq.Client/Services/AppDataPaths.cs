using System;
using System.IO;

namespace OpenFreqClient.Services;

internal static class AppDataPaths
{
    private const string AppDirectoryName = "OpenFreq";

    /// <summary>Optional instance-isolation suffix (e.g. "-test2"), set via the --profile CLI arg
    /// in Program.Main before anything else runs. Lets a second client instance run alongside the
    /// normal one on the same machine (e.g. to manually test SATCOM audio between two clients)
    /// without both instances reading/writing the same settings file, log directory, and
    /// recordings directory. Empty by default -- normal single-instance use is unaffected.</summary>
    public static string ProfileSuffix { get; set; } = "";

    private static string EffectiveAppDirectoryName => AppDirectoryName + ProfileSuffix;

    public static string RoamingDirectory => GetSpecialFolderPath(
        Environment.SpecialFolder.ApplicationData,
        Path.Combine(GetExecutableDirectory(), "appdata"));

    public static string LocalDirectory => GetSpecialFolderPath(
        Environment.SpecialFolder.LocalApplicationData,
        RoamingDirectory);

    public static string ClientConfigPath => Path.Combine(RoamingDirectory, "OpenFreq.Client.json");

    public static string ClientLogDirectory => Path.Combine(LocalDirectory, "Logs");

    public static string ClientRecordingDirectory => Path.Combine(LocalDirectory, "Recordings");

    /// <summary>Where the local speech-to-text model is cached after its one-time download -- see
    /// WhisperSpeechTranscriber. Large (tens of MB), so it lives under LocalDirectory like
    /// recordings, not the small roaming settings/config files.</summary>
    public static string ClientModelDirectory => Path.Combine(LocalDirectory, "Models");

    private static string GetSpecialFolderPath(Environment.SpecialFolder folder, string fallback)
    {
        var root = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(root)
            ? fallback
            : Path.Combine(root, EffectiveAppDirectoryName);
    }

    private static string GetExecutableDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
