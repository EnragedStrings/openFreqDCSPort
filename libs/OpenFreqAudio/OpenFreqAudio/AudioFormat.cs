namespace OpenFreqAudio;

/// <summary>
/// The audio format every part of OpenFreq shares: the rate the radio DSP, the Opus
/// codec and the sound devices all run at, the frame everything paces audio in,
/// and the silence that ends a talk-spurt.
/// </summary>
public static class AudioFormat
{
    // Baseband is 8 kHz (4kHz Nyquist)
    // NB: Opus only accepts 8000, 12000, 16000, 24000, or 48000 Hz
    public const int SampleRate = 48000;

    // One Opus frame, which is the unit everything upstream paces audio in.
    // The RTP sender encodes one per packet, the jitter buffer releases one per playout
    // slot and conceals a missing one, and OggOpusRecorder writes one at a time.
    public const int OpusFrameMs = 20;
    public const int OpusSamplesPerFrame = SampleRate * OpusFrameMs / 1000;

    // A gap in a peer's audio longer than this ends its talk-spurt (PTT released),
    // so the next audio starts a new one. The jitter buffer, the receive physics
    // and the radio mixer all use it. It must be longer than any gap inside one
    // transmission, such as the jitter buffer pausing playout for up to 250 ms to grow.
    public const int TalkspurtGapMs = 500;
}
