using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// A store with the memory store's tokens and <c>Plan</c>, whose prefix a test sets: its token and, per segment, how many
/// of the segment's write's commands it holds, which its <c>Holds</c> answers, so a case holds part of a write, as row
/// 3.6's archive logs may after a drain which crashed inside an append run (archive.md §1, §8.1 step 4). Its prefix's
/// content is empty: the cases judge the chain, not the fold.
/// </summary>
internal sealed class PrefixCutStore : IArchiveStore, IDisposable
{
    private static readonly RowToken OriginBase = new([0xff]);

    private readonly InMemoryArchiveStore _tokens = new();

    /// <summary>Gets how many commands of each segment's write the prefix holds; none for a segment it does not name.</summary>
    public Dictionary<LedgerSegmentId, int> Held { get; } = [];

    /// <summary>Gets or sets the prefix's token.</summary>
    public RowToken Token { get; set; }

    public ArchiveStoreKind Kind => ArchiveStoreKind.Memory;

    public int? MaxAppendBytes => null;

    /// <summary>Returns the prefix the store answers, which the caller disposes.</summary>
    public ArchivePrefix Prefix() => new(Token, Token.IsEmpty ? OriginBase : Token, hasBase: false, tombstone: null, default, coverage: this);

    public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        ArchivePrefix? prefix = Prefix();
        try
        {
            var read = new ArchiveStoreRead.Read(prefix);
            prefix = null;
            return ValueTask.FromResult<ArchiveStoreRead>(read);
        }
        finally
        {
            prefix?.Dispose();
        }
    }

    public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        _tokens.StoreRecordAsync(parameters, cancellationToken);

    public ArchivePlan Begin(RowToken token) => _tokens.Begin(token);

    public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, write.Commands.Length);
        return count <= Held.GetValueOrDefault(write.Segment);
    }

    public bool IsReplaceRequested(RowToken token) => _tokens.IsReplaceRequested(token);

    public bool AdmitsKey(ReadOnlySpan<byte> key) => true;

    public void Dispose() => _tokens.Dispose();
}
