using System.Collections.Concurrent;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled asynchronous members of <c>Stream</c>, <c>TextReader</c>, and
/// <c>TextWriter</c>. The BCL's base implementations, which a type that overrides only the synchronous members inherits,
/// run those members on the thread pool, block on a semaphore or task, or await through BCL awaiters. Rewritten, every
/// synchronous read, write, and flush runs on the simulation's logical thread, and each scenario returns what the same
/// probe returns uninstrumented, on real threads: results, serialization order, cancellation, exceptions and their
/// messages, base calls from overrides, and the shapes Clog's streams use. Rewritten without the rules, the base
/// <c>ReadAsync</c> reads on a thread-pool thread.
/// </summary>
public sealed class StreamConformanceTests : IDisposable
{
    private const string ProbeType = "Conf.StreamProbe";

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Buffers;
        using System.Collections.Generic;
        using System.IO;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Conf
        {
            // Overrides only the synchronous members, so every asynchronous member is the BCL's base. Each read returns
            // the next byte of a counter, one byte at a time.
            public class SyncStream : Stream
            {
                private readonly Action _report;
                private readonly List<string> _log;
                private byte _next = 1;

                public SyncStream(Action report, List<string> log, int length = 1000)
                {
                    _report = report;
                    _log = log;
                    Remaining = length;
                }

                public int Remaining { get; private set; }

                public bool Readable { get; set; } = true;

                public bool Writable { get; set; } = true;

                public Exception ReadFailure { get; set; }

                public override bool CanRead => Readable;
                public override bool CanSeek => false;
                public override bool CanWrite => Writable;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

                public override void Flush()
                {
                    _report();
                    _log.Add("flush");
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    _report();
                    if (ReadFailure != null) throw ReadFailure;
                    if (count == 0 || Remaining == 0) return 0;
                    buffer[offset] = _next++;
                    Remaining--;
                    _log.Add("read" + buffer[offset]);
                    return 1;
                }

                public override void Write(byte[] buffer, int offset, int count)
                {
                    _report();
                    _log.Add("write" + string.Join(".", new ArraySegment<byte>(buffer, offset, count)));
                }

                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
            }

            // Overrides ReadAsync and the Begin/End pairs with calls to their base implementations, as a decorator does.
            public sealed class BaseCallingStream : SyncStream
            {
                public BaseCallingStream(Action report, List<string> log) : base(report, log) { }

                public int Calls { get; private set; }

                public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                {
                    Calls++;
                    return base.ReadAsync(buffer, offset, count, cancellationToken);
                }

                public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
                {
                    Calls++;
                    return base.BeginWrite(buffer, offset, count, callback, state);
                }

                public override void EndWrite(IAsyncResult asyncResult)
                {
                    Calls++;
                    base.EndWrite(asyncResult);
                }
            }

            // The shape of Clog's ArcBufferReadStream: synchronous ReadAsync overrides over a locked cursor.
            public sealed class CursorReadStream : Stream
            {
                private readonly object _sync = new object();
                private readonly byte[] _content;
                private int _position;

                public CursorReadStream(byte[] content) => _content = content;

                public override bool CanRead => true;
                public override bool CanSeek => true;
                public override bool CanWrite => false;
                public override long Length => _content.Length;
                public override long Position { get => _position; set => _position = (int)value; }
                public override void Flush() { }
                public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

                public override int Read(Span<byte> buffer)
                {
                    lock (_sync)
                    {
                        int count = Math.Min(buffer.Length, Math.Min(3, _content.Length - _position));
                        _content.AsSpan(_position, count).CopyTo(buffer);
                        _position += count;
                        return count;
                    }
                }

                public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(Read(buffer.AsSpan(offset, count)));
                }

                public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(Read(buffer.Span));
                }

                public override long Seek(long offset, SeekOrigin origin) => _position = (int)offset;
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            }

            // The shape of Clog's ArcBufferWriteStream: synchronous WriteAsync and FlushAsync overrides.
            public sealed class AppendWriteStream : Stream
            {
                public List<byte> Written { get; } = new List<byte>();

                public override bool CanRead => false;
                public override bool CanSeek => false;
                public override bool CanWrite => true;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => Written.Count; set => throw new NotSupportedException(); }
                public override void Flush() { }
                public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
                public override void Write(ReadOnlySpan<byte> buffer) => Written.AddRange(buffer.ToArray());

                public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                    WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

                public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Write(buffer.Span);
                    return ValueTask.CompletedTask;
                }

                public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }

            // A reader over a string which overrides only Peek and Read().
            public sealed class ProbeReader : TextReader
            {
                private readonly Action _report;
                private readonly string _text;
                private int _position;

                public ProbeReader(Action report, string text)
                {
                    _report = report;
                    _text = text;
                }

                public override int Peek() => _position < _text.Length ? _text[_position] : -1;

                public override int Read()
                {
                    _report();
                    return _position < _text.Length ? _text[_position++] : -1;
                }
            }

            // A writer into a StringBuilder which overrides only Write(char) and Flush.
            public sealed class ProbeWriter : TextWriter
            {
                private readonly Action _report;

                public ProbeWriter(Action report) => _report = report;

                public StringBuilder Text { get; } = new StringBuilder();

                public override Encoding Encoding => Encoding.UTF8;

                public override void Write(char value)
                {
                    _report();
                    Text.Append(value);
                }

                public override void Flush()
                {
                    _report();
                    Text.Append("|flush|");
                }
            }

            public static class StreamProbe
            {
                public static async Task<string> BaseStreamMembers(Action report)
                {
                    var log = new List<string>();
                    var stream = new SyncStream(report, log);
                    var buffer = new byte[4];
                    log.Add("n" + await stream.ReadAsync(buffer, 0, 4));
                    log.Add("n" + await stream.ReadAsync(buffer, 0, 4, CancellationToken.None));
                    log.Add("n" + await stream.ReadAsync(buffer.AsMemory(1, 2)));
                    await stream.WriteAsync(new byte[] { 7, 8 }, 0, 2);
                    await stream.WriteAsync(new byte[] { 9 }, 0, 1, CancellationToken.None);
                    await stream.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 5, 6 }));
                    await stream.FlushAsync();
                    await stream.FlushAsync(CancellationToken.None);
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, 2));
                    await stream.ReadExactlyAsync(buffer, 1, 2);
                    log.Add("n" + await stream.ReadAtLeastAsync(buffer, 3, throwOnEndOfStream: false));
                    var limited = new SyncStream(report, log, length: 5);
                    var destination = new MemoryStream();
                    await limited.CopyToAsync(destination);
                    await limited.CopyToAsync(destination, 16);
                    await limited.CopyToAsync(destination, CancellationToken.None);
                    log.Add("copied" + string.Join(".", destination.ToArray()));

                    var readCallback = new TaskCompletionSource<string>();
                    var writeCallback = new TaskCompletionSource<string>();
                    IAsyncResult read = stream.BeginRead(buffer, 0, 1, result => readCallback.SetResult("read:" + result.AsyncState + result.IsCompleted), "r");
                    log.Add("n" + stream.EndRead(read));
                    IAsyncResult write = stream.BeginWrite(buffer, 0, 1, result => writeCallback.SetResult("write:" + result.AsyncState + result.IsCompleted), "w");
                    stream.EndWrite(write);
                    log.Add(read.AsyncState + "," + write.AsyncState + "," + read.CompletedSynchronously + "," + read.IsCompleted);
                    await stream.DisposeAsync();
                    return string.Join(" ", log) + " |" + await readCallback.Task + "," + await writeCallback.Task;
                }

                // Reads and writes issued without awaiting run one at a time, in order.
                public static async Task<string> Serialized(Action report)
                {
                    var log = new List<string>();
                    var stream = new SyncStream(report, log);
                    var buffers = new[] { new byte[1], new byte[1], new byte[1] };
                    Task<int> first = stream.ReadAsync(buffers[0], 0, 1);
                    Task second = stream.WriteAsync(new byte[] { 42 }, 0, 1);
                    Task<int> third = stream.ReadAsync(buffers[1], 0, 1);
                    IAsyncResult fourth = stream.BeginRead(buffers[2], 0, 1, null, null);
                    int last = stream.EndRead(fourth);
                    await Task.WhenAll(first, second, third);
                    return string.Join(" ", log) + "|" + buffers[0][0] + buffers[1][0] + buffers[2][0] + last;
                }

                public static async Task<string> Cancellation(Action report)
                {
                    var log = new List<string>();
                    var stream = new SyncStream(report, log);
                    var canceled = new CancellationToken(true);
                    Task<int> read = stream.ReadAsync(new byte[1], 0, 1, canceled);
                    Task write = stream.WriteAsync(new byte[1], 0, 1, canceled);
                    Task flush = stream.FlushAsync(canceled);
                    var outcomes = new List<string> { read.Status.ToString(), write.Status.ToString() };
                    try
                    {
                        await flush;
                    }
                    catch (OperationCanceledException exception)
                    {
                        outcomes.Add(flush.Status + ":" + (exception.CancellationToken == canceled));
                    }

                    try
                    {
                        await stream.ReadAsync(new byte[1].AsMemory(), canceled);
                    }
                    catch (OperationCanceledException exception)
                    {
                        outcomes.Add(exception.GetType().Name + (exception.CancellationToken == canceled));
                    }

                    await stream.FlushAsync();
                    return string.Join(",", outcomes) + "|" + string.Join(" ", log);
                }

                public static async Task<string> Failures(Action report)
                {
                    var results = new List<string>();
                    async Task Record(Func<Task> action)
                    {
                        try
                        {
                            await action();
                            results.Add("none");
                        }
                        catch (Exception exception)
                        {
                            results.Add(exception.GetType().Name + ":" + exception.Message);
                        }
                    }

                    var log = new List<string>();
                    var failing = new SyncStream(report, log) { ReadFailure = new InvalidDataException("bad") };
                    await Record(() => failing.ReadAsync(new byte[1], 0, 1));
                    var unreadable = new SyncStream(report, log) { Readable = false };
                    await Record(() => { unreadable.ReadAsync(new byte[1], 0, 1); return Task.CompletedTask; });
                    await Record(() => { unreadable.BeginRead(new byte[1], 0, 1, null, null); return Task.CompletedTask; });
                    await Record(() => unreadable.CopyToAsync(new MemoryStream()));
                    var closed = new SyncStream(report, log) { Readable = false, Writable = false };
                    await Record(() => closed.CopyToAsync(new MemoryStream()));
                    var unwritable = new SyncStream(report, log) { Writable = false };
                    await Record(() => { unwritable.WriteAsync(new byte[1], 0, 1); return Task.CompletedTask; });
                    var stream = new SyncStream(report, log, length: 1);
                    await Record(() => stream.ReadExactlyAsync(new byte[2]).AsTask());
                    await Record(() => stream.ReadAtLeastAsync(new byte[1], 2).AsTask());
                    await Record(() => { stream.ReadAsync(null, 0, 1); return Task.CompletedTask; });
                    await Record(() => { stream.ReadAsync(new byte[1], 0, 2); return Task.CompletedTask; });
                    await Record(() => stream.CopyToAsync(new MemoryStream(), 0));
                    IAsyncResult read = new SyncStream(report, log).BeginRead(new byte[1], 0, 1, null, null);
                    await Record(() => { stream.EndWrite(read); return Task.CompletedTask; });
                    var reader = new SyncStream(report, log);
                    IAsyncResult own = reader.BeginRead(new byte[1], 0, 1, null, null);
                    reader.EndRead(own);
                    await Record(() => { reader.EndRead(own); return Task.CompletedTask; });
                    await Record(() => { reader.EndRead(null); return Task.CompletedTask; });
                    await Record(() => { reader.EndRead(Task.FromResult(1)); return Task.CompletedTask; });
                    return string.Join("\n", results);
                }

                public static async Task<string> BaseCalls(Action report)
                {
                    var log = new List<string>();
                    var stream = new BaseCallingStream(report, log);
                    int read = await stream.ReadAsync(new byte[1], 0, 1);
                    await stream.WriteAsync(new byte[] { 3 }, 0, 1);
                    return read + "|" + stream.Calls + "|" + string.Join(" ", log);
                }

                // Clog's uses: ReadExactlyAsync and CopyToAsync over its streams, and a ReadAsync(Memory) copy loop.
                public static async Task<string> ClogShapes()
                {
                    byte[] content = new byte[20];
                    for (int i = 0; i < content.Length; i++) content[i] = (byte)i;
                    var exact = new byte[7];
                    await new CursorReadStream(content).ReadExactlyAsync(exact);
                    var sink = new AppendWriteStream();
                    await new CursorReadStream(content).CopyToAsync(sink);
                    var source = new CursorReadStream(content);
                    var loop = new AppendWriteStream();
                    var memory = new byte[4];
                    int read;
                    while ((read = await source.ReadAsync(memory.AsMemory())) != 0)
                    {
                        await loop.WriteAsync(memory.AsMemory(0, read));
                    }

                    await loop.FlushAsync();
                    return string.Join(".", exact) + "|" + sink.Written.Count + "|" + string.Join(".", loop.Written);
                }

                public static async Task<string> TextMembers(Action report)
                {
                    var results = new List<string>();
                    results.Add(await new ProbeReader(report, "ab\ncd").ReadLineAsync());
                    results.Add(await new ProbeReader(report, "ef\ngh").ReadLineAsync(CancellationToken.None));
                    results.Add(await new ProbeReader(report, "ij").ReadToEndAsync());
                    results.Add(await new ProbeReader(report, "kl").ReadToEndAsync(CancellationToken.None));
                    var chars = new char[4];
                    int n = await new ProbeReader(report, "mnop").ReadAsync(chars, 0, 3);
                    results.Add(n + new string(chars, 0, n));
                    n = await new ProbeReader(report, "qr").ReadAsync(chars.AsMemory(1, 2));
                    results.Add(n + new string(chars, 0, 3));
                    n = await new ProbeReader(report, "stuv").ReadBlockAsync(chars, 0, 4);
                    results.Add(n + new string(chars));
                    n = await new ProbeReader(report, "wx").ReadBlockAsync(chars.AsMemory());
                    results.Add(n + new string(chars));

                    var writer = new ProbeWriter(report);
                    writer.NewLine = "/";
                    await writer.WriteAsync('a');
                    await writer.WriteAsync(new Rune(0x1F600));
                    await writer.WriteAsync(new Rune('b'));
                    await writer.WriteAsync("cd");
                    await writer.WriteAsync("ef", CancellationToken.None);
                    await writer.WriteAsync(new StringBuilder("gh"));
                    await writer.WriteAsync(new[] { 'i', 'j' });
                    await writer.WriteAsync(new[] { 'k', 'l', 'm' }, 1, 1);
                    await writer.WriteAsync("no".AsMemory());
                    await writer.WriteLineAsync('p');
                    await writer.WriteLineAsync(new Rune(0x1F601));
                    await writer.WriteLineAsync("q");
                    await writer.WriteLineAsync("r", CancellationToken.None);
                    await writer.WriteLineAsync(new StringBuilder("s"));
                    await writer.WriteLineAsync((StringBuilder)null);
                    await writer.WriteLineAsync(new[] { 't' });
                    await writer.WriteLineAsync((char[])null);
                    await writer.WriteLineAsync(new[] { 'u', 'v' }, 1, 1);
                    await writer.WriteLineAsync("w".AsMemory());
                    await writer.WriteLineAsync();
                    await writer.WriteLineAsync(CancellationToken.None);
                    await writer.FlushAsync();
                    await writer.FlushAsync(CancellationToken.None);
                    Task canceled = writer.WriteAsync("x".AsMemory(), new CancellationToken(true));
                    results.Add(writer.Text + "|" + canceled.Status);
                    return string.Join(" ", results);
                }

                public static async Task<int> ReadOnce(Action report)
                {
                    var stream = new SyncStream(report, new List<string>());
                    return await stream.ReadAsync(new byte[1], 0, 1);
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;
    private readonly Lazy<UninstrumentedProbe> _bcl;

    public StreamConformanceTests()
    {
        _probe = new(() => _fixture.StageControlledTasks("Conf.Stream", ProbeType, Source, optimize: true));
        _bcl = new(() => _fixture.CompileUninstrumented("Conf.StreamBcl", ProbeType, Source));
    }

    public static TheoryData<string> Scenarios => ["BaseStreamMembers", "Serialized", "Cancellation", "Failures", "BaseCalls", "TextMembers"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ScenarioRunsOnTheLogicalThreadAndReturnsWhatTheBclReturns(string scenario)
    {
        string expected = await (Task<string>)_bcl.Value.Method(scenario).Invoke(null, [(Action)(() => { })])!;
        using var host = new SimulationHost(Start);
        var observed = new ConcurrentQueue<bool>();

        string actual = Completed(host.Invoke(_probe.Value.Method(scenario), (Action)(() => observed.Enqueue(host.Scheduler.IsSimulationThread))));

        Assert.Equal(expected, actual);
        Assert.NotEmpty(observed);
        Assert.All(observed, Assert.True);
    }

    [Fact]
    public async Task ClogStreamShapesReturnWhatTheBclReturns()
    {
        string expected = await (Task<string>)_bcl.Value.Method("ClogShapes").Invoke(null, null)!;
        using var host = new SimulationHost(Start);

        Assert.Equal(expected, Completed(host.Invoke(_probe.Value.Method("ClogShapes"))));
    }

    [Fact]
    public void WithoutTheRulesTheBaseReadAsyncReadsOnTheThreadPool()
    {
        StagedProbe escaping = _fixture.StageControlledTasksWithout(
            "Conf.StreamEscape",
            ProbeType,
            Source,
            rule => rule.Id.StartsWith("clockwork.io.", StringComparison.Ordinal));
        // The guard is off: the escape this test pins queues the read to the thread pool.
        using var host = new SimulationHost(Start, allowThreadPoolEscapes: true);
        var observed = new ConcurrentQueue<bool>();

        _ = host.Invoke(escaping.Method("ReadOnce"), (Action)(() => observed.Enqueue(host.Scheduler.IsSimulationThread)));

        Assert.True(SpinWait.SpinUntil(() => !observed.IsEmpty, TimeSpan.FromSeconds(30)));
        Assert.Equal([false], observed);
    }

    private static string Completed(object? task)
    {
        var typed = Assert.IsType<Task<string>>(task, exactMatch: false);
        Assert.Equal(TaskStatus.RanToCompletion, typed.Status);
        return typed.GetAwaiter().GetResult();
    }

    public void Dispose() => _fixture.Dispose();
}
