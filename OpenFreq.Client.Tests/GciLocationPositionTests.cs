using System.Collections.ObjectModel;
using NSubstitute;
using OpenFreq.Client.Models;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqAudio;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>A GCI location's map position must follow the coordinates it shows, however they got there.</summary>
public class GciLocationPositionTests
{
    // Caucasus coordinates, outside the default Korea KTO theater.
    private const double OutLat = 43.9, OutLon = 43.8;

    [Fact]
    public void TypedOutOfTheaterCoordinates_MoveTheRadio_AndShowTheError()
    {
        var loc = VmFactory.Location();
        var (cLat, cLon) = TheaterCoordinateConverter.GetCenterLatLon(loc.Settings.SelectedTheater);
        loc.Latitude = cLat;
        loc.Longitude = cLon;
        Assert.False(loc.HasCoordinateError);

        loc.Latitude = OutLat;
        loc.Longitude = OutLon;

        var expected = TheaterCoordinateConverter.LatLonToXYMeters(loc.Settings.SelectedTheater, OutLat, OutLon,
            TheaterCoordinateConverter.CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM);
        Assert.Equal(expected.x, loc.RadioStationData.Vector3.X, 3);
        Assert.Equal(expected.y, loc.RadioStationData.Vector3.Y, 3);
        Assert.True(loc.HasCoordinateError);
    }

    [Fact]
    public void TypedAndLoadedLocations_WithTheSameCoordinates_AreAtTheSamePosition()
    {
        var typed = VmFactory.Location();
        typed.Latitude = OutLat;
        typed.Longitude = OutLon;
        typed.AltitudeFeet = 30000;

        var of = VmFactory.OpenFreq();
        var loaded = new LocationViewModel(of, VmFactory.Hotkey(), Substitute.For<IAcmiClientService>(),
            VmFactory.Settings(of), "Loaded", RadioStationPresets.FighterF16,
            RadioStationData.RadioStationType.STATIONARY,
            new ObservableCollection<LocationViewModel.TacviewAircraftItem>(),
            latitude: OutLat, longitude: OutLon, altitudeFeet: 30000);

        Assert.Equal(typed.RadioStationData.Vector3.X, loaded.RadioStationData.Vector3.X, 3);
        Assert.Equal(typed.RadioStationData.Vector3.Y, loaded.RadioStationData.Vector3.Y, 3);
        Assert.Equal(typed.RadioStationData.Vector3.Z, loaded.RadioStationData.Vector3.Z, 3);
        Assert.True(loaded.HasCoordinateError);
    }

    [Fact]
    public void ChangingTheTheater_RecomputesThePosition()
    {
        var loc = VmFactory.Location();
        var (cLat, cLon) = TheaterCoordinateConverter.GetCenterLatLon(loc.Settings.SelectedTheater);
        loc.Latitude = cLat;
        loc.Longitude = cLon;
        var before = loc.RadioStationData.Vector3;

        var other = TheaterCoordinateConverter.GetAvailableTheaters().First(t => t != loc.Settings.SelectedTheater);
        loc.Settings.SelectedTheater = other;

        var expected = TheaterCoordinateConverter.LatLonToXYMeters(other, cLat, cLon,
            TheaterCoordinateConverter.CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM);
        Assert.Equal(expected.x, loc.RadioStationData.Vector3.X, 3);
        Assert.Equal(expected.y, loc.RadioStationData.Vector3.Y, 3);
        Assert.NotEqual(before.X, loc.RadioStationData.Vector3.X);
    }
}
