using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// An archive-log store over an <see cref="IObjectStore"/> with create-once writes only (archive-logs.md §7.4, §9.2): the
/// chunk layout, for object stores which append to no object, as S3 and Google Cloud Storage do not. The log
/// <c>{name}</c> is the objects under <c>{name}/</c>: each generation's base, <c>{name}/{g:D20}/base-{h:x16}</c>, with
/// <c>h</c> the key's xxHash64, a <c>CLAB</c> header followed by the base's content; and each append run's chunk,
/// <c>{name}/{g:D20}/{P₀:D20}</c>, holding the run from length <c>P₀</c> of the generation's appends.
/// </summary>
/// <remarks>
/// <para>
/// The log's current generation is the highest with a base, and its appends are its chunks chained by offset from 0,
/// stopping at a gap. A write lists the log, creates its object, only when absent, only while the log is at the
/// write's position, and lists the log again to decide: an append holds once the chain of the current generation reaches
/// its chunk, and an opener once its base is the highest. An object which lands late so lands only in a generation which
/// exists: at its chain's end, where it is the same write's, or in a generation a newer one has replaced, which no reader
/// reads. A create which meets <see cref="ObjectCreateOutcome.Exists"/> answers
/// <see cref="ArchiveLogWriteOutcome.ConditionFailed"/>, as a retry which meets its own landing does on Azure; its caller
/// classifies (archive-logs.md §8.3).
/// </para>
/// <para>
/// A log's objects are the names exactly two components below <c>{name}/</c>, so a log whose name extends another's with
/// <c>/</c> keeps its own objects apart. Objects under <c>durable-storage/</c> are Durable Storage's own (§7.5), never a log's.
/// </para>
/// </remarks>
public sealed class ChunkArchiveLogStore : IArchiveLogStore
{
    /// <summary>The chunk layout's name in the store's record.</summary>
    public const string LayoutName = "chunks";

    /// <summary>The chunk layout's name rule: an object-name prefix short enough for its bases' names.</summary>
    public const string NameRule = "chunks-1";

    /// <summary>The most chunks a generation holds; the headroom above <see cref="ReplaceRequestChunks"/> admits 16 more segments' runs.</summary>
    public const ulong MaxChunks = 48;

    /// <summary>The chunks at which the layout asks for a <c>Replace</c>, so a cold read makes at most about 34 requests.</summary>
    public const ulong ReplaceRequestChunks = 32;

    private const int Digits = 20;
    private const string BaseMark = "base-";
    private const int HashDigits = 16;

    /// <summary>The characters a base's name adds to its log's: <c>/{g:D20}/base-{h:x16}</c>.</summary>
    private const int BaseSuffixLength = 1 + Digits + 1 + 5 + HashDigits;

    private readonly IObjectStore _objects;

    /// <summary>Initializes a new instance of the <see cref="ChunkArchiveLogStore"/> class.</summary>
    /// <param name="objects">The object store, already under the cluster's store prefix, which the store's record and drain markers share.</param>
    public ChunkArchiveLogStore(IObjectStore objects)
        : this(objects, ChunkLayout)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ChunkArchiveLogStore"/> class with another layout of chunks, for tests.</summary>
    /// <exception cref="ArgumentException">The layout packs blocks.</exception>
    internal ChunkArchiveLogStore(IObjectStore objects, ArchiveLogLayout layout)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Packing != ArchiveLogPacking.Chunks)
        {
            throw new ArgumentException("The chunk layout packs each append run as one chunk.", nameof(layout));
        }

        _objects = objects;
        Layout = layout;
    }

    /// <summary>Gets the chunk layout: one chunk per append run, of any length, 48 chunks, and a request at 32.</summary>
    public static ArchiveLogLayout ChunkLayout { get; } = new(LayoutName, ArchiveLogPacking.Chunks, null, MaxChunks, ReplaceRequestChunks, NameRule);

    /// <inheritdoc/>
    public ArchiveLogLayout Layout { get; }

    /// <summary>
    /// Gets a value indicating whether the store is the <c>creates-before-listing</c> mutant, whose writes create their
    /// objects without first listing the log at their position; for tests.
    /// </summary>
    internal bool CreatesBeforeListingMutant { get; init; }

    /// <summary>
    /// Gets a value indicating whether the store is the <c>skips-read-recheck</c> mutant, whose reads answer from their
    /// first listing without listing the log again; for tests.
    /// </summary>
    internal bool SkipsReadRecheckMutant { get; init; }

    /// <summary>Gets the most bases a page of <see cref="ListGenerationsAsync"/> lists, at least 1; 1,000 by default.</summary>
    public int ListPageSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 1_000;

    /// <inheritdoc/>
    /// <remarks>
    /// A name whose bases' names are object names, so at most 157 characters, and which lies outside
    /// <c>durable-storage/</c>, where the store's record and drain markers lie.
    /// </remarks>
    public bool AdmitsName(string name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= ObjectName.MaxLength - BaseSuffixLength
        && ObjectName.IsValid(name)
        && !(name + "/").StartsWith(ArchiveLogNames.ReservedPrefix, StringComparison.Ordinal);

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        CheckName(name);
        if (await ListLogAsync(name, cancellationToken).ConfigureAwait(false) is not { } log)
        {
            return ArchiveLogRead.Absent.Instance;
        }

        ArchiveLogRead.Read.CheckContentLength(log.BaseLength - ArchiveLogChunkBase.HeaderLength + (long)log.Length);
        using var @base = await _objects.ReadAsync(log.Base, 0, checked((int)log.BaseLength), cancellationToken).ConfigureAwait(false);
        if (@base is not { } baseBytes)
        {
            // A newer generation replaced the base and the sweeper deleted it after the listing.
            return ArchiveLogRead.Stale.Instance;
        }

        var header = Decoded(name, log.Generation, ArchiveLogChunkBase.Decode(CheckLength(baseBytes, log.Base, log.BaseLength).AsReadOnlySequence()));
        using var writer = new ArcBufferWriter();
        writer.Write(baseBytes.AsReadOnlySequence().Slice(ArchiveLogChunkBase.HeaderLength));
        foreach (var chunk in log.Chain)
        {
            using var read = await _objects.ReadAsync(chunk.Name, 0, checked((int)chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read is not { } bytes)
            {
                return ArchiveLogRead.Stale.Instance;
            }

            var chunkBytes = CheckLength(bytes, chunk.Name, chunk.Length).AsReadOnlySequence();
            if (ArchiveLogChunk.Check(chunkBytes) is { } rejection)
            {
                throw new ArchiveLogCorruptException($"The archive-log chunk '{chunk.Name}' is malformed: {rejection.Message}", rejection);
            }

            writer.Write(chunkBytes.Slice(0, chunkBytes.Length - ArchiveLogChunk.TrailerLength));
        }

        if (!await StillAtAsync(name, log.Generation, cancellationToken).ConfigureAwait(false))
        {
            return ArchiveLogRead.Stale.Instance;
        }

        using var content = writer.Length == 0 ? default : writer.ConsumeSlice(writer.Length);
        var position = new ArchiveLogPosition(log.Generation, header.Opener?.Rows ?? 0, log.Length, (ulong)log.Chain.Count);
        return new ArchiveLogRead.Read(position, header.Opener, checked((int)header.ContentLength), content);
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        CheckName(name);
        while (true)
        {
            if (await ListLogAsync(name, cancellationToken).ConfigureAwait(false) is not { } log)
            {
                return null;
            }

            using var read = await _objects.ReadAsync(log.Base, 0, ArchiveLogChunkBase.HeaderLength, cancellationToken).ConfigureAwait(false);
            if (read is not { } bytes)
            {
                // A newer generation replaced the base and the sweeper deleted it after the listing: list again.
                continue;
            }

            var header = Decoded(name, log.Generation, ArchiveLogChunkBase.DecodeHeader(bytes.AsReadOnlySequence().ToArray(), log.BaseLength));
            if (!await StillAtAsync(name, log.Generation, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            return new ArchiveLogProperties(new ArchiveLogPosition(log.Generation, header.Opener?.Rows ?? 0, log.Length, (ulong)log.Chain.Count), header.Opener);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
    {
        CheckName(name);
        ArgumentNullException.ThrowIfNull(run);
        if (run.Count == 0 || run.Any(static unit => unit.IsEmpty))
        {
            throw new ArgumentException("An append run holds at least one unit, each non-empty.", nameof(run));
        }

        var start = ArchiveLogConditions.RunStart(from);
        var at = await ListLogAsync(name, cancellationToken).ConfigureAwait(false) is { } listed ? (listed.Generation, listed.Length, Units: (ulong)listed.Chain.Count) : default;
        if (from.IsOrigin && at == default)
        {
            // An existing base of generation 1 is this write's, by the callers' demand; the chunk's condition decides.
            _ = await CreateAsync(BaseName(name.Value, 1, name.KeyHash), ArchiveLogChunkBase.Encode(name.KeyHash, null, ReadOnlySequence<byte>.Empty), cancellationToken).ConfigureAwait(false);
            at = (1, 0, 0);
        }

        var offset = start.Length;
        foreach (var unit in run)
        {
            // A chunk is created only in a generation which exists, at its chain's end, so one which lands late lands in a
            // generation a newer one has replaced, which no reader reads, or meets the same write's chunk.
            if ((at.Generation, at.Length) != (start.Generation, offset) && !CreatesBeforeListingMutant)
            {
                return ArchiveLogWriteOutcome.ConditionFailed.Instance;
            }

            // Admission keeps a key's chunks within the limit only as far as the segment starts among its in-flight
            // entries fit the headroom above the replace request (archive §3), which nothing bounds by construction, so a
            // generation at its limit refuses the chunk, which blocks the key's drain (archive-logs.md §8.1).
            if (at.Units >= Layout.MaxUnits)
            {
                throw new ArchiveLogRefusedException(string.Create(CultureInfo.InvariantCulture, $"The archive log '{name}' holds {at.Units} chunks in generation {at.Generation}, its layout's limit."));
            }

            using var writer = new ArcBufferWriter();
            ArchiveLogChunk.Encode(writer, unit);
            using var bytes = writer.ConsumeSlice(writer.Length);
            var chunk = ChunkName(name.Value, start.Generation, offset);
            if (await CreateAsync(chunk, bytes, cancellationToken).ConfigureAwait(false) is not ObjectCreateOutcome.Created
                || await ListLogAsync(name, cancellationToken).ConfigureAwait(false) is not { } log
                || log.Generation != start.Generation
                || !log.Chain.Any(listed => listed.Name == chunk))
            {
                return ArchiveLogWriteOutcome.ConditionFailed.Instance;
            }

            at = (log.Generation, log.Length, (ulong)log.Chain.Count);
            offset += (ulong)unit.Length;
        }

        return ArchiveLogWriteOutcome.Applied.Instance;
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken)
    {
        CheckName(name);
        var generation = checked(from.Generation + 1);
        var encoded = ArchiveLogChunkBase.Encode(name.KeyHash, opener, content);
        var baseName = BaseName(name.Value, generation, name.KeyHash);

        // A base is created only on the generation it follows, so one which lands late meets the same write's base or lies
        // below a newer generation, which no reader reads.
        var current = (await ListLogAsync(name, cancellationToken).ConfigureAwait(false))?.Generation ?? 0;
        if (current >= generation)
        {
            await CheckBaseAsync(name, baseName, generation, encoded, cancellationToken).ConfigureAwait(false);
            return ArchiveLogWriteOutcome.ConditionFailed.Instance;
        }

        if (current != from.Generation && !CreatesBeforeListingMutant)
        {
            return ArchiveLogWriteOutcome.ConditionFailed.Instance;
        }

        if (await CreateAsync(baseName, encoded, cancellationToken).ConfigureAwait(false) is not ObjectCreateOutcome.Created)
        {
            await CheckBaseAsync(name, baseName, generation, encoded, cancellationToken).ConfigureAwait(false);
            return ArchiveLogWriteOutcome.ConditionFailed.Instance;
        }

        return await ListLogAsync(name, cancellationToken).ConfigureAwait(false) is { } log && log.Generation == generation
            ? ArchiveLogWriteOutcome.Applied.Instance
            : ArchiveLogWriteOutcome.ConditionFailed.Instance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Lists pages of at most <see cref="ListPageSize"/> bases, with the generations they bring, from one listing of the
    /// store, in the ordinal order of the bases' object names, <c>{log}/{g:D20}/base-{h:x16}</c>; a continuation is the
    /// object name of its page's last base, from which the next listing starts.
    /// </para>
    /// <para>
    /// A generation which holds chunks and no base, a straggler's chunks which landed after the sweeper deleted the
    /// generation, is listed just before the next base of its log, with that base's key hash, so the sweeper deletes it
    /// as it deletes a superseded base: a log's generations list in ascending order, and a generation's chunks list before
    /// its own base. One with no later base in the listing, which only a log with no base above it can hold, is not
    /// listed.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = new List<ArchiveLogGeneration>();
        var chunked = new Dictionary<string, SortedSet<ulong>>(StringComparer.Ordinal);
        string? last = null;
        await foreach (var listed in _objects.ListAsync(string.Empty, continuation, cancellationToken).ConfigureAwait(false))
        {
            var objectName = listed.Name.Value;
            if (objectName.StartsWith(ArchiveLogNames.ReservedPrefix, StringComparison.Ordinal)
                || Parse(objectName) is not { } parsed)
            {
                continue;
            }

            if (parsed.KeyHash is not { } keyHash)
            {
                if (!chunked.TryGetValue(parsed.Log, out var generations))
                {
                    generations = [];
                    chunked.Add(parsed.Log, generations);
                }

                _ = generations.Add(parsed.Generation);
                continue;
            }

            var entries = new List<ArchiveLogGeneration>();
            if (chunked.Remove(parsed.Log, out var withChunks))
            {
                entries.AddRange(withChunks.Where(generation => generation < parsed.Generation).Select(generation => new ArchiveLogGeneration(parsed.Log, generation, keyHash)));
            }

            entries.Add(new ArchiveLogGeneration(parsed.Log, parsed.Generation, keyHash));
            if (page.Count > 0 && page.Count + entries.Count > ListPageSize)
            {
                yield return new ArchiveLogGenerationPage([.. page], last);
                page.Clear();
            }

            page.AddRange(entries);
            last = objectName;
        }

        yield return new ArchiveLogGenerationPage([.. page], null);
    }

    /// <inheritdoc/>
    /// <remarks>Deletes the generation's chunks, then its base, so a sweep which fails between them lists the base again.</remarks>
    public async ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(log);
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        var chunks = new List<ObjectName>();
        var bases = new List<ObjectName>();
        await foreach (var listed in _objects.ListAsync(GenerationPrefix(log, generation), cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (Parse(listed.Name.Value) is { } parsed && parsed.Log == log)
            {
                (parsed.KeyHash is null ? chunks : bases).Add(listed.Name);
            }
        }

        foreach (var name in chunks.Concat(bases))
        {
            await _objects.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns the name of generation <paramref name="generation"/>'s base of the log <paramref name="log"/>.</summary>
    internal static ObjectName BaseName(string log, ulong generation, ulong keyHash) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{log}/{generation:D20}/{BaseMark}{keyHash:x16}"));

    /// <summary>Returns the name of the chunk starting at length <paramref name="offset"/> of a generation's appends.</summary>
    internal static ObjectName ChunkName(string log, ulong generation, ulong offset) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{log}/{generation:D20}/{offset:D20}"));

    /// <summary>
    /// Parses an object name as a log's base or chunk, strictly: <c>{log}/{g:D20}/base-{h:x16}</c> with <c>g ≥ 1</c> and
    /// <c>h</c> in lowercase, or <c>{log}/{g:D20}/{P₀:D20}</c>; null for any other name.
    /// </summary>
    internal static ParsedName? Parse(string name)
    {
        var last = name.LastIndexOf('/');
        var generationStart = last > 0 ? name.LastIndexOf('/', last - 1) : -1;
        var generation = generationStart > 0 && last - generationStart - 1 == Digits ? ParseDigits(name.AsSpan(generationStart + 1, Digits)) : null;
        if (generation is null or 0)
        {
            return null;
        }

        var leaf = name.AsSpan(last + 1);
        var log = name[..generationStart];
        if (leaf.Length == Digits)
        {
            return ParseDigits(leaf) is { } offset ? new ParsedName(log, generation.Value, offset, null) : null;
        }

        return leaf.Length == BaseMark.Length + HashDigits
            && leaf.StartsWith(BaseMark, StringComparison.Ordinal)
            && !leaf[BaseMark.Length..].ContainsAnyExcept("0123456789abcdef")
            ? new ParsedName(log, generation.Value, null, ulong.Parse(leaf[BaseMark.Length..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
            : null;
    }

    private static ulong? ParseDigits(ReadOnlySpan<char> digits) =>
        digits.Length == Digits && !digits.ContainsAnyExceptInRange('0', '9')
            && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string GenerationPrefix(string log, ulong generation) =>
        string.Create(CultureInfo.InvariantCulture, $"{log}/{generation:D20}/");

    private static ArcBuffer CheckLength(ArcBuffer bytes, ObjectName name, long length) =>
        bytes.Length == length
            ? bytes
            : throw new ArchiveLogCorruptException($"The archive-log object '{name}' held {bytes.Length} bytes when read and {length} when listed, though an object never changes.");

    /// <summary>Returns a base's decoded header, which names the log's key.</summary>
    /// <exception cref="ArchiveLogCorruptException">The base breaks its format or names another key.</exception>
    private static ArchiveLogChunkBase Decoded(ArchiveLogName name, ulong generation, ArchiveLogChunkBaseDecoding decoding) =>
        decoding switch
        {
            ArchiveLogChunkBase { Opener: null } when generation > 1 => throw new ArchiveLogCorruptException(
                $"The archive log '{name}''s base of generation {generation} records no opener, though only an opener creates a generation above 1.",
                DecodeRejection.Create(ArchiveLogChunkBase.ReasonOpenerMissing, "A base above generation 1 records no opener.")),
            ArchiveLogChunkBase header => header.KeyHash == name.KeyHash
                ? header
                : throw new ArchiveLogCorruptException($"The archive log '{name}''s base of generation {generation} records the key hash {header.KeyHash:x16}, not its key's {name.KeyHash:x16}: it names another key."),
            InvalidDataException rejection => throw new ArchiveLogCorruptException($"The archive log '{name}''s base of generation {generation} is malformed: {rejection.Message}", rejection),
            null => throw new UnreachableException(),
        };

    private void CheckName(ArchiveLogName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!AdmitsName(name.Value))
        {
            throw new ArgumentException($"The chunk layout forms no objects for the log '{name}': its caller did not check AdmitsName.", nameof(name));
        }
    }

    /// <summary>
    /// Lists the log again after a read, and returns whether its current generation is still <paramref name="generation"/>.
    /// A listing names every object which existed throughout it, but may miss one created during it, such as a newer
    /// generation's base, while naming a straggler's chunk created after that base; the read's state is then one the log
    /// never held, and the listing which follows names the newer base.
    /// </summary>
    private async ValueTask<bool> StillAtAsync(ArchiveLogName name, ulong generation, CancellationToken cancellationToken) =>
        SkipsReadRecheckMutant || (await ListLogAsync(name, cancellationToken).ConfigureAwait(false))?.Generation == generation;

    /// <summary>
    /// Checks an existing base against an opener's own, by the header which records its kind, rows, tombstone, and the
    /// content's length and MD5: the same write from the same position (archive-logs.md §3). A base no opener created,
    /// a key's first append run's, holds no write to compare, as Azure keeps no checkpoint for it; and a base the sweeper
    /// deleted belongs to a generation the log has passed.
    /// </summary>
    /// <exception cref="ArchiveLogCorruptException">The stored base differs, so another write came from the same position.</exception>
    private async ValueTask CheckBaseAsync(ArchiveLogName name, ObjectName baseName, ulong generation, byte[] encoded, CancellationToken cancellationToken)
    {
        using var stored = await _objects.ReadAsync(baseName, 0, ArchiveLogChunkBase.HeaderLength, cancellationToken).ConfigureAwait(false);
        if (stored is not { } bytes)
        {
            return;
        }

        var header = bytes.AsReadOnlySequence().ToArray();
        if (ArchiveLogChunkBase.RecordsNoOpener(header))
        {
            return;
        }

        if (!header.AsSpan().SequenceEqual(encoded.AsSpan(0, ArchiveLogChunkBase.HeaderLength)))
        {
            throw new ArchiveLogCorruptException($"The archive log '{name}' holds a base of generation {generation} other than the opener's: two writes came from one position.");
        }
    }

    /// <summary>Creates an object only when absent; a refusal no retry removes is the log's.</summary>
    private async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, byte[] content, CancellationToken cancellationToken)
    {
        using var bytes = ArcBuffer.Wrap(content);
        return await CreateAsync(name, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, CancellationToken cancellationToken)
    {
        try
        {
            return await _objects.CreateAsync(name, content, ObjectStoreChecksums.Crc32C(content), cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectStoreRefusedException refused)
        {
            throw new ArchiveLogRefusedException($"The object store refused the archive-log object '{name}', which no retry can create.", refused);
        }
    }

    /// <summary>
    /// Lists the log's objects and returns its current generation: the highest with a base, its base, and the chain of its
    /// chunks from offset 0 up to the first gap; null when no generation has a base.
    /// </summary>
    /// <exception cref="ArchiveLogCorruptException">The current generation has two bases, or its base names another key.</exception>
    private async ValueTask<ListedLog?> ListLogAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        var generations = new Dictionary<ulong, (List<(ListedObject Object, ulong KeyHash)> Bases, Dictionary<ulong, ListedObject> Chunks)>();
        await foreach (var listed in _objects.ListAsync(name.Value + "/", cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (Parse(listed.Name.Value) is not { } parsed || parsed.Log != name.Value)
            {
                continue;
            }

            if (!generations.TryGetValue(parsed.Generation, out var objects))
            {
                objects = ([], []);
                generations.Add(parsed.Generation, objects);
            }

            if (parsed.Offset is { } offset)
            {
                objects.Chunks[offset] = listed;
            }
            else
            {
                objects.Bases.Add((listed, parsed.KeyHash!.Value));
            }
        }

        var current = generations.Where(static pair => pair.Value.Bases.Count > 0).Select(static pair => pair.Key).DefaultIfEmpty().Max();
        if (current == 0)
        {
            return null;
        }

        var (bases, chunks) = generations[current];
        if (bases.Count > 1)
        {
            throw new ArchiveLogCorruptException($"The archive log '{name}' holds {bases.Count} bases of generation {current}, under distinct key hashes, though a generation has one.");
        }

        var (@base, keyHash) = bases[0];
        if (keyHash != name.KeyHash)
        {
            throw new ArchiveLogCorruptException($"The archive log '{name}''s base of generation {current}, '{@base.Name}', names the key hash {keyHash:x16}, not its key's {name.KeyHash:x16}: it names another key.");
        }

        if (@base.Length < ArchiveLogChunkBase.HeaderLength)
        {
            throw new ArchiveLogCorruptException(
                $"The archive log '{name}''s base of generation {current}, '{@base.Name}', holds {@base.Length} bytes, fewer than its header.",
                DecodeRejection.Create(ArchiveLogChunkBase.ReasonTruncated, "A base is shorter than its header."));
        }

        var chain = new List<ListedObject>();
        var length = 0UL;
        while (chunks.TryGetValue(length, out var chunk))
        {
            if (chunk.Length <= ArchiveLogChunk.TrailerLength)
            {
                throw new ArchiveLogCorruptException(
                    $"The archive-log chunk '{chunk.Name}' holds {chunk.Length} bytes, no content before its trailer.",
                    DecodeRejection.Create(ArchiveLogChunk.ReasonTruncated, "A chunk holds no content before its trailer."));
            }

            chain.Add(chunk);
            length += (ulong)(chunk.Length - ArchiveLogChunk.TrailerLength);
        }

        return new ListedLog(current, @base.Name, @base.Length, chain, length);
    }

    /// <summary>A parsed object name: a base, with its key hash, or a chunk, with its offset.</summary>
    internal readonly record struct ParsedName(string Log, ulong Generation, ulong? Offset, ulong? KeyHash);

    /// <summary>A log's current generation as a listing found it.</summary>
    private sealed record ListedLog(ulong Generation, ObjectName Base, long BaseLength, List<ListedObject> Chain, ulong Length);
}
