using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Interop.Tests;

/// <summary>An account the interop tests run on.</summary>
public enum InteropBackend
{
    /// <summary>The fake Azure transport (<see cref="FakeAzureBlobService"/>), in process, on every change.</summary>
    Fake,

    /// <summary>Azurite, from the AppHost, under <c>CLOG_EMULATORS=1</c>.</summary>
    Azurite,

    /// <summary>A live storage account, whose blob service endpoint <see cref="AzureLiveAccount.Variable"/> holds.</summary>
    Live,
}

/// <summary>
/// One test's clients on a backend (archive-logs.md §10): a container of its own, which Clog writes through a client whose
/// requests pass a <see cref="Gate"/>, and which plain Orleans reads through a client of its own with read-only access, as
/// LOG-I6 gives a plain Orleans deployment; and, for the cases in which Orleans itself writes, further containers with
/// write access. The containers are deleted, and the fake's checks of what archive logs keep asserted, at the end.
/// </summary>
/// <remarks>
/// Read-only access is the fake's transport passing only <c>GET</c> and <c>HEAD</c>, which refuses every other request
/// with 403 <c>AuthorizationPermissionMismatch</c> as the service refuses a SAS without the permission; on Azurite, an
/// account SAS with read and list permissions; on a live account, a user delegation SAS of the container with read and list
/// permissions, which the identity's Storage Blob Data Owner role may sign.
/// </remarks>
internal sealed class InteropServices : IAsyncDisposable
{
    private static readonly Uri FakeService = new("https://fake.blob.core.windows.net/");

    private readonly List<BlobContainerClient> _created = [];
    private readonly List<IDisposable> _disposables = [];
    private readonly List<FakeAzureBlobService> _services = [];
    private Func<Task<BlobContainerClient>> _newContainer = static () => throw new InvalidOperationException("The services have no other container.");

    private InteropServices(BlobContainerClient owner, Gate gate)
    {
        Owner = owner;
        Gate = gate;
    }

    /// <summary>Gets Clog's client of the test's container, with write access, whose requests pass <see cref="Gate"/>.</summary>
    public BlobContainerClient Owner { get; }

    /// <summary>Gets the gate Clog's requests pass.</summary>
    public Gate Gate { get; }

    /// <summary>Gets plain Orleans' client of the backend's blob service, with read-only access to the test's container.</summary>
    public BlobServiceClient ReadOnlyService { get; private set; } = null!;

    /// <summary>Gets plain Orleans' client of the test's container, with read-only access.</summary>
    public BlobContainerClient ReadOnly => ReadOnlyService.GetBlobContainerClient(Owner.Name);

    /// <summary>Gets the fake's records of what archive logs keep of their blobs, broken; none on the services.</summary>
    public IReadOnlyList<string> Violations => [.. _services.SelectMany(static service => service.Violations)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Returns the clients of a container of the test's own on <paramref name="backend"/>.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="emulators">The emulators, Azurite among them.</param>
    /// <param name="checkInvariants">Whether the fake checks, after each write, what archive logs keep of their blobs.</param>
    public static async Task<InteropServices> CreateAsync(InteropBackend backend, CloudEmulators emulators, bool checkInvariants = true)
    {
        switch (backend)
        {
            case InteropBackend.Fake:
                {
                    var service = new FakeAzureBlobService(checkInvariants: checkInvariants, strict: true);
                    var gate = new Gate { InnerHandler = service };
                    var readOnly = new ReadOnlyTransport { InnerHandler = service };
                    var services = new InteropServices(new BlobServiceClient(FakeService, StoreClientOptions(gate, fake: true)).GetBlobContainerClient(FakeAzureBlobService.Container), gate);
                    services._services.Add(service);
                    services.ReadOnlyService = new BlobServiceClient(FakeService, CloudEmulators.AzureOptions(services.Own(readOnly)));
                    services._newContainer = () =>
                    {
                        // A fake service is one container, so another container is another service. It checks no archive
                        // log's invariants: Orleans writes its own journals there, whose generations are its GUIDs.
                        var other = new FakeAzureBlobService(strict: true);
                        services._services.Add(other);
                        return Task.FromResult(new BlobServiceClient(FakeService, CloudEmulators.AzureOptions(other)).GetBlobContainerClient(FakeAzureBlobService.Container));
                    };
                    return services;
                }

            case InteropBackend.Azurite:
                {
                    var endpoint = emulators.Require(Emulator.Azurite);
                    var gate = new Gate { InnerHandler = new SocketsHttpHandler() };
                    var owner = CloudEmulators.AzureService(endpoint, gate);
                    return await StartAsync(owner, gate, services =>
                    {
                        services.ReadOnlyService = new BlobServiceClient(
                            owner.GenerateAccountSasUri(AccountSasPermissions.Read | AccountSasPermissions.List, Expiry, AccountSasResourceTypes.Service | AccountSasResourceTypes.Container | AccountSasResourceTypes.Object),
                            CloudEmulators.AzureOptions(services.Own(new SocketsHttpHandler())));
                        return Task.CompletedTask;
                    });
                }

            case InteropBackend.Live:
                {
                    AzureLiveAccount.Require();
                    await AzureLiveAccount.SweepStaleContainersAsync(Token);
                    var gate = new Gate { InnerHandler = new SocketsHttpHandler() };
                    var owner = AzureLiveAccount.Service(StoreClientOptions(gate, fake: false));
                    return await StartAsync(owner, gate, async services =>
                    {
                        var key = (await owner.GetUserDelegationKeyAsync(startsOn: null, Expiry, Token)).Value;
                        var sas = new BlobSasBuilder { BlobContainerName = services.Owner.Name, Resource = "c", ExpiresOn = Expiry };
                        sas.SetPermissions(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List);
                        var uri = new BlobUriBuilder(owner.Uri) { Sas = sas.ToSasQueryParameters(key, owner.AccountName) }.ToUri();
                        services.ReadOnlyService = new BlobServiceClient(uri, CloudEmulators.AzureOptions(services.Own(new SocketsHttpHandler())));
                    });
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(backend));
        }
    }

    /// <summary>Takes <paramref name="disposable"/>, which the services dispose at the end, and returns it.</summary>
    private T Own<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    /// <summary>Returns another container of the test's own, with write access, which the test deletes.</summary>
    public Task<BlobContainerClient> NewContainerAsync() => _newContainer();

    /// <summary>Asserts that the fake recorded no broken invariant and no unmodelled condition.</summary>
    public void AssertNoViolations() => Assert.Empty(Violations);

    public async ValueTask DisposeAsync()
    {
        // The cases assert the fake's checks after each step, so disposal asserts none, which would replace a case's own
        // failure; each delete is bounded, so an unresponsive service fails the case rather than hanging it.
        try
        {
            foreach (var container in _created)
            {
                using var timeout = new CancellationTokenSource(AzureLiveAccount.DeleteTimeout, TimeProvider.System);
                _ = await container.DeleteIfExistsAsync(cancellationToken: timeout.Token);
            }
        }
        finally
        {
            Gate.Dispose();
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }

            foreach (var service in _services)
            {
                service.Dispose();
            }
        }
    }

    /// <summary>
    /// The options of Clog's client of the archive container, as the composition configures them (archive-logs.md §9.1),
    /// whose retries on the fake are 1 ms apart, so a gate which refuses a request costs no real time.
    /// </summary>
    private static BlobClientOptions StoreClientOptions(HttpMessageHandler transport, bool fake)
    {
        var options = AzureArchiveLogStore.ConfigureClientOptions(CloudEmulators.AzureOptions(transport));
        if (fake)
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.MaxDelay = TimeSpan.FromMilliseconds(1);
        }

        return options;
    }

    /// <summary>Gets the expiry of the read-only signatures: two hours from now, past any case's end.</summary>
    private static DateTimeOffset Expiry => TimeProvider.System.GetUtcNow().AddHours(2);

    /// <summary>
    /// Creates the test's container on a service and completes the services with <paramref name="configure"/>. The services
    /// own the container, which their disposal deletes, from before its creation, so a creation or a configuration which
    /// throws leaves none behind.
    /// </summary>
    private static async Task<InteropServices> StartAsync(BlobServiceClient owner, Gate gate, Func<InteropServices, Task> configure)
    {
        var services = new InteropServices(owner.GetBlobContainerClient(Name()), gate);
        try
        {
            services._created.Add(services.Owner);
            _ = await services.Owner.CreateAsync(cancellationToken: Token);
            await configure(services);
        }
        catch
        {
            await services.DisposeAsync();
            throw;
        }

        services._newContainer = async () =>
        {
            var other = owner.GetBlobContainerClient(Name());
            services._created.Add(other);
            _ = await other.CreateAsync(cancellationToken: Token);
            return other;
        };
        return services;

        static string Name() => FormattableString.Invariant($"{AzureLiveAccount.InteropContainerPrefix}{Guid.NewGuid():N}");
    }
}

/// <summary>
/// Refuses, before the service sees them, the requests <see cref="Refuses"/> matches, as a broken connection would, with
/// <see cref="GateRefusedException"/>.
/// </summary>
internal sealed class Gate : DelegatingHandler
{
    /// <summary>Gets or sets the requests the gate refuses; none when null.</summary>
    public Func<HttpRequestMessage, bool>? Refuses { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Refuses?.Invoke(request) == true
            ? Task.FromException<HttpResponseMessage>(new GateRefusedException())
            : base.SendAsync(request, cancellationToken);
}

/// <summary>The failure of a request the test's <see cref="Gate"/> refused.</summary>
internal sealed class GateRefusedException() : HttpRequestException("The test's gate refused the request before the service saw it.");

/// <summary>
/// A read-only principal's transport over the fake: <c>GET</c> and <c>HEAD</c> pass, and every other request is refused
/// with 403 <c>AuthorizationPermissionMismatch</c>, as the service refuses a SAS without the permission.
/// </summary>
internal sealed class ReadOnlyTransport : DelegatingHandler
{
    /// <summary>The response header which names a refusal's error code.</summary>
    private const string ErrorCodeHeader = "x-ms-error-code";

    /// <summary>The error code of a request its principal lacks the permission for.</summary>
    private const string PermissionMismatch = "AuthorizationPermissionMismatch";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
        {
            return base.SendAsync(request, cancellationToken);
        }

        var refused = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
        {
            RequestMessage = request,
            Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{PermissionMismatch}</Code><Message>Read-only.</Message></Error>", System.Text.Encoding.UTF8, "application/xml"),
        };
        refused.Headers.TryAddWithoutValidation(ErrorCodeHeader, PermissionMismatch);
        return Task.FromResult(refused);
    }
}
