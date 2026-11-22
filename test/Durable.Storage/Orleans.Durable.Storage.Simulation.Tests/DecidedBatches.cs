using System.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The check of LG-I5's durable answers against the replicas (ledgers.md §2.4): every slot of a closed ledger below its
/// recorded <c>T</c> holds the batch of the proposal answered <see cref="ProposalOutcome.Durable"/> at it, or a marker its
/// writer proposed to end an aged segment. Each proposal's batch holds one row, whose payload is the 32-bit index which
/// names its proposal, and which resolves, from the ledger's first slot and after a rewind from any slot, to the version
/// its writer's <see cref="LedgerBatch.BeginRow"/> returned (LG-I12, LG-I13).
/// </summary>
internal static class DecidedBatches
{
    /// <summary>
    /// Scans <paramref name="closed"/>'s slots below its recorded <c>T</c> through <paramref name="reader"/>, from slot 0 and
    /// then from <paramref name="rewindFrom"/>, and asserts that each holds the proposal <paramref name="durableAt"/> names
    /// at it, or a marker, and that each proposal's row resolves to the version <paramref name="versionAt"/> holds for its
    /// slot; a scan no quorum answered starts again after <paramref name="backoff"/>. Each batch of the scan from slot 0
    /// is passed to <paramref name="observe"/> with its slot.
    /// </summary>
    internal static async Task VerifyAsync(
        LedgerReader reader,
        LedgerMetadata closed,
        IReadOnlyDictionary<long, int> durableAt,
        Func<Task> backoff,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<long, RowVersion>? versionAt = null,
        long rewindFrom = 0,
        Action<long, LedgerScanItem, LedgerRead.Data>? observe = null)
    {
        var end = Assert.IsType<LedgerStatus.Closed>(closed.Status.Value).T;
        foreach (var from in rewindFrom > 0 && rewindFrom < end.Value ? (long[])[0, rewindFrom] : [0])
        {
            while (true)
            {
                try
                {
                    var slot = from;
                    await foreach (var item in reader.ScanAsync(closed, new Slot(from), end, cancellationToken))
                    {
                        var read = item.Read;
                        Assert.True(read.TryGetValue(out LedgerRead.Data data), FormattableString.Invariant($"s{closed.Id}'s slot {slot}, below its recorded T {end}, read {read}"));
                        using (data)
                        {
                            Check(closed, slot, item, data, durableAt, versionAt);
                            if (from == 0)
                            {
                                observe?.Invoke(slot, item, data);
                            }
                        }

                        slot++;
                    }

                    Assert.Equal(end.Value, slot);
                    break;
                }
                catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                {
                    await backoff();
                }
            }
        }
    }

    private static void Check(LedgerMetadata closed, long slot, LedgerScanItem item, LedgerRead.Data data, IReadOnlyDictionary<long, int> durableAt, IReadOnlyDictionary<long, RowVersion>? versionAt)
    {
        if (data.View.Count == 0)
        {
            // A marker its writer proposed to end a segment, which no proposal names.
            Assert.False(durableAt.ContainsKey(slot), FormattableString.Invariant($"s{closed.Id}'s slot {slot} holds a marker where a proposal was durable"));
            return;
        }

        Assert.True(durableAt.TryGetValue(slot, out var proposal), FormattableString.Invariant($"s{closed.Id}'s slot {slot} holds a batch, though no proposal was durable at it"));
        Assert.Equal(proposal, IndexOf(data));
        if (versionAt is not null && versionAt.TryGetValue(slot, out var version))
        {
            foreach (var row in item.Rows)
            {
                Assert.True(version == row.Version, FormattableString.Invariant($"s{closed.Id}'s slot {slot} resolves to version {row.Version}, and its writer's BeginRow returned {version}"));
            }
        }
    }

    /// <summary>Returns the index which names the proposal of a batch's one row.</summary>
    internal static int IndexOf(LedgerRead.Data data)
    {
        foreach (var row in data.View)
        {
            return BitConverter.ToInt32(row.Body.ToArray());
        }

        throw new InvalidOperationException("A batch holds a row.");
    }
}
