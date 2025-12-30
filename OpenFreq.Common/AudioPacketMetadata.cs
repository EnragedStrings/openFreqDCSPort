using System.Text.Json.Serialization;

namespace OpenFreq.Common;

/// <summary>
/// Metadata included in the header of each UDP audio packet
/// </summary>
public class AudioPacketMetadata
{
    [JsonPropertyName("id")]
    public string clientId  { get; set; }
    /// <summary>
    /// List of frequencies this audio packet should be transmitted on
    /// </summary>
    [JsonPropertyName("frequencies")]
    public List<double> Frequencies { get; set; } = new();
    
    /// <summary>
    /// Optional timestamp for debugging/monitoring
    /// </summary>
    [JsonPropertyName("capture_timestamp")]
    public long CaptureTimestamp { get; set; }
    
    [JsonPropertyName("send_timestamp")]
    public long SendTimestamp { get; set; }
    
    [JsonPropertyName("server_send_timestamp")]

    public long ServerSendTimestamp { get; set; }

    
    [JsonPropertyName("position")]
    public AircraftPosition? Position { get; set; }
    
    public bool In3D { get; set; }
    
    public int PcmDataLength { get; set; }
}

// Position data structure
public class AircraftPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    
    public AircraftPosition() {}

    public AircraftPosition(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }
    
    public AircraftPosition ToHeightmapPosition()
    {
        double cellSizeM = 31.25d;
        const double GameSizeMeters = 1024000d;

        return new AircraftPosition
        {
            X = this.X / cellSizeM,
            Y = (GameSizeMeters - this.Y) / cellSizeM,
            Z = this.Z
        };
/*
        const double HeightmapSize = 32768d;
        const double GameSizeMeters = 1024000d;
        double scale = HeightmapSize / GameSizeMeters;

        double heightX = X * scale;
        double heightY = (GameSizeMeters - Y) * scale; // flip Y axis
        return new AircraftPosition(heightX, heightY, Z);
        */
    }

}