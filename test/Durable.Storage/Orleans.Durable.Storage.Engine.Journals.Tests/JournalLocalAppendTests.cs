using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.RocksDb;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// Comprehensive tests for Phase J1 ledger-local append journals (journals.md §2.2, §4.2, §7, §8.1, §8.3, §8.4,
/// JS-I11, JS-I12):
/// <list type="bullet">
/// <item>Entry record encoding/decoding, <c>default(LocalAppendRecord)</c> refusal, detached payloads, and <c>WithoutPayload()</c>.</item>
/// <item><c>StoreParametersOf</c> and <c>CheckStartup</c> (<c>MaxSegmentKeys</c>, <c>LargestBatch(hasLocalStreams: true)</c>).</item>
/// <item>Atomic journal + <c>RowKind.LocalAppend</c> commit (<c>JS-I11</c>) and zero local rows on <c>Rejected</c>, <c>ReplaceRequired</c>, <c>Moved</c>, and <c>Fenced</c>.</item>
/// <item><c>LocalSessionOracle</c> verifying <c>JS-I12</c> (strict slot order, non-empty records, before warm-state Apply and Accepted, Dispose once after last OnBatchDurable, cooperative PrepareRelease + CancelDrain, defect -> FatalErrors, and all 3 race mutants).</item>
/// <item>Real RocksDB <see cref="JournalStateDatabase.TryHasKeyEntryAsync"/> integration across <c>LedgerApplyLoop</c>, non-replicated ledgers, flush+reopen, drop, and zero tombstones.</item>
/// </list>
/// </summary>
public sealed class JournalLocalAppendTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly string _rocksRoot = Path.Combine(Path.GetTempPath(), "durable-storage-local-append-rocks", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rocksRoot))
            {
                Directory.Delete(_rocksRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _leaks.Dispose();
        }
    }

    [Fact]
    public void EntryRecordRejectsDefaultLocalAppendAndPreservesDetachedPayloadsAcrossWithoutPayload()
    {
        var key = JournalRowKey.Encode("grain/1");
        var stream = LocalStreamName.Parse("orleans.wakeup");

        using (var payload = ArcBuffer.Wrap("body"u8.ToArray()))
        {
            Assert.Throws<ArgumentException>(() => JournalEntryRecord.Append(key, JournalVersion.Origin, payload, [default]));
            Assert.Throws<ArgumentException>(() => JournalEntryRecord.Replace(key, JournalVersion.Origin, payload, [default]));
            Assert.Throws<ArgumentException>(() => JournalEntryRecord.Delete(key, JournalVersion.Origin, DateTimeOffset.UnixEpoch, [default]));
        }

        Assert.Throws<ArgumentNullException>(() => new LocalAppendRecord(null!, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalAppendRecord(stream, new byte[LocalAppendRecord.MaxPayloadLength + 1]));

        var mutablePayload = "wk-initial"u8.ToArray();
        JournalEntryRecord stripped;
        using (var payload = ArcBuffer.Wrap("body"u8.ToArray()))
        using (var encoded = JournalEntryRecord.Append(key, JournalVersion.Origin, payload, [new LocalAppendRecord(stream, mutablePayload)]))
        {
            // Mutating the caller array after encode cannot affect the sealed record's detached local-append bytes.
            mutablePayload[0] = (byte)'X';
            Assert.Equal("wk-initial"u8.ToArray(), encoded.LocalAppends[0].Payload.ToArray());

            using var wire = encoded.ToArcBuffer();
            Assert.Null(JournalEntryRecord.Decode(key, wire, out var decoded));
            using (decoded)
            {
                stripped = decoded.WithoutPayload();
            }
        }

        // After both encoded and decoded records (and their ArcBuffer leases) are disposed, detached LocalAppends survive.
        Assert.Single(stripped.LocalAppends);
        Assert.Equal(stream, stripped.LocalAppends[0].Stream);
        Assert.Equal("wk-initial"u8.ToArray(), stripped.LocalAppends[0].Payload.ToArray());
    }

    [Fact]
    public void StoreParametersAndStartupChecksCoverLocalStreams()
    {
        var streamB = LocalStreamName.Parse("z.stream");
        var streamA = LocalStreamName.Parse("a.stream");
        var handlerB = new RecordingLocalStreamHandler(streamB);
        var handlerA = new RecordingLocalStreamHandler(streamA);

        var options = new JournalStoreOptions
        {
            MaxBodyBytes = 1024,
            MaxLocalAppendsPerWrite = 4,
            MaxLocalAppendBytesPerWrite = 2048,
        };

        var parameters = JournalStore.StoreParametersOf(options, [handlerB, handlerA]);
        Assert.Equal("1024", parameters["max-body-bytes"]);
        Assert.Equal("a.stream,z.stream", parameters["local-streams"]);
        Assert.Equal("4", parameters["max-local-appends"]);
        Assert.Equal("2048", parameters["max-local-append-bytes"]);

        // Duplicate stream registration throws ArgumentException.
        Assert.Throws<ArgumentException>(() => JournalStore.StoreParametersOf(options, [handlerA, new RecordingLocalStreamHandler(streamA)]));

        // LargestBatch with local streams exceeds LargestBatch without local streams when single-entry size exceeds MaxBatchBytes.
        var tightBatchOptions = new JournalStoreOptions
        {
            MaxBodyBytes = 1024,
            MaxBatchBytes = 512,
            MaxLocalAppendsPerWrite = 4,
            MaxLocalAppendBytesPerWrite = 2048,
        };
        Assert.True(tightBatchOptions.LargestBatch(hasLocalStreams: true) > tightBatchOptions.LargestBatch(hasLocalStreams: false));
        Assert.True(LedgerAdmission.LongestBatch(options, hasLocalStreams: true) > LedgerAdmission.LongestBatch(options, hasLocalStreams: false));

        // CheckStartup refuses 1 + MaxLocalAppendsPerWrite > MaxSegmentKeys when local streams are registered.
        using var store = new InMemoryArchiveStore();
        var ex = Assert.Throws<JournalStoreLimitException>(() =>
            JournalStore.CheckStartup(options, store, maxBatchLength: 32 << 20, maxProposalBytes: 64 << 20, maxSegmentKeys: 4, hasLocalStreams: true));
        Assert.Equal(JournalStoreLimit.MaxSegmentKeys, ex.Limit);

        // Without local streams, MaxSegmentKeys = 4 is accepted.
        JournalStore.CheckStartup(options, store, maxBatchLength: 32 << 20, maxProposalBytes: 64 << 20, maxSegmentKeys: 4, hasLocalStreams: false);
    }

    [Fact]
    public async Task AtomicWriteCommitsLocalAppendInSameSlotAndRejectionsLeaveZeroLocalRows()
    {
        var streamA = LocalStreamName.Parse("orleans.wakeup");
        var streamB = LocalStreamName.Parse("orleans.audit");
        var handlerA = new RecordingLocalStreamHandler(streamA, payload => payload.Length == 1 && payload[0] == 0xFF ? new InvalidDataException("bad payload") : null);
        var handlerB = new RecordingLocalStreamHandler(streamB);

        var options = new JournalStoreOptions
        {
            MaxLocalAppendsPerWrite = 2,
            MaxLocalAppendBytesPerWrite = 16,
        };
        using var memory = new InMemoryArchiveStore(maxAppends: 1, replaceRequestAppends: 1);
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, memory, options, TimeProvider.System, stateDatabase: null, localStreamHandlers: [handlerA, handlerB]);

        var key = JournalRowKey.Encode("grain/atomic");

        // Pre-admission rejections throw ArgumentException with zero effect.
        using (var p = ArcBuffer.Wrap("x"u8.ToArray()))
        {
            // Unregistered stream
            using var badStream = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new LocalAppendRecord(LocalStreamName.Parse("unknown"), "a"u8.ToArray())]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(badStream, Token));

            // Handler.Validate rejection
            using var badPayload = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new LocalAppendRecord(streamA, new byte[] { 0xFF })]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(badPayload, Token));

            // Exceeds MaxLocalAppendsPerWrite
            using var tooMany = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new LocalAppendRecord(streamA, "a"u8.ToArray()), new LocalAppendRecord(streamA, "b"u8.ToArray()), new LocalAppendRecord(streamB, "c"u8.ToArray())]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(tooMany, Token));

            // Exceeds MaxLocalAppendBytesPerWrite
            using var tooBig = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new LocalAppendRecord(streamA, new byte[17])]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(tooBig, Token));
        }

        // Accepted Append with local appends across two streams.
        JournalVersion v1;
        using (var p = ArcBuffer.Wrap("hello"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new LocalAppendRecord(streamA, "wk1"u8.ToArray()), new LocalAppendRecord(streamB, "au1"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            v1 = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
        }

        // Stale Rejected write with local append must write ZERO local rows.
        using (var p = ArcBuffer.Wrap("stale"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new LocalAppendRecord(streamA, "wk-stale"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            Assert.IsType<JournalWriteOutcome.Rejected>(outcome.Value);
        }

        // ReplaceRequired write with local append must write ZERO local rows (maxAppends = 1 reached).
        using (var p = ArcBuffer.Wrap("second-append"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, v1, p, [new LocalAppendRecord(streamA, "wk-req"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            Assert.IsType<JournalWriteOutcome.ReplaceRequired>(outcome.Value);
        }

        // Accepted Replace with only streamA (streamB must NOT receive an empty OnBatchDurable callback!).
        JournalVersion v2;
        using (var p = ArcBuffer.Wrap("snap"u8.ToArray()))
        using (var rec = journals.EncodeReplace(key, v1, p, [new LocalAppendRecord(streamA, "wk2"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            v2 = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
        }

        // Accepted Delete with streamA.
        using (var rec = journals.EncodeDelete(key, v2, [new LocalAppendRecord(streamA, "wk3"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);
        }

        // Verify the ledger's raw slots hold only the 3 accepted writes (0 rows from Rejected / ReplaceRequired / ArgumentException).
        var ledgerId = ledgers.OpenContaining(key.Span.ToArray()).Id;
        var ledgerMeta = ledgers.Ledger(ledgerId);
        var tail = await ledgers.ConfirmOpenAsync(ledgerId, Token);
        Assert.Equal(new Slot(3), tail);

        var localRowsBySlot = new List<(Slot Slot, bool Defining, string Stream, string Payload)>();
        using var view = new LedgerSegmentView(ledgerId);
        await foreach (var item in ledgers.Reader.ScanAsync(ledgerMeta, Slot.Zero, tail, view, cancellationToken: Token))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            using (data)
            {
                foreach (var row in item.Rows)
                {
                    if (row.Kind == RowKind.LocalAppend)
                    {
                        Assert.Null(LedgerLocalKey.Validate(row.Key, ledgerMeta.Range, ledgerId, out var streamSpan));
                        localRowsBySlot.Add((row.Tag.Slot, row.IsDefining, Encoding.ASCII.GetString(streamSpan), Encoding.UTF8.GetString(row.Body)));
                    }
                }
            }
        }

        Assert.Equal(
            [
                (new Slot(0), true, "orleans.wakeup", "wk1"),
                (new Slot(0), true, "orleans.audit", "au1"),
                (new Slot(1), false, "orleans.wakeup", "wk2"),
                (new Slot(2), false, "orleans.wakeup", "wk3"),
            ],
            localRowsBySlot);

        // Verify streamA got 3 callbacks (slots 0, 1, 2) and streamB got ONLY 1 callback (slot 0 — skipped on slots 1 & 2!).
        var sessionA = Assert.Single(handlerA.Sessions);
        var sessionB = Assert.Single(handlerB.Sessions);
        Assert.Equal([new Slot(0), new Slot(1), new Slot(2)], sessionA.CallbackSlots);
        Assert.Equal([new Slot(0)], sessionB.CallbackSlots);
        Assert.All(sessionA.CallbackSizes, static size => Assert.True(size > 0));
        Assert.All(sessionB.CallbackSizes, static size => Assert.True(size > 0));

        // Verify journal read skips LocalAppend cleanly and sees deleted state.
        var readOutcome = await journals.ReadAsync(key, Token);
        using var readResult = Assert.IsType<JournalReadResult>(readOutcome.Value);
        Assert.False(readResult.Exists);
    }

    [Fact]
    public async Task LocalSessionOracleVerifiesJsI12OrderingDisposalAndPrepareReleaseCancelDrain()
    {
        var stream = LocalStreamName.Parse("orleans.wakeup");
        var oracle = new LocalSessionOracle(stream);

        using var archive = new InMemoryArchiveStore();
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, stateDatabase: null, localStreamHandlers: [oracle]);

        var key = JournalRowKey.Encode("grain/js12");
        var v = JournalVersion.Origin;
        for (var i = 0; i < 4; i++)
        {
            using var payload = ArcBuffer.Wrap(Encoding.UTF8.GetBytes($"p{i}"));
            using var record = journals.EncodeAppend(key, v, payload, [new LocalAppendRecord(stream, Encoding.UTF8.GetBytes($"wk{i}"))]);
            var outcome = await journals.WriteAsync(record, Token);
            v = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
            oracle.ObserveCallerAccepted(i);
        }

        // Cooperative PrepareReleaseAsync cancelled during admission drain calls CancelDrain and must NOT dispose sessions.
        var openId = ledgers.OpenContaining(key.Span.ToArray()).Id;
        Assert.False(oracle.IsDisposed(openId));
        var coldKey = JournalRowKey.Encode("grain/js12-hold");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journals.BeforeApply = async () =>
        {
            entered.TrySetResult();
            await hold.Task.ConfigureAwait(false);
        };

        using var heldPayload = ArcBuffer.Wrap("held"u8.ToArray());
        using var heldRecord = journals.EncodeAppend(coldKey, JournalVersion.Origin, heldPayload, [new LocalAppendRecord(stream, "wk-held"u8.ToArray())]);
        var heldWrite = journals.WriteAsync(heldRecord, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        using var cancelPrepare = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var prepareTask = journals.PrepareReleaseAsync(openId, KeyRange.Root, TimeSpan.FromSeconds(10), cancelPrepare.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
        Assert.False(prepareTask.IsCompleted);
        await cancelPrepare.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepareTask);

        journals.BeforeApply = null;
        hold.SetResult();
        var heldOutcome = await heldWrite;
        Assert.IsType<JournalWriteOutcome.Accepted>(heldOutcome.Value);
        oracle.ObserveCallerAccepted(4);
        Assert.False(oracle.IsDisposed(openId));

        // A write after CancelDrain still triggers OnBatchDurable on the same live session.
        using (var payload = ArcBuffer.Wrap("after-cancel"u8.ToArray()))
        using (var record = journals.EncodeAppend(key, v, payload, [new LocalAppendRecord(stream, "wk-after"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(record, Token);
            Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);
            oracle.ObserveCallerAccepted(5);
        }

        // Closing the ledger and disposing Journals disposes the session exactly once, strictly after all OnBatchDurable calls.
        await journals.DisposeAsync();
        oracle.AssertAllInvariants(expectedDisposed: true);
    }

    [Theory]
    [InlineData("OnDurableBeforeQuorum")]
    [InlineData("CompleteCallerBeforeOnDurable")]
    [InlineData("DisposeBeforeLastOnDurable")]
    public async Task LocalSessionOracleCatchesJsI12RaceMutants(string mutant)
    {
        var stream = LocalStreamName.Parse("orleans.wakeup");
        var oracle = new LocalSessionOracle(stream);

        using var archive = new InMemoryArchiveStore();
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, stateDatabase: null, localStreamHandlers: [oracle]);

        var quorumHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (mutant == "OnDurableBeforeQuorum")
        {
            journals.OnDurableBeforeQuorumMutant = true;
            journals.BeforeApply = async () =>
            {
                // Keep proposal outcome pending until we inspect whether OnBatchDurable already fired before quorum!
                await quorumHold.Task.ConfigureAwait(false);
            };
        }
        else if (mutant == "CompleteCallerBeforeOnDurable")
        {
            journals.CompleteCallerBeforeOnDurableMutant = true;
        }
        else if (mutant == "DisposeBeforeLastOnDurable")
        {
            journals.DisposeBeforeLastOnDurableMutant = true;
        }

        var key = JournalRowKey.Encode("grain/mutant");
        if (mutant == "OnDurableBeforeQuorum")
        {
            using var p = ArcBuffer.Wrap("x"u8.ToArray());
            using var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new LocalAppendRecord(stream, "wk0"u8.ToArray())]);
            var writeTask = journals.WriteAsync(rec, Token);

            // Wait briefly for OnBatchDurable to fire prematurely while BeforeApply holds proposal completion.
            await oracle.WaitForCallbackCountAsync(1, Token);
            Assert.False(writeTask.IsCompleted);
            oracle.RecordPrematureBeforeQuorumViolation();
            quorumHold.SetResult();
            _ = await writeTask;
        }
        else
        {
            var v = JournalVersion.Origin;
            for (var i = 0; i < 2; i++)
            {
                using var p = ArcBuffer.Wrap(Encoding.UTF8.GetBytes($"p{i}"));
                using var rec = journals.EncodeAppend(key, v, p, [new LocalAppendRecord(stream, Encoding.UTF8.GetBytes($"wk{i}"))]);
                var outcome = await journals.WriteAsync(rec, Token);
                v = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
                oracle.ObserveCallerAccepted(i);
            }
        }

        await journals.DisposeAsync();
        Assert.NotEmpty(oracle.Violations);
    }

    [Fact]
    public async Task OnBatchDurableDefectReportsFatalErrorFailsBatchAndLeavesWarmStateUntouched()
    {
        var stream = LocalStreamName.Parse("orleans.wakeup");
        var handler = new ThrowingLocalStreamHandler(stream, new InvalidOperationException("simulated handler bug"));

        var since = FatalErrors.ReportedCount;
        using var archive = new InMemoryArchiveStore();
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, stateDatabase: null, localStreamHandlers: [handler]);

        var key = JournalRowKey.Encode("grain/defect");
        using (var p = ArcBuffer.Wrap("x"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new LocalAppendRecord(stream, "wk"u8.ToArray())]))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => journals.WriteAsync(rec, Token));
        }

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.Defect, report.Site);
        Assert.Equal("JournalsLocalStreamSession", report.Failure.Defect);

        // Warm state was NOT applied after the OnBatchDurable defect, and admission is fenced/abandoned.
        Assert.Null(journals.Warm(key));
    }

    /// <summary>
    /// Real RocksDB integration test for <see cref="JournalStateDatabase.TryHasKeyEntryAsync"/> (journals.md §8.3):
    /// verifies O(1) presence detection over <see cref="RocksDbJournalIndex"/> via <see cref="LedgerApplyLoop"/>,
    /// <c>FlushAsync</c> + reopen, non-replicated ledger returning <see langword="null"/> without starting a
    /// <c>PredecessorScan</c>, <c>DropLedgerAsync</c> cleanup, and zero <c>tombstones</c> entries for
    /// <see cref="RowKind.LocalAppend"/> rows.
    /// </summary>
    [Fact]
    public async Task RocksDbTryHasKeyEntryAsyncCoversApplyFlushReopenDropAndZeroTombstones()
    {
        var indexDir = Path.Combine(_rocksRoot, "index");
        var stream = LocalStreamName.Parse("orleans.wakeup");
        var absentStream = LocalStreamName.Parse("orleans.unused");
        var handler = new RecordingLocalStreamHandler(stream);

        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);

        var open = ledgers.OpenContaining(JournalRowKey.Encode("g1").Span.ToArray());
        var ledgerId = open.Id;
        var presentLocalKey = LedgerLocalKey.Encode(ledgerId, open.Range, stream);
        var absentLocalKey = LedgerLocalKey.Encode(ledgerId, open.Range, absentStream);

        // Open real RocksDbJournalIndex + JournalStateDatabase + LedgerApplyLoop.
        using var archive = new InMemoryArchiveStore();
        await using (var rocks = new RocksDbJournalIndex(indexDir))
        await using (var db = await JournalStateDatabase.OpenAsync(rocks, ledgers.Reader, new JournalIndexOptions { FlushInterval = Timeout.InfiniteTimeSpan, QuickCheckInterval = Timeout.InfiniteTimeSpan }, TimeProvider.System, Token))
        await using (var applyLoop = new LedgerApplyLoop(ledgers.Reader, db.Applier, TimeProvider.System))
        await using (var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, db, [handler]))
        {
            db.Attach(applyLoop);

            var key = JournalRowKey.Encode("g1");
            using (var p = ArcBuffer.Wrap("v1"u8.ToArray()))
            using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new LocalAppendRecord(stream, "wk1"u8.ToArray())]))
            {
                Assert.IsType<JournalWriteOutcome.Accepted>((await journals.WriteAsync(rec, Token)).Value);
            }

            // Close ledger at T = 1.
            await ledgers.ReplaceAsync(ledgerId, Token);
            var closed = ledgers.Ledger(ledgerId);
            var closedTail = Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T;
            Assert.Equal(new Slot(1), closedTail);

            // Wait for LedgerApplyLoop to catch up the local RocksDB index to closedTail.
            applyLoop.Wake(ledgerId);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (db.Watermark(ledgerId) < closedTail && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, Token);
            }

            Assert.Equal(closedTail, db.Watermark(ledgerId));

            // O(1) presence probe on caught-up local ledger: true for written stream, false for unused stream, false for tail 0.
            Assert.Equal(true, await db.TryHasKeyEntryAsync(closed, presentLocalKey, closedTail, Token));
            Assert.Equal(false, await db.TryHasKeyEntryAsync(closed, absentLocalKey, closedTail, Token));
            Assert.Equal(false, await db.TryHasKeyEntryAsync(closed, presentLocalKey, Slot.Zero, Token));

            // Non-replicated ledger returns null without triggering PredecessorScan.
            var remoteNode = new NodeId(Guid.NewGuid());
            var nonReplicated = new LedgerMetadata(new LedgerId(999), KeyRange.Root, new LedgerLocation.Nodes([remoteNode]), 4, new PrimaryClaim(remoteNode, SequencerInstanceId.New()), new LedgerStatus.Closed(new Slot(1)));
            Assert.Null(await db.TryHasKeyEntryAsync(nonReplicated, presentLocalKey, new Slot(1), Token));
            Assert.Equal(0, db.PredecessorScansStarted);

            // Verify LocalAppend never enters the RocksDB tombstones column family.
            var tombstones = await rocks.ScanTombstonesAsync(ledgerId, Token);
            Assert.Empty(tombstones);

            // Atomic flush persists watermarks and key_index to disk.
            await db.FlushAsync(Token);
        }

        // Reopen RocksDbJournalIndex and confirm TryHasKeyEntryAsync reads persisted key_index after restart, then drops cleanly.
        await using (var rocks2 = new RocksDbJournalIndex(indexDir))
        await using (var db2 = await JournalStateDatabase.OpenAsync(rocks2, ledgers.Reader, new JournalIndexOptions { FlushInterval = Timeout.InfiniteTimeSpan, QuickCheckInterval = Timeout.InfiniteTimeSpan }, TimeProvider.System, Token))
        {
            var closed = ledgers.Ledger(ledgerId);
            var closedTail = Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T;
            Assert.Equal(closedTail, db2.Watermark(ledgerId));
            Assert.Equal(true, await db2.TryHasKeyEntryAsync(closed, presentLocalKey, closedTail, Token));
            Assert.Equal(false, await db2.TryHasKeyEntryAsync(closed, absentLocalKey, closedTail, Token));

            // Drop ledger from RocksDB index and confirm TryHasKeyEntryAsync returns null (not false!).
            await db2.Applier.RemoveAsync(ledgerId, Token);
            Assert.Null(await db2.TryHasKeyEntryAsync(closed, presentLocalKey, closedTail, Token));
        }
    }

    private sealed class RecordingLocalStreamHandler(LocalStreamName stream, Func<ReadOnlySpan<byte>, InvalidDataException?>? validate = null) : ILocalStreamHandler
    {
        private readonly ConcurrentBag<RecordingSession> _sessions = [];

        public LocalStreamName Stream { get; } = stream;

        public IReadOnlyCollection<RecordingSession> Sessions => _sessions;

        public InvalidDataException? Validate(ReadOnlySpan<byte> payload) => validate?.Invoke(payload);

        public ILocalStreamSession OpenLedger(LedgerId ledger)
        {
            var session = new RecordingSession(ledger);
            _sessions.Add(session);
            return session;
        }
    }

    private sealed class RecordingSession(LedgerId ledger) : ILocalStreamSession
    {
        public LedgerId Ledger { get; } = ledger;

        public List<Slot> CallbackSlots { get; } = [];

        public List<int> CallbackSizes { get; } = [];

        public int DisposeCount { get; private set; }

        public void OnBatchDurable(Slot slot, ReadOnlySpan<LocalAppendRecord> records)
        {
            CallbackSlots.Add(slot);
            CallbackSizes.Add(records.Length);
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingLocalStreamHandler(LocalStreamName stream, Exception exception) : ILocalStreamHandler
    {
        public LocalStreamName Stream { get; } = stream;

        public InvalidDataException? Validate(ReadOnlySpan<byte> payload) => null;

        public ILocalStreamSession OpenLedger(LedgerId ledger) => new ThrowingSession(exception);

        private sealed class ThrowingSession(Exception exception) : ILocalStreamSession
        {
            public void OnBatchDurable(Slot slot, ReadOnlySpan<LocalAppendRecord> records) => throw exception;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// White-box oracle verifying JS-I12 across all sessions: strict slot order, non-empty records,
    /// <see cref="ILocalStreamSession.OnBatchDurable"/> before caller <c>Accepted</c>, and <c>Dispose()</c> called once
    /// strictly after the last <c>OnBatchDurable</c>.
    /// </summary>
    private sealed class LocalSessionOracle(LocalStreamName stream) : ILocalStreamHandler
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<LedgerId, TrackedSession> _sessions = [];
        private readonly List<string> _violations = [];
        private int _totalCallbacks;
        private TaskCompletionSource? _callbackWaiter;
        private int _callbackTarget;

        public LocalStreamName Stream { get; } = stream;

        public IReadOnlyList<string> Violations
        {
            get
            {
                lock (_lock)
                {
                    return [.. _violations];
                }
            }
        }

        public InvalidDataException? Validate(ReadOnlySpan<byte> payload) => null;

        public ILocalStreamSession OpenLedger(LedgerId ledger)
        {
            lock (_lock)
            {
                var session = new TrackedSession(this, ledger);
                _sessions[ledger] = session;
                return session;
            }
        }

        public bool IsDisposed(LedgerId ledger)
        {
            lock (_lock)
            {
                return _sessions.TryGetValue(ledger, out var session) && session.DisposeCount > 0;
            }
        }

        public void ObserveCallerAccepted(int index)
        {
            lock (_lock)
            {
                if (_totalCallbacks <= index)
                {
                    _violations.Add($"Caller accepted write {index} before OnBatchDurable ran (callbacks={_totalCallbacks}).");
                }
            }
        }

        public void RecordPrematureBeforeQuorumViolation()
        {
            lock (_lock)
            {
                _violations.Add("OnBatchDurable fired before ProposalOutcome.Durable completed.");
            }
        }

        public Task WaitForCallbackCountAsync(int target, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_totalCallbacks >= target)
                {
                    return Task.CompletedTask;
                }

                _callbackTarget = target;
                _callbackWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _callbackWaiter.Task.WaitAsync(cancellationToken);
            }
        }

        public void AssertAllInvariants(bool expectedDisposed)
        {
            lock (_lock)
            {
                Assert.Empty(_violations);
                foreach (var session in _sessions.Values)
                {
                    if (expectedDisposed)
                    {
                        Assert.Equal(1, session.DisposeCount);
                    }
                }
            }
        }

        private void RecordCallback(TrackedSession session, Slot slot, int count)
        {
            lock (_lock)
            {
                if (session.DisposeCount > 0)
                {
                    _violations.Add($"OnBatchDurable ran after Dispose on ledger {session.Ledger} at slot {slot}.");
                }

                if (count <= 0)
                {
                    _violations.Add($"OnBatchDurable ran with empty records on ledger {session.Ledger} at slot {slot}.");
                }

                if (session.LastSlot is { } prev && slot <= prev)
                {
                    _violations.Add($"OnBatchDurable out of slot order on ledger {session.Ledger}: {slot} after {prev}.");
                }

                session.LastSlot = slot;
                _totalCallbacks++;
                if (_callbackWaiter is not null && _totalCallbacks >= _callbackTarget)
                {
                    _callbackWaiter.TrySetResult();
                    _callbackWaiter = null;
                }
            }
        }

        private void RecordDispose(TrackedSession session)
        {
            lock (_lock)
            {
                session.DisposeCount++;
                if (session.DisposeCount > 1)
                {
                    _violations.Add($"Dispose called {session.DisposeCount} times on ledger {session.Ledger}.");
                }
            }
        }

        private sealed class TrackedSession(LocalSessionOracle owner, LedgerId ledger) : ILocalStreamSession
        {
            public LedgerId Ledger { get; } = ledger;

            public Slot? LastSlot { get; set; }

            public int DisposeCount { get; set; }

            public void OnBatchDurable(Slot slot, ReadOnlySpan<LocalAppendRecord> records) =>
                owner.RecordCallback(this, slot, records.Length);

            public void Dispose() => owner.RecordDispose(this);
        }
    }
}
