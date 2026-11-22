using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// Deletes the run objects no commit can reference (archive §2.7, §8.5): it lists the drain and compaction outputs, then
/// reads the manifest, and deletes each listed object which that manifest does not reference and whose reference
/// condition is false in it (LSM-I6), once an earlier sweep of this sweeper found the same.
/// </summary>
/// <remarks>
/// <para>
/// Listing first makes the manifest read later than every listed object's upload, so it holds every reference a commit
/// made to such an object before the read, and a reference condition which is false in it stays false: conditions only
/// change from true to false, and an object never re-enters the manifest (LSM-I7). A drain output's condition is its
/// name's, <c>d/{S}/{a}</c>; a compaction output's is its trailer's inputs, which the sweeper reads once, from the length
/// its listing gave, and keeps while the object is listed, since objects never change. One predicate,
/// <see cref="ManifestCommitRules.ConditionIsFalse"/>, decides a condition here and for the committer.
/// </para>
/// <para>
/// The grace: a sweep only marks an object it finds deletable, and deletes one which an earlier sweep marked and which it
/// finds deletable again, so an object outlives by one sweep the first sweep which could have deleted it. The grace costs
/// one sweep interval of garbage and spares readers whose manifest view advances within that interval a view which names
/// the objects a sweep deletes; safety rests on LSM-I6 alone. Marks live in this instance's memory, so the first sweep
/// after a restart deletes nothing, and the host paces sweeps at least one interval apart, which makes the grace one.
/// </para>
/// <para>
/// An unreferenced object whose condition cannot be read, a compaction output whose trailer breaks a rule of the format or
/// disagrees with its name, or a name under <c>d/</c> which names no drain, is unreadable: the sweep leaves it in place,
/// logs it, counts it in its result and in the gauge <c>orleans.durable.storage.archive.lsm.sweep.unreadable_objects</c>, and reads it again
/// on the next sweep. The sweep reads trailers and deletes objects up to <see cref="Concurrency"/> at a time, each
/// decision resting on the one listing and manifest read.
/// </para>
/// </remarks>
public sealed partial class Sweeper
{
    /// <summary>The most trailer reads, and the most deletes, a sweep has in flight at once.</summary>
    public const int Concurrency = 16;

    private static readonly string[] Prefixes = [RunObjectNames.DrainPrefix, RunObjectNames.CompactionPrefix];

    private readonly IObjectStore _store;
    private readonly ManifestView _manifest;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly HashSet<ObjectName> _marked = [];
    private readonly Dictionary<ObjectName, RunObjectCondition.Compaction> _trailerReads = [];
    private int _sweeping;

    /// <summary>
    /// Initializes a new instance of the <see cref="Sweeper"/> class, which holds no marks.
    /// </summary>
    /// <param name="store">The object store whose run objects it sweeps.</param>
    /// <param name="manifest">The process's manifest view, through which each sweep reads the manifest.</param>
    /// <param name="timeProvider">Times each sweep for its duration metric.</param>
    /// <param name="logger">Logs each unreadable object.</param>
    public Sweeper(IObjectStore store, ManifestView manifest, TimeProvider timeProvider, ILogger<Sweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _manifest = manifest;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The verdict a sweep reaches on one listed object, from its listing and manifest read and this sweeper's marks.
    /// </summary>
    private enum Verdict
    {
        Referenced,
        Pending,
        Mark,
        Delete,
        Unreadable,
        Gone,
    }

    /// <summary>
    /// Gets or sets a value indicating whether the sweeper marks every unreferenced drain output, whatever its condition:
    /// the mutant which deletes a drain output a commit can still reference, as the model's <c>sweep-drain</c> does.
    /// </summary>
    internal bool SweepDrainMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sweeper marks every unreferenced compaction output, whatever its
    /// condition: the mutant which deletes a compaction output a commit can still reference, as the model's
    /// <c>sweep-compaction</c> does.
    /// </summary>
    internal bool SweepCompactionMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sweeper reads the manifest before it lists: the mutant which misreads an
    /// object uploaded and committed between the two, as the model's <c>snapshot-before-list</c> does.
    /// </summary>
    internal bool SnapshotBeforeListMutant { get; set; }

    /// <summary>
    /// Runs one sweep: lists the drain and compaction outputs, then reads the manifest, then deletes each listed object
    /// the manifest does not reference and whose reference condition is false in it, which an earlier sweep found so too,
    /// and marks those no earlier sweep found so.
    /// </summary>
    /// <param name="cancellationToken">Stops the sweep; a delete already sent may still land.</param>
    /// <returns>What the sweep found and did.</returns>
    /// <exception cref="ArchiveCorruptException">The manifest breaks its format.</exception>
    /// <exception cref="InvalidOperationException">Another sweep of this sweeper is running.</exception>
    /// <remarks>
    /// An exception from the object store ends the sweep; the marks it made stand, and an object whose delete failed
    /// stays marked.
    /// </remarks>
    public async Task<SweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) != 0)
        {
            throw new InvalidOperationException("A sweeper runs one sweep at a time.");
        }

        try
        {
            var started = _timeProvider.GetTimestamp();
            var result = await SweepOnceAsync(cancellationToken).ConfigureAwait(false);
            SweepMetrics.Record(result, _timeProvider.GetElapsedTime(started));
            return result;
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private async Task<SweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        // LSM-I6: the listing comes first, so the manifest read after it holds every reference to a listed object.
        ManifestValue manifest;
        Dictionary<ObjectName, long> present;
        if (SnapshotBeforeListMutant)
        {
            manifest = (await _manifest.ReadAsync(cancellationToken).ConfigureAwait(false)).Value;
            present = await ListRunObjectsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            present = await ListRunObjectsAsync(cancellationToken).ConfigureAwait(false);
            manifest = (await _manifest.ReadAsync(cancellationToken).ConfigureAwait(false)).Value;
        }

        var manifestObjects = manifest.Sections.Select(static section => section.Object).ToHashSet();
        var conditions = await ReadConditionsAsync(present, manifestObjects, cancellationToken).ConfigureAwait(false);
        int referenced = 0, pending = 0, marked = 0, unreadable = 0;
        var deletable = new List<ObjectName>();
        foreach (var name in present.Keys.Order().ToArray())
        {
            switch (Classify(name, manifestObjects, manifest, conditions))
            {
                case Verdict.Referenced:
                    // A marked object's condition was false in an older manifest, so no commit has referenced it since.
                    Debug.Assert(SweepDrainMutant || SweepCompactionMutant || !_marked.Contains(name), $"A sweep marked {name}, which the manifest references.");
                    _ = _marked.Remove(name);
                    referenced++;
                    break;
                case Verdict.Pending:
                    Debug.Assert(!_marked.Contains(name), $"A sweep marked {name}, whose reference condition holds.");
                    pending++;
                    break;
                case Verdict.Mark:
                    _ = _marked.Add(name);
                    marked++;
                    break;
                case Verdict.Delete:
                    deletable.Add(name);
                    break;
                case Verdict.Unreadable:
                    LogUnreadable(_logger, name, (ArchiveCorruptException)conditions[name].Value!);
                    unreadable++;
                    break;
                case Verdict.Gone:
                    // Another sweeper deleted the object after the listing.
                    _ = present.Remove(name);
                    break;
                default:
                    throw new UnreachableException("A verdict is one of its cases.");
            }
        }

        var deleted = await DeleteAsync(deletable, present, cancellationToken).ConfigureAwait(false);

        // Objects which are no longer listed are gone, and their names are never used again (LSM-I7).
        _ = _marked.RemoveWhere(name => !present.ContainsKey(name));
        foreach (var name in _trailerReads.Keys)
        {
            if (!present.ContainsKey(name))
            {
                _ = _trailerReads.Remove(name);
            }
        }

        return new SweepResult(referenced, pending, marked, deleted, unreadable);
    }

    /// <summary>
    /// Lists the drain and compaction outputs, each distinct name once with the length its listing gave.
    /// </summary>
    private async Task<Dictionary<ObjectName, long>> ListRunObjectsAsync(CancellationToken cancellationToken)
    {
        var present = new Dictionary<ObjectName, long>();
        foreach (var prefix in Prefixes)
        {
            await foreach (var entry in _store.ListAsync(prefix, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                _ = present.TryAdd(entry.Name, entry.Length);
            }
        }

        return present;
    }

    /// <summary>
    /// Returns the reference condition of each listed object the manifest does not reference: a drain output's from its
    /// name, a compaction output's from its trailer, kept from an earlier sweep or read now, up to
    /// <see cref="Concurrency"/> at a time; the corruption which keeps it unreadable; or <see cref="ConditionRead.Gone"/>
    /// when the object is absent.
    /// </summary>
    private async Task<Dictionary<ObjectName, ConditionRead>> ReadConditionsAsync(
        Dictionary<ObjectName, long> present,
        HashSet<ObjectName> manifestObjects,
        CancellationToken cancellationToken)
    {
        var conditions = new Dictionary<ObjectName, ConditionRead>();
        var reads = new List<ListedObject>();
        foreach (var (name, length) in present)
        {
            if (manifestObjects.Contains(name))
            {
                continue;
            }

            if (name.Value.StartsWith(RunObjectNames.DrainPrefix, StringComparison.Ordinal))
            {
                conditions[name] = RunObjectNames.DrainConditionOf(name) is { } drain
                    ? drain
                    : new ArchiveCorruptException(
                        string.Create(CultureInfo.InvariantCulture, $"{name} names no drain output's ledger and watermark."),
                        DecodeRejection.Create(RunObjectReader.NameMismatch, "A drain output's name is d/{S}/{a}/{id}."));
            }
            else if (_trailerReads.TryGetValue(name, out var known))
            {
                conditions[name] = known;
            }
            else
            {
                Debug.Assert(name.Value.StartsWith(RunObjectNames.CompactionPrefix, StringComparison.Ordinal), "A sweep lists only d/ and c/.");
                reads.Add(new ListedObject(name, length));
            }
        }

        var read = new ConcurrentDictionary<ObjectName, ConditionRead>();
        await Parallel.ForEachAsync(
            reads,
            InParallel(cancellationToken),
            async (listed, token) => read[listed.Name] = await ReadTrailerConditionAsync(listed, token).ConfigureAwait(false)).ConfigureAwait(false);
        foreach (var (name, condition) in read)
        {
            conditions[name] = condition;
            if (condition.Value is RunObjectCondition.Compaction compaction)
            {
                _trailerReads[name] = compaction;
            }
        }

        return conditions;
    }

    /// <summary>
    /// Reads a compaction output's condition from its trailer. A corrupt trailer is not kept: the next sweep reads it
    /// again, so a fault of one read never makes an object unreadable for good.
    /// </summary>
    private async Task<ConditionRead> ReadTrailerConditionAsync(ListedObject listed, CancellationToken cancellationToken)
    {
        RunTrailer? trailer;
        try
        {
            trailer = await RunObjectReader.ReadTrailerAsync(_store, listed, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveCorruptException exception)
        {
            return exception;
        }

        // A trailer which agrees with its name under c/ holds a compaction's condition.
        return trailer is null
            ? ConditionRead.Gone.Instance
            : trailer.Condition.Value as RunObjectCondition.Compaction
                ?? throw new UnreachableException("A compaction output's trailer which agrees with its name holds a compaction's condition.");
    }

    /// <summary>
    /// Decides one listed object, reading the sweeper's marks and changing nothing.
    /// </summary>
    private Verdict Classify(ObjectName name, HashSet<ObjectName> manifestObjects, ManifestValue manifest, Dictionary<ObjectName, ConditionRead> conditions)
    {
        if (manifestObjects.Contains(name))
        {
            return Verdict.Referenced;
        }

        return conditions[name] switch
        {
            ArchiveCorruptException => Verdict.Unreadable,
            ConditionRead.Gone => Verdict.Gone,
            RunObjectCondition.Drain drain => Deletable(drain, SweepDrainMutant),
            RunObjectCondition.Compaction compaction => Deletable(compaction, SweepCompactionMutant),
            null => throw new UnreachableException("A condition read holds its case."),
        };

        Verdict Deletable(RunObjectCondition condition, bool ignoreCondition)
        {
            if (!ignoreCondition && !ManifestCommitRules.ConditionIsFalse(manifest, condition))
            {
                return Verdict.Pending;
            }

            return _marked.Contains(name) ? Verdict.Delete : Verdict.Mark;
        }
    }

    /// <summary>
    /// Deletes <paramref name="names"/>, up to <see cref="Concurrency"/> at a time, dropping each one's mark and listing
    /// once its delete completes; one whose delete fails stays marked.
    /// </summary>
    /// <returns>The number deleted.</returns>
    private async Task<int> DeleteAsync(List<ObjectName> names, Dictionary<ObjectName, long> present, CancellationToken cancellationToken)
    {
        var deleted = new ConcurrentQueue<ObjectName>();
        try
        {
            await Parallel.ForEachAsync(
                names,
                InParallel(cancellationToken),
                async (name, token) =>
                {
                    await _store.DeleteAsync(name, token).ConfigureAwait(false);
                    deleted.Enqueue(name);
                }).ConfigureAwait(false);
        }
        finally
        {
            foreach (var name in deleted)
            {
                _ = _marked.Remove(name);
                _ = present.Remove(name);
            }
        }

        return deleted.Count;
    }

    /// <summary>
    /// Returns the options of a sweep's parallel trailer reads and deletes: up to <see cref="Concurrency"/> at a time,
    /// under the sweep's token. A failure stops the loop from starting more and cancels the calls in flight.
    /// </summary>
    private static ParallelOptions InParallel(CancellationToken cancellationToken) =>
        new() { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancellationToken };

    [LoggerMessage(Level = LogLevel.Error, Message = "The sweep left the unreadable run object {Name} in place.")]
    private static partial void LogUnreadable(ILogger logger, ObjectName name, Exception exception);

    /// <summary>
    /// What a sweep reads of an unreferenced object's reference condition: the condition, the corruption which keeps it
    /// unreadable, or <see cref="Gone"/>.
    /// </summary>
    private readonly union ConditionRead(RunObjectCondition.Drain, RunObjectCondition.Compaction, ArchiveCorruptException, ConditionRead.Gone)
    {
        /// <summary>The object was absent when the sweep read its trailer.</summary>
        public sealed record Gone
        {
            private Gone()
            {
            }

            public static Gone Instance { get; } = new();
        }
    }
}
