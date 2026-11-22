using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A process's cache of verified run object tails, each an object's section metadata (archive §7, §8.7), which its
/// archive store and its compaction worker share: each object's tail is fetched at most once while it is cached,
/// concurrent first reads share one fetch, and a tail found corrupt stays corrupt, since objects never change.
/// </summary>
/// <remarks>
/// <para>
/// A fetch takes no caller's cancellation, since callers share it; each caller stops waiting on its own token. A fetch
/// which fails or finds the object absent leaves the cache.
/// </para>
/// <para>
/// The cache keeps every entry whose section's partition overlaps a preloaded range (<see cref="SetPreloaded"/>), which
/// the archive store loads ahead of reads. Every other entry, which a read loaded on demand, joins the cache only while
/// such entries hold at most <see cref="ArchiveOptions.TailCacheBytes"/>, and otherwise serves the callers which shared
/// its fetch and leaves. <see cref="Retain"/>, which the archive store runs as its view advances and as its preloaded
/// ranges change, removes the entries of objects the view no longer lists and the on-demand entries no read used since
/// the pass before.
/// </para>
/// </remarks>
public sealed class TailCache
{
    private readonly IObjectStore _store;
    private readonly long _capacityBytes;
    private readonly ConcurrentDictionary<ObjectName, TailFetch> _tails = new();
    private readonly Lock _sync = new();
    private ImmutableArray<KeyRange> _preloaded = [];
    private long _cachedBytes;
    private long _onDemandBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="TailCache"/> class.
    /// </summary>
    /// <param name="store">The object store holding the run objects.</param>
    /// <param name="options">The bound of the on-demand entries, <see cref="ArchiveOptions.TailCacheBytes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid.</exception>
    public TailCache(IObjectStore store, ArchiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options = options with { };
        options.Validate();
        _store = store;
        _capacityBytes = options.TailCacheBytes;
    }

    /// <summary>Gets or sets the mutant the cache and its archive store run as; for the tests which must catch each.</summary>
    internal MetadataCacheMutant Mutant { get; set; }

    /// <summary>Gets the bytes of the tails the cache holds; for tests.</summary>
    internal long CachedBytes
    {
        get
        {
            lock (_sync)
            {
                return _cachedBytes;
            }
        }
    }

    /// <summary>Gets the objects whose tails the cache holds or is fetching; for tests.</summary>
    internal IReadOnlyCollection<ObjectName> Cached => [.. _tails.Keys];

    /// <summary>Gets the preloaded ranges.</summary>
    internal ImmutableArray<KeyRange> Preloaded
    {
        get
        {
            lock (_sync)
            {
                return _preloaded;
            }
        }
    }

    /// <summary>
    /// Returns the verified tail of <paramref name="section"/>'s object without allocating when it is already cached and
    /// completed cleanly; returns <see langword="false"/> when absent, still fetching, or faulted (so faulted corrupt tails
    /// flow through <see cref="GetAsync"/> and rethrow their <see cref="ArchiveCorruptException"/>).
    /// </summary>
    internal bool TryGet(SectionRef section, [NotNullWhen(true)] out RunTail? tail, bool read = true)
    {
        if (_tails.TryGetValue(section.Object, out var cached)
            && cached.Source.Task.IsCompletedSuccessfully
            && cached.Source.Task.Result is { } completed)
        {
            if (read)
            {
                cached.Read = true;
            }

            tail = completed;
            return true;
        }

        tail = null;
        return false;
    }

    /// <summary>
    /// Returns the verified tail of <paramref name="section"/>'s object, fetching it once for every concurrent caller, or
    /// <see langword="null"/> when the object is absent; the entry counts as read when <paramref name="read"/>, so a load
    /// ahead of reads counts as none.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The tail breaks a rule or contradicts the reference.</exception>
    internal Task<RunTail?> GetAsync(SectionRef section, CancellationToken cancellationToken, bool read = true)
    {
        while (true)
        {
            if (_tails.TryGetValue(section.Object, out var cached))
            {
                if (read)
                {
                    cached.Read = true;
                }

                return cached.Source.Task.WaitAsync(cancellationToken);
            }

            var fetch = new TailFetch(section.Partition) { Read = read, Preloaded = IsPreloaded(section.Partition) };
            if (_tails.TryAdd(section.Object, fetch))
            {
                _ = FetchAsync(section, fetch);
                return fetch.Source.Task.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>Returns whether <paramref name="partition"/> overlaps a preloaded range.</summary>
    internal bool IsPreloaded(KeyRange partition)
    {
        lock (_sync)
        {
            return Overlaps(_preloaded, partition);
        }
    }

    /// <summary>
    /// Makes <paramref name="ranges"/> the preloaded ranges, replacing the previous set: an entry whose partition overlaps
    /// none of them is an on-demand entry from now on, and leaves at a later <see cref="Retain"/> unless a read uses it.
    /// </summary>
    internal void SetPreloaded(ImmutableArray<KeyRange> ranges)
    {
        lock (_sync)
        {
            _preloaded = ranges;
            foreach (var fetch in _tails.Values)
            {
                Classify(fetch);
            }
        }
    }

    /// <summary>
    /// Removes the entries of every object <paramref name="manifest"/> does not list, and every on-demand entry which has
    /// served no read since the previous pass, then starts the next pass's count of reads.
    /// </summary>
    internal void Retain(ManifestValue manifest)
    {
        var listed = Mutant == MetadataCacheMutant.KeepsUnlisted ? null : manifest.Sections.Select(static section => section.Object).ToHashSet();
        lock (_sync)
        {
            foreach (var (name, fetch) in _tails)
            {
                var unlisted = listed is not null && !listed.Contains(name);
                var unread = fetch.Source.Task.IsCompleted && !fetch.Read && (!fetch.Preloaded || Mutant == MetadataCacheMutant.EvictsPreloaded) && Mutant != MetadataCacheMutant.KeepsUnread;
                if ((unlisted || unread) && _tails.TryRemove(KeyValuePair.Create(name, fetch)))
                {
                    Uncount(fetch);
                }

                fetch.Read = false;
            }
        }
    }

    private static bool Overlaps(ImmutableArray<KeyRange> ranges, KeyRange partition)
    {
        foreach (var range in ranges)
        {
            if (range.Overlaps(partition))
            {
                return true;
            }
        }

        return false;
    }

    private static long TailBytes(RunTail tail) => tail.Trailer.ObjectLength - tail.Trailer.MetadataOffset;

    /// <summary>
    /// Reads and verifies the tail of <paramref name="section"/>'s object with no caller's token. A tail which is corrupt
    /// stays cached; one which is absent or failed leaves the cache, and so does an on-demand one past the cache's bound,
    /// once it has served the callers which shared it.
    /// </summary>
    private async Task FetchAsync(SectionRef section, TailFetch fetch)
    {
        try
        {
            var tail = await RunObjectReader.ReadTailAsync(_store, section, CancellationToken.None).ConfigureAwait(false);
            Keep(section.Object, fetch, tail);
            _ = fetch.Source.TrySetResult(tail);
        }
        catch (ArchiveCorruptException exception)
        {
            _ = fetch.Source.TrySetException(exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = _tails.TryRemove(KeyValuePair.Create(section.Object, fetch));
            _ = fetch.Source.TrySetException(exception);
        }
    }

    /// <summary>
    /// Counts a fetched tail's bytes while its entry is still cached and either its partition overlaps a preloaded range
    /// or it fits the on-demand bound; otherwise the entry leaves the cache, as one for an absent object does.
    /// </summary>
    private void Keep(ObjectName name, TailFetch fetch, RunTail? tail)
    {
        lock (_sync)
        {
            if (tail is not null && _tails.TryGetValue(name, out var cached) && ReferenceEquals(cached, fetch))
            {
                var bytes = TailBytes(tail);
                var preloaded = Overlaps(_preloaded, fetch.Partition);
                if (preloaded || _onDemandBytes + bytes <= _capacityBytes)
                {
                    fetch.Bytes = bytes;
                    fetch.Preloaded = preloaded;
                    _cachedBytes += bytes;
                    _onDemandBytes += preloaded ? 0 : bytes;
                    ArchiveMetrics.RecordCachedTailBytes(bytes);
                    return;
                }
            }

            _ = _tails.TryRemove(KeyValuePair.Create(name, fetch));
        }
    }

    /// <summary>Moves a counted entry between the preloaded and on-demand bytes as the preloaded ranges change.</summary>
    private void Classify(TailFetch fetch)
    {
        var preloaded = Overlaps(_preloaded, fetch.Partition);
        if (preloaded != fetch.Preloaded)
        {
            _onDemandBytes += preloaded ? -fetch.Bytes : fetch.Bytes;
            fetch.Preloaded = preloaded;
        }
    }

    private void Uncount(TailFetch fetch)
    {
        _cachedBytes -= fetch.Bytes;
        _onDemandBytes -= fetch.Preloaded ? 0 : fetch.Bytes;
        ArchiveMetrics.RecordCachedTailBytes(-fetch.Bytes);
        fetch.Bytes = 0;
    }

    /// <summary>
    /// A tail's one shared fetch: its section's partition, the bytes it counts in the cache while it is cached, whether
    /// its partition overlaps a preloaded range, and whether a read used it since the last pass.
    /// </summary>
    private sealed class TailFetch(KeyRange partition)
    {
        private volatile bool _read;

        public TaskCompletionSource<RunTail?> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public KeyRange Partition { get; } = partition;

        public long Bytes { get; set; }

        public bool Preloaded { get; set; }

        public bool Read
        {
            get => _read;
            set => _read = value;
        }
    }
}

/// <summary>
/// The section metadata cache's mutants, each breaking one rule of archive-lsm.md §8.3 and §8.7, which the tests must
/// catch.
/// </summary>
internal enum MetadataCacheMutant
{
    /// <summary>The rules as specified.</summary>
    None,

    /// <summary>The store's view advances before the new sections' metadata loads.</summary>
    InstallsBeforeLoads,

    /// <summary>A view advance loads no section's metadata.</summary>
    LoadsNoNewSections,

    /// <summary>An unread entry leaves the cache though its partition overlaps a preloaded range.</summary>
    EvictsPreloaded,

    /// <summary>An unread on-demand entry never leaves the cache.</summary>
    KeepsUnread,

    /// <summary>An entry stays though the view no longer lists its object.</summary>
    KeepsUnlisted,
}
