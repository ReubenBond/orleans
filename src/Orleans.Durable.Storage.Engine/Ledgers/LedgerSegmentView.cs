using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Numerics;
using Orleans.Durable.Storage.Engine.Buffers;


namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The decoder of one ledger's segments (ledgers.md §2.2, §4, §7): it takes decoded batches in slot order, from a slot at
/// which a segment starts, checks each against its predecessors by the segment rules, and lends each batch's rows
/// resolved: each with its key, version, and token (<see cref="ResolvedRow"/>).
/// </summary>
/// <remarks>
/// <para>
/// A batch with the start flag begins a new segment, resetting the ordinals and the key table. The view owns a key table
/// which copies each defined key once, with its defining row's version base and token, and the key's count so far, so
/// each row's version is computed once, for every reader (LG-I13). It keeps counts and positions, never bodies, so its
/// caller releases each batch's pages once the view advances past it. A whole view's table is sized by the segment's
/// contents: every key it holds was defined in bytes the view decoded.
/// </para>
/// <para>
/// A key-filtered view records that key's version and token alone, and the positions of that key's rows in the batch it
/// last advanced past, so its memory is constant beside that key's rows; it lends that key's rows alone, and checks the
/// segment rules for that key only, beside each batch's segment offset and ordinal base.
/// </para>
/// <para>
/// A view which rejects a batch is unusable: the batches are corrupt (conventions.md §4), and every later
/// <see cref="Advance"/> throws <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public sealed class LedgerSegmentView : IDisposable
{
    internal const string ReasonOffsetMismatch = "segment-offset-mismatch";
    internal const string ReasonOrdinalBaseMismatch = "ordinal-base-mismatch";
    internal const string ReasonDuplicateKey = "duplicate-key";
    internal const string ReasonCountOverflow = "count-overflow";

    // The rows-per-batch capacity a whole view keeps across a segment start; a larger table is released there.
    private const int KeptRowCapacity = 256;

    // The key-table capacity a whole view keeps across a segment start; larger pooled buffers return to ArrayPool.
    private const int KeptKeyCapacity = 256;
    private const int KeptKeyArenaBytes = 8 * 1024;

    private readonly LedgerId _ledger;
    private readonly byte[]? _filter;

    // A whole view's pooled key table, by ordinal, its contiguous key-byte arena, and an open-addressing hash table
    // storing (entryIndex + 1) to detect duplicate keys without per-key byte[] or Dictionary allocations.
    private KeyEntry[] _keys = [];
    private int _keyCount;
    private byte[] _keyArena = [];
    private int _keyBytesUsed;
    private int[] _buckets = [];
    private int _bucketCapacity;

    // A filtered view's key, once its segment defines it, and its ordinal.
    private KeyEntry _filtered;
    private bool _hasFiltered;
    private ulong _filteredOrdinal;

    // The keys the segment defined, which the next batch's ordinal base names.
    private Slot _start;
    private Slot _last;
    private bool _started;

    // The batch the view last advanced past, which its caller owns: a whole view's state of each of its rows, and a
    // filtered view's rows of its key, each with its count after it.
    private ArcBuffer _batch;
    private RowState[] _rows = [];
    private readonly List<RowState> _filteredRows = [];
    private bool _broken;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="LedgerSegmentView"/> class, which resolves every key's rows.</summary>
    /// <param name="ledger">The ledger whose batches the view decodes, whose ID tags the rows it resolves.</param>
    public LedgerSegmentView(LedgerId ledger)
    {
        if (ledger == default)
        {
            throw new ArgumentException("A segment lies in a ledger; the default ledger ID names none.", nameof(ledger));
        }

        _ledger = ledger;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerSegmentView"/> class, which resolves the rows of
    /// <paramref name="key"/> alone, in memory constant beside that key's rows.
    /// </summary>
    /// <param name="ledger">The ledger whose batches the view decodes, whose ID tags the rows it resolves.</param>
    /// <param name="key">The key whose rows the view resolves, which it copies.</param>
    public LedgerSegmentView(LedgerId ledger, ReadOnlySpan<byte> key)
        : this(ledger)
    {
        KeyRange.CheckKey(key);
        _filter = key.ToArray();
    }

    /// <summary>Gets the identity of the segment the view last advanced in.</summary>
    /// <exception cref="InvalidOperationException">The view has advanced past no batch.</exception>
    public LedgerSegmentId Segment => _started ? new(_ledger, _start) : throw new InvalidOperationException("The view has advanced past no batch.");

    /// <summary>Gets the number of keys the view's current segment defined.</summary>
    public ulong DefinedKeys { get; private set; }

    /// <summary>Gets the rows of the batch the view last advanced past, resolved, which are valid until it advances again.</summary>
    public RowEnumerable Rows => new(this);

    /// <summary>Gets the number of batches the view has taken, which stamps the rows each lends.</summary>
    internal long Advances { get; private set; }

    /// <summary>Gets a value indicating whether the view is started, usable, and holds an active segment state.</summary>
    internal bool IsStarted => _started && !_broken && !_disposed;

    /// <summary>Gets the slot the view last advanced past.</summary>
    internal Slot LastSlot => _last;

    /// <summary>Returns whether the view, usable, last advanced past the slot before <paramref name="slot"/>.</summary>
    internal bool ResumesAt(Slot slot) => _started && !_broken && !_disposed && _last.Value + 1 == slot.Value;

    /// <summary>
    /// Returns whether the view already holds an earlier prefix of the segment starting at <paramref name="segmentStart"/>
    /// strictly before <paramref name="from"/>, so scanning can resume at <c>LastSlot + 1</c> instead of rewinding to
    /// <paramref name="segmentStart"/>.
    /// </summary>
    internal bool CanResumeSegment(Slot segmentStart, Slot from) =>
        _started && !_broken && !_disposed && _start == segmentStart && _last.Value + 1 < from.Value;

    /// <summary>
    /// Encodes the view's active segment key-ordinal table into a self-checked binary snapshot so a restarted process
    /// can restore the view at <see cref="LastSlot"/> without rewinding to the segment's start (ledgers.md §8.5).
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is filtered, broken, or has not advanced past any batch.</exception>
    /// <exception cref="ObjectDisposedException">The view was disposed.</exception>
    public byte[] EncodeSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_filter is not null)
        {
            throw new InvalidOperationException("Filtered segment views do not support snapshots.");
        }

        if (_broken || !_started)
        {
            throw new InvalidOperationException("The view has no valid active segment state to snapshot.");
        }

        const int HeaderLength = 1 + sizeof(long) + sizeof(long) + sizeof(int);
        const int PerKeyFixedLength = sizeof(long) + sizeof(ulong) + sizeof(int) + sizeof(int) + sizeof(int);
        const int TrailerLength = sizeof(uint);

        var totalBytes = HeaderLength + TrailerLength;
        for (var i = 0; i < _keyCount; i++)
        {
            ref readonly var entry = ref _keys[i];
            totalBytes = checked(totalBytes + PerKeyFixedLength + entry.KeyLength + (entry.Base?.Length ?? 0) + (entry.Token?.Length ?? 0));
        }

        var buffer = new byte[totalBytes];
        var span = buffer.AsSpan();
        var pos = 0;
        span[pos++] = 1; // Snapshot format version
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos, sizeof(long)), _start.Value);
        pos += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos, sizeof(long)), _last.Value);
        pos += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, sizeof(int)), _keyCount);
        pos += sizeof(int);

        for (var i = 0; i < _keyCount; i++)
        {
            ref readonly var entry = ref _keys[i];
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos, sizeof(long)), entry.DefiningSlot.Value);
            pos += sizeof(long);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(pos, sizeof(ulong)), entry.Count);
            pos += sizeof(ulong);

            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, sizeof(int)), entry.KeyLength);
            pos += sizeof(int);
            _keyArena.AsSpan(entry.KeyOffset, entry.KeyLength).CopyTo(span.Slice(pos, entry.KeyLength));
            pos += entry.KeyLength;

            var baseLen = entry.Base?.Length ?? 0;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, sizeof(int)), baseLen);
            pos += sizeof(int);
            if (entry.Base is { Length: > 0 } baseBytes)
            {
                baseBytes.CopyTo(span.Slice(pos, baseLen));
                pos += baseLen;
            }

            var tokenLen = entry.Token?.Length ?? 0;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, sizeof(int)), tokenLen);
            pos += sizeof(int);
            if (entry.Token is { Length: > 0 } tokenBytes)
            {
                tokenBytes.CopyTo(span.Slice(pos, tokenLen));
                pos += tokenLen;
            }
        }

        var crc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, span[..pos]);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos, TrailerLength), crc);
        return buffer;
    }

    /// <summary>
    /// Restores an unfiltered view from a binary snapshot produced by <see cref="EncodeSnapshot"/>. Returns
    /// <see langword="false"/> and leaves the view cleanly reset if the snapshot is truncated, malformed, corrupt, or
    /// incompatible so the caller can fall back to full-segment replay.
    /// </summary>
    public bool TryRestoreSnapshot(ReadOnlySpan<byte> snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_filter is not null || _broken)
        {
            return false;
        }

        const int HeaderLength = 1 + sizeof(long) + sizeof(long) + sizeof(int);
        const int TrailerLength = sizeof(uint);
        if (snapshot.Length < HeaderLength + TrailerLength)
        {
            return false;
        }

        var bodyLength = snapshot.Length - TrailerLength;
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(snapshot[bodyLength..]);
        if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, snapshot[..bodyLength]) != expectedCrc)
        {
            return false;
        }

        var pos = 0;
        var version = snapshot[pos++];
        if (version != 1)
        {
            return false;
        }

        var rawStart = BinaryPrimitives.ReadInt64LittleEndian(snapshot.Slice(pos, sizeof(long)));
        pos += sizeof(long);
        var rawLast = BinaryPrimitives.ReadInt64LittleEndian(snapshot.Slice(pos, sizeof(long)));
        pos += sizeof(long);
        var keyCount = BinaryPrimitives.ReadInt32LittleEndian(snapshot.Slice(pos, sizeof(int)));
        pos += sizeof(int);

        if (rawStart < 0 || rawLast < rawStart || keyCount < 0)
        {
            return false;
        }

        // Reset cleanly before populating; any failure below calls ResetToEmpty().
        ResetToEmpty();
        try
        {
            for (var i = 0; i < keyCount; i++)
            {
                if (bodyLength - pos < sizeof(long) + sizeof(ulong) + sizeof(int))
                {
                    ResetToEmpty();
                    return false;
                }

                var rawDefSlot = BinaryPrimitives.ReadInt64LittleEndian(snapshot.Slice(pos, sizeof(long)));
                pos += sizeof(long);
                var count = BinaryPrimitives.ReadUInt64LittleEndian(snapshot.Slice(pos, sizeof(ulong)));
                pos += sizeof(ulong);
                var keyLen = BinaryPrimitives.ReadInt32LittleEndian(snapshot.Slice(pos, sizeof(int)));
                pos += sizeof(int);

                if (rawDefSlot < rawStart || rawDefSlot > rawLast || keyLen <= 0 || keyLen > bodyLength - pos)
                {
                    ResetToEmpty();
                    return false;
                }

                var keySpan = snapshot.Slice(pos, keyLen);
                pos += keyLen;

                if (bodyLength - pos < sizeof(int))
                {
                    ResetToEmpty();
                    return false;
                }

                var baseLen = BinaryPrimitives.ReadInt32LittleEndian(snapshot.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (baseLen < 0 || baseLen > bodyLength - pos)
                {
                    ResetToEmpty();
                    return false;
                }

                var versionBase = baseLen == 0 ? null : snapshot.Slice(pos, baseLen).ToArray();
                pos += baseLen;

                if (bodyLength - pos < sizeof(int))
                {
                    ResetToEmpty();
                    return false;
                }

                var tokenLen = BinaryPrimitives.ReadInt32LittleEndian(snapshot.Slice(pos, sizeof(int)));
                pos += sizeof(int);
                if (tokenLen < 0 || tokenLen > bodyLength - pos)
                {
                    ResetToEmpty();
                    return false;
                }

                var token = tokenLen == 0 ? null : snapshot.Slice(pos, tokenLen).ToArray();
                pos += tokenLen;

                var hash = LedgerSegmentDictionary.KeyComparer.Instance.GetHashCode(keySpan);
                if (_bucketCapacity == 0 || (_keyCount + 1) * 10L > _bucketCapacity * 7L)
                {
                    GrowBuckets();
                }

                var mask = _bucketCapacity - 1;
                var bucket = hash & mask;
                while (true)
                {
                    var stored = _buckets[bucket];
                    if (stored == 0)
                    {
                        break;
                    }

                    ref readonly var existing = ref _keys[stored - 1];
                    if (existing.HashCode == hash && _keyArena.AsSpan(existing.KeyOffset, existing.KeyLength).SequenceEqual(keySpan))
                    {
                        ResetToEmpty();
                        return false;
                    }

                    bucket = (bucket + 1) & mask;
                }

                var keyOffset = AppendKeyBytes(keySpan);
                if (_keyCount == _keys.Length)
                {
                    GrowKeys();
                }

                var entryIndex = _keyCount++;
                _keys[entryIndex] = new KeyEntry(keyOffset, keyLen, hash, versionBase, token, count, new Slot(rawDefSlot));
                _buckets[bucket] = entryIndex + 1;
            }

            if (pos != bodyLength)
            {
                ResetToEmpty();
                return false;
            }

            _start = new Slot(rawStart);
            _last = new Slot(rawLast);
            _started = true;
            DefinedKeys = (ulong)keyCount;
            _batch = default;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ResetToEmpty();
            return false;
        }
    }

    /// <summary>Resets the view to its unstarted state, releasing pooled buffers.</summary>
    internal void ResetToEmpty()
    {
        ReleaseKeyTable();
        _start = Slot.Zero;
        _last = Slot.Zero;
        _started = false;
        DefinedKeys = 0;
        _batch = default;
    }

    /// <summary>
    /// Checks the decoded batch at <paramref name="slot"/> against its predecessors (ledgers.md §7's segment rules) and,
    /// when it passes, advances the view past it, so <see cref="Rows"/> lends its rows resolved.
    /// </summary>
    /// <param name="slot">
    /// The batch's slot: the slot after the last the view advanced past, or, for a batch which starts a segment, any slot.
    /// </param>
    /// <param name="view">The batch, decoded by the batch rules, whose buffer its caller keeps while it reads the rows.</param>
    /// <returns>
    /// <see langword="null"/> when the batch passes; otherwise the <see cref="InvalidDataException"/> naming the first
    /// segment rule it breaks, its reason code in <see cref="Exception.Data"/> under <see cref="DecodeRejection.ReasonKey"/>,
    /// after which the view is unusable.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The batch does not start a segment, and <paramref name="slot"/> is not the slot after the last the view advanced
    /// past: a skipped or repeated batch would resolve the later rows of its keys to wrong versions. The view is unchanged.
    /// </exception>
    /// <exception cref="InvalidOperationException">The view rejected an earlier batch.</exception>
    /// <exception cref="ObjectDisposedException">The view was disposed.</exception>
    public InvalidDataException? Advance(Slot slot, BatchView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_broken)
        {
            throw new InvalidOperationException("The view rejected an earlier batch, so its segment cannot be resolved past it.");
        }

        if (_started && !view.IsSegmentStart && slot.Value != _last.Value + 1)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The view last advanced past slot {_last}, so it takes slot {_last.Value + 1} or a segment's start, not slot {slot}."),
                nameof(slot));
        }

        Advances++;
        _batch = default;
        var rejection = AdvanceCore(slot, view);
        _broken = rejection is not null;
        if (rejection is null)
        {
            _batch = view.Buffer;
            _last = slot;
        }

        return rejection;
    }

    /// <summary>Releases the key table.</summary>
    public void Dispose()
    {
        _disposed = true;
        ReleaseKeyTable();
        _filtered = default;
        _hasFiltered = false;
        _rows = [];
        _filteredRows.Clear();
        _batch = default;
    }

    /// <summary>
    /// Lends the rows of the batch the view advanced past at <paramref name="advance"/>, for a scan's item.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view has advanced since, or was disposed: the item is stale.</exception>
    internal RowEnumerable RowsAt(long advance) =>
        !_disposed && advance == Advances
            ? new(this)
            : throw new InvalidOperationException("The scan has advanced past the item, or ended, so its rows are no longer lent.");

    private static InvalidDataException Reject(string reason, string message) => DecodeRejection.Create(reason, message);

    private static RowToken TokenOf(byte[]? bytes) => bytes is null ? default : new RowToken(bytes);

    private static byte[]? BytesOf(RowToken token) => token.IsEmpty ? null : token.ToArray();

    private InvalidDataException? AdvanceCore(Slot slot, BatchView view)
    {
        if (view.IsSegmentStart)
        {
            _start = slot;
            _started = true;
            DefinedKeys = 0;
            _hasFiltered = false;
            ResetKeyTable();
            if (_rows.Length > KeptRowCapacity)
            {
                _rows = [];
            }
        }
        else if (!_started)
        {
            return Reject(
                ReasonOffsetMismatch,
                string.Create(CultureInfo.InvariantCulture, $"The batch at slot {slot}, the first decoded, does not begin a segment: its segment offset is {view.LedgerSegmentOffset}."));
        }
        else if (slot < _start || view.LedgerSegmentOffset != (ulong)(slot - _start))
        {
            return Reject(
                ReasonOffsetMismatch,
                string.Create(CultureInfo.InvariantCulture, $"The batch at slot {slot} has segment offset {view.LedgerSegmentOffset}, and its segment starts at slot {_start}."));
        }

        if (view.OrdinalBase != DefinedKeys)
        {
            return Reject(
                ReasonOrdinalBaseMismatch,
                string.Create(CultureInfo.InvariantCulture, $"The batch at slot {slot} has ordinal base {view.OrdinalBase}, and its segment defined {DefinedKeys} keys before it."));
        }

        if (_filter is not null)
        {
            _filteredRows.Clear();
        }
        else if (_rows.Length < view.Count)
        {
            _rows = new RowState[Math.Max(view.Count, 2 * _rows.Length)];
        }

        foreach (var row in view)
        {
            if (Resolve(slot, row) is { } rejection)
            {
                return rejection;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves one row: defines its key or looks it up by ordinal, and advances the key's count, which the row's version
    /// takes (LG-I13); a count which would pass the <c>u64</c> maximum breaks <c>count-overflow</c>.
    /// </summary>
    private InvalidDataException? Resolve(Slot slot, BatchRow row)
    {
        if (row.Kind == RowKind.Message)
        {
            if (_filter is null)
            {
                _rows[row.Index] = new RowState(-1, 0);
            }

            return null;
        }

        int entry;
        if (row.IsDefining)
        {
            if (Define(slot, row, out entry) is { } duplicate)
            {
                return duplicate;
            }

            DefinedKeys++;
        }
        else
        {
            entry = Lookup(row.Ordinal);
        }

        if (entry < 0)
        {
            // A filtered view skips another key's row.
            return null;
        }

        ref var count = ref _filter is null ? ref _keys[entry].Count : ref _filtered.Count;
        if (count == ulong.MaxValue)
        {
            return Reject(
                ReasonCountOverflow,
                string.Create(CultureInfo.InvariantCulture, $"Row {row.Index} of the batch at slot {slot} would advance its key's count past the u64 maximum, in the segment which starts at slot {_start}."));
        }

        count++;
        if (_filter is null)
        {
            _rows[row.Index] = new RowState(entry, count);
        }
        else
        {
            _filteredRows.Add(new RowState(row.Index, count));
        }

        return null;
    }

    /// <summary>
    /// Defines a defining row's key at the next ordinal, unless its segment already defined it, and returns its entry's
    /// index in the key table, or −1 for a key a filtered view skips.
    /// </summary>
    private InvalidDataException? Define(Slot slot, BatchRow row, out int entry)
    {
        entry = -1;
        var key = row.Key;
        if (_filter is null)
        {
            var hash = LedgerSegmentDictionary.KeyComparer.Instance.GetHashCode(key);
            if (_bucketCapacity == 0 || (_keyCount + 1) * 10L > _bucketCapacity * 7L)
            {
                GrowBuckets();
            }

            var mask = _bucketCapacity - 1;
            var bucket = hash & mask;
            while (true)
            {
                var stored = _buckets[bucket];
                if (stored == 0)
                {
                    break;
                }

                ref readonly var existing = ref _keys[stored - 1];
                if (existing.HashCode == hash && _keyArena.AsSpan(existing.KeyOffset, existing.KeyLength).SequenceEqual(key))
                {
                    return Reject(
                        ReasonDuplicateKey,
                        string.Create(CultureInfo.InvariantCulture, $"Row {row.Index} of the batch at slot {slot} defines a key its segment, which starts at slot {_start}, already defined."));
                }

                bucket = (bucket + 1) & mask;
            }

            var keyOffset = AppendKeyBytes(key);
            if (_keyCount == _keys.Length)
            {
                GrowKeys();
            }

            entry = _keyCount++;
            var versionBefore = row.VersionBefore;
            _keys[entry] = new KeyEntry(keyOffset, key.Length, hash, BytesOf(versionBefore.Base), BytesOf(row.Token), versionBefore.Count, slot);
            _buckets[bucket] = entry + 1;
            return null;
        }

        if (!key.SequenceEqual(_filter))
        {
            return null;
        }

        if (_hasFiltered)
        {
            return Reject(
                ReasonDuplicateKey,
                string.Create(CultureInfo.InvariantCulture, $"Row {row.Index} of the batch at slot {slot} defines a key its segment, which starts at slot {_start}, already defined."));
        }

        var filteredVersionBefore = row.VersionBefore;
        entry = 0;
        _filtered = new KeyEntry(0, _filter.Length, 0, BytesOf(filteredVersionBefore.Base), BytesOf(row.Token), filteredVersionBefore.Count, slot);
        _hasFiltered = true;
        _filteredOrdinal = DefinedKeys;
        return null;
    }

    private void ResetKeyTable()
    {
        if (_keys.Length > KeptKeyCapacity || _keyArena.Length > KeptKeyArenaBytes)
        {
            ReleaseKeyTable();
            return;
        }

        if (_keyCount > 0)
        {
            Array.Clear(_keys, 0, _keyCount);
            Array.Clear(_buckets, 0, _bucketCapacity);
            _keyCount = 0;
            _keyBytesUsed = 0;
        }
    }

    private void ReleaseKeyTable()
    {
        if (_keys.Length > 0)
        {
            ArrayPool<KeyEntry>.Shared.Return(_keys, clearArray: true);
            _keys = [];
        }

        if (_keyArena.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_keyArena, clearArray: false);
            _keyArena = [];
        }

        if (_buckets.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_buckets, clearArray: false);
            _buckets = [];
        }

        _keyCount = 0;
        _keyBytesUsed = 0;
        _bucketCapacity = 0;
    }

    private int AppendKeyBytes(ReadOnlySpan<byte> key)
    {
        var needed = _keyBytesUsed + key.Length;
        if (needed > _keyArena.Length)
        {
            var next = ArrayPool<byte>.Shared.Rent(Math.Max(needed, Math.Max(256, 2 * _keyArena.Length)));
            if (_keyBytesUsed > 0)
            {
                Buffer.BlockCopy(_keyArena, 0, next, 0, _keyBytesUsed);
            }

            if (_keyArena.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_keyArena, clearArray: false);
            }

            _keyArena = next;
        }

        var offset = _keyBytesUsed;
        key.CopyTo(_keyArena.AsSpan(offset, key.Length));
        _keyBytesUsed = needed;
        return offset;
    }

    private void GrowKeys()
    {
        var next = ArrayPool<KeyEntry>.Shared.Rent(Math.Max(16, 2 * _keys.Length));
        if (_keyCount > 0)
        {
            Array.Copy(_keys, next, _keyCount);
        }

        if (_keys.Length > 0)
        {
            ArrayPool<KeyEntry>.Shared.Return(_keys, clearArray: true);
        }

        _keys = next;
    }

    private void GrowBuckets()
    {
        var newCapacity = _bucketCapacity == 0
            ? 16
            : (int)BitOperations.RoundUpToPowerOf2((uint)(_bucketCapacity * 2));
        var next = ArrayPool<int>.Shared.Rent(newCapacity);
        Array.Clear(next, 0, newCapacity);
        var mask = newCapacity - 1;
        for (var index = 0; index < _keyCount; index++)
        {
            var slot = _keys[index].HashCode & mask;
            while (next[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            next[slot] = index + 1;
        }

        if (_buckets.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_buckets, clearArray: false);
        }

        _buckets = next;
        _bucketCapacity = newCapacity;
    }

    /// <summary>
    /// Returns the key-table index of a referencing row's ordinal, which the batch rules and <c>ordinal-base-mismatch</c>
    /// keep below the keys the segment defined, or −1 for another key's row in a filtered view.
    /// </summary>
    private int Lookup(ulong ordinal) =>
        _filter is null ? (int)ordinal : _hasFiltered && _filteredOrdinal == ordinal ? 0 : -1;

    /// <summary>
    /// A key the segment defined: its span in <see cref="_keyArena"/> (or <see cref="_filter"/>), its hash code, its
    /// version base and token, each null when empty, its defining row's slot in the segment, and its count after its last
    /// row so far. Its ordinal in a whole view is its index in <see cref="_keys"/>.
    /// </summary>
    private struct KeyEntry(int keyOffset, int keyLength, int hashCode, byte[]? versionBase, byte[]? token, ulong count, Slot definingSlot)
    {
        public readonly int KeyOffset => keyOffset;

        public readonly int KeyLength => keyLength;

        public readonly int HashCode => hashCode;

        public readonly byte[]? Base => versionBase;

        public readonly byte[]? Token => token;

        public readonly Slot DefiningSlot => definingSlot;

        public ulong Count = count;
    }

    /// <summary>
    /// A row of the current batch: in a whole view, its key's entry and its count after it; in a filtered view, the row's
    /// index in its batch and its count after it.
    /// </summary>
    private readonly record struct RowState(int Position, ulong Count);

    /// <summary>The rows of the batch a view last advanced past, resolved.</summary>
    public readonly ref struct RowEnumerable
    {
        private readonly LedgerSegmentView _view;

        internal RowEnumerable(LedgerSegmentView view) => _view = view;

        /// <summary>Returns an enumerator which lends the rows in order, each valid only for the step which lends it.</summary>
        public RowEnumerator GetEnumerator() => new(_view);
    }

    /// <summary>Lends the resolved rows of the batch a view last advanced past, in order.</summary>
    public ref struct RowEnumerator
    {
        private readonly LedgerSegmentView _view;
        private BatchView.Enumerator _rows;
        private int _filtered;

        internal RowEnumerator(LedgerSegmentView view)
        {
            _view = view;
            _rows = view._batch.Length == 0 ? default : BatchView.OfVerified(view._batch).GetEnumerator();
            _filtered = 0;
            Current = default;
        }

        /// <summary>Gets the row the enumerator lends, valid until the next <see cref="MoveNext"/>.</summary>
        public ResolvedRow Current { get; private set; }

        public bool MoveNext()
        {
            var view = _view;
            if (view._batch.Length == 0)
            {
                return false;
            }

            while (_rows.MoveNext())
            {
                var row = _rows.Current;
                if (row.Kind == RowKind.Message)
                {
                    if (view._filter is not null)
                    {
                        continue;
                    }

                    Current = new ResolvedRow(
                        new Tag(view._ledger, view._last, row.Index),
                        default,
                        default,
                        default,
                        view._start,
                        0,
                        row);
                    return true;
                }

                KeyEntry entry;
                ReadOnlySpan<byte> key;
                ulong count;
                ulong ordinal;
                if (view._filter is null)
                {
                    var state = view._rows[row.Index];
                    entry = view._keys[state.Position];
                    key = view._keyArena.AsSpan(entry.KeyOffset, entry.KeyLength);
                    count = state.Count;
                    ordinal = (ulong)state.Position;
                }
                else if (_filtered < view._filteredRows.Count && view._filteredRows[_filtered].Position == row.Index)
                {
                    entry = view._filtered;
                    key = view._filter;
                    count = view._filteredRows[_filtered++].Count;
                    ordinal = view._filteredOrdinal;
                }
                else
                {
                    continue;
                }

                Current = new ResolvedRow(
                    new Tag(view._ledger, view._last, row.Index),
                    key,
                    new RowVersion(TokenOf(entry.Base), count),
                    TokenOf(entry.Token),
                    entry.DefiningSlot,
                    ordinal,
                    row);
                return true;
            }

            Current = default;
            return false;
        }
    }
}

/// <summary>
/// One row a <see cref="LedgerSegmentView"/> lends, resolved, valid only for the step which lends it (ledgers.md §2.2,
/// §4): its tag, key, kind, version after it (LG-I13), its key's token, defining slot, and ordinal in the segment, and
/// its body.
/// </summary>
public readonly ref struct ResolvedRow
{
    private readonly BatchRow _row;
    private readonly ReadOnlySpan<byte> _key;

    internal ResolvedRow(Tag tag, ReadOnlySpan<byte> key, RowVersion version, RowToken token, Slot definingSlot, ulong ordinal, BatchRow row)
    {
        Tag = tag;
        _key = key;
        Version = version;
        Token = token;
        DefiningSlot = definingSlot;
        Ordinal = ordinal;
        _row = row;
    }

    /// <summary>Gets the row's tag: its ledger, its batch's slot, and its index in the batch.</summary>
    public Tag Tag { get; }

    /// <summary>Gets the row's key, from its view's key table; empty for a <see cref="RowKind.Message"/> row.</summary>
    public ReadOnlySpan<byte> Key => _key;

    /// <summary>Gets a <see cref="RowKind.Message"/> row's validated ASCII channel bytes.</summary>
    /// <exception cref="InvalidOperationException">The row is not a <see cref="RowKind.Message"/> row.</exception>
    public ReadOnlySpan<byte> Channel => _row.Channel;

    /// <summary>Gets the byte offset of the row's body from the start of its batch's buffer.</summary>
    public int BatchBodyOffset => _row.BatchBodyOffset;

    /// <summary>Gets a value indicating whether the row defines its key in its segment: its key's first row there.</summary>
    public bool IsDefining => _row.IsDefining;

    /// <summary>Gets the slot of the batch which defined this row's key in its segment.</summary>
    public Slot DefiningSlot { get; }

    /// <summary>Gets the key's ordinal in its segment, assigned by its defining row.</summary>
    public ulong Ordinal { get; }

    /// <summary>Gets how the row changes its key's state.</summary>
    public RowKind Kind => _row.Kind;

    /// <summary>Gets the key's version after the row: its defining row's stored version, advanced once per row (LG-I13).</summary>
    public RowVersion Version { get; }

    /// <summary>Gets the key's token in the row's segment, which its defining row stored.</summary>
    public RowToken Token { get; }

    /// <summary>Gets the length of the row's body, in bytes.</summary>
    public int BodyLength => _row.BodyLength;

    /// <summary>Gets the row's body, lent over its batch's buffer and valid while that buffer is.</summary>
    public ReadOnlySequence<byte> Body => _row.Body;

    /// <summary>
    /// Returns an owned buffer of the row's body, which retains its pages rather than copying them and outlives the
    /// batch's buffer; the caller disposes it. An empty body is the default buffer.
    /// </summary>
    public ArcBuffer RetainBody() => _row.RetainBody();

    /// <summary>
    /// Makes an owned <see cref="Row"/>: a copy of the row's key with the body's pages retained rather than copied. The
    /// caller owns and disposes it; it outlives the batch's buffer and the view.
    /// </summary>
    public Row ToRow() => Row.Adopt(_key.ToArray(), Kind, RetainBody());
}
