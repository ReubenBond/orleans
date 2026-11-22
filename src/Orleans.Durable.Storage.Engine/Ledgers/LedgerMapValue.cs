using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Buffers;

using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// What decoding a ledger map value gives (conventions.md §15): the value, or the <see cref="InvalidDataException"/>
/// naming the first rule its bytes break, with the rule's reason code (ledgers.md §7).
/// </summary>
internal readonly union LedgerMapDecoding(LedgerMapValue, InvalidDataException);

/// <summary>
/// One value of the <c>ledger-map</c> register (ledgers.md §2.3, §4): the live ledgers in ID order, the retirement floor,
/// the ID the next installed ledger takes, and an epoch which every write increments once. Immutable.
/// </summary>
/// <remarks>
/// <para>
/// An absent register reads as <see cref="Empty"/>. The value carries no MetaStore version: the committer keeps the
/// version with the value it last read or wrote, and nothing on the data plane uses one.
/// </para>
/// <para>
/// Its binary form is <c>CLLM</c> version 1 (ledgers.md §7). Decoding accepts only the canonical encoding and checks the
/// format alone; the committer checks LG-I1 to LG-I3, LG-I9 to LG-I11 before every write.
/// </para>
/// </remarks>
public sealed class LedgerMapValue
{
    internal const string ReasonTruncated = "truncated";
    internal const string ReasonBadMagic = "bad-magic";
    internal const string ReasonBadVersion = "bad-version";
    internal const string ReasonCrcMismatch = "crc-mismatch";
    internal const string ReasonZeroEpoch = "zero-epoch";
    internal const string ReasonZeroNextId = "zero-next-id";
    internal const string ReasonEntryTruncated = "entry-truncated";
    internal const string ReasonZeroId = "zero-id";
    internal const string ReasonIdsNotAscending = "ids-not-ascending";
    internal const string ReasonIdAtOrAboveNext = "id-at-or-above-next";
    internal const string ReasonEvenReplicaCount = "even-replica-count";
    internal const string ReasonEmptyNode = "empty-node";
    internal const string ReasonDuplicateReplica = "duplicate-replica";
    internal const string ReasonZeroK = "zero-k";
    internal const string ReasonKAboveInt = "k-above-int";
    internal const string ReasonEmptySequencer = "empty-sequencer";
    internal const string ReasonBadStatus = "bad-status";
    internal const string ReasonTailOutOfRange = "tail-out-of-range";
    internal const string ReasonTrailingBytes = "trailing-bytes";

    /// <summary>The magic, version, epoch, next ID, floor, and ledger count.</summary>
    internal const int HeaderLength = 36;

    internal const int CrcLength = 4;

    internal const uint FormatVersion = 1;

    private const int IdentityLength = 16;

    /// <summary>The fewest bytes a ledger's entry takes: an open ledger over the whole keyspace with no replicas.</summary>
    private const int MinEntryLength = sizeof(ulong) + sizeof(uint) + sizeof(byte) + sizeof(uint) + 2 * IdentityLength + sizeof(byte);

    /// <summary>The largest page a buffer writer rents (buffers.md §8): an encoding which fits is written in place.</summary>
    private const int LargestPageLength = 1 << 20;

    private const byte StatusOpen = 0;

    private const byte StatusClosed = 1;

    private LedgerMapIndex? _index;
    private LedgerMapLineage? _lineage;

    /// <summary>Initializes a new instance of the <see cref="LedgerMapValue"/> class.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="nextLedgerId"/> is the default, or <paramref name="ledgers"/> is not in ascending ID order below it.
    /// </exception>
    internal LedgerMapValue(ulong epoch, LedgerId nextLedgerId, ulong retirementFloor, ImmutableArray<LedgerMetadata> ledgers)
    {
        if (nextLedgerId == default)
        {
            throw new ArgumentException("A map value names its next ledger ID.", nameof(nextLedgerId));
        }

        ledgers = ledgers.IsDefault ? [] : ledgers;
        var length = HeaderLength + CrcLength;
        for (var index = 0; index < ledgers.Length; index++)
        {
            var id = ledgers[index].Id;
            if (id == default || id >= nextLedgerId || index > 0 && ledgers[index - 1].Id >= id)
            {
                throw new ArgumentException("A map value lists its ledgers in ascending ID order, below its next ledger ID.", nameof(ledgers));
            }

            length = checked(length + EntryLength(ledgers[index]));
        }

        Epoch = epoch;
        NextLedgerId = nextLedgerId;
        RetirementFloor = retirementFloor;
        Ledgers = ledgers;
        EncodedLength = length;
    }

    /// <summary>Gets the value of an absent register: epoch 0, next ledger ID 1, floor 0, and no ledgers.</summary>
    public static LedgerMapValue Empty { get; } = new(0, new LedgerId(1), 0, []);

    /// <summary>Gets the value's epoch: the number of writes the register has applied.</summary>
    public ulong Epoch { get; }

    /// <summary>Gets the ID the next installed ledger takes (LG-I3).</summary>
    public LedgerId NextLedgerId { get; }

    /// <summary>Gets the retirement floor: the greatest revision a floor raise carried (LG-I11).</summary>
    public ulong RetirementFloor { get; }

    /// <summary>Gets the live ledgers, in ascending ID order.</summary>
    public ImmutableArray<LedgerMetadata> Ledgers { get; }

    /// <summary>Gets the length of the value's <c>CLLM</c> encoding, in bytes.</summary>
    internal int EncodedLength { get; }

    private static ReadOnlySpan<byte> Magic => "CLLM"u8;

    /// <summary>Returns the ledger listed under <paramref name="id"/>, or <see langword="null"/> when none is.</summary>
    public LedgerMetadata? Get(LedgerId id) => IndexOf(Ledgers.AsSpan(), id) is >= 0 and var index ? Ledgers[index] : null;

    /// <summary>
    /// Returns the listed ledger with the lowest ID below <paramref name="ledger"/>'s whose range overlaps it, whose drop
    /// must come before <paramref name="ledger"/>'s (LG-I10); <see langword="null"/> when none does, so the value lets
    /// <paramref name="ledger"/> drop once it is complete, or when the value does not list <paramref name="ledger"/>.
    /// </summary>
    /// <remarks>
    /// The first call builds the answer for every listed ledger at once, in <c>O(n·d)</c> lookups over the value's
    /// <c>n</c> ledgers and <c>d</c> distinct range depths, and the value keeps it; later calls look it up. Safe from any
    /// thread.
    /// </remarks>
    public LedgerId? OlderOverlapping(LedgerId ledger)
    {
        var lineage = Volatile.Read(ref _lineage);
        if (lineage is null)
        {
            lineage = new LedgerMapLineage(Ledgers);
            lineage = Interlocked.CompareExchange(ref _lineage, lineage, null) ?? lineage;
        }

        return lineage.OlderOverlapping(ledger);
    }

    /// <summary>
    /// Returns whether the value no longer lists the ledger: its ID is below <see cref="NextLedgerId"/>, so an install
    /// created it, and the value does not list it, so it left the map for good. IDs are never reused (LG-I3), so every
    /// later value no longer lists it either. A ledger at or above <see cref="NextLedgerId"/>, which a later value may
    /// list, is not one.
    /// </summary>
    public bool NoLongerLists(LedgerId id) => id.Value < NextLedgerId.Value && Get(id) is null;

    /// <summary>
    /// Returns the open ledger whose range contains <paramref name="key"/>, and every closed ledger whose range contains
    /// it, in ID order (ledgers.md §2.3); <see langword="null"/> while the value lists no open ledger, before the
    /// keyspace is bootstrapped.
    /// </summary>
    /// <remarks>
    /// It probes one lookup per distinct depth the value lists, copies nothing, and allocates only when closed ledgers
    /// contain the key. The map view builds the value's index before it publishes the value; for another value, the
    /// first lookup builds it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="KeyRange.MaxKeyLength"/> bytes.</exception>
    public LedgerLookup? Find(ReadOnlySpan<byte> key)
    {
        KeyRange.CheckKey(key);
        return BuildIndex().Find(key);
    }

    /// <summary>
    /// Returns the open ledger whose range contains <paramref name="key"/> (ledgers.md §2.3), as <see cref="Find"/>'s
    /// <c>Open</c> does, without collecting the closed ledgers: it probes one lookup per distinct depth and allocates
    /// nothing once the value's index is built. <see langword="null"/> before the keyspace is bootstrapped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or longer than <see cref="KeyRange.MaxKeyLength"/> bytes.</exception>
    public LedgerMetadata? FindOpen(ReadOnlySpan<byte> key)
    {
        KeyRange.CheckKey(key);
        return BuildIndex().FindOpen(key);
    }

    /// <summary>
    /// Returns whether an install which closes <paramref name="closes"/> and opens <paramref name="opens"/>, chosen at
    /// <paramref name="chosenAt"/>, meets its rule in this value (ledgers.md §2.6).
    /// </summary>
    internal bool InstallRuleHolds(ReadOnlySpan<LedgerId> closes, ReadOnlySpan<KeyRange> opens, ulong chosenAt) =>
        InstallRuleHolds(Ledgers.AsSpan(), RetirementFloor, closes, opens, chosenAt);

    /// <summary>
    /// Returns whether an install which closes <paramref name="closes"/> and opens <paramref name="opens"/>, chosen at
    /// <paramref name="chosenAt"/>, meets its rule in a map listing <paramref name="ledgers"/> under
    /// <paramref name="retirementFloor"/> (ledgers.md §2.6): it was chosen at or above the floor, every ledger it closes is
    /// listed open, and every listed open ledger overlapping a range it opens is one it closes (LG-I2, LG-I11). The map
    /// committer's rule, the executor's precondition, and the registry's decision of a pending lifetime are this one test.
    /// </summary>
    /// <remarks>
    /// Once the rule fails in a map, it fails in every later one: the floor only rises, a closed ledger never reopens, and
    /// once any ledger is open the open ledgers cover the keyspace (LG-I1), so a ledger overlapping an opened range stays
    /// listed open until a change closes it, which only an install that closes it can do.
    /// </remarks>
    internal static bool InstallRuleHolds(
        ReadOnlySpan<LedgerMetadata> ledgers,
        ulong retirementFloor,
        ReadOnlySpan<LedgerId> closes,
        ReadOnlySpan<KeyRange> opens,
        ulong chosenAt)
    {
        if (chosenAt < retirementFloor)
        {
            return false;
        }

        foreach (var id in closes)
        {
            if (IndexOf(ledgers, id) is not (>= 0 and var index) || !ledgers[index].IsOpen)
            {
                return false;
            }
        }

        foreach (var ledger in ledgers)
        {
            if (ledger.IsOpen && !closes.Contains(ledger.Id))
            {
                foreach (var range in opens)
                {
                    if (range.Overlaps(ledger.Range))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Returns the position of <paramref name="id"/> in <paramref name="ledgers"/>, in ID order, or −1.</summary>
    internal static int IndexOf(ReadOnlySpan<LedgerMetadata> ledgers, LedgerId id)
    {
        int lower = 0, upper = ledgers.Length - 1;
        while (lower <= upper)
        {
            var middle = lower + (upper - lower) / 2;
            var order = ledgers[middle].Id.CompareTo(id);
            if (order == 0)
            {
                return middle;
            }

            (lower, upper) = order < 0 ? (middle + 1, upper) : (lower, middle - 1);
        }

        return -1;
    }

    /// <summary>Returns the bytes the entry of a ledger over <paramref name="range"/> with <paramref name="replicas"/> replicas takes in the encoding, closed or open.</summary>
    internal static int EntryLength(KeyRange range, int replicas, bool closed) => EntryLength(range.EncodedLength, replicas, closed);

    private static int EntryLength(int rangeLength, int replicas, bool closed) =>
        sizeof(ulong) + rangeLength + sizeof(byte) + replicas * IdentityLength + sizeof(uint)
        + 2 * IdentityLength + sizeof(byte) + (closed ? sizeof(ulong) : 0);

    /// <summary>
    /// Returns the most open ranges a map within <paramref name="maxValueLength"/> bytes holds while each range also lists
    /// one closed predecessor, every ledger with <paramref name="replicas"/> replicas and the ranges split evenly: the
    /// peak of a cluster in which every range was replaced and none of the closed ledgers has dropped yet.
    /// </summary>
    internal static int RangeCapacity(int maxValueLength, int replicas)
    {
        var room = maxValueLength - Empty.EncodedLength;
        var capacity = 0;
        for (var depth = 0; depth < 31; depth++)
        {
            // A range at this depth encodes its depth and its prefix's bytes; a closed entry adds its tail.
            var rangeLength = sizeof(uint) + (depth + 7) / 8;
            var fits = room / (2 * EntryLength(rangeLength, replicas, closed: false) + sizeof(ulong));
            capacity = Math.Max(capacity, Math.Min(fits, 1 << depth));
            if (fits <= 1 << depth)
            {
                break;
            }
        }

        return capacity;
    }

    /// <summary>
    /// Reads the register once and decodes its value, disposing the entry read (conventions.md §1); an absent register
    /// reads as <see cref="Empty"/>, with no version. The map view and the committer read through it.
    /// </summary>
    /// <exception cref="InvalidDataException">The register holds a value which is not a ledger map: the rejection itself.</exception>
    internal static async Task<(LedgerMapValue Value, MetaStoreVersion? Version)> ReadAsync(IConditionalRegister register, CancellationToken cancellationToken)
    {
        using var entry = await register.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return (Empty, null);
        }

        return Decode(entry.Value.AsReadOnlySequence()) switch
        {
            LedgerMapValue value => (value, entry.Version),
            InvalidDataException rejection => throw rejection,
            null => throw new UnreachableException(),
        };
    }

    /// <summary>Builds the value's lookup index, if no lookup has, and returns it.</summary>
    internal LedgerMapIndex BuildIndex()
    {
        var index = Volatile.Read(ref _index);
        if (index is null)
        {
            index = new LedgerMapIndex(Ledgers);
            index = Interlocked.CompareExchange(ref _index, index, null) ?? index;
        }

        return index;
    }

    /// <summary>Writes the value's <c>CLLM</c> encoding into one owned buffer, which the caller owns and disposes.</summary>
    internal ArcBuffer Encode()
    {
        using var writer = new ArcBufferWriter(EncodedLength);
        if (EncodedLength <= LargestPageLength)
        {
            Write(writer.GetSpan(EncodedLength)[..EncodedLength]);
            writer.AdvanceWriter(EncodedLength);
        }
        else
        {
            var bytes = ArrayPool<byte>.Shared.Rent(EncodedLength);
            try
            {
                Write(bytes.AsSpan(0, EncodedLength));
                writer.Write(bytes.AsSpan(0, EncodedLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        return writer.ConsumeSlice(EncodedLength);
    }

    /// <summary>Writes the value's <c>CLLM</c> encoding, <see cref="EncodedLength"/> bytes, to <paramref name="destination"/>.</summary>
    internal void Write(Span<byte> destination)
    {
        Debug.Assert(destination.Length == EncodedLength, "The destination holds exactly the encoding.");
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Epoch);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], NextLedgerId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], RetirementFloor);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], (uint)Ledgers.Length);
        var position = HeaderLength;
        foreach (var ledger in Ledgers)
        {
            var entry = destination[position..];
            BinaryPrimitives.WriteUInt64LittleEndian(entry, ledger.Id.Value);
            var at = sizeof(ulong);
            ledger.Range.Write(entry[at..]);
            at += ledger.Range.EncodedLength;
            at += WriteLocation(entry[at..], ledger.Location);

            BinaryPrimitives.WriteUInt32LittleEndian(entry[at..], (uint)ledger.K);
            at += sizeof(uint);
            at += WriteIdentity(entry[at..], ledger.Primary.Node.Value);
            at += WriteIdentity(entry[at..], ledger.Primary.Sequencer.Value);
            at += ledger.Status switch
            {
                LedgerStatus.Open => WriteOpen(entry[at..]),
                LedgerStatus.Closed closed => WriteClosed(entry[at..], closed.T),
                null => throw new UnreachableException(),
            };
            position += at;
        }

        var crcAt = destination.Length - CrcLength;
        Debug.Assert(position == crcAt, "The ledgers fill the value up to its CRC-32C.");
        BinaryPrimitives.WriteUInt32LittleEndian(destination[crcAt..], Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, destination[..crcAt]));
    }

    /// <summary>
    /// Decodes a <c>CLLM</c> value, which the caller keeps, accepting only its canonical encoding (ledgers.md §7). A
    /// value which spans segments is read from a pooled copy.
    /// </summary>
    internal static LedgerMapDecoding Decode(ReadOnlySequence<byte> value)
    {
        if (value.IsSingleSegment)
        {
            return Decode(value.FirstSpan);
        }

        var length = checked((int)value.Length);
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            value.CopyTo(bytes);
            return Decode(bytes.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// Decodes a <c>CLLM</c> value, accepting only its canonical encoding, or returns the rejection naming the first rule
    /// its bytes break, in ledgers.md §7's order.
    /// </summary>
    internal static LedgerMapDecoding Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength + CrcLength)
        {
            return Rejection(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"A ledger map value holds its {HeaderLength}-byte header and its CRC-32C, but has {bytes.Length} bytes."));
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            return Rejection(ReasonBadMagic, "A ledger map value begins with the magic CLLM.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (version != FormatVersion)
        {
            return Rejection(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"A ledger map value is version {FormatVersion}, not {version}."));
        }

        var crcAt = bytes.Length - CrcLength;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[crcAt..]) != Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, bytes[..crcAt]))
        {
            return Rejection(ReasonCrcMismatch, "A ledger map value's CRC-32C does not match its bytes.");
        }

        var epoch = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        if (epoch == 0)
        {
            return Rejection(ReasonZeroEpoch, "A stored ledger map value was written, so its epoch is at least 1.");
        }

        var nextLedgerId = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        if (nextLedgerId == 0)
        {
            return Rejection(ReasonZeroNextId, "A ledger map value's next ledger ID is at least 1.");
        }

        var floor = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
        var body = bytes[..crcAt];

        // The capacity is bounded by the bytes, never by the count alone.
        var ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>((int)Math.Min(count, (uint)((body.Length - HeaderLength) / MinEntryLength)));
        var position = HeaderLength;
        ulong previousId = 0;
        for (var index = 0u; index < count; index++)
        {
            var rejection = ReadLedger(body, ref position, nextLedgerId, previousId, out var ledger);
            if (rejection is not null)
            {
                return rejection;
            }

            previousId = ledger.Id.Value;
            ledgers.Add(ledger);
        }

        if (position != body.Length)
        {
            return Rejection(ReasonTrailingBytes, string.Create(CultureInfo.InvariantCulture, $"A ledger map value's last ledger ends at its CRC-32C, but {body.Length - position} bytes follow it."));
        }

        var value = new LedgerMapValue(epoch, new LedgerId(nextLedgerId), floor, ledgers.DrainToImmutable());
        Debug.Assert(value.EncodedLength == bytes.Length, "A value decoded from its canonical encoding re-encodes to its length.");
        return value;
    }

    private static int EntryLength(LedgerMetadata ledger) => EntryLength(ledger.Range, ledger.Location, !ledger.IsOpen);

    /// <summary>
    /// Returns the bytes the entry of a ledger over <paramref name="range"/> at <paramref name="location"/> takes in the
    /// encoding, closed or open: a ledger in the object store stores a replica count of 0 and no replicas (ledgers.md §7).
    /// </summary>
    internal static int EntryLength(KeyRange range, LedgerLocation location, bool closed) => EntryLength(range, ReplicaCount(location), closed);

    /// <summary>Returns the replica count a ledger's location stores: <c>N</c> on nodes, and 0 in the object store.</summary>
    private static int ReplicaCount(LedgerLocation location) => location switch
    {
        LedgerLocation.Nodes nodes => nodes.Replicas.Length,
        LedgerLocation.ObjectStore => 0,
        null => throw new UnreachableException("conventions.md §15: a listed ledger's location holds a case."),
    };

    /// <summary>Writes a ledger's location: its replica count and replicas, or a count of 0 in the object store.</summary>
    private static int WriteLocation(Span<byte> destination, LedgerLocation location)
    {
        switch (location)
        {
            case LedgerLocation.Nodes nodes:
                destination[0] = (byte)nodes.Replicas.Length;
                var at = sizeof(byte);
                foreach (var replica in nodes.Replicas)
                {
                    at += WriteIdentity(destination[at..], replica.Value);
                }

                return at;
            case LedgerLocation.ObjectStore:
                destination[0] = 0;
                return sizeof(byte);
            case null:
            default:
                throw new UnreachableException("conventions.md §15: a listed ledger's location holds a case.");
        }
    }

    /// <summary>
    /// Reads one ledger's entry at <paramref name="position"/>, advancing past it, and checks its fields in layout order,
    /// each first to end at or before the CRC-32C and then against its own rule.
    /// </summary>
    private static InvalidDataException? ReadLedger(ReadOnlySpan<byte> body, ref int position, ulong nextLedgerId, ulong previousId, out LedgerMetadata ledger)
    {
        ledger = default;
        var fields = body[position..];
        var at = 0;
        if (!Take(fields, ref at, sizeof(ulong), out var idBytes))
        {
            return Rejection(ReasonEntryTruncated, "A ledger's entry runs past the value's CRC-32C.");
        }

        var id = BinaryPrimitives.ReadUInt64LittleEndian(idBytes);
        if (id == 0)
        {
            return Rejection(ReasonZeroId, "A listed ledger's ID is at least 1.");
        }

        if (id <= previousId)
        {
            return Rejection(ReasonIdsNotAscending, string.Create(CultureInfo.InvariantCulture, $"A ledger map value lists its ledgers in ascending ID order, but ledger {id} follows ledger {previousId}."));
        }

        if (id >= nextLedgerId)
        {
            return Rejection(ReasonIdAtOrAboveNext, string.Create(CultureInfo.InvariantCulture, $"A listed ledger's ID is below the next ledger ID {nextLedgerId}, but is {id}."));
        }

        var rangeRejection = KeyRange.Read(fields[at..], out var range);
        if (rangeRejection is not null)
        {
            return rangeRejection;
        }

        at += range.EncodedLength;
        var locationRejection = ReadLocation(fields, ref at, id, out var location);
        if (locationRejection is not null)
        {
            return locationRejection;
        }

        if (!Take(fields, ref at, sizeof(uint), out var kBytes))
        {
            return EntryTruncated(id);
        }

        var k = BinaryPrimitives.ReadUInt32LittleEndian(kBytes);
        if (k == 0)
        {
            return Rejection(ReasonZeroK, string.Create(CultureInfo.InvariantCulture, $"Ledger {id}'s window K is at least 1."));
        }

        if (k > int.MaxValue)
        {
            return Rejection(ReasonKAboveInt, string.Create(CultureInfo.InvariantCulture, $"Ledger {id}'s window K is at most {int.MaxValue}, not {k}."));
        }

        if (!Take(fields, ref at, IdentityLength, out var nodeBytes))
        {
            return EntryTruncated(id);
        }

        var node = ReadIdentity(nodeBytes);
        if (node == Guid.Empty)
        {
            return EmptyNode(id);
        }

        if (!Take(fields, ref at, IdentityLength, out var sequencerBytes))
        {
            return EntryTruncated(id);
        }

        var sequencer = ReadIdentity(sequencerBytes);
        if (sequencer == Guid.Empty)
        {
            return Rejection(ReasonEmptySequencer, string.Create(CultureInfo.InvariantCulture, $"Ledger {id}'s primary claim names the empty sequencer identity."));
        }

        var statusRejection = ReadStatus(fields, ref at, id, out var status);
        if (statusRejection is not null)
        {
            return statusRejection;
        }

        ledger = new LedgerMetadata(new LedgerId(id), range, location, (int)k, new PrimaryClaim(new NodeId(node), new SequencerInstanceId(sequencer)), status);
        position += at;
        return null;
    }

    /// <summary>
    /// Reads ledger <paramref name="id"/>'s location: its replica count and replicas, an odd number of distinct, non-empty
    /// nodes, or a count of 0 for a ledger in the object store (ledgers.md §7).
    /// </summary>
    private static InvalidDataException? ReadLocation(ReadOnlySpan<byte> fields, ref int at, ulong id, out LedgerLocation location)
    {
        location = default;
        if (!Take(fields, ref at, sizeof(byte), out var countByte))
        {
            return EntryTruncated(id);
        }

        var count = countByte[0];
        if (count == 0)
        {
            location = LedgerLocation.ObjectStore.Instance;
            return null;
        }

        if (count % 2 == 0)
        {
            return Rejection(ReasonEvenReplicaCount, string.Create(CultureInfo.InvariantCulture, $"Ledger {id} has an odd number of replicas, or none in the object store, not {count}."));
        }

        var nodes = new NodeId[count];
        for (var index = 0; index < count; index++)
        {
            if (!Take(fields, ref at, IdentityLength, out var replicaBytes))
            {
                return EntryTruncated(id);
            }

            var replica = ReadIdentity(replicaBytes);
            if (replica == Guid.Empty)
            {
                return EmptyNode(id);
            }

            nodes[index] = new NodeId(replica);
            if (nodes.AsSpan(0, index).Contains(nodes[index]))
            {
                return Rejection(ReasonDuplicateReplica, string.Create(CultureInfo.InvariantCulture, $"Ledger {id} names replica {nodes[index]} twice."));
            }
        }

        location = new LedgerLocation.Nodes(ImmutableCollectionsMarshal.AsImmutableArray(nodes));
        return null;
    }

    /// <summary>Reads ledger <paramref name="id"/>'s status, and its recorded tail when it is closed.</summary>
    private static InvalidDataException? ReadStatus(ReadOnlySpan<byte> fields, ref int at, ulong id, out LedgerStatus status)
    {
        status = default;
        if (!Take(fields, ref at, sizeof(byte), out var statusByte))
        {
            return EntryTruncated(id);
        }

        if (statusByte[0] == StatusOpen)
        {
            status = LedgerStatus.Open.Instance;
            return null;
        }

        if (statusByte[0] != StatusClosed)
        {
            return Rejection(ReasonBadStatus, string.Create(CultureInfo.InvariantCulture, $"Ledger {id}'s status is 0 (open) or 1 (closed), not {statusByte[0]}."));
        }

        if (!Take(fields, ref at, sizeof(ulong), out var tailBytes))
        {
            return EntryTruncated(id);
        }

        var tail = BinaryPrimitives.ReadUInt64LittleEndian(tailBytes);
        if (tail > long.MaxValue)
        {
            return Rejection(ReasonTailOutOfRange, string.Create(CultureInfo.InvariantCulture, $"Ledger {id}'s recorded tail is below 2^63, not {tail}."));
        }

        status = new LedgerStatus.Closed(new Slot((long)tail));
        return null;
    }

    /// <summary>Takes the next field of <paramref name="length"/> bytes, unless it would end past <paramref name="fields"/>.</summary>
    private static bool Take(ReadOnlySpan<byte> fields, ref int at, int length, out ReadOnlySpan<byte> field)
    {
        if (fields.Length - at < length)
        {
            field = default;
            return false;
        }

        field = fields.Slice(at, length);
        at += length;
        return true;
    }

    private static InvalidDataException EntryTruncated(ulong id) =>
        Rejection(ReasonEntryTruncated, string.Create(CultureInfo.InvariantCulture, $"Ledger {id}'s entry runs past the value's CRC-32C."));

    private static InvalidDataException EmptyNode(ulong id) =>
        Rejection(ReasonEmptyNode, string.Create(CultureInfo.InvariantCulture, $"Ledger {id} names the empty node identity."));

    private static InvalidDataException Rejection(string reason, string message) => DecodeRejection.Create(reason, message);

    /// <summary>Reads a node or sequencer identity: its UUID's 16 bytes, big-endian (identity.md §2, §8).</summary>
    private static Guid ReadIdentity(ReadOnlySpan<byte> bytes) => new(bytes, bigEndian: true);

    private static int WriteIdentity(Span<byte> destination, Guid identity)
    {
        var written = identity.TryWriteBytes(destination, bigEndian: true, out var length);
        Debug.Assert(written && length == IdentityLength, "An identity takes 16 bytes.");
        return IdentityLength;
    }

    private static int WriteOpen(Span<byte> destination)
    {
        destination[0] = StatusOpen;
        return sizeof(byte);
    }

    private static int WriteClosed(Span<byte> destination, Slot t)
    {
        destination[0] = StatusClosed;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[sizeof(byte)..], (ulong)t.Value);
        return sizeof(byte) + sizeof(ulong);
    }
}
