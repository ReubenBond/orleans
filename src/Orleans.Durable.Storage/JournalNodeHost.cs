using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage;

/// <summary>
/// The host side of <see cref="IJournalStoreGrain"/> on a silo (orleans.md §5.5, §7.2): it runs each forwarded write and
/// read on the node's Journals, through their gate (<see cref="LocalJournals"/>), once the node's serving gate admits it.
/// </summary>
/// <remarks>
/// <para>
/// A call is checked in order, each check before the Journals, so each refusal has no effect: the node's serving gate
/// (<see cref="LocalNode.ServingAsync"/>), which answers <see cref="NotThisNode"/> for another node's key, waits for the
/// registration (OR-I2), and, once the node stops, answers a write or a read <c>Moved</c> naming no owner and a page
/// <see cref="JournalForwardedPage.Expired"/>; then a key which is no journal key, refused with
/// <see cref="JournalRefusal.Argument"/>; then, for a write, a record which fails its verification against the key's row
/// key, refused with <see cref="JournalRefusal.Invalid"/> naming its rejection (JS-I5). The gate itself answers a write
/// <c>Throttled</c> before the Journals while the key's ledger is throttled (OR-I10). Each refusal travels in the reply.
/// A call never ends with <see cref="global::Orleans.Runtime.LimitExceededException"/> or <see cref="NodePlacementRefusedException"/>,
/// which the writing silo's router takes as Orleans' own refusal of the call, proof of no effect: the Journals' replica
/// calls convert both to <see cref="LedgerReplicaUnavailableException"/> (orleans.md §5.5).
/// </para>
/// <para>
/// The host owns a forwarded write's record, which Orleans decoded or, on a call within one silo, its proxy pinned: it moves
/// the record into Durable Storage's page pool and releases it on every path (OR-I18). A read's pages are copies the reply carries,
/// which the host never releases (<see cref="ForwardedReads"/>).
/// </para>
/// </remarks>
internal sealed class JournalNodeHost(LocalNode local, LocalJournals journals, ForwardedReads reads)
{
    /// <summary>Writes a forwarded entry on the Journals of the node the grain keyed by <paramref name="key"/> names.</summary>
    /// <remarks>The host owns the entry's record, and releases it on every path on which it receives it (OR-I18).</remarks>
    public Task<JournalForwardedWriteOutcome> WriteAsync(NodeId key, JournalForwardedEntry entry, CancellationToken cancellationToken) =>
        WriteCoreAsync(grain: null, scheduler: null, key, entry, cancellationToken);

    /// <summary>Runs a forwarded write called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<JournalForwardedWriteOutcome> WriteFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, JournalForwardedEntry entry, CancellationToken cancellationToken) =>
        WriteCoreAsync(grain, scheduler, key, entry, cancellationToken);

    private async Task<JournalForwardedWriteOutcome> WriteCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, JournalForwardedEntry entry, CancellationToken cancellationToken)
    {
        var received = entry.Record;
        try
        {
            if (scheduler is not null)
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }

            if (Refusal<JournalForwardedWriteOutcome>(await local.ServingAsync(key, cancellationToken).ConfigureAwait(false), NotThisNode.Instance, LocalJournals.Stopped) is { } refusal)
            {
                if (grain is not null && refusal.Value is NotThisNode)
                {
                    await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
                }

                return refusal;
            }

            if (RowKey(entry.Key) is not { } rowKey)
            {
                return NoJournalKey(entry.Key);
            }

            using var bytes = WireBuffers.MoveToArcBuffer(ref received);
            if (JournalEntryRecord.Decode(rowKey, bytes, out var record) is { } rejection)
            {
                return new JournalCallRefused(JournalRefusal.Invalid, rejection.Message);
            }

            using (record)
            {
                return Forwarded(await journals.WriteAsync(record, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            received.Dispose();
        }
    }

    /// <summary>Reads the journal <paramref name="journalKey"/> on the Journals of the node the grain keyed by <paramref name="key"/> names.</summary>
    public Task<JournalForwardedRead> ReadAsync(NodeId key, string journalKey, CancellationToken cancellationToken) =>
        ReadCoreAsync(grain: null, scheduler: null, key, journalKey, cancellationToken);

    /// <summary>Runs a forwarded read called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<JournalForwardedRead> ReadFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, string journalKey, CancellationToken cancellationToken) =>
        ReadCoreAsync(grain, scheduler, key, journalKey, cancellationToken);

    private async Task<JournalForwardedRead> ReadCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, string journalKey, CancellationToken cancellationToken)
    {
        if (scheduler is not null)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        // The read's deadline runs from its receipt, so the wait for the registration counts against it (orleans.md §5.5).
        var deadline = reads.DeadlineFromNow();
        if (Refusal<JournalForwardedRead>(await local.ServingAsync(key, cancellationToken).ConfigureAwait(false), NotThisNode.Instance, LocalJournals.Stopped) is { } refusal)
        {
            if (grain is not null && refusal.Value is NotThisNode)
            {
                await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
            }

            return refusal;
        }

        if (RowKey(journalKey) is not { } rowKey)
        {
            return NoJournalKey(journalKey);
        }

        return await reads.ReadAsync(ReadContentAsync, deadline, cancellationToken).ConfigureAwait(false);

        async Task<ForwardedReads.Source> ReadContentAsync(CancellationToken token) =>
            await journals.ReadAsync(rowKey, token).ConfigureAwait(false) switch
            {
                JournalMoved moved => moved,
                JournalReadResult result => await TakeAndDisposeAsync(result, token).ConfigureAwait(false),
                null => throw new UnreachableException(),
            };
    }

    /// <summary>Answers a page of a snapshot the node of the grain keyed by <paramref name="key"/> pinned.</summary>
    public Task<JournalForwardedPage> ReadPageAsync(NodeId key, Guid snapshot, long offset, CancellationToken cancellationToken) =>
        ReadPageCoreAsync(grain: null, scheduler: null, key, snapshot, offset, cancellationToken);

    /// <summary>Runs a forwarded page read called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<JournalForwardedPage> ReadPageFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, Guid snapshot, long offset, CancellationToken cancellationToken) =>
        ReadPageCoreAsync(grain, scheduler, key, snapshot, offset, cancellationToken);

    private async Task<JournalForwardedPage> ReadPageCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, Guid snapshot, long offset, CancellationToken cancellationToken)
    {
        if (scheduler is not null)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        var page = await local.ServingAsync(key, cancellationToken).ConfigureAwait(false) switch
        {
            LedgerStore or LocalNodeServing.Diskless => reads.Page(snapshot, offset),
            NotThisNode => NotThisNode.Instance,
            LocalNodeServing.Stopping => JournalForwardedPage.Expired.Instance,
            null => throw new UnreachableException(),
        };
        if (grain is not null && page.Value is NotThisNode)
        {
            await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
        }

        return page;
    }

    /// <summary>
    /// Releases a snapshot the node of the grain keyed by <paramref name="key"/> pinned for a read its reader abandoned;
    /// answers <see cref="NotThisNode"/> on another node's silo, and nothing otherwise.
    /// </summary>
    public async Task<NotThisNode?> ReleaseSnapshotAsync(NodeId key, Guid snapshot, CancellationToken cancellationToken)
    {
        var serving = await local.ServingAsync(key, cancellationToken).ConfigureAwait(false);
        if (serving.Value is LedgerStore or LocalNodeServing.Diskless)
        {
            reads.Release(snapshot);
        }

        return serving.Value as NotThisNode;
    }

    /// <summary>Runs a snapshot release called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal async Task ReleaseSnapshotFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, Guid snapshot)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (await ReleaseSnapshotAsync(key, snapshot, CancellationToken.None).ConfigureAwait(false) is not null)
        {
            await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the serving gate's refusal of a write or a read: <paramref name="notThisNode"/> for another node's key,
    /// <paramref name="stopping"/> once the node stops, and none while it serves the key's node.
    /// </summary>
    private static T? Refusal<T>(LocalNodeServing serving, T notThisNode, T stopping)
        where T : struct => serving switch
        {
            LedgerStore or LocalNodeServing.Diskless => null,
            NotThisNode => notThisNode,
            LocalNodeServing.Stopping => stopping,
            null => throw new UnreachableException(),
        };

    /// <summary>Returns the row key of the journal <paramref name="key"/>, or none when it is no journal key.</summary>
    private static JournalRowKey? RowKey(string key)
    {
        try
        {
            return JournalRowKey.Encode(key);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static JournalCallRefused NoJournalKey(string key) =>
        new(JournalRefusal.Argument, $"'{key}' is no journal key: it is empty, malformed UTF-16, or too long.");

    private static async Task<ForwardedReads.Source> TakeAndDisposeAsync(JournalReadResult result, CancellationToken cancellationToken)
    {
        using (result)
        {
            return await ForwardedReads.TakeAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }

    private static JournalForwardedWriteOutcome Forwarded(LocalJournalWriteOutcome outcome) => outcome switch
    {
        JournalWriteOutcome.Accepted accepted => accepted,
        JournalWriteOutcome.Rejected rejected => rejected,
        JournalWriteOutcome.ReplaceRequired replaceRequired => replaceRequired,
        JournalMoved moved => moved,
        JournalWriteOutcome.Fenced fenced => fenced,
        JournalWriteOutcome.Ambiguous ambiguous => ambiguous,
        LocalJournalWriteOutcome.Throttled throttled => throttled,
        LocalJournalWriteOutcome.Unavailable unavailable => unavailable,
        null => throw new UnreachableException(),
    };
}
