using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// A ledger of segments a writer proposes from a script, whose rows' versions the writer recorded as it gave them, for
/// tests of what readers resolve (ledgers.md §2.5, LG-I13).
/// </summary>
internal static class LedgerSegmentScript
{
    internal const int Window = 4;
    internal static readonly LedgerId Id = new(3);
    internal static readonly NodeId[] Nodes = [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2), LedgerMapSamples.Node(3)];

    // The batches the writer proposes, by their rows' keys; an empty batch is a marker (EndSegmentAsync). With at most
    // three keys a segment, each row counted as a key it may define, segments start at slots 0, 2, 4, 6, 7 (the marker),
    // and 11, and the ledger ends mid-segment.
    internal static readonly string[] Script = ["a", "b", "ab", "c", "a", "d", "da", "", "b", "b", "ef", "a", "ac"];

    /// <summary>
    /// Proposes <see cref="Script"/> through a writer which ends its segments at three keys, recording each row's version as
    /// <see cref="LedgerBatch.BeginRow"/> returned it, and leaves the ledger open, listed in a map whose first value lists
    /// it alone.
    /// </summary>
    internal static async Task<(TestLedgerMap Map, LedgerMetadata Open, List<ScriptedRow> Expected)> WriteAsync(ILedgerReplicaTransport transport, CancellationToken token)
    {
        var map = await TestLedgerMap.OpenAsync(token);
        var expected = new List<ScriptedRow>();
        var tokens = new Dictionary<(long Segment, byte Key), RowToken>();
        var lifetime = new Sequencer(Window);
        var open = new LedgerMetadata(Id, KeyRange.Root, [.. Nodes], Window, new PrimaryClaim(Nodes[0], lifetime.InstanceId), LedgerStatus.Open.Instance);
        await using (var process = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null))
        {
            using var lease = process.LeaseLedger(open);
            var defaults = LedgerWriterOptions.Default;
            var host = new LedgerWriterHost(int.MaxValue, NullLogger.Instance, new LedgerWriterOptions(defaults.MaxSegmentBytes, 3, defaults.MaxSegmentAge, defaults.MaxProposalBytes));
            await using var writer = lease.CreateWriter(lifetime, map.View, host, new FakeTimeProvider());
            await map.PublishAsync(new LedgerMapValue(1, new LedgerId(Id.Value + 1), 0, [open]), token);
            for (var slot = 0; slot < Script.Length; slot++)
            {
                var keys = Script[slot];
                if (keys.Length == 0)
                {
                    Assert.Equal(new ProposalOutcome(new ProposalOutcome.Durable(new Slot(slot))), await writer.EndSegmentAsync(token));
                    continue;
                }

                var maxLength = LedgerBatch.MaxHeaderLength(keys.Length) + keys.Length * LedgerBatch.MaxRowLength(1, 0, 0, 8);
                using var batch = await writer.CreateBatchAsync(keys.Length, (int)maxLength, token);
                for (var index = 0; index < keys.Length; index++)
                {
                    var body = Body(slot, index);
                    // Each key's token names its key and the segment's first slot, so a row resolved in the wrong segment
                    // or to the wrong key shows.
                    var key = (byte)keys[index];
                    var segmentToken = new RowToken([key, (byte)batch.Segment!.Value.Start.Value]);
                    var defines = batch.HeldVersion([key]) == default && !tokens.ContainsKey((batch.Segment.Value.Start.Value, key));
                    if (defines)
                    {
                        tokens[(batch.Segment.Value.Start.Value, key)] = segmentToken;
                    }

                    var version = batch.AddRow([key], RowKind.Append, batch.HeldVersion([key]), segmentToken, new(Encoding.UTF8.GetBytes(body)));
                    expected.Add(new ScriptedRow(slot, keys[index], version, tokens[(batch.Segment.Value.Start.Value, key)], body));
                }

                Assert.Equal(new ProposalOutcome(new ProposalOutcome.Durable(new Slot(slot))), await writer.ProposeAsync(batch, token));
            }
        }

        return (map, open, expected);
    }

    /// <summary>
    /// Recovers <paramref name="open"/> in another process, which ends it at the script's last slot, mid-segment, and
    /// returns it closed there.
    /// </summary>
    internal static async Task<LedgerMetadata> RecoverAsync(TestLedgerMap map, ILedgerReplicaTransport transport, LedgerMetadata open, CancellationToken token)
    {
        await using var executor = new LedgerReader(map.View, transport, new FakeTimeProvider(), localNode: null);
        using var recovering = executor.LeaseLedger(open);
        var end = (await recovering.Ledger.RecoverAsync(token)).End;
        return open.Close(end);
    }

    internal static string Body(int slot, int index) => $"{slot:00}:{index}";

    /// <summary>Adds each row the item resolves to <paramref name="rows"/>.</summary>
    internal static void Collect(LedgerScanItem item, long slot, List<ScriptedRow> rows)
    {
        foreach (var row in item.Rows)
        {
            rows.Add(new ScriptedRow(slot, (char)row.Key[0], row.Version, row.Token, Encoding.UTF8.GetString(row.Body)));
        }
    }
}

/// <summary>One row as its writer versioned it, and as a scan resolves it.</summary>
internal readonly record struct ScriptedRow(long Slot, char Key, RowVersion Version, RowToken Token, string Body);
