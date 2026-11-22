using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.RowKind;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The history reader (archive.md §2.2, §8.1, §8.2): the map, then the store while every live ledger's scan runs, the
/// open ledger's to its known contiguous tail, then the open ledger's seal check and the rest of its scan, every live
/// ledger from slot 0, with the open ledger's <c>K</c> reads in flight in all, merged by the store's <c>Holds</c>, which
/// marks the rows the prefix holds, with the token chain and versions of the others checked; the restarts on a
/// <c>Stale</c> store, a dropped ledger, and a sealed open one, and the resumption after backpressure; and the store's
/// first-touch answer with its map value.
/// </summary>
public sealed class ArchiveTests : IAsyncLifetime
{
    private const byte K = 0x10;
    private static readonly byte[] Key = [K];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private ArchiveCluster? _cluster;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ArchiveCluster Cluster => _cluster!;

    public async ValueTask InitializeAsync() => _cluster = await ArchiveCluster.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
        }

        _leaks.Dispose();
    }

    /// <summary>
    /// The store's prefix and every live row from slot 0 merge by <c>Holds</c>: the rows of the drained ledger, still
    /// listed, come back held with their lengths only, and the others with their bodies, another key's rows left out; in
    /// the process which serves the open ledger's writer the read is linearizable.
    /// </summary>
    [Fact]
    public async Task AReadMarksTheRowsThePrefixHoldsAndFoldsTheRest()
    {
        var a = await Cluster.AppendAsync(K, Append, "a");
        var b = await Cluster.AppendAsync(K, Append, "b");
        _ = await Cluster.AppendAsync(0x90, Append, "other");
        var first = await Cluster.ReplaceLedgerAsync(K);
        var c = await Cluster.AppendAsync(K, Append, "c");
        await Cluster.DrainAsync(first);

        using var read = await Cluster.Primary.Archive.ReadAsync(Key, Token);

        Assert.Equal([(a, Append, string.Empty, true), (b, Append, string.Empty, true), (c, Append, "c", false)], ArchiveRows.Of(read.Rows));
        Assert.Equal([1, 1, 1], read.Rows.ToArray().Select(static row => row.BodyLength));
        Assert.Equal("abc", ArchiveRows.Content(read));
        Assert.Equal(new Dictionary<LedgerId, Slot> { [first] = new Slot(3), [c.Ledger] = new Slot(1) }, read.Through);
        Assert.Equal(c.Ledger, read.OpenLedger);
        Assert.True(read.Linearizable);
    }

    /// <summary>A read folds a key's history (archive.md §2.1): a <c>Replace</c> supersedes, a <c>Delete</c> leaves its tombstone.</summary>
    [Fact]
    public async Task AReadFoldsTheKeysHistory()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        _ = await Cluster.AppendAsync(K, Replace, "b");
        _ = await Cluster.AppendAsync(K, Append, "c");

        using (var read = await Cluster.Primary.Archive.ReadAsync(Key, Token))
        {
            Assert.Equal("bc", ArchiveRows.Content(read));
            Assert.True(ArchiveOracle.Fold(read.Prefix, read.Rows).HasBase);
        }

        _ = await Cluster.AppendAsync(K, Delete, "r");
        using (var read = await Cluster.Primary.Archive.ReadAsync(Key, Token))
        {
            var fold = ArchiveOracle.Fold(read.Prefix, read.Rows);
            Assert.Equal((string.Empty, "r"), (ArchiveRows.Content(read), System.Text.Encoding.UTF8.GetString(fold.Tombstone!)));
        }

        _ = await Cluster.AppendAsync(K, Append, "d");
        using (var read = await Cluster.Primary.Archive.ReadAsync(Key, Token))
        {
            Assert.Equal("d", ArchiveRows.Content(read));
            Assert.Null(ArchiveOracle.Fold(read.Prefix, read.Rows).Tombstone);
        }
    }

    /// <summary>
    /// A segment the store holds in a ledger the map still lists comes back held, the open ledger's later segment not;
    /// each segment's defining row stores the token the previous write leaves, which the read checks.
    /// </summary>
    [Fact]
    public async Task ASegmentTheStoreHoldsInAListedLedgerReadsBackHeld()
    {
        var a = await Cluster.AppendAsync(K, Append, "a");
        await Cluster.EndSegmentAsync(K);
        var b = await Cluster.AppendAsync(K, Append, "b");
        await Cluster.CarryTailAsync(K);
        await Cluster.DrainAsync(a.Ledger, below: new Slot(b.Slot.Value + 1));

        using var read = await Cluster.Primary.Archive.ReadAsync(Key, Token);

        Assert.Equal([(a, Append, string.Empty, true), (b, Append, "b", false)], ArchiveRows.Of(read.Rows));
        Assert.NotNull(read.Prefix);
        Assert.Equal(read.Prefix.Token, read.Rows[1].Token);
        Assert.Equal("ab", ArchiveRows.Content(read));
    }

    /// <summary>
    /// A first touch answers the store's prefix, or none for <c>Absent</c>, with the very map value read before the store,
    /// though the map changes while the store is read; that value's live ledgers hold the rest of the key's history
    /// (archive.md §8.2), which the answer's merge marks by <c>Holds</c>.
    /// </summary>
    [Fact]
    public async Task ReadStorePairsTheStoresAnswerWithTheMapValueReadBeforeIt()
    {
        var a = await Cluster.AppendAsync(K, Append, "a");
        var hooked = new HookedArchiveStore(Cluster.Store);
        var reader = await Cluster.StartProcessAsync(hooked);
        var before = reader.View.Current;
        var first = a.Ledger;
        hooked.BeforeNextRead = async cancellationToken =>
        {
            _ = await Cluster.ReplaceLedgerAsync(K);
            _ = await reader.View.ReadAsync(cancellationToken);
        };

        using (var absent = await reader.Archive.ReadStoreAsync(Key, Token))
        {
            Assert.Same(before, absent.Map);
            Assert.Null(absent.Prefix);
            Assert.True(reader.View.Current.Epoch > before.Epoch);
        }

        await Cluster.DrainAsync(first);
        using var answer = await reader.Archive.ReadStoreAsync(Key, Token);
        Assert.NotNull(answer.Prefix);
        Assert.Equal("a", System.Text.Encoding.UTF8.GetString(answer.Prefix.Content.ToArray()));
        Assert.Same(reader.View.Current, answer.Map);

        var segments = await Cluster.SegmentsAsync(Cluster.Primary.View.Current.Get(first)!.Value, new Slot(1));
        try
        {
            ArchiveRow[] live = [.. segments.Single().Select(static row => row.Row)];
            using var merged = Assert.IsType<ArchiveRead>(answer.Merge(live, new Dictionary<LedgerId, Slot> { [first] = new Slot(1) }, openLedger: null, linearizable: false).Value);
            Assert.Equal([(a, Append, string.Empty, true)], ArchiveRows.Of(merged.Rows));
            Assert.Equal("a", ArchiveRows.Content(merged));
        }
        finally
        {
            KeyedRow.Release(segments.Single());
        }
    }

    /// <summary>
    /// A store which answers <c>Stale</c> makes either read take the map again and read the store again with it: a value
    /// at least as new, here newer, since the reading process's view was stale (HI-I1).
    /// </summary>
    [Fact]
    public async Task AStaleStoreAnswerRereadsTheMapAndTheStore()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        var hooked = new HookedArchiveStore(Cluster.Store) { StaleAnswers = 1 };
        var reader = await Cluster.StartProcessAsync(hooked);
        var stale = reader.View.Current.Epoch;
        _ = await Cluster.ReplaceLedgerAsync(K);

        using (var read = await reader.Archive.ReadAsync(Key, Token))
        {
            Assert.Equal("a", ArchiveRows.Content(read));
        }

        var current = Cluster.Primary.View.Current.Epoch;
        Assert.Equal([stale, current], hooked.ReadEpochs);

        hooked.StaleAnswers = 1;
        using (var answer = await reader.Archive.ReadStoreAsync(Key, Token))
        {
            Assert.Null(answer.Prefix);
            Assert.Equal(current, answer.Map.Epoch);
        }

        Assert.Equal([stale, current, current, current], hooked.ReadEpochs);
        Assert.Equal(2, reader.Archive.Counts.Stale.Value);
    }

    /// <summary>
    /// A read whose map lists a ledger which has since dropped meets the drop in its scan, and reads the map and the store
    /// again, which then cover the ledger (HI-I1, HI-I2); outside the primary's process it reads the open ledger below the
    /// contiguous tail its replicas hold, and is not linearizable.
    /// </summary>
    [Fact]
    public async Task AReadWhichMeetsADroppedLedgerRestartsWithAFreshMap()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        _ = await Cluster.AppendAsync(K, Append, "b");
        var first = await Cluster.ReplaceLedgerAsync(K);
        var c = await Cluster.AppendAsync(K, Append, "c");
        await Cluster.CarryTailAsync(K);
        var hooked = new HookedArchiveStore(Cluster.Store);
        var reader = await Cluster.StartProcessAsync(hooked);
        var stale = reader.View.Current.Epoch;
        await Cluster.DrainAsync(first);
        await Cluster.DropAsync(first);

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal("abc", ArchiveRows.Content(read));
        Assert.Equal([(c, Append, "c", false)], ArchiveRows.Of(read.Rows));
        Assert.Equal([stale, Cluster.Primary.View.Current.Epoch], hooked.ReadEpochs);
        Assert.Equal(new Dictionary<LedgerId, Slot> { [c.Ledger] = new Slot(1) }, read.Through);
        Assert.Equal(c.Ledger, read.OpenLedger);
        Assert.False(read.Linearizable);
        Assert.Equal(1, reader.Archive.Counts.Dropped.Value);
    }

    /// <summary>
    /// Regression test, #425: when a live-ledger scan meets a dropped ledger and cancels the attempt while the store read
    /// is still in flight and throws <see cref="OperationCanceledException"/> on the attempt token, the read drains it and
    /// restarts with a fresh map instead of letting cancellation escape.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADroppedLedgerCancellingAnInFlightStoreReadRestartsTheRead()
    {
        var token = TestContext.Current.CancellationToken;
        _ = await Cluster.AppendAsync(K, Append, "a");
        var first = await Cluster.ReplaceLedgerAsync(K);
        _ = await Cluster.AppendAsync(K, Append, "b");
        await Cluster.CarryTailAsync(K);
        var hooked = new HookedArchiveStore(Cluster.Store);
        var reader = await Cluster.StartProcessAsync(hooked);
        var stale = reader.View.Current.Epoch;
        await Cluster.DrainAsync(first);
        await Cluster.DropAsync(first);
        hooked.BeforeNextRead = static cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);

        using var read = await reader.Archive.ReadAsync(Key, token);

        Assert.Equal("ab", ArchiveRows.Content(read));
        Assert.Equal([stale, Cluster.Primary.View.Current.Epoch], hooked.ReadEpochs);
        Assert.Equal(1, reader.Archive.Counts.Dropped.Value);
    }

    /// <summary>
    /// Regression test, #425: when the store read has already returned a prefix owning rented <see cref="ArcBuffer"/>s
    /// before a live-ledger scan meets a dropped ledger and cancels the attempt, the read disposes that answer without
    /// leaking its buffers and restarts with a fresh map.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADroppedLedgerAfterTheStoreAnsweredDisposesThePrefixAndRestarts()
    {
        var token = TestContext.Current.CancellationToken;
        _ = await Cluster.AppendAsync(K, Append, "a");
        var first = await Cluster.ReplaceLedgerAsync(K);
        await Cluster.DrainAsync(first);
        await Cluster.DropAsync(first);
        _ = await Cluster.AppendAsync(K, Append, "b");
        var second = await Cluster.ReplaceLedgerAsync(K);
        _ = await Cluster.AppendAsync(K, Append, "c");
        await Cluster.CarryTailAsync(K);
        var storeAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooked = new HookedArchiveStore(Cluster.Store)
        {
            AfterNextRead = () =>
            {
                _ = storeAnswered.TrySetResult();
                return Task.CompletedTask;
            },
        };
        var reader = await Cluster.StartProcessAsync(
            hooked,
            gate: (ledger, _) => ledger == second ? storeAnswered.Task : Task.CompletedTask);
        var stale = reader.View.Current.Epoch;
        await Cluster.DrainAsync(second);
        await Cluster.DropAsync(second);

        using var read = await reader.Archive.ReadAsync(Key, token);

        Assert.Equal("abc", ArchiveRows.Content(read));
        Assert.Equal([stale, Cluster.Primary.View.Current.Epoch], hooked.ReadEpochs);
        Assert.Equal(1, reader.Archive.Counts.Dropped.Value);
    }

    /// <summary>
    /// Regression test, #425: when a live-ledger scan meets a dropped ledger and cancels the attempt, caller cancellation
    /// still propagates instead of restarting the read.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CallerCancellationPropagatesWhenADroppedLedgerCancelsTheAttempt()
    {
        var token = TestContext.Current.CancellationToken;
        _ = await Cluster.AppendAsync(K, Append, "a");
        var first = await Cluster.ReplaceLedgerAsync(K);
        _ = await Cluster.AppendAsync(K, Append, "b");
        await Cluster.CarryTailAsync(K);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var hooked = new HookedArchiveStore(Cluster.Store)
        {
            BeforeNextRead = async attemptToken =>
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (attemptToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled))
                {
                    await cancelled.Task;
                }

                await caller.CancelAsync();
                attemptToken.ThrowIfCancellationRequested();
            },
        };
        var reader = await Cluster.StartProcessAsync(hooked);
        await Cluster.DrainAsync(first);
        await Cluster.DropAsync(first);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => (await reader.Archive.ReadAsync(Key, caller.Token)).Dispose());
        Assert.Single(hooked.ReadEpochs);
    }

    /// <summary>
    /// Regression test, #425: a non-cancellation store failure still propagates when a live-ledger scan meets a dropped
    /// ledger on the same attempt.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AStoreFailureBesideADroppedLedgerPropagates()
    {
        var token = TestContext.Current.CancellationToken;
        _ = await Cluster.AppendAsync(K, Append, "a");
        var first = await Cluster.ReplaceLedgerAsync(K);
        var hooked = new HookedArchiveStore(Cluster.Store) { Failure = new IOException("The store is unavailable.") };
        var reader = await Cluster.StartProcessAsync(hooked);
        await Cluster.DrainAsync(first);
        await Cluster.DropAsync(first);

        _ = await Assert.ThrowsAsync<IOException>(async () => (await reader.Archive.ReadAsync(Key, token)).Dispose());
        Assert.Single(hooked.ReadEpochs);
    }

    /// <summary>
    /// A legal rebase which a reader on a stale map meets is no corruption (HI-I4, archive.md §8.1 step 4): the reader's
    /// map lists the closed ledger 1, which holds the key's rows, and the open ledger 2; ledger 1 drops, the primary rebases
    /// the key at the store's prefix and writes its next row to ledger 2 from that version base. The reader scans ledger 1,
    /// then ledger 2, whose row's version does not follow ledger 1's; it reads the map again, finds ledger 1 dropped, and
    /// reads again, answering the key's history; with the map unmoved, the same break is corruption.
    /// </summary>
    [Fact]
    public async Task AVersionBreakFromARebaseAfterADropRestartsAReadOnAStaleMap()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        var first = await Cluster.ReplaceLedgerAsync(K);
        var reader = await Cluster.StartProcessAsync(Cluster.Store);
        await Cluster.DrainAsync(first);
        Assert.IsType<Orleans.Durable.Storage.Engine.Ledgers.Control.LedgerMapCommitOutcome.Applied>((await Cluster.Committer.CommitAsync(new Orleans.Durable.Storage.Engine.Ledgers.Control.LedgerMapChange.Drop(first), Token)).Value);
        _ = await Cluster.Primary.View.ReadAsync(Token);
        using (var stored = Assert.IsType<ArchiveStoreRead.Read>((await Cluster.Store.ReadAsync(Key, Cluster.Primary.View.Current, Token)).Value))
        {
            Cluster.Chains.Rebase(Key, stored.Prefix.VersionBase, stored.Prefix.Token);
        }

        var b = await Cluster.AppendAsync(K, Append, "b");
        await Cluster.CarryTailAsync(K);

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal("ab", ArchiveRows.Content(read));
        Assert.Equal([(b, Append, "b", false)], ArchiveRows.Of(read.Rows));
        Assert.Equal(1, reader.Archive.Counts.Dropped.Value);
    }

    /// <summary>
    /// A prefix which reaches past the end the read's scans reached, here one a store answers from a write past the
    /// ledger's rows, makes the read start again (archive.md §8.1 step 4), and the read then answers the key's history.
    /// </summary>
    [Fact]
    public async Task APrefixPastTheReadsEndRestartsTheRead()
    {
        var a = await Cluster.AppendAsync(K, Append, "a");
        var first = await Cluster.ReplaceLedgerAsync(K);
        await Cluster.DrainAsync(first);
        using var future = new Orleans.Durable.Storage.Engine.Archive.Memory.InMemoryArchiveStore();
        var segment = new LedgerSegmentId(first, Slot.Zero);
        var afterA = future.Plan(new ArchiveWrite(segment, default, [ArchiveCommand.Append(1)]));
        using (var bytes = ArcBuffer.Wrap("a"u8.ToArray()))
        using (var row = ArchiveRow.Live(a, Append, new RowVersion(default, 1), segment, default, bytes))
        {
            future.Drain(Key, [row]);
        }

        var past = new LedgerSegmentId(first, new Slot(100));
        using (var bytes = ArcBuffer.Wrap("z"u8.ToArray()))
        using (var row = ArchiveRow.Live(new Tag(first, new Slot(100), 0), Append, new RowVersion(default, 2), past, afterA, bytes))
        {
            future.Drain(Key, [row]);
        }

        var hooked = new HookedArchiveStore(Cluster.Store) { NextReadFrom = future };
        var reader = await Cluster.StartProcessAsync(hooked);

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal("a", ArchiveRows.Content(read));
        Assert.Equal(1, reader.Archive.Counts.PastEnd.Value);
    }

    /// <summary>
    /// A read copies its rows' bodies into buffers of its own (archive.md §4), so once the live ledgers release their
    /// batches it holds only pages of its key's own bytes, one here for sixteen rows each in a batch of its own; the read's
    /// disposal returns them.
    /// </summary>
    [Fact]
    public async Task AReadHoldsOnlyItsKeysBytesNotItsBatchesPages()
    {
        for (var index = 0; index < 16; index++)
        {
            _ = await Cluster.AppendAsync(K, Append, "x");
        }

        var read = await Cluster.Primary.Archive.ReadAsync(Key, Token);
        try
        {
            await Cluster.DisposeAsync();
            _cluster = null;

            Assert.True(_leaks.Outstanding <= 1, $"The read holds {_leaks.Outstanding} pages for 16 bytes of its key.");
            Assert.Equal(new string('x', 16), ArchiveRows.Content(read));
        }
        finally
        {
            read.Dispose();
        }

        Assert.Equal(0, _leaks.Outstanding);
    }

    /// <summary>
    /// A read whose map lists open a ledger which has since been sealed and closed finds it sealed when it checks it, once
    /// the store answered, and starts again once the map lists it closed, reading it to its recorded <c>T</c> (HI-I1, LG-I8).
    /// </summary>
    [Fact]
    public async Task AReadWhichFindsItsOpenLedgerSealedRestartsOnceTheMapListsItClosed()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        var hooked = new HookedArchiveStore(Cluster.Store);
        var reader = await Cluster.StartProcessAsync(hooked);
        var stale = reader.View.Current.Epoch;
        var first = await Cluster.ReplaceLedgerAsync(K);

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal("a", ArchiveRows.Content(read));
        Assert.Equal(new Slot(1), read.Through[first]);
        Assert.Equal([stale, Cluster.Primary.View.Current.Epoch], hooked.ReadEpochs);
        Assert.Equal(1, reader.Archive.Counts.Sealed.Value);
    }

    /// <summary>
    /// archive.md §10, read order: a drain and a drop of a ledger after the store answered, with the reader's view
    /// refreshed, leave a read which took the map first finding every row: it scans the ledger before the drop, or meets
    /// the drop and reads the map and the store again (HI-I1).
    /// </summary>
    [Fact]
    public async Task TheMapIsReadBeforeTheStore()
    {
        var (_, reader, _) = await DrainAfterTheStoreAnswersAsync();

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal("abc", ArchiveRows.Content(read));
    }

    /// <summary>
    /// archive.md §10's mutant, which reads the store before it takes the map, misses the rows the drain moved between
    /// them, which the previous test shows the reader finds: the chain check finds the live rows starting past the prefix's
    /// token (HI-I4), and a reader which skips that check answers without them.
    /// </summary>
    [Fact]
    public async Task TheMapAfterStoreMutantMissesTheDrainedRows()
    {
        var (_, reader, hooked) = await DrainAfterTheStoreAnswersAsync();
        reader.Archive.MapAfterStoreMutant = true;
        var stale = reader.View.Current.Epoch;

        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => (await reader.Archive.ReadAsync(Key, Token)).Dispose());
        Assert.Equal([stale], hooked.ReadEpochs);
    }

    /// <summary>The map-after-store mutant, with the chain check skipped too, answers the key's history without the drained rows.</summary>
    [Fact]
    public async Task TheMapAfterStoreMutantWithoutTheChainCheckAnswersWithoutTheDrainedRows()
    {
        var (_, reader, _) = await DrainAfterTheStoreAnswersAsync();
        reader.Archive.MapAfterStoreMutant = true;
        reader.Archive.MergeMutant = ArchiveMerge.Mutant.SkipsTheChainCheck;

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal("c", ArchiveRows.Content(read));
    }

    /// <summary>
    /// A row whose stored token breaks its key's chain fails the read with <c>ArchiveCorruptException</c> (HI-I4); a
    /// reader which skips its check, archive.md §10's mutant, answers the read, whose rows the chain checker flags.
    /// </summary>
    [Fact]
    public async Task ARowWhichBreaksTheChainFailsTheReadAndTheCheckerFlagsTheMutant()
    {
        Cluster.Chains.Mutant = ArchiveChains.ChainMutant.WrongParent;
        _ = await Cluster.AppendAsync(K, Append, "a");
        await Cluster.EndSegmentAsync(K);
        _ = await Cluster.AppendAsync(K, Append, "b");
        await Cluster.EndSegmentAsync(K);
        _ = await Cluster.AppendAsync(K, Append, "c");

        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(async () => (await Cluster.Primary.Archive.ReadAsync(Key, Token)).Dispose());

        Cluster.Primary.Archive.MergeMutant = ArchiveMerge.Mutant.SkipsTheChainCheck;
        using var read = await Cluster.Primary.Archive.ReadAsync(Key, Token);
        Assert.Contains("HI-I4", TokenChainChecker.Check(Cluster.Store, read.Prefix?.Token ?? default, ChainRow.Of(read.Rows)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A restart whose map did not advance waits out a jittered backoff of at most a second on the reader's clock; one
    /// whose map advanced begins at once (archive.md §8.1).
    /// </summary>
    [Fact]
    public async Task ARestartWaitsOutItsBackoffUnlessTheMapAdvanced()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        var time = new CountingTimeProvider();
        var hooked = new HookedArchiveStore(Cluster.Store) { StaleAnswers = 2 };
        var reader = await Cluster.StartProcessAsync(hooked, time);

        // Each advance waits for the backoff's timer: an advance before the reader arms it would leave it unfired. With no
        // backpressure, every timer on the reader's clock is a restart's backoff.
        var reading = reader.Archive.ReadAsync(Key, Token).AsTask();
        await WaitUntilAsync(() => time.Timers >= 1 || reading.IsCompleted);
        Assert.False(reading.IsCompleted, "The read completed without waiting out its first restart's backoff.");
        Assert.True(time.Timers == 1, $"The reader armed {time.Timers} timers, not its first backoff's alone.");
        Assert.Single(hooked.ReadEpochs);
        Assert.Equal(0, reader.Archive.Counts.Backpressured.Value);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => time.Timers >= 2 || reading.IsCompleted);
        Assert.False(reading.IsCompleted, "The read completed without waiting out its second restart's backoff.");
        Assert.True(time.Timers == 2, $"The reader armed {time.Timers} timers, not its two backoffs' alone.");
        Assert.Equal(2, hooked.ReadEpochs.Count);
        Assert.Equal(0, reader.Archive.Counts.Backpressured.Value);
        time.Advance(TimeSpan.FromSeconds(1));
        (await reading.WaitAsync(Bound, TimeProvider.System, Token)).Dispose();
        Assert.Equal(3, hooked.ReadEpochs.Count);
        Assert.Equal(2, time.Timers);
        Assert.Equal(0, reader.Archive.Counts.Backpressured.Value);

        // The map advanced past the reader's view, so its restart after Stale begins without the clock moving.
        _ = await Cluster.ReplaceLedgerAsync(K);
        hooked.StaleAnswers = 1;
        (await reader.Archive.ReadAsync(Key, Token).AsTask().WaitAsync(Bound, TimeProvider.System, Token)).Dispose();
        Assert.Equal(3, reader.Archive.Counts.Stale.Value);
    }

    /// <summary>
    /// Regression test, #270 (archive.md §8.1): every restart whose map did not advance waits at least 20 ms on the
    /// reader's clock, and every wait stays within twice its exponential step and 1 s. Polly's own jittered backoff drew a
    /// wait of zero about once in 840 restarts, which repeated at once without arming a timer, so a restart began without
    /// waiting; many first restarts through the reader's pipeline each arm a timer of at least 20 ms.
    /// </summary>
    [Fact]
    public async Task EveryRestartWaitsAtLeastItsFirstStep()
    {
        var time = new CountingTimeProvider();
        var restarts = ArchiveReader.Retry<string?>(time, static answer => answer is null, counter: null);
        const int Restarts = 20_000;
        for (var run = 0; run < Restarts; run++)
        {
            var attempts = 0;
            var timers = time.Timers;
            var restarted = restarts.ExecuteAsync(_ => ValueTask.FromResult(++attempts == 1 ? null : "answered"), Token).AsTask();
            Assert.True(attempts == 1 && time.Timers == timers + 1, $"Restart {run} began without waiting on a timer.");
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("answered", await restarted.WaitAsync(Bound, TimeProvider.System, Token));
        }

        Assert.True(time.ShortestDue >= TimeSpan.FromMilliseconds(20), $"A restart waited {time.ShortestDue}.");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var step = TimeSpan.FromMilliseconds(Math.Min(20 * Math.Pow(2, attempt), 1000));
            var wait = Backoff.Delay(attempt, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1), Random.Shared.NextDouble());
            Assert.InRange(wait, step, TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>A store's failure propagates at once, with no restart.</summary>
    [Fact]
    public async Task AStoreFailurePropagatesWithoutARestart()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        var hooked = new HookedArchiveStore(Cluster.Store) { Failure = new IOException("The store is unavailable.") };
        var reader = await Cluster.StartProcessAsync(hooked);

        _ = await Assert.ThrowsAsync<IOException>(async () => (await reader.Archive.ReadAsync(Key, Token)).Dispose());
        Assert.Single(hooked.ReadEpochs);
    }

    /// <summary>
    /// A store's failure while a closed ledger's scan runs beside it propagates once the scan settles, releasing the rows
    /// the scan collected, as the leak check shows.
    /// </summary>
    [Fact]
    public async Task AStoreFailureBesideAClosedLedgersScanPropagates()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        _ = await Cluster.ReplaceLedgerAsync(K);
        var hooked = new HookedArchiveStore(Cluster.Store) { Failure = new IOException("The store is unavailable.") };
        var reader = await Cluster.StartProcessAsync(hooked);

        _ = await Assert.ThrowsAsync<IOException>(async () => (await reader.Archive.ReadAsync(Key, Token)).Dispose());
    }

    /// <summary>Cancellation stops a read which meets nothing but <c>Stale</c> answers.</summary>
    [Fact]
    public async Task CancellationStopsAReadWhichMeetsEndlessStaleAnswers()
    {
        var hooked = new HookedArchiveStore(Cluster.Store) { StaleAnswers = int.MaxValue };
        var reader = await Cluster.StartProcessAsync(hooked);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => (await reader.Archive.ReadAsync(Key, cancellation.Token)).Dispose());
        Assert.True(hooked.ReadEpochs.Count > 1);
    }

    /// <summary>
    /// A restart after the scan collected rows of a closed ledger, when a replica of the open one answers dropped, releases
    /// them, as the leak check shows, and the next attempt reads every row.
    /// </summary>
    [Fact]
    public async Task ARestartReleasesTheRowsItCollected()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        _ = await Cluster.AppendAsync(K, Append, "b");
        _ = await Cluster.ReplaceLedgerAsync(K);
        var c = await Cluster.AppendAsync(K, Append, "c");
        await Cluster.CarryTailAsync(K);
        var dropOnce = 1;
        var reader = await Cluster.StartProcessAsync(
            Cluster.Store,
            reads: (ledger, slot) => ledger == c.Ledger && Interlocked.Exchange(ref dropOnce, 0) == 1 ? new ReplicaRead(ReplicaWriteOutcome.Dropped.Instance) : (ReplicaRead?)null);

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal("abc", ArchiveRows.Content(read));
        Assert.Equal(1, reader.Archive.Counts.Dropped.Value);
    }

    /// <summary>
    /// Backpressure on a replica read delays that read, and the scan resumes from its slot with the rows it collected and
    /// no restart.
    /// </summary>
    [Fact]
    public async Task BackpressureResumesTheScanFromItsSlot()
    {
        var a = await Cluster.AppendAsync(K, Append, "a");
        var b = await Cluster.AppendAsync(K, Append, "b");
        await Cluster.CarryTailAsync(K);
        var once = 1;
        var reader = await Cluster.StartProcessAsync(
            Cluster.Store,
            reads: (ledger, slot) => slot == new Slot(1) && Interlocked.Exchange(ref once, 0) == 1 ? new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance) : (ReplicaRead?)null);

        using var read = await reader.Archive.ReadAsync(Key, Token);

        Assert.Equal([(a, Append, "a", false), (b, Append, "b", false)], ArchiveRows.Of(read.Rows));
        var counts = reader.Archive.Counts;
        Assert.Equal((1, 0, 0, 0), (counts.Backpressured.Value, counts.Stale.Value, counts.Dropped.Value, counts.Sealed.Value));
    }

    /// <summary>
    /// The live ledgers' scans run together (archive.md §8.1): while the store read is held, the closed ledger's reads
    /// wait for the open ledger's first read, which reads below the open ledger's known contiguous tail in the reader's
    /// process, so the read completes only if they run at once; the open ledger's scan reads past that tail only once the
    /// store has answered, to its seal check's tail.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TheLiveLedgersScansRunTogetherAndTheOpenOneReadsPastItsKnownTailOnlyOnceTheStoreAnswered()
    {
        var token = TestContext.Current.CancellationToken;
        _ = await Cluster.AppendAsync(K, Append, "a");
        var closed = await Cluster.ReplaceLedgerAsync(K);
        Assert.True(
            Assert.IsType<LedgerStatus.Closed>(Cluster.Primary.View.Current.Get(closed)!.Value.Status.Value).T.Value < ArchiveCluster.Window,
            "The closed ledger's reads leave the open ledger a place in the window.");
        _ = await Cluster.AppendAsync(K, Append, "b");
        _ = await Cluster.AppendAsync(K, Append, "c");
        await Cluster.CarryTailAsync(K);
        var open = Cluster.OpenLedger(K);
        var store = new HookedArchiveStore(Cluster.Store);
        var gating = false;
        var storeAnswered = false;
        var known = Slot.Zero;
        var openRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var belowKnown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsBelowKnown = 0;
        var reads = new ConcurrentQueue<(LedgerId Ledger, Slot Slot, bool StoreAnswered)>();
        var reader = await Cluster.StartProcessAsync(store, gate: (ledger, slot) =>
        {
            if (!Volatile.Read(ref gating))
            {
                return Task.CompletedTask;
            }

            reads.Enqueue((ledger, slot, Volatile.Read(ref storeAnswered)));
            if (ledger != open.Id)
            {
                return openRead.Task;
            }

            _ = openRead.TrySetResult();
            if (slot < known && Interlocked.Increment(ref readsBelowKnown) == known.Value)
            {
                _ = belowKnown.TrySetResult();
            }

            return Task.CompletedTask;
        });

        // A first read teaches the reader's process the open ledger's tail; the later rows lie past it.
        (await reader.Archive.ReadAsync(Key, token)).Dispose();
        known = reader.Reader.KnownContiguousTail(open);
        Assert.True(known > Slot.Zero);
        _ = await Cluster.AppendAsync(K, Append, "d");
        _ = await Cluster.AppendAsync(K, Append, "e");
        await Cluster.CarryTailAsync(K);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeNextRead = _ => release.Task;
        store.AfterNextRead = () =>
        {
            Volatile.Write(ref storeAnswered, true);
            return Task.CompletedTask;
        };
        Volatile.Write(ref gating, true);
        var reading = reader.Archive.ReadAsync(Key, token).AsTask();
        await belowKnown.Task.WaitAsync(token);
        Assert.False(reading.IsCompleted);
        release.SetResult();
        using var read = await reading;

        Assert.Equal("abcde", ArchiveRows.Content(read));
        var log = reads.ToArray();
        Assert.Contains(log, entry => entry.Ledger == closed);
        Assert.All(log.Where(entry => entry.Ledger == open.Id && entry.Slot < known), static entry => Assert.False(entry.StoreAnswered));
        Assert.Contains(log, entry => entry.Ledger == open.Id && entry.Slot >= known);
        Assert.All(log.Where(entry => entry.Ledger == open.Id && entry.Slot >= known), static entry => Assert.True(entry.StoreAnswered));
    }

    /// <summary>
    /// The live ledgers' scans keep the open ledger's <c>K</c> reads in flight in all (archive.md §8.1): with every replica
    /// read held until the test releases it, oldest first, a read of a closed and an open ledger of many slots each, both
    /// below the tail the reader's process knows, keeps exactly <c>K</c> in flight at its peak.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TheLiveLedgersScansKeepTheOpenLedgersKReadsInFlightInAll()
    {
        var token = TestContext.Current.CancellationToken;
        const int Rows = 2 * ArchiveCluster.Window;
        for (var index = 0; index < Rows; index++)
        {
            _ = await Cluster.AppendAsync(K, Append, "a");
        }

        _ = await Cluster.ReplaceLedgerAsync(K);
        for (var index = 0; index < Rows; index++)
        {
            _ = await Cluster.AppendAsync(K, Append, "b");
        }

        await Cluster.CarryTailAsync(K);
        var gating = false;
        var inFlight = 0;
        var peak = 0;
        var held = Channel.CreateUnbounded<TaskCompletionSource>();
        var reader = await Cluster.StartProcessAsync(Cluster.Store, gate: (_, _) =>
        {
            if (!Volatile.Read(ref gating))
            {
                return Task.CompletedTask;
            }

            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(held.Writer.TryWrite(read));
            return read.Task;
        });

        (await reader.Archive.ReadAsync(Key, token)).Dispose();
        Assert.True(reader.Reader.KnownContiguousTail(Cluster.OpenLedger(K)).Value >= ArchiveCluster.Window);
        Volatile.Write(ref gating, true);
        var reading = reader.Archive.ReadAsync(Key, token).AsTask();
        while (true)
        {
            var next = held.Reader.ReadAsync(token).AsTask();
            if (await Task.WhenAny(next, reading) == reading)
            {
                break;
            }

            var read = await next;
            _ = Interlocked.Decrement(ref inFlight);
            read.SetResult();
        }

        held.Writer.Complete();
        using var answer = await reading;

        Assert.Equal(2 * Rows, answer.Rows.Length);
        Assert.Equal(ArchiveCluster.Window, Volatile.Read(ref peak));

        static void InterlockedMax(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value && Interlocked.CompareExchange(ref location, value, current) is var seen && seen != current)
            {
                current = seen;
            }
        }
    }

    /// <summary>
    /// Single open-ledger reads handle synchronous and asynchronous archive stores, absent keys, live rows, and
    /// caller cancellation while an async store read and open-ledger scan overlap without leaking buffers.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SingleOpenLedgerReadsHandleSyncAndAsyncStoresAndCancellationWithoutLeaks()
    {
        var token = TestContext.Current.CancellationToken;
        var open = Cluster.OpenLedger(K);

        // Absent key on a single open ledger with sync store.
        var syncReader = await Cluster.StartProcessAsync(Cluster.Store);
        using (var absent = await syncReader.Archive.ReadAsync(Key, token))
        {
            Assert.Null(absent.Prefix);
            Assert.True(absent.Rows.IsEmpty);
            Assert.Equal(open.Id, absent.OpenLedger);
            Assert.True(absent.Through.ContainsKey(open.Id));
            Assert.Single(absent.Through);
        }

        // Single open ledger with live rows and async store overlapping the open-ledger scan.
        var a = await Cluster.AppendAsync(K, Append, "a");
        var b = await Cluster.AppendAsync(K, Append, "b");
        await Cluster.CarryTailAsync(K);

        var hooked = new HookedArchiveStore(Cluster.Store);
        var asyncReader = await Cluster.StartProcessAsync(hooked);
        // Teach KnownContiguousTail so the next read enters the concurrent store+open-scan branch.
        using (var warm = await asyncReader.Archive.ReadAsync(Key, token))
        {
            Assert.Equal([(a, Append, "a", false), (b, Append, "b", false)], ArchiveRows.Of(warm.Rows));
        }

        Assert.True(asyncReader.Reader.KnownContiguousTail(open) >= new Slot(2));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooked.BeforeNextRead = _ => release.Task;
        var pending = asyncReader.Archive.ReadAsync(Key, token).AsTask();
        Assert.False(pending.IsCompleted);
        release.SetResult();
        using (var overlapped = await pending)
        {
            Assert.Equal("ab", ArchiveRows.Content(overlapped));
            Assert.Single(overlapped.Through);
        }

        // Caller cancellation while async store is held and open-ledger scan has already collected rows releases buffers cleanly.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooked.BeforeNextRead = async ct =>
        {
            _ = held.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct);
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelled = asyncReader.Archive.ReadAsync(Key, cancel.Token).AsTask();
        await held.Task.WaitAsync(token);
        await cancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    public sealed class Unbootstrapped : IAsyncLifetime
    {
        private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
        private ArchiveCluster? _cluster;

        private ArchiveCluster Cluster => _cluster!;

        public async ValueTask InitializeAsync() => _cluster = await ArchiveCluster.StartUnbootstrappedAsync();

        public async ValueTask DisposeAsync()
        {
            if (_cluster is not null)
            {
                await _cluster.DisposeAsync();
            }

            _leaks.Dispose();
        }

        /// <summary>A read before the bootstrap, which a map read after the call confirms, answers no rows and no ledger.</summary>
        [Fact]
        public async Task AReadBeforeTheBootstrapAnswersNoLedger()
        {
            using var read = await Cluster.Primary.Archive.ReadAsync(Key, Token);

            Assert.Null(read.Prefix);
            Assert.Empty(read.Rows.ToArray());
            Assert.Empty(read.Through);
            Assert.Null(read.OpenLedger);
            Assert.False(read.Linearizable);
        }

        /// <summary>A view which predates the bootstrap lists no ledger; the reader reads the map again and finds the key's.</summary>
        [Fact]
        public async Task AViewWhichPredatesTheBootstrapReadsTheMapAgain()
        {
            var reader = await Cluster.StartProcessAsync(Cluster.Store);
            Assert.Equal(0ul, reader.View.Current.Epoch);
            await Cluster.BootstrapAsync();
            var a = await Cluster.AppendAsync(K, Append, "a");
            await Cluster.CarryTailAsync(K);

            using var read = await reader.Archive.ReadAsync(Key, Token);

            Assert.Equal([(a, Append, "a", false)], ArchiveRows.Of(read.Rows));
            Assert.Equal(new Dictionary<LedgerId, Slot> { [a.Ledger] = new Slot(1) }, read.Through);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Bound, "Timed out waiting.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
        }
    }

    /// <summary>
    /// Appends a and b to a ledger it closes, c to the next, and starts a reader with a hooked store whose next read is
    /// followed, before the reader hears it, by the first ledger's drain and drop and a refresh of the reader's view.
    /// </summary>
    private async Task<(LedgerId First, ArchiveProcess Reader, HookedArchiveStore Hooked)> DrainAfterTheStoreAnswersAsync()
    {
        _ = await Cluster.AppendAsync(K, Append, "a");
        _ = await Cluster.AppendAsync(K, Append, "b");
        var first = await Cluster.ReplaceLedgerAsync(K);
        _ = await Cluster.AppendAsync(K, Append, "c");
        await Cluster.CarryTailAsync(K);
        var hooked = new HookedArchiveStore(Cluster.Store);
        var reader = await Cluster.StartProcessAsync(hooked);
        hooked.AfterNextRead = async () =>
        {
            await Cluster.DrainAsync(first);
            await Cluster.DropAsync(first);
            _ = await reader.View.ReadAsync(Token);
        };

        return (first, reader, hooked);
    }

    /// <summary>A fake clock which counts the timers armed on it, so a test can wait until a backoff has armed its delay.</summary>
    private sealed class CountingTimeProvider : FakeTimeProvider
    {
        private int _timers;
        private long _shortestDue = long.MaxValue;

        public int Timers => Volatile.Read(ref _timers);

        /// <summary>Gets the shortest due time a timer was armed with.</summary>
        public TimeSpan ShortestDue => TimeSpan.FromTicks(Volatile.Read(ref _shortestDue));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Counted once armed, so an advance after the count fires it.
            var timer = base.CreateTimer(callback, state, dueTime, period);
            long shortest;
            do
            {
                shortest = Volatile.Read(ref _shortestDue);
            }
            while (dueTime.Ticks < shortest && Interlocked.CompareExchange(ref _shortestDue, dueTime.Ticks, shortest) != shortest);

            _ = Interlocked.Increment(ref _timers);
            return timer;
        }
    }
}
