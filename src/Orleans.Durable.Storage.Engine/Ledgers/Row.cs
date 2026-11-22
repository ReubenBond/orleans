using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One keyed change, <c>(Key, Kind, Body)</c>, which owns its key and a lease on its body
/// (ledgers.md §2.2, §4; conventions.md §1).
/// </summary>
/// <remarks>
/// Construction retains its own lease on the caller's body, possibly empty, which <see cref="Dispose"/> releases; the
/// caller keeps and disposes its own. A row's version, token, and ordinal are not part of it: a batch lends its rows as
/// <see cref="BatchRow"/>s, its segment resolves them as <see cref="ResolvedRow"/>s, and <see cref="ResolvedRow.ToRow"/>
/// makes one an owned <see cref="Row"/>.
/// </remarks>
public readonly struct Row : IDisposable
{
    private readonly byte[] _key;

    /// <summary>Initializes a new instance of the <see cref="Row"/> struct, copying the key.</summary>
    /// <param name="key">The row's key, 1 to <see cref="KeyRange.MaxKeyLength"/> bytes.</param>
    /// <param name="kind">How the row changes its key's state.</param>
    /// <param name="body">The row's body, possibly empty, of which the row retains its own lease.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is empty or longer than <see cref="KeyRange.MaxKeyLength"/> bytes, or
    /// <paramref name="kind"/> is undefined.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="body"/> has been released.</exception>
    public Row(ReadOnlySpan<byte> key, RowKind kind, ArcBuffer body)
    {
        KeyRange.CheckKey(key);
        if (!RowKinds.IsDefined(kind))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Row kind {(byte)kind} is not defined."), nameof(kind));
        }

        _key = key.ToArray();
        Kind = kind;
        Body = body.Retain();
    }

    private Row(RowKind kind, byte[] key, ArcBuffer body)
    {
        Debug.Assert(key.Length is >= 1 and <= KeyRange.MaxKeyLength, "A row's key is 1 to 65,535 bytes.");
        Debug.Assert(RowKinds.IsDefined(kind), "A row's kind is defined.");
        _key = key;
        Kind = kind;
        Body = body;
    }

    /// <summary>Gets the row's key.</summary>
    public ReadOnlyMemory<byte> Key => _key;

    /// <summary>Gets how the row changes its key's state.</summary>
    public RowKind Kind { get; }

    /// <summary>Gets the row's body, whose lease the row owns.</summary>
    public ArcBuffer Body { get; }

    /// <summary>Releases the row's lease on its body.</summary>
    public void Dispose() => Body.Dispose();

    /// <summary>
    /// Returns a row which shares this row's key, which no row changes, and holds its own lease on the body, which
    /// it retains without copying it; the caller disposes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The body has been released.</exception>
    public Row Retain() => new(Kind, _key, Body.Retain());

    /// <summary>
    /// Makes a row which takes the key and body lease, which the caller has validated, as they are.
    /// </summary>
    internal static Row Adopt(byte[] key, RowKind kind, ArcBuffer body) => new(kind, key, body);
}
