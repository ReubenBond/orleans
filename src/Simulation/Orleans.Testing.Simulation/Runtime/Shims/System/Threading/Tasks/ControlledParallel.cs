using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// Static shims for the <see cref="Parallel"/> surface, which the rewriter redirects the supported static call sites to.
/// Every loop runs on a deterministic pool of controlled workers on the simulation coordinator, with the BCL's
/// parallelism limit, cancellation, and exception aggregation; the contract is the Parallel loops section of
/// docs/compatibility.md. The overloads whose body receives a <see cref="ParallelLoopState"/> are rejected at the call
/// site (see <see cref="RejectUnsupported"/>).
/// </summary>
public static partial class ControlledParallel
{
    /// <summary>
    /// Gets the active simulation's processor count, which sizes a loop where the BCL sizes it by
    /// <see cref="Environment.ProcessorCount"/>: every loop without <see cref="ParallelOptions"/>, or with
    /// <see cref="ParallelOptions.MaxDegreeOfParallelism"/> of -1, runs this many workers, on every host. It is the
    /// value the program reads as <see cref="Environment.ProcessorCount"/> under simulation (see
    /// <see cref="ControlledEnvironment.GetProcessorCount"/>), so both have one source.
    /// </summary>
    internal static int SimulatedProcessorCount => ControlledEnvironment.GetProcessorCount();
    private const string InvokeApi = "System.Threading.Tasks.Parallel.Invoke";
    private const string ForApi = "System.Threading.Tasks.Parallel.For";
    private const string ForEachApi = "System.Threading.Tasks.Parallel.ForEach";
    private const string ForAsyncApi = "System.Threading.Tasks.Parallel.ForAsync";
    private const string ForEachAsyncApi = "System.Threading.Tasks.Parallel.ForEachAsync";

    // The BCL's messages for the same failures.
    private const string NullActionMessage = "One of the actions was null.";
    private const string PartitionerNotDynamicMessage = "The Partitioner must support dynamic partitioning.";
    private const string PartitionerReturnedNullMessage = "The Partitioner returned a null partitioner source.";
    private const string NullEnumeratorMessage = "The source returned a null enumerator.";

    private static readonly ParallelOptions DefaultOptions = new();

    // A completed ParallelLoopResult (IsCompleted = true, LowestBreakIteration = null). The struct has no public
    // constructor; the BCL returns this value from a loop over an empty range without queuing any work.
    private static readonly ParallelLoopResult CompletedResult = Parallel.For(0, 0, static _ => { });


    /// <summary>Controlled <see cref="Parallel.Invoke(global::System.Action[])"/>.</summary>
    /// <param name="actions">The actions to execute.</param>
    public static void Invoke(params Action[] actions) => Invoke(DefaultOptions, actions);

    /// <summary>Controlled <see cref="Parallel.Invoke(ParallelOptions, global::System.Action[])"/>.</summary>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="actions">The actions to execute.</param>
    /// <remarks>
    /// The actions run on the synchronous loops' worker pool, the first worker on the caller. Every action runs unless the
    /// token is cancelled, which the workers check after each action, and the actions' faults are aggregated.
    /// </remarks>
    public static void Invoke(ParallelOptions parallelOptions, params Action[] actions)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(InvokeApi);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(actions);
        SimulationExecutionSnapshot target = ResolveTarget(parallelOptions, caller, InvokeApi);
        CancellationToken cancellationToken = parallelOptions.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        var actionsCopy = new Action[actions.Length];
        for (int i = 0; i < actionsCopy.Length; i++)
        {
            actionsCopy[i] = actions[i] ?? throw new ArgumentException(NullActionMessage);
        }

        if (actionsCopy.Length > 0)
        {
            InvokeCore(actionsCopy, target, DegreeOfParallelism(parallelOptions), cancellationToken);
        }
    }

    /// <summary>Controlled <see cref="Parallel.For(int, int, global::System.Action{int})"/>.</summary>
    /// <param name="fromInclusive">The start index, inclusive.</param>
    /// <param name="toExclusive">The end index, exclusive.</param>
    /// <param name="body">The per-iteration body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    public static ParallelLoopResult For(int fromInclusive, int toExclusive, Action<int> body) =>
        For(fromInclusive, toExclusive, DefaultOptions, body);

    /// <summary>Controlled <see cref="Parallel.For(int, int, ParallelOptions, global::System.Action{int})"/>.</summary>
    /// <param name="fromInclusive">The start index, inclusive.</param>
    /// <param name="toExclusive">The end index, exclusive.</param>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="body">The per-iteration body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    public static ParallelLoopResult For(int fromInclusive, int toExclusive, ParallelOptions parallelOptions, Action<int> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForApi);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);
        return ForCore(fromInclusive, toExclusive, parallelOptions, ResolveTarget(parallelOptions, caller, ForApi), body, ForApi);
    }

    /// <summary>Controlled <see cref="Parallel.For(long, long, global::System.Action{long})"/>.</summary>
    /// <param name="fromInclusive">The start index, inclusive.</param>
    /// <param name="toExclusive">The end index, exclusive.</param>
    /// <param name="body">The per-iteration body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    public static ParallelLoopResult For(long fromInclusive, long toExclusive, Action<long> body) =>
        For(fromInclusive, toExclusive, DefaultOptions, body);

    /// <summary>Controlled <see cref="Parallel.For(long, long, ParallelOptions, global::System.Action{long})"/>.</summary>
    /// <param name="fromInclusive">The start index, inclusive.</param>
    /// <param name="toExclusive">The end index, exclusive.</param>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="body">The per-iteration body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    public static ParallelLoopResult For(long fromInclusive, long toExclusive, ParallelOptions parallelOptions, Action<long> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForApi);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);
        return ForCore(fromInclusive, toExclusive, parallelOptions, ResolveTarget(parallelOptions, caller, ForApi), body, ForApi);
    }

    /// <summary>Controlled <see cref="Parallel.ForEach{TSource}(IEnumerable{TSource}, global::System.Action{TSource})"/>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The source sequence.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    public static ParallelLoopResult ForEach<TSource>(IEnumerable<TSource> source, Action<TSource> body) =>
        ForEach(source, DefaultOptions, body);

    /// <summary>Controlled <see cref="Parallel.ForEach{TSource}(IEnumerable{TSource}, ParallelOptions, global::System.Action{TSource})"/>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The source sequence.</param>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    /// <remarks>
    /// As in the BCL, an <see cref="IList{T}"/> (including an array) is indexed over <c>[0, Count)</c>, with
    /// <see cref="ICollection{T}.Count"/> read when the loop starts. Any other sequence is enumerated through its one
    /// enumerator, which the workers share, taking one element at a time, and which is disposed when the loop ends.
    /// </remarks>
    public static ParallelLoopResult ForEach<TSource>(IEnumerable<TSource> source, ParallelOptions parallelOptions, Action<TSource> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForEachApi);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);
        SimulationExecutionSnapshot target = ResolveTarget(parallelOptions, caller, ForEachApi);
        CancellationToken cancellationToken = parallelOptions.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (source is IList<TSource> list)
        {
            return ForCore(0, list.Count, parallelOptions, target, index => body(list[index]), ForEachApi);
        }

        RunToCompletion(
            new EnumerableLoop<TSource>(
                ForEachApi,
                target,
                source,
                DegreeOfParallelism(parallelOptions),
                ToAsyncBody(body),
                cancellationToken),
            ForEachApi);
        return CompletedResult;
    }

    /// <summary>Controlled <see cref="Parallel.ForEach{TSource}(Partitioner{TSource}, global::System.Action{TSource})"/>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The partitioner.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    public static ParallelLoopResult ForEach<TSource>(Partitioner<TSource> source, Action<TSource> body) =>
        ForEach(source, DefaultOptions, body);

    /// <summary>Controlled <see cref="Parallel.ForEach{TSource}(Partitioner{TSource}, ParallelOptions, global::System.Action{TSource})"/>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The partitioner.</param>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A completed <see cref="ParallelLoopResult"/>.</returns>
    /// <remarks>
    /// As in the BCL, the partitioner must support dynamic partitions; each worker enumerates its own partition (an
    /// <see cref="OrderablePartitioner{TSource}"/>'s orderable partitions, whose values the body receives), disposing it
    /// when it finishes, and the partitions' enumerable is disposed when the loop ends.
    /// </remarks>
    public static ParallelLoopResult ForEach<TSource>(Partitioner<TSource> source, ParallelOptions parallelOptions, Action<TSource> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForEachApi);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);
        SimulationExecutionSnapshot target = ResolveTarget(parallelOptions, caller, ForEachApi);
        if (!source.SupportsDynamicPartitions)
        {
            throw new InvalidOperationException(PartitionerNotDynamicMessage);
        }

        parallelOptions.CancellationToken.ThrowIfCancellationRequested();
        if (source is OrderablePartitioner<TSource> orderable)
        {
            RunPartitions(orderable.GetOrderableDynamicPartitions(), parallelOptions, target, pair => body(pair.Value));
        }
        else
        {
            RunPartitions(source.GetDynamicPartitions(), parallelOptions, target, body);
        }

        return CompletedResult;
    }

    /// <summary>Controlled <c>Parallel.ForAsync&lt;T&gt;(T, T, Func&lt;T, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="T">The integer type.</typeparam>
    /// <param name="fromInclusive">The start index, inclusive.</param>
    /// <param name="toExclusive">The end index, exclusive.</param>
    /// <param name="body">The per-iteration body.</param>
    /// <returns>A task that completes when every iteration has completed.</returns>
    public static Task ForAsync<T>(T fromInclusive, T toExclusive, Func<T, CancellationToken, ValueTask> body)
        where T : notnull, IBinaryInteger<T> =>
        ForAsync(fromInclusive, toExclusive, CancellationToken.None, body);

    // The shims mirror the BCL signatures, which take the CancellationToken before the body.
#pragma warning disable CA1068
    /// <summary>Controlled <c>Parallel.ForAsync&lt;T&gt;(T, T, CancellationToken, Func&lt;T, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="T">The integer type.</typeparam>
    /// <param name="fromInclusive">The start index, inclusive.</param>
    /// <param name="toExclusive">The end index, exclusive.</param>
    /// <param name="cancellationToken">The token that cancels the loop.</param>
    /// <param name="body">The per-iteration body.</param>
    /// <returns>A task that completes when every iteration has completed.</returns>
    public static Task ForAsync<T>(
        T fromInclusive,
        T toExclusive,
        CancellationToken cancellationToken,
        Func<T, CancellationToken, ValueTask> body)
        where T : notnull, IBinaryInteger<T>
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForAsyncApi);
        ArgumentNullException.ThrowIfNull(fromInclusive);
        ArgumentNullException.ThrowIfNull(toExclusive);
        ArgumentNullException.ThrowIfNull(body);
        return ForAsyncCore(fromInclusive, toExclusive, caller, SimulatedProcessorCount, body, cancellationToken);
    }

    /// <summary>Controlled <c>Parallel.ForEachAsync&lt;TSource&gt;(IEnumerable&lt;TSource&gt;, CancellationToken, Func&lt;TSource, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The source sequence.</param>
    /// <param name="cancellationToken">The token that cancels the loop.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A task that completes when every element has been processed.</returns>
    public static Task ForEachAsync<TSource>(
        IEnumerable<TSource> source,
        CancellationToken cancellationToken,
        Func<TSource, CancellationToken, ValueTask> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForEachAsyncApi);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(body);
        return StartAsyncLoop(
            () => new EnumerableLoop<TSource>(ForEachAsyncApi, caller, source, SimulatedProcessorCount, body, cancellationToken),
            cancellationToken);
    }

    /// <summary>Controlled <c>Parallel.ForEachAsync&lt;TSource&gt;(IAsyncEnumerable&lt;TSource&gt;, CancellationToken, Func&lt;TSource, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The asynchronous source sequence.</param>
    /// <param name="cancellationToken">The token that cancels the loop.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A task that completes when every element has been processed.</returns>
    public static Task ForEachAsync<TSource>(
        IAsyncEnumerable<TSource> source,
        CancellationToken cancellationToken,
        Func<TSource, CancellationToken, ValueTask> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForEachAsyncApi);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(body);
        return StartAsyncLoop(
            () => new AsyncEnumerableLoop<TSource>(ForEachAsyncApi, caller, source, SimulatedProcessorCount, body, cancellationToken),
            cancellationToken);
    }
#pragma warning restore CA1068

    /// <summary>Controlled <c>Parallel.ForAsync&lt;T&gt;(T, T, ParallelOptions, Func&lt;T, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="T">The integer type.</typeparam>
    /// <param name="fromInclusive">The start index, inclusive.</param>
    /// <param name="toExclusive">The end index, exclusive.</param>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="body">The per-iteration body.</param>
    /// <returns>A task that completes when every iteration has completed.</returns>
    public static Task ForAsync<T>(
        T fromInclusive,
        T toExclusive,
        ParallelOptions parallelOptions,
        Func<T, CancellationToken, ValueTask> body)
        where T : notnull, IBinaryInteger<T>
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForAsyncApi);
        ArgumentNullException.ThrowIfNull(fromInclusive);
        ArgumentNullException.ThrowIfNull(toExclusive);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);
        return ForAsyncCore(
            fromInclusive,
            toExclusive,
            ResolveTarget(parallelOptions, caller, ForAsyncApi),
            DegreeOfParallelism(parallelOptions),
            body,
            parallelOptions.CancellationToken);
    }

    /// <summary>Controlled <c>Parallel.ForEachAsync&lt;TSource&gt;(IEnumerable&lt;TSource&gt;, Func&lt;TSource, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The source sequence.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A task that completes when every element has been processed.</returns>
    public static Task ForEachAsync<TSource>(IEnumerable<TSource> source, Func<TSource, CancellationToken, ValueTask> body) =>
        ForEachAsync(source, CancellationToken.None, body);

    /// <summary>Controlled <c>Parallel.ForEachAsync&lt;TSource&gt;(IEnumerable&lt;TSource&gt;, ParallelOptions, Func&lt;TSource, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The source sequence.</param>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A task that completes when every element has been processed.</returns>
    public static Task ForEachAsync<TSource>(
        IEnumerable<TSource> source,
        ParallelOptions parallelOptions,
        Func<TSource, CancellationToken, ValueTask> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForEachAsyncApi);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);
        SimulationExecutionSnapshot target = ResolveTarget(parallelOptions, caller, ForEachAsyncApi);
        int degreeOfParallelism = DegreeOfParallelism(parallelOptions);
        CancellationToken cancellationToken = parallelOptions.CancellationToken;
        return StartAsyncLoop(
            () => new EnumerableLoop<TSource>(ForEachAsyncApi, target, source, degreeOfParallelism, body, cancellationToken),
            cancellationToken);
    }

    /// <summary>Controlled <c>Parallel.ForEachAsync&lt;TSource&gt;(IAsyncEnumerable&lt;TSource&gt;, Func&lt;TSource, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The asynchronous source sequence.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A task that completes when every element has been processed.</returns>
    public static Task ForEachAsync<TSource>(IAsyncEnumerable<TSource> source, Func<TSource, CancellationToken, ValueTask> body) =>
        ForEachAsync(source, CancellationToken.None, body);

    /// <summary>Controlled <c>Parallel.ForEachAsync&lt;TSource&gt;(IAsyncEnumerable&lt;TSource&gt;, ParallelOptions, Func&lt;TSource, CancellationToken, ValueTask&gt;)</c>.</summary>
    /// <typeparam name="TSource">The element type.</typeparam>
    /// <param name="source">The asynchronous source sequence.</param>
    /// <param name="parallelOptions">The options.</param>
    /// <param name="body">The per-element body.</param>
    /// <returns>A task that completes when every element has been processed.</returns>
    public static Task ForEachAsync<TSource>(
        IAsyncEnumerable<TSource> source,
        ParallelOptions parallelOptions,
        Func<TSource, CancellationToken, ValueTask> body)
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(ForEachAsyncApi);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parallelOptions);
        ArgumentNullException.ThrowIfNull(body);
        SimulationExecutionSnapshot target = ResolveTarget(parallelOptions, caller, ForEachAsyncApi);
        int degreeOfParallelism = DegreeOfParallelism(parallelOptions);
        CancellationToken cancellationToken = parallelOptions.CancellationToken;
        return StartAsyncLoop(
            () => new AsyncEnumerableLoop<TSource>(ForEachAsyncApi, target, source, degreeOfParallelism, body, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Rejection injected before an unsupported <see cref="Parallel"/> call site: the overloads whose body receives a
    /// <see cref="ParallelLoopState"/>, including the thread-local (<c>TLocal</c>) overloads.
    /// </summary>
    /// <param name="apiName">The unsupported API, supplied by the rewriter.</param>
    public static void RejectUnsupported(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.Parallel,
            apiName,
            "its body receives a ParallelLoopState (break/stop, and the thread-local TLocal overloads), which has no " +
            "public constructor, so Clockwork cannot supply one; use an overload whose body takes only the element, " +
            "and stop the loop through ParallelOptions.CancellationToken.");
    }

    private static ParallelLoopResult ForCore<T>(
        T fromInclusive,
        T toExclusive,
        ParallelOptions parallelOptions,
        SimulationExecutionSnapshot target,
        Action<T> body,
        string apiName)
        where T : IBinaryInteger<T>
    {
        // As in the BCL, an empty range returns before the token is observed.
        if (fromInclusive >= toExclusive)
        {
            return CompletedResult;
        }

        CancellationToken cancellationToken = parallelOptions.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        RunToCompletion(
            new RangeLoop<T>(
                apiName,
                target,
                fromInclusive,
                toExclusive,
                DegreeOfParallelism(parallelOptions),
                ToAsyncBody(body),
                cancellationToken),
            apiName);
        return CompletedResult;
    }

    private static void RunPartitions<TPartition>(
        IEnumerable<TPartition>? partitions,
        ParallelOptions parallelOptions,
        SimulationExecutionSnapshot target,
        Action<TPartition> body) =>
        RunToCompletion(
            new PartitionLoop<TPartition>(
                ForEachApi,
                target,
                partitions ?? throw new InvalidOperationException(PartitionerReturnedNullMessage),
                DegreeOfParallelism(parallelOptions),
                ToAsyncBody(body),
                parallelOptions.CancellationToken),
            ForEachApi);

    private static Task ForAsyncCore<T>(
        T fromInclusive,
        T toExclusive,
        SimulationExecutionSnapshot target,
        int degreeOfParallelism,
        Func<T, CancellationToken, ValueTask> body,
        CancellationToken cancellationToken)
        where T : notnull, IBinaryInteger<T>
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (fromInclusive >= toExclusive)
        {
            return Task.CompletedTask;
        }

        return StartAsyncLoop(
            () => new RangeLoop<T>(ForAsyncApi, target, fromInclusive, toExclusive, degreeOfParallelism, body, cancellationToken),
            cancellationToken);
    }

    // Starts an asynchronous loop, as the BCL's ForEachAsync does: a token already cancelled yields a cancelled task,
    // and a source which fails to produce its enumerator yields a faulted task rather than a synchronous throw.
    private static Task StartAsyncLoop<TItem>(Func<ParallelLoop<TItem>> create, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ParallelLoop<TItem> loop;
        try
        {
            loop = create();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }

        loop.Start();
        return loop.Completion;
    }

    // Runs the actions on a range loop over their indices; each action's fault leaves the loop running, and the token
    // check after each action stops it. The loop records that cancellation after every action fault, since the bodies
    // run synchronously.
    private static void InvokeCore(
        Action[] actions,
        SimulationExecutionSnapshot target,
        int degreeOfParallelism,
        CancellationToken cancellationToken)
    {
        List<Exception> exceptions = [];
        var loop = new RangeLoop<int>(
            InvokeApi,
            target,
            0,
            actions.Length,
            degreeOfParallelism,
            (index, _) =>
            {
                try
                {
                    actions[index]();
                }
                catch (Exception exception)
                {
                    exceptions.Add(exception);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return default;
            },
            CancellationToken.None);
        loop.RunSynchronously();
        WaitForCompletion(loop, InvokeApi);
        exceptions.AddRange(loop.Exceptions ?? []);
        if (exceptions.Count > 0)
        {
            ThrowSingleCancellationExceptionOrOtherException(exceptions, new AggregateException(exceptions), cancellationToken);
        }
    }

    // Runs a synchronous loop to completion, its first worker on the caller, releases its source, and throws what the
    // BCL's synchronous loops throw: as there, a fault releasing the source propagates in place of the loop's outcome.
    private static void RunToCompletion<TItem>(ParallelLoop<TItem> loop, string apiName)
    {
        loop.RunSynchronously();
        WaitForCompletion(loop, apiName);
        loop.DisposeSource();
        CancellationToken cancellationToken = loop.ExternalCancellationToken;
        if (loop.Exceptions is { } exceptions)
        {
            ThrowSingleCancellationExceptionOrOtherException(exceptions, new AggregateException(exceptions), cancellationToken);
        }

        if (loop.ExternalCancellationObserved)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void WaitForCompletion<TItem>(ParallelLoop<TItem> loop, string apiName)
    {
        Task completion = loop.Completion;
        if (!completion.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntil(() => completion.IsCompleted, apiName, CancellationToken.None);
        }

        RaceSynchronization.Wait(completion);

        // A synchronous loop reports its outcome by throwing, so its completion's fault is observed here.
        _ = completion.Exception;
    }

    // As the BCL: if every fault is an OperationCanceledException for the cancelled token, throw the first of them;
    // otherwise throw the other exception.
    [global::System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowSingleCancellationExceptionOrOtherException(
        IReadOnlyCollection<Exception> exceptions,
        Exception otherException,
        CancellationToken cancellationToken) =>
        ExceptionDispatchInfo.Throw(ReduceToSingleCancellationException(exceptions, cancellationToken) ?? otherException);

    private static OperationCanceledException? ReduceToSingleCancellationException(
        IReadOnlyCollection<Exception> exceptions,
        CancellationToken cancellationToken)
    {
        if (exceptions.Count == 0 || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        OperationCanceledException? first = null;
        foreach (Exception exception in exceptions)
        {
            if (exception is not OperationCanceledException cancellation || !cancellationToken.Equals(cancellation.CancellationToken))
            {
                return null;
            }

            first ??= cancellation;
        }

        return first;
    }

    // The snapshot a loop's workers run under: the options' scheduler resolved by the one scheduler policy, or the
    // caller's when it names none, whatever TaskScheduler.Current is.
    private static SimulationExecutionSnapshot ResolveTarget(
        ParallelOptions parallelOptions,
        SimulationExecutionSnapshot caller,
        string apiName) =>
        parallelOptions.TaskScheduler is { } scheduler
            ? ControlledTaskScheduler.ResolveTarget(scheduler, caller, SimulationApiCategory.Parallel, apiName)
            : caller;

    // The options' limit, or the simulated processor count when it is -1.
    private static int DegreeOfParallelism(ParallelOptions parallelOptions) =>
        parallelOptions.MaxDegreeOfParallelism == -1 ? SimulatedProcessorCount : parallelOptions.MaxDegreeOfParallelism;

    private static Func<TItem, CancellationToken, ValueTask> ToAsyncBody<TItem>(Action<TItem> body) =>
        (item, _) =>
        {
            body(item);
            return default;
        };
}
