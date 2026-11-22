using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// Controlled <see cref="global::System.Threading.Tasks.TaskAsyncEnumerableExtensions"/>. The rewriter redirects the
/// <c>ConfigureAwait</c> and <c>WithCancellation</c> extension calls here; each constructs the controlled configured
/// disposable or enumerable with the same arguments the BCL passes to its configured types, so <c>await using</c> and
/// <c>await foreach</c> resume through the coordinator. <c>ToBlockingEnumerable</c> pumps the simulation while it waits
/// for each step, as a controlled synchronous wait does, instead of blocking the simulation's logical thread.
/// </summary>
public static class ControlledTaskAsyncEnumerableExtensions
{
    private const string ApiName = "System.Threading.Tasks.TaskAsyncEnumerableExtensions";
    private const string ToBlockingEnumerableApiName = ApiName + ".ToBlockingEnumerable";

    /// <summary>
    /// Controlled <c>TaskAsyncEnumerableExtensions.ToBlockingEnumerable&lt;T&gt;(IAsyncEnumerable&lt;T&gt;, CancellationToken)</c>.
    /// </summary>
    /// <typeparam name="T">The type of the values being iterated.</typeparam>
    /// <param name="source">The source enumerable.</param>
    /// <param name="cancellationToken">The token passed to <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)"/>.</param>
    /// <returns>An enumerable that enumerates <paramref name="source"/>, waiting for each step.</returns>
    /// <remarks>
    /// As in the BCL, the source is not enumerated until the result is, each <c>MoveNext</c> calls
    /// <see cref="IAsyncEnumerator{T}.MoveNextAsync"/> exactly once, and the source enumerator is disposed when the
    /// enumeration completes, faults, or is disposed early. Where the BCL blocks the calling thread until a step
    /// completes, which would stop the simulation that completes it, this pumps ready simulation work until it does.
    /// </remarks>
    public static IEnumerable<T> ToBlockingEnumerable<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ToBlockingEnumerableApiName);
        return ToBlockingEnumerableIterator(source, cancellationToken);
    }

    // Mirrors the BCL iterator, draining the simulation in place of its blocking ManualResetEventSlim wait.
    private static IEnumerable<T> ToBlockingEnumerableIterator<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        // As in the BCL, a null source throws NullReferenceException when enumeration starts.
        IAsyncEnumerator<T> enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
#pragma warning disable CA2012 // Each value task is consumed exactly once, after it completes.
                ValueTask<bool> moveNext = enumerator.MoveNextAsync();
#pragma warning restore CA2012
                if (!moveNext.IsCompleted)
                {
                    SimulationTaskRuntime.DrainUntilCompleted(moveNext, ToBlockingEnumerableApiName);
                }

                if (!moveNext.Result)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            ValueTask dispose = enumerator.DisposeAsync();
            if (!dispose.IsCompleted)
            {
                SimulationTaskRuntime.DrainUntilCompleted(dispose, ToBlockingEnumerableApiName);
            }

            dispose.GetAwaiter().GetResult();
        }
    }

    /// <summary>Controlled <c>TaskAsyncEnumerableExtensions.ConfigureAwait(IAsyncDisposable, bool)</c>.</summary>
    /// <param name="source">The source async disposable.</param>
    /// <param name="continueOnCapturedContext">The requested continuation-context behaviour.</param>
    /// <returns>The configured async disposable.</returns>
    public static ControlledConfiguredAsyncDisposable ConfigureAwait(IAsyncDisposable source, bool continueOnCapturedContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName + ".ConfigureAwait");
        return new(source, continueOnCapturedContext);
    }

    /// <summary>Controlled <c>TaskAsyncEnumerableExtensions.ConfigureAwait&lt;T&gt;(IAsyncEnumerable&lt;T&gt;, bool)</c>.</summary>
    /// <typeparam name="T">The type of the values being iterated.</typeparam>
    /// <param name="source">The source enumerable.</param>
    /// <param name="continueOnCapturedContext">The requested continuation-context behaviour.</param>
    /// <returns>The configured enumerable, with no cancellation token.</returns>
    public static ControlledConfiguredCancelableAsyncEnumerable<T> ConfigureAwait<T>(
        IAsyncEnumerable<T> source,
        bool continueOnCapturedContext)
        where T : allows ref struct
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName + ".ConfigureAwait");
        return new(source, continueOnCapturedContext, cancellationToken: default);
    }

    /// <summary>Controlled <c>TaskAsyncEnumerableExtensions.WithCancellation&lt;T&gt;(IAsyncEnumerable&lt;T&gt;, CancellationToken)</c>.</summary>
    /// <typeparam name="T">The type of the values being iterated.</typeparam>
    /// <param name="source">The source enumerable.</param>
    /// <param name="cancellationToken">The token passed to <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)"/>.</param>
    /// <returns>The configured enumerable, continuing on the captured context as the BCL default does.</returns>
    public static ControlledConfiguredCancelableAsyncEnumerable<T> WithCancellation<T>(
        IAsyncEnumerable<T> source,
        CancellationToken cancellationToken)
        where T : allows ref struct
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName + ".WithCancellation");
        return new(source, continueOnCapturedContext: true, cancellationToken);
    }
}
