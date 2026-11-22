using System.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Proposes owned <see cref="Row"/>s, as the tests write them, through the batch a <see cref="LedgerWriter"/> creates
/// (ledgers.md §2.4): each row is written into the batch, which the proposal takes, with the empty token and, as its
/// version before, the version its writer's dictionary holds for its key in the open segment, or <c>(empty, 0)</c>.
/// </summary>
internal static class LedgerProposals
{
    /// <summary>
    /// Writes <paramref name="rows"/>, which the caller keeps, into one batch of <paramref name="writer"/> and proposes it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="rows"/> is empty or holds a default row, or the batch refuses a row (<see cref="LedgerBatch.BeginRow"/>).
    /// Nothing is appended, and the writer is abandoned, since the batch it created is never proposed.
    /// </exception>
    public static ValueTask<ProposalOutcome> ProposeAsync(this LedgerWriter writer, IReadOnlyList<Row> rows, CancellationToken cancellationToken = default) =>
        ProposeAsync(writer, rows, versions: null, cancellationToken);

    /// <summary>
    /// Writes <paramref name="rows"/> into one batch of <paramref name="writer"/> and proposes it, adding to
    /// <paramref name="versions"/> the version <see cref="LedgerBatch.BeginRow"/> returned for each row, in order, which a
    /// reader resolves the row to once it is decided (LG-I13).
    /// </summary>
    public static async ValueTask<ProposalOutcome> ProposeAsync(this LedgerWriter writer, IReadOnlyList<Row> rows, List<RowVersion>? versions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);
        var length = MaxLength(rows);
        using var batch = await writer.CreateBatchAsync(rows.Count, (int)Math.Min(length, writer.MaxBatchLength), cancellationToken);
        foreach (var row in rows)
        {
            var version = batch.AddRow(row.Key.Span, row.Kind, batch.HeldVersion(row.Key.Span), default, row.Body.AsReadOnlySequence());
            versions?.Add(version);
        }

        return await writer.ProposeAsync(batch, cancellationToken);
    }

    /// <summary>Returns the most bytes a batch of <paramref name="rows"/> takes, with empty version bases and tokens.</summary>
    /// <exception cref="ArgumentException"><paramref name="rows"/> is empty or holds a default row.</exception>
    public static long MaxLength(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("A batch holds at least one row.", nameof(rows));
        }

        long length = LedgerBatch.MaxHeaderLength(rows.Count);
        foreach (var row in rows)
        {
            if (row.Key.IsEmpty)
            {
                throw new ArgumentException("A batch holds no default Row, which no constructor made.", nameof(rows));
            }

            length += LedgerBatch.MaxRowLength(row.Key.Length, 0, 0, row.Body.Length);
        }

        return length;
    }
}
