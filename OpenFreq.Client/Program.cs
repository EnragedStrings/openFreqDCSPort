using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Updates;
using OpenFreqClient.Services;
using Serilog;
using Serilog.Events;

namespace OpenFreqClient;

sealed class Program
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    public static string Version { get; private set; } = "unknown";
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // --dcs-export <list|install|uninstall>: headless CLI mode, no window/DI/Serilog -- this is
        // what the Windows installer's [Run]/[UninstallRun] steps and its detection wizard page
        // shell out to, so there's exactly one implementation of "find DCS's Saved Games folder and
        // read/write the export there" (DcsExportInstaller) instead of a second one drifting out of
        // sync in PowerShell (see git history: that's exactly how a real bug shipped once already).
        // Checked before everything else so it never touches AppDataPaths/Avalonia at all.
        if (TryRunDcsExportCli(args))
            return;

        // --profile <name>: isolates settings/logs/recordings under "OpenFreq-<name>" instead of
        // "OpenFreq", so a second instance can run alongside the normal one on the same machine
        // (e.g. `dotnet run --project OpenFreq.Client -- --profile test2`) without both instances
        // fighting over the same config file. Must run before anything touches AppDataPaths.
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--profile") continue;
            var profileName = args[i + 1].Trim();
            if (profileName.Length > 0)
                AppDataPaths.ProfileSuffix = "-" + profileName;
            break;
        }

        // Initialize Serilog for file logging
        var logsDirectory = AppDataPaths.ClientLogDirectory;
        Directory.CreateDirectory(logsDirectory);

        var logFile = Path.Combine(logsDirectory, $"openfreq-client-{DateTime.Now:yyyy-MM-dd}.log");

#if DEBUG
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)


            // OVERRIDES - Disables Debug Logs
            // --------------------------------
            // Outputs the Playback Buffer State
            .MinimumLevel.Override("OpenFreqAudio.RadioPlayback", LogEventLevel.Warning)

            // Outputs the Physics Calculations
            .MinimumLevel.Override("OpenFreqAudio.FastPathAudioSim", LogEventLevel.Warning)

            // Outputs the packet timings (playback queue)
            .MinimumLevel.Override("OpenFreq.Common.RtpAudioReceiver", LogEventLevel.Debug)

            // Outputs the RTP receiver
            .MinimumLevel.Override("OpenFreq.Common.RtpSourceContext", LogEventLevel.Debug)


            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OpenFreqClient")
            .WriteTo.File(
                logFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
#else
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OpenFreqClient")
            .WriteTo.File(
                logFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .CreateLogger();
#endif

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };

        Version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        Log.Information("OpenFreq Client {Version} starting", Version);

        // A previous session may have silently downloaded a newer version in the background (see
        // IUpdateService/OpenFreqSettings.AutoUpdateEnabled) and staged it here, ready to apply.
        // Checked before anything else (DI, window) so this only adds a brief relaunch on the one
        // startup where an update is waiting -- it never interrupts a session already in progress,
        // since nothing is running yet at this exact moment except this just-started process.
        if (TryApplyStagedUpdate(args))
            return;

        // Set up dependency injection
        var services = new ServiceCollection();
        services.AddOpenFreqServices();
        ServiceProvider = services.BuildServiceProvider();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Returns true (having already run the requested action and set
    /// Environment.ExitCode) if argv contained "--dcs-export &lt;list|install|uninstall&gt;". False
    /// (no-op) otherwise, letting normal startup continue.</summary>
    private static bool TryRunDcsExportCli(string[] args)
    {
        var index = Array.IndexOf(args, "--dcs-export");
        if (index < 0 || index + 1 >= args.Length)
            return false;

        var action = args[index + 1].Trim().ToLowerInvariant();
        if (action is not ("list" or "install" or "uninstall"))
        {
            Console.Error.WriteLine($"Unknown --dcs-export action '{action}'. Expected: list, install, uninstall.");
            Environment.ExitCode = 1;
            return true;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var installer = new DcsExportInstaller(loggerFactory.CreateLogger<DcsExportInstaller>());

        switch (action)
        {
            case "list":
                var detected = installer.DetectSavedGamesDirectories();
                if (detected.Count == 0)
                    Console.WriteLine("(none detected)");
                else
                    foreach (var directory in detected)
                        Console.WriteLine(directory);
                break;

            case "install":
                installer.EnsureInstalled();
                break;

            case "uninstall":
                installer.Uninstall();
                break;
        }

        Environment.ExitCode = 0;
        return true;
    }

    /// <summary>Returns true (and has already armed a relaunch + exited nothing itself -- the
    /// caller must return immediately) if a background-staged update was found and applied.
    /// See SelfUpdateStager/SelfUpdateLauncher (OpenFreq.Common.Updates).</summary>
    private static bool TryApplyStagedUpdate(string[] args)
    {
        var stagingDir = AppDataPaths.ClientUpdateStagingDirectory;
        var marker = SelfUpdateStager.TakeStagedMarker(stagingDir);
        if (marker == null || !File.Exists(marker.ExePath))
            return false;

        var currentExePath = Environment.ProcessPath;
        if (currentExePath == null)
        {
            Log.Warning("Staged update {Version} found but current executable path is unknown -- skipping",
                marker.Version);
            return false;
        }

        Log.Information("Applying background-staged update to {Version}", marker.Version);
        SelfUpdateStager.WriteAppliedMarker(stagingDir, marker.Version, marker.ReleaseNotes);
        SelfUpdateLauncher.LaunchApplyAndRestart(currentExePath, marker.ExePath, Environment.ProcessId, args);
        Log.CloseAndFlush();
        return true;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
