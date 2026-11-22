using System.Diagnostics;
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A storage service's emulator in the AppHost.
/// </summary>
public enum Emulator
{
    /// <summary>Azurite's Blob service, for Azure Blob Storage.</summary>
    Azurite,

    /// <summary>LocalStack's S3, for Amazon S3.</summary>
    LocalStack,

    /// <summary>fake-gcs-server, for Google Cloud Storage: conditions and checksums as the service checks them.</summary>
    FakeGcsServer,

    /// <summary>
    /// Google's Cloud Storage testbench, which recovers interrupted resumable uploads as the service does. It answers an
    /// upload whose bytes fail their checksum with 412, which a client reads as a failed precondition, so the integrity
    /// test runs on <see cref="FakeGcsServer"/>.
    /// </summary>
    GcsTestbench,
}

/// <summary>
/// The emulators of <c>apphost/Orleans.Durable.Storage.AppHost</c>, started once for the test assembly through Aspire's testing host when
/// <see cref="EnabledVariable"/> is 1, and the clients the tests reach them and stubbed services through.
/// </summary>
/// <remarks>
/// The emulator suites run at milestones (verification.md §10): without the variable every emulator test skips. With it,
/// an emulator which does not start or answer within <see cref="ReadyTimeout"/> fails its tests with the reason, so a run
/// passes only on evidence. Each test assembly which links this file names the emulators it uses with
/// <see cref="CloudEmulatorsUseAttribute"/>, and creates the buckets of those other than Azurite through its
/// <see cref="ICloudStores"/>; the AppHost's other resources, its MetaStore emulators among them, are left out.
/// </remarks>
public sealed class CloudEmulators : IAsyncLifetime
{
    /// <summary>
    /// The environment variable which runs the emulator suites.
    /// </summary>
    internal const string EnabledVariable = "CLOG_EMULATORS";

    /// <summary>
    /// The container, or bucket, which every store of the tests lies in, under a prefix of its own.
    /// </summary>
    internal const string ContainerName = "durable-storage-objects";

    /// <summary>
    /// Azurite's well-known development account, which Microsoft publishes for the emulator.
    /// </summary>
    private const string AzuriteAccount = "devstoreaccount1";

    private const string AzuriteKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    private static readonly IReadOnlyDictionary<Emulator, (string Resource, string Endpoint)> Resources =
        new Dictionary<Emulator, (string, string)>
        {
            [Emulator.Azurite] = ("azurite", "blob"),
            [Emulator.LocalStack] = ("localstack", "gateway"),
            [Emulator.FakeGcsServer] = ("gcs", "http"),
            [Emulator.GcsTestbench] = ("gcs-testbench", "http"),
        };

    private readonly Dictionary<Emulator, Uri> _endpoints = [];
    private readonly Dictionary<Emulator, string> _failures = [];
    private DistributedApplication? _app;

    /// <summary>
    /// Gets a value indicating whether the emulator suites run.
    /// </summary>
    internal static bool Enabled => string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Orleans_Durable_Storage_AppHost>(cancellationToken);

            // The host's EventLog provider would write each emulator's standard error to the machine's Application log.
            _ = builder.Services.Configure<LoggerFilterOptions>(static options =>
                options.Rules.Add(new LoggerFilterRule("EventLog", categoryName: null, LogLevel.None, filter: null)));
            var used = Used.Select(static emulator => Resources[emulator].Resource).ToHashSet(StringComparer.Ordinal);
            foreach (var resource in builder.Resources.Where(resource => resource.Name is "dynamodb" or "firestore" || Resources.Values.Any(known => known.Resource == resource.Name) && !used.Contains(resource.Name)).ToList())
            {
                _ = builder.Resources.Remove(resource);
            }

            _app = await builder.BuildAsync(cancellationToken);
            await _app.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var emulator in Used)
            {
                Fail(emulator, $"The AppHost's emulators did not start (is Docker running?): {exception.GetType().Name}: {exception.Message}");
            }

            return;
        }

        await Task.WhenAll(Used.Select(emulator => StartAsync(emulator, cancellationToken)));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Returns the emulator's endpoint. Without <see cref="EnabledVariable"/> it skips the calling test; with it, it fails
    /// the test when the emulator is unavailable.
    /// </summary>
    internal Uri Require(Emulator emulator)
    {
        if (!Enabled)
        {
            Assert.Skip($"The emulator suites run at milestones: set {EnabledVariable}=1 (just emulators) with Docker running.");
        }

        lock (_endpoints)
        {
            if (_endpoints.TryGetValue(emulator, out var endpoint))
            {
                return endpoint;
            }

            Assert.Fail($"{emulator}: {_failures.GetValueOrDefault(emulator, "the emulator did not start.")}");
            throw new UnreachableException();
        }
    }

    /// <summary>
    /// Returns a client of Azure Blob Storage at <paramref name="endpoint"/>, Azurite's development account, whose requests
    /// pass through <paramref name="transport"/>.
    /// </summary>
    /// <remarks>
    /// The client addresses the account by IP address, from which the SDK reads the account from the path, as it does for
    /// no host name.
    /// </remarks>
    internal static BlobContainerClient AzureContainer(Uri endpoint, HttpMessageHandler transport) =>
        AzureService(endpoint, transport).GetBlobContainerClient(ContainerName);

    /// <summary>
    /// Returns a client of Azurite's development account at <paramref name="endpoint"/>, whose requests pass through
    /// <paramref name="transport"/>.
    /// </summary>
    internal static BlobServiceClient AzureService(Uri endpoint, HttpMessageHandler transport)
    {
        var connection = $"DefaultEndpointsProtocol={endpoint.Scheme};AccountName={AzuriteAccount};AccountKey={AzuriteKey};BlobEndpoint={endpoint.Scheme}://127.0.0.1:{endpoint.Port}/{AzuriteAccount};";
        return new BlobServiceClient(connection, AzureOptions(transport));
    }

    /// <summary>
    /// Returns the options of an Azure Blob Storage client whose requests pass through <paramref name="transport"/>, with
    /// short retry delays.
    /// </summary>
    internal static BlobClientOptions AzureOptions(HttpMessageHandler transport)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(transport) };
        options.Retry.Delay = TimeSpan.FromMilliseconds(20);
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(200);
        return options;
    }

    private async Task StartAsync(Emulator emulator, CancellationToken cancellationToken)
    {
        var (resource, endpointName) = Resources[emulator];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadyTimeout);
        try
        {
            var state = await _app!.ResourceNotifications.WaitForResourceAsync(
                resource,
                static update => update.Snapshot.State?.Text is { } text && (text == KnownResourceStates.Running || KnownResourceStates.TerminalStates.Contains(text)),
                timeout.Token);
            if (state.Snapshot.State?.Text != KnownResourceStates.Running)
            {
                Fail(emulator, $"the container '{resource}' ended in state {state.Snapshot.State?.Text} (exit code {state.Snapshot.ExitCode}).");
                return;
            }

            var endpoint = _app.GetEndpoint(resource, endpointName);
            await ProbeAsync(emulator, endpoint, timeout.Token);
            lock (_endpoints)
            {
                _endpoints[emulator] = endpoint;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Fail(emulator, $"the container '{resource}' did not answer within {ReadyTimeout}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(emulator, $"the container '{resource}' failed its probe: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>Gets the test assembly's choice of emulators, which <see cref="CloudEmulatorsUseAttribute"/> names.</summary>
    private static CloudEmulatorsUseAttribute Use { get; } =
        typeof(CloudEmulators).Assembly.GetCustomAttribute<CloudEmulatorsUseAttribute>()
            ?? throw new InvalidOperationException("A test assembly which links CloudEmulators names the emulators it uses with [assembly: CloudEmulatorsUse(...)].");

    /// <summary>Gets the emulators this test assembly's tests use; the AppHost starts no other.</summary>
    private static IReadOnlyList<Emulator> Used => Use.Emulators;

    /// <summary>
    /// Creates the container or bucket the tests use, retrying until the emulator answers.
    /// </summary>
    private static async Task ProbeAsync(Emulator emulator, Uri endpoint, CancellationToken cancellationToken)
    {
        using var transport = new SocketsHttpHandler();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (emulator == Emulator.Azurite)
                {
                    _ = await AzureContainer(endpoint, transport).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                }
                else
                {
                    var stores = (ICloudStores)Activator.CreateInstance(Use.Stores ?? throw new InvalidOperationException($"No ICloudStores creates the {emulator} bucket."))!;
                    await stores.CreateAsync(emulator, endpoint, transport, cancellationToken);
                }

                return;
            }
            catch (Exception) when (attempt < 60 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, cancellationToken);
            }
        }
    }

    private void Fail(Emulator emulator, string reason)
    {
        lock (_endpoints)
        {
            _failures[emulator] = reason;
        }
    }
}

/// <summary>Names the emulators a test assembly's tests use, which <see cref="CloudEmulators"/> starts, and no other.</summary>
/// <param name="emulators">The emulators.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CloudEmulatorsUseAttribute(params Emulator[] emulators) : Attribute
{
    /// <summary>Gets the emulators.</summary>
    public IReadOnlyList<Emulator> Emulators { get; } = emulators;

    /// <summary>Gets or sets the <see cref="ICloudStores"/>, with a parameterless constructor, which creates the buckets of the emulators other than Azurite.</summary>
    public Type? Stores { get; set; }
}

/// <summary>Creates the bucket a test assembly's tests use on an emulator other than Azurite.</summary>
public interface ICloudStores
{
    /// <summary>
    /// Creates the bucket the tests use on <paramref name="emulator"/>, whose requests pass through
    /// <paramref name="transport"/>; one which exists is left as it is.
    /// </summary>
    Task CreateAsync(Emulator emulator, Uri endpoint, HttpMessageHandler transport, CancellationToken cancellationToken);
}
