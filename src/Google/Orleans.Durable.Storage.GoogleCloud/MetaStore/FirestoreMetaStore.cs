using Google.Cloud.Firestore;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.GoogleCloud.MetaStore;

/// <summary>
/// Keeps each register in one Firestore document, whose update time is the register version.
/// </summary>
/// <remarks>
/// The register keyed <c>(cluster, name)</c> is the document <c>{cluster:N}:{name}</c> of the collection, with each
/// <c>/</c> of the name written as <c>:</c>, because <c>/</c> separates path segments and no register name contains
/// <c>:</c> (metastore.md §7). Getting a register performs no I/O: a store constructed with a build function builds
/// its database on the first read or write, and builds it again after a failed build.
/// </remarks>
public sealed class FirestoreMetaStore : IMetaStore
{
    /// <summary>
    /// The default collection.
    /// </summary>
    public const string DefaultCollection = "durableStorageRegisters";

    private readonly Func<CancellationToken, Task<FirestoreDb>> _build;
    private readonly string _collection;
    private readonly Lock _lock = new();
    private Task<FirestoreDb>? _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="FirestoreMetaStore"/> class over a built database.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="collection">The collection which holds the register documents.</param>
    public FirestoreMetaStore(FirestoreDb database, string collection = DefaultCollection)
        : this(CreateBuild(database), collection)
    {
        _database = Task.FromResult(database);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FirestoreMetaStore"/> class which builds its database on first
    /// use.
    /// </summary>
    /// <param name="build">Builds the database; it may resolve a credential.</param>
    /// <param name="collection">The collection which holds the register documents.</param>
    public FirestoreMetaStore(Func<CancellationToken, Task<FirestoreDb>> build, string collection = DefaultCollection)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (collection.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A Firestore collection name cannot contain '/'.", nameof(collection));
        }

        _build = build;
        _collection = collection;
    }

    /// <summary>
    /// Gets a value indicating whether the database was built.
    /// </summary>
    internal bool IsBuilt
    {
        get
        {
            lock (_lock)
            {
                return _database is { IsCompletedSuccessfully: true };
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<IConditionalRegister> GetRegisterAsync(
        Guid cluster,
        string name,
        CancellationToken cancellationToken = default)
    {
        var documentPath = GetDocumentPath(_collection, cluster, name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IConditionalRegister>(new FirestoreConditionalRegister(this, documentPath));
    }

    /// <summary>
    /// Forms the document identifier of the register keyed <paramref name="cluster"/> and <paramref name="name"/>.
    /// </summary>
    internal static string GetDocumentId(Guid cluster, string name) =>
        MetaStoreRegisterName.ToKey(cluster, name).Replace(MetaStoreRegisterName.Separator, ':');

    /// <summary>
    /// Forms the document path of the register keyed <paramref name="cluster"/> and <paramref name="name"/>.
    /// </summary>
    internal static string GetDocumentPath(string collection, Guid cluster, string name) =>
        string.Concat(collection, "/", GetDocumentId(cluster, name));

    /// <summary>
    /// Returns the database, building it when no build has completed or is in progress.
    /// </summary>
    internal async ValueTask<FirestoreDb> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        Task<FirestoreDb> database;
        lock (_lock)
        {
            // The build is shared by every caller, so no one caller's cancellation cancels it.
            database = _database ??= _build(CancellationToken.None);
        }

        try
        {
            return await database.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (database.IsFaulted || database.IsCanceled)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_database, database))
                {
                    _database = null;
                }
            }

            throw;
        }
    }

    private static Func<CancellationToken, Task<FirestoreDb>> CreateBuild(FirestoreDb database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return _ => Task.FromResult(database);
    }
}
