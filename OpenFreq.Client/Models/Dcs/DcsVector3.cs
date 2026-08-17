namespace OpenFreq.Client.Models.Dcs;

public class DcsVector3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public DcsVector3()
    {
    }

    public DcsVector3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}
