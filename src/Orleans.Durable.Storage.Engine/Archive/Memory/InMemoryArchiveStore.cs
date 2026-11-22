using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Memory;

/// <summary>
/// An archive store in memory, for tests and simulation (archive.md §9), a third provider with its own token encoding
/// (<see cref="InMemoryArchiveTokens"/>): each key's position is its generation, which each opener raises, and the appends
/// that generation holds. <see cref="Drain"/> takes one key's rows of a closed segment, as a drain would, computes its
/// write, checks its token against the key's chain (HI-I4), and applies it unless the store holds it.
/// </summary>
/// <remarks>
/// <para>
/// A read answers the key's prefix, or <c>Absent</c>, and never <c>Stale</c>: its state only grows. The prefix's token
/// and version base are the key's position, so the version base advances with every write; it records no coverage, since
/// the token suffices, and <see cref="Holds"/> holds a write's first <c>i</c> commands when the prefix's position is at or past the position
/// after them in the key's chain. Its parameters, an optional <see cref="MaxAppendBytes"/> and a limit on the appends one
/// generation holds, with the threshold at or below it at which <see cref="IsReplaceRequested"/> answers, let tests reach
/// <c>ReplaceRequired</c> at unit limits; it admits every key.
/// </para>
/// <para>The store keeps its own lease on each key's content, and <see cref="Dispose"/> releases them.</para>
/// </remarks>
public sealed class InMemoryArchiveStore : IArchiveStore, IDisposable
{
    /// <summary>The layout the store's record names, distinct from every archive-log layout.</summary>
    public const string Layout = "memory-store";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, KeyState> _keys = [with(StringComparer.Ordinal)];
    private readonly Func<UInt128> _newId;
    private readonly int? _maxAppends;
    private readonly int? _replaceRequestAppends;
    private byte[]? _record;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="InMemoryArchiveStore"/> class.</summary>
    /// <param name="newId">
    /// Draws the store's ID, non-zero, when its record is created; by default from the platform's random number
    /// generator. A simulation passes its seeded source.
    /// </param>
    /// <param name="maxAppendBytes">The largest <c>Append</c> body the store accepts, or <see langword="null"/> for unbounded.</param>
    /// <param name="maxAppends">
    /// The most appends an append run may bring one generation to, or <see langword="null"/> for unbounded; an opener's
    /// appends land in its base and always admit.
    /// </param>
    /// <param name="replaceRequestAppends">
    /// The appends of a generation at which the store asks for a <c>Replace</c>, at most <paramref name="maxAppends"/>, so
    /// the store asks from every token from which <c>Admits</c> refuses an <c>Append</c> (HI-I5); below it, a writer asked
    /// has headroom before the refusal. Required with <paramref name="maxAppends"/>; without it, <see langword="null"/>
    /// for never.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is not positive, or the threshold passes the limit.</exception>
    /// <exception cref="ArgumentException">The store has a limit on a generation's appends but no threshold.</exception>
    public InMemoryArchiveStore(Func<UInt128>? newId = null, int? maxAppendBytes = null, int? maxAppends = null, int? replaceRequestAppends = null)
    {
        if (maxAppendBytes is { } bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes, nameof(maxAppendBytes));
        }

        if (maxAppends is { } appends)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(appends, nameof(maxAppends));
        }

        if (replaceRequestAppends is { } threshold)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold, nameof(replaceRequestAppends));
            if (maxAppends is { } limit)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, limit, nameof(replaceRequestAppends));
            }
        }
        else if (maxAppends is not null)
        {
            throw new ArgumentException("A store which limits a generation's appends asks for a Replace at or before its limit (HI-I5).", nameof(replaceRequestAppends));
        }

        _newId = newId ?? (static () => ArchiveStoreId.NewRandom().Value);
        MaxAppendBytes = maxAppendBytes;
        _maxAppends = maxAppends;
        _replaceRequestAppends = replaceRequestAppends;
    }

    /// <inheritdoc/>
    public ArchiveStoreKind Kind => ArchiveStoreKind.Memory;

    /// <summary>Gets the bytes of the store's record, empty before its first <see cref="StoreRecordAsync"/>; for tests.</summary>
    internal ReadOnlyMemory<byte> RecordBytes
    {
        get
        {
            lock (_lock)
            {
                return _record;
            }
        }
    }

    /// <summary>
    /// Replaces the store's record with <paramref name="record"/>, as another process's first call would have created it;
    /// for tests.
    /// </summary>
    internal void ReplaceRecord(ArchiveStoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            _record = record.Encode();
        }
    }

    /// <inheritdoc/>
    public int? MaxAppendBytes { get; }

    /// <summary>
    /// Takes one key's rows of one closed segment, as a drain would, in segment order per ledger and in lineage order:
    /// computes the key's write, checks its token against the key's position (HI-I4) and its commands against the store's
    /// limits, and applies it unless the store holds it. A write the store holds, as a straggler drain's, changes nothing.
    /// </summary>
    /// <param name="key">The rows' key.</param>
    /// <param name="rows">The key's decided rows in the segment, in strictly increasing tag order, which the store borrows.</param>
    /// <exception cref="ArgumentException">
    /// The rows are none, of two segments, out of tag order, or marked held, or the key is not a key.
    /// </exception>
    /// <exception cref="ArchiveCorruptException">
    /// The rows store two tokens; the write's token is not its key's position and the store holds none of it, or it
    /// holds part of it; or the store's limits refuse one of its commands, which no admission proposes.
    /// </exception>
    /// <exception cref="NotSupportedException">The key's content would pass <see cref="int.MaxValue"/> bytes.</exception>
    public void Drain(ReadOnlyMemory<byte> key, ReadOnlySpan<ArchiveRow> rows)
    {
        KeyRange.CheckKey(key.Span);
        CheckRows(rows);
        var write = ArchiveWrite.OfRows(rows);
        var name = Convert.ToHexString(key.Span);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = _keys.GetValueOrDefault(name);
            var position = state?.Position ?? default;
            if (HoldsAt(position, write, write.Commands.Length))
            {
                return;
            }

            if (HoldsAt(position, write, 1))
            {
                throw new ArchiveCorruptException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The store holds part of the write of segment ({write.Segment.Ledger}, {write.Segment.Start}), from {write.Token}, and its key's chain is at {InMemoryArchiveTokens.Encode(position)}: a drain writes a write whole."));
            }

            // The memory store refuses a write from another position, as a drain out of order would make one, rather than
            // asserting, so its tests can drive one (archive §10).
            if (StoredTokens.Violation(write.Segment, write.Token, InMemoryArchiveTokens.Encode(position)) is { } violation)
            {
                throw new ArchiveCorruptException(violation);
            }

            CheckAdmitted(write);
            Apply(name, state, write, rows[(rows.Length - write.Commands.Length)..]);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_keys.TryGetValue(Convert.ToHexString(key.Span), out var state))
            {
                return ValueTask.FromResult<ArchiveStoreRead>(ArchiveStoreRead.Absent.Instance);
            }

            var token = InMemoryArchiveTokens.Encode(state.Position);
            return ValueTask.FromResult<ArchiveStoreRead>(new ArchiveStoreRead.Read(new ArchivePrefix(token, token, state.HasBase, state.Tombstone, state.Content, coverage: null)));
        }
    }

    /// <inheritdoc/>
    /// <remarks>The record's layout is <c>memory-store</c>, and its fields the store's parameters.</remarks>
    public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArchiveStoreRecord.CheckParameters(parameters);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_record is null)
            {
                var created = ArchiveStoreRecord.Create(new ArchiveStoreId(_newId()), Layout, Fields(), parameters);
                _record = created.Encode();
                return ValueTask.FromResult(created);
            }

            var stored = ArchiveStoreRecord.Decode(_record) switch
            {
                ArchiveStoreRecord record => record,
                InvalidDataException rejection => throw new ArchiveCorruptException("The memory store's record breaks its format.", rejection),
                null => throw new UnreachableException(),
            };

            stored.CheckStoreFieldsMatch(Layout, Fields());
            return ValueTask.FromResult(stored);
        }
    }

    /// <inheritdoc/>
    public ArchivePlan Begin(RowToken token) => new MemoryPlan(InMemoryArchiveTokens.Decode(token), _maxAppends);

    /// <inheritdoc/>
    public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, write.Commands.Length);
        return HoldsAt(InMemoryArchiveTokens.Decode(prefix.Token), write, count);
    }

    /// <inheritdoc/>
    public bool IsReplaceRequested(RowToken token)
    {
        var appends = InMemoryArchiveTokens.Decode(token).Appends;
        return _replaceRequestAppends is { } threshold && appends >= (ulong)threshold;
    }

    /// <inheritdoc/>
    public bool AdmitsKey(ReadOnlySpan<byte> key) => true;

    /// <summary>Releases every key's content.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var state in _keys.Values)
            {
                state.Content.Dispose();
            }
        }
    }

    /// <summary>Returns whether a key at <paramref name="position"/> holds the first <paramref name="count"/> commands of <paramref name="write"/>.</summary>
    private static bool HoldsAt(InMemoryArchivePosition position, ArchiveWrite write, int count)
    {
        if (count == 0)
        {
            return true;
        }

        var after = InMemoryArchiveTokens.Decode(write.Token);
        for (var index = 0; index < count; index++)
        {
            after = after.After(write.Commands[index]);
        }

        return position >= after;
    }

    /// <summary>Throws unless <paramref name="rows"/> are one segment's, in strictly increasing tag order, none held.</summary>
    private static void CheckRows(ReadOnlySpan<ArchiveRow> rows)
    {
        if (rows.IsEmpty)
        {
            throw new ArgumentException("A drain gives at least one row of the key.", nameof(rows));
        }

        for (var index = 0; index < rows.Length; index++)
        {
            if (rows[index].IsHeld)
            {
                throw new ArgumentException("A drain gives decided rows, none marked held.", nameof(rows));
            }

            if (rows[index].Segment != rows[0].Segment)
            {
                throw new ArgumentException("A drain gives one segment's rows.", nameof(rows));
            }

            if (index > 0 && rows[index].Tag <= rows[index - 1].Tag)
            {
                throw new ArgumentException("A drain gives its rows in strictly increasing tag order.", nameof(rows));
            }

            if (rows[index].Token != rows[0].Token)
            {
                throw new ArchiveCorruptException("A key's rows in one segment store two tokens.");
            }
        }
    }

    private static ArcBuffer Concatenate(ArcBuffer? first, ReadOnlySpan<ArchiveRow> rows)
    {
        var length = (long)(first?.Length ?? 0);
        foreach (var row in rows)
        {
            length += row.Kind == RowKind.Delete ? 0 : row.BodyLength;
        }

        ArchivePrefix.CheckContentLength(length);
        using var writer = new ArcBufferWriter();
        if (first is { } prefix)
        {
            writer.Write(prefix.AsReadOnlySequence());
        }

        foreach (var row in rows)
        {
            if (row.Kind != RowKind.Delete)
            {
                writer.Write(row.Body.AsReadOnlySequence());
            }
        }

        return writer.Length == 0 ? default : writer.ConsumeSlice(writer.Length);
    }

    /// <summary>Throws unless the store's limits admit each of <paramref name="write"/>'s commands in turn.</summary>
    private void CheckAdmitted(ArchiveWrite write)
    {
        var plan = Begin(write.Token);
        foreach (var command in write.Commands)
        {
            var tooLong = command.Kind == RowKind.Append && command.ContentLength > (MaxAppendBytes ?? int.MaxValue);
            if (tooLong || !plan.Admits(command))
            {
                throw new ArchiveCorruptException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The write of segment ({write.Segment.Ledger}, {write.Segment.Start}) holds {command} after {plan.Count} commands, which the store's limits refuse: no admission proposes it."));
            }

            plan.Add(command);
        }
    }

    /// <summary>
    /// Applies <paramref name="write"/>, whose commands are <paramref name="rows"/>, to the key's state,
    /// <paramref name="state"/> or none.
    /// </summary>
    private void Apply(string key, KeyState? state, ArchiveWrite write, ReadOnlySpan<ArchiveRow> rows)
    {
        var position = state?.Position ?? default;
        foreach (var command in write.Commands)
        {
            position = position.After(command, write.Superseded);
        }

        ArcBuffer content;
        bool hasBase;
        ReadOnlyMemory<byte>? tombstone;
        if (write.IsOpener)
        {
            var opener = write.Commands[0];
            content = Concatenate(null, rows);
            hasBase = opener.Kind == RowKind.Replace;
            tombstone = write.Commands.Length == 1 ? opener.Tombstone : null;
        }
        else
        {
            content = Concatenate(state?.Content, rows);
            hasBase = state?.HasBase ?? false;
            tombstone = null;
        }

        state?.Content.Dispose();
        _keys[key] = new KeyState(position, hasBase, tombstone, content);
    }

    private Dictionary<string, string> Fields() => new(StringComparer.Ordinal)
    {
        ["max-append-bytes"] = MaxAppendBytes?.ToString(CultureInfo.InvariantCulture) ?? "unbounded",
        ["max-appends"] = _maxAppends?.ToString(CultureInfo.InvariantCulture) ?? "unbounded",
        ["replace-request-appends"] = _replaceRequestAppends?.ToString(CultureInfo.InvariantCulture) ?? "never",
    };

    /// <summary>A key's state: its position, whether a <c>Replace</c> opened its generation, its tombstone, and its content.</summary>
    private sealed record KeyState(InMemoryArchivePosition Position, bool HasBase, ReadOnlyMemory<byte>? Tombstone, ArcBuffer Content);

    /// <summary>
    /// The fold of the memory store's <c>Plan</c>: an opener opens the next generation, holding the appends after it, and
    /// an append run adds its appends to the generation, which an append run may bring to the store's limit.
    /// </summary>
    private sealed class MemoryPlan(InMemoryArchivePosition from, int? maxAppends) : ArchivePlan
    {
        private readonly InMemoryArchivePosition _from = from;
        private InMemoryArchivePosition _position = from;
        private bool _opened;

        public override RowToken End()
        {
            Debug.Assert(Count == 0 ? _position == _from : _position > _from, "Plan of a non-empty write lies above its token (HI-I3).");
            return InMemoryArchiveTokens.Encode(_position);
        }

        protected override void Apply(ArchiveCommand command)
        {
            _position = _position.After(command, Superseded);
            _opened |= command.IsOpener;
        }

        protected override bool AdmitsNext(ArchiveCommand command) =>
            command.IsOpener || _opened || maxAppends is not { } limit || _position.Appends < (ulong)limit;
    }
}
