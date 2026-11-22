using Amazon.S3;
using Orleans.Durable.Storage.S3.Archive;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The live Amazon S3 Express One Zone directory bucket (<c>--x-s3</c>) the S3 Express object-ledger fidelity suite runs
/// on at milestones (object-ledgers.md §9, verification.md §10, §13): its bucket name, which <see cref="Variable"/> holds,
/// authenticated with the AWS SDK's default credential chain and S3 Express session authentication.
/// </summary>
/// <remarks>
/// Without <see cref="Variable"/> each live case skips with <see cref="SkipReason"/>, which the emulator run allows
/// verbatim while the variable is unset (test/Durable.Storage/eng/validate.ps1). Each test isolates its objects under a unique timestamped
/// prefix that is deleted on disposal, and a once-per-process sweep cleans prefixes left behind by aborted runs.
/// </remarks>
internal sealed class S3ExpressLiveAccount : IAsyncDisposable
{
    /// <summary>
    /// The environment variable holding a live S3 Express One Zone directory bucket name (ending in <c>--x-s3</c>)
    /// accessible with the AWS SDK's default credentials and region.
    /// </summary>
    public const string Variable = "DURABLE_STORAGE_S3_EXPRESS_LIVE";

    /// <summary>The reason a live case skips without <see cref="Variable"/>, which test/Durable.Storage/eng/validate.ps1 allows verbatim.</summary>
    public const string SkipReason =
        $"The live-account S3 Express fidelity run waits on {Variable}: set it to an S3 Express One Zone directory bucket name (ending in '--x-s3') accessible with the AWS SDK's default credentials and region.";

    private const string ProviderPrefix = "clog-fidelity-s3e/";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    private static readonly LiveBucketAccount.OncePerProcessSweep StaleSweep = new();

    private S3ExpressLiveAccount(AmazonS3Client client, string bucketName, string prefix, TransportDouble transport)
    {
        Client = client;
        BucketName = bucketName;
        Prefix = prefix;
        Transport = transport;
        Store = new S3ObjectStore(client, bucketName, prefix);
    }

    /// <summary>Gets the underlying S3 client.</summary>
    public AmazonS3Client Client { get; }

    /// <summary>Gets the directory bucket name.</summary>
    public string BucketName { get; }

    /// <summary>Gets the unique prefix isolating this test run's objects.</summary>
    public string Prefix { get; }

    /// <summary>Gets the object store in directory-bucket mode scoped to <see cref="Prefix"/>.</summary>
    public S3ObjectStore Store { get; }

    /// <summary>Gets the HTTP transport beneath <see cref="Client"/>.</summary>
    public TransportDouble Transport { get; }

    /// <summary>
    /// Opens an isolated live S3 Express directory-bucket prefix for one test, or skips the calling case while
    /// <see cref="Variable"/> is unset.
    /// </summary>
    public static async Task<S3ExpressLiveAccount> OpenAsync(CancellationToken cancellationToken)
    {
        var bucket = LiveBucketAccount.NormalizeBucketName(Environment.GetEnvironmentVariable(Variable), "s3://", Variable);
        if (bucket is null)
        {
            Assert.Skip(SkipReason);
        }

        if (!S3DirectoryBucketLayout.IsDirectoryBucket(bucket))
        {
            throw new InvalidOperationException(
                $"{Variable} ('{bucket}') must name an S3 Express One Zone directory bucket ending in '--x-s3'.");
        }

        await StaleSweep.RunOnceAsync(
            ct => S3LiveAccount.SweepStaleS3PrefixesAsync(bucket, ProviderPrefix, ct),
            cancellationToken);

        var transport = new TransportDouble();
        AmazonS3Client? client = null;
        try
        {
            client = new AmazonS3Client(new AmazonS3Config
            {
                HttpClientFactory = CloudClients.CreateS3TransportFactory(transport),
            });

            var account = new S3ExpressLiveAccount(client, bucket, LiveBucketAccount.CreateRunPrefix(ProviderPrefix), transport);
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
            await S3LiveAccount.DeleteS3PrefixAsync(Client, BucketName, Prefix, timeout.Token);
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
}
