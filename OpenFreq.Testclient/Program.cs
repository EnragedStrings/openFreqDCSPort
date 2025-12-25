/*
 * OpenFreq Server Test Client
 * Simple single-file client for testing OpenFreq Server with BASS audio
 *
 * Usage: dotnet run <server-ip> <password> <frequency>
 * Example: dotnet run 127.0.0.1 changeMe123 123.450
 *
 * Controls:
 * - Press SPACE to transmit (Push-to-Talk)
 * - Press Q to quit
 */

using System.Runtime.InteropServices;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpusSharp.Core;

string serverIp = args.Length > 0 ? args[0] : "127.0.0.1";
string password = args.Length > 1 ? args[1] : "changeMe123";
List<double> frequencies =
[
    //1.234d,
    334.0d,
    307.3d
];

Console.WriteLine("╔════════════════════════════════════════╗");
Console.WriteLine("║   OpenFreq Server Test Client (BASS)    ║");
Console.WriteLine("╚════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine($"Server: {serverIp}");
Console.WriteLine($"Frequency: {frequencies} MHz");
Console.WriteLine();

// Initialize BASS
if (!Bass.Init())
{
    Console.WriteLine($"Failed to initialize BASS: {Bass.LastError}");
    return;
}

Console.WriteLine("BASS initialized successfully");

if (Bass.RecordingDeviceCount == 0)
{
    Console.WriteLine("No recording device found");
    Bass.Free();
    return;
}

// Get recording device
var recordDevice = 0;
Console.WriteLine($"Using {Bass.RecordGetDeviceInfo(recordDevice).Name}");
Bass.RecordInit(recordDevice);


using ILoggerFactory factory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});
// Create client
var client = new OpenFreqRtcClient(factory.CreateLogger<OpenFreqRtcClient>(), serverIp, password);
var testClient = new TestClientWrapper(client, frequencies);

try
{
    await testClient.ConnectAsync();

    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════");
    Console.WriteLine("  Controls:");
    Console.WriteLine("  [SPACE] - Push to Talk");
    Console.WriteLine("  [Q] - Quit");
    Console.WriteLine("═══════════════════════════════════════");
    Console.WriteLine();

    await testClient.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally
{
    testClient.Dispose();
    Bass.Free();
}

// ============================================================================
// TestClientWrapper Class - Wraps OpenFreqRtcClient with BASS audio
// ============================================================================

public class TestClientWrapper : IDisposable
{
    const int SAMPLE_RATE = 48000;
    const int CHANNELS = 1;
    
    private readonly Queue<byte> _audioBuffer = new Queue<byte>();
    private readonly int OPUS_FRAME_BYTES = 1920;

    private readonly OpenFreqRtcClient _client;
    private readonly List<double> _frequencies;
    private bool _isTransmitting;
    private bool _isRunning;
    private CancellationTokenSource _cts = new();

    // BASS handles
    private int _recordHandle;
    private int _playbackStream;
    
    private OpusDecoder _opusDecoder = new OpusDecoder(SAMPLE_RATE, CHANNELS);


    public TestClientWrapper(OpenFreqRtcClient client, List<double> frequencies)
    {
        _client = client;
        _frequencies = frequencies;

        // Hook up events
        _client.Authenticated += OnAuthenticated;
        _client.FrequencyJoined += OnFrequencyJoined;
        _client.PeerJoined += OnPeerJoined;
        _client.PeerLeft += OnPeerLeft;
        _client.PeerTransmissionStateChanged += OnPeerTransmissionStateChanged;
        _client.AudioDataReceived += OnAudioDataReceived;
        _client.ErrorOccurred += OnError;
        _client.TransmissionStateChanged += OnTransmissionStateChanged;
    }

    public async Task ConnectAsync()
    {
        // Connect to server
        await _client.ConnectAsync();
        Console.WriteLine("✓ WebSocket connected");
        Console.WriteLine($"✓ Authenticated (Peer ID: {_client.MyPeerId})");
        Console.WriteLine($"✓ Audio port assigned: {_client.AudioPort}");

        // Join frequency
        foreach (var freq in _frequencies)
        {
            await _client.JoinFrequencyAsync(freq);
            await Task.Delay(300);
            Console.WriteLine($"✓ Joined frequency {_frequencies}");
        }

        // Initialize playback stream
        _playbackStream = Bass.CreateStream(SAMPLE_RATE, CHANNELS, BassFlags.Default, StreamProcedureType.Push);
        if (_playbackStream == 0)
        {
            throw new Exception($"Failed to create playback stream: {Bass.LastError}");
        }

        // When starting playback, pre-fill with 3-4 frames of silence
        for (int i = 0; i < 4; i++)
        {
            byte[] silence = new byte[1920];
            Bass.StreamPutData(_playbackStream, silence, silence.Length);
        }
        Bass.ChannelPlay(_playbackStream, false);
        Console.WriteLine("✓ Audio playback ready");
    }

    public async Task RunAsync()
    {
        _isRunning = true;
        DateTime? lastSpaceTime = null;
        const int SpaceReleaseDelayMs = 150;

        // Input loop
        while (_isRunning)
        {

            // Consume all available keys in the buffer
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Spacebar)
                {
                    lastSpaceTime = DateTime.UtcNow;
                }
                else if (key.Key == ConsoleKey.Q)
                {
                    await StopTransmissionAsync();
                    _isRunning = false;
                    break;
                }
            }

            if (!_isRunning) break;

            // Determine if space is "currently held" based on recent activity
            bool spaceIsHeld = lastSpaceTime.HasValue && 
                               (DateTime.UtcNow - lastSpaceTime.Value).TotalMilliseconds < SpaceReleaseDelayMs;

            // Handle transmission
            if (spaceIsHeld && !_isTransmitting)
            {
                await StartTransmissionAsync();
            }
            else if (!spaceIsHeld && _isTransmitting)
            {
                await StopTransmissionAsync();
                lastSpaceTime = null;
            }

            await Task.Delay(50);
        }
    }

    private async Task StartTransmissionAsync()
    {
        Bass.CurrentRecordingDevice = 0;
        _isTransmitting = true;

        // Start recording with callback that sends via client
        _recordHandle = Bass.RecordStart(SAMPLE_RATE, CHANNELS, BassFlags.RecordPause, RecordProcedure);
        if (_recordHandle == 0)
        {
            Console.WriteLine($"Failed to start recording: {Bass.LastError}");
            return;
        }

        Bass.ChannelPlay(_recordHandle);

        // Tell client to start transmission on this frequency

        foreach (var frequency in _frequencies)
        {
            await _client.StartTransmissionAsync(frequency);
        }
    }

    private async Task StopTransmissionAsync()
    {
        if (!_isTransmitting) return;

        _isTransmitting = false;

        // Stop recording
        if (_recordHandle != 0)
        {
            Bass.ChannelStop(_recordHandle);
            Bass.StreamFree(_recordHandle);
            _recordHandle = 0;
        }

        // Tell client to stop transmission
        foreach (var frequency in _frequencies)
        {
            await _client.StopTransmissionAsync(frequency);
        }    }

    private bool RecordProcedure(int handle, IntPtr buffer, int length, IntPtr user)
    {
        if (!_isTransmitting)
            return true;

        try
        {
            // Copy audio data to managed array
            byte[] audioData = new byte[length];
            Marshal.Copy(buffer, audioData, 0, length);

            // Add to buffer
            foreach (byte b in audioData)
            {
                _audioBuffer.Enqueue(b);
            }

            // Process complete frames
            while (_audioBuffer.Count >= OPUS_FRAME_BYTES)
            {
                // Extract exactly one frame
                byte[] frameData = new byte[OPUS_FRAME_BYTES];
                for (int i = 0; i < OPUS_FRAME_BYTES; i++)
                {
                    frameData[i] = _audioBuffer.Dequeue();
                }

                // Send complete frame
                
                _client.SendAudioDataSync(_frequencies, frameData);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending audio: {ex.Message}");
        }

        return true;
    }

    // Event handlers
    private void OnAuthenticated(object? sender, AuthenticationEventArgs e)
    {
        // Already logged in ConnectAsync
    }

    private void OnFrequencyJoined(object? sender, FrequencyJoinedEventArgs e)
    {
        // Already logged in ConnectAsync
    }

    private void OnPeerJoined(object? sender, PeerEventArgs e)
    {
        Console.WriteLine($"[PEER JOINED] {e.PeerId}");
    }

    private void OnPeerLeft(object? sender, PeerEventArgs e)
    {
        Console.WriteLine($"[PEER LEFT] {e.PeerId}");
    }

    private void OnPeerTransmissionStateChanged(object? sender, PeerTransmissionEventArgs e)
    {
        var state = e.IsTransmitting ? "TRANSMITTING" : "STOPPED";
        var shortId = e.PeerId.Length > 8 ? e.PeerId.Substring(0, 8) : e.PeerId;
        Console.WriteLine($"[PEER {shortId}] {state}");
    }

    private void OnTransmissionStateChanged(object? sender, TransmissionStateEventArgs e)
    {
        Console.WriteLine(e.IsTransmitting ? "[TRANSMITTING]" : "[STOPPED]");
    }

    private void OnAudioDataReceived(object? sender, AudioDataEventArgs e)
    {
        if (_playbackStream == 0) return;

        long bufferLevel = Bass.ChannelGetData(_playbackStream, IntPtr.Zero, (int)DataFlags.Available);
        double bufferMs = (bufferLevel / 96000.0) * 1000.0;

        const double MAX_BUFFER_MS = 125; // More tolerance

        if (bufferMs > MAX_BUFFER_MS)
        {
            // Just flush and start fresh
            Bass.ChannelSetPosition(_playbackStream, 0);
            Console.WriteLine($"[BUFFER RESET] Was {bufferMs:F1}ms");
        }
    
        Bass.StreamPutData(_playbackStream, e.AudioData, e.AudioData.Length);
    }

    private void OnError(object? sender, OpenFreq.Common.ErrorEventArgs e)
    {
        Console.WriteLine($"[ERROR] {e.ErrorMessage}");
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();

        if (_recordHandle != 0)
        {
            Bass.ChannelStop(_recordHandle);
            Bass.StreamFree(_recordHandle);
        }

        if (_playbackStream != 0)
        {
            Bass.ChannelStop(_playbackStream);
            Bass.StreamFree(_playbackStream);
        }

        _client.Dispose();

        Console.WriteLine("Disconnected");
    }
}