using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
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

        // Set up dependency injection
        var services = new ServiceCollection();
        services.AddOpenFreqServices();
        ServiceProvider = services.BuildServiceProvider();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
