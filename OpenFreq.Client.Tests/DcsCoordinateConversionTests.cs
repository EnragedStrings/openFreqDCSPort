using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FalconBmsDataService.Models;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Client.Models.Dcs;
using OpenFreq.Client.Services;

namespace OpenFreq.Client.Tests;

/// <summary>
/// DcsExportService.ConvertLatLonToLocal over real loopback UDP, with this test playing DCS: the request goes out
/// on the LOS request port, the answer (OpenFreqDCS.lua buildCoordResponse) is cached per key and theater.
/// </summary>
public class DcsCoordinateConversionTests
{
    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static void Send(UdpClient from, int port, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        from.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<T?> PollAsync<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (probe() is { } value) return value;
            await Task.Delay(20);
        }

        return null;
    }

    private static void SendExport(UdpClient dcs, int port, string theater) =>
        Send(dcs, port, $$"""{"schema":"openfreq.dcs.export","version":1,"theater":"{{theater}}"}""");

    [Fact]
    public async Task LatLon_IsRequestedFromDcs_AndTheAnswerIsCached()
    {
        using var fakeDcs = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        fakeDcs.Client.ReceiveTimeout = 2000;
        var losPort = ((IPEndPoint)fakeDcs.Client.LocalEndPoint!).Port;
        var udpPort = FreePort();
        using var service = new DcsExportService(NullLogger<DcsExportService>.Instance)
            { UdpPort = udpPort, LosRequestPort = losPort };
        service.Start();

        SendExport(fakeDcs, udpPort, "Caucasus");
        await PollAsync(() => service.State == ServiceState.Connected ? "yes" : null);
        Assert.Equal(ServiceState.Connected, service.State);

        Assert.Null(service.ConvertLatLonToLocal("jam:a", 42.5, 41.5, null, 12));

        IPEndPoint? remote = null;
        var request = JsonDocument.Parse(fakeDcs.Receive(ref remote)).RootElement;
        Assert.Equal("openfreq.dcs.coord.request", request.GetProperty("schema").GetString());
        var point = request.GetProperty("points")[0];
        Assert.Equal("jam:a", point.GetProperty("id").GetString());
        Assert.Equal(42.5, point.GetProperty("lat").GetDouble());
        Assert.Equal(12, point.GetProperty("agl").GetDouble());

        // a second call while the request is in flight doesn't re-send
        Assert.Null(service.ConvertLatLonToLocal("jam:a", 42.5, 41.5, null, 12));

        Send(fakeDcs, udpPort, $$"""
            {"schema":"openfreq.dcs.coord.response","version":1,"requestId":"{{request.GetProperty("requestId").GetString()}}",
             "available":true,"points":[{"id":"jam:a","x":-1000,"y":512,"z":2000}]}
            """);
        var local = await PollAsync(() => service.ConvertLatLonToLocal("jam:a", 42.5, 41.5, null, 12));

        Assert.NotNull(local);
        Assert.Equal(-1000, local.X);
        Assert.Equal(512, local.Y);
        Assert.Equal(2000, local.Z);

        // a different map: the cached conversion no longer applies
        SendExport(fakeDcs, udpPort, "Syria");
        await PollAsync(() => service.Theater == "Syria" ? "yes" : null);
        Assert.Null(service.ConvertLatLonToLocal("jam:a", 42.5, 41.5, null, 12));
        service.Stop();
    }
}
