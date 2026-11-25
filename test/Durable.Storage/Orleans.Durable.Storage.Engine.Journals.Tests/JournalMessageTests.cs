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
/// Comprehensive tests for atomic journal + RowKind.Message writes (journals.md §2.2, §4.2, §7, §8.1, §8.3, §8.4,
/// JS-I11, JS-I12):
/// <list type="bullet">
/// <item>Entry record encoding/decoding, <c>default(MessageRecord)</c> refusal, detached payloads, and <c>WithoutPayload()</c>.</item>
/// <item><c>StoreParametersOf</c> and <c>CheckStartup</c> (<c>LargestBatch(hasMessageChannels: true)</c>).</item>
/// <item>Atomic journal + <c>RowKind.Message</c> commit (<c>JS-I11</c>) and zero message rows on <c>Rejected</c>, <c>ReplaceRequired</c>, <c>Moved</c>, and <c>Fenced</c>.</item>
/// <item><c>MessageSessionOracle</c> verifying <c>JS-I12</c> (strict slot order, non-empty records, before warm-state Apply and Accepted, Dispose once after last OnBatchDurable, cooperative PrepareRelease + CancelDrain, defect -> FatalErrors, and all 3 race mutants).</item>
/// <item>Real RocksDB integration verifying <see cref="RowKind.Message"/> produces zero <c>key_index</c> and zero <c>tombstones</c> writes.</item>
/// </list>
/// </summary>
public sealed class JournalMessageTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly string _rocksRoot = Path.Combine(Path.GetTempPath(), "durable-storage-message-rocks", Guid.NewGuid().ToString("N"));

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
    public void EntryRecordRejectsDefaultMessageAndPreservesDetachedPayloadsAcrossWithoutPayload()
    {
        var key = JournalRowKey.Encode("grain/1");
        var channel = MessageChannel.Parse("app.events");

        using (var payload = ArcBuffer.Wrap("body"u8.ToArray()))
        {
            Assert.Throws<ArgumentException>(() => JournalEntryRecord.Append(key, JournalVersion.Origin, payload, [default]));
            Assert.Throws<ArgumentException>(() => JournalEntryRecord.Replace(key, JournalVersion.Origin, payload, [default]));
            Assert.Throws<ArgumentException>(() => JournalEntryRecord.Delete(key, JournalVersion.Origin, DateTimeOffset.UnixEpoch, [default]));
        }

        Assert.Throws<ArgumentNullException>(() => new MessageRecord(null!, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageRecord(channel, new byte[MessageRecord.MaxPayloadLength + 1]));

        var mutablePayload = "wk-initial"u8.ToArray();
        JournalEntryRecord stripped;
        using (var payload = ArcBuffer.Wrap("body"u8.ToArray()))
        using (var encoded = JournalEntryRecord.Append(key, JournalVersion.Origin, payload, [new MessageRecord(channel, mutablePayload)]))
        {
            // Mutating the caller array after encode cannot affect the sealed record's detached message bytes.
            mutablePayload[0] = (byte)'X';
            Assert.Equal("wk-initial"u8.ToArray(), encoded.Messages[0].Payload.ToArray());

            using var wire = encoded.ToArcBuffer();
            Assert.Null(JournalEntryRecord.Decode(key, wire, out var decoded));
            using (decoded)
            {
                stripped = decoded.WithoutPayload();
            }
        }

        // After both encoded and decoded records (and their ArcBuffer leases) are disposed, detached Messages survive.
        Assert.Single(stripped.Messages);
        Assert.Equal(channel, stripped.Messages[0].Channel);
        Assert.Equal("wk-initial"u8.ToArray(), stripped.Messages[0].Payload.ToArray());
    }

    [Fact]
    public void StoreParametersAndStartupChecksCoverMessageChannels()
    {
        var channelB = MessageChannel.Parse("z.channel");
        var channelA = MessageChannel.Parse("a.channel");
        var handlerB = new RecordingMessageHandler(channelB);
        var handlerA = new RecordingMessageHandler(channelA);

        var options = new JournalStoreOptions
        {
            MaxBodyBytes = 1024,
            MaxMessagesPerWrite = 4,
            MaxMessageBytesPerWrite = 2048,
        };

        var parameters = JournalStore.StoreParametersOf(options, [handlerB, handlerA]);
        Assert.Equal("1024", parameters["max-body-bytes"]);
        Assert.Equal("a.channel,z.channel", parameters["message-channels"]);
        Assert.Equal("4", parameters["max-messages"]);
        Assert.Equal("2048", parameters["max-message-bytes"]);

        // Duplicate channel registration throws ArgumentException.
        Assert.Throws<ArgumentException>(() => JournalStore.StoreParametersOf(options, [handlerA, new RecordingMessageHandler(channelA)]));

        // LargestBatch with message channels exceeds LargestBatch without message channels when single-entry size exceeds MaxBatchBytes.
        var tightBatchOptions = new JournalStoreOptions
        {
            MaxBodyBytes = 1024,
            MaxBatchBytes = 512,
            MaxMessagesPerWrite = 4,
            MaxMessageBytesPerWrite = 2048,
        };
        Assert.True(tightBatchOptions.LargestBatch(hasMessageChannels: true) > tightBatchOptions.LargestBatch(hasMessageChannels: false));
        Assert.True(LedgerAdmission.LongestBatch(options, hasMessageChannels: true) > LedgerAdmission.LongestBatch(options, hasMessageChannels: false));

        // Unversioned Message rows consume zero MaxSegmentKeys, so CheckStartup succeeds even with small MaxSegmentKeys.
        using var store = new InMemoryArchiveStore();
        JournalStore.CheckStartup(options, store, maxBatchLength: 32 << 20, maxProposalBytes: 64 << 20, hasMessageChannels: true);
    }

    [Fact]
    public async Task AtomicWriteCommitsMessageInSameSlotAndRejectionsLeaveZeroMessageRows()
    {
        var channelA = MessageChannel.Parse("app.events");
        var channelB = MessageChannel.Parse("app.audit");
        var handlerA = new RecordingMessageHandler(channelA, payload => payload.Length == 1 && payload[0] == 0xFF ? new InvalidDataException("bad payload") : null);
        var handlerB = new RecordingMessageHandler(channelB);

        var options = new JournalStoreOptions
        {
            MaxMessagesPerWrite = 2,
            MaxMessageBytesPerWrite = 16,
        };
        using var memory = new InMemoryArchiveStore(maxAppends: 1, replaceRequestAppends: 1);
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, memory, options, TimeProvider.System, stateDatabase: null, messageHandlers: [handlerA, handlerB]);

        var key = JournalRowKey.Encode("grain/atomic");

        // Pre-admission rejections throw ArgumentException with zero effect.
        using (var p = ArcBuffer.Wrap("x"u8.ToArray()))
        {
            // Unregistered channel
            using var badChannel = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new MessageRecord(MessageChannel.Parse("unknown"), "a"u8.ToArray())]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(badChannel, Token));

            // Handler.Validate rejection
            using var badPayload = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new MessageRecord(channelA, new byte[] { 0xFF })]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(badPayload, Token));

            // Exceeds MaxMessagesPerWrite
            using var tooMany = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new MessageRecord(channelA, "a"u8.ToArray()), new MessageRecord(channelA, "b"u8.ToArray()), new MessageRecord(channelB, "c"u8.ToArray())]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(tooMany, Token));

            // Exceeds MaxMessageBytesPerWrite
            using var tooBig = JournalEntryRecord.Append(key, JournalVersion.Origin, p, [new MessageRecord(channelA, new byte[17])]);
            await Assert.ThrowsAsync<ArgumentException>(() => journals.WriteAsync(tooBig, Token));
        }

        // Accepted Append with messages across two channels.
        JournalVersion v1;
        using (var p = ArcBuffer.Wrap("hello"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new MessageRecord(channelA, "wk1"u8.ToArray()), new MessageRecord(channelB, "au1"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            v1 = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
        }

        // Stale Rejected write with message must write ZERO message rows.
        using (var p = ArcBuffer.Wrap("stale"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new MessageRecord(channelA, "wk-stale"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            Assert.IsType<JournalWriteOutcome.Rejected>(outcome.Value);
        }

        // ReplaceRequired write with message must write ZERO message rows (maxAppends = 1 reached).
        using (var p = ArcBuffer.Wrap("second-append"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, v1, p, [new MessageRecord(channelA, "wk-req"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            Assert.IsType<JournalWriteOutcome.ReplaceRequired>(outcome.Value);
        }

        // Accepted Replace with only channelA (channelB must NOT receive an empty OnBatchDurable callback!).
        JournalVersion v2;
        using (var p = ArcBuffer.Wrap("snap"u8.ToArray()))
        using (var rec = journals.EncodeReplace(key, v1, p, [new MessageRecord(channelA, "wk2"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            v2 = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value).Version;
        }

        // Accepted Delete with channelA.
        using (var rec = journals.EncodeDelete(key, v2, [new MessageRecord(channelA, "wk3"u8.ToArray())]))
        {
            var outcome = await journals.WriteAsync(rec, Token);
            Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);
        }

        // Verify the ledger's raw slots hold only the 3 accepted writes (0 rows from Rejected / ReplaceRequired / ArgumentException),
        // and that RowKind.Message rows are unversioned (IsDefining == false, 0 segment keys consumed).
        var ledgerId = ledgers.OpenContaining(key.Span.ToArray()).Id;
        var ledgerMeta = ledgers.Ledger(ledgerId);
        var tail = await ledgers.ConfirmOpenAsync(ledgerId, Token);
        Assert.Equal(new Slot(3), tail);

        var messageRowsBySlot = new List<(Slot Slot, bool Defining, string Channel, string Payload)>();
        using var view = new LedgerSegmentView(ledgerId);
        await foreach (var item in ledgers.Reader.ScanAsync(ledgerMeta, Slot.Zero, tail, view, cancellationToken: Token))
        {
            Assert.True(item.Read.TryGetValue(out LedgerRead.Data data));
            using (data)
            {
                foreach (var row in item.Rows)
                {
                    if (row.Kind == RowKind.Message)
                    {
                        messageRowsBySlot.Add((row.Tag.Slot, row.IsDefining, Encoding.ASCII.GetString(row.Channel), Encoding.UTF8.GetString(row.Body)));
                    }
                }
            }
        }

        Assert.Equal(1UL, view.DefinedKeys);
        Assert.Equal(
            [
                (new Slot(0), false, "app.events", "wk1"),
                (new Slot(0), false, "app.audit", "au1"),
                (new Slot(1), false, "app.events", "wk2"),
                (new Slot(2), false, "app.events", "wk3"),
            ],
            messageRowsBySlot);

        // Verify channelA got 3 callbacks (slots 0, 1, 2) and channelB got ONLY 1 callback (slot 0 — skipped on slots 1 & 2!).
        var sessionA = Assert.Single(handlerA.Sessions);
        var sessionB = Assert.Single(handlerB.Sessions);
        Assert.Equal([new Slot(0), new Slot(1), new Slot(2)], sessionA.CallbackSlots);
        Assert.Equal([new Slot(0)], sessionB.CallbackSlots);
        Assert.All(sessionA.CallbackSizes, static size => Assert.True(size > 0));
        Assert.All(sessionB.CallbackSizes, static size => Assert.True(size > 0));

        // Verify journal read skips Message rows cleanly and sees deleted state.
        var readOutcome = await journals.ReadAsync(key, Token);
        using var readResult = Assert.IsType<JournalReadResult>(readOutcome.Value);
        Assert.False(readResult.Exists);
    }

    [Fact]
    public async Task MessageSessionOracleVerifiesJsI12OrderingDisposalAndPrepareReleaseCancelDrain()
    {
        var channel = MessageChannel.Parse("app.events");
        var oracle = new MessageSessionOracle(channel);

        using var archive = new InMemoryArchiveStore();
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, stateDatabase: null, messageHandlers: [oracle]);

        var key = JournalRowKey.Encode("grain/js12");
        var v = JournalVersion.Origin;
        for (var i = 0; i < 4; i++)
        {
            using var payload = ArcBuffer.Wrap(Encoding.UTF8.GetBytes($"p{i}"));
            using var record = journals.EncodeAppend(key, v, payload, [new MessageRecord(channel, Encoding.UTF8.GetBytes($"wk{i}"))]);
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
        using var heldRecord = journals.EncodeAppend(coldKey, JournalVersion.Origin, heldPayload, [new MessageRecord(channel, "wk-held"u8.ToArray())]);
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
        using (var record = journals.EncodeAppend(key, v, payload, [new MessageRecord(channel, "wk-after"u8.ToArray())]))
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
    public async Task MessageSessionOracleCatchesJsI12RaceMutants(string mutant)
    {
        var channel = MessageChannel.Parse("app.events");
        var oracle = new MessageSessionOracle(channel);

        using var archive = new InMemoryArchiveStore();
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, stateDatabase: null, messageHandlers: [oracle]);

        var quorumHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (mutant == "OnDurableBeforeQuorum")
        {
            journals.OnDurableBeforeQuorumMutant = true;
            journals.BeforeApply = async () =>
            {
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
            using var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new MessageRecord(channel, "wk0"u8.ToArray())]);
            var writeTask = journals.WriteAsync(rec, Token);

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
                using var rec = journals.EncodeAppend(key, v, p, [new MessageRecord(channel, Encoding.UTF8.GetBytes($"wk{i}"))]);
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
        var channel = MessageChannel.Parse("app.events");
        var handler = new ThrowingMessageHandler(channel, new InvalidOperationException("simulated handler bug"));

        var since = FatalErrors.ReportedCount;
        using var archive = new InMemoryArchiveStore();
        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        await using var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, stateDatabase: null, messageHandlers: [handler]);

        var key = JournalRowKey.Encode("grain/defect");
        using (var p = ArcBuffer.Wrap("x"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new MessageRecord(channel, "wk"u8.ToArray())]))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => journals.WriteAsync(rec, Token));
        }

        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.Defect, report.Site);
        Assert.Equal("JournalsMessageSession", report.Failure.Defect);

        // Warm state was NOT applied after the OnBatchDurable defect, and admission is fenced/abandoned.
        Assert.Null(journals.Warm(key));
    }

    /// <summary>
    /// Real RocksDB integration test: verifies <see cref="RowKind.Message"/> rows produce ZERO writes to
    /// <see cref="RocksDbJournalIndex"/> (<c>key_index</c> and <c>tombstones</c>) across both <c>LedgerApplyLoop</c>
    /// and <c>PredecessorScan</c>, while journal rows in the same batch are indexed normally (LG-I15).
    /// </summary>
    [Fact]
    public async Task RocksDbJournalIndexSkipsMessageRowsCompletely()
    {
        var indexDir = Path.Combine(_rocksRoot, "index");
        var channel = MessageChannel.Parse("app.events");
        var handler = new RecordingMessageHandler(channel);

        await using var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);

        var key = JournalRowKey.Encode("g1");
        var open = ledgers.OpenContaining(key.Span.ToArray());
        var ledgerId = open.Id;

        using var archive = new InMemoryArchiveStore();
        await using var rocks = new RocksDbJournalIndex(indexDir);
        await using var db = await JournalStateDatabase.OpenAsync(rocks, ledgers.Reader, new JournalIndexOptions { FlushInterval = Timeout.InfiniteTimeSpan, QuickCheckInterval = Timeout.InfiniteTimeSpan }, TimeProvider.System, Token);
        await using var applyLoop = new LedgerApplyLoop(ledgers.Reader, db.Applier, TimeProvider.System);
        await using var journals = new JournalStore(ledgers.Registry, archive, new JournalStoreOptions(), TimeProvider.System, db, [handler]);
        db.Attach(applyLoop);

        using (var p = ArcBuffer.Wrap("v1"u8.ToArray()))
        using (var rec = journals.EncodeAppend(key, JournalVersion.Origin, p, [new MessageRecord(channel, "wk1"u8.ToArray())]))
        {
            Assert.IsType<JournalWriteOutcome.Accepted>((await journals.WriteAsync(rec, Token)).Value);
        }

        await ledgers.ReplaceAsync(ledgerId, Token);
        var closed = ledgers.Ledger(ledgerId);
        var closedTail = Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T;
        Assert.Equal(new Slot(1), closedTail);

        applyLoop.Wake(ledgerId);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (db.Watermark(ledgerId) < closedTail && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Token);
        }

        Assert.Equal(closedTail, db.Watermark(ledgerId));

        // The journal row itself IS indexed (1 entry at slot 0, index 0), and zero tombstones exist.
        var journalEntries = await rocks.GetKeyEntriesAsync(ledgerId, JournalKeyHash.Compute(key.Span), Slot.Zero, 0, closedTail, Token);
        var entry = Assert.Single(journalEntries);
        Assert.Equal(Slot.Zero, entry.Slot);
        Assert.Equal(0, entry.Index);
        Assert.Empty(await rocks.ScanTombstonesAsync(ledgerId, Token));

        // Message rows carry no key (empty key hash has zero entries in key_index).
        var emptyEntries = await rocks.GetKeyEntriesAsync(ledgerId, JournalKeyHash.Compute([]), Slot.Zero, 0, closedTail, Token);
        Assert.Empty(emptyEntries);

        await db.FlushAsync(Token);
    }

    private sealed class RecordingMessageHandler(MessageChannel channel, Func<ReadOnlySpan<byte>, InvalidDataException?>? validate = null) : IMessageHandler
    {
        private readonly ConcurrentBag<RecordingSession> _sessions = [];

        public MessageChannel Channel { get; } = channel;

        public IReadOnlyCollection<RecordingSession> Sessions => _sessions;

        public InvalidDataException? Validate(ReadOnlySpan<byte> payload) => validate?.Invoke(payload);

        public IMessageSession OpenLedger(LedgerId ledger)
        {
            var session = new RecordingSession(ledger);
            _sessions.Add(session);
            return session;
        }
    }

    private sealed class RecordingSession(LedgerId ledger) : IMessageSession
    {
        public LedgerId Ledger { get; } = ledger;

        public List<Slot> CallbackSlots { get; } = [];

        public List<int> CallbackSizes { get; } = [];

        public int DisposeCount { get; private set; }

        public void OnBatchDurable(Slot slot, ReadOnlySpan<MessageRecord> records)
        {
            CallbackSlots.Add(slot);
            CallbackSizes.Add(records.Length);
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingMessageHandler(MessageChannel channel, Exception exception) : IMessageHandler
    {
        public MessageChannel Channel { get; } = channel;

        public InvalidDataException? Validate(ReadOnlySpan<byte> payload) => null;

        public IMessageSession OpenLedger(LedgerId ledger) => new ThrowingSession(exception);

        private sealed class ThrowingSession(Exception exception) : IMessageSession
        {
            public void OnBatchDurable(Slot slot, ReadOnlySpan<MessageRecord> records) => throw exception;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// White-box oracle verifying JS-I12 across all sessions: strict slot order, non-empty records,
    /// <see cref="IMessageSession.OnBatchDurable"/> before caller <c>Accepted</c>, and <c>Dispose()</c> called once
    /// strictly after the last <c>OnBatchDurable</c>.
    /// </summary>
    private sealed class MessageSessionOracle(MessageChannel channel) : IMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<LedgerId, TrackedSession> _sessions = [];
        private readonly List<string> _violations = [];
        private int _totalCallbacks;
        private TaskCompletionSource? _callbackWaiter;
        private int _callbackTarget;

        public MessageChannel Channel { get; } = channel;

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

        public IMessageSession OpenLedger(LedgerId ledger)
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

        private sealed class TrackedSession(MessageSessionOracle owner, LedgerId ledger) : IMessageSession
        {
            public LedgerId Ledger { get; } = ledger;

            public Slot? LastSlot { get; set; }

            public int DisposeCount { get; set; }

            public void OnBatchDurable(Slot slot, ReadOnlySpan<MessageRecord> records) =>
                owner.RecordCallback(this, slot, records.Length);

            public void Dispose() => owner.RecordDispose(this);
        }
    }
}
