using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A journal's read (journals.md §2.3, §8.2): its <see cref="CurrentVersion"/>, whether it <see cref="Exists"/>, its
/// <see cref="ContentLength"/>, and its content, all the fold of one history read through one cut, with the replace
/// request the read gives (<see cref="ReplaceRequested"/>). It owns the read, whose bytes it assembled before it was
/// returned; its caller disposes it, which releases the chunks <see cref="ReadPayloadsAsync"/> has not yielded.
/// </summary>
/// <remarks>
/// A result is used by one caller at a time: enumerating it concurrently with its disposal is a misuse, which may retain a
/// chunk whose pages the disposal released.
/// </remarks>
public sealed class JournalReadResult : IDisposable
{
    private readonly ArchiveRead _read;
    private readonly ArcBuffer[] _chunks;

    // The result's own lease on the prefix's content, a chunk when no unheld opener supersedes it.
    private readonly ArcBuffer _prefixContent;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalReadResult"/> class over <paramref name="read"/>, which it takes,
    /// and its fold <paramref name="state"/>, asking for a replace by <paramref name="store"/>'s request too.
    /// </summary>
    internal JournalReadResult(ArchiveRead read, JournalState? state, IArchiveStore store, JournalVersion? versionOverride = null)
    {
        _read = read;
        CurrentVersion = versionOverride ?? state?.CurrentVersion ?? JournalVersion.Origin;
        Exists = state?.Exists ?? false;
        ContentLength = state?.ContentLength ?? 0;
        _prefixContent = read.Prefix?.Content.Retain() ?? default;
        try
        {
            (_chunks, var baseLength) = FoldContent(_prefixContent, read.Rows);
            Debug.Assert(_chunks.Sum(static chunk => (long)chunk.Length) == ContentLength, "A read's chunks are its fold's content (journals.md §8.3).");
            Debug.Assert(baseLength == (state?.BaseLength ?? 0), "A read's base length matches its state fold's BaseLength (journals.md §8.7).");
            ReplaceRequested = (state?.IsReplaceRequested ?? false) || store.IsReplaceRequested(read.NextToken);
        }
        catch
        {
            _prefixContent.Dispose();
            throw;
        }
    }

    /// <summary>Gets the end of the live rows the read took of each ledger, its cut, which the read-bound oracle folds through.</summary>
    internal IReadOnlyDictionary<LedgerId, Slot> Through => _read.Through;

    /// <summary>Gets the journal's version: the version of the content the read returns.</summary>
    public JournalVersion CurrentVersion { get; }

    /// <summary>Gets a value indicating whether the journal exists (journals.md §2.1).</summary>
    public bool Exists { get; }

    /// <summary>Gets the length of the journal's content, which the chunks <see cref="ReadPayloadsAsync"/> yields sum to.</summary>
    public long ContentLength { get; }

    /// <summary>
    /// Gets a value indicating whether the read asks the journal's writer for a <c>Replace</c> (journals.md §2.4, §8.7):
    /// its content holds more than <c>(1 + α)</c> times the bytes of its base, the content its last opener or the
    /// archive's prefix began, with <c>α = 1</c>; or the archive store asks for one at the key's current token
    /// (<c>IsReplaceRequested</c>). A hint the writer latches until a <c>Replace</c> is accepted.
    /// </summary>
    public bool ReplaceRequested { get; }

    /// <summary>
    /// Yields the journal's content in order, in chunks unrelated to entry boundaries, each of which the caller then owns
    /// and disposes (conventions.md §1). The content is assembled already: enumerating reads no further.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The result was disposed.</exception>
    public async IAsyncEnumerable<ArcBuffer> ReadPayloadsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < _chunks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            yield return _chunks[index].Retain();
        }

        // The content is assembled; the asynchronous shape is the contract of streamed reads (journals.md §2.3, row 5.2).
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Releases the read, and with it every chunk not yielded.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _prefixContent.Dispose();
            _read.Dispose();
        }
    }

    /// <summary>
    /// Folds a read's content into chunks, in one pass over the rows the prefix does not hold, as the
    /// state's fold applies them (journals.md §4.2, §8.3): from the prefix's content, a <c>Replace</c> begins the content
    /// again with its body, a <c>Delete</c> empties it, and an <c>Append</c> adds its body. Empty chunks are left out.
    /// Returns the chunks, which <paramref name="prefixContent"/> and <paramref name="rows"/> own, and the length of the
    /// content's base, which the last opener or the prefix began.
    /// </summary>
    internal static (ArcBuffer[] Chunks, long BaseLength) FoldContent(ArcBuffer prefixContent, ReadOnlySpan<ArchiveRow> rows)
    {
        ArcBuffer single = default;
        List<ArcBuffer>? chunks = null;
        var count = 0;
        if (prefixContent.Length > 0)
        {
            single = prefixContent;
            count = 1;
        }

        long baseLength = prefixContent.Length;
        foreach (ref readonly var row in rows)
        {
            if (row.IsHeld)
            {
                continue;
            }

            switch (row.Kind)
            {
                case RowKind.Replace:
                    chunks?.Clear();
                    if (row.Body.Length > 0)
                    {
                        single = row.Body;
                        count = 1;
                    }
                    else
                    {
                        single = default;
                        count = 0;
                    }

                    baseLength = row.Body.Length;
                    break;
                case RowKind.Delete:
                    chunks?.Clear();
                    single = default;
                    count = 0;
                    baseLength = 0;
                    break;
                case RowKind.Message:
                    throw new UnreachableException("RowKind.Message is excluded from journal reads (LG-I15).");
                case RowKind.Append:
                    if (row.Body.Length > 0)
                    {
                        if (count == 0)
                        {
                            single = row.Body;
                            count = 1;
                        }
                        else
                        {
                            if (chunks is null)
                            {
                                chunks = [single, row.Body];
                            }
                            else
                            {
                                if (chunks.Count == 0)
                                {
                                    chunks.Add(single);
                                }

                                chunks.Add(row.Body);
                            }

                            count++;
                        }
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        ArcBuffer[] result = count switch
        {
            0 => [],
            1 => [single],
            _ => [.. chunks!],
        };
        return (result, baseLength);
    }
}
