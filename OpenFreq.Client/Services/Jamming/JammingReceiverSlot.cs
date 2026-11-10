using System;
using OpenFreq.Client.Models;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Common;

namespace OpenFreq.Client.Services.Jamming;

/// <summary>
/// One tuned receiver slot as the jamming model needs it: where the radio is (in every frame this client knows)
/// and its sensitivity, the same reference <c>ReceivedSnrDb</c> uses (OpenFreqService.GetReceiverSensitivityDb).
/// </summary>
/// <param name="HeightmapPosition">Position in the loaded heightmap's frame (x, y, altitude m), as fed to the
/// terrain signal calculator; null when unknown.</param>
/// <param name="DcsLocalPosition">DCS mission-local position (x north, y altitude, z east), DCS slots only.</param>
/// <param name="Geodetic">Latitude, longitude, altitude MSL, when known (DCS slots).</param>
public sealed record JammingReceiverSlot(
    int FrequencyKhz,
    Guid SlotId,
    RadioStationData.RadioStationType StationType,
    Vector3? HeightmapPosition,
    DcsVector3? DcsLocalPosition,
    (double LatitudeDeg, double LongitudeDeg, double AltitudeMeters)? Geodetic,
    double ReceiverSensitivityDbm,
    bool IsSatcomActive,
    bool IsEnabled);
