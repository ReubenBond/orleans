using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Orleans.Durable.Storage.GoogleCloud.Archive;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The live Google Cloud Storage bucket the GCS object-ledger fidelity suite runs on at milestones
/// (object-ledgers.md §9, verification.md §10, §13): its bucket name, which <see cref="Variable"/> holds, authenticated
/// with Application Default Credentials.
/// </summary>
/// <remarks>
/// Without <see cref="Variable"/> each live case skips with <see cref="SkipReason"/>, which the emulator run allows
/// verbatim while the variable is unset (test/Durable.Storage/eng/validate.ps1). Each test isolates its objects under a unique timestamped
/// prefix that is deleted on disposal, and a once-per-process sweep cleans prefixes left behind by aborted runs.
/// </remarks>
internal sealed class GcsLiveAccount : IAsyncDisposable
{
    /// <summary>
    /// The environment variable holding a live Google Cloud Storage bucket name accessible with Application Default
    /// Credentials.
    /// </summary>
    public const string Variable = "CLOG_GCS_LIVE";

    /// <summary>The reason a live case skips without <see cref="Variable"/>, which test/Durable.Storage/eng/validate.ps1 allows verbatim.</summary>
    public const string SkipReason =
        $"The live-account Cloud Storage fidelity run waits on {Variable}: set it to a GCS bucket name accessible with Application Default Credentials.";

    private const string ProviderPrefix = "clog-fidelity-gcs/";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lock CredentialLock = new();
    private static readonly LiveBucketAccount.OncePerProcessSweep StaleSweep = new();
    private static Task<GoogleCredential>? _cachedCredential;

    private GcsLiveAccount(StorageClient client, string bucketName, string prefix, TransportDouble transport)
    {
        Client = client;
        BucketName = bucketName;
        Prefix = prefix;
        Transport = transport;
        Store = new GoogleCloudStorageObjectStore(client, bucketName, prefix);
    }

    /// <summary>Gets the underlying Cloud Storage client.</summary>
    public StorageClient Client { get; }

    /// <summary>Gets the bucket name.</summary>
    public string BucketName { get; }

    /// <summary>Gets the unique prefix isolating this test run's objects.</summary>
    public string Prefix { get; }

    /// <summary>Gets the object store scoped to <see cref="Prefix"/>.</summary>
    public GoogleCloudStorageObjectStore Store { get; }

    /// <summary>Gets the HTTP transport beneath <see cref="Client"/>.</summary>
    public TransportDouble Transport { get; }

    /// <summary>
    /// Opens an isolated live Cloud Storage prefix for one test, or skips the calling case while <see cref="Variable"/>
    /// is unset.
    /// </summary>
    public static async Task<GcsLiveAccount> OpenAsync(CancellationToken cancellationToken)
    {
        var bucket = LiveBucketAccount.NormalizeBucketName(Environment.GetEnvironmentVariable(Variable), "gs://", Variable);
        if (bucket is null)
        {
            Assert.Skip(SkipReason);
        }

        var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        await StaleSweep.RunOnceAsync(
            ct => SweepStalePrefixesAsync(credential, bucket, ct),
            cancellationToken);

        var transport = new TransportDouble();
        StorageClient? client = null;
        try
        {
            client = new StorageClientBuilder
            {
                GoogleCredential = credential,
                HttpClientFactory = CloudClients.CreateGoogleTransportFactory(transport),
            }.Build();

            var account = new GcsLiveAccount(client, bucket, LiveBucketAccount.CreateRunPrefix(ProviderPrefix), transport);
            client = null;
            transport = null;
            return account;
        }
        finally
        {
            client?.Dispose();
            transport?.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Exception? cleanupFailure = null;
        int open;
        try
        {
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            var names = new List<string>();
            await foreach (var obj in Client.ListObjectsAsync(BucketName, Prefix).WithCancellation(timeout.Token))
            {
                if (obj.Name is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            await LiveBucketAccount.DeleteAllAsync(
                names,
                (name, ct) => Client.DeleteObjectAsync(BucketName, name, cancellationToken: ct),
                timeout.Token);
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
        }
        finally
        {
            open = Transport.OpenResponses;
            Client.Dispose();
            Transport.Dispose();
        }

        LiveBucketAccount.FinalizeDisposal(cleanupFailure, open);
    }

    private static Task<GoogleCredential> GetCredentialAsync(CancellationToken cancellationToken)
    {
        Task<GoogleCredential> credentialTask;
        lock (CredentialLock)
        {
            if (_cachedCredential is { IsFaulted: true } or { IsCanceled: true })
            {
                _cachedCredential = null;
            }

            _cachedCredential ??= GoogleCredential.GetApplicationDefaultAsync(CancellationToken.None);
            credentialTask = _cachedCredential;
        }

        return credentialTask.WaitAsync(cancellationToken);
    }

    private static async Task SweepStalePrefixesAsync(
        GoogleCredential credential,
        string bucket,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CleanupTimeout);
        using var transport = new TransportDouble();
        using var client = new StorageClientBuilder
        {
            GoogleCredential = credential,
            HttpClientFactory = CloudClients.CreateGoogleTransportFactory(transport),
        }.Build();

        var now = TimeProvider.System.GetUtcNow();
        var staleKeys = new List<string>();
        await foreach (var obj in client.ListObjectsAsync(bucket, ProviderPrefix).WithCancellation(timeout.Token))
        {
            if (obj.Name is not { Length: > 0 } name || !name.StartsWith(ProviderPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var slashAfterRun = name.IndexOf('/', ProviderPrefix.Length);
            if (slashAfterRun < 0)
            {
                continue;
            }

            var runPrefix = name[..(slashAfterRun + 1)];
            if (LiveBucketAccount.IsStaleRunPrefix(runPrefix, ProviderPrefix, now))
            {
                staleKeys.Add(name);
            }
        }

        try
        {
            await LiveBucketAccount.DeleteAllAsync(
                staleKeys,
                (name, ct) => client.DeleteObjectAsync(bucket, name, cancellationToken: ct),
                timeout.Token);
        }
        catch (AggregateException)
        {
            // Best-effort stale sweep; a live test run continues if a stale object cannot be removed.
        }
    }
}
