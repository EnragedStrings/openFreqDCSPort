using OpenFreqAudio;
using OpenFreqServer.SrsBridge;

namespace OpenFreq.Server.Tests.SrsBridge;

/// <summary>
/// Covers SrsSignalQuality's geodetic-distance AudioParams computation and its "missing position
/// data is not evidence of a bad link" fallback -- see the class's own doc comment.
/// </summary>
public class SrsSignalQualityTests
{
    [Fact]
    public void MissingTransmitterPosition_FallsBackToClear()
    {
        var audioParams = SrsSignalQuality.CreateAudioParams(251_000, 10, 0,
            txLatitudeDeg: null, txLongitudeDeg: null, txAltitudeMeters: null,
            rxLatitudeDeg: 45.0, rxLongitudeDeg: 45.0, rxAltitudeMeters: 1000);

        Assert.False(audioParams.SignalBlocked);
        Assert.Equal(0, audioParams.DropoutRate);
        Assert.Equal(0, audioParams.DeepFadeRate);
    }

    [Fact]
    public void MissingReceiverPosition_FallsBackToClear()
    {
        var audioParams = SrsSignalQuality.CreateAudioParams(251_000, 10, 0,
            txLatitudeDeg: 45.0, txLongitudeDeg: 45.0, txAltitudeMeters: 1000,
            rxLatitudeDeg: null, rxLongitudeDeg: null, rxAltitudeMeters: null);

        Assert.False(audioParams.SignalBlocked);
    }

    [Fact]
    public void BothPositionsKnown_ComputesRealDistanceDegradation()
    {
        var near = SrsSignalQuality.CreateAudioParams(251_000, 10, 0,
            45.0, 45.0, 3000, 45.05, 45.0, 3000);
        var far = SrsSignalQuality.CreateAudioParams(251_000, 10, 0,
            45.0, 45.0, 3000, 47.0, 45.0, 3000);

        Assert.True(far.ReceivedSnrDb < near.ReceivedSnrDb);
    }

    [Fact]
    public void FarBeyondHorizon_BlocksSignal()
    {
        var audioParams = SrsSignalQuality.CreateAudioParams(251_000, 10, 0,
            0.0, 0.0, 0, 5.0, 0.0, 0);

        Assert.True(audioParams.SignalBlocked);
    }
}
