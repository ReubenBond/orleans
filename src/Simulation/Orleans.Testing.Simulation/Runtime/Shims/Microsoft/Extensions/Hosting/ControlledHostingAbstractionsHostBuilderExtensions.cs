using Orleans.Testing.Simulation.Runtime.Shims;
using Microsoft.Extensions.Hosting;

namespace Orleans.Testing.Simulation.Shims.Microsoft.Extensions.Hosting;

/// <summary>
/// Controlled <see cref="HostingAbstractionsHostBuilderExtensions"/>. The BCL's <c>StartAsync</c> builds the host and
/// awaits its start in framework code, whose continuation leaves the logical thread where the simulation's
/// synchronization context is current, and its <c>Start</c> blocks the calling thread on that. These shims build the
/// host and start it as the BCL does, awaiting the start through the coordinator or waiting for it with the controlled
/// wait, so the host's work runs while the caller waits. The outcomes and exceptions are the BCL's.
/// </summary>
public static class ControlledHostingAbstractionsHostBuilderExtensions
{
    private const string Api = "Microsoft.Extensions.Hosting.HostingAbstractionsHostBuilderExtensions";

    /// <summary>Controlled <c>HostingAbstractionsHostBuilderExtensions.Start(IHostBuilder)</c>.</summary>
    /// <param name="hostBuilder">The builder of the host to start.</param>
    /// <returns>The started host.</returns>
    public static IHost Start(IHostBuilder hostBuilder)
    {
        const string api = Api + ".Start";
        SimulationRuntimeDispatch.RequireActiveSimulation(api);
        IHost host = hostBuilder.Build();
        ControlledHostingAbstractionsHostExtensions.WaitFor(host.StartAsync(), api);
        return host;
    }

    /// <summary>Controlled <c>HostingAbstractionsHostBuilderExtensions.StartAsync(IHostBuilder, CancellationToken)</c>.</summary>
    /// <param name="hostBuilder">The builder of the host to start.</param>
    /// <param name="cancellationToken">The token which cancels the start.</param>
    /// <returns>A task whose result is the started host.</returns>
    public static Task<IHost> StartAsync(IHostBuilder hostBuilder, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(Api + ".StartAsync");
        return StartAsyncCore(hostBuilder, cancellationToken);
    }

    // An async method, as the BCL's is, so a null builder or a failing build faults the returned task.
    private static async Task<IHost> StartAsyncCore(IHostBuilder hostBuilder, CancellationToken cancellationToken)
    {
        IHost host = hostBuilder.Build();
        await ControlledHostingAbstractionsHostExtensions.Coordinated(host.StartAsync(cancellationToken));
        return host;
    }
}
