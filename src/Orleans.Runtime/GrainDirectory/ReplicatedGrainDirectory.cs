using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

public static class TESTLATCH
{
    public static volatile bool LATCH = false;
}

internal sealed partial class ReplicatedGrainDirectory(
    GrainDirectoryReplica localReplica,
    ILogger<ReplicatedGrainDirectory> logger,
    ILocalSiloDetails localSiloDetails,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider)
    : SystemTarget(Constants.DirectoryReplicaClientType, localSiloDetails.SiloAddress, loggerFactory), IGrainDirectory, IGrainDirectoryReplicaClient, ILifecycleParticipant<ISiloLifecycle>
{
    // The recovery membership value is used to avoid a race between concurrent registration & recovery operations which could lead to lost registrations.
    // This could occur when a new activation is created and begins registering itself with a host which crashes. Concurrently, the new owner initiates
    // recovery and asks all silos for their activations. When this silo processes this request, it will have the activation in its internal
    // 'ActivationDirectory' even though these activations may not yet have completed registration. Therefore, multiple silos may return an entry for the same
    // grain. By ensuring that any registration occurred at a version at least as high as the recovery version, we avoid this issue. This could be made more
    // precise by also tracking the sets of ranges which need to be recovered, but that complicates things somewhat since it would require tracking the ranges
    // for each recovery version.
    private long _recoveryMembershipVersionValue;

    public async Task<GrainAddress?> Lookup(GrainId grainId) => await InvokeAsync(
        grainId,
        static (replica, version, grainId) => replica.LookupAsync(version, grainId),
        grainId,
        strict: false);

    public async Task<GrainAddress?> Register(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address) => replica.RegisterAsync(version, address, null),
        address,
        strict: true);

    public async Task Unregister(GrainAddress address) => await InvokeAsync(
        address.GrainId,
        static (replica, version, address) => replica.UnregisterAsync(version, address),
        address,
        strict: false);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress) => await InvokeAsync(
        address.GrainId,
        static (replica, version, state) => replica.RegisterAsync(version, state.Address, state.PreviousAddress),
        (Address: address, PreviousAddress: previousAddress),
        strict: true);

    public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;

    private async Task<TResult> InvokeAsync<TState, TResult>(
        GrainId grainId,
        Func<IGrainDirectoryReplica, MembershipVersion, TState, ValueTask<DirectoryResult<TResult>>> func,
        TState state,
        bool strict = true,
        [CallerMemberName] string operation = "")
    {
        DirectoryResult<TResult> invokeResult;
        var view = localReplica.CurrentView;
        while (true)
        {

// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
                await Task.Delay(100);
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
            
            var initialVersion = _recoveryMembershipVersionValue;
            if (view.Version.Value < _recoveryMembershipVersionValue || !view.TryGetOwnerIndex(grainId, out var owner))
            {
                if (view.Members.Length == 0 && view.Version.Value > 0)
                {
                    return default!;
                }

                view = await localReplica.RefreshViewAsync(new(view.Version.Value + 1));
                continue;
            }

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Invoking '{Operation}' on '{Owner}' for grain '{GrainId}'.", operation, owner, grainId);
            }

            var replica = localReplica.GetReplica(owner);
            invokeResult = await func(replica, view.Version, state);

// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
                await Task.Delay(100);
            if (TESTLATCH.LATCH && Random.Shared.Next(10) == 4)
            {
                await Task.Delay(5000);
            }
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING

            if (initialVersion != _recoveryMembershipVersionValue)
            {
                // If the recovery version changed, perform a view refresh and re-issue the operation.
                // See the comment on the declaration of '_recoveryMembershipVersionValue' for more details.
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
                logger.LogError("WOWOWOW - Retry '{Operation}' on '{Owner}' for grain '{GrainId}' due to recovery version change from '{InitialVersion}' to '{CurrentVersion}'", operation, owner, grainId, initialVersion, _recoveryMembershipVersionValue);  
                logger.LogError("WOWOWOW - Retry '{Operation}' on '{Owner}' for grain '{GrainId}' due to recovery version change from '{InitialVersion}' to '{CurrentVersion}'", operation, owner, grainId, initialVersion, _recoveryMembershipVersionValue);  
                logger.LogError("WOWOWOW - Retry '{Operation}' on '{Owner}' for grain '{GrainId}' due to recovery version change from '{InitialVersion}' to '{CurrentVersion}'", operation, owner, grainId, initialVersion, _recoveryMembershipVersionValue);  
                logger.LogError("WOWOWOW - Retry '{Operation}' on '{Owner}' for grain '{GrainId}' due to recovery version change from '{InitialVersion}' to '{CurrentVersion}'", operation, owner, grainId, initialVersion, _recoveryMembershipVersionValue);  
                logger.LogError("WOWOWOW - Retry '{Operation}' on '{Owner}' for grain '{GrainId}' due to recovery version change from '{InitialVersion}' to '{CurrentVersion}'", operation, owner, grainId, initialVersion, _recoveryMembershipVersionValue);  
                logger.LogError("WOWOWOW - Retry '{Operation}' on '{Owner}' for grain '{GrainId}' due to recovery version change from '{InitialVersion}' to '{CurrentVersion}'", operation, owner, grainId, initialVersion, _recoveryMembershipVersionValue);  
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
// TESTING TESTING TESTING
                continue;
            }

            if (invokeResult.TryGetResult(view.Version, out var result))
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogInformation("Invoked '{Operation}' on '{Owner}' for grain '{GrainId}' and received result '{Result}'.", operation, owner, grainId, result);
                }

                return result;
            }
            else
            {
                // Sync with the remote replica.
                view = await localReplica.RefreshViewAsync(invokeResult.Version);
            }
        }
    }

    public ValueTask<Immutable<List<GrainAddress>>> GetRegisteredActivations(MembershipVersion membershipVersion, RingRangeCollection ranges)
    {
        if (_recoveryMembershipVersionValue < membershipVersion.Value)
        {
            // Interlocked.Exchange is used to ensure that the value is immediately visible to any thread registering an activation.
            Interlocked.Exchange(ref _recoveryMembershipVersionValue, membershipVersion.Value);
        }

        var localActivations = serviceProvider.GetRequiredService<ActivationDirectory>();
        var grainDirectoryResolver = serviceProvider.GetRequiredService<GrainDirectoryResolver>();
        var result = new List<GrainAddress>();
        foreach (var (grainId, activation) in localActivations)
        {
            var directory = GetGrainDirectory(activation, grainDirectoryResolver);
            if (directory is not null && directory == this)
            {
                if (ranges.Contains(activation.GrainId.GetUniformHashCode()))
                {
                    result.Add(activation.Address);
                }
            }
        }

        return new(result.AsImmutable());

        static IGrainDirectory? GetGrainDirectory(IGrainContext grainContext, GrainDirectoryResolver grainDirectoryResolver)
        {
            if (grainContext is ActivationData activationData)
            {
                return activationData.Shared.GrainDirectory;
            }
            else if (grainContext is SystemTarget systemTarget)
            {
                return null;
            }
            else if (grainContext.GetComponent<PlacementStrategy>() is { IsUsingGrainDirectory: true })
            {
                return grainDirectoryResolver.Resolve(grainContext.GrainId.Type);
            }

            return null;
        }
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(nameof(RemoteGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart);
        Task OnRuntimeInitializeStart(CancellationToken cancellationToken)
        {
            var catalog = serviceProvider.GetRequiredService<Catalog>();
            catalog.RegisterSystemTarget(this);

            return Task.CompletedTask;
        }
    }
}
