using GPSTracker.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GPSTracker.FakeDeviceGateway;

internal class Program
{

    private static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .UseOrleansClient((ctx, clientBuilder) => clientBuilder.UseLocalhostClustering())
            .UseConsoleLifetime()
            .Build();

        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var client = host.Services.GetRequiredService<IClusterClient>();

        await LoadDriver.DriveLoad(client, 25, lifetime.ApplicationStopping);
        await host.StopAsync();
    }
}
