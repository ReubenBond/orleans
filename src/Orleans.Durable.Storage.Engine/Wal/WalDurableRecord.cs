using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// A zero-allocation proof that every page holding a WAL record has completed durable storage I/O (WAL-I1,
/// conventions.md §13).
/// </summary>
/// <remarks>
/// Only <see cref="WriteAheadLog"/> constructs a valid value, strictly after the record's write groups have completed
/// durable storage I/O without error. The default value holds no proof: reading <see cref="Position"/> or
/// <see cref="Page"/>, or calling <see cref="EnsureDurable"/>, throws <see cref="InvalidOperationException"/>.
/// </remarks>
public readonly struct WalDurableRecord : IEquatable<WalDurableRecord>
{
    private readonly WalPosition _position;
    private readonly WalPage _page;
    private readonly bool _durable;

    internal WalDurableRecord(WalPosition position, WalPage page)
    {
        ArgumentOutOfRangeException.ThrowIfZero(position.File);
        ArgumentOutOfRangeException.ThrowIfZero(page.File);
        _position = position;
        _page = page;
        _durable = true;
    }

    /// <summary>
    /// Gets a value indicating whether this token witnesses a completed durable WAL write rather than the default value.
    /// </summary>
    public bool IsValid => _durable;

    /// <summary>
    /// Gets the durable record's position in the log.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value is <see langword="default"/> and witnesses no durable write.</exception>
    public WalPosition Position => _durable ? _position : throw Uninitialized();

    /// <summary>
    /// Gets the page holding the durable record's first frame.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value is <see langword="default"/> and witnesses no durable write.</exception>
    public WalPage Page => _durable ? _page : throw Uninitialized();

    /// <summary>
    /// Verifies that this token was minted from a completed durable WAL write.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value is <see langword="default"/> and witnesses no durable write.</exception>
    public void EnsureDurable()
    {
        if (!_durable)
        {
            throw Uninitialized();
        }
    }

    /// <inheritdoc/>
    public bool Equals(WalDurableRecord other) =>
        _durable == other._durable && _position == other._position && _page == other._page;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is WalDurableRecord other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_durable, _position, _page);

    /// <inheritdoc/>
    public override string ToString() =>
        _durable
            ? string.Create(CultureInfo.InvariantCulture, $"{_position} ({_page})")
            : "WalDurableRecord(uninitialized)";

    /// <summary>Compares two durability proofs for equality.</summary>
    public static bool operator ==(WalDurableRecord left, WalDurableRecord right) => left.Equals(right);

    /// <summary>Compares two durability proofs for inequality.</summary>
    public static bool operator !=(WalDurableRecord left, WalDurableRecord right) => !left.Equals(right);

    private static InvalidOperationException Uninitialized() =>
        new("A default WalDurableRecord does not witness a durable WAL write (conventions.md §13).");
}
