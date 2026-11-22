using System.Runtime.InteropServices;
using System.Threading;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// The controlled substitute for
/// <see cref="global::System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable{T}"/> - the value produced by
/// <c>source.ConfigureAwait(bool)</c> and <c>source.WithCancellation(CancellationToken)</c> and iterated by
/// <c>await foreach</c>. As in the BCL, <see cref="GetAsyncEnumerator"/> passes the configured token to
/// <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)"/>, where an async iterator combines it with
/// its <c>[EnumeratorCancellation]</c> parameter, and the enumerator configures each
/// <see cref="IAsyncEnumerator{T}.MoveNextAsync"/> and <see cref="IAsyncDisposable.DisposeAsync"/> value task. Here
/// they produce controlled configured awaitables, so every iteration and dispose await resumes through the
/// coordinator and treats <c>continueOnCapturedContext</c> exactly as <c>ValueTask.ConfigureAwait(bool)</c> does.
/// </summary>
/// <typeparam name="T">The type of the values being iterated.</typeparam>
[StructLayout(LayoutKind.Auto)]
public readonly struct ControlledConfiguredCancelableAsyncEnumerable<T>
    where T : allows ref struct
{
    private const string ApiName = "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1";
    private readonly IAsyncEnumerable<T> _enumerable;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _continueOnCapturedContext;

    internal ControlledConfiguredCancelableAsyncEnumerable(
        IAsyncEnumerable<T> enumerable,
        bool continueOnCapturedContext,
        CancellationToken cancellationToken)
    {
        _enumerable = enumerable;
        _continueOnCapturedContext = continueOnCapturedContext;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Configures how awaits on the tasks returned from the iteration are performed.</summary>
    /// <param name="continueOnCapturedContext">The requested continuation-context behaviour.</param>
    /// <returns>The configured enumerable, keeping this enumerable's cancellation token.</returns>
    public ControlledConfiguredCancelableAsyncEnumerable<T> ConfigureAwait(bool continueOnCapturedContext)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName + ".ConfigureAwait");
        return new(_enumerable, continueOnCapturedContext, _cancellationToken);
    }

    /// <summary>Sets the token passed to <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)"/>.</summary>
    /// <param name="cancellationToken">The token to use.</param>
    /// <returns>The configured enumerable, keeping this enumerable's continuation-context behaviour.</returns>
    public ControlledConfiguredCancelableAsyncEnumerable<T> WithCancellation(CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName + ".WithCancellation");
        return new(_enumerable, _continueOnCapturedContext, cancellationToken);
    }

    /// <summary>Returns an enumerator that iterates the source with the configured token and awaits.</summary>
    /// <returns>The configured enumerator.</returns>
    public Enumerator GetAsyncEnumerator()
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ApiName + ".GetAsyncEnumerator");

        // As in the BCL, a default instance throws NullReferenceException here rather than checking for null.
        return new(_enumerable.GetAsyncEnumerator(_cancellationToken), _continueOnCapturedContext);
    }

    /// <summary>
    /// The controlled substitute for
    /// <see cref="global::System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable{T}.Enumerator"/>.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    public readonly struct Enumerator
    {
        private const string EnumeratorApiName = ApiName + ".Enumerator";
        private readonly IAsyncEnumerator<T> _enumerator;
        private readonly bool _continueOnCapturedContext;

        internal Enumerator(IAsyncEnumerator<T> enumerator, bool continueOnCapturedContext)
        {
            _enumerator = enumerator;
            _continueOnCapturedContext = continueOnCapturedContext;
        }

        /// <summary>Gets the element at the current position of the enumerator.</summary>
        public T Current
        {
            get
            {
                SimulationRuntimeDispatch.RequireActiveSimulation(EnumeratorApiName + ".get_Current");
                return _enumerator.Current;
            }
        }

        /// <summary>Advances the enumerator to the next element.</summary>
        /// <returns>An awaitable whose result is <see langword="true"/> if the enumerator advanced.</returns>
        public ControlledConfiguredValueTaskAwaitable<bool> MoveNextAsync()
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(EnumeratorApiName + ".MoveNextAsync");
            return new(_enumerator.MoveNextAsync(), _continueOnCapturedContext);
        }

        /// <summary>Asynchronously disposes the enumerator.</summary>
        /// <returns>An awaitable for the enumerator's dispose operation.</returns>
        public ControlledConfiguredValueTaskAwaitable DisposeAsync()
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(EnumeratorApiName + ".DisposeAsync");
            return new(_enumerator.DisposeAsync(), _continueOnCapturedContext);
        }
    }
}
