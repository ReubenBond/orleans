#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class ReplicatedGrainDirectory : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _processViewChangesTask;

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(ReplicatedGrainDirectory),
            ServiceLifecycleStage.RuntimeInitialize,
            ct =>
            {
                using var _ = new ExecutionContextSuppressor();
                _processViewChangesTask = ProcessViewChangesAsync();
                return Task.CompletedTask;
            },
            async ct =>
            {
                _shutdownCts.Cancel();
                if (_processViewChangesTask is { } task)
                {
                    await task.WaitAsync(ct).SuppressThrowing();
                }
            });
    }

    private async Task ProcessViewChangesAsync()
    {
        var localActivationDirectory = serviceProvider.GetRequiredService<ActivationDirectory>();
        var grainDirectoryResolver = serviceProvider.GetRequiredService<GrainDirectoryResolver>();

        // Yield immediately to the caller.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                await foreach (var view in localReplica.ViewUpdates.WithCancellation(_shutdownCts.Token))
                {
                    await ProcessViewChangeAsync(view, localActivationDirectory, grainDirectoryResolver, _shutdownCts.Token);
                }
            }
            catch (Exception exception)
            {
                if (!_shutdownCts.IsCancellationRequested)
                {
                    logger.LogError(exception, "An error occurred while processing view changes.");
                }
            }
        }
    }

    private async Task ProcessViewChangeAsync(
        DirectoryMembershipSnapshot view,
        ActivationDirectory localActivationDirectory,
        GrainDirectoryResolver grainDirectoryResolver,
        CancellationToken cancellationToken)
    {
        var dataLossVersionTasks = new List<Task<(bool Success, MembershipVersion DataLossVersion)>>(view.Members.Length);
        foreach (var member in view.Members)
        {
            dataLossVersionTasks.Add(GetDataLossVersion(view.Version, member, cancellationToken));
        }

        await Task.WhenAll(dataLossVersionTasks).WaitAsync(cancellationToken);

        var isLatestView = true;
        var dataLossVersions = new MembershipVersion[dataLossVersionTasks.Count];
        for (var i = 0; i < dataLossVersionTasks.Count; i++)
        {
            var task = dataLossVersionTasks[i];
            Debug.Assert(task.IsCompleted);

            var result = await dataLossVersionTasks[i];
            if (!result.Success)
            {
                isLatestView = true;
                break;
            }

            dataLossVersions[i] = result.DataLossVersion;
        }

        if (!isLatestView)
        {
            // One of the replicas returned a higher view number, so return now and wait for the higher view to be processed.
            return;
        }

        Dictionary<SiloAddress, List<IGrainContext>> activationsToRecover = [];
        foreach (var (grainId, activation) in localActivationDirectory)
        {
            var directory = GetGrainDirectory(activation, grainDirectoryResolver);
            if (directory != this)
            {
                // Skip activations not registered to this directory.
                continue;
            }

            var address = activation.Address;
            if (view.TryGetOwnerIndex(grainId, out var ownerIndex))
            {
                if (dataLossVersions[ownerIndex] > address.MembershipVersion)
                {
                    AddActivationToRecoveryList(view.Members[ownerIndex], activation);
                }
            }
        }

        void AddActivationToRecoveryList(SiloAddress rangeOwner, IGrainContext activation)
        {
            ref var rangeActivations = ref CollectionsMarshal.GetValueRefOrAddDefault(activationsToRecover, rangeOwner, out _);
            rangeActivations ??= [];
            rangeActivations.Add(activation);
        }

        var tasks = new List<Task>(activationsToRecover.Count);
        foreach (var (rangeOwner, activationList) in activationsToRecover)
        {
            var reasonText = $"This activation is being deactivated due to a failure of server {rangeOwner}, since it was responsible for this activation's grain directory registration.";
            var reason = new DeactivationReason(DeactivationReasonCode.InternalFailure, reasonText);
            tasks.Add(DeactivateActivations(reason, activationList, cancellationToken));
        }

        await Task.WhenAll(tasks);

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

        async Task DeactivateActivations(DeactivationReason reason, List<IGrainContext> list, CancellationToken token)
        {
            if (list.Count == 0)
            {
                return;
            }

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("DeactivateActivations: {Count} activations.", list.Count);

            var tasks = new List<Task>(list.Count);
            foreach (var activation in list)
            {
                tasks.Add(activation.DeactivateAsync(reason, token));
            }

            await Task.WhenAll(tasks).WaitAsync(token).SuppressThrowing();
        }
    }

    private async Task<(bool Success, MembershipVersion DataLossVersion)> GetDataLossVersion(MembershipVersion viewNumber, SiloAddress replicaAddress, CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await localReplica.GetReplica(replicaAddress).GetDataLossVersion(viewNumber);
                if (result.TryGetResult(viewNumber, out var dataLossVersion))
                {
                    return (true, dataLossVersion);
                }

                // The caller will need to refresh.
                return (false, default);
            }
            catch (Exception exception)
            {
                // Sleep and retry.
                logger.LogError(exception, "An error occurred while fetching data loss version from '{Replica}'.", replicaAddress);
                await Task.Delay(delay, token).SuppressThrowing();
                delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 1.5));
            }
        }

        return (false, default);
    }
}
