using System.Runtime.InteropServices;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// The controlled substitute for <see cref="global::System.Runtime.CompilerServices.ConfiguredAsyncDisposable"/> - the
/// value produced by <c>asyncDisposable.ConfigureAwait(continueOnCapturedContext)</c> and disposed by
/// <c>await using</c>. As in the BCL, <see cref="DisposeAsync"/> configures the source's
/// <see cref="global::System.IAsyncDisposable.DisposeAsync"/> value task; here it produces a
/// <see cref="ControlledConfiguredValueTaskAwaitable"/>, so the dispose await resumes through the coordinator and
/// treats <c>continueOnCapturedContext</c> exactly as <c>ValueTask.ConfigureAwait(bool)</c> does.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ControlledConfiguredAsyncDisposable
{
    private const string ApiName = "System.Runtime.CompilerServices.ConfiguredAsyncDisposable.DisposeAsync";
    private readonly IAsyncDisposable _source;
    private readonly bool _continueOnCapturedContext;

    internal ControlledConfiguredAsyncDisposable(IAsyncDisposable source, bool continueOnCapturedContext)
    {
        _source = source;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    /// <summary>Asynchronously disposes the source.</summary>
    /// <returns>An awaitable for the source's dispose operation.</returns>
    public ControlledConfiguredValueTaskAwaitable DisposeAsync()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName);

        // As in the BCL, a default instance throws NullReferenceException here rather than checking for null.
        return new(_source.DisposeAsync(), _continueOnCapturedContext);
    }
}
