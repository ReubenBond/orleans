using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A value of the manifest (archive §2.2, §4): its epoch, <see cref="DrainedBelow"/>, a watermark for each ledger at or
/// above it which the archive holds rows of, and the committed sections.
/// </summary>
/// <remarks>
/// <para>
/// Every manifest replace increments the epoch once; an absent manifest reads as <see cref="Absent"/>, epoch 0. Each
/// ledger below <see cref="DrainedBelow"/> is complete, and the table holds no entry for it. A ledger at or above it has
/// an entry only while the archive holds rows of it, so no entry is at slot 0 and incomplete, and the entry of
/// <see cref="DrainedBelow"/> is incomplete, since <see cref="DrainedBelow"/> advances past every complete ledger
/// contiguous from it. Sections are kept in order of their position, each object and offset once.
/// </para>
/// <para>
/// Equality compares the epoch, <see cref="DrainedBelow"/>, the watermarks, and the sections, element by element.
/// </para>
/// </remarks>
public sealed class ManifestValue : IEquatable<ManifestValue>
{
    // Built on first use from the immutable sections, so racing builders build equal indexes.

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestValue"/> class.
    /// </summary>
    /// <param name="epoch">The number of replaces which made the manifest.</param>
    /// <param name="drainedBelow">The ledger below which every ledger is complete.</param>
    /// <param name="watermarks">The watermark of each ledger at or above <paramref name="drainedBelow"/> the archive holds rows of.</param>
    /// <param name="sections">The committed sections, each object and offset once.</param>
    /// <exception cref="ArgumentException">The value breaks a rule of the remarks.</exception>
    public ManifestValue(
        ulong epoch,
        LedgerId drainedBelow,
        IEnumerable<KeyValuePair<LedgerId, ManifestWatermark>> watermarks,
        IEnumerable<SectionRef> sections)
        : this(epoch, drainedBelow, ImmutableSortedDictionary.CreateRange(watermarks ?? throw new ArgumentNullException(nameof(watermarks))), Ordered(sections))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestValue"/> class from sections already in position order, each
    /// position once, as the decoder and a change's merge give them; the order is checked only in Debug builds.
    /// </summary>
    internal ManifestValue(ulong epoch, LedgerId drainedBelow, ImmutableSortedDictionary<LedgerId, ManifestWatermark> watermarks, ImmutableArray<SectionRef> sections)
    {
        if (drainedBelow == default)
        {
            throw new ArgumentException("DrainedBelow names a ledger; the default ledger ID names none.", nameof(drainedBelow));
        }

        foreach (var (ledger, watermark) in watermarks)
        {
            if (ledger < drainedBelow)
            {
                throw new ArgumentException($"Ledger {ledger} lies below DrainedBelow {drainedBelow}, which has no entry.", nameof(watermarks));
            }

            if (watermark.Below == Slot.Zero && !watermark.Complete)
            {
                throw new ArgumentException($"Ledger {ledger}'s entry holds no row.", nameof(watermarks));
            }

            if (watermark.Complete && ledger == drainedBelow)
            {
                throw new ArgumentException($"Ledger {ledger} is complete, so DrainedBelow lies past it.", nameof(watermarks));
            }
        }

        Debug.Assert(IsInPositionOrder(sections), "Sections lie in position order, each position once.");
        Epoch = epoch;
        DrainedBelow = drainedBelow;
        Watermarks = watermarks;
        Sections = sections;
    }

    /// <summary>
    /// Gets the value an absent manifest reads as: epoch 0, <see cref="DrainedBelow"/> 1, and no watermarks or sections.
    /// </summary>
    public static ManifestValue Absent { get; } = new(0, new LedgerId(1), [], []);

    /// <summary>
    /// Gets the number of replaces which made the manifest.
    /// </summary>
    public ulong Epoch { get; }

    /// <summary>
    /// Gets the ledger below which every ledger is complete.
    /// </summary>
    public LedgerId DrainedBelow { get; }

    /// <summary>
    /// Gets the watermark of each ledger at or above <see cref="DrainedBelow"/> the archive holds rows of.
    /// </summary>
    public ImmutableSortedDictionary<LedgerId, ManifestWatermark> Watermarks { get; }

    /// <summary>
    /// Gets the committed sections, in order of their position.
    /// </summary>
    public ImmutableArray<SectionRef> Sections { get; }

    /// <summary>
    /// Returns whether the archive holds every row of <paramref name="ledger"/>: it lies below <see cref="DrainedBelow"/>,
    /// or is marked complete.
    /// </summary>
    public bool IsComplete(LedgerId ledger) => ledger < DrainedBelow || Watermarks.TryGetValue(ledger, out var watermark) && watermark.Complete;

    /// <summary>
    /// Returns whether the value lists <paramref name="section"/>: a section at its position, equal to it.
    /// </summary>
    public bool Contains(SectionRef section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var index = Sections.BinarySearch(section, SectionRef.PositionOrder);
        return index >= 0 && Sections[index].Equals(section);
    }

    /// <summary>
    /// Returns whether the value lists a section at <paramref name="position"/>, its object and offset.
    /// </summary>
    internal bool Lists(SectionPosition position) =>
        Sections.AsSpan().BinarySearch(new PositionKey(position)) >= 0;

    /// <summary>Orders sections against a position, for a binary search by position.</summary>
    private readonly struct PositionKey(SectionPosition position) : IComparable<SectionRef>
    {
        public int CompareTo(SectionRef? other) =>
            other is null ? 1 : position.CompareTo(new SectionPosition(other.Object, other.Offset));
    }

    /// <summary>
    /// Returns the value at <paramref name="epoch"/>, with the same contents.
    /// </summary>
    internal ManifestValue WithEpoch(ulong epoch) => new(epoch, DrainedBelow, Watermarks, Sections);

    /// <summary>
    /// Returns the watermark recorded for <paramref name="ledger"/>, at or above <see cref="DrainedBelow"/>: its entry's,
    /// or slot 0 when it has none.
    /// </summary>
    public Slot RecordedWatermark(LedgerId ledger) => Watermarks.TryGetValue(ledger, out var watermark) ? watermark.Below : Slot.Zero;

    /// <summary>
    /// Returns the watermark of <paramref name="ledger"/>, as a ledger map value lists it (archive §2.2): its recovered
    /// tail <c>T</c> when this value shows it complete and the map lists it closed, and otherwise the watermark recorded
    /// for it, or 0. The archive holds every row of the ledger below it.
    /// </summary>
    /// <returns>
    /// The watermark, or <see langword="null"/> when this value shows complete a ledger the map lists open: the map value
    /// predates the ledger's close, drain, and completion, and its reader reads the map again.
    /// </returns>
    /// <exception cref="ArchiveCorruptException">The value records for the closed ledger a watermark past its <c>T</c>, or
    /// a completion at another slot than <c>T</c>: a ledger drains only below <c>T</c>, and completes only at it.</exception>
    public Slot? Watermark(LedgerMetadata ledger)
    {
        var complete = IsComplete(ledger.Id);
        if (ledger.Status.Value is not LedgerStatus.Closed closed)
        {
            return complete ? null : RecordedWatermark(ledger.Id);
        }

        var hasEntry = Watermarks.TryGetValue(ledger.Id, out var entry);
        if (hasEntry && (entry.Complete ? entry.Below != closed.T : entry.Below > closed.T))
        {
            throw new ArchiveCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The manifest at epoch {Epoch} records ledger {ledger.Id}'s watermark at {entry.Below}{(entry.Complete ? ", complete," : string.Empty)} against its T {closed.T}."));
        }

        return complete ? closed.T : RecordedWatermark(ledger.Id);
    }

    /// <summary>
    /// Returns every section whose partition contains <paramref name="key"/>, at every depth, in position order. It probes
    /// one lookup per depth the sections' partitions sit at, and allocates only when partitions at several depths contain
    /// the key.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="KeyRange.MaxKeyLength"/> bytes.</exception>
    public ImmutableArray<SectionRef> SectionsContaining(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty || key.Length > KeyRange.MaxKeyLength)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A key is 1 to {KeyRange.MaxKeyLength} bytes, not {key.Length}."), nameof(key));
        }

        return Partitions.Containing(key);
    }

    /// <summary>Returns the sections of exactly <paramref name="partition"/>, in position order.</summary>
    internal ImmutableArray<SectionRef> SectionsOf(KeyRange partition) => Partitions.Of(partition);

    /// <summary>Returns each partition which holds sections, with its sections in position order.</summary>
    internal IEnumerable<KeyValuePair<KeyRange, ImmutableArray<SectionRef>>> SectionsByPartition() => Partitions.All();

    private PartitionIndex Partitions => LazyInitializer.EnsureInitialized(ref field, () => new PartitionIndex(Sections));

    /// <inheritdoc/>
    public bool Equals(ManifestValue? other) =>
        other is not null
        && Epoch == other.Epoch
        && DrainedBelow == other.DrainedBelow
        && Watermarks.SequenceEqual(other.Watermarks)
        && Sections.SequenceEqual(other.Sections);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ManifestValue);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Epoch, DrainedBelow, Watermarks.Count, Sections.Length);

    /// <summary>
    /// Returns <paramref name="sections"/> in position order.
    /// </summary>
    /// <exception cref="ArgumentException">Two sections lie at one position.</exception>
    private static ImmutableArray<SectionRef> Ordered(IEnumerable<SectionRef> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ImmutableArray<SectionRef> ordered = [.. sections.Order(SectionRef.PositionOrder)];
        for (var index = 1; index < ordered.Length; index++)
        {
            if (SectionRef.CompareByPosition(ordered[index - 1], ordered[index]) == 0)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Two sections lie at offset {ordered[index].Offset} of {ordered[index].Object}."),
                    nameof(sections));
            }
        }

        return ordered;
    }

    private static bool IsInPositionOrder(ImmutableArray<SectionRef> sections)
    {
        for (var index = 1; index < sections.Length; index++)
        {
            if (SectionRef.CompareByPosition(sections[index - 1], sections[index]) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The sections grouped by partition, one lookup per depth the partitions sit at, so a key's sections are found by
    /// probing each depth once without constructing a range: partitions at one depth are equal or disjoint, so at most
    /// one at each depth contains the key.
    /// </summary>
    private sealed class PartitionIndex
    {
        private readonly (int Depth, Dictionary<KeyRange, ImmutableArray<SectionRef>>.AlternateLookup<KeyProbe> Lookup)[] _depths;

        public PartitionIndex(ImmutableArray<SectionRef> sections)
        {
            var byDepth = new SortedDictionary<int, Dictionary<KeyRange, ImmutableArray<SectionRef>.Builder>>();
            foreach (var section in sections)
            {
                var partition = section.Partition;
                if (!byDepth.TryGetValue(partition.Depth, out var partitions))
                {
                    byDepth.Add(partition.Depth, partitions = []);
                }

                if (!partitions.TryGetValue(partition, out var group))
                {
                    partitions.Add(partition, group = ImmutableArray.CreateBuilder<SectionRef>());
                }

                group.Add(section);
            }

            _depths =
            [
                .. byDepth.Select(static depth => (
                    depth.Key,
                    depth.Value.ToDictionary(static entry => entry.Key, static entry => entry.Value.ToImmutable(), PartitionComparer.Instance)
                        .GetAlternateLookup<KeyProbe>())),
            ];
        }

        /// <summary>Returns the sections of exactly <paramref name="partition"/>, in position order.</summary>
        public ImmutableArray<SectionRef> Of(KeyRange partition)
        {
            foreach (var (depth, lookup) in _depths)
            {
                if (depth == partition.Depth)
                {
                    return lookup.Dictionary.TryGetValue(partition, out var sections) ? sections : [];
                }
            }

            return [];
        }

        /// <summary>Returns each partition with its sections, shallowest first.</summary>
        public IEnumerable<KeyValuePair<KeyRange, ImmutableArray<SectionRef>>> All() =>
            _depths.SelectMany(static depth => depth.Lookup.Dictionary);

        /// <summary>Returns the sections whose partition contains <paramref name="key"/>, in position order.</summary>
        public ImmutableArray<SectionRef> Containing(ReadOnlySpan<byte> key)
        {
            ImmutableArray<SectionRef> first = [];
            ImmutableArray<SectionRef>.Builder? merged = null;
            foreach (var (depth, lookup) in _depths)
            {
                if (!lookup.TryGetValue(new KeyProbe(key, depth), out var sections))
                {
                    continue;
                }

                if (first.IsEmpty)
                {
                    first = sections;
                    continue;
                }

                if (merged is null)
                {
                    merged = ImmutableArray.CreateBuilder<SectionRef>();
                    merged.AddRange(first);
                }

                merged.AddRange(sections);
            }

            if (merged is null)
            {
                return first;
            }

            // Each partition's sections are in position order already; those of several partitions interleave.
            merged.Sort(SectionRef.PositionOrder);
            return merged.DrainToImmutable();
        }
    }

    /// <summary>A key and a depth: the partition at that depth which contains the key, unconstructed.</summary>
    private readonly ref struct KeyProbe(ReadOnlySpan<byte> key, int depth)
    {
        public ReadOnlySpan<byte> Key { get; } = key;

        public int Depth { get; } = depth;
    }

    /// <summary>
    /// Compares partitions by value, and a probe with the partition containing its key at its depth, hashing both by the
    /// depth and the first 8 bytes of the prefix the depth fixes.
    /// </summary>
    private sealed class PartitionComparer : IEqualityComparer<KeyRange>, IAlternateEqualityComparer<KeyProbe, KeyRange>
    {
        public static PartitionComparer Instance { get; } = new();

        public bool Equals(KeyRange x, KeyRange y) => x.Equals(y);

        public int GetHashCode(KeyRange obj) => Hash(obj.Prefix, obj.Depth);

        public bool Equals(KeyProbe alternate, KeyRange other) => other.Depth == alternate.Depth && other.Contains(alternate.Key);

        public int GetHashCode(KeyProbe alternate) => Hash(alternate.Key, alternate.Depth);

        public KeyRange Create(KeyProbe alternate) => KeyRange.Containing(alternate.Key, alternate.Depth);

        /// <summary>Hashes the depth and the first <c>min(depth, 64)</c> bits of <paramref name="bits"/>, 0 past its end.</summary>
        private static int Hash(ReadOnlySpan<byte> bits, int depth)
        {
            Span<byte> leading = stackalloc byte[sizeof(ulong)];
            leading.Clear();
            bits[..Math.Min(bits.Length, sizeof(ulong))].CopyTo(leading);
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(leading);
            value &= depth == 0 ? 0 : depth >= 64 ? ulong.MaxValue : ulong.MaxValue << (64 - depth);
            return HashCode.Combine(depth, value);
        }
    }
}
