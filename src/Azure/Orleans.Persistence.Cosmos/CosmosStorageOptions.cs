using Orleans.Core;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Options for Azure Cosmos DB grain persistence.
/// </summary>
public class CosmosGrainStorageOptions : CosmosOptions
{
    private const string ORLEANS_STORAGE_CONTAINER = "OrleansStorage";
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// Stage of silo lifecycle where storage should be initialized. Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

    /// <summary>
    /// Gets or sets a value indicating whether state should be deleted when <see cref="IStorage.ClearStateAsync"/> is called.
    /// </summary>
    public bool DeleteStateOnClear { get; set; }

    /// <summary>
    /// List of JSON path strings.
    /// Each entry on this list represents a property in the State Object that will be included in the document index.
    /// The default is to not add any property in the State object.
    /// </summary>
    public List<string> StateFieldsToIndex { get; set; } = new();

    /// <summary>
    /// Gets or sets the retry filter used to determine whether a request should be retried.
    /// If the return value is <see langword="null"/>, the call will not be reattempted.
    /// If the return value is <see cref="TimeSpan.Zero"/>, the call will be reattempted immediately.
    /// Otherwise, the call will be reattempted after the returned period elapses.
    /// </summary>
    public Func<int, Exception, TimeSpan?>? RetryFilter { get; set; }

    /// <summary>
    /// Initializes a new <see cref="CosmosGrainStorageOptions"/> instance.
    /// </summary>
    public CosmosGrainStorageOptions()
    {
        ContainerName = ORLEANS_STORAGE_CONTAINER;
    }
}
