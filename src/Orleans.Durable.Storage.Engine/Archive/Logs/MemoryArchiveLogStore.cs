using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>The requests of <see cref="MemoryArchiveLogStore"/>, each of which a fault may fail (archive-logs.md §9, §10).</summary>
public enum ArchiveLogRequestKind
{
    /// <summary>A key's first append run creates its log at generation 1, as Azure's <c>Create</c> of the append blob.</summary>
    CreateLog,

    /// <summary>One unit of an append run, as Azure's <c>Append Block</c>.</summary>
    AppendUnit,

    /// <summary>An opener's base, as Azure's <c>Put Blob</c> of a checkpoint.</summary>
    CreateBase,

    /// <summary>An opener's new generation, as Azure's <c>Put Blob</c> re-creating the write-ahead blob.</summary>
    OpenGeneration,

    /// <summary>A properties read, as Azure's <c>Get Blob Properties</c>.</summary>
    GetProperties,

    /// <summary>A read of the log, as Azure's <c>Get Blob</c>s.</summary>
    Read,

    /// <summary>A listing of the generation bases.</summary>
    List,

    /// <summary>A deletion of a generation's base.</summary>
    DeleteBase,
}

/// <summary>A request <see cref="MemoryArchiveLogStore"/> is about to make, which its fault injector sees.</summary>
/// <param name="Kind">The request.</param>
/// <param name="Log">The log's name, or empty for a listing.</param>
/// <param name="Generation">The generation the request names: an append's, a created one's, or a deleted base's; 0 for a read.</param>
/// <param name="Offset">An append unit's starting length, or 0.</param>
public readonly record struct ArchiveLogRequest(ArchiveLogRequestKind Kind, string Log, ulong Generation, ulong Offset);

/// <summary>A fault <see cref="MemoryArchiveLogStore"/> injects into one request.</summary>
public enum ArchiveLogFault
{
    /// <summary>The request runs as usual.</summary>
    None,

    /// <summary>The request fails with an <see cref="IOException"/> before it lands.</summary>
    FailBefore,

    /// <summary>The request lands and then fails with an <see cref="IOException"/>: its answer is lost.</summary>
    FailAfter,

    /// <summary>
    /// A write lands and then answers that its condition failed, as a client's retry which meets its own landing does
    /// (a 409 or 412 on Azure); a read runs as usual.
    /// </summary>
    RetryMeetsLanding,
}

/// <summary>
/// An archive-log store in process memory, for tests and simulation (archive-logs.md §9): Azure's model of generations,
/// offsets, blocks, and conditions. Each log has a current generation, whose write-ahead content is a list of units, and
/// the opener which created it; each opener's base is kept apart, by generation, as Azure keeps a checkpoint. An append
/// unit lands only while the log is at its generation with the length at which it starts (<c>appendpos</c> and the
/// generation tag); an opener's base is created only when absent, and its new generation only while the log is at the
/// generation it starts from (LOG-I1, LOG-I2). Its logs last only as long as the store.
/// </summary>
/// <remarks>
/// A fault injector sees every request before it is made and may fail it before or after it lands, or answer a landed
/// write's condition failed, as a retry which meets its own landing does. Content is copied in; a read returns pooled
/// pages its caller owns. As Azure's store does, an opener whose base exists checks it against its own content, and a
/// generation records its base's MD5, which every read checks (archive-logs.md §3, §7.3).
/// </remarks>
public sealed class MemoryArchiveLogStore : IArchiveLogStore
{
    /// <summary>The memory store's layout name in the store's record.</summary>
    public const string LayoutName = "memory";

    /// <summary>The memory store's name rule: any non-empty name.</summary>
    public const string NameRule = "any";

    /// <summary>The largest block of the default layout: Azure's 100 MiB, which a test checks against Azure's layout.</summary>
    private const int ModelBlockBytes = 100 << 20;

    /// <summary>The most blocks of the default layout: Azure's 49,900.</summary>
    private const ulong ModelMaxUnits = 49_900;

    /// <summary>The blocks at which the default layout asks for a <c>Replace</c>: Azure's 40,000.</summary>
    private const ulong ModelReplaceRequestUnits = 40_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Log> _logs = [with(StringComparer.Ordinal)];
    private readonly SortedDictionary<(string Log, ulong Generation), Base> _bases = [];
    private readonly Dictionary<(string Log, ulong Generation), int> _creations = [];
    private readonly Func<ArchiveLogRequest, ArchiveLogFault>? _faults;

    /// <summary>Initializes a new instance of the <see cref="MemoryArchiveLogStore"/> class.</summary>
    /// <param name="layout">The store's layout, of blocks; by default <see cref="DefaultLayout"/>, Azure's limits under the name <c>memory</c>.</param>
    /// <param name="faults">The fault injector, which sees every request; by default none.</param>
    /// <exception cref="ArgumentException">The layout packs chunks.</exception>
    public MemoryArchiveLogStore(ArchiveLogLayout? layout = null, Func<ArchiveLogRequest, ArchiveLogFault>? faults = null)
    {
        if (layout is { Packing: not ArchiveLogPacking.Blocks })
        {
            throw new ArgumentException("The memory store models Azure's blocks.", nameof(layout));
        }

        Layout = layout ?? DefaultLayout;
        _faults = faults;
    }

    /// <summary>Gets the memory store's default layout: Azure's limits, 100 MiB blocks, 49,900 units, and a request at 40,000.</summary>
    public static ArchiveLogLayout DefaultLayout { get; } = new(LayoutName, ArchiveLogPacking.Blocks, ModelBlockBytes, ModelMaxUnits, ModelReplaceRequestUnits, NameRule);

    /// <inheritdoc/>
    public ArchiveLogLayout Layout { get; }

    /// <summary>Gets the most bases a page of <see cref="ListGenerationsAsync"/> lists, at least 1; Azure's 5,000 by default.</summary>
    public int ListPageSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 5_000;

    /// <inheritdoc/>
    /// <remarks>Any non-empty name: the memory store keys its logs by name and forms no other.</remarks>
    public bool AdmitsName(string name) => !string.IsNullOrEmpty(name);

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        await StartAsync(cancellationToken);
        ThrowIfFails(new(ArchiveLogRequestKind.Read, name.Value, 0, 0), "read");
        Log? log;
        Base? @base = null;
        byte[][] units;
        lock (_lock)
        {
            if (!_logs.TryGetValue(name.Value, out log))
            {
                return ArchiveLogRead.Absent.Instance;
            }

            CheckKey(name, log.KeyHash);
            if (log.Opener is not null && !_bases.TryGetValue((name.Value, log.Generation), out @base))
            {
                // A newer generation replaced the base and the sweeper deleted it after this read's write-ahead read.
                return ArchiveLogRead.Stale.Instance;
            }

            units = [.. log.Units];
        }

        if (@base is not null)
        {
            CheckKey(name, @base.KeyHash);
            if (!Md5Of(@base.Content).AsSpan().SequenceEqual(log.BaseMd5))
            {
                throw new ArchiveLogCorruptException($"The archive log '{name}'s base of generation {log.Generation} does not hash to the MD5 its generation records.");
            }
        }

        var appended = units.Sum(static unit => (long)unit.Length);
        ArchiveLogRead.Read.CheckContentLength((@base?.Content.Length ?? 0) + appended);
        using var writer = new ArcBufferWriter();
        if (@base is not null)
        {
            writer.Write(@base.Content);
        }

        foreach (var unit in units)
        {
            writer.Write(unit);
        }

        using var content = writer.Length == 0 ? default : writer.ConsumeSlice(writer.Length);
        var position = new ArchiveLogPosition(log.Generation, log.Opener?.Rows ?? 0, (ulong)appended, (ulong)units.Length);
        return new ArchiveLogRead.Read(position, log.Opener, @base?.Content.Length ?? 0, content);
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        await StartAsync(cancellationToken);
        ThrowIfFails(new(ArchiveLogRequestKind.GetProperties, name.Value, 0, 0), "properties read");
        lock (_lock)
        {
            if (!_logs.TryGetValue(name.Value, out var log))
            {
                return null;
            }

            CheckKey(name, log.KeyHash);
            return new ArchiveLogProperties(log.Position, log.Opener);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(run);
        CheckRun(run);
        await StartAsync(cancellationToken);
        var start = ArchiveLogConditions.RunStart(from);
        if (from.IsOrigin)
        {
            // A 409 on the create goes to the append, whose own condition decides.
            _ = Request(new(ArchiveLogRequestKind.CreateLog, name.Value, start.Generation, 0), static log => log is null, () => Create(name, new Log(start.Generation, null, [], name.KeyHash)));
        }

        var offset = start.Length;
        foreach (var unit in run)
        {
            var at = offset;
            var bytes = unit.ToArray();
            if (!Request(new(ArchiveLogRequestKind.AppendUnit, name.Value, start.Generation, at), log => log is not null && log.Generation == start.Generation && log.Length == at, () => _logs[name.Value].Append(bytes)))
            {
                return ArchiveLogWriteOutcome.ConditionFailed.Instance;
            }

            offset += (ulong)bytes.Length;
        }

        return ArchiveLogWriteOutcome.Applied.Instance;
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        await StartAsync(cancellationToken);
        var generation = checked(from.Generation + 1);
        var bytes = content.ToArray();
        var md5 = Md5Of(bytes);

        // An existing base is the same write's, by the caller's demand of one write per token, which the store checks.
        if (!Request(new(ArchiveLogRequestKind.CreateBase, name.Value, generation, 0), _ => !_bases.ContainsKey((name.Value, generation)), () => _bases.Add((name.Value, generation), new Base(bytes, name.KeyHash))))
        {
            CheckBase(name, generation, bytes);
        }

        return Request(
            new(ArchiveLogRequestKind.OpenGeneration, name.Value, generation, 0),
            log => from.Generation == 0 ? log is null : log is not null && log.Generation == from.Generation,
            () => Create(name, new Log(generation, opener, md5, name.KeyHash)))
            ? ArchiveLogWriteOutcome.Applied.Instance
            : ArchiveLogWriteOutcome.ConditionFailed.Instance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Lists pages of <see cref="ListPageSize"/> bases; a continuation is the name <c>{log}/{g:D20}</c> of the base its page
    /// ended at, and the next page lists the names after it, whatever was created or deleted meanwhile. Each page is a
    /// request a fault may fail.
    /// </remarks>
    public async IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = continuation;
        while (true)
        {
            await StartAsync(cancellationToken);
            ThrowIfFails(new(ArchiveLogRequestKind.List, string.Empty, 0, 0), "listing");
            List<(string Name, ArchiveLogGeneration Generation)> listed;
            lock (_lock)
            {
                var after = cursor;
                listed = [.. _bases
                    .Select(static entry => (Name: BaseName(entry.Key.Log, entry.Key.Generation), Generation: new ArchiveLogGeneration(entry.Key.Log, entry.Key.Generation, entry.Value.KeyHash)))
                    .Where(entry => after is null || string.CompareOrdinal(entry.Name, after) > 0)
                    .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
                    .Take(ListPageSize + 1)];
            }

            var page = listed.Take(ListPageSize).ToList();
            cursor = listed.Count > ListPageSize ? page[^1].Name : null;
            yield return new ArchiveLogGenerationPage([.. page.Select(static entry => entry.Generation)], cursor);
            if (cursor is null)
            {
                yield break;
            }
        }
    }
    private static string BaseName(string log, ulong generation) => string.Create(CultureInfo.InvariantCulture, $"{log}/{generation:D20}");

    /// <inheritdoc/>
    public async ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(log);
        await StartAsync(cancellationToken);
        var fault = Fault(new(ArchiveLogRequestKind.DeleteBase, log, generation, 0));
        ThrowIf(fault == ArchiveLogFault.FailBefore, "deletion");
        lock (_lock)
        {
            _ = _bases.Remove((log, generation));
        }

        ThrowIf(fault == ArchiveLogFault.FailAfter, "deletion");
    }

    /// <summary>Returns how many times a request created <paramref name="generation"/> of <paramref name="log"/>; for tests.</summary>
    internal int Creations(string log, ulong generation)
    {
        lock (_lock)
        {
            return _creations.GetValueOrDefault((log, generation));
        }
    }

    /// <summary>
    /// Returns each log's position, whether an opener created its current generation, and whether that generation's base
    /// exists, as of one instant, for a simulation's step checks (LOG-I2); for tests.
    /// </summary>
    internal IReadOnlyList<(string Log, ArchiveLogPosition Position, bool Opened, bool BaseExists)> Snapshot()
    {
        lock (_lock)
        {
            return [.. _logs.Select(pair => (pair.Key, pair.Value.Position, pair.Value.Opener is not null, _bases.ContainsKey((pair.Key, pair.Value.Generation))))];
        }
    }

    /// <summary>Replaces a generation's base, as damage at rest or another writer would; for tests.</summary>
    internal void SetBase(string log, ulong generation, byte[] content)
    {
        lock (_lock)
        {
            _bases[(log, generation)] = _bases[(log, generation)] with { Content = content };
        }
    }

    /// <summary>Sets the key hash a log records, as damage to its metadata would; for tests.</summary>
    internal void SetKeyHash(string log, ulong keyHash)
    {
        lock (_lock)
        {
            _logs[log].KeyHash = keyHash;
        }
    }

    private static void CheckRun(IReadOnlyList<ReadOnlySequence<byte>> run)
    {
        if (run.Count == 0 || run.Any(static unit => unit.IsEmpty))
        {
            throw new ArgumentException("An append run holds at least one unit, each non-empty.", nameof(run));
        }
    }

    private static void CheckKey(ArchiveLogName name, ulong keyHash)
    {
        if (keyHash != name.KeyHash)
        {
            throw new ArchiveLogCorruptException($"The archive log '{name}' records the key hash {keyHash:x16}, not its key's {name.KeyHash:x16}: it names another key.");
        }
    }

    private static void ThrowIf(bool fail, string request)
    {
        if (fail)
        {
            throw new IOException($"The memory archive-log store failed a {request}, as injected.");
        }
    }

    /// <summary>Completes each call asynchronously, as a remote store's does, after checking its cancellation.</summary>
    private static async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
    }

    /// <summary>Returns the MD5 of <paramref name="bytes"/>, Azure's checksum of a base, which the memory store models.</summary>
    private static byte[] Md5Of(byte[] bytes)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(bytes);
        return md5.GetHashAndReset();
    }

    private ArchiveLogFault Fault(ArchiveLogRequest request) => _faults?.Invoke(request) ?? ArchiveLogFault.None;

    /// <summary>Lands a generation's creation, under the store's lock, and counts it.</summary>
    private void Create(ArchiveLogName name, Log log)
    {
        _logs[name.Value] = log;
        _creations[(name.Value, log.Generation)] = _creations.GetValueOrDefault((name.Value, log.Generation)) + 1;
    }

    /// <summary>Checks an existing base against an opener's own: the same write from the same position (archive-logs.md §3).</summary>
    /// <exception cref="ArchiveLogCorruptException">The stored base differs, so another write came from the same position.</exception>
    private void CheckBase(ArchiveLogName name, ulong generation, byte[] content)
    {
        Base? stored;
        lock (_lock)
        {
            stored = _bases.GetValueOrDefault((name.Value, generation));
        }

        // A base the sweeper deleted belongs to a generation the log has passed, where the opener's own condition fails.
        if (stored is not null && !stored.Content.AsSpan().SequenceEqual(content))
        {
            throw new ArchiveLogCorruptException($"The archive log '{name}' holds a base of generation {generation} of {stored.Content.Length} bytes other than the opener's {content.Length}: two writes came from one position.");
        }
    }

    /// <summary>Throws when a read's fault fails it; a read lands nothing, so it fails before or after alike.</summary>
    private void ThrowIfFails(ArchiveLogRequest request, string name) =>
        ThrowIf(Fault(request) is ArchiveLogFault.FailBefore or ArchiveLogFault.FailAfter, name);

    /// <summary>
    /// Makes one conditional write request: lands <paramref name="land"/> when <paramref name="condition"/> holds of the
    /// log, under the request's fault.
    /// </summary>
    /// <returns>Whether the request answers that it landed.</returns>
    private bool Request(ArchiveLogRequest request, Func<Log?, bool> condition, Action land)
    {
        var fault = Fault(request);
        ThrowIf(fault == ArchiveLogFault.FailBefore, request.Kind.ToString());
        bool landed;
        lock (_lock)
        {
            landed = condition(_logs.GetValueOrDefault(request.Log));
            if (landed)
            {
                land();
            }
        }

        ThrowIf(fault == ArchiveLogFault.FailAfter, request.Kind.ToString());
        return landed && fault != ArchiveLogFault.RetryMeetsLanding;
    }

    /// <summary>A log's current generation: its write-ahead units, its opener and its base's MD5, and the key hash it records.</summary>
    private sealed class Log(ulong generation, ArchiveLogOpener? opener, byte[] baseMd5, ulong keyHash)
    {
        private readonly List<byte[]> _units = [];

        public ulong Generation { get; } = generation;

        public ArchiveLogOpener? Opener { get; } = opener;

        public byte[] BaseMd5 { get; } = baseMd5;

        public ulong KeyHash { get; set; } = keyHash;

        public IReadOnlyList<byte[]> Units => _units;

        public ulong Length { get; private set; }

        public ArchiveLogPosition Position => new(Generation, Opener?.Rows ?? 0, Length, (ulong)_units.Count);

        public void Append(byte[] unit)
        {
            _units.Add(unit);
            Length += (ulong)unit.Length;
        }
    }

    /// <summary>A generation's base and the key hash it records.</summary>
    private sealed record Base(byte[] Content, ulong KeyHash);
}
