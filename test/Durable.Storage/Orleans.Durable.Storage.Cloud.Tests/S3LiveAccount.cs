using Amazon.S3;
using Amazon.S3.Model;
using Orleans.Durable.Storage.S3.Archive;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The live general-purpose Amazon S3 bucket the S3 object-ledger fidelity suite runs on at milestones
/// (object-ledgers.md §9, verification.md §10, §13): its bucket name, which <see cref="Variable"/> holds, authenticated
/// with the AWS SDK's default credential chain and region.
/// </summary>
/// <remarks>
/// Without <see cref="Variable"/> each live case skips with <see cref="SkipReason"/>, which the emulator run allows
/// verbatim while the variable is unset (eng/validate.ps1). Each test isolates its objects under a unique timestamped
/// prefix that is deleted on disposal, and a once-per-process sweep cleans prefixes left behind by aborted runs.
/// </remarks>
internal sealed class S3LiveAccount : IAsyncDisposable
{
    /// <summary>
    /// The environment variable holding a live general-purpose S3 bucket name accessible with the AWS SDK's default
    /// credentials and region.
    /// </summary>
    public const string Variable = "CLOG_S3_LIVE";

    /// <summary>The reason a live case skips without <see cref="Variable"/>, which eng/validate.ps1 allows verbatim.</summary>
    public const string SkipReason =
        $"The live-account S3 fidelity run waits on {Variable}: set it to a general-purpose S3 bucket name accessible with the AWS SDK's default credentials and region.";

    private const string ProviderPrefix = "clog-fidelity-s3/";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    private static readonly LiveBucketAccount.OncePerProcessSweep StaleSweep = new();

    private S3LiveAccount(AmazonS3Client client, string bucketName, string prefix, TransportDouble transport)
    {
        Client = client;
        BucketName = bucketName;
        Prefix = prefix;
        Transport = transport;
        Store = new S3ObjectStore(client, bucketName, prefix);
    }

    /// <summary>Gets the underlying S3 client.</summary>
    public AmazonS3Client Client { get; }

    /// <summary>Gets the bucket name.</summary>
    public string BucketName { get; }

    /// <summary>Gets the unique prefix isolating this test run's objects.</summary>
    public string Prefix { get; }

    /// <summary>Gets the object store scoped to <see cref="Prefix"/>.</summary>
    public S3ObjectStore Store { get; }

    /// <summary>Gets the HTTP transport beneath <see cref="Client"/>.</summary>
    public TransportDouble Transport { get; }

    /// <summary>
    /// Opens an isolated live S3 prefix for one test, or skips the calling case while <see cref="Variable"/> is unset.
    /// </summary>
    public static async Task<S3LiveAccount> OpenAsync(CancellationToken cancellationToken)
    {
        var bucket = LiveBucketAccount.NormalizeBucketName(Environment.GetEnvironmentVariable(Variable), "s3://", Variable);
        if (bucket is null)
        {
            Assert.Skip(SkipReason);
        }

        if (S3DirectoryBucketLayout.IsDirectoryBucket(bucket))
        {
            throw new InvalidOperationException(
                $"{Variable} ('{bucket}') names an S3 Express directory bucket ('--x-s3'); use {S3ExpressLiveAccount.Variable} for directory-bucket runs.");
        }

        await StaleSweep.RunOnceAsync(
            ct => SweepStaleS3PrefixesAsync(bucket, ProviderPrefix, ct),
            cancellationToken);

        var transport = new TransportDouble();
        AmazonS3Client? client = null;
        try
        {
            client = new AmazonS3Client(new AmazonS3Config
            {
                HttpClientFactory = CloudClients.CreateS3TransportFactory(transport),
            });

            var account = new S3LiveAccount(client, bucket, LiveBucketAccount.CreateRunPrefix(ProviderPrefix), transport);
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
            await DeleteS3PrefixAsync(Client, BucketName, Prefix, timeout.Token);
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

    internal static async Task SweepStaleS3PrefixesAsync(
        string bucket,
        string providerPrefix,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CleanupTimeout);
        using var transport = new TransportDouble();
        using var client = new AmazonS3Client(new AmazonS3Config
        {
            HttpClientFactory = CloudClients.CreateS3TransportFactory(transport),
        });

        var now = TimeProvider.System.GetUtcNow();
        var stalePrefixes = new List<string>();
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = providerPrefix,
                    Delimiter = "/",
                    ContinuationToken = continuation,
                },
                timeout.Token);

            if (response.CommonPrefixes is not null)
            {
                foreach (var prefix in response.CommonPrefixes)
                {
                    if (LiveBucketAccount.IsStaleRunPrefix(prefix, providerPrefix, now))
                    {
                        stalePrefixes.Add(prefix);
                    }
                }
            }

            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);

        foreach (var stalePrefix in stalePrefixes)
        {
            try
            {
                await DeleteS3PrefixAsync(client, bucket, stalePrefix, timeout.Token);
            }
            catch (AggregateException)
            {
                // Best-effort stale sweep; a live test run continues if a stale object cannot be removed.
            }
        }
    }

    internal static async Task DeleteS3PrefixAsync(
        AmazonS3Client client,
        string bucket,
        string prefix,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    ContinuationToken = continuation,
                },
                cancellationToken);

            if (response.S3Objects is not null)
            {
                foreach (var obj in response.S3Objects)
                {
                    keys.Add(obj.Key);
                }
            }

            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);

        await LiveBucketAccount.DeleteAllAsync(
            keys,
            (key, ct) => client.DeleteObjectAsync(bucket, key, ct),
            cancellationToken);
    }
}
