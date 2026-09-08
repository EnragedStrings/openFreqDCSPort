using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFreq.Common;

namespace OpenFreqClient.Services;

/// <summary>
/// Abstraction over the local speech-to-text engine (see WhisperSpeechTranscriber), so the
/// surrounding capture/buffering/dispatch pipeline in OpenFreqService can be exercised in tests
/// with a fake instead of a real native Whisper model.
/// </summary>
public interface ISpeechTranscriber : IDisposable
{
    /// <summary>Transcribes one complete PTT session's raw mono PCM into word-level timed tokens,
    /// StartSec/EndSec relative to the start of <paramref name="pcm"/> -- see
    /// TranscriptWordDto's own doc comment on why word-level, not sentence-level. Returns an empty
    /// list (never throws) on a missing/failed-to-load model, a transcription error, or when no
    /// speech was detected -- a caller should treat "nothing to send" as the normal outcome for a
    /// short/silent buffer, not an error condition.</summary>
    Task<List<TranscriptWordDto>> TranscribeAsync(short[] pcm, int sampleRate, CancellationToken ct = default);
}
