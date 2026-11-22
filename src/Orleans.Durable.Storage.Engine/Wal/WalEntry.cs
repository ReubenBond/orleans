using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// A complete record observed by <see cref="WriteAheadLog.ReadAllAsync"/>.
/// </summary>
/// <param name="Position">The position of the record's first frame.</param>
/// <param name="Page">The page holding the record's first frame.</param>
/// <param name="Payload">
/// The record bytes, borrowed until the enumeration advances; <see cref="ArcBuffer.Retain"/> them to keep them.
/// </param>
public readonly record struct WalRecord(WalPosition Position, WalPage Page, ArcBuffer Payload);

/// <summary>
/// A run of hole pages: pages a crash left incomplete, which replay recorded. Records with bytes on these pages were
/// never acknowledged durable.
/// </summary>
/// <param name="Start">The position of the first hole byte.</param>
/// <param name="End">The position after the last hole byte.</param>
public readonly record struct WalTorn(WalPosition Start, WalPosition End);

/// <summary>
/// A run of invalid pages, or a valid page which breaks the frame rules. The range was durable, so the records it held
/// are lost or damaged.
/// </summary>
/// <param name="Start">The position of the first damaged byte.</param>
/// <param name="End">The position after the last damaged byte.</param>
public readonly record struct WalCorruption(WalPosition Start, WalPosition End);

/// <summary>
/// One log entry observed by <see cref="WriteAheadLog.ReadAllAsync"/>: a <see cref="WalRecord"/>, a <see cref="WalTorn"/>
/// range, or a <see cref="WalCorruption"/> range.
/// </summary>
/// <remarks>
/// Pattern matching reads each case from its own field without boxing.
/// </remarks>
[Union]
public readonly struct WalEntry : IUnion
{
    private readonly WalRecord _record;
    private readonly WalTorn _torn;
    private readonly WalCorruption _corruption;
    private readonly Kind _kind;

    public WalEntry(WalRecord value)
    {
        _record = value;
        _kind = Kind.Record;
    }

    public WalEntry(WalTorn value)
    {
        _torn = value;
        _kind = Kind.Torn;
    }

    public WalEntry(WalCorruption value)
    {
        _corruption = value;
        _kind = Kind.Corruption;
    }

    private enum Kind : byte
    {
        None,
        Record,
        Torn,
        Corruption,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Record => _record,
        Kind.Torn => _torn,
        Kind.Corruption => _corruption,
        Kind.None => null,
        _ => throw new UnreachableException(),
    };

    public bool HasValue => _kind != Kind.None;

    public bool TryGetValue(out WalRecord value)
    {
        value = _record;
        return _kind == Kind.Record;
    }

    public bool TryGetValue(out WalTorn value)
    {
        value = _torn;
        return _kind == Kind.Torn;
    }

    public bool TryGetValue(out WalCorruption value)
    {
        value = _corruption;
        return _kind == Kind.Corruption;
    }

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

/// <summary>
/// The outcome of <see cref="WriteAheadLog.ReadAsync(WalPosition, CancellationToken)"/> or <see cref="WriteAheadLog.ReadAsync(WalPage, ulong, CancellationToken)"/>: the record's owned bytes, or the <see cref="WalCorruption"/>
/// range which holds them.
/// </summary>
[Union]
public readonly struct WalRead : IUnion
{
    private readonly ArcBuffer _buffer;
    private readonly WalCorruption _corruption;
    private readonly Kind _kind;

    public WalRead(ArcBuffer value)
    {
        _buffer = value;
        _kind = Kind.Buffer;
    }

    public WalRead(WalCorruption value)
    {
        _corruption = value;
        _kind = Kind.Corruption;
    }

    private enum Kind : byte
    {
        None,
        Buffer,
        Corruption,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Buffer => _buffer,
        Kind.Corruption => _corruption,
        Kind.None => null,
        _ => throw new UnreachableException(),
    };

    public bool HasValue => _kind != Kind.None;

    public bool TryGetValue(out ArcBuffer value)
    {
        value = _buffer;
        return _kind == Kind.Buffer;
    }

    public bool TryGetValue(out WalCorruption value)
    {
        value = _corruption;
        return _kind == Kind.Corruption;
    }

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
