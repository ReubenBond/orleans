using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// Writes one run object (archive §7) row by row: <see cref="BeginSection"/>, <see cref="Add(TaggedRow)"/> for each of the
/// section's rows in order, <see cref="EndSection"/>, for each section, then <see cref="Complete"/>.
/// </summary>
/// <remarks>
/// <para>
/// The writer streams each row into its block as it is added, starting a new block before a row which would make the
/// block longer than the block size unless the block is empty, and keeps per section only its blocks' entries, the
/// XXH3 hash of each distinct key, from which <see cref="Complete"/> writes the block indexes, Bloom filters, and
/// trailer.
/// </para>
/// <para>
/// Before <see cref="Complete"/> returns, the writer reads its object back under every rule of the format and compares
/// it with its input: every row's key, tag, kind, and payload, through a digest of each; each section's row count; and
/// that each Bloom filter admits every key. A mismatch throws <see cref="InvalidOperationException"/>, and nothing is
/// returned to upload.
/// </para>
/// </remarks>
internal sealed class RunObjectWriter : IDisposable
{
    private readonly ArcBufferWriter _writer;
    private readonly Crc32 _crc = new(Crc32ParameterSet.Crc32C);
    private readonly XxHash128 _digest = new();
    private readonly RunObjectCondition _condition;
    private readonly RunSectionSettings _settings;
    private readonly List<SectionState> _sections = [];
    private SectionState? _section;
    private bool _completed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunObjectWriter"/> class.
    /// </summary>
    /// <param name="condition">The object's reference condition: for a drain, every row is of its ledger, at its
    /// watermark or above.</param>
    /// <param name="settings">The block size and Bloom bits per key of every section.</param>
    /// <param name="expectedLength">The object's expected length, which sizes the writer's first page.</param>
    /// <exception cref="ArgumentException">The condition is the default, a drain of no ledger, or a compaction without
    /// inputs in strictly increasing order of position.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The block size is below 1, or the bits per key outside 1 to 64.</exception>
    public RunObjectWriter(RunObjectCondition condition, RunSectionSettings settings, int expectedLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.BlockBytes, 1, nameof(settings));
        if (settings.BloomBitsPerKey is < 1 or > RunBloomFilter.MaxBitsPerKey)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.BloomBitsPerKey, "A Bloom filter takes 1 to 64 bits per key.");
        }

        ThrowIfInvalid(condition);
        _condition = condition;
        _settings = settings;
        _writer = new ArcBufferWriter(Math.Max(expectedLength, 0));
    }

    /// <summary>
    /// Starts a section of <paramref name="partition"/>, whose rows <see cref="Add(TaggedRow)"/> takes.
    /// </summary>
    /// <exception cref="InvalidOperationException">A section is open, or the object is complete.</exception>
    public void BeginSection(KeyRange partition)
    {
        ThrowIfCompleted();
        if (_section is not null)
        {
            throw new InvalidOperationException("The previous section has not ended.");
        }

        _section = new SectionState(partition, _writer.Length);
    }

    /// <summary>
    /// Writes <paramref name="row"/>, the open section's next row, which the writer reads and keeps.
    /// </summary>
    /// <exception cref="ArgumentException">The row is the default; its tag lies past slot <c>2^63 - 2</c>, or, in a drain
    /// output, is of another ledger or below its watermark; its key lies outside the section's partition; or it does not
    /// lie after the section's previous row, by key and then tag.</exception>
    /// <exception cref="InvalidOperationException">No section is open, or the row would make the object longer than
    /// <see cref="RunObjectFormat.MaxObjectLength"/>.</exception>
    public void Add(TaggedRow row) => Add(row.Tag, row.Row.Key.Span, row.Row.Kind, row.Row.Body);

    /// <summary>
    /// Writes one row from its tag, key, kind, and borrowed <paramref name="body"/>, which the writer reads synchronously.
    /// </summary>
    public void Add(Tag tag, ReadOnlySpan<byte> key, RowKind kind, ArcBuffer body)
    {
        ThrowIfCompleted();
        var section = _section ?? throw new InvalidOperationException("No section is open.");
        var continues = ValidateAndCheckContinues(section, tag, key);
        var length = RunObjectFormat.RowLength(key.Length, body.Length);
        if (_writer.Length + length + RunObjectFormat.CrcLength > RunObjectFormat.MaxObjectLength)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The row would make the run object longer than {RunObjectFormat.MaxObjectLength} bytes."));
        }

        if (section.BlockOpen && section.BlockLength + length > _settings.BlockBytes)
        {
            CloseBlock(section);
        }

        if (!section.BlockOpen)
        {
            section.OpenBlock(_writer.Length, continues, key);
            _crc.Reset();
        }

        WriteRow(tag, key, kind, body);
        section.BlockLength += length;
        section.Record(tag, key, continues);
        RunObjectFormat.AppendRow(_digest, key, tag, kind, body);
    }

    /// <summary>
    /// Ends the open section, which holds at least one row.
    /// </summary>
    /// <exception cref="InvalidOperationException">No section is open, or it holds no row.</exception>
    public void EndSection()
    {
        ThrowIfCompleted();
        var section = _section ?? throw new InvalidOperationException("No section is open.");
        if (section.RowCount == 0)
        {
            throw new InvalidOperationException("A section holds at least one row.");
        }

        CloseBlock(section);
        section.End = _writer.Length;
        _sections.Add(section);
        _section = null;
    }

    /// <summary>
    /// Writes every section's block index and Bloom filter, then the trailer, and checks that the object reads back as
    /// its input.
    /// </summary>
    /// <returns>The object, which the caller owns, with its trailer and a CRC-32C of its bytes which the check then verified.</returns>
    /// <exception cref="InvalidOperationException">A section is open, there is none, the object would be longer than
    /// <see cref="RunObjectFormat.MaxObjectLength"/>, or it does not read back as its input.</exception>
    public EncodedRunObject Complete()
    {
        ThrowIfCompleted();
        if (_section is not null || _sections.Count == 0)
        {
            throw new InvalidOperationException("A run object completes with every section ended, and holds at least one.");
        }

        _completed = true;
        var metadataOffset = (long)_writer.Length;
        var parts = new (long Index, long Filter)[_sections.Count];
        for (var index = 0; index < _sections.Count; index++)
        {
            parts[index] = WriteMetadata(_sections[index]);
        }

        var trailerOffset = (long)_writer.Length;
        var entries = ImmutableArray.CreateBuilder<RunSectionEntry>(_sections.Count);
        for (var index = 0; index < _sections.Count; index++)
        {
            var section = _sections[index];
            var filterEnd = index + 1 < _sections.Count ? parts[index + 1].Index : trailerOffset;
            entries.Add(new RunSectionEntry(
                section.Start,
                section.End - section.Start,
                section.Partition,
                section.MinTag,
                section.MaxTag,
                section.RowCount,
                parts[index].Index,
                parts[index].Filter,
                filterEnd));
        }

        var trailerLength = WriteTrailer(metadataOffset, entries);
        if (trailerLength > RunObjectFormat.MaxTrailerLength)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The run object's trailer would be {trailerLength} bytes, longer than {RunObjectFormat.MaxTrailerLength}."));
        }

        var length = trailerOffset + trailerLength;
        if (length > RunObjectFormat.MaxObjectLength)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The run object would be {length} bytes, longer than {RunObjectFormat.MaxObjectLength}."));
        }

        var trailer = new RunTrailer(metadataOffset, trailerOffset, length, entries.MoveToImmutable(), _condition);
        var content = _writer.ConsumeSlice((int)length);

        // The CRC first, then the check of the bytes it covers: a page changed before the CRC fails the check, and one
        // changed after it fails at the object store, so no change slips in between.
        var crc32c = ObjectStoreChecksums.Crc32C(content);
        try
        {
            CheckReadsBack(content, trailer);
        }
        catch
        {
            content.Dispose();
            throw;
        }

        var blocks = ImmutableArray.CreateBuilder<ObjectRange>(_sections.Sum(static section => section.Blocks.Count));
        foreach (var section in _sections)
        {
            foreach (var block in section.Blocks)
            {
                blocks.Add(new ObjectRange(block.Offset, block.Length));
            }
        }

        return new EncodedRunObject(content, trailer, crc32c, blocks.MoveToImmutable());
    }

    /// <summary>Releases the writer's pages and pooled buffers; an object <see cref="Complete"/> returned keeps its own.</summary>
    public void Dispose()
    {
        _section?.Dispose();
        _section = null;
        foreach (var section in _sections)
        {
            section.Dispose();
        }

        _sections.Clear();
        _writer.Dispose();
    }

    private static void ThrowIfInvalid(RunObjectCondition condition)
    {
        switch (condition.Value)
        {
            case RunObjectCondition.Drain drain:
                if (drain.Ledger == default)
                {
                    throw new ArgumentException("A drain output's condition names its ledger.", nameof(condition));
                }

                return;
            case RunObjectCondition.Compaction compaction:
                if (compaction.Inputs.IsDefaultOrEmpty)
                {
                    throw new ArgumentException("A compaction output's condition names its inputs.", nameof(condition));
                }

                for (var input = 0; input < compaction.Inputs.Length; input++)
                {
                    var position = compaction.Inputs[input];
                    if (position.Object is null || position.Offset < 0 || input > 0 && compaction.Inputs[input - 1].CompareTo(position) >= 0)
                    {
                        throw new ArgumentException("A compaction's inputs name sections, in strictly increasing order of object name, then offset.", nameof(condition));
                    }
                }

                return;
            case null:
                throw new ArgumentException("A run object records its reference condition.", nameof(condition));
            default:
                throw new UnreachableException();
        }
    }

    private bool ValidateAndCheckContinues(SectionState section, Tag tag, ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty || tag.Ledger == default)
        {
            throw new ArgumentException("The row is the default, which holds no row.", "row");
        }

        if (tag.Slot.Value > SectionRef.MaxTagSlot)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The row's tag lies at slot {tag.Slot}, past 2^63 - 2, the last slot a ledger has."), "row");
        }

        if (_condition.Value is RunObjectCondition.Drain drain && (tag.Ledger != drain.Ledger || tag.Slot < drain.From))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The row's tag lies outside the drain of ledger {drain.Ledger} from slot {drain.From}."), "row");
        }

        if (!section.Partition.Contains(key))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The row's key lies outside the section's partition {section.Partition}."), "row");
        }

        if (section.RowCount == 0)
        {
            return false;
        }

        var keyOrder = key.SequenceCompareTo(section.PreviousKey);
        if (keyOrder < 0 || keyOrder == 0 && tag.CompareTo(section.PreviousTag) <= 0)
        {
            throw new ArgumentException("The row does not lie after the section's previous row, by key and then tag.", "row");
        }

        return keyOrder == 0;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The run object is complete.");
        }
    }

    /// <summary>
    /// Checks that <paramref name="content"/> reads back under every rule as the writer's input: its trailer, every row
    /// through the digest, and each section's row count and Bloom filter against what the writer recorded from the
    /// input rows.
    /// </summary>
    private void CheckReadsBack(ArcBuffer content, RunTrailer trailer)
    {
        var expectedDigest = _digest.GetCurrentHashAsUInt128();
        _digest.Reset();
        var decoding = RunObjectFormat.Verify(content, _digest);
        if (decoding.Value is InvalidDataException rejection)
        {
            throw new InvalidOperationException("The run object the writer encoded breaks the format: " + rejection.Message, rejection);
        }

        var tail = (RunTail)decoding.Value;
        if (!tail.Trailer.Equals(trailer) || _digest.GetCurrentHashAsUInt128() != expectedDigest)
        {
            throw new InvalidOperationException("The run object the writer encoded does not read back as its rows.");
        }

        for (var index = 0; index < _sections.Count; index++)
        {
            var section = _sections[index];
            var read = tail.Sections[index];
            var admitted = read.Entry.RowCount == section.RowCount;
            if (admitted)
            {
                foreach (var hash in section.Hashes)
                {
                    if (!read.Filter.MayContain(hash))
                    {
                        admitted = false;
                        break;
                    }
                }
            }

            if (!admitted)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Section {index} of the run object the writer encoded does not read back with its rows' count and keys."));
            }
        }
    }

    private void CloseBlock(SectionState section)
    {
        if (!section.BlockOpen)
        {
            return;
        }

        EndPart();
        section.CloseBlock(_writer.Length);
    }

    /// <summary>
    /// Writes a row: its key, tag, kind, and payload length in one span, then its payload.
    /// </summary>
    private void WriteRow(Tag tag, ReadOnlySpan<byte> key, RowKind kind, ArcBuffer payload)
    {
        var fixedLength = sizeof(ushort) + key.Length + RunObjectFormat.TagLength + 1 + sizeof(uint);
        var span = _writer.GetSpan(fixedLength)[..fixedLength];
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)key.Length);
        key.CopyTo(span[sizeof(ushort)..]);
        var fields = span[(sizeof(ushort) + key.Length)..];
        BinaryPrimitives.WriteUInt64LittleEndian(fields, tag.Ledger.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(fields[8..], (ulong)tag.Slot.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[16..], (uint)tag.Index);
        fields[20] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(fields[21..], (uint)payload.Length);
        Advance(span);

        foreach (var segment in payload)
        {
            Bytes(segment);
        }
    }

    /// <summary>
    /// Writes a section's block index and Bloom filter.
    /// </summary>
    /// <returns>The offsets of the block index and the Bloom filter.</returns>
    private (long Index, long Filter) WriteMetadata(SectionState section)
    {
        var indexOffset = (long)_writer.Length;
        _crc.Reset();
        UInt32((uint)section.Blocks.Count);
        foreach (var block in section.Blocks)
        {
            UInt32((uint)block.Length);
            Byte(block.Continues ? (byte)1 : (byte)0);
            Key(block.FirstKey.Span);
        }

        EndPart();
        var filterOffset = (long)_writer.Length;
        var filter = RunBloomFilter.Create(section.Hashes, _settings.BloomBitsPerKey);
        _crc.Reset();
        Byte((byte)filter.HashCount);
        Bytes(filter.Bits);
        EndPart();
        return (indexOffset, filterOffset);
    }

    /// <summary>
    /// Writes the trailer.
    /// </summary>
    /// <returns>The trailer's length.</returns>
    private int WriteTrailer(long metadataOffset, ImmutableArray<RunSectionEntry>.Builder entries)
    {
        var start = _writer.Length;
        _crc.Reset();
        Bytes(RunObjectFormat.Magic);
        UInt32(RunObjectFormat.Version);
        UInt32((uint)entries.Count);
        UInt64((ulong)metadataOffset);
        foreach (var entry in entries)
        {
            UInt64((ulong)entry.Offset);
            UInt64((ulong)entry.Length);
            UInt32((uint)entry.Partition.Depth);
            Bytes(entry.Partition.Prefix);
            Tag(entry.MinTag);
            Tag(entry.MaxTag);
            UInt64(entry.RowCount);
            UInt64((ulong)entry.IndexOffset);
            UInt64((ulong)entry.FilterOffset);
        }

        switch (_condition.Value)
        {
            case RunObjectCondition.Drain drain:
                Byte(0);
                UInt64(drain.Ledger.Value);
                UInt64((ulong)drain.From.Value);
                break;
            case RunObjectCondition.Compaction compaction:
                Byte(1);
                UInt32((uint)compaction.Inputs.Length);
                foreach (var input in compaction.Inputs)
                {
                    Name(input.Object);
                    UInt64((ulong)input.Offset);
                }

                break;
            case null:
                throw new UnreachableException("The writer checked the condition.");
            default:
                throw new UnreachableException();
        }

        UInt32((uint)checked(_writer.Length - start + sizeof(uint) + RunObjectFormat.CrcLength));
        EndPart();
        return _writer.Length - start;
    }

    private void EndPart()
    {
        var span = _writer.GetSpan(RunObjectFormat.CrcLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span, _crc.GetCurrentHashAsUInt32());
        _writer.AdvanceWriter(RunObjectFormat.CrcLength);
    }

    private void Byte(byte value)
    {
        var span = _writer.GetSpan(1);
        span[0] = value;
        Advance(span[..1]);
    }

    private void UInt32(uint value)
    {
        var span = _writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        Advance(span[..sizeof(uint)]);
    }

    private void UInt64(ulong value)
    {
        var span = _writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        Advance(span[..sizeof(ulong)]);
    }

    private void Bytes(ReadOnlySpan<byte> value)
    {
        _crc.Append(value);
        _writer.Write(value);
    }

    private void Key(ReadOnlySpan<byte> key)
    {
        var span = _writer.GetSpan(sizeof(ushort) + key.Length)[..(sizeof(ushort) + key.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)key.Length);
        key.CopyTo(span[sizeof(ushort)..]);
        Advance(span);
    }

    private void Tag(Tag tag)
    {
        UInt64(tag.Ledger.Value);
        UInt64((ulong)tag.Slot.Value);
        UInt32((uint)tag.Index);
    }

    private void Name(ObjectName name)
    {
        var span = _writer.GetSpan(1 + name.Value.Length);
        span[0] = (byte)name.Value.Length;
        var written = Encoding.ASCII.GetBytes(name.Value, span[1..]);
        Advance(span[..(1 + written)]);
    }

    private void Advance(Span<byte> written)
    {
        _crc.Append(written);
        _writer.AdvanceWriter(written.Length);
    }

    /// <summary>
    /// What the writer keeps of one section: its blocks' entries, the open block, the hash of each distinct key, its
    /// totals, and its previous row, against which the next is ordered.
    /// </summary>
    private sealed class SectionState(KeyRange partition, long start) : IDisposable
    {
        private byte[] _previousKey = [];
        private int _previousLength;
        private long _blockStart;
        private bool _blockContinues;
        private byte[] _blockFirstKey = [];
        private ulong[] _hashes = [];
        private int _hashCount;

        public KeyRange Partition { get; } = partition;

        public long Start { get; } = start;

        public long End { get; set; }

        public List<RunBlockEntry> Blocks { get; } = [];

        public ReadOnlySpan<ulong> Hashes => _hashes.AsSpan(0, _hashCount);

        public bool BlockOpen { get; private set; }

        public long BlockLength { get; set; }

        public ulong RowCount { get; private set; }

        public Tag MinTag { get; private set; }

        public Tag MaxTag { get; private set; }

        public Tag PreviousTag { get; private set; }

        public ReadOnlySpan<byte> PreviousKey => _previousKey.AsSpan(0, _previousLength);

        public void OpenBlock(long offset, bool continues, ReadOnlySpan<byte> firstKey)
        {
            BlockOpen = true;
            _blockStart = offset;
            _blockContinues = continues;
            _blockFirstKey = firstKey.ToArray();
            BlockLength = RunObjectFormat.CrcLength;
        }

        public void CloseBlock(long end)
        {
            Blocks.Add(new RunBlockEntry(_blockStart, end - _blockStart, _blockContinues, _blockFirstKey));
            BlockOpen = false;
        }

        public void Record(Tag tag, ReadOnlySpan<byte> key, bool continues)
        {
            if (!continues)
            {
                AddHash(XxHash3.HashToUInt64(key));
                if (_previousKey.Length < key.Length)
                {
                    _previousKey = new byte[Math.Max(key.Length, 2 * _previousKey.Length)];
                }

                key.CopyTo(_previousKey);
                _previousLength = key.Length;
            }

            MinTag = RowCount == 0 || tag < MinTag ? tag : MinTag;
            MaxTag = RowCount == 0 || tag > MaxTag ? tag : MaxTag;
            RowCount++;
            PreviousTag = tag;
        }

        public void Dispose()
        {
            if (_hashes.Length > 0)
            {
                ArrayPool<ulong>.Shared.Return(_hashes, clearArray: false);
                _hashes = [];
                _hashCount = 0;
            }
        }

        private void AddHash(ulong hash)
        {
            if (_hashCount == _hashes.Length)
            {
                var next = ArrayPool<ulong>.Shared.Rent(Math.Max(16, 2 * _hashes.Length));
                if (_hashCount > 0)
                {
                    Array.Copy(_hashes, next, _hashCount);
                    ArrayPool<ulong>.Shared.Return(_hashes, clearArray: false);
                }

                _hashes = next;
            }

            _hashes[_hashCount++] = hash;
        }
    }
}
