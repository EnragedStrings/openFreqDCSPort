using OpenFreqAudio;

namespace OpenFreqAudio.Tests;

/// <summary>
/// Covers the geometric (no-terrain) signal model shared by OpenFreqService's own no-terrain
/// fallback and the SRS bridge (SrsSignalQuality.cs) -- see FreeSpacePathModel's own doc comment.
/// </summary>
public class FreeSpacePathModelTests
{
    [Fact]
    public void GreatCircleDistanceMeters_KnownPoints_MatchesExpectedDistance()
    {
        // London (51.5074, -0.1278) to Paris (48.8566, 2.3522), both at sea level -- real
        // great-circle distance is ~343.5 km. Loose tolerance since this is a sanity check on the
        // haversine implementation, not a geodesy precision test.
        var distance = FreeSpacePathModel.GreatCircleDistanceMeters(51.5074, -0.1278, 0, 48.8566, 2.3522, 0);

        Assert.InRange(distance, 340_000, 347_000);
    }

    [Fact]
    public void GreatCircleDistanceMeters_SamePoint_IsZero()
    {
        var distance = FreeSpacePathModel.GreatCircleDistanceMeters(45.0, 45.0, 1000, 45.0, 45.0, 1000);

        Assert.Equal(0, distance, precision: 6);
    }

    [Fact]
    public void GreatCircleDistanceMeters_AltitudeDeltaOnly_MatchesVerticalDistance()
    {
        // Same lat/lon, only altitude differs -- surface distance is 0, so total distance should
        // equal the altitude delta exactly (Pythagoras with a zero leg).
        var distance = FreeSpacePathModel.GreatCircleDistanceMeters(45.0, 45.0, 0, 45.0, 45.0, 5000);

        Assert.Equal(5000, distance, precision: 3);
    }

    [Fact]
    public void CreateBaseAudioParams_FartherDistance_WeakerSignal()
    {
        var near = FreeSpacePathModel.CreateBaseAudioParams(251_000, 10, 0, 10_000, -113);
        var far = FreeSpacePathModel.CreateBaseAudioParams(251_000, 10, 0, 100_000, -113);

        Assert.True(far.ReceivedSnrDb < near.ReceivedSnrDb);
    }

    [Fact]
    public void ApplyHorizonLoss_WellBeyondHorizon_BlocksSignal()
    {
        var audioParams = FreeSpacePathModel.CreateBaseAudioParams(251_000, 10, 0, 5000, -113);

        // Two ground-level (2m AGL floor per CalculateRadioHorizonMeters) stations far enough
        // apart that no amount of standard-atmosphere refraction lets them see each other.
        FreeSpacePathModel.ApplyHorizonLoss(audioParams, 500_000, 0, 0);

        Assert.True(audioParams.SignalBlocked);
    }

    [Fact]
    public void ApplyHorizonLoss_WellWithinHorizon_DoesNotBlock()
    {
        var audioParams = FreeSpacePathModel.CreateBaseAudioParams(251_000, 10, 0, 5000, -113);

        FreeSpacePathModel.ApplyHorizonLoss(audioParams, 5000, 3000, 3000);

        Assert.False(audioParams.SignalBlocked);
    }

    [Fact]
    public void CreateAudioParams_CombinesBaseAndHorizonStages()
    {
        var combined = FreeSpacePathModel.CreateAudioParams(251_000, 10, 0, 500_000, 0, 0, -113);
        var baseOnly = FreeSpacePathModel.CreateBaseAudioParams(251_000, 10, 0, 500_000, -113);

        // The one-shot form must include the horizon block that the base-only form doesn't apply.
        Assert.False(baseOnly.SignalBlocked);
        Assert.True(combined.SignalBlocked);
    }
}
