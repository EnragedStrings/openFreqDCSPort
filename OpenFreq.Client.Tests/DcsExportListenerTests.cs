using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Client.Services;

namespace OpenFreq.Client.Tests;

/// <summary>
/// The DCS export listener used to fail silently when its UDP port was taken (typically by a second OpenFreq window):
/// DCS kept exporting, this window showed no radios, and only the log said why. It must now tell the user.
/// </summary>
public class DcsExportListenerTests
{
    [Fact]
    public void PortTaken_ReportsUserFacingError()
    {
        // Windows refuses a second bind of the same UDP port (what a second OpenFreq window hits); Linux .NET sockets
        // share it, so there is nothing to report there -- and DCS, the only sender, runs on Windows.
        if (!OperatingSystem.IsWindows()) return;
        using var occupier = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)occupier.Client.LocalEndPoint!).Port;
        using var service = new DcsExportService(NullLogger<DcsExportService>.Instance) { UdpPort = port };
        string? raised = null;
        service.ListenerErrorOccurred += (_, message) => raised = message;

        service.Start();

        Assert.NotNull(raised);
        Assert.Equal(raised, service.ListenerError);
        Assert.Contains(port.ToString(), raised);
    }

    [Fact]
    public void PortFree_NoError()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var service = new DcsExportService(NullLogger<DcsExportService>.Instance) { UdpPort = port };
        var raised = false;
        service.ListenerErrorOccurred += (_, _) => raised = true;

        service.Start();
        service.Stop();

        Assert.False(raised);
        Assert.Null(service.ListenerError);
    }
}
