using Azure.Identity;
using Azure.Storage.Blobs;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The live storage account the Azure suites run on at milestones (verification.md §10): its blob service endpoint, which
/// <see cref="Variable"/> holds, and the Microsoft Entra ID credential its cases authenticate with.
/// </summary>
/// <remarks>
/// Without <see cref="Variable"/> each live case skips with <see cref="SkipReason"/>, which the emulator run, in which any
/// other skip fails, allows verbatim while the variable is unset (eng/validate.ps1). The cases authenticate through
/// <see cref="DefaultAzureCredential"/>, which takes the Azure CLI's sign-in on a workstation and a workload identity in
/// CI; the identity holds Storage Blob Data Owner, the built-in role which may write blob index tags.
/// </remarks>
internal static class AzureLiveAccount
{
    /// <summary>
    /// The environment variable which holds a live account's blob service endpoint, such as
    /// <c>https://account.blob.core.windows.net/</c>, on which the signed-in identity holds Storage Blob Data Owner.
    /// </summary>
    public const string Variable = "CLOG_AZURE_LIVE";

    /// <summary>The reason a live case skips without <see cref="Variable"/>, which eng/validate.ps1 allows verbatim.</summary>
    public const string SkipReason =
        $"The live-account fidelity run waits on {Variable}: set it to a storage account's blob service endpoint, on which the identity DefaultAzureCredential signs in with holds Storage Blob Data Owner.";

    /// <summary>The prefix of the containers the Azure fidelity tests create, one per case, which each case deletes.</summary>
    public const string FidelityContainerPrefix = "clog-fidelity-";

    /// <summary>The prefix of the containers the Orleans interop tests create, which each case deletes.</summary>
    public const string InteropContainerPrefix = "clog-interop-";

    /// <summary>Gets the bound of one container's deletion, so an unresponsive service fails a case rather than hanging it.</summary>
    public static TimeSpan DeleteTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the age past which a test's container is stale: a run aborted before its cases deleted it left it.</summary>
    private static TimeSpan StaleAge { get; } = TimeSpan.FromDays(1);

    private static readonly Lock SweepLock = new();

    private static Task? _sweep;

    // Read once, so every live case of a run sees the same account; a value which is not an absolute URI fails each case.
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable(Variable) is { } endpoint && !string.IsNullOrWhiteSpace(endpoint)
        ? endpoint.Trim()
        : null;

    // One credential for the run, so its cases share one token.
    private static readonly Lazy<DefaultAzureCredential> Credential = new(static () => new DefaultAzureCredential());

    /// <summary>Skips the calling case, with <see cref="SkipReason"/>, while <see cref="Variable"/> is unset.</summary>
    public static void Require()
    {
        if (Endpoint is null)
        {
            Assert.Skip(SkipReason);
        }
    }

    /// <summary>
    /// Deletes, once per run, the test containers on the live account more than a day old, which runs aborted before their
    /// cases ended left behind and which the account bills for; a run younger than a day keeps its own. The sweep owns a
    /// dedicated client and transport so a cancelling caller never disposes resources underneath an in-flight sweep. A
    /// sweep which failed or was cancelled fails the cases which awaited it and is dropped, so the next case sweeps again.
    /// </summary>
    public static Task SweepStaleContainersAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Require();

        TaskCompletionSource? starter = null;
        Task shared;
        lock (SweepLock)
        {
            if (_sweep is { IsFaulted: true } or { IsCanceled: true })
            {
                _sweep = null;
            }

            if (_sweep is null)
            {
                starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _sweep = starter.Task;
            }

            shared = _sweep;
        }

        if (starter is not null)
        {
            _ = starter.Task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = RunSweepStarterAsync(starter);
        }

        return shared.WaitAsync(cancellationToken);
    }

    /// <summary>Returns a client of the live account's blob service, or skips the calling case without <see cref="Variable"/>.</summary>
    public static BlobServiceClient Service(BlobClientOptions options)
    {
        Require();
        return new BlobServiceClient(new Uri(Endpoint!, UriKind.Absolute), Credential.Value, options);
    }

    private static async Task RunSweepStarterAsync(TaskCompletionSource starter)
    {
        try
        {
            await SweepAsync().ConfigureAwait(false);
            _ = starter.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            _ = starter.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            _ = starter.TrySetException(ex);
        }
    }

    private static async Task SweepAsync()
    {
        using var handler = new SocketsHttpHandler();
        var service = Service(CloudEmulators.AzureOptions(handler));
        var stale = TimeProvider.System.GetUtcNow() - StaleAge;
        using var sweepTimeout = new CancellationTokenSource(DeleteTimeout);
        foreach (var prefix in (string[])[FidelityContainerPrefix, InteropContainerPrefix])
        {
            await foreach (var container in service.GetBlobContainersAsync(prefix: prefix, cancellationToken: sweepTimeout.Token).ConfigureAwait(false))
            {
                if (container.Properties.LastModified < stale)
                {
                    _ = await service.GetBlobContainerClient(container.Name).DeleteIfExistsAsync(cancellationToken: sweepTimeout.Token).ConfigureAwait(false);
                }
            }
        }
    }
}
