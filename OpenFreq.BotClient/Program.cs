/*
 * OpenFreq.BotClient -- demo/reference bot-client
 *
 * Connects once, joins one or more named/positioned frequencies simultaneously (e.g. "Nellis
 * Tower" and "Luke Tower" on different frequencies over one connection -- the same "one
 * connection, many positions" shape GCI mode's own multi-location UI already uses), sets
 * WantsTranscripts so the server delivers text transcripts of everything audible at each
 * position (already gated by real line-of-sight/audibility -- see TranscriptDeliveryMessage),
 * and replies by transmitting a short audio clip back on whichever frequency/position received
 * the transcript. This is the reference implementation an LLM-driven ATC/GCI tool would build
 * on: swap the fixed "reply with a WAV" step for a call out to an LLM and a TTS engine.
 *
 * Usage: dotnet run [path-to-config.json]  (defaults to "botclient.json" next to the exe)
 */

using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFreq.BotClient;
using OpenFreq.Common;
using OpenFreqAudio;

var configPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "botclient.json");

if (!File.Exists(configPath))
{
    var exampleSource = Path.Combine(AppContext.BaseDirectory, "botclient.example.json");
    if (File.Exists(exampleSource))
        File.Copy(exampleSource, configPath);

    Console.WriteLine($"No config found at '{configPath}'.");
    Console.WriteLine(File.Exists(configPath)
        ? $"Wrote a starter config there from botclient.example.json -- edit it (server address, password, positions) and run again."
        : "Create one modeled on botclient.example.json (server address, password, and a list of named/positioned frequencies) and run again.");
    return;
}

BotClientConfig config;
try
{
    await using var configStream = File.OpenRead(configPath);
    config = await JsonSerializer.DeserializeAsync(configStream, BotClientJsonContext.Default.BotClientConfig)
              ?? throw new InvalidDataException("Config deserialized to null");
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to read config '{configPath}': {ex.Message}");
    return;
}

if (config.Positions.Count == 0)
{
    Console.WriteLine("Config has no positions configured -- nothing to join. Add at least one entry to \"positions\".");
    return;
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("BotClient");

var positionsByFrequency = config.Positions
    .GroupBy(p => p.FrequencyKhz)
    .ToDictionary(g => g.Key, g => g.First());
if (positionsByFrequency.Count != config.Positions.Count)
    logger.LogWarning("Multiple configured positions share the same frequency -- only the first will be used for replies on that frequency");

var client = new OpenFreqRtcClient(loggerFactory, config.ServerAddress, config.Password, config.DisplayName,
    wantsTranscripts: true);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

client.TranscriptReceived += async (_, e) =>
{
    var message = e.Message;
    if (!positionsByFrequency.TryGetValue(message.FrequencyKhz, out var position))
        return;

    logger.LogInformation("[{Position}] \"{Text}\" (from {From})", position.Name, message.Text, message.FromDisplayName);

    try
    {
        await RespondAsync(client, position, logger);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to send response on {Position}", position.Name);
    }
};

client.ErrorOccurred += (_, e) => logger.LogWarning("Client error: {Message}", e.ErrorMessage);

try
{
    logger.LogInformation("Connecting to {Server}...", config.ServerAddress);
    await client.ConnectAsync();
    logger.LogInformation("Connected (peer {PeerId})", client.MyPeerId);

    foreach (var position in config.Positions)
    {
        await client.JoinFrequencyAsync(position.FrequencyKhz, position.Lat, position.Lon, position.Alt);
        logger.LogInformation("Joined \"{Name}\" on {FreqMhz:F3} MHz", position.Name, position.FrequencyKhz / 1000.0);
    }

    logger.LogInformation("Listening for transcripts. Press Ctrl+C to stop.");
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // normal shutdown via Ctrl+C
}
catch (Exception ex)
{
    logger.LogError(ex, "Fatal error");
}
finally
{
    client.Dispose();
}

return;

static async Task RespondAsync(OpenFreqRtcClient client, BotPositionConfig position, ILogger logger)
{
    var pcm = LoadOrSynthesizeResponse(position, logger);

    var transmissions = new List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position,
        Vector3? velocity, Vector3? dcsPosition, AmbientNoiseType ambientNoiseType, bool enc, int encKey,
        bool hqOn, double? latitudeDeg, double? longitudeDeg, double? altitudeMeters)>
    {
        (position.FrequencyKhz, 50, 0, null, null, null, AmbientNoiseType.None, false, 0, false,
            position.Lat, position.Lon, position.Alt)
    };

    await client.StartTransmissionAsync(position.FrequencyKhz, position.Lat, position.Lon, position.Alt);
    try
    {
        const int samplesPerFrame = OpenFreqRtcClient.OPUS_SAMPLES_PER_FRAME;
        for (var offset = 0; offset < pcm.Length; offset += samplesPerFrame)
        {
            var count = Math.Min(samplesPerFrame, pcm.Length - offset);
            var frame = count == samplesPerFrame ? pcm[offset..(offset + count)] : PadFrame(pcm, offset, count, samplesPerFrame);
            client.SendAudio(frame, transmissions);
            await Task.Delay(OpenFreqRtcClient.FRAME_SIZE_MS);
        }
    }
    finally
    {
        await client.StopTransmissionAsync(position.FrequencyKhz);
    }
}

static short[] PadFrame(short[] pcm, int offset, int count, int frameSize)
{
    var frame = new short[frameSize];
    Array.Copy(pcm, offset, frame, 0, count);
    return frame;
}

static short[] LoadOrSynthesizeResponse(BotPositionConfig position, ILogger logger)
{
    if (!string.IsNullOrWhiteSpace(position.ResponseWavPath) && File.Exists(position.ResponseWavPath))
    {
        try
        {
            var wav = WavFile.Read(position.ResponseWavPath);
            var mono = WavFile.ToMono(wav);
            return WavFile.Resample(mono, wav.SampleRate, OpenFreqRtcClient.SAMPLE_RATE);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load response WAV '{Path}' for {Position}, using a synthesized tone instead",
                position.ResponseWavPath, position.Name);
        }
    }

    return SynthesizeAcknowledgmentTone();
}

/// <summary>A short two-beep acknowledgment tone -- used when no response WAV is configured, so
/// the demo runs end-to-end with zero audio assets required.</summary>
static short[] SynthesizeAcknowledgmentTone()
{
    const int sampleRate = OpenFreqRtcClient.SAMPLE_RATE;
    const double beepSeconds = 0.15;
    const double gapSeconds = 0.08;
    const double frequencyHz = 700;
    const double amplitude = 0.3 * short.MaxValue;

    var beepSamples = (int)(sampleRate * beepSeconds);
    var gapSamples = (int)(sampleRate * gapSeconds);
    var pcm = new short[beepSamples * 2 + gapSamples];

    for (var i = 0; i < beepSamples; i++)
        pcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
    for (var i = 0; i < beepSamples; i++)
        pcm[beepSamples + gapSamples + i] = (short)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));

    return pcm;
}
