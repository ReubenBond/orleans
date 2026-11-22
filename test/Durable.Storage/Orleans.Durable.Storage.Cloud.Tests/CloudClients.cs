using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Google;
using Google.Apis.Http;
using Google.Cloud.Storage.V1;
using Orleans.Durable.Storage.Cloud.Tests;
using Orleans.Durable.Storage.Testing;

[assembly: AssemblyFixture(typeof(CloudEmulators))]
[assembly: CloudEmulatorsUse(Emulator.Azurite, Emulator.LocalStack, Emulator.FakeGcsServer, Emulator.GcsTestbench, Stores = typeof(CloudClients))]

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The clients of the cloud stores' emulators other than Azurite, and the creation of their buckets, which
/// <see cref="CloudEmulators"/> runs when it starts them.
/// </summary>
internal sealed class CloudClients : ICloudStores
{
    /// <summary>
    /// Returns a client of S3 at <paramref name="endpoint"/> whose requests pass through <paramref name="transport"/>.
    /// </summary>
    /// <remarks>
    /// The SDK's retry quota is one token bucket per service URL for the whole process (<c>StandardRetryPolicy</c>'s
    /// static <c>CapacityManager</c>): each retry spends 14 of 500 tokens, and only a success refunds any. Every test's
    /// client of the emulator shares it, so the faults other tests inject drain it, and once it is empty the client
    /// surfaces a fault instead of retrying it (#377). A test's client therefore takes no quota, unless
    /// <paramref name="throttleRetries"/> asks for the SDK's default, so its retries depend on its own requests alone.
    /// <c>ForcePathStyle</c> and <paramref name="disableS3ExpressSessionAuth"/> are test-only seams for routing SDK
    /// requests to LocalStack or the in-process <see cref="FakeS3DirectoryBucketService"/>; production S3 Express One
    /// Zone clients use virtual-hosted endpoints and SigV4 Express session authentication.
    /// </remarks>
    public static AmazonS3Client S3Client(Uri endpoint, HttpMessageHandler transport, bool throttleRetries = false, bool disableS3ExpressSessionAuth = false) =>
        new(
            new BasicAWSCredentials("test", "test"),
            new AmazonS3Config
            {
                ServiceURL = endpoint.GetLeftPart(UriPartial.Authority),
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                HttpClientFactory = new S3TransportFactory(transport),
                ThrottleRetries = throttleRetries,
                DisableS3ExpressSessionAuth = disableS3ExpressSessionAuth,
            });

    /// <summary>
    /// Returns a client of Cloud Storage at <paramref name="endpoint"/> whose requests pass through
    /// <paramref name="transport"/>.
    /// </summary>
    public static StorageClient StorageClient(Uri endpoint, HttpMessageHandler transport) =>
        new StorageClientBuilder
        {
            BaseUri = endpoint.GetLeftPart(UriPartial.Authority) + "/storage/v1/",
            UnauthenticatedAccess = true,
            HttpClientFactory = new GoogleTransportFactory(transport),
        }.Build();

    /// <inheritdoc/>
    public async Task CreateAsync(Emulator emulator, Uri endpoint, HttpMessageHandler transport, CancellationToken cancellationToken)
    {
        switch (emulator)
        {
            case Emulator.LocalStack:
                using (var client = S3Client(endpoint, transport))
                {
                    _ = await client.PutBucketAsync(CloudEmulators.ContainerName, cancellationToken);
                }

                break;
            case Emulator.FakeGcsServer or Emulator.GcsTestbench:
                using (var client = StorageClient(endpoint, transport))
                {
                    try
                    {
                        _ = await client.CreateBucketAsync("durable-storage-dev", CloudEmulators.ContainerName, cancellationToken: cancellationToken);
                    }
                    catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.Conflict)
                    {
                        // The bucket exists.
                    }
                }

                break;
            case Emulator.Azurite:
            default:
                throw new ArgumentOutOfRangeException(nameof(emulator));
        }
    }

    /// <summary>
    /// Hands the S3 client an <see cref="HttpClient"/> over the test's transport.
    /// </summary>
    internal static Amazon.Runtime.HttpClientFactory CreateS3TransportFactory(HttpMessageHandler transport) =>
        new S3TransportFactory(transport);

    /// <summary>
    /// Hands the Cloud Storage client the test's transport beneath its own message handler.
    /// </summary>
    internal static Google.Apis.Http.HttpClientFactory CreateGoogleTransportFactory(HttpMessageHandler transport) =>
        new GoogleTransportFactory(transport);

    /// <summary>
    /// Hands the S3 client an <see cref="HttpClient"/> over the test's transport.
    /// </summary>
    private sealed class S3TransportFactory(HttpMessageHandler transport) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(transport, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }

    /// <summary>
    /// Hands the Cloud Storage client the test's transport beneath its own message handler.
    /// </summary>
    private sealed class GoogleTransportFactory(HttpMessageHandler transport) : Google.Apis.Http.HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new ForwardingHandler(transport);
    }

    /// <summary>
    /// Passes requests to a transport its client does not own, so disposing the client leaves the transport.
    /// </summary>
    private sealed class ForwardingHandler(HttpMessageHandler transport) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(transport, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
