using System.Diagnostics;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// Controlled <c>System.Runtime.CompilerServices.AsyncHelpers</c> members. The <c>Await</c> overloads that take a
/// configured awaitable are the runtime-async intrinsics a method compiled with runtime async calls at each
/// <c>await x.ConfigureAwait(...)</c>: the runtime suspends the calling method there and resumes it through its own
/// continuation machinery, not through the awaitable, so the continuation would bypass the simulation coordinator.
/// The rewriter redirects them here, where the configured awaitable has already been substituted, and each throws a
/// <see cref="SimulationApiException"/> naming the API instead. The <c>HandleAsyncEntryPoint</c> overloads, through
/// which an async <c>Main</c> returns, wait for its task through the controlled awaiter, which pumps the simulation
/// until the task completes.
/// </summary>
public static class ControlledAsyncHelpers
{
    private const string ApiName = "System.Runtime.CompilerServices.AsyncHelpers.Await";

    private const string EntryPointApiName = "System.Runtime.CompilerServices.AsyncHelpers.HandleAsyncEntryPoint";

    private const string Reason =
        "a runtime-async method suspends at this intrinsic and resumes through the runtime's own continuation " +
        "machinery, which bypasses the simulation coordinator; compile the method without runtime async so the C# " +
        "compiler emits an async state machine, which Clockwork controls.";

    /// <summary>Rejected controlled <c>AsyncHelpers.Await(ConfiguredTaskAwaitable)</c>.</summary>
    /// <param name="configuredAwaitable">The configured awaitable.</param>
    public static void Await(ControlledConfiguredTaskAwaitable configuredAwaitable) =>
        throw Rejected(ApiName + "(System.Runtime.CompilerServices.ConfiguredTaskAwaitable)");

    /// <summary>Rejected controlled <c>AsyncHelpers.Await&lt;T&gt;(ConfiguredTaskAwaitable&lt;T&gt;)</c>.</summary>
    /// <typeparam name="T">The awaited result type.</typeparam>
    /// <param name="configuredAwaitable">The configured awaitable.</param>
    /// <returns>Never returns.</returns>
    public static T Await<T>(ControlledConfiguredTaskAwaitable<T> configuredAwaitable) =>
        throw Rejected(ApiName + "<T>(System.Runtime.CompilerServices.ConfiguredTaskAwaitable<T>)");

    /// <summary>Rejected controlled <c>AsyncHelpers.Await(ConfiguredValueTaskAwaitable)</c>.</summary>
    /// <param name="configuredAwaitable">The configured awaitable.</param>
    public static void Await(ControlledConfiguredValueTaskAwaitable configuredAwaitable) =>
        throw Rejected(ApiName + "(System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable)");

    /// <summary>Rejected controlled <c>AsyncHelpers.Await&lt;T&gt;(ConfiguredValueTaskAwaitable&lt;T&gt;)</c>.</summary>
    /// <typeparam name="T">The awaited result type.</typeparam>
    /// <param name="configuredAwaitable">The configured awaitable.</param>
    /// <returns>Never returns.</returns>
    public static T Await<T>(ControlledConfiguredValueTaskAwaitable<T> configuredAwaitable) =>
        throw Rejected(ApiName + "<T>(System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<T>)");

    /// <summary>Controlled <c>AsyncHelpers.HandleAsyncEntryPoint(Task)</c>.</summary>
    /// <param name="task">The entry point's task.</param>
    [StackTraceHidden]
    public static void HandleAsyncEntryPoint(Task task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EntryPointApiName);
        new ControlledTaskAwaiter(task).GetResult();
    }

    /// <summary>Controlled <c>AsyncHelpers.HandleAsyncEntryPoint(Task&lt;int&gt;)</c>.</summary>
    /// <param name="task">The entry point's task.</param>
    /// <returns>The entry point's exit code.</returns>
    [StackTraceHidden]
    public static int HandleAsyncEntryPoint(Task<int> task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(EntryPointApiName);
        return new ControlledTaskAwaiter<int>(task).GetResult();
    }

    private static SimulationApiException Rejected(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        return new SimulationApiException(SimulationApiCategory.Task, apiName, Reason);
    }
}
