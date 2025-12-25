using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenFreqClient.Models;

namespace OpenFreq.Services.Acmi;

public interface IAcmiClientService : IDisposable
{
    /// <summary>Fired when connection status changes</summary>
    event EventHandler<AcmiConnectionEventArgs>? ConnectionStatusChanged;

    /// <summary>Fired when connection is established</summary>
    event EventHandler<AcmiConnectionEventArgs>? Connected;

    /// <summary>Fired when connection is lost</summary>
    event EventHandler<AcmiConnectionEventArgs>? ConnectionLost;

    /// <summary>
    /// Fired when the tracked aircraft's position/transform is updated.
    /// Only fires for the aircraft set via SetTrackedAircraft().
    /// </summary>
    event EventHandler<AircraftTransformEventArgs>? TrackedAircraftTransformUpdated;

    /// <summary>Connects to the ACMI server</summary>
    Task<bool> ConnectAsync(string connectionString, string password = "", int maxRetries = 99);

    /// <summary>Disconnects from the ACMI server</summary>
    Task DisconnectAsync();

    /// <summary>Gets an aircraft by its object ID</summary>
    AcmiAircraft? GetAircraft(string objectId);

    /// <summary>Gets all aircraft currently tracked</summary>
    IEnumerable<AcmiAircraft> GetAllAircraft();
    
    /// <summary>
    /// Sets which aircraft to track for position updates.
    /// Only this aircraft will trigger the TrackedAircraftTransformUpdated event.
    /// Pass null to stop tracking.
    /// </summary>
    void SetTrackedAircraft(string? objectId);
    
    /// <summary>Gets the currently tracked aircraft object ID, or null if none</summary>
    string? TrackedAircraftId { get; }
    
    public AcmiConnectionStatus Status { get; }
}

/// <summary>
/// Event args for tracked aircraft transform updates
/// </summary>
public class AircraftTransformEventArgs : EventArgs
{
    /// <summary>The aircraft's object ID</summary>
    public string ObjectId { get; set; } = string.Empty;
    
    /// <summary>The aircraft's current transform data</summary>
    public AircraftTransform Transform { get; set; } = new();
    
    /// <summary>When this update occurred</summary>
    public DateTime Timestamp { get; set; }
}