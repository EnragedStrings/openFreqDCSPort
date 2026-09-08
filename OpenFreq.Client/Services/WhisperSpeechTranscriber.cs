using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqAudio.Satcom;
using Whisper.net;
using Whisper.net.Ggml;

namespace OpenFreqClient.Services;

/// <summary>
/// Local (fully offline) speech-to-text via Whisper.net (whisper.cpp binding, MIT-licensed) -- no
/// audio ever leaves this machine, matching the "local only" choice made for this feature. The
/// model is not bundled with the app (it's tens of MB); instead it's downloaded once, on first use,
/// via Whisper.net's own Hugging-Face downloader and cached under
/// AppDataPaths.ClientModelDirectory, same pattern as any other lazily-fetched large asset.
/// </summary>
public sealed class WhisperSpeechTranscriber : ISpeechTranscriber
{
    // whisper.cpp's models are trained on and require exactly 16kHz mono input, regardless of the
    // network pipeline's own 48kHz (OpenFreqRtcClient.SAMPLE_RATE) -- downsampled below via the
    // same SatcomDownsampler already used (and tested) for the SATCOM vocoder's 48kHz->8kHz path.
    // 48000 is an exact 3x multiple of 16000, satisfying SatcomDownsampler's integer-ratio
    // requirement.
    private const int WhisperSampleRate = 16000;

    private readonly ILogger<WhisperSpeechTranscriber> _logger;
    private readonly string _modelPath;
    private readonly GgmlType _ggmlType;
    private readonly string _language;

    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public WhisperSpeechTranscriber(ILogger<WhisperSpeechTranscriber> logger, string modelPath,
        GgmlType ggmlType = GgmlType.TinyEn, string language = "en")
    {
        _logger = logger;
        _modelPath = modelPath;
        _ggmlType = ggmlType;
        _language = language;
    }

    private async Task<WhisperFactory?> GetFactoryAsync(CancellationToken ct)
    {
        if (_factory != null) return _factory;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_factory != null) return _factory;

            if (!File.Exists(_modelPath))
            {
                _logger.LogInformation(
                    "Speech-to-text model not found locally; downloading {GgmlType} to {Path}...",
                    _ggmlType, _modelPath);

                Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
                var tempPath = _modelPath + ".download";
                await using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(_ggmlType))
                await using (var fileStream = File.Create(tempPath))
                {
                    await modelStream.CopyToAsync(fileStream, ct);
                }

                File.Move(tempPath, _modelPath, overwrite: true);
                _logger.LogInformation("Speech-to-text model downloaded to {Path}", _modelPath);
            }

            _factory = WhisperFactory.FromPath(_modelPath);
            return _factory;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load/download speech-to-text model from {Path} -- " +
                                  "transcription disabled for this session", _modelPath);
            return null;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<List<TranscriptWordDto>> TranscribeAsync(short[] pcm, int sampleRate,
        CancellationToken ct = default)
    {
        var words = new List<TranscriptWordDto>();

        try
        {
            var factory = await GetFactoryAsync(ct);
            if (factory == null) return words;

            var samples = ToWhisperSamples(pcm, sampleRate);
            if (samples.Length == 0) return words;

            using var processor = factory.CreateBuilder()
                .WithLanguage(_language)
                .WithTokenTimestamps()
                .SplitOnWord()
                .Build();

            await foreach (var segment in processor.ProcessAsync(samples, ct))
            {
                if (string.IsNullOrWhiteSpace(segment.Text)) continue;

                words.Add(new TranscriptWordDto
                {
                    Text = segment.Text,
                    StartSec = segment.Start.TotalSeconds,
                    EndSec = segment.End.TotalSeconds
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech-to-text transcription failed for this transmission");
        }

        return words;
    }

    /// <summary>16-bit PCM at the network's native rate -> normalized float32 at whisper.cpp's
    /// required 16kHz. SplitOnWord()+WithTokenTimestamps() above makes each yielded "segment"
    /// correspond to one word already, with its own Start/End -- see TranscriptWordDto's own doc
    /// comment on why word-level granularity matters (stepped-transmission redaction).</summary>
    private static float[] ToWhisperSamples(short[] pcm, int sampleRate)
    {
        if (sampleRate == WhisperSampleRate)
        {
            var direct = new float[pcm.Length];
            for (var i = 0; i < pcm.Length; i++)
                direct[i] = pcm[i] / 32768f;
            return direct;
        }

        var downsampler = new SatcomDownsampler(sampleRate, WhisperSampleRate);
        var output = new List<float>(pcm.Length * WhisperSampleRate / sampleRate + 1);
        foreach (var sample in pcm)
        {
            if (downsampler.Process(sample / 32768.0, out var outSample))
                output.Add((float)outSample);
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _initLock.Dispose();
    }
}
