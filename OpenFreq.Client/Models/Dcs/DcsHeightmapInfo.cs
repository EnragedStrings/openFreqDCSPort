using System;

namespace OpenFreq.Client.Models.Dcs;

public class DcsHeightmapInfo
{
    public string Theater { get; set; } = string.Empty;
    public string RawPath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public double OriginX { get; set; }
    public double OriginZ { get; set; }
    public double WidthMeters { get; set; }
    public double HeightMeters { get; set; }
    public int SamplesX { get; set; }
    public int SamplesZ { get; set; }
    public bool Ready { get; set; }

    public double CellSizeMeters =>
        SamplesX > 1 && SamplesZ > 1
            ? Math.Max(WidthMeters / (SamplesX - 1), HeightMeters / (SamplesZ - 1))
            : 1.0d;

    public DcsHeightmapInfo Clone()
    {
        return new DcsHeightmapInfo
        {
            Theater = Theater,
            RawPath = RawPath,
            MetadataPath = MetadataPath,
            OriginX = OriginX,
            OriginZ = OriginZ,
            WidthMeters = WidthMeters,
            HeightMeters = HeightMeters,
            SamplesX = SamplesX,
            SamplesZ = SamplesZ,
            Ready = Ready
        };
    }
}
