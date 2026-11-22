using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Replay;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <c>ChannelReader&lt;T&gt;.ReadAllAsync</c>, <c>ReadAsync</c>, and
/// <c>ChannelWriter&lt;T&gt;.WriteAsync</c>. The BCL implements the base <c>ReadAllAsync</c>, and the base
/// <c>ReadAsync</c>/<c>WriteAsync</c> which a custom reader or writer inherits, as async methods compiled into the
/// channels assembly, whose awaits resume on the thread pool. Rewritten, each step runs on the simulation's logical
/// thread: a reader which reports the thread its <c>TryRead</c> runs on sees only the logical thread, and a channel's
/// elements arrive in the order written. The same scenarios run uninstrumented against the BCL and produce the same
/// outcomes, a seed replays its producer/consumer interleaving, and a probe rewritten without one of the rules runs that
/// member's <c>TryRead</c> or <c>TryWrite</c> on a thread-pool thread.
/// </summary>
public sealed class ChannelConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.ChannelProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Channels;
        using System.Threading.Tasks;

        namespace Conf
        {
            // A reader and writer which inherit the BCL's ReadAllAsync/ReadAsync/WriteAsync, and report the thread
            // their TryRead/TryWrite run on. A waiter's continuations run asynchronously, as the built-in channels'
            // do, so a BCL await on one resumes on the thread pool.
            public sealed class ProbeChannel
            {
                private readonly Queue<int> _items = new Queue<int>();
                private readonly Action _report;
                private TaskCompletionSource<bool> _readable;
                private TaskCompletionSource<bool> _writable;
                private bool _completed;
                private Exception _error;

                public ProbeChannel(Action report, int capacity)
                {
                    _report = report;
                    Capacity = capacity;
                    Reader = new ProbeReader(this);
                    Writer = new ProbeWriter(this);
                }

                public int Capacity { get; }

                public int Count => _items.Count;

                public int TryReads { get; private set; }

                public ChannelReader<int> Reader { get; }

                public ChannelWriter<int> Writer { get; }

                private static ValueTask<bool> Wait(ref TaskCompletionSource<bool> waiter)
                {
                    waiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return new ValueTask<bool>(waiter.Task);
                }

                private static void Signal(ref TaskCompletionSource<bool> waiter, bool result)
                {
                    TaskCompletionSource<bool> signalled = waiter;
                    waiter = null;
                    signalled?.TrySetResult(result);
                }

                private static void Fail(ref TaskCompletionSource<bool> waiter, Exception error)
                {
                    TaskCompletionSource<bool> signalled = waiter;
                    waiter = null;
                    signalled?.TrySetException(error);
                }

                private sealed class ProbeReader : ChannelReader<int>
                {
                    private readonly ProbeChannel _channel;

                    public ProbeReader(ProbeChannel channel) => _channel = channel;

                    public override bool TryRead(out int item)
                    {
                        _channel.TryReads++;
                        _channel._report();
                        if (!_channel._items.TryDequeue(out item)) return false;
                        Signal(ref _channel._writable, true);
                        return true;
                    }

                    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
                    {
                        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<bool>(cancellationToken);
                        if (_channel._items.Count > 0) return new ValueTask<bool>(true);
                        if (_channel._completed)
                        {
                            return _channel._error == null
                                ? new ValueTask<bool>(false)
                                : ValueTask.FromException<bool>(_channel._error);
                        }

                        return Wait(ref _channel._readable);
                    }
                }

                private sealed class ProbeWriter : ChannelWriter<int>
                {
                    private readonly ProbeChannel _channel;

                    public ProbeWriter(ProbeChannel channel) => _channel = channel;

                    public override bool TryWrite(int item)
                    {
                        _channel._report();
                        if (_channel._completed || _channel._items.Count >= _channel.Capacity) return false;
                        _channel._items.Enqueue(item);
                        Signal(ref _channel._readable, true);
                        return true;
                    }

                    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
                    {
                        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<bool>(cancellationToken);
                        if (_channel._completed) return new ValueTask<bool>(false);
                        if (_channel._items.Count < _channel.Capacity) return new ValueTask<bool>(true);
                        return Wait(ref _channel._writable);
                    }

                    public override bool TryComplete(Exception error = null)
                    {
                        if (_channel._completed) return false;
                        _channel._completed = true;
                        _channel._error = error;
                        if (error == null) Signal(ref _channel._readable, false);
                        else Fail(ref _channel._readable, error);
                        Signal(ref _channel._writable, false);
                        return true;
                    }
                }
            }

            // A reader and writer whose overrides count their calls and then call the base implementation, as a
            // decorator does.
            public sealed class CountingReader : ChannelReader<int>
            {
                private readonly ChannelReader<int> _inner;

                public CountingReader(ChannelReader<int> inner) => _inner = inner;

                public int Calls { get; private set; }

                public override bool TryRead(out int item) => _inner.TryRead(out item);

                public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
                    _inner.WaitToReadAsync(cancellationToken);

                public override ValueTask<int> ReadAsync(CancellationToken cancellationToken = default)
                {
                    Calls++;
                    return base.ReadAsync(cancellationToken);
                }

                public override IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken = default)
                {
                    Calls++;
                    return base.ReadAllAsync(cancellationToken);
                }
            }

            public sealed class CountingWriter : ChannelWriter<int>
            {
                private readonly ChannelWriter<int> _inner;

                public CountingWriter(ChannelWriter<int> inner) => _inner = inner;

                public int Calls { get; private set; }

                public override bool TryWrite(int item) => _inner.TryWrite(item);

                public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
                    _inner.WaitToWriteAsync(cancellationToken);

                public override bool TryComplete(Exception error = null) => _inner.TryComplete(error);

                public override ValueTask WriteAsync(int item, CancellationToken cancellationToken = default)
                {
                    Calls++;
                    return base.WriteAsync(item, cancellationToken);
                }
            }

            // A reader and writer whose TryRead/TryWrite throw, and whose waits always succeed.
            public sealed class ThrowingReader : ChannelReader<int>
            {
                private readonly Exception _error;

                public ThrowingReader(Exception error) => _error = error;

                public override bool TryRead(out int item) => throw _error;

                public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
                    new ValueTask<bool>(true);
            }

            public sealed class ThrowingWriter : ChannelWriter<int>
            {
                private readonly Exception _error;

                public ThrowingWriter(Exception error) => _error = error;

                public override bool TryWrite(int item) => throw _error;

                public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
                    new ValueTask<bool>(true);
            }

            public static class ChannelProbe
            {
                // Reads and writes through overrides which call the base implementations.
                public static async Task<string> ThroughOverrides(Action report)
                {
                    var channel = new ProbeChannel(report, capacity: 1);
                    var reader = new CountingReader(channel.Reader);
                    var writer = new CountingWriter(channel.Writer);
                    Task producer = Produce(writer, 3);
                    var read = new List<int> { await reader.ReadAsync() };
                    await foreach (int item in reader.ReadAllAsync())
                    {
                        read.Add(item);
                    }

                    await producer;
                    return string.Join(",", read) + "|" + reader.Calls + "," + writer.Calls;
                }

                // Writes `count` elements through WriteAsync, a virtual millisecond apart, and reads them with
                // ReadAllAsync or ReadAsync.
                public static async Task<string> ReadAll(Action report, int count, int capacity, bool readAsync)
                {
                    var channel = new ProbeChannel(report, capacity);
                    Task producer = Produce(channel.Writer, count);
                    var read = new List<int>();
                    if (readAsync)
                    {
                        try
                        {
                            while (true) read.Add(await channel.Reader.ReadAsync());
                        }
                        catch (ChannelClosedException)
                        {
                        }
                    }
                    else
                    {
                        await foreach (int item in channel.Reader.ReadAllAsync())
                        {
                            read.Add(item);
                        }
                    }

                    await producer;
                    return string.Join(",", read);
                }

                // Reads a built-in bounded channel with ReadAllAsync while a producer writes to it.
                public static async Task<string> ReadAllBuiltIn(int count, CancellationToken cancellationToken)
                {
                    Channel<int> channel = Channel.CreateBounded<int>(1);
                    Task producer = Produce(channel.Writer, count);
                    var read = new List<int>();
                    await foreach (int item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        read.Add(item);
                    }

                    await producer;
                    return string.Join(",", read);
                }

                public static async Task<string> CancelReadAll()
                {
                    Channel<int> channel = Channel.CreateUnbounded<int>();
                    using var source = new CancellationTokenSource();
                    var read = new List<int>();
                    channel.Writer.TryWrite(1);
                    try
                    {
                        await foreach (int item in channel.Reader.ReadAllAsync(source.Token))
                        {
                            read.Add(item);
                            source.Cancel();
                        }
                    }
                    catch (OperationCanceledException exception) when (exception.CancellationToken == source.Token)
                    {
                        read.Add(-1);
                    }

                    return string.Join(",", read);
                }

                // Starts an enumeration of a built-in channel, whose token is combined with the enumerator's, and takes
                // one step, leaving the enumerator for its caller to dispose.
                public static IAsyncEnumerator<int> StartEnumeration()
                {
                    Channel<int> channel = Channel.CreateUnbounded<int>();
                    channel.Writer.TryWrite(1);
                    var method = new CancellationTokenSource();
                    var enumeration = new CancellationTokenSource();
                    IAsyncEnumerator<int> enumerator = channel.Reader.ReadAllAsync(method.Token).GetAsyncEnumerator(enumeration.Token);
                    if (!enumerator.MoveNextAsync().IsCompletedSuccessfully) throw new InvalidOperationException("No element.");
                    return enumerator;
                }

                // Leaves a ReadAsync, WriteAsync, or ReadAllAsync step waiting on a probe channel, then makes it ready.
                // With `consume`, awaits the step and returns the awaiting task; otherwise returns the pending value
                // task, which the caller joins.
                public static object Escape(string member, Action report, bool consume)
                {
                    var channel = new ProbeChannel(report, capacity: 1);
                    switch (member)
                    {
                        case "ReadAsync":
                            ValueTask<int> read = channel.Reader.ReadAsync();
                            channel.Writer.TryWrite(1);
                            return consume ? Outcome(read) : read;
                        case "WriteAsync":
                            channel.Writer.TryWrite(0);
                            ValueTask write = channel.Writer.WriteAsync(1);
                            channel.Reader.TryRead(out _);
                            return consume ? Outcome(write) : write;
                        default:
                            ValueTask<bool> step = channel.Reader.ReadAllAsync().GetAsyncEnumerator().MoveNextAsync();
                            channel.Writer.TryWrite(1);
                            return consume ? Outcome(step) : step;
                    }
                }

                // Two consumers read a bounded channel through ReadAllAsync while a producer writes to it, tracing who
                // read each element; the scheduler chooses the interleaving.
                public static async Task<string> Interleave(int count)
                {
                    Channel<int> channel = Channel.CreateBounded<int>(1);
                    var trace = new List<string>();
                    Task first = Consume(channel.Reader, "a", trace);
                    Task second = Consume(channel.Reader, "b", trace);
                    for (int i = 0; i < count; i++)
                    {
                        await channel.Writer.WriteAsync(i);
                        trace.Add("w" + i);
                    }

                    channel.Writer.Complete();
                    await first;
                    await second;
                    return string.Join(",", trace);
                }

                // Scenarios whose outcomes the controlled run and the BCL must agree on. Each is driven by its own
                // steps, so the BCL run's outcome does not depend on the thread pool's timing.
                public static Task<string> Scenario(string name)
                {
                    string[] parts = name.Split('/');
                    switch (parts[0])
                    {
                        case "combine": return Combine(parts[1], parts.Length > 2);
                        case "precanceled": return PreCanceled(parts[1]);
                        case "throws": return Throws(parts[1], parts[2]);
                        case "closed": return Closed(parts[1]);
                        case "error": return CompletedWithError(parts[1], parts.Length > 2);
                        case "lazy": return Lazy();
                        case "builtin-read": return BuiltInRead();
                        case "dispose-pending": return DisposePending();
                        default: throw new ArgumentOutOfRangeException(nameof(name));
                    }
                }

                // Cancels the token passed to ReadAllAsync, the one passed to GetAsyncEnumerator, or one token passed to
                // both, before a step or while one waits.
                private static async Task<string> Combine(string canceled, bool pending)
                {
                    Channel<int> channel = Channel.CreateUnbounded<int>();
                    using var method = new CancellationTokenSource();
                    using var enumeration = new CancellationTokenSource();
                    CancellationToken enumerationToken = canceled == "same" ? method.Token : enumeration.Token;
                    CancellationTokenSource cancel = canceled == "enumeration" ? enumeration : method;
                    var log = new List<string>();
                    if (pending)
                    {
                        await using IAsyncEnumerator<int> enumerator =
                            channel.Reader.ReadAllAsync(method.Token).GetAsyncEnumerator(enumerationToken);
                        ValueTask<bool> step = enumerator.MoveNextAsync();
                        log.Add(Status(step));
                        cancel.Cancel();
                        log.Add(await Outcome(step, method.Token, enumeration.Token));
                        log.Add(await Outcome(enumerator.MoveNextAsync()));
                        return string.Join(",", log);
                    }

                    channel.Writer.TryWrite(1);
                    try
                    {
                        await foreach (int item in channel.Reader.ReadAllAsync(method.Token).WithCancellation(enumerationToken))
                        {
                            log.Add("item " + item);
                            cancel.Cancel();
                        }

                        log.Add("end");
                    }
                    catch (OperationCanceledException exception)
                    {
                        log.Add(Describe(exception, method.Token, enumeration.Token));
                    }

                    return string.Join(",", log);
                }

                // The base ReadAsync and WriteAsync check the token before they call TryRead or TryWrite.
                private static async Task<string> PreCanceled(string member)
                {
                    var channel = new ProbeChannel(() => { }, capacity: 2);
                    channel.Writer.TryWrite(5);
                    using var source = new CancellationTokenSource();
                    source.Cancel();
                    string outcome = member == "read"
                        ? await Outcome(channel.Reader.ReadAsync(source.Token), source.Token)
                        : await Outcome(channel.Writer.WriteAsync(6, source.Token), source.Token);
                    return outcome + "|reads " + channel.TryReads + ",count " + channel.Count;
                }

                // A TryRead or TryWrite which throws: a synchronous exception or a faulted step, and then the end.
                private static async Task<string> Throws(string member, string kind)
                {
                    Exception error =
                        kind == "invalid" ? new InvalidOperationException("boom") :
                        kind == "closed" ? (Exception)new ChannelClosedException("closed") :
                        new OperationCanceledException("stop");
                    if (member == "read") return await Start(() => new ThrowingReader(error).ReadAsync());
                    if (member == "write") return await Start(() => new ThrowingWriter(error).WriteAsync(1));

                    await using IAsyncEnumerator<int> enumerator = new ThrowingReader(error).ReadAllAsync().GetAsyncEnumerator();
                    ValueTask<bool> step = enumerator.MoveNextAsync();
                    string first = Status(step) + ":" + await Outcome(step);
                    return first + "|" + await Outcome(enumerator.MoveNextAsync()) + "," + enumerator.Current;
                }

                // ReadAsync from, and WriteAsync to, a completed channel.
                private static async Task<string> Closed(string member)
                {
                    var channel = new ProbeChannel(() => { }, capacity: 1);
                    channel.Writer.Complete();
                    return member == "read"
                        ? await Start(() => channel.Reader.ReadAsync())
                        : await Start(() => channel.Writer.WriteAsync(1));
                }

                // A channel completed with an error, before a step or while one waits.
                private static async Task<string> CompletedWithError(string member, bool pending)
                {
                    var error = new InvalidOperationException("broken");
                    var log = new List<string>();
                    if (member == "read")
                    {
                        var probe = new ProbeChannel(() => { }, capacity: 1);
                        ValueTask<int> read = probe.Reader.ReadAsync();
                        log.Add(Status(read));
                        probe.Writer.Complete(error);
                        log.Add(await Outcome(read));
                        log.Add(await Outcome(probe.Reader.ReadAsync()));
                        return string.Join(",", log);
                    }

                    Channel<int> channel = Channel.CreateUnbounded<int>();
                    await using IAsyncEnumerator<int> enumerator = channel.Reader.ReadAllAsync().GetAsyncEnumerator();
                    if (pending)
                    {
                        ValueTask<bool> step = enumerator.MoveNextAsync();
                        log.Add(Status(step));
                        channel.Writer.Complete(error);
                        log.Add(await Outcome(step));
                    }
                    else
                    {
                        channel.Writer.TryWrite(1);
                        channel.Writer.Complete(error);
                        log.Add(await Outcome(enumerator.MoveNextAsync()) + " " + enumerator.Current);
                        log.Add(await Outcome(enumerator.MoveNextAsync()));
                    }

                    log.Add(await Outcome(enumerator.MoveNextAsync()) + " " + enumerator.Current);
                    return string.Join(",", log);
                }

                // ReadAllAsync reads nothing until a step: neither the call nor GetAsyncEnumerator calls TryRead.
                private static async Task<string> Lazy()
                {
                    var channel = new ProbeChannel(() => { }, capacity: 2);
                    channel.Writer.TryWrite(1);
                    channel.Writer.TryWrite(2);
                    IAsyncEnumerable<int> all = channel.Reader.ReadAllAsync();
                    var log = new List<string> { "reads " + channel.TryReads };
                    await using IAsyncEnumerator<int> enumerator = all.GetAsyncEnumerator();
                    log.Add("reads " + channel.TryReads + ",count " + channel.Count);
                    log.Add(await Outcome(enumerator.MoveNextAsync()) + " " + enumerator.Current);
                    log.Add("reads " + channel.TryReads + ",count " + channel.Count);
                    return string.Join(",", log);
                }

                // A built-in channel's own ReadAsync, waiting and then on a completed channel.
                private static async Task<string> BuiltInRead()
                {
                    Channel<int> channel = Channel.CreateBounded<int>(1);
                    ValueTask<int> read = channel.Reader.ReadAsync();
                    var log = new List<string> { Status(read) };
                    channel.Writer.TryWrite(4);
                    log.Add(await Outcome(read));
                    channel.Writer.Complete();
                    log.Add(await Outcome(channel.Reader.ReadAsync()));
                    return string.Join(",", log);
                }

                // DisposeAsync while a step waits, then after it completes, then a step after disposal.
                private static async Task<string> DisposePending()
                {
                    Channel<int> channel = Channel.CreateUnbounded<int>();
                    IAsyncEnumerator<int> enumerator = channel.Reader.ReadAllAsync().GetAsyncEnumerator();
                    ValueTask<bool> step = enumerator.MoveNextAsync();
                    var log = new List<string> { Status(step) };
                    log.Add(await Start(() => enumerator.DisposeAsync()));
                    channel.Writer.TryWrite(3);
                    log.Add(await Outcome(step) + " " + enumerator.Current);
                    log.Add(await Start(() => enumerator.DisposeAsync()));
                    log.Add(await Outcome(enumerator.MoveNextAsync()) + " " + enumerator.Current);
                    return string.Join(",", log);
                }

                private static async Task Produce(ChannelWriter<int> writer, int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        await Task.Delay(1);
                        await writer.WriteAsync(i);
                    }

                    writer.Complete();
                }

                private static async Task Consume(ChannelReader<int> reader, string name, List<string> trace)
                {
                    await foreach (int item in reader.ReadAllAsync())
                    {
                        trace.Add(name + item);
                    }
                }

                // Calls a member, telling a synchronous exception from the returned value task's outcome.
                private static Task<string> Start<T>(Func<ValueTask<T>> call)
                {
                    ValueTask<T> pending;
                    try
                    {
                        pending = call();
                    }
                    catch (Exception exception)
                    {
                        return Task.FromResult("threw " + Describe(exception));
                    }

                    return Outcome(pending);
                }

                private static Task<string> Start(Func<ValueTask> call)
                {
                    ValueTask pending;
                    try
                    {
                        pending = call();
                    }
                    catch (Exception exception)
                    {
                        return Task.FromResult("threw " + Describe(exception));
                    }

                    return Outcome(pending);
                }

                // The task the value task converts to, as its final status and result or exception.
                private static async Task<string> Outcome<T>(ValueTask<T> pending, params CancellationToken[] tokens)
                {
                    Task<T> task = pending.AsTask();
                    try
                    {
                        return "ok " + await task;
                    }
                    catch (Exception exception)
                    {
                        return task.Status + " " + Describe(exception, tokens);
                    }
                }

                private static async Task<string> Outcome(ValueTask pending, params CancellationToken[] tokens)
                {
                    Task task = pending.AsTask();
                    try
                    {
                        await task;
                        return "ok";
                    }
                    catch (Exception exception)
                    {
                        return task.Status + " " + Describe(exception, tokens);
                    }
                }

                // Only for a value task whose completion this probe controls, so the BCL run reports it deterministically.
                private static string Status<T>(ValueTask<T> pending) =>
                    pending.IsCompletedSuccessfully ? "completed" :
                    pending.IsCanceled ? "canceled" :
                    pending.IsFaulted ? "faulted" :
                    "pending";

                // An exception's type and message, and for a cancellation which of `tokens` it carries.
                private static string Describe(Exception exception, params CancellationToken[] tokens)
                {
                    string description = exception.GetType().Name + "(" + exception.Message + ")";
                    if (exception is OperationCanceledException canceled)
                    {
                        int index = Array.IndexOf(tokens, canceled.CancellationToken);
                        description += index >= 0 ? " token " + index
                            : canceled.CancellationToken.CanBeCanceled ? " other token"
                            : " no token";
                    }

                    return description;
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public ChannelConformanceTests()
    {
        _release = new(() => _fixture.StageControlledTasks("Conf.ChannelRel", ProbeType, Source, optimize: true));
        _debug = new(() => _fixture.StageControlledTasks("Conf.ChannelDbg", ProbeType, Source, optimize: false));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.ChannelBcl", ProbeType, Source));
    }

    public static TheoryData<bool, bool> OptimizeAndReadAsync => new()
    {
        { true, true },
        { true, false },
        { false, true },
        { false, false },
    };

    public static TheoryData<string> Scenarios =>
    [
        "combine/method",
        "combine/enumeration",
        "combine/same",
        "combine/method/pending",
        "combine/enumeration/pending",
        "combine/same/pending",
        "precanceled/read",
        "precanceled/write",
        "throws/read/invalid",
        "throws/read/closed",
        "throws/read/canceled",
        "throws/write/invalid",
        "throws/write/closed",
        "throws/write/canceled",
        "throws/readall/invalid",
        "throws/readall/closed",
        "throws/readall/canceled",
        "closed/read",
        "closed/write",
        "error/read",
        "error/readall",
        "error/readall/pending",
        "lazy",
        "builtin-read",
        "dispose-pending",
    ];

    [Theory]
    [MemberData(nameof(OptimizeAndReadAsync))]
    public void InheritedReadAndWriteRunOnTheLogicalThread(bool optimize, bool readAsync)
    {
        using var host = new SimulationHost(Start);
        var observed = new ConcurrentQueue<bool>();

        // A capacity of one makes the writer wait for the reader as well.
        var task = (Task<string>)host.Invoke(Method("ReadAll", optimize), Report(host, observed), 5, 1, readAsync)!;

        Assert.Equal("0,1,2,3,4", Completed(task));
        Assert.NotEmpty(observed);
        Assert.All(observed, Assert.True);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverridesWhichCallTheirBaseRunTheBaseImplementationOnTheLogicalThread(bool optimize)
    {
        using var host = new SimulationHost(Start);
        var observed = new ConcurrentQueue<bool>();

        var task = (Task<string>)host.Invoke(Method("ThroughOverrides", optimize), Report(host, observed))!;

        Assert.Equal("0,1,2|2,3", Completed(task));
        Assert.NotEmpty(observed);
        Assert.All(observed, Assert.True);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuiltInChannelReadAllAsyncDeliversEveryElementInOrder(bool optimize)
    {
        using var host = new SimulationHost(Start);

        var task = (Task<string>)host.Invoke(Method("ReadAllBuiltIn", optimize), 5, CancellationToken.None)!;

        Assert.Equal("0,1,2,3,4", Completed(task));
    }

    [Fact]
    public void ReadAllAsyncObservesItsToken()
    {
        using var host = new SimulationHost(Start);

        var task = (Task<string>)host.Invoke(Method("CancelReadAll"))!;

        Assert.Equal("1,-1", Completed(task));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ScenarioHasTheBclOutcome(string scenario)
    {
        string controlled;

        // A built-in channel completes a wait which its token cancels on the thread pool (#59).
        using (var host = new SimulationHost(Start, isKnownEscape: IsAChannelsCancellationCallback))
        {
            controlled = Completed((Task<string>)host.Invoke(Method("Scenario"), scenario)!);
        }

        var bcl = (Task<string>)_bcl.Value.Method("Scenario").Invoke(null, [scenario])!;

        Assert.Equal(await bcl.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), controlled);
    }

    // The #59 escape: a built-in channel's cancellation callback, which CancellationTokenSource runs, queues the canceled
    // wait's completion. The frames between the thread pool's and ExecuteCallbackHandlers are all the channel assembly's
    // (its queueing helper is a forwarder the JIT may inline); any other enqueue, such as a channel's own race in
    // OnCompleted, is not this escape.
    private static bool IsAChannelsCancellationCallback(StackTrace stack)
    {
        StackFrame[] frames = stack.GetFrames();
        int frame = Array.FindIndex(frames, candidate => candidate.GetMethod()?.DeclaringType == typeof(ThreadPool));
        if (frame < 0)
        {
            return false;
        }

        while (frame < frames.Length && frames[frame].GetMethod()?.DeclaringType == typeof(ThreadPool))
        {
            frame++;
        }

        int channelFrames = 0;
        while (frame < frames.Length && frames[frame].GetMethod()?.DeclaringType?.Assembly == typeof(System.Threading.Channels.Channel).Assembly)
        {
            frame++;
            channelFrames++;
        }

        return channelFrames > 0
            && frame < frames.Length
            && frames[frame].GetMethod() is { Name: "ExecuteCallbackHandlers" } caller
            && caller.DeclaringType == typeof(CancellationTokenSource);
    }

    [Fact]
    public async Task ReadAllAsyncEnumeratorDisposesAfterTheSimulationEnds()
    {
        IAsyncEnumerator<int> enumerator;
        using (var host = new SimulationHost(Start))
        {
            enumerator = (IAsyncEnumerator<int>)host.Invoke(Method("StartEnumeration"))!;
        }

        ValueTask disposal = enumerator.DisposeAsync();

        Assert.True(disposal.IsCompletedSuccessfully);
        await disposal;
    }

    [Fact]
    public void SeedReplaysItsProducerConsumerInterleavingAndSeedsChooseDifferentInterleavings()
    {
        const int Count = 6;
        MethodInfo method = Method("Interleave");
        var traces = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 1; seed <= 8; seed++)
        {
            var recorded = new List<(ReplayExecutionResult Result, string Trace)>();
            for (int recording = 0; recording < 2; recording++)
            {
                var probe = new StrongBox<Task<string>?>();
                ReplayExecutionResult result = ReplayRunner.RecordCluster(
                    SeededRandom(seed),
                    cluster => Schedule(cluster, method, Count, probe),
                    TestContext.Current.CancellationToken);
                Assert.Equal(ReplayTerminationKind.Completed, result.Artifact.Outcome.Kind);
                recorded.Add((result, Completed(Assert.IsType<Task<string>>(probe.Value, exactMatch: false))));
            }

            Assert.Equal(recorded[0].Trace, recorded[1].Trace);
            Assert.Equal(recorded[0].Result.ArtifactId, recorded[1].Result.ArtifactId);
            AssertEachElementReadOnceInOrder(recorded[0].Trace, Count);

            var replayedProbe = new StrongBox<Task<string>?>();
            ReplayExecutionResult replayed = ReplayRunner.ReplayCluster(
                recorded[0].Result.Artifact,
                ReplayCompatibilityRequirements.Current(),
                cluster => Schedule(cluster, method, Count, replayedProbe),
                maxSteps: 1_000_000,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(replayed.Reproduced);
            Assert.Equal(recorded[0].Trace, Completed(Assert.IsType<Task<string>>(replayedProbe.Value, exactMatch: false)));
            traces.Add(recorded[0].Trace);
        }

        Assert.True(traces.Count > 1, $"Eight seeds chose one interleaving: {string.Join(" | ", traces)}");
    }

    [Theory]
    [InlineData("clockwork.channels.reader.readallasync", "ReadAllAsync")]
    [InlineData("clockwork.channels.reader.readasync", "ReadAsync")]
    [InlineData("clockwork.channels.writer.writeasync", "WriteAsync")]
    public void WithoutItsRuleTheMemberRunsItsStepOnTheThreadPool(string ruleId, string member)
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.ChannelEscape" + member,
            ProbeType,
            Source,
            rule => rule.Id == ruleId);

        bool[] controlled = ReportThreads(Method("Escape"), member, escaped: false);
        bool[] escaped = ReportThreads(escaping.Method("Escape"), member, escaped: true);

        Assert.NotEmpty(controlled);
        Assert.All(controlled, Assert.True);
        Assert.Contains(false, escaped);
    }

    // Reports, for each TryRead and TryWrite of the probe's step, whether it ran on the simulation's logical thread.
    // An escaped step completes on the thread pool after the drive returns, so it is joined before the host is disposed.
    private static bool[] ReportThreads(MethodInfo escape, string member, bool escaped)
    {
        // The guard is off: the escape this test pins runs the step on the thread pool.
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);
        var observed = new ConcurrentQueue<bool>();

        object? step = host.Invoke(escape, member, Report(host, observed), !escaped);

        if (escaped)
        {
            Task joined = step switch
            {
                ValueTask<int> read => read.AsTask(),
                ValueTask<bool> moved => moved.AsTask(),
                ValueTask written => written.AsTask(),
                _ => throw new InvalidOperationException($"Unexpected step {step}."),
            };
            Assert.True(joined.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal(TaskStatus.RanToCompletion, joined.Status);
        }
        else
        {
            Assert.StartsWith("ok", Completed(Assert.IsType<Task<string>>(step, exactMatch: false)), StringComparison.Ordinal);
        }

        return [.. observed];
    }

    private static void Schedule(SimulationCluster cluster, MethodInfo method, int count, StrongBox<Task<string>?> probe)
    {
        SimulationNode node = cluster.AddNode("node");
        node.Context.SchedulerLane.Enqueue(() => probe.Value = (Task<string>)method.Invoke(null, [count])!);
    }

    // Each element is written once and read once, by one consumer, and each consumer reads in the order written.
    private static void AssertEachElementReadOnceInOrder(string trace, int count)
    {
        string[] entries = trace.Split(',');
        for (int item = 0; item < count; item++)
        {
            Assert.Single(entries, entry => entry == "w" + item);
            Assert.Single(entries, entry => entry[0] is 'a' or 'b' && entry[1..] == item.ToString(CultureInfo.InvariantCulture));
        }

        foreach (char consumer in "ab")
        {
            int[] read = [.. entries.Where(entry => entry[0] == consumer).Select(entry => int.Parse(entry[1..], CultureInfo.InvariantCulture))];
            Assert.Equal(read.Order(), read);
        }
    }

    private static ReplayRecordingOptions SeededRandom(int seed) => new()
    {
        SimulationSeed = 1,
        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
        ScheduleSeed = seed,
        MaxSteps = 1_000_000,
    };

    // Reports, for each TryRead and TryWrite, whether it ran on the simulation's logical thread.
    private static Action Report(SimulationHost host, ConcurrentQueue<bool> observed) =>
        () => observed.Enqueue(host.Scheduler.IsSimulationThread);

    private static string Completed(Task<string> task)
    {
        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        return task.GetAwaiter().GetResult();
    }

    private MethodInfo Method(string name, bool optimize = true) => (optimize ? _release : _debug).Value.Method(name);

    public void Dispose() => _fixture.Dispose();
}
