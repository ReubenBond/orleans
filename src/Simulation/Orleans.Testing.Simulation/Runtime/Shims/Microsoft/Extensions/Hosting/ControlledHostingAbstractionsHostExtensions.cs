using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Shims.System.Threading;
using Orleans.Testing.Simulation.Shims.System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Testing.Simulation.Shims.Microsoft.Extensions.Hosting;

/// <summary>
/// Controlled <see cref="HostingAbstractionsHostExtensions"/>. The BCL's members await the host's work in framework code,
/// whose continuations leave the logical thread where the simulation's synchronization context is current; block the
/// calling thread on that work (<c>Start</c>, <c>Run</c>, <c>WaitForShutdown</c>); or time out on a real timer
/// (<c>StopAsync(IHost, TimeSpan)</c>). These shims run the BCL's steps in the same order: they await through the
/// coordinator, wait with the controlled wait, which pumps the coordinator so the host's work runs while the caller
/// waits, and time out with a token a virtual timer cancels. The outcomes and exceptions are the BCL's.
/// </summary>
public static class ControlledHostingAbstractionsHostExtensions
{
    private const string Api = "Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions";

    /// <summary>Controlled <c>HostingAbstractionsHostExtensions.Start(IHost)</c>.</summary>
    /// <param name="host">The host to start.</param>
    public static void Start(IHost host)
    {
        const string api = Api + ".Start";
        SimulationRuntimeDispatch.RequireActiveSimulation(api);
        WaitFor(host.StartAsync(), api);
    }

    /// <summary>Controlled <c>HostingAbstractionsHostExtensions.Run(IHost)</c>.</summary>
    /// <param name="host">The host to run.</param>
    public static void Run(IHost host)
    {
        const string api = Api + ".Run";
        SimulationRuntimeDispatch.RequireActiveSimulation(api);
        WaitFor(RunAsyncCore(host, CancellationToken.None), api);
    }

    /// <summary>Controlled <c>HostingAbstractionsHostExtensions.RunAsync(IHost, CancellationToken)</c>.</summary>
    /// <param name="host">The host to run, which is disposed once it stops.</param>
    /// <param name="token">The token which triggers shutdown.</param>
    /// <returns>A task which completes once the host has stopped and been disposed.</returns>
    public static Task RunAsync(IHost host, CancellationToken token)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(Api + ".RunAsync");
        return RunAsyncCore(host, token);
    }

    /// <summary>Controlled <c>HostingAbstractionsHostExtensions.WaitForShutdown(IHost)</c>.</summary>
    /// <param name="host">The running host.</param>
    public static void WaitForShutdown(IHost host)
    {
        const string api = Api + ".WaitForShutdown";
        SimulationRuntimeDispatch.RequireActiveSimulation(api);
        WaitFor(WaitForShutdownAsyncCore(host, CancellationToken.None), api);
    }

    /// <summary>Controlled <c>HostingAbstractionsHostExtensions.WaitForShutdownAsync(IHost, CancellationToken)</c>.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="token">The token which triggers shutdown.</param>
    /// <returns>A task which completes once shutdown is triggered and the host has stopped.</returns>
    public static Task WaitForShutdownAsync(IHost host, CancellationToken token)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(Api + ".WaitForShutdownAsync");
        return WaitForShutdownAsyncCore(host, token);
    }

    /// <summary>Controlled <c>HostingAbstractionsHostExtensions.StopAsync(IHost, TimeSpan)</c>.</summary>
    /// <param name="host">The host to stop.</param>
    /// <param name="timeout">The time after which the stop token is cancelled, in simulated time.</param>
    /// <returns>A task which completes when the host has stopped.</returns>
    public static Task StopAsync(IHost host, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(Api + ".StopAsync");
        return StopAsyncCore(host, timeout);
    }

    // The async cores are async methods, as the BCL's are, so a null host or an invalid timeout faults the returned task.
    private static async Task RunAsyncCore(IHost host, CancellationToken token)
    {
        try
        {
            await Coordinated(host.StartAsync(token));
            await Coordinated(WaitForShutdownAsyncCore(host, token));
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await new CoordinatedValueTaskAwaitable(asyncDisposable.DisposeAsync());
            }
            else
            {
                host.Dispose();
            }
        }
    }

    private static async Task WaitForShutdownAsyncCore(IHost host, CancellationToken token)
    {
        IHostApplicationLifetime applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using CancellationTokenRegistration registration = token.Register(
            static state => ((IHostApplicationLifetime)state!).StopApplication(),
            applicationLifetime);
        try
        {
            await Coordinated(ControlledTask.Delay(Timeout.InfiniteTimeSpan, applicationLifetime.ApplicationStopping));
        }
        catch (OperationCanceledException)
        {
            // Shutdown was triggered.
        }

        // As in the BCL, the stop does not take the token, which would make it abortive.
        await Coordinated(host.StopAsync(CancellationToken.None));
    }

    private static async Task StopAsyncCore(IHost host, TimeSpan timeout)
    {
        CancellationTokenSource source = ControlledCancellationTokenSource.Create(timeout);
        try
        {
            await Coordinated(host.StopAsync(source.Token));
        }
        finally
        {
            ControlledCancellationTokenSource.Dispose(source);
        }
    }

    /// <summary>Awaits <paramref name="task"/> through the coordinator.</summary>
    internal static CoordinatedValueTaskAwaitable Coordinated(Task task) => new(new ValueTask(task));

    /// <summary>Waits for <paramref name="task"/> with the controlled wait, then observes its outcome as the BCL's blocking wait does.</summary>
    internal static void WaitFor(Task task, string api)
    {
        if (!task.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntilCompleted(task, api, CancellationToken.None);
        }

        RaceSynchronization.Wait(task);
        task.GetAwaiter().GetResult();
    }
}
