using System;

namespace FalconBmsDataService.Models;

/// <summary>
/// Represents the current position of the aircraft in Falcon BMS coordinates
/// </summary>
public class FlightPosition
{
    /// <summary>
    /// Ownship North position (Feet)
    /// </summary>
    public int X { get; set; }

    /// <summary>
    /// Ownship East position (Feet)
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    /// Ownship Down position (Feet)
    /// </summary>
    public int Z { get; set; }

    /// <summary>
    /// Timestamp when this data was captured
    /// </summary>
    public DateTime Timestamp { get; set; }

    public FlightPosition()
    {
        Timestamp = DateTime.UtcNow;
    }

    public FlightPosition(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
        Timestamp = DateTime.UtcNow;
    }

    public override string ToString()
    {
        return $"Position(X={X:F2}, Y={Y:F2}, Z={Z:F2})";
    }
}
