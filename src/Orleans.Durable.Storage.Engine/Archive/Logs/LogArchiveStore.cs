using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// The archive store over archive logs (archive-logs.md §2.2): each key's drained commands in its log, whose position
/// <c>(g, r, P, b)</c> is the key's token and version base (§7.2), and absence from one read of the log.
/// </summary>
/// <remarks>
/// <para>
/// A read answers the log's prefix as the store's <see cref="IArchiveLogStore.ReadAsync"/> found it: its position as its
/// token, version base, and coverage; <c>HasBase</c> when a <c>Replace</c> opened the current generation; the opener's
/// tombstone while nothing followed it; and the log's content. A log at <c>(1, 0, 0, 0)</c> with no opener, which a key's
/// first append run leaves when it crashes between its create and its first block, holds no write, and answers
/// <c>Absent</c>, so a prefix's token depends only on the writes it holds (§8.4). The pure members read only the token and the store's
/// layout, which the store's record <c>durable-storage/store</c> fixes for the cluster's life (HI-I3): <c>Plan</c> by the fold
/// <see cref="ArchiveLogPlan"/>, <c>Holds</c> by <see cref="ArchiveLogConditions.Holds(ArchiveLogPosition, ArchiveWrite, int)"/>,
/// the same function the writer's classifier evaluates.
/// </para>
/// <para>
/// The store's record lies in the object store of durable storage's objects under <c>durable-storage/</c>, which on Azure is the logs' own
/// container (archive-logs.md §3, §7.5).
/// </para>
/// </remarks>
public sealed class LogArchiveStore : IArchiveStore
{
    private static readonly ObjectName RecordName = new(ArchiveLogNames.StoreRecord);

    private readonly IArchiveLogStore _logs;
    private readonly IObjectStore _objects;
    private readonly ArchiveLogNames _names;
    private readonly ArchiveLogLayout _layout;

    /// <summary>Initializes a new instance of the <see cref="LogArchiveStore"/> class.</summary>
    /// <remarks>
    /// The construction contract: <paramref name="logs"/> and <paramref name="objects"/> lie in one container, the
    /// cluster's (archive-logs.md §7.5), which the composition's store factory meets by building both over one container
    /// client; the archive gate binds the two only as far as this contract holds (orleans.md §5.13).
    /// </remarks>
    /// <param name="logs">The archive-log store.</param>
    /// <param name="objects">The object store of durable storage's objects under <c>durable-storage/</c>, in the same container: the store's record.</param>
    /// <param name="names">The composition's names of the keys' logs.</param>
    public LogArchiveStore(IArchiveLogStore logs, IObjectStore objects, ArchiveLogNames names)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(names);
        _logs = logs;
        _objects = objects;
        _names = names;
        _layout = logs.Layout;
    }

    /// <inheritdoc/>
    public ArchiveStoreKind Kind => ArchiveStoreKind.Logs;

    /// <summary>Gets the archive-log store of the keys' logs.</summary>
    internal IArchiveLogStore Logs => _logs;

    /// <summary>Gets the object store of durable storage's objects under <c>durable-storage/</c>, in the logs' own container.</summary>
    internal IObjectStore Objects => _objects;

    /// <summary>Gets the names of the keys' logs, whose scheme the store's record pins.</summary>
    internal ArchiveLogNames Names => _names;

    /// <inheritdoc/>
    public int? MaxAppendBytes => _layout.MaxAppendBytes;

    /// <summary>Gets a store field the record check ignores, a mutant for tests; <see langword="null"/> as specified.</summary>
    internal string? IgnoredRecordField { get; init; }

    /// <summary>Gets the mutant of <c>Holds</c> the store answers with, for tests; <see cref="ArchiveLogConditions.HoldsMutant.None"/> as specified.</summary>
    internal ArchiveLogConditions.HoldsMutant HoldsMutant { get; init; }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>Absent</c> without I/O for a key <see cref="AdmitsKey"/> refuses, which has no rows; otherwise from one read of
    /// the key's log, which checks its key hash (archive-logs.md §8.4).
    /// </remarks>
    /// <exception cref="ArchiveCorruptException">The log breaks its format, names another key, or is at a position no token encodes.</exception>
    public async ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (_names.TryOf(key.Span) is not { } name || !_logs.AdmitsName(name.Value))
        {
            return ArchiveStoreRead.Absent.Instance;
        }

        ArchiveLogRead found;
        try
        {
            found = await _logs.ReadAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveLogCorruptException exception)
        {
            throw new ArchiveCorruptException(exception.Message, exception);
        }

        switch (found.Value)
        {
            case ArchiveLogRead.Absent:
                return ArchiveStoreRead.Absent.Instance;
            case ArchiveLogRead.Stale:
                return ArchiveStoreRead.Stale.Instance;
            case ArchiveLogRead.Read read:
                using (read)
                {
                    return HoldsNoWrite(read) ? ArchiveStoreRead.Absent.Instance : ToRead(name, read);
                }

            case null:
            default:
                throw new UnreachableException();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The record is <c>durable-storage/store</c>, with the store's layout and its fields <c>max-append-bytes</c>, <c>max-units</c>,
    /// <c>replace-request-units</c>, <c>name-rule</c>, and <c>name-scheme</c> (archive-logs.md §7.5).
    /// </remarks>
    public async ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        ArchiveStoreRecord.CheckParameters(parameters);
        if (await ReadRecordAsync(cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            var created = ArchiveStoreRecord.Create(ArchiveStoreId.NewRandom(), _layout.Name, Fields(), parameters);
            using var content = ArcBuffer.Wrap(created.Encode());
            switch (await _objects.CreateAsync(RecordName, content, null, cancellationToken).ConfigureAwait(false))
            {
                case ObjectCreateOutcome.Created:
                    return created;
                case ObjectCreateOutcome.Exists:
                    stored = await ReadRecordAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new ArchiveCorruptException($"{RecordName} exists but reads as absent.");
                    break;
                case null:
                default:
                    throw new UnreachableException();
            }
        }

        var fields = Fields();
        if (IgnoredRecordField is { } ignored && stored.StoreFields.TryGetValue(ignored, out var recorded))
        {
            // The mutant takes the record's value of the field it ignores.
            fields[ignored] = recorded;
        }

        stored.CheckStoreFieldsMatch(_layout.Name, fields);
        return stored;
    }

    /// <inheritdoc/>
    /// <exception cref="ArchiveCorruptException"><paramref name="token"/> is not one archive logs mint.</exception>
    public ArchivePlan Begin(RowToken token) => new ArchiveLogPlan(_layout, ArchiveLogTokens.PositionOf(token));

    /// <inheritdoc/>
    /// <remarks>The function the writer's classifier evaluates, over the prefix's position (archive-logs.md §8.3).</remarks>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is not one of this store's.</exception>
    public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return prefix.Coverage is ArchiveLogPosition position
            ? ArchiveLogConditions.Holds(position, write, count, HoldsMutant)
            : throw new ArgumentException("The prefix's coverage is not an archive log's position.", nameof(prefix));
    }

    /// <inheritdoc/>
    /// <remarks>Whether the token's units reach the layout's <see cref="ArchiveLogLayout.ReplaceRequestUnits"/>.</remarks>
    public bool IsReplaceRequested(RowToken token) => ArchiveLogTokens.PositionOf(token).Units >= _layout.ReplaceRequestUnits;

    /// <inheritdoc/>
    /// <remarks>
    /// Whether the composition's mapping names the key's log, and the store can form that name under its layout's
    /// <see cref="ArchiveLogLayout.NameRule"/>, as one name no other aliases (archive-logs.md §7.1); the rule's version and
    /// the mapping's scheme are the record's.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a key.</exception>
    public bool AdmitsKey(ReadOnlySpan<byte> key) => _names.TryOf(key) is { } name && _logs.AdmitsName(name.Value);

    /// <summary>
    /// Returns whether a read holds no write: generation 1 at length 0 with no opener, the log a key's first append run
    /// created and then wrote nothing to.
    /// </summary>
    internal static bool HoldsNoWrite(ArchiveLogRead.Read read) =>
        read.Position == new ArchiveLogPosition(1, 0, 0, 0) && read.Opener is null;

    /// <summary>Returns the prefix of a log's read: its position as its token, version base, and coverage.</summary>
    /// <exception cref="ArchiveCorruptException">The position encodes as no token.</exception>
    private static ArchiveStoreRead ToRead(ArchiveLogName name, ArchiveLogRead.Read read)
    {
        var token = TokenOf(name, read.Position);
        ReadOnlyMemory<byte>? tombstone = read.Tombstone is { } tomb ? (ReadOnlyMemory<byte>?)ArchiveLogOpeners.BytesOf(tomb) : null;
        return new ArchiveStoreRead.Read(ArchivePrefix.Of(token, token, read.HasBase, tombstone, read.Content, read.Position));
    }

    /// <summary>Returns the token of a log's position, which a store answered.</summary>
    /// <exception cref="ArchiveCorruptException">The position encodes as no token.</exception>
    private static RowToken TokenOf(ArchiveLogName name, ArchiveLogPosition position) =>
        ArchiveLogTokens.Violation(position) is { } violation
            ? throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"The archive log '{name}' is at {position}, which no token encodes: {violation}."))
            : ArchiveLogTokens.Encode(position);

    private Dictionary<string, string> Fields() => new(StringComparer.Ordinal)
    {
        ["max-append-bytes"] = Unbounded(_layout.MaxAppendBytes),
        ["max-units"] = Unbounded(_layout.MaxUnits),
        ["replace-request-units"] = _layout.ReplaceRequestUnits.ToString(CultureInfo.InvariantCulture),
        ["name-rule"] = _layout.NameRule,
        ["name-scheme"] = _names.Scheme,
    };

    private static string Unbounded<T>(T? value)
        where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture) ?? "unbounded";

    /// <summary>Reads the store's record, or <see langword="null"/> when it is absent.</summary>
    /// <exception cref="ArchiveCorruptException">The record breaks its format.</exception>
    internal async ValueTask<ArchiveStoreRecord?> ReadRecordAsync(CancellationToken cancellationToken)
    {
        if (await _objects.ReadVersionedAsync(RecordName, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        using var content = found.Content;
        return ArchiveStoreRecord.Decode(content.ToArray()) switch
        {
            ArchiveStoreRecord record => record,
            InvalidDataException rejection => throw new ArchiveCorruptException($"{RecordName} breaks the format of a store's record.", rejection),
            null => throw new UnreachableException(),
        };
    }
}
