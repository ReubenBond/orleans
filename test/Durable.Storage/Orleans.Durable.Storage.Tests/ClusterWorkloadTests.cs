using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The oracles of <see cref="ClusterWorkload"/>: what the history records of each write outcome, the check that every
/// answered read reports a version an acceptance reported, and the content of a slow-path history read.
/// </summary>
public sealed class ClusterWorkloadTests : IDisposable
{
    private static readonly JournalVersion First = JournalVersion.Origin.Next();
    private static readonly JournalVersion Second = First.Next();

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void EachOutcomeIsRecordedAsWhatItAssertsAndOnlyAnAmbiguousOneIsUnbounded()
    {
        Assert.Equal((JournalOutput.Accepted(First.ToString()), false), ClusterWorkload.Record(new JournalWriteOutcome.Accepted(First, ReplaceRequested: false)));
        Assert.Equal((JournalOutput.Rejected(Second.ToString(), deposed: true), false), ClusterWorkload.Record(new JournalWriteOutcome.Rejected(Second)));
        Assert.Equal((JournalOutput.ReplaceRequiredBy(deposed: true), false), ClusterWorkload.Record(JournalWriteOutcome.ReplaceRequired.Instance));
        Assert.Equal((JournalOutput.Moved, false), ClusterWorkload.Record(new JournalMoved(null)));
        Assert.Equal((JournalOutput.Fenced, false), ClusterWorkload.Record(JournalWriteOutcome.Fenced.Instance));
        Assert.Equal((JournalOutput.Ambiguous, true), ClusterWorkload.Record(JournalWriteOutcome.Ambiguous.Instance));
    }

    [Fact]
    public void AReadMayReportOriginAnAcceptedVersionOrARebase()
    {
        var rebase = new JournalVersion(new RowToken([7, 7]), 0);
        ClusterWorkload.AssertReadsReportAcceptedVersions(
        [
            Read(0, 1, JournalVersion.Origin),
            Append(0, 2, JournalVersion.Origin, JournalOutput.Accepted(First.ToString())),
            Read(1, 3, First),
            Read(1, 4, rebase),
        ]);
    }

    [Fact]
    public void AReadReportingAVersionNoAcceptanceReportedFails()
    {
        // An acceptance which misreported its version: the read reports the journal's real one.
        var failure = Assert.ThrowsAny<Exception>(() => ClusterWorkload.AssertReadsReportAcceptedVersions(
        [
            Append(0, 1, JournalVersion.Origin, JournalOutput.Accepted(Second.ToString())),
            Read(1, 2, First),
        ]));

        Assert.Contains($"reported version {First}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadReturningOnceAnUnboundedWriteWasInvokedIsNotChecked()
    {
        ClusterWorkload.AssertReadsReportAcceptedVersions(
        [
            Append(0, 1, JournalVersion.Origin, JournalOutput.Ambiguous, unbounded: true),
            Read(1, 2, First),
        ]);

        // A read invoked before the write, which overlaps it, may see it.
        ClusterWorkload.AssertReadsReportAcceptedVersions(
        [
            Read(1, 1, First, returned: 3),
            Append(0, 2, JournalVersion.Origin, JournalOutput.Ambiguous, unbounded: true),
        ]);

        _ = Assert.ThrowsAny<Exception>(() => ClusterWorkload.AssertReadsReportAcceptedVersions(
        [
            Read(1, 1, First),
            Append(0, 2, JournalVersion.Origin, JournalOutput.Ambiguous, unbounded: true),
        ]));
    }

    [Fact]
    public void TheSlowPathsContentIsThePrefixsThenEachRowItDoesNotHold()
    {
        using var content = Buffer("ab"u8);
        using var prefix = ArchivePrefix.Of(default, new RowToken([1]), hasBase: false, tombstone: null, content, coverage: null);
        using var appended = Row(1, RowKind.Append, "cd"u8);
        using var heldRow = Row(2, RowKind.Append, "xx"u8);
        var held = heldRow.AsHeld();
        Assert.Equal("abcd"u8.ToArray(), ClusterWorkload.SlowPathContent(prefix, [held, appended]));

        using var replaced = Row(3, RowKind.Replace, "ef"u8);
        using var after = Row(4, RowKind.Append, "g"u8);
        Assert.Equal("efg"u8.ToArray(), ClusterWorkload.SlowPathContent(prefix, [appended, replaced, after]));

        using var deleted = Row(5, RowKind.Delete, []);
        Assert.Empty(ClusterWorkload.SlowPathContent(prefix, [appended, deleted]));
        Assert.Equal("cd"u8.ToArray(), ClusterWorkload.SlowPathContent(null, [appended]));
    }

    [Fact]
    public void AFinalReadOfARebasedFoldNamesTheFoldsVersionOrAnEarlierName()
    {
        var rebase = new JournalVersion(new RowToken([1]), 0);
        using var content = Buffer("ab"u8);
        using var prefix = ArchivePrefix.Of(default, new RowToken([1]), hasBase: false, tombstone: null, content, coverage: null);

        Assert.True(ClusterWorkload.AssertFinalRead("key", rebase, exists: true, "ab"u8.ToArray(), prefix, []));
        Assert.True(ClusterWorkload.AssertFinalRead("key", Second, exists: true, "ab"u8.ToArray(), prefix, []));
        var failure = Assert.ThrowsAny<Exception>(() => ClusterWorkload.AssertFinalRead("key", new JournalVersion(new RowToken([2]), 0), exists: true, "ab"u8.ToArray(), prefix, []));
        Assert.Contains("neither the fold's", failure.Message, StringComparison.Ordinal);
        _ = Assert.ThrowsAny<Exception>(() => ClusterWorkload.AssertFinalRead("key", rebase, exists: true, "ax"u8.ToArray(), prefix, []));
    }

    [Fact]
    public void AFinalReadOfAFoldWithLiveRowsNamesTheFoldsVersion()
    {
        using var first = Row(1, RowKind.Append, "cd"u8);
        using var second = Row(2, RowKind.Append, "e"u8);

        Assert.False(ClusterWorkload.AssertFinalRead("key", Second, exists: true, "cde"u8.ToArray(), null, [first, second]));
        _ = Assert.ThrowsAny<Exception>(() => ClusterWorkload.AssertFinalRead("key", First, exists: true, "cde"u8.ToArray(), null, [first, second]));
        _ = Assert.ThrowsAny<Exception>(() => ClusterWorkload.AssertFinalRead("key", Second, exists: false, "cde"u8.ToArray(), null, [first, second]));
    }

    private static HistoryOperation<JournalInput, JournalOutput> Read(int client, long call, JournalVersion version, long? returned = null) =>
        new(client, JournalInput.Read("key"), call, JournalOutput.Read(exists: true, version.ToString(), default), returned ?? call);

    private static HistoryOperation<JournalInput, JournalOutput> Append(int client, long call, JournalVersion expected, JournalOutput output, bool unbounded = false) =>
        new(client, JournalInput.Append("key", expected.ToString(), new byte[] { 1 }), call, output, unbounded ? HistoryOperation.Unbounded : call);

    /// <summary>Returns a live row of <paramref name="body"/>, which holds its own lease on it.</summary>
    private static ArchiveRow Row(int slot, RowKind kind, ReadOnlySpan<byte> body)
    {
        var ledger = new LedgerId(1);
        using var buffer = Buffer(body);
        return ArchiveRow.Live(new Tag(ledger, new Slot(slot), 0), kind, new RowVersion(default, (ulong)slot), new LedgerSegmentId(ledger, Slot.Zero), default, buffer);
    }

    private static ArcBuffer Buffer(ReadOnlySpan<byte> bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }
}
