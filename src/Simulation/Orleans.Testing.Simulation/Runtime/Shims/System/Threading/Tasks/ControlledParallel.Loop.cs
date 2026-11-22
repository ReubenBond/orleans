using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

public static partial class ControlledParallel
{
    private const string LoopApi = "System.Threading.Tasks.Parallel";

    /// <summary>
    /// The deterministic worker pool behind every controlled <see cref="Parallel"/> loop, whose contract is the Parallel
    /// loops section of docs/compatibility.md. Each worker takes an
    /// element, runs the body, and repeats until the source is exhausted or the loop's internal cancellation is
    /// requested, which a body fault or the caller's token triggers. A worker launches the next one after it takes its
    /// first element, until the degree of parallelism is exhausted, so at most that many bodies are in flight. The last
    /// worker to finish completes the loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An asynchronous loop queues its first worker, as the BCL's <c>ForEachAsync</c> does; a synchronous loop runs it on
    /// the caller, as the BCL's root replica does, so a loop with a degree of parallelism of 1 runs entirely on the caller,
    /// as the caller's strand (unless its scheduler is another node's, which runs every worker). Every other worker is a
    /// fresh controlled operation on a fresh strand under the loop's target snapshot. Every await uses a controlled
    /// awaiter, so all of the loop's work runs on the simulation's logical thread.
    /// </para>
    /// <para>
    /// Between iterations a worker passes through a scheduling point - the resumption after a body which suspended, or a
    /// yield after one which completed synchronously - where another worker could take the source first on a real
    /// machine, so the scheduling strategy chooses the interleaving of the workers' iterations. Every take synchronizes
    /// with the takes before it, as the BCL's lock or interlocked index does, and every body happens before the loop's
    /// completion.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The element type.</typeparam>
    private abstract class ParallelLoop<TItem>
    {
        private readonly string _apiName;
        private readonly SimulationExecutionSnapshot _target;
        private readonly ExecutionContext? _context;
        private readonly Func<TItem, CancellationToken, ValueTask> _body;
        private readonly TaskCompletionSource _completion = new();
        private readonly object _completionGate = new();
        private readonly object _sourceGate = new();
        private CancellationTokenRegistration _registration;
        private List<Exception>? _exceptions;
        private int _remainingDop;
        private int _activeWorkers;
        private bool _synchronous;

        protected ParallelLoop(
            string apiName,
            SimulationExecutionSnapshot target,
            int degreeOfParallelism,
            Func<TItem, CancellationToken, ValueTask> body,
            CancellationToken externalCancellationToken)
        {
            _apiName = apiName;
            _target = target;
            _context = ExecutionContext.Capture();
            _remainingDop = degreeOfParallelism;
            _body = body;
            ExternalCancellationToken = externalCancellationToken;
        }

        /// <summary>
        /// Gets the internal source that stops every worker at its next element. It holds no timer or link, so it has
        /// nothing to release, and a cancellation callback still running when the loop completes may cancel it.
        /// </summary>
        public CancellationTokenSource InternalCancellation { get; } = new();

        /// <summary>Gets the caller's token.</summary>
        public CancellationToken ExternalCancellationToken { get; }

        /// <summary>Gets a value indicating whether the caller's token was cancelled while the loop was registered on it.</summary>
        public bool ExternalCancellationObserved { get; private set; }

        /// <summary>Gets the faults recorded by the workers, in the order they occurred, or <see langword="null"/> if none.</summary>
        public IReadOnlyList<Exception>? Exceptions => _exceptions;

        /// <summary>
        /// Gets the loop's completion: cancelled if the caller's token is cancelled when the loop completes, otherwise
        /// faulted with <see cref="Exceptions"/> if any were recorded, otherwise successful (the BCL <c>ForEachAsync</c>
        /// outcome).
        /// </summary>
        public Task Completion => _completion.Task;

        /// <summary>Registers on the caller's token, once the source is in hand, and queues the first worker.</summary>
        public void Start()
        {
            Register();
            QueueWorkerIfDopAvailable();
        }

        /// <summary>
        /// Registers on the caller's token and runs the first worker on the caller until it stops taking elements,
        /// launching the others as it goes, in the loop's captured <see cref="ExecutionContext"/>, as the BCL runs its
        /// root replica inline as a task: what the bodies set there flows from one to the next but not back to the
        /// caller. A loop whose target is another node queues its first worker there instead, as the BCL queues its
        /// root replica to a scheduler which declines to run it inline. The loop's source is released by
        /// <see cref="DisposeSource"/> once the loop has completed.
        /// </summary>
        public void RunSynchronously()
        {
            _synchronous = true;
            Register();
            if (!Equals(_target.Node, SimulationExecutionContext.Current?.Node))
            {
                QueueWorkerIfDopAvailable();
                return;
            }

            Debug.Assert(_remainingDop > 0, "A loop runs at least one worker.");
            _remainingDop--;
            _activeWorkers++;
            SimulationTaskRuntime.RunWithCapturedExecutionContext(_context, RunCallerWorker);
        }

        /// <summary>
        /// Releases a synchronous loop's source once the loop has completed, as the BCL's synchronous loops do after their
        /// replicas finish, so a fault there propagates in place of the loop's outcome.
        /// </summary>
        public void DisposeSource()
        {
            Debug.Assert(_synchronous && Completion.IsCompleted, "Only a completed synchronous loop releases its source here.");
            ValueTask disposal = DisposeSourceAsync();
            Debug.Assert(disposal.IsCompleted, "A synchronous loop's source is released synchronously.");
            disposal.GetAwaiter().GetResult();
        }

        /// <summary>Records a fault and stops every worker at its next element.</summary>
        /// <param name="exception">The fault.</param>
        public void RecordException(Exception exception)
        {
            (_exceptions ??= []).Add(exception);
            try
            {
                SimulationCompletion.Cancel(InternalCancellation, LoopApi);
            }
            catch (AggregateException callbackFaults)
            {
                _exceptions.AddRange(callbackFaults.InnerExceptions);
            }
        }

        /// <summary>Creates the per-worker view of the source.</summary>
        /// <returns>The worker's cursor.</returns>
        protected abstract Cursor CreateCursor();

        /// <summary>
        /// Releases the source once every worker has finished: an asynchronous loop's last worker records a fault here
        /// among the loop's faults, while a synchronous loop's caller propagates it (see <see cref="DisposeSource"/>).
        /// </summary>
        /// <returns>A task that completes when the source is released.</returns>
        protected virtual ValueTask DisposeSourceAsync() => default;

        /// <summary>Consumes the edge from every earlier take, as acquiring the source's lock does.</summary>
        protected void EnterSource() => RaceSynchronization.Wait(_sourceGate);

        /// <summary>Publishes the edge to every later take, as releasing the source's lock does.</summary>
        protected void ExitSource() => RaceSynchronization.Signal(_sourceGate);

        /// <summary>Queues a continuation which a released lock hands to the worker that waits for it.</summary>
        /// <param name="continuation">The parked worker's continuation.</param>
        protected void Resume(Action continuation) =>
            SimulationTaskRuntime.QueueCapturedWork(_target, context: null, continuation, _apiName);

        private void Register()
        {
            Debug.Assert(_activeWorkers == 0 && _registration == default, "A loop starts once.");
            _registration = ExternalCancellationToken.UnsafeRegister(
                static state => ((ParallelLoop<TItem>)state!).OnExternalCancellation(),
                this);
        }

        private void OnExternalCancellation()
        {
            ExternalCancellationObserved = true;
            SimulationCompletion.Cancel(InternalCancellation, LoopApi);
        }

        private void QueueWorkerIfDopAvailable()
        {
            Debug.Assert(_remainingDop >= 0, "The loop launched more workers than its degree of parallelism.");
            if (_remainingDop == 0)
            {
                return;
            }

            _remainingDop--;
            _activeWorkers++;
            SimulationTaskRuntime.QueueCapturedWork(_target, _context, () => _ = RunWorkerAsync(), _apiName);
        }

        private async Task RunWorkerAsync()
        {
            Cursor? cursor = null;
            bool launchedNext = false;
            try
            {
                cursor = CreateCursor();
                while (!InternalCancellation.IsCancellationRequested)
                {
                    if (!await new CoordinatedValueTaskAwaitable<bool>(cursor.MoveNextAsync()))
                    {
                        break;
                    }

                    if (!launchedNext)
                    {
                        launchedNext = true;
                        QueueWorkerIfDopAvailable();
                    }

                    ValueTask body = _body(cursor.Current, InternalCancellation.Token);
                    bool bodyCompletedSynchronously = body.IsCompleted;
                    await new CoordinatedValueTaskAwaitable(body);

                    // A body which suspended resumed this worker through a scheduling point; one which completed
                    // synchronously needs one here, where another worker could take the source first.
                    if (bodyCompletedSynchronously)
                    {
                        await default(ControlledYieldAwaitable);
                    }
                }
            }
            catch (Exception exception)
            {
                RecordException(exception);
            }

            await FinishWorkerAsync(cursor);
        }

        // The caller's worker: RunWorkerAsync on the caller's thread, which waits for each value task by pumping the
        // coordinator and yields through it between iterations.
        private void RunCallerWorker()
        {
            Cursor? cursor = null;
            bool launchedNext = false;
            try
            {
                cursor = CreateCursor();
                while (!InternalCancellation.IsCancellationRequested)
                {
                    if (!new ControlledValueTaskAwaiter<bool>(cursor.MoveNextAsync()).GetResult())
                    {
                        break;
                    }

                    if (!launchedNext)
                    {
                        launchedNext = true;
                        QueueWorkerIfDopAvailable();
                    }

                    new ControlledValueTaskAwaiter(_body(cursor.Current, InternalCancellation.Token)).GetResult();
                    _ = SimulationTaskRuntime.RunOne(_apiName, CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                RecordException(exception);
            }

            _ = FinishWorkerAsync(cursor);
        }

        private async Task FinishWorkerAsync(Cursor? cursor)
        {
            try
            {
                cursor?.Release();
            }
            catch (Exception exception)
            {
                RecordException(exception);
            }

            RaceSynchronization.Signal(_completionGate);
            Debug.Assert(_activeWorkers > 0, "A worker finished which the loop did not launch.");
            if (--_activeWorkers == 0)
            {
                await CompleteLoopAsync();
            }
        }

        // Run by the last worker to finish: releases the loop's registration and an asynchronous loop's source, then
        // completes it.
        private async Task CompleteLoopAsync()
        {
            RaceSynchronization.Wait(_completionGate);
            try
            {
                // Unregister rather than Dispose, which would wait for a cancellation callback running on an operation
                // the coordinator has paused, blocking this thread for good.
                _registration.Unregister();
                if (!_synchronous)
                {
                    await new CoordinatedValueTaskAwaitable(DisposeSourceAsync());
                }
            }
            catch (Exception exception)
            {
                RecordException(exception);
            }

            SimulationCompletion.Run(
                (source: _completion, canceledBy: ExternalCancellationToken, exceptions: _exceptions),
                static completion =>
                {
                    if (completion.canceledBy.IsCancellationRequested)
                    {
                        completion.source.SetCanceled(completion.canceledBy);
                    }
                    else if (completion.exceptions is null)
                    {
                        completion.source.SetResult();
                    }
                    else
                    {
                        completion.source.SetException(completion.exceptions);
                    }
                },
                LoopApi);

            RaceSynchronization.Signal(_completion.Task);
        }

        /// <summary>A worker's view of the source: the next element it takes, and the resources it holds.</summary>
        protected abstract class Cursor
        {
            /// <summary>Gets or sets the element most recently taken.</summary>
            public TItem Current { get; protected set; } = default!;

            /// <summary>
            /// Takes the next element, or returns <see langword="false"/> when the source is exhausted; the worker checks
            /// for stopping before calling.
            /// </summary>
            /// <returns><see langword="true"/> if an element was taken.</returns>
            public abstract ValueTask<bool> MoveNextAsync();

            /// <summary>Releases what the worker holds once it stops taking elements.</summary>
            public virtual void Release()
            {
            }
        }
    }

    /// <summary>
    /// A loop whose workers share one source, taking its elements one at a time under an asynchronous FIFO lock as the
    /// BCL's <c>ForEachAsync</c> does, so a source which suspends (or pumps the simulation) inside <c>MoveNext</c> is
    /// never re-entered. A released lock passes straight to the next parked worker, whose continuation it queues, so a
    /// parked worker costs the coordinator nothing until then.
    /// </summary>
    /// <typeparam name="TItem">The element type.</typeparam>
    private abstract class SharedSourceLoop<TItem>(
        string apiName,
        SimulationExecutionSnapshot target,
        int degreeOfParallelism,
        Func<TItem, CancellationToken, ValueTask> body,
        CancellationToken externalCancellationToken)
        : ParallelLoop<TItem>(apiName, target, degreeOfParallelism, body, externalCancellationToken)
    {
        private readonly Queue<LockWaiter> _lockWaiters = new();
        private bool _lockHeld;

        /// <summary>Gets the source's current element.</summary>
        protected abstract TItem SourceCurrent { get; }

        /// <summary>Advances the shared source; called only while the lock is held.</summary>
        /// <returns><see langword="true"/> if the source produced an element.</returns>
        protected abstract ValueTask<bool> MoveNextSourceAsync();

        /// <inheritdoc />
        protected sealed override Cursor CreateCursor() => new SharedCursor(this);

        private LockAwaitable AcquireLock()
        {
            if (!_lockHeld)
            {
                _lockHeld = true;
                return new LockAwaitable(waiter: null);
            }

            var waiter = new LockWaiter(this);
            _lockWaiters.Enqueue(waiter);
            return new LockAwaitable(waiter);
        }

        private void ReleaseLock()
        {
            Debug.Assert(_lockHeld, "The source's lock was released without being held.");
            ExitSource();
            if (_lockWaiters.TryDequeue(out LockWaiter? next))
            {
                next.Grant();
                return;
            }

            _lockHeld = false;
        }

        // A worker parked on the lock: granted the lock by the worker which releases it, which resumes it.
        private sealed class LockWaiter(SharedSourceLoop<TItem> loop)
        {
            private Action? _continuation;

            public bool Granted { get; private set; }

            public void Park(Action continuation)
            {
                Debug.Assert(!Granted && _continuation is null, "A worker parks once, before the lock is granted.");
                _continuation = continuation;
            }

            public void Grant()
            {
                Granted = true;
                if (_continuation is { } continuation)
                {
                    loop.Resume(continuation);
                }
            }
        }

        private readonly struct LockAwaitable(LockWaiter? waiter) : ICriticalNotifyCompletion
        {
            public bool IsCompleted => waiter is null || waiter.Granted;

            public LockAwaitable GetAwaiter() => this;

            public void GetResult() => Debug.Assert(IsCompleted, "The lock was taken before it was granted.");

            public void OnCompleted(Action continuation) => waiter!.Park(continuation);

            public void UnsafeOnCompleted(Action continuation) => waiter!.Park(continuation);
        }

        private sealed class SharedCursor(SharedSourceLoop<TItem> loop) : Cursor
        {
            public override async ValueTask<bool> MoveNextAsync()
            {
                await loop.AcquireLock();
                loop.EnterSource();
                try
                {
                    // Acquiring the lock can suspend, so the loop may have stopped since the worker last checked.
                    if (loop.InternalCancellation.IsCancellationRequested
                        || !await new CoordinatedValueTaskAwaitable<bool>(loop.MoveNextSourceAsync()))
                    {
                        return false;
                    }

                    Current = loop.SourceCurrent;
                    return true;
                }
                finally
                {
                    loop.ReleaseLock();
                }
            }
        }
    }

    /// <summary>A loop over the integers in <c>[fromInclusive, toExclusive)</c>, taken in ascending order.</summary>
    /// <typeparam name="T">The integer type.</typeparam>
    private sealed class RangeLoop<T>(
        string apiName,
        SimulationExecutionSnapshot target,
        T fromInclusive,
        T toExclusive,
        int degreeOfParallelism,
        Func<T, CancellationToken, ValueTask> body,
        CancellationToken externalCancellationToken)
        : ParallelLoop<T>(apiName, target, degreeOfParallelism, body, externalCancellationToken)
        where T : IBinaryInteger<T>
    {
        private readonly T _toExclusive = toExclusive;
        private T _next = fromInclusive;

        protected override Cursor CreateCursor() => new RangeCursor(this);

        private sealed class RangeCursor(RangeLoop<T> loop) : Cursor
        {
            public override ValueTask<bool> MoveNextAsync()
            {
                loop.EnterSource();
                bool taken = loop._next < loop._toExclusive;
                if (taken)
                {
                    Current = loop._next;
                    loop._next++;
                }

                loop.ExitSource();
                return new ValueTask<bool>(taken);
            }
        }
    }

    /// <summary>A loop over an <see cref="IEnumerable{T}"/>'s single enumerator, which it disposes when the loop ends.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    private sealed class EnumerableLoop<T>(
        string apiName,
        SimulationExecutionSnapshot target,
        IEnumerable<T> source,
        int degreeOfParallelism,
        Func<T, CancellationToken, ValueTask> body,
        CancellationToken externalCancellationToken)
        : SharedSourceLoop<T>(apiName, target, degreeOfParallelism, body, externalCancellationToken)
    {
        private readonly IEnumerator<T> _enumerator =
            source.GetEnumerator() ?? throw new InvalidOperationException(NullEnumeratorMessage);

        protected override T SourceCurrent => _enumerator.Current;

        protected override ValueTask<bool> MoveNextSourceAsync() => new(_enumerator.MoveNext());

        protected override ValueTask DisposeSourceAsync()
        {
            _enumerator.Dispose();
            return default;
        }
    }

    /// <summary>
    /// A loop over an <see cref="IAsyncEnumerable{T}"/>'s single enumerator, obtained with the loop's internal
    /// cancellation token and disposed asynchronously when the loop ends.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    private sealed class AsyncEnumerableLoop<T> : SharedSourceLoop<T>
    {
        private readonly IAsyncEnumerator<T> _enumerator;

        public AsyncEnumerableLoop(
            string apiName,
            SimulationExecutionSnapshot target,
            IAsyncEnumerable<T> source,
            int degreeOfParallelism,
            Func<T, CancellationToken, ValueTask> body,
            CancellationToken externalCancellationToken)
            : base(apiName, target, degreeOfParallelism, body, externalCancellationToken)
        {
            _enumerator = source.GetAsyncEnumerator(InternalCancellation.Token)
                ?? throw new InvalidOperationException(NullEnumeratorMessage);
        }

        protected override T SourceCurrent => _enumerator.Current;

        protected override ValueTask<bool> MoveNextSourceAsync() => _enumerator.MoveNextAsync();

        protected override ValueTask DisposeSourceAsync() => _enumerator.DisposeAsync();
    }

    /// <summary>
    /// A loop over a <see cref="Partitioner{TSource}"/>'s dynamic partitions: each worker enumerates its own partition,
    /// which it disposes when it finishes, and the partitions' enumerable is disposed when the loop ends.
    /// </summary>
    /// <typeparam name="TPartition">The partition element type.</typeparam>
    private sealed class PartitionLoop<TPartition>(
        string apiName,
        SimulationExecutionSnapshot target,
        IEnumerable<TPartition> partitions,
        int degreeOfParallelism,
        Func<TPartition, CancellationToken, ValueTask> body,
        CancellationToken externalCancellationToken)
        : ParallelLoop<TPartition>(apiName, target, degreeOfParallelism, body, externalCancellationToken)
    {
        protected override Cursor CreateCursor() =>
            new PartitionCursor(this, partitions.GetEnumerator() ?? throw new InvalidOperationException(NullEnumeratorMessage));

        protected override ValueTask DisposeSourceAsync()
        {
            (partitions as IDisposable)?.Dispose();
            return default;
        }

        private sealed class PartitionCursor(PartitionLoop<TPartition> loop, IEnumerator<TPartition> partition) : Cursor
        {
            public override ValueTask<bool> MoveNextAsync()
            {
                // The partitions share their source, whose takes synchronize as the BCL partitioners' locks do.
                loop.EnterSource();
                try
                {
                    bool taken = partition.MoveNext();
                    if (taken)
                    {
                        Current = partition.Current;
                    }

                    return new ValueTask<bool>(taken);
                }
                finally
                {
                    loop.ExitSource();
                }
            }

            public override void Release() => partition.Dispose();
        }
    }
}
