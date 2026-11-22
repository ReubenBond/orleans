using System.Buffers;
using System.Collections.Immutable;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// <see cref="ArchiveLogWriter"/> (archive-logs.md §6, §8.2; LOG-I1): each write's requests planned from its token alone;
/// a failed condition decided by one properties read; failures before and after a request lands, which it retries as
/// they are; retries which meet their own landing; failed properties reads; and crashes mid-run and between an opener's
/// two requests, after which a later writer completes the write; contradictions no conforming store leaves, refusals,
/// and the caller's cancellation, none of which it retries. Retries run on a fake clock with no delay or jitter.
/// </summary>
public sealed class ArchiveLogWriterTests : IDisposable
{
    private static readonly ArchiveLogLayout Layout = new(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 2, 100, 50, MemoryArchiveLogStore.NameRule);

    private static readonly ArchiveLogName Name = new("k/writer", 0x77);

    private static readonly LedgerSegmentId Segment = new(new LedgerId(5), Slot.Zero);

    private static readonly ArchiveLogWriterOptions Quick = new() { MaxRetries = 4, Delay = TimeSpan.Zero };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly List<ArchiveLogRequest> _requests = [];
    private readonly Queue<(ArchiveLogRequestKind Kind, ArchiveLogFault Fault)> _faults = [];

    public void Dispose() => _leaks.Dispose();

    /// <summary>A key's first run packs its appends into blocks and lands them as one run; an opener writes its base and generation.</summary>
    [Fact]
    public async Task AWriteLandsAtItsTokensPositionInItsPackedUnits()
    {
        var store = Store();
        var first = Write(default, ArchiveCommand.Append(1), ArchiveCommand.Append(1), ArchiveCommand.Append(2));

        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, first)).Value);
        Assert.Equal([ArchiveLogRequestKind.CreateLog, ArchiveLogRequestKind.AppendUnit, ArchiveLogRequestKind.AppendUnit], Kinds());
        Assert.Equal(new ArchiveLogPosition(1, 0, 4, 2), await PositionAsync(store));

        var opener = Write(Plan(first), ArchiveCommand.Delete(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, opener)).Value);
        var properties = await store.GetPropertiesAsync(Name, Token);
        Assert.Equal((new ArchiveLogPosition(2, 1, 0, 0), new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, 0x0102030405060708, 1)), (properties!.Position, properties.Opener));
    }

    /// <summary>A straggler, or a drain racing the one which landed the write, finds it held by one properties read.</summary>
    [Fact]
    public async Task AWriteWhichLandedIsHeldOnItsConditionsFailure()
    {
        var store = Store();
        var first = Write(default, ArchiveCommand.Append(2), ArchiveCommand.Append(2));
        var opener = Write(Plan(first), ArchiveCommand.Replace(2));
        await WriteAsync(store, first);
        await WriteAsync(store, opener);
        _requests.Clear();

        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, first)).Value);
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, opener)).Value);
        Assert.Equal(2, Kinds().Count(static kind => kind == ArchiveLogRequestKind.GetProperties));
    }

    /// <summary>
    /// A request whose answer is lost after it landed, or whose retry meets its own landing, leaves the run at a unit
    /// boundary: the writer's properties read resumes it there, landing each unit once.
    /// </summary>
    [Theory]
    [InlineData(ArchiveLogFault.FailAfter)]
    [InlineData(ArchiveLogFault.RetryMeetsLanding)]
    [InlineData(ArchiveLogFault.FailBefore)]
    public async Task ALostOrRetriedUnitResumesTheRunAtItsBoundary(ArchiveLogFault fault)
    {
        var store = Store();
        var run = Write(default, ArchiveCommand.Append(2), ArchiveCommand.Append(2), ArchiveCommand.Append(2));
        _faults.Enqueue((ArchiveLogRequestKind.AppendUnit, ArchiveLogFault.None));
        _faults.Enqueue((ArchiveLogRequestKind.AppendUnit, fault));

        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, run)).Value);
        Assert.Equal(new ArchiveLogPosition(1, 0, 6, 3), await PositionAsync(store));
        Assert.Equal(Bytes(run), await ContentAsync(store));
    }

    /// <summary>
    /// A writer which crashes between an opener's base and its generation, or mid-run, leaves part of the write landed; a
    /// later writer of the same write, which holds no state, retries the opener or resumes the run and completes it.
    /// </summary>
    [Fact]
    public async Task ALaterWriterCompletesAWriteACrashLeftInPart()
    {
        var store = Store();
        var first = Write(default, ArchiveCommand.Append(2), ArchiveCommand.Append(2));
        var opener = Write(Plan(first), ArchiveCommand.Replace(3), ArchiveCommand.Append(1));
        var run = Write(Plan(opener), ArchiveCommand.Append(2), ArchiveCommand.Append(2));
        await WriteAsync(store, first);

        _faults.Enqueue((ArchiveLogRequestKind.CreateBase, ArchiveLogFault.None));
        _faults.Enqueue((ArchiveLogRequestKind.OpenGeneration, ArchiveLogFault.FailBefore));
        await Assert.ThrowsAsync<IOException>(async () => await WriteAsync(store, opener, retries: 0));
        Assert.Equal(new ArchiveLogPosition(1, 0, 4, 2), await PositionAsync(store));
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, opener)).Value);

        _faults.Enqueue((ArchiveLogRequestKind.AppendUnit, ArchiveLogFault.None));
        _faults.Enqueue((ArchiveLogRequestKind.AppendUnit, ArchiveLogFault.FailBefore));
        await Assert.ThrowsAsync<IOException>(async () => await WriteAsync(store, run, retries: 0));
        Assert.Equal(new ArchiveLogPosition(2, 2, 2, 1), await PositionAsync(store));
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, run)).Value);

        Assert.Equal(new ArchiveLogPosition(2, 2, 4, 2), await PositionAsync(store));
        byte[] content = [.. Bytes(opener), .. Bytes(run)];
        Assert.Equal(content, await ContentAsync(store));
    }

    /// <summary>An opener whose answer was lost, and whose generation then landed, is held by its properties read.</summary>
    [Fact]
    public async Task AnOpenerWhoseAnswerWasLostIsHeld()
    {
        var store = Store();
        var opener = Write(default, ArchiveCommand.Replace(2));
        _faults.Enqueue((ArchiveLogRequestKind.CreateBase, ArchiveLogFault.FailAfter));
        _faults.Enqueue((ArchiveLogRequestKind.CreateBase, ArchiveLogFault.None));
        _faults.Enqueue((ArchiveLogRequestKind.OpenGeneration, ArchiveLogFault.RetryMeetsLanding));

        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, opener)).Value);
        Assert.Equal(new ArchiveLogPosition(1, 1, 0, 0), await PositionAsync(store));
    }

    /// <summary>A properties read which fails is retried as it is.</summary>
    [Fact]
    public async Task AFailedPropertiesReadIsRetried()
    {
        var store = Store();
        var first = Write(default, ArchiveCommand.Append(2));
        await WriteAsync(store, first);
        _faults.Enqueue((ArchiveLogRequestKind.GetProperties, ArchiveLogFault.FailBefore));
        _faults.Enqueue((ArchiveLogRequestKind.GetProperties, ArchiveLogFault.FailAfter));

        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, first)).Value);
    }

    /// <summary>
    /// A write whose predecessor never landed returns to the drain, which decides on it (archive-logs.md §8.1). An opener's
    /// condition is its starting generation alone, so once the log reaches that generation it lands: lineage order makes a
    /// drain write it only after its predecessor is held.
    /// </summary>
    [Fact]
    public async Task AWriteWhosePredecessorIsMissingReturnsIt()
    {
        var store = Store();
        var first = Write(default, ArchiveCommand.Append(2));
        var second = Write(Plan(first), ArchiveCommand.Append(2));
        var opener = Write(Plan(second), ArchiveCommand.Replace(1));

        Assert.IsType<ArchiveLogConditionOutcome.PredecessorMissing>((await WriteAsync(store, second)).Value);
        Assert.IsType<ArchiveLogConditionOutcome.PredecessorMissing>((await WriteAsync(store, opener)).Value);
        await WriteAsync(store, first);
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, opener)).Value);
        Assert.Equal(new ArchiveLogPosition(2, 1, 0, 0), await PositionAsync(store));
    }

    /// <summary>
    /// A log whose position lies inside a unit the write packs, past an opener's start on its own generation, or whose new
    /// generation records another opener, contradicts the write's chain: the writer lands nothing more.
    /// </summary>
    [Fact]
    public async Task ALogWhichContradictsTheChainIsCorrupt()
    {
        var store = Store();
        var first = Write(default, ArchiveCommand.Append(1), ArchiveCommand.Append(1));
        _ = await store.AppendAsync(Name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>(new byte[1])], Token);
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await WriteAsync(store, first));

        var other = Store();
        var opener = Write(default, ArchiveCommand.Replace(1));
        _ = await other.OpenAsync(Name, ArchiveLogPosition.Origin, new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, null, 1), ReadOnlySequence<byte>.Empty, Token);
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await WriteAsync(other, opener));

        var inside = Store();
        var run = Write(default, ArchiveCommand.Append(2));
        _ = await inside.AppendAsync(Name, ArchiveLogPosition.Origin, [new ReadOnlySequence<byte>(new byte[3])], Token);
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await WriteAsync(inside, Write(Plan(run), ArchiveCommand.Append(2))));
    }

    /// <summary>
    /// A write whose condition failed while the log was behind it, and whose predecessor then brought the log to its start,
    /// is issued once more and lands: an opener's <c>Retry</c>, and a run's <c>Resume(0)</c>.
    /// </summary>
    [Fact]
    public async Task AWriteWhosePredecessorLandedMeanwhileIsIssuedOnceMore()
    {
        var store = new LyingStore(Store());
        var first = Write(default, ArchiveCommand.Append(2), ArchiveCommand.Append(2));
        await WriteAsync(store, first, ContentOf(first), 0, Token);
        var run = Write(Plan(first), ArchiveCommand.Append(2));
        var opener = Write(Plan(run), ArchiveCommand.Replace(1));

        store.FailWrites = 1;
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, run, ContentOf(run), 0, Token)).Value);
        store.FailWrites = 1;
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, opener, new(new byte[1]), 0, Token)).Value);
        Assert.Equal(new ArchiveLogPosition(2, 1, 0, 0), await PositionAsync(store.Inner));
    }

    /// <summary>
    /// Once the log reached a write's start, positions only grow, so a second failed condition which leaves an opener at its
    /// start, or an append run at or before the unit whose condition failed, is a contradiction a conforming store never
    /// leaves, as a generation tag which disagrees with its metadata, or a re-blocked log, does: the writer throws it as
    /// corruption, with no further request.
    /// </summary>
    [Fact]
    public async Task AFailedConditionWhichLeavesTheWriteAtItsStartTwiceIsCorrupt()
    {
        var store = new LyingStore(Store());
        var first = Write(default, ArchiveCommand.Append(2), ArchiveCommand.Append(2));
        await WriteAsync(store, first, ContentOf(first), 0, Token);

        store.FailWrites = 2;
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await WriteAsync(store, Write(Plan(first), ArchiveCommand.Replace(1)), new(new byte[1]), 0, Token));

        var run = Write(Plan(first), ArchiveCommand.Append(2), ArchiveCommand.Append(2));
        store.FailWrites = 2;
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await WriteAsync(store, run, ContentOf(run), 0, Token));
        Assert.Equal(new ArchiveLogPosition(1, 0, 4, 2), await PositionAsync(store.Inner));
    }

    /// <summary>
    /// A store's refusal, which no retry can make, reaches the caller after one attempt; a cancellation which is not the
    /// caller's, as a transport's own timeout, is retried, and the write lands.
    /// </summary>
    [Fact]
    public async Task ARefusalIsNotRetriedAndATransportsTimeoutIs()
    {
        var store = new LyingStore(Store());
        var run = Write(default, ArchiveCommand.Append(2));

        store.Throw = new ArchiveLogRefusedException("refused");
        await Assert.ThrowsAsync<ArchiveLogRefusedException>(async () => await WriteAsync(store, run, ContentOf(run), 4, Token));
        Assert.Equal(1, store.Attempts);

        store.Throw = new TaskCanceledException("The transport timed out.");
        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store, run, ContentOf(run), 4, Token)).Value);
        Assert.Equal(new ArchiveLogPosition(1, 0, 2, 1), await PositionAsync(store.Inner));
    }

    /// <summary>
    /// The caller's cancellation between two units of a run stops the write with no retry, its outcome unknown; a later
    /// writer, holding no state, resumes the run where it stopped.
    /// </summary>
    [Fact]
    public async Task ACancelledWriteIsNotRetriedAndALaterWriterResumesIt()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var store = new LyingStore(Store()) { AfterAppend = cancellation.Cancel };
        var run = Write(default, ArchiveCommand.Append(2), ArchiveCommand.Append(2), ArchiveCommand.Append(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await WriteAsync(store, run, ContentOf(run), 4, cancellation.Token));
        Assert.Equal(1, store.Attempts);
        Assert.Equal(new ArchiveLogPosition(1, 0, 2, 1), await PositionAsync(store.Inner));

        Assert.IsType<ArchiveLogConditionOutcome.Held>((await WriteAsync(store.Inner, run)).Value);
        Assert.Equal(new ArchiveLogPosition(1, 0, 6, 3), await PositionAsync(store.Inner));
        Assert.Equal(Bytes(run), await ContentAsync(store.Inner));
    }

    [Fact]
    public async Task TheContentIsTheCommandsBytesAndTheTokenIsArchiveLogs()
    {
        var store = Store();

        await Assert.ThrowsAsync<ArgumentException>(async () => await new ArchiveLogWriter(store, new FakeTimeProvider()).WriteAsync(Name, Write(default, ArchiveCommand.Append(2)), new ReadOnlySequence<byte>(new byte[3]), Token));
        await Assert.ThrowsAsync<ArchiveCorruptException>(async () => await WriteAsync(store, Write(new RowToken([9]), ArchiveCommand.Append(1))));
        await Assert.ThrowsAsync<ArgumentException>(async () => await WriteAsync(store, Write(default, ArchiveCommand.Delete(new byte[3]))));
        Assert.Empty(_requests);
    }

    /// <summary>
    /// Without faults, a write issues exactly the requests <see cref="ArchiveLogWriter.PlannedRequests"/> plans, which a
    /// drain admits by (archive-logs.md §8.6): over random chains of openers and append runs, each from the token its
    /// predecessor plans, an opener's two, and a run's units and a key's first run's creation.
    /// </summary>
    [Fact]
    public void PlannedRequestsEqualIssuedRequestsWithoutFaults() =>
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 5], Gen.Int[1, Layout.MaxAppendBytes!.Value].Array[0, 6]).Array[1, 5].Sample(chain =>
        {
            var store = new MemoryArchiveLogStore(Layout);
            var writer = new ArchiveLogWriter(store, new FakeTimeProvider(), Quick);
            RowToken token = default;
            foreach (var (opener, baseLength, appends) in chain)
            {
                var commands = (opener switch
                {
                    1 => [ArchiveCommand.Replace(baseLength)],
                    2 => [ArchiveCommand.Delete(default)],
                    _ => Array.Empty<ArchiveCommand>(),
                }).Concat(appends.Select(ArchiveCommand.Append));
                var write = Write(token, [.. commands]);
                var issued = new RequestTally();

                Assert.IsType<ArchiveLogConditionOutcome.Held>(writer.WriteAsync(Name, write, ContentOf(write), issued, Token).AsTask().GetAwaiter().GetResult().Value);
                Assert.Equal(writer.PlannedRequests(write), issued.Issued);
                token = Plan(write);
            }
        });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ArchiveWrite Write(RowToken token, params ArchiveCommand[] commands) => new(Segment, token, [.. commands]);

    private static RowToken Plan(ArchiveWrite write)
    {
        var plan = new ArchiveLogPlan(Layout, ArchiveLogTokens.PositionOf(write.Token));
        foreach (var command in write.Commands)
        {
            plan.Add(command);
        }

        return plan.End();
    }

    /// <summary>A write's content: each command's bytes, a distinct value per write and command.</summary>
    private static byte[] Bytes(ArchiveWrite write) =>
        [.. write.Commands.SelectMany((command, index) => Enumerable.Repeat(unchecked((byte)(write.Token.Length * 16 + index + 1)), command.ContentLength))];

    private MemoryArchiveLogStore Store() => new(Layout, request =>
    {
        lock (_requests)
        {
            _requests.Add(request);
            return _faults.TryPeek(out var next) && next.Kind == request.Kind ? _faults.Dequeue().Fault : ArchiveLogFault.None;
        }
    });

    private ImmutableArray<ArchiveLogRequestKind> Kinds()
    {
        lock (_requests)
        {
            return [.. _requests.Select(static request => request.Kind)];
        }
    }

    private static Task<ArchiveLogConditionOutcome> WriteAsync(MemoryArchiveLogStore store, ArchiveWrite write, int retries = 4)
    {
        var bytes = Bytes(write);
        var content = bytes.Length > 1 ? SequenceChain.Of(bytes[..1], bytes[1..]) : new ReadOnlySequence<byte>(bytes);
        return WriteAsync(store, write, content, retries, Token);
    }

    private static Task<ArchiveLogConditionOutcome> WriteAsync(IArchiveLogStore store, ArchiveWrite write, ReadOnlySequence<byte> content, int retries, CancellationToken cancellationToken) =>
        new ArchiveLogWriter(store, new FakeTimeProvider(), Quick with { MaxRetries = retries }).WriteAsync(Name, write, content, cancellationToken).AsTask();

    private static ReadOnlySequence<byte> ContentOf(ArchiveWrite write) => new(Bytes(write));

    private static async Task<ArchiveLogPosition?> PositionAsync(MemoryArchiveLogStore store) =>
        (await store.GetPropertiesAsync(Name, Token))?.Position;

    private static async Task<byte[]> ContentAsync(MemoryArchiveLogStore store)
    {
        using var read = Assert.IsType<ArchiveLogRead.Read>((await store.ReadAsync(Name, Token)).Value);
        return read.Content.ToArray();
    }

    /// <summary>
    /// A store which, on request, answers a write's failed condition without running it, as a log whose generation tag
    /// disagrees with its metadata does; throws instead of running it; or lands an append run's first unit alone and then
    /// runs a callback, as a caller which cancels between two units does.
    /// </summary>
    private sealed class LyingStore(MemoryArchiveLogStore inner) : IArchiveLogStore
    {
        public MemoryArchiveLogStore Inner => inner;

        public int FailWrites { get; set; }

        public Exception? Throw { get; set; }

        public Action? AfterAppend { get; set; }

        public int Attempts { get; private set; }

        public ArchiveLogLayout Layout => inner.Layout;

        public bool AdmitsName(string name) => inner.AdmitsName(name);

        public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.ReadAsync(name, cancellationToken);

        public ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.GetPropertiesAsync(name, cancellationToken);

        public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
        {
            Attempt();
            if (FailWrites > 0)
            {
                FailWrites--;
                return ArchiveLogWriteOutcome.ConditionFailed.Instance;
            }

            if (AfterAppend is { } after)
            {
                AfterAppend = null;
                _ = await inner.AppendAsync(name, from, [run[0]], cancellationToken);
                after();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await inner.AppendAsync(name, from, run, cancellationToken);
        }

        public ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken)
        {
            Attempt();
            if (FailWrites > 0)
            {
                FailWrites--;
                return ValueTask.FromResult<ArchiveLogWriteOutcome>(ArchiveLogWriteOutcome.ConditionFailed.Instance);
            }

            return inner.OpenAsync(name, from, opener, content, cancellationToken);
        }

        public IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken) => inner.ListGenerationsAsync(continuation, cancellationToken);

        public ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken) => inner.DeleteGenerationAsync(log, generation, cancellationToken);

        private void Attempt()
        {
            Attempts++;
            if (Throw is { } exception)
            {
                Throw = null;
                throw exception;
            }
        }
    }
}
