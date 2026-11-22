using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Replay;
using LoopOutcome = (System.Threading.Tasks.TaskStatus Status, string[] Faults, string[] Trace, int MaxInFlight);

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="Parallel"/> loops: every <c>ForEachAsync</c> and <c>ForAsync</c>
/// overload and the synchronous <c>For</c>, <c>ForEach</c> (over sequences, lists, and partitioners), and <c>Invoke</c>
/// run on the coordinator with their bodies awaiting virtual delays and a device read which a virtual timer completes.
/// Each loop keeps no more than its degree of parallelism of bodies in flight, under the default schedule and under
/// seeded random schedules; aggregates faults, observes cancellation, and disposes its source as the BCL does (the same
/// probes run uninstrumented against the BCL, gated so that the BCL's outcome is deterministic); replays a recorded
/// seed's interleaving exactly; and interleaves its workers differently under different seeds. A probe rewritten without
/// the asynchronous loop rules shows the escape the rules close: the BCL's loop runs the bodies on thread-pool threads.
/// </summary>
public sealed class ParallelLoopConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.ParallelLoopProbe";
    private const int AsyncElementCount = 10;
    private const int SyncElementCount = 6;
    private const int MaxDegreeOfParallelism = 3;

    // The simulated processor count a cluster uses unless configured otherwise, which sizes the loops without a limit.
    // Pinned as a literal: it shapes every recorded schedule, so a change must fail here.
    private const int SimulatedProcessorCount = 4;

    // The seeded random schedules the safety properties are checked under, beside the default schedule.
    private const int SafetySeeds = 4;

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Linq;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf
        {
            // Thread-safe, so the uninstrumented runs against the BCL record faithfully too.
            public sealed class Recorder
            {
                private readonly object _gate = new object();
                private readonly List<string> _trace = new List<string>();
                private int _inFlight;
                private int _maxInFlight;
                private CancellationToken _bodyToken;

                public int MaxInFlight { get { lock (_gate) return _maxInFlight; } }

                // The token the loop passed to its bodies.
                public CancellationToken BodyToken { get { lock (_gate) return _bodyToken; } }

                public string[] Trace { get { lock (_gate) return _trace.ToArray(); } }

                // A body which starts after its loop's token was cancelled is marked with '*'.
                public void Enter(long item, CancellationToken token = default)
                {
                    lock (_gate)
                    {
                        _bodyToken = token;
                        _inFlight++;
                        if (_inFlight > _maxInFlight) _maxInFlight = _inFlight;
                        _trace.Add("+" + item + (token.IsCancellationRequested ? "*" : ""));
                    }
                }

                public void Exit(long item)
                {
                    lock (_gate)
                    {
                        _inFlight--;
                        _trace.Add("-" + item);
                    }
                }

                public void Mark(string entry)
                {
                    lock (_gate) _trace.Add(entry);
                }
            }

            public static class Faults
            {
                // A fault as the tests compare it: its type, its message, and whether it is a cancellation by `token`.
                public static string Describe(Exception exception, CancellationToken token) =>
                    exception.GetType().Name + "(" + exception.Message + ")"
                    + (IsCancellationBy(exception, token) ? "@token" : "");

                public static string[] DescribeAll(Task loop, CancellationToken token) =>
                    ((IEnumerable<Exception>)loop.Exception?.InnerExceptions ?? Array.Empty<Exception>())
                        .Select(exception => Describe(exception, token))
                        .ToArray();

                // What a synchronous loop threw. `collapseCancellations` replaces the cancellations by `token` among an
                // aggregate's faults with one "+canceled" mark, for an Invoke whose actions' count of cancellations
                // depends on the BCL's thread timing.
                public static string DescribeThrown(
                    Exception thrown,
                    CancellationToken token,
                    bool sortInner = false,
                    bool collapseCancellations = false)
                {
                    if (thrown == null) return "none";
                    if (!(thrown is AggregateException aggregate)) return Describe(thrown, token);
                    IEnumerable<Exception> inner = aggregate.InnerExceptions;
                    bool canceled = collapseCancellations && inner.Any(exception => IsCancellationBy(exception, token));
                    if (collapseCancellations) inner = inner.Where(exception => !IsCancellationBy(exception, token));
                    IEnumerable<string> described = inner.Select(exception => Describe(exception, token));
                    if (sortInner) described = described.OrderBy(entry => entry, StringComparer.Ordinal);
                    return "AggregateException[" + string.Join(", ", described) + "]" + (canceled ? "+canceled" : "");
                }

                private static bool IsCancellationBy(Exception exception, CancellationToken token) =>
                    exception is OperationCanceledException canceled && canceled.CancellationToken == token;
            }

            public static class Device
            {
                // A read which a timer completes, as a device completion arriving from outside the caller.
                public static Task<long> ReadAsync(long key)
                {
                    var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Timer timer = null;
                    // Created disarmed and armed only after the assignment, so the callback always sees the timer.
                    timer = new Timer(
                        _ =>
                        {
                            timer.Dispose();
                            completion.TrySetResult(key * 10);
                        },
                        null,
                        Timeout.InfiniteTimeSpan,
                        Timeout.InfiniteTimeSpan);
                    timer.Change(TimeSpan.FromMilliseconds(3), Timeout.InfiniteTimeSpan);
                    return completion.Task;
                }
            }

            // A partitioner which is not orderable: every dynamic partition drains one shared queue.
            public sealed class QueuePartitioner : Partitioner<long>
            {
                private readonly ConcurrentQueue<long> _items;

                public QueuePartitioner(IEnumerable<long> items) => _items = new ConcurrentQueue<long>(items);

                public override bool SupportsDynamicPartitions => true;

                public override IList<IEnumerator<long>> GetPartitions(int partitionCount) => throw new NotSupportedException();

                public override IEnumerable<long> GetDynamicPartitions() => new Partitions(_items);

                private sealed class Partitions : IEnumerable<long>
                {
                    private readonly ConcurrentQueue<long> _items;

                    public Partitions(ConcurrentQueue<long> items) => _items = items;

                    public IEnumerator<long> GetEnumerator()
                    {
                        while (_items.TryDequeue(out long item)) yield return item;
                    }

                    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                }
            }

            // A source of [0, count) which counts its enumerators and their disposals, and which can throw from
            // MoveNext (at element 2) or from Dispose/DisposeAsync.
            public sealed class TrackedSource : IEnumerable<long>, IAsyncEnumerable<long>
            {
                private readonly int _count;
                private readonly bool _throwOnMoveNext;
                private readonly bool _throwOnDispose;
                public int Enumerators;
                public int Disposals;

                public TrackedSource(int count, string mode)
                {
                    _count = count;
                    _throwOnMoveNext = mode == "MoveNextFault";
                    _throwOnDispose = mode == "DisposeFault";
                }

                public IEnumerator<long> GetEnumerator()
                {
                    Interlocked.Increment(ref Enumerators);
                    return new Enumerator(this);
                }

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                public IAsyncEnumerator<long> GetAsyncEnumerator(CancellationToken cancellationToken = default)
                {
                    Interlocked.Increment(ref Enumerators);
                    return new Enumerator(this);
                }

                private sealed class Enumerator : IEnumerator<long>, IAsyncEnumerator<long>
                {
                    private readonly TrackedSource _source;
                    private int _next = -1;

                    public Enumerator(TrackedSource source) => _source = source;

                    public long Current => _next;

                    object IEnumerator.Current => Current;

                    public bool MoveNext()
                    {
                        _next++;
                        if (_source._throwOnMoveNext && _next == 2) throw new InvalidOperationException("move next");
                        return _next < _source._count;
                    }

                    public ValueTask<bool> MoveNextAsync()
                    {
                        try
                        {
                            return new ValueTask<bool>(MoveNext());
                        }
                        catch (Exception exception)
                        {
                            return ValueTask.FromException<bool>(exception);
                        }
                    }

                    public void Reset() => throw new NotSupportedException();

                    public void Dispose()
                    {
                        Interlocked.Increment(ref _source.Disposals);
                        if (_source._throwOnDispose) throw new InvalidOperationException("dispose");
                    }

                    public ValueTask DisposeAsync()
                    {
                        try
                        {
                            Dispose();
                            return default;
                        }
                        catch (Exception exception)
                        {
                            return ValueTask.FromException(exception);
                        }
                    }
                }
            }

            // A dynamic partitioner over [0, count) which counts the partitions it hands out, their disposals, and
            // the disposals of the partitions' enumerable.
            public sealed class TrackedPartitioner : Partitioner<long>
            {
                private readonly ConcurrentQueue<long> _items;
                public int Partitions;
                public int PartitionDisposals;
                public int EnumerableDisposals;

                public TrackedPartitioner(int count) => _items = new ConcurrentQueue<long>(Enumerable.Range(0, count).Select(i => (long)i));

                public override bool SupportsDynamicPartitions => true;

                public override IList<IEnumerator<long>> GetPartitions(int partitionCount) => throw new NotSupportedException();

                public override IEnumerable<long> GetDynamicPartitions() => new DynamicPartitions(this);

                private sealed class DynamicPartitions : IEnumerable<long>, IDisposable
                {
                    private readonly TrackedPartitioner _owner;

                    public DynamicPartitions(TrackedPartitioner owner) => _owner = owner;

                    public IEnumerator<long> GetEnumerator()
                    {
                        Interlocked.Increment(ref _owner.Partitions);
                        return new Partition(_owner);
                    }

                    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

                    public void Dispose() => Interlocked.Increment(ref _owner.EnumerableDisposals);
                }

                private sealed class Partition : IEnumerator<long>
                {
                    private readonly TrackedPartitioner _owner;
                    private long _current;

                    public Partition(TrackedPartitioner owner) => _owner = owner;

                    public long Current => _current;

                    object IEnumerator.Current => Current;

                    public bool MoveNext() => _owner._items.TryDequeue(out _current);

                    public void Reset() => throw new NotSupportedException();

                    public void Dispose() => Interlocked.Increment(ref _owner.PartitionDisposals);
                }
            }

            public static class ParallelLoopProbe
            {
                public static async ValueTask Body(long item, CancellationToken cancellationToken, Recorder recorder)
                {
                    recorder.Enter(item, cancellationToken);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
                        long value = await Device.ReadAsync(item);
                        await Task.Yield();
                        if (value != item * 10) throw new InvalidOperationException("bad read");
                    }
                    finally
                    {
                        recorder.Exit(item);
                    }
                }

                // An asynchronous source, each of whose elements arrives after a suspension, at no cost in virtual time.
                public static async IAsyncEnumerable<long> Stream(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    for (int i = 0; i < count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Yield();
                        yield return i;
                    }
                }

                public static Task Loop(
                    string overload,
                    int count,
                    ParallelOptions options,
                    Func<long, CancellationToken, ValueTask> body) =>
                    Loop(overload, Enumerable.Range(0, count).Select(i => (long)i), Stream(count), count, options, body);

                public static Task Loop(
                    string overload,
                    IEnumerable<long> items,
                    IAsyncEnumerable<long> stream,
                    int count,
                    ParallelOptions options,
                    Func<long, CancellationToken, ValueTask> body)
                {
                    CancellationToken token = options.CancellationToken;
                    switch (overload)
                    {
                        case "ForEachAsync(IEnumerable)": return Parallel.ForEachAsync(items, body);
                        case "ForEachAsync(IEnumerable, CancellationToken)": return Parallel.ForEachAsync(items, token, body);
                        case "ForEachAsync(IEnumerable, ParallelOptions)": return Parallel.ForEachAsync(items, options, body);
                        case "ForEachAsync(IAsyncEnumerable)": return Parallel.ForEachAsync(stream, body);
                        case "ForEachAsync(IAsyncEnumerable, CancellationToken)": return Parallel.ForEachAsync(stream, token, body);
                        case "ForEachAsync(IAsyncEnumerable, ParallelOptions)": return Parallel.ForEachAsync(stream, options, body);
                        case "ForAsync": return Parallel.ForAsync(0L, (long)count, body);
                        case "ForAsync(CancellationToken)": return Parallel.ForAsync(0L, (long)count, token, body);
                        case "ForAsync(ParallelOptions)": return Parallel.ForAsync(0, count, options, (item, ct) => body(item, ct));
                        default: throw new ArgumentOutOfRangeException(nameof(overload), overload, null);
                    }
                }

                public static void SyncLoop(string overload, int count, ParallelOptions options, Action<long> body) =>
                    SyncLoop(overload, Enumerable.Range(0, count).Select(i => (long)i), count, options, body);

                public static void SyncLoop(string overload, IEnumerable<long> lazy, int count, ParallelOptions options, Action<long> body)
                {
                    List<long> list = Enumerable.Range(0, count).Select(i => (long)i).ToList();
                    Action[] actions = Enumerable.Range(0, count).Select(i => (Action)(() => body(i))).ToArray();
                    switch (overload)
                    {
                        case "For(int)": Parallel.For(0, count, i => body(i)); break;
                        case "For(int, ParallelOptions)": Parallel.For(0, count, options, i => body(i)); break;
                        case "For(long)": Parallel.For(0L, count, body); break;
                        case "For(long, ParallelOptions)": Parallel.For(0L, count, options, body); break;
                        case "ForEach(IEnumerable)": Parallel.ForEach(lazy, body); break;
                        case "ForEach(IEnumerable, ParallelOptions)": Parallel.ForEach(lazy, options, body); break;
                        case "ForEach(IList, ParallelOptions)": Parallel.ForEach(list, options, body); break;
                        case "ForEach(Partitioner)": Parallel.ForEach(Partitioner.Create(lazy), body); break;
                        case "ForEach(Partitioner, ParallelOptions)": Parallel.ForEach(Partitioner.Create(list, true), options, body); break;
                        case "ForEach(QueuePartitioner, ParallelOptions)": Parallel.ForEach(new QueuePartitioner(list), options, body); break;
                        case "Invoke": Parallel.Invoke(actions); break;
                        case "Invoke(ParallelOptions)": Parallel.Invoke(options, actions); break;
                        default: throw new ArgumentOutOfRangeException(nameof(overload), overload, null);
                    }
                }

                // Runs a loop whose bodies await a virtual delay and a device read.
                public static Task<(TaskStatus Status, string[] Faults, string[] Trace, int MaxInFlight)> Run(
                    string overload,
                    int count,
                    int maxDegreeOfParallelism)
                {
                    var recorder = new Recorder();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
                    return Observe(Loop(overload, count, options, (item, ct) => Body(item, ct, recorder)), recorder);
                }

                // Bodies [0, waiting) wait on a gate which never opens, so only the bodies' token ends them, and body
                // `waiting` throws once they have all entered: the loop's faults are then the throw followed by one
                // cancellation per waiting body, whatever the threads' timing. The limit leaves no worker for a
                // later element.
                public static Task<(TaskStatus Status, string[] Faults, string[] Trace, int MaxInFlight)> Fault(
                    string overload,
                    int count,
                    int waiting)
                {
                    var recorder = new Recorder();
                    var gate = new TaskCompletionSource<bool>();
                    var allEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (waiting == 0) allEntered.SetResult(true);
                    int entered = 0;
                    var options = new ParallelOptions { MaxDegreeOfParallelism = waiting + 1 };
                    return Observe(
                        Loop(overload, count, options, async (item, ct) =>
                        {
                            recorder.Enter(item, ct);
                            try
                            {
                                if (item < waiting)
                                {
                                    if (Interlocked.Increment(ref entered) == waiting) allEntered.SetResult(true);
                                    await gate.Task.WaitAsync(ct);
                                }
                                else if (item == waiting)
                                {
                                    await allEntered.Task;
                                    recorder.Mark("!" + item);
                                    throw new InvalidOperationException("boom " + item);
                                }
                            }
                            finally
                            {
                                recorder.Exit(item);
                            }
                        }),
                        recorder);
                }

                // The body of `cancelling` cancels the caller's token as it starts, then returns or throws.
                public static async Task<(TaskStatus Status, bool CanceledByCallerToken, string[] Trace)> Cancel(
                    string overload,
                    int count,
                    long cancelling,
                    bool throwAfterCancelling)
                {
                    var recorder = new Recorder();
                    using var source = new CancellationTokenSource();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = source.Token };
                    Task loop = Loop(overload, count, options, (item, ct) =>
                    {
                        if (item == cancelling)
                        {
                            recorder.Enter(item, ct);
                            source.Cancel();
                            recorder.Exit(item);
                            if (throwAfterCancelling) throw new InvalidOperationException("boom " + item);
                            return default;
                        }

                        return Body(item, ct, recorder);
                    });
                    bool canceledByCallerToken = false;
                    try
                    {
                        await loop;
                    }
                    catch (OperationCanceledException exception)
                    {
                        canceledByCallerToken = exception.CancellationToken == source.Token;
                    }
                    catch (Exception)
                    {
                    }

                    return (loop.Status, canceledByCallerToken, recorder.Trace);
                }

                public static Task<(bool CanceledOnReturn, int Started)> PreCanceled(string overload)
                {
                    int started = 0;
                    var options = new ParallelOptions { CancellationToken = new CancellationToken(canceled: true) };
                    Task loop = Loop(overload, 4, options, (item, ct) =>
                    {
                        started++;
                        return default;
                    });
                    return Task.FromResult((loop.IsCanceled, started));
                }

                // Runs a synchronous loop whose bodies sleep, which waits on the virtual clock.
                public static Task<(TaskStatus Status, string[] Faults, string[] Trace, int MaxInFlight)> RunSync(
                    string overload,
                    int count,
                    int maxDegreeOfParallelism)
                {
                    var recorder = new Recorder();
                    SyncLoop(overload, count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, item =>
                    {
                        recorder.Enter(item);
                        Thread.Sleep(5);
                        recorder.Exit(item);
                    });
                    return Task.FromResult((TaskStatus.RanToCompletion, new string[0], recorder.Trace, recorder.MaxInFlight));
                }

                // Runs a synchronous loop whose bodies block on a device read.
                public static Task<(TaskStatus Status, string[] Faults, string[] Trace, int MaxInFlight)> RunSyncBlocking(
                    string overload,
                    int count,
                    int maxDegreeOfParallelism)
                {
                    var recorder = new Recorder();
                    SyncLoop(overload, count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, item =>
                    {
                        recorder.Enter(item);
                        Task<long> read = Device.ReadAsync(item);
                        read.Wait();
                        if (read.Result != item * 10) throw new InvalidOperationException("bad read");
                        recorder.Exit(item);
                    });
                    return Task.FromResult((TaskStatus.RanToCompletion, new string[0], recorder.Trace, recorder.MaxInFlight));
                }

                // Runs a loop whose synchronous bodies record which worker took each element, as "item@worker". A
                // worker takes its id on its first element, in an AsyncLocal which flows through the worker's own
                // iterations and to no other worker.
                public static async Task<string[]> RunWorkers(string overload, int count, int maxDegreeOfParallelism)
                {
                    var worker = new AsyncLocal<int>();
                    int workers = 0;
                    var taken = new List<string>();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
                    void Record(long item)
                    {
                        if (worker.Value == 0) worker.Value = ++workers;
                        taken.Add(item + "@" + worker.Value);
                    }

                    if (overload.Contains("Async"))
                    {
                        await Loop(overload, count, options, (item, ct) =>
                        {
                            Record(item);
                            return default;
                        });
                    }
                    else
                    {
                        SyncLoop(overload, count, options, Record);
                    }

                    return taken.ToArray();
                }

                // The body of `failing` (and of `secondFailing`, if any) throws after sleeping.
                public static Task<(string Thrown, string[] Trace)> SyncFault(string overload, int count, long failing, long secondFailing)
                {
                    var recorder = new Recorder();
                    Exception thrown = null;
                    try
                    {
                        SyncLoop(overload, count, new ParallelOptions { MaxDegreeOfParallelism = 2 }, item =>
                        {
                            recorder.Enter(item);
                            Thread.Sleep(5);
                            recorder.Exit(item);
                            if (item == failing || item == secondFailing)
                            {
                                recorder.Mark("!" + item);
                                throw new InvalidOperationException("boom " + item);
                            }
                        });
                    }
                    catch (Exception exception)
                    {
                        thrown = exception;
                    }

                    return Task.FromResult((Faults.DescribeThrown(thrown, default, sortInner: true), recorder.Trace));
                }

                // The body of `cancelling` cancels the options' token, then: "Cancel" returns; "Fault" throws an
                // InvalidOperationException; "Canceled" throws the token's OperationCanceledException, as does every
                // body which starts once the token is cancelled.
                public static Task<(string Thrown, string[] Trace)> SyncCancel(
                    string overload,
                    int count,
                    long cancelling,
                    int maxDegreeOfParallelism,
                    string mode)
                {
                    var recorder = new Recorder();
                    using var source = new CancellationTokenSource();
                    CancellationToken token = source.Token;
                    Exception thrown = null;
                    var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = token };
                    try
                    {
                        SyncLoop(overload, count, options, item =>
                        {
                            recorder.Enter(item);
                            if (item == cancelling)
                            {
                                source.Cancel();
                                if (mode == "Fault") throw new InvalidOperationException("boom " + item);
                                if (mode == "Canceled") throw new OperationCanceledException(token);
                            }
                            else if (mode == "Canceled")
                            {
                                token.ThrowIfCancellationRequested();
                            }

                            Thread.Sleep(5);
                            recorder.Exit(item);
                        });
                    }
                    catch (Exception exception)
                    {
                        thrown = exception;
                    }

                    string described = Faults.DescribeThrown(thrown, token, collapseCancellations: overload.StartsWith("Invoke"));
                    return Task.FromResult((described, recorder.Trace));
                }

                // The body of element 1 throws ("BodyFault") or cancels the options' token ("Cancel"); the source may
                // throw from MoveNext or Dispose. Reports the outcome and how often the source was enumerated and
                // disposed.
                public static async Task<string> Disposal(string overload, string mode)
                {
                    const int count = 6;
                    var source = new TrackedSource(count, mode);
                    using var cancellation = new CancellationTokenSource();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellation.Token };
                    string outcome;
                    if (overload.Contains("Async"))
                    {
                        Task loop = Loop(overload, source, source, count, options, (item, ct) =>
                        {
                            Act(item);
                            return default;
                        });
                        try
                        {
                            await loop;
                        }
                        catch (Exception)
                        {
                        }

                        outcome = loop.Status + "[" + string.Join(", ", Faults.DescribeAll(loop, cancellation.Token)) + "]";
                    }
                    else
                    {
                        Exception thrown = null;
                        try
                        {
                            SyncLoop(overload, source, count, options, Act);
                        }
                        catch (Exception exception)
                        {
                            thrown = exception;
                        }

                        outcome = Faults.DescribeThrown(thrown, cancellation.Token);
                    }

                    return outcome + " enumerators=" + source.Enumerators + " disposals=" + source.Disposals;

                    void Act(long item)
                    {
                        if (item != 1) return;
                        if (mode == "BodyFault") throw new InvalidOperationException("boom 1");
                        if (mode == "Cancel") cancellation.Cancel();
                    }
                }

                // A ForEach over a dynamic partitioner, whose body of element 1 throws or cancels the options' token:
                // reports the outcome, whether every partition handed out was disposed, and how often the partitions'
                // enumerable was disposed.
                public static Task<string> PartitionDisposal(string mode)
                {
                    var partitioner = new TrackedPartitioner(6);
                    using var cancellation = new CancellationTokenSource();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellation.Token };
                    Exception thrown = null;
                    try
                    {
                        Parallel.ForEach(partitioner, options, item =>
                        {
                            if (item != 1) return;
                            if (mode == "BodyFault") throw new InvalidOperationException("boom 1");
                            if (mode == "Cancel") cancellation.Cancel();
                        });
                    }
                    catch (Exception exception)
                    {
                        thrown = exception;
                    }

                    return Task.FromResult(
                        Faults.DescribeThrown(thrown, cancellation.Token)
                        + " everyPartitionDisposed=" + (partitioner.Partitions > 0 && partitioner.Partitions == partitioner.PartitionDisposals)
                        + " enumerableDisposals=" + partitioner.EnumerableDisposals);
                }

                // A synchronous loop of degree 1 inside a lock on `gate`, whose bodies take the lock again: each body
                // reports the caller's AsyncLocal as it sees it and whether it holds the lock, then sets the AsyncLocal;
                // the caller reports the AsyncLocal after the loop.
                public static Task<string> CallerWorker(string overload)
                {
                    var gate = new object();
                    var local = new AsyncLocal<string>();
                    var seen = new List<string>();
                    local.Value = "caller";
                    lock (gate)
                    {
                        SyncLoop(overload, 3, new ParallelOptions { MaxDegreeOfParallelism = 1 }, item =>
                        {
                            bool holdsLock = Monitor.IsEntered(gate);
                            lock (gate)
                            {
                                seen.Add(item + ":" + local.Value + ":" + holdsLock);
                            }

                            local.Value = "body" + item;
                        });
                    }

                    return Task.FromResult(string.Join(",", seen) + "|" + local.Value);
                }

                // Runs a loop on `scheduler`, each body reporting the node it runs on, as "item@node".
                public static async Task<string[]> SchedulerNodes(string overload, TaskScheduler scheduler, Func<string> currentNode)
                {
                    var nodes = new List<string>();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = 3, TaskScheduler = scheduler };
                    void Record(long item)
                    {
                        lock (nodes) nodes.Add(item + "@" + currentNode());
                    }

                    if (overload.Contains("Async"))
                    {
                        await Loop(overload, 6, options, (item, ct) =>
                        {
                            Record(item);
                            return default;
                        });
                    }
                    else
                    {
                        SyncLoop(overload, 6, options, Record);
                    }

                    lock (nodes) return nodes.OrderBy(entry => entry, StringComparer.Ordinal).ToArray();
                }

                public static void CustomScheduler(bool asynchronous)
                {
                    var options = new ParallelOptions { TaskScheduler = new ConcurrentExclusiveSchedulerPair().ConcurrentScheduler };
                    if (asynchronous)
                    {
                        _ = Parallel.ForEachAsync(new[] { 1 }, options, (item, ct) => default);
                    }
                    else
                    {
                        Parallel.For(0, 1, options, item => { });
                    }
                }

                public static Task ReportThreads(bool forAsync, int count, Action report)
                {
                    Func<int, CancellationToken, ValueTask> body = (item, ct) =>
                    {
                        report();
                        return default;
                    };
                    return forAsync
                        ? Parallel.ForAsync(0, count, body)
                        : Parallel.ForEachAsync(Enumerable.Range(0, count), body);
                }

                private static async Task<(TaskStatus Status, string[] Faults, string[] Trace, int MaxInFlight)> Observe(
                    Task loop,
                    Recorder recorder)
                {
                    try
                    {
                        await loop;
                    }
                    catch (Exception)
                    {
                    }

                    return (loop.Status, Faults.DescribeAll(loop, recorder.BodyToken), recorder.Trace, recorder.MaxInFlight);
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public ParallelLoopConformanceTests()
    {
        _release = new(() => _fixture.StageControlledTasks("Conf.ParallelLoopRel", ProbeType, Source, optimize: true));
        _debug = new(() => _fixture.StageControlledTasks("Conf.ParallelLoopDbg", ProbeType, Source, optimize: false));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.ParallelLoopBcl", ProbeType, Source));
    }

    public static TheoryData<string, bool> AsyncOverloads
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string overload in AsyncOverloadNames)
            {
                data.Add(overload, true);
                data.Add(overload, false);
            }

            return data;
        }
    }

    public static TheoryData<string> AllAsyncOverloads => new(AsyncOverloadNames);

    public static TheoryData<string> AsyncOverloadsWithToken => new(AsyncOverloadNames.Where(HasToken));

    public static TheoryData<string, bool> AsyncCancellations
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string overload in AsyncOverloadNames.Where(HasToken))
            {
                data.Add(overload, false);
                data.Add(overload, true);
            }

            return data;
        }
    }

    public static TheoryData<string> SyncOverloads => new(SyncOverloadNames);

    public static TheoryData<string> SyncOverloadsWithOptions => new(SyncOverloadNames.Where(HasOptions));

    public static TheoryData<string> InvokeOverloads => new(SyncOverloadNames.Where(IsInvoke));

    public static TheoryData<string, string> SyncCancellations
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string overload in SyncOverloadNames.Where(HasOptions))
            {
                data.Add(overload, "Cancel");
                data.Add(overload, "Fault");
                data.Add(overload, "Canceled");
            }

            return data;
        }
    }

    public static TheoryData<string> AllOverloads => new(AsyncOverloadNames.Concat(SyncOverloadNames));

    // Each loop's limit boundaries: fully serial, at and beyond the element count, an explicit -1 (the simulated
    // processor count), and an empty source; and Invoke with more than ten actions.
    public static TheoryData<string, int, int> LimitBoundaries
    {
        get
        {
            var data = new TheoryData<string, int, int>();
            foreach (string overload in AsyncOverloadNames.Concat(SyncOverloadNames))
            {
                int count = IsAsync(overload) ? AsyncElementCount : SyncElementCount;
                if (HasOptions(overload))
                {
                    data.Add(overload, count, 1);
                    data.Add(overload, count, count);
                    data.Add(overload, count, count + 6);
                    data.Add(overload, count, -1);
                }

                data.Add(overload, 0, MaxDegreeOfParallelism);
            }

            data.Add("Invoke", 12, MaxDegreeOfParallelism);
            data.Add("Invoke(ParallelOptions)", 12, MaxDegreeOfParallelism);
            data.Add("Invoke(ParallelOptions)", 12, -1);
            return data;
        }
    }

    public static TheoryData<string, string> Disposals
    {
        get
        {
            string[] overloads =
            [
                .. AsyncOverloadNames.Where(overload => overload.StartsWith("ForEachAsync", StringComparison.Ordinal)),
                "ForEach(IEnumerable)",
                "ForEach(IEnumerable, ParallelOptions)",
                "ForEach(Partitioner)",
            ];
            var data = new TheoryData<string, string>();
            foreach (string overload in overloads)
            {
                foreach (string mode in (string[])["Success", "BodyFault", "MoveNextFault", "DisposeFault", "Cancel"])
                {
                    if (mode != "Cancel" || HasToken(overload))
                    {
                        data.Add(overload, mode);
                    }
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string> Schedulers
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string overload in AsyncOverloadNames.Concat(SyncOverloadNames).Where(HasOptions))
            {
                data.Add(overload, "Default");
                data.Add(overload, "CallerNode");
                data.Add(overload, "OtherNode");
            }

            return data;
        }
    }

    private static string[] AsyncOverloadNames =>
    [
        "ForEachAsync(IEnumerable)",
        "ForEachAsync(IEnumerable, CancellationToken)",
        "ForEachAsync(IEnumerable, ParallelOptions)",
        "ForEachAsync(IAsyncEnumerable)",
        "ForEachAsync(IAsyncEnumerable, CancellationToken)",
        "ForEachAsync(IAsyncEnumerable, ParallelOptions)",
        "ForAsync",
        "ForAsync(CancellationToken)",
        "ForAsync(ParallelOptions)",
    ];

    private static string[] SyncOverloadNames =>
    [
        "For(int)",
        "For(int, ParallelOptions)",
        "For(long)",
        "For(long, ParallelOptions)",
        "ForEach(IEnumerable)",
        "ForEach(IEnumerable, ParallelOptions)",
        "ForEach(IList, ParallelOptions)",
        "ForEach(Partitioner)",
        "ForEach(Partitioner, ParallelOptions)",
        "ForEach(QueuePartitioner, ParallelOptions)",
        "Invoke",
        "Invoke(ParallelOptions)",
    ];

    [Theory]
    [MemberData(nameof(AsyncOverloads))]
    public async Task AsyncLoopRunsEveryElementOnceWithinItsParallelismLimit(string overload, bool optimize)
    {
        int limit = HasOptions(overload) ? MaxDegreeOfParallelism : SimulatedProcessorCount;
        foreach ((string schedule, LoopOutcome outcome) in Explore<LoopOutcome>(Method("Run", optimize), overload, AsyncElementCount, MaxDegreeOfParallelism))
        {
            Assert.Equal(TaskStatus.RanToCompletion, outcome.Status);
            Assert.Empty(outcome.Faults);
            AssertEachElementOnce(outcome.Trace, AsyncElementCount);
            Assert.True(limit == outcome.MaxInFlight, $"{schedule}: {outcome.MaxInFlight} bodies in flight, expected {limit}.");
        }

        LoopOutcome bcl = await Invoke<LoopOutcome>(_bcl.Value.Method("Run"), overload, AsyncElementCount, MaxDegreeOfParallelism);
        Assert.Equal(TaskStatus.RanToCompletion, bcl.Status);
        AssertEachElementOnce(bcl.Trace, AsyncElementCount);
        if (HasOptions(overload))
        {
            Assert.InRange(bcl.MaxInFlight, 1, MaxDegreeOfParallelism);
        }
    }

    [Theory]
    [MemberData(nameof(LimitBoundaries))]
    public async Task LoopKeepsItsParallelismLimitAtItsBoundaries(string overload, int count, int maxDegreeOfParallelism)
    {
        bool asynchronous = IsAsync(overload);
        int limit = HasOptions(overload) && maxDegreeOfParallelism != -1 ? maxDegreeOfParallelism : SimulatedProcessorCount;
        int expected = Math.Min(limit, count);
        MethodInfo method = Method(asynchronous ? "Run" : "RunSync");
        foreach ((string schedule, LoopOutcome outcome) in Explore<LoopOutcome>(method, overload, count, maxDegreeOfParallelism))
        {
            Assert.Equal(TaskStatus.RanToCompletion, outcome.Status);
            AssertEachElementOnce(outcome.Trace, count);
            Assert.True(expected == outcome.MaxInFlight, $"{schedule}: {outcome.MaxInFlight} bodies in flight, expected {expected}.");
            if (expected == 1)
            {
                // A loop of degree 1 runs its elements one after another, in order.
                Assert.Equal(Enumerable.Range(0, count).SelectMany(item => (string[])[$"+{item}", $"-{item}"]), outcome.Trace);
            }
        }

        LoopOutcome bcl = await Invoke<LoopOutcome>(_bcl.Value.Method(asynchronous ? "Run" : "RunSync"), overload, count, maxDegreeOfParallelism);
        AssertEachElementOnce(bcl.Trace, count);
        Assert.InRange(bcl.MaxInFlight, Math.Min(1, count), HasOptions(overload) && maxDegreeOfParallelism != -1 ? expected : count);
    }

    [Theory]
    [MemberData(nameof(AllAsyncOverloads))]
    public async Task AsyncLoopAggregatesFaultsAsTheBclDoes(string overload)
    {
        // Without options the BCL runs Environment.ProcessorCount workers, so on a machine with fewer than three the
        // probe waits on fewer bodies; it then has no later element, which a BCL worker could take before the fault.
        bool hasOptions = HasOptions(overload);
        int waiting = hasOptions ? 2 : Math.Min(2, Environment.ProcessorCount - 1);
        int count = hasOptions ? AsyncElementCount : waiting + 1;

        LoopOutcome bcl = await Invoke<LoopOutcome>(_bcl.Value.Method("Fault"), overload, count, waiting);
        Assert.Equal(TaskStatus.Faulted, bcl.Status);
        Assert.Equal($"InvalidOperationException(boom {waiting})", bcl.Faults[0]);
        Assert.Equal(waiting, bcl.Faults.Skip(1).Count(fault => fault.EndsWith("@token", StringComparison.Ordinal)));
        Assert.Equal(waiting + 1, bcl.Faults.Length);

        foreach ((string schedule, LoopOutcome outcome) in Explore<LoopOutcome>(Method("Fault"), overload, count, waiting))
        {
            Assert.Equal(TaskStatus.Faulted, outcome.Status);
            Assert.Equal($"{schedule}: {string.Join(" | ", bcl.Faults)}", $"{schedule}: {string.Join(" | ", outcome.Faults)}");

            // Recording the fault stops every worker before its next element.
            Assert.Contains($"!{waiting}", outcome.Trace);
            Assert.DoesNotContain(outcome.Trace, entry => entry.EndsWith('*'));
            Assert.Equal(waiting + 1, outcome.Trace.Count(entry => entry.StartsWith('+')));
        }
    }

    [Theory]
    [MemberData(nameof(AsyncCancellations))]
    public async Task AsyncLoopObservesCancellationAsTheBclDoes(string overload, bool throwAfterCancelling)
    {
        // The caller's cancelled token outranks every fault, including the cancelling body's own.
        var bcl = await Invoke<(TaskStatus Status, bool CanceledByCallerToken, string[] Trace)>(
            _bcl.Value.Method("Cancel"), overload, AsyncElementCount, 2L, throwAfterCancelling);
        Assert.Equal((TaskStatus.Canceled, true), (bcl.Status, bcl.CanceledByCallerToken));

        foreach ((string schedule, var outcome) in Explore<(TaskStatus Status, bool CanceledByCallerToken, string[] Trace)>(
            Method("Cancel"), overload, AsyncElementCount, 2L, throwAfterCancelling))
        {
            Assert.Equal((schedule, bcl.Status, bcl.CanceledByCallerToken), (schedule, outcome.Status, outcome.CanceledByCallerToken));

            // Cancellation stops every worker before its next element.
            Assert.DoesNotContain(outcome.Trace, entry => entry.EndsWith('*'));
            Assert.True(outcome.Trace.Count(entry => entry.StartsWith('+')) < AsyncElementCount, string.Join(",", outcome.Trace));
        }
    }

    [Theory]
    [MemberData(nameof(AsyncOverloadsWithToken))]
    public async Task AsyncLoopWithACancelledTokenReturnsACancelledTaskWithoutRunningABody(string overload)
    {
        using var host = new SimulationHost(Start);
        (bool, int) controlled = Completed<(bool, int)>(host.Invoke(Method("PreCanceled"), overload));
        (bool, int) bcl = await Invoke<(bool, int)>(_bcl.Value.Method("PreCanceled"), overload);

        Assert.Equal((true, 0), controlled);
        Assert.Equal(bcl, controlled);
    }

    [Theory]
    [MemberData(nameof(SyncOverloads))]
    public async Task SyncLoopRunsEveryElementOnceWithinItsParallelismLimit(string overload)
    {
        int limit = HasOptions(overload) ? MaxDegreeOfParallelism : SimulatedProcessorCount;
        foreach ((string schedule, LoopOutcome outcome) in Explore<LoopOutcome>(Method("RunSync"), overload, SyncElementCount, MaxDegreeOfParallelism))
        {
            AssertEachElementOnce(outcome.Trace, SyncElementCount);
            Assert.True(limit == outcome.MaxInFlight, $"{schedule}: {outcome.MaxInFlight} bodies in flight, expected {limit}.");
        }

        LoopOutcome bcl = await Invoke<LoopOutcome>(_bcl.Value.Method("RunSync"), overload, SyncElementCount, MaxDegreeOfParallelism);
        AssertEachElementOnce(bcl.Trace, SyncElementCount);
        if (HasOptions(overload))
        {
            Assert.InRange(bcl.MaxInFlight, 1, MaxDegreeOfParallelism);
        }
    }

    [Theory]
    [MemberData(nameof(SyncOverloads))]
    public async Task SyncLoopWhoseBodiesBlockOnAReadRunsEveryElementOnceWithinItsParallelismLimit(string overload)
    {
        int limit = HasOptions(overload) ? MaxDegreeOfParallelism : SimulatedProcessorCount;
        foreach ((string schedule, LoopOutcome outcome) in Explore<LoopOutcome>(Method("RunSyncBlocking"), overload, SyncElementCount, MaxDegreeOfParallelism))
        {
            AssertEachElementOnce(outcome.Trace, SyncElementCount);
            Assert.True(outcome.MaxInFlight <= limit, $"{schedule}: {outcome.MaxInFlight} bodies in flight, limit {limit}.");
        }

        LoopOutcome bcl = await Invoke<LoopOutcome>(_bcl.Value.Method("RunSyncBlocking"), overload, SyncElementCount, MaxDegreeOfParallelism);
        AssertEachElementOnce(bcl.Trace, SyncElementCount);
    }

    [Theory]
    [MemberData(nameof(SyncOverloads))]
    public async Task SyncLoopAggregatesFaultsAsTheBclDoes(string overload)
    {
        var bcl = await Invoke<(string Thrown, string[] Trace)>(_bcl.Value.Method("SyncFault"), overload, SyncElementCount, 1L, -1L);
        Assert.Equal("AggregateException[InvalidOperationException(boom 1)]", bcl.Thrown);

        foreach ((string schedule, var outcome) in Explore<(string Thrown, string[] Trace)>(
            Method("SyncFault"), overload, SyncElementCount, 1L, -1L))
        {
            Assert.Equal($"{schedule}: {bcl.Thrown}", $"{schedule}: {outcome.Thrown}");
            if (IsInvoke(overload))
            {
                // Invoke runs every action despite a fault.
                AssertEachElementOnce(outcome.Trace, SyncElementCount);
            }
            else
            {
                // A loop stops launching iterations after its first fault.
                Assert.DoesNotContain(outcome.Trace.SkipWhile(entry => entry != "!1"), entry => entry.StartsWith('+'));
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvokeOverloads))]
    public async Task InvokeAggregatesEveryFailingActionAsTheBclDoes(string overload)
    {
        var bcl = await Invoke<(string Thrown, string[] Trace)>(_bcl.Value.Method("SyncFault"), overload, SyncElementCount, 1L, 3L);
        Assert.Equal("AggregateException[InvalidOperationException(boom 1), InvalidOperationException(boom 3)]", bcl.Thrown);

        foreach ((string schedule, var outcome) in Explore<(string Thrown, string[] Trace)>(
            Method("SyncFault"), overload, SyncElementCount, 1L, 3L))
        {
            Assert.Equal($"{schedule}: {bcl.Thrown}", $"{schedule}: {outcome.Thrown}");
            AssertEachElementOnce(outcome.Trace, SyncElementCount);
        }
    }

    [Theory]
    [MemberData(nameof(SyncCancellations))]
    public async Task SyncLoopObservesCancellationAsTheBclDoes(string overload, string mode)
    {
        // A cancelled token gives its OperationCanceledException, as does a set of faults which are all cancellations
        // by it; any other fault gives the AggregateException of the faults.
        string expected = mode == "Fault"
            ? "AggregateException[InvalidOperationException(boom 1)]" + (IsInvoke(overload) ? "+canceled" : "")
            : "OperationCanceledException(The operation was canceled.)@token";
        var bcl = await Invoke<(string Thrown, string[] Trace)>(_bcl.Value.Method("SyncCancel"), overload, SyncElementCount, 1L, 2, mode);
        Assert.Equal(expected, bcl.Thrown);

        foreach ((string schedule, var outcome) in Explore<(string Thrown, string[] Trace)>(
            Method("SyncCancel"), overload, SyncElementCount, 1L, 2, mode))
        {
            Assert.Equal($"{schedule}: {bcl.Thrown}", $"{schedule}: {outcome.Thrown}");

            // Cancellation stops every worker before its next element.
            Assert.DoesNotContain(outcome.Trace.SkipWhile(entry => entry != "+1").Skip(1), entry => entry.StartsWith('+'));
        }
    }

    [Fact]
    public void InvokeOfSixActionsStopsAtTheTokenOneOfThemCancels()
    {
        // No BCL oracle: without a limit the BCL runs six actions as six tasks, so its outcome depends on its threads'
        // timing - nothing, the TaskCanceledException of an action which had yet to start, or, when the token is
        // cancelled before the first action runs inline, RunSynchronously's InvalidOperationException. The controlled
        // Invoke runs the actions on its worker pool, which checks the token after each action, as the BCL's does
        // with a limit.
        foreach ((string schedule, var outcome) in Explore<(string Thrown, string[] Trace)>(
            Method("SyncCancel"), "Invoke(ParallelOptions)", 6, 1L, -1, "Cancel"))
        {
            Assert.Equal(
                $"{schedule}: OperationCanceledException(The operation was canceled.)@token",
                $"{schedule}: {outcome.Thrown}");

            // A worker checks the token after each action, so each runs at most the action in hand once it is
            // cancelled, and the actions left over never run.
            Assert.True(outcome.Trace.Count(entry => entry.StartsWith('+')) < 6, string.Join(",", outcome.Trace));
        }
    }

    [Theory]
    [MemberData(nameof(SyncOverloadsWithOptions))]
    public async Task SyncLoopRunsItsFirstWorkerOnTheCallerAsTheBclDoes(string overload)
    {
        // With a degree of parallelism of 1 every body runs on the caller, as the caller: the lock the caller holds
        // is re-entered, not waited for; the body sees the caller's AsyncLocal; and what a body sets flows to the
        // later bodies but not back to the caller.
        string bcl = await Invoke<string>(_bcl.Value.Method("CallerWorker"), overload);
        Assert.Equal("0:caller:True,1:body0:True,2:body1:True|caller", bcl);

        foreach ((string schedule, string outcome) in Explore<string>(Method("CallerWorker"), overload))
        {
            Assert.Equal($"{schedule}: {bcl}", $"{schedule}: {outcome}");
        }
    }

    [Theory]
    [MemberData(nameof(Disposals))]
    public async Task LoopDisposesItsSourceOnceAsTheBclDoes(string overload, string mode)
    {
        string bcl = await Invoke<string>(_bcl.Value.Method("Disposal"), overload, mode);
        Assert.EndsWith("enumerators=1 disposals=1", bcl, StringComparison.Ordinal);

        foreach ((string schedule, string outcome) in Explore<string>(Method("Disposal"), overload, mode))
        {
            Assert.Equal($"{schedule}: {bcl}", $"{schedule}: {outcome}");
        }
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("BodyFault")]
    [InlineData("Cancel")]
    public async Task PartitionedLoopDisposesEveryPartitionAsTheBclDoes(string mode)
    {
        string bcl = await Invoke<string>(_bcl.Value.Method("PartitionDisposal"), mode);
        Assert.EndsWith("everyPartitionDisposed=True enumerableDisposals=1", bcl, StringComparison.Ordinal);

        foreach ((string schedule, string outcome) in Explore<string>(Method("PartitionDisposal"), mode))
        {
            Assert.Equal($"{schedule}: {bcl}", $"{schedule}: {outcome}");
        }
    }

    [Theory]
    [MemberData(nameof(Schedulers))]
    public async Task LoopRunsItsWorkersOnTheNodeOfItsOptionsScheduler(string overload, string scheduler)
    {
        using var host = new SimulationHost(Start, nodeAddresses: ["caller", "other"]);
        TaskScheduler target = scheduler switch
        {
            "Default" => TaskScheduler.Default,
            "CallerNode" => host.NodeTaskScheduler("caller"),
            _ => host.NodeTaskScheduler("other"),
        };
        string node = scheduler == "OtherNode" ? "other" : "caller";
        Func<string> currentNode = () => SimulationExecutionContext.Current?.Node?.Address ?? "none";

        string[] nodes = Completed<string[]>(host.Invoke(Method("SchedulerNodes"), overload, target, currentNode));

        Assert.Equal(Enumerable.Range(0, 6).Select(item => $"{item}@{node}"), nodes);
        if (scheduler == "Default")
        {
            string[] bcl = await Invoke<string[]>(_bcl.Value.Method("SchedulerNodes"), overload, TaskScheduler.Default, (Func<string>)(() => "caller"));
            Assert.Equal(bcl, nodes);
        }
    }

    [Theory]
    [MemberData(nameof(AllOverloads))]
    public void SeedReplaysItsInterleavingAndSeedsChooseDifferentInterleavings(string overload)
    {
        bool asynchronous = IsAsync(overload);
        int count = asynchronous ? AsyncElementCount : SyncElementCount;
        int limit = HasOptions(overload) ? MaxDegreeOfParallelism : SimulatedProcessorCount;
        MethodInfo method = Method(asynchronous ? "Run" : "RunSync");
        object?[] args = [overload, count, MaxDegreeOfParallelism];
        var traces = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 1; seed <= 8; seed++)
        {
            var recorded = new List<(ReplayExecutionResult Result, LoopOutcome Outcome)>();
            for (int recording = 0; recording < 2; recording++)
            {
                var probe = new StrongBox<object?>();
                ReplayExecutionResult result = ReplayRunner.RecordCluster(
                    SeededRandom(seed),
                    cluster => Schedule(cluster, method, args, probe),
                    TestContext.Current.CancellationToken);
                Assert.Equal(ReplayTerminationKind.Completed, result.Artifact.Outcome.Kind);
                recorded.Add((result, Completed<LoopOutcome>(probe.Value)));
            }

            LoopOutcome outcome = recorded[0].Outcome;
            AssertEachElementOnce(outcome.Trace, count);
            Assert.True(outcome.MaxInFlight <= limit, $"seed {seed}: {outcome.MaxInFlight} bodies in flight, limit {limit}.");
            Assert.Equal(outcome.Trace, recorded[1].Outcome.Trace);
            Assert.Equal(recorded[0].Result.ArtifactId, recorded[1].Result.ArtifactId);

            var replayedProbe = new StrongBox<object?>();
            ReplayExecutionResult replayed = ReplayRunner.ReplayCluster(
                recorded[0].Result.Artifact,
                ReplayCompatibilityRequirements.Current(),
                cluster => Schedule(cluster, method, args, replayedProbe),
                maxSteps: 1_000_000,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(replayed.Reproduced);
            Assert.Equal(outcome.Trace, Completed<LoopOutcome>(replayedProbe.Value).Trace);
            traces.Add(string.Join(",", outcome.Trace));
        }

        Assert.True(traces.Count > 1, $"Eight seeds chose one interleaving: {string.Join(" | ", traces)}");
    }

    [Theory]
    [MemberData(nameof(AllOverloads))]
    public void SeedsChooseWhichWorkerTakesEachElement(string overload)
    {
        // The bodies complete synchronously, so only the loop's own scheduling point between a worker's iterations
        // lets another worker take the next element: without it one worker would take every element under every seed.
        bool asynchronous = IsAsync(overload);
        int count = asynchronous ? AsyncElementCount : SyncElementCount;
        int limit = HasOptions(overload) ? MaxDegreeOfParallelism : SimulatedProcessorCount;
        var interleavings = new HashSet<string>(StringComparer.Ordinal);
        int mostWorkers = 0;
        for (int seed = 1; seed <= 8; seed++)
        {
            string[] taken = Completed<string[]>(RecordSeeded(seed, Method("RunWorkers"), overload, count, MaxDegreeOfParallelism));

            Assert.Equal(
                Enumerable.Range(0, count),
                taken.Select(entry => int.Parse(entry.Split('@')[0], System.Globalization.CultureInfo.InvariantCulture)).Order());
            int workers = taken.Select(entry => entry.Split('@')[1]).Distinct(StringComparer.Ordinal).Count();
            Assert.True(workers <= limit, $"seed {seed}: {workers} workers, limit {limit}: {string.Join(",", taken)}");
            mostWorkers = Math.Max(mostWorkers, workers);
            interleavings.Add(string.Join(",", taken));
        }

        Assert.True(mostWorkers > 1, $"One worker took every element under every seed: {string.Join(" | ", interleavings)}");
        Assert.True(interleavings.Count > 1, $"Eight seeds chose one interleaving: {string.Join(" | ", interleavings)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CustomTaskSchedulerIsRejected(bool asynchronous)
    {
        using var host = new SimulationHost(Start);

        var exception = Assert.IsType<SimulationApiException>(
            Record.Exception(() => host.Invoke(Method("CustomScheduler"), asynchronous)));

        Assert.Equal(SimulationApiCategory.Parallel, exception.Category);
        Assert.Contains("TaskScheduler", exception.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutTheAsyncLoopRulesTheBodiesEscapeToTheThreadPool(bool forAsync)
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            forAsync ? "Conf.ParallelEscapeFor" : "Conf.ParallelEscapeForEach",
            ProbeType,
            Source,
            rule => rule.Id.StartsWith("clockwork.parallel.foreachasync", StringComparison.Ordinal)
                || rule.Id.StartsWith("clockwork.parallel.forasync", StringComparison.Ordinal));

        Assert.Equal(Enumerable.Repeat(false, 3), ReportThreads(escaping.Method("ReportThreads"), forAsync, controlled: false));
        Assert.Equal(Enumerable.Repeat(true, 3), ReportThreads(Method("ReportThreads"), forAsync, controlled: true));
    }

    // Reports, for each body, whether it ran on the simulation's logical thread.
    private static bool[] ReportThreads(MethodInfo method, bool forAsync, bool controlled)
    {
        // The guard is off for an uncontrolled loop, whose escape runs the bodies on the thread pool.
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: !controlled);
        var observed = new ConcurrentQueue<bool>();
        var loop = (Task)host.Invoke(method, forAsync, 3, new Action(() => observed.Enqueue(host.Scheduler.IsSimulationThread)))!;

        // A controlled loop completes within the drive; an escaped one completes on thread-pool threads, outside the
        // drive which returned, and is joined before the host is disposed.
        Assert.True(!controlled || loop.IsCompleted, "The controlled loop outlived the drive which ran it.");
        Assert.True(loop.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(TaskStatus.RanToCompletion, loop.Status);
        return [.. observed];
    }

    // The probe's outcome under the default schedule and under each of the seeded random safety schedules.
    private static IEnumerable<(string Schedule, T Outcome)> Explore<T>(MethodInfo method, params object?[] args)
    {
        using (var host = new SimulationHost(Start))
        {
            yield return ("default schedule", Completed<T>(host.Invoke(method, args)));
        }

        for (int seed = 1; seed <= SafetySeeds; seed++)
        {
            yield return ($"seed {seed}", Completed<T>(RecordSeeded(seed, method, args)));
        }
    }

    private static object? RecordSeeded(int seed, MethodInfo method, params object?[] args)
    {
        var probe = new StrongBox<object?>();
        ReplayExecutionResult result = ReplayRunner.RecordCluster(
            SeededRandom(seed),
            cluster => Schedule(cluster, method, args, probe),
            TestContext.Current.CancellationToken);
        Assert.Equal(ReplayTerminationKind.Completed, result.Artifact.Outcome.Kind);
        return probe.Value;
    }

    private static void Schedule(SimulationCluster cluster, MethodInfo method, object?[] args, StrongBox<object?> probe)
    {
        SimulationNode node = cluster.AddNode("node");
        node.Context.SchedulerLane.Enqueue(() => probe.Value = method.Invoke(null, args));
    }

    private static ReplayRecordingOptions SeededRandom(int seed) => new()
    {
        SimulationSeed = 1,
        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
        ScheduleSeed = seed,
        MaxSteps = 1_000_000,
    };

    private static void AssertEachElementOnce(string[] trace, int count)
    {
        for (int item = 0; item < count; item++)
        {
            Assert.Single(trace, entry => entry == "+" + item);
            Assert.Single(trace, entry => entry == "-" + item);
        }

        Assert.Equal(2 * count, trace.Count(entry => entry[0] is '+' or '-'));
    }

    private static bool IsAsync(string overload) => overload.Contains("Async", StringComparison.Ordinal);

    private static bool IsInvoke(string overload) => overload.StartsWith("Invoke", StringComparison.Ordinal);

    private static bool HasToken(string overload) =>
        overload.Contains("CancellationToken", StringComparison.Ordinal) || HasOptions(overload);

    private static bool HasOptions(string overload) => overload.Contains("ParallelOptions", StringComparison.Ordinal);

    private static T Completed<T>(object? task)
    {
        var typed = Assert.IsAssignableFrom<Task<T>>(task);
        Assert.Equal(TaskStatus.RanToCompletion, typed.Status);
        return typed.Result;
    }

    private static async Task<T> Invoke<T>(MethodInfo method, params object?[] args) =>
        await ((Task<T>)method.Invoke(null, args)!).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private MethodInfo Method(string name, bool optimize = true) => (optimize ? _release : _debug).Value.Method(name);

    public void Dispose() => _fixture.Dispose();
}
