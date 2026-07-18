using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Dissemination;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal partial class SiloMetadataCache(
    ISiloMetadataClient siloMetadataClient,
    IMembershipManager membershipManager,
    IOptions<ClusterMembershipOptions> clusterMembershipOptions,
    IOptions<SiloMetadata> localMetadata,
    ILocalSiloDetails localSiloDetails,
    IServiceProvider serviceProvider,
    ILogger<SiloMetadataCache> logger)
    : ISiloMetadataCache, ISiloMetadataDisseminationParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private static readonly TimeSpan DisseminationGracePeriod = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<SiloAddress, SiloMetadata> _metadata = new();
    private readonly Dictionary<SiloAddress, DateTime> _negativeCache = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SiloAddress _localSiloAddress = localSiloDetails.SiloAddress;
    private readonly SiloMetadata _localMetadata = localMetadata.Value;
    private long _lastMetadataDisseminationMembershipVersion = -1;
    private TimeSpan negativeCachePeriod;

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        Task? task = null;
        Task OnStart(CancellationToken _)
        {
            // This gives time for the cluster to be voted Dead and for membership updates to propagate that out
            negativeCachePeriod = clusterMembershipOptions.Value.ProbeTimeout * clusterMembershipOptions.Value.NumMissedProbesLimit
                                  + (2 * clusterMembershipOptions.Value.TableRefreshTimeout);
            _metadata[_localSiloAddress] = _localMetadata;
            task = Task.Run(() => this.ProcessMembershipUpdates(_cts.Token));
            return Task.CompletedTask;
        }

        async Task OnStop(CancellationToken ct)
        {
            await _cts.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (task is not null)
            {
                await task.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        lifecycle.Subscribe(
            nameof(ClusterMembershipService),
            ServiceLifecycleStage.RuntimeServices,
            OnStart,
            OnStop);
    }

    private async Task ProcessMembershipUpdates(CancellationToken ct)
    {
        try
        {
            LogDebugStartProcessingMembershipUpdates(logger);
            await foreach (var update in membershipManager.MembershipUpdates.WithCancellation(ct))
            {
                var now = DateTime.UtcNow;
                var recentlyActiveSilos = update.Entries
                    .Where(e => e.Value.Status is SiloStatus.Active or SiloStatus.Joining)
                    .Where(e => !e.Value.HasMissedIAmAlives(clusterMembershipOptions.Value, now))
                    .ToArray();

                if (recentlyActiveSilos.Any(entry => entry.Key.Equals(_localSiloAddress)))
                {
                    await TryPublishLocalMetadataViaDissemination(ct);
                }

                var missingSilos = recentlyActiveSilos
                    .Where(entry => !_metadata.ContainsKey(entry.Key))
                    .ToArray();
                if (missingSilos.Length > 0 && IsDisseminationEnabled())
                {
                    await Task.Delay(DisseminationGracePeriod, ct);
                    now = DateTime.UtcNow;
                }

                // Direct fetching remains the compatibility and recovery path.
                foreach (var membershipEntry in missingSilos)
                {
                    if (_metadata.ContainsKey(membershipEntry.Key))
                    {
                        continue;
                    }

                    if (_negativeCache.TryGetValue(membershipEntry.Key, out var expiration) && expiration > now)
                    {
                        continue;
                    }

                    try
                    {
                        var metadata = await siloMetadataClient.GetSiloMetadata(membershipEntry.Key).WaitAsync(ct);
                        ApplyMetadata(membershipEntry.Key, metadata);
                        _negativeCache.Remove(membershipEntry.Key, out _);
                    }
                    catch (Exception exception)
                    {
                        _negativeCache.TryAdd(membershipEntry.Key, now + negativeCachePeriod);
                        LogErrorFetchingSiloMetadata(logger, exception, membershipEntry.Key);
                    }
                }

                // Remove entries for members that are now dead
                var deadSilos = update.Entries
                    .Where(e => e.Value.Status == SiloStatus.Dead);
                foreach (var membershipEntry in deadSilos)
                {
                    _metadata.TryRemove(membershipEntry.Key, out _);
                    _negativeCache.Remove(membershipEntry.Key, out _);
                }

                // Remove entries for members that are no longer in the table
                foreach (var silo in _metadata.Keys.ToList())
                {
                    if (!update.Entries.ContainsKey(silo))
                    {
                        _metadata.TryRemove(silo, out _);
                        _negativeCache.Remove(silo, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ignore and continue shutting down.
        }
        catch (Exception exception)
        {
            LogErrorProcessingMembershipUpdates(logger, exception);
        }
        finally
        {
            LogDebugStoppingMembershipProcessor(logger);
        }
    }

    public SiloMetadata GetSiloMetadata(SiloAddress siloAddress) => _metadata.GetValueOrDefault(siloAddress) ?? SiloMetadata.Empty;

    public void SetMetadata(SiloAddress siloAddress, SiloMetadata metadata) => ApplyMetadata(siloAddress, metadata);

    ImmutableDictionary<SiloAddress, SiloMetadata> ISiloMetadataDisseminationParticipant.GetSiloMetadataForDissemination()
    {
        var membership = membershipManager.CurrentSnapshot;
        var now = DateTime.UtcNow;
        var builder = ImmutableDictionary.CreateBuilder<SiloAddress, SiloMetadata>();
        foreach (var entry in _metadata)
        {
            if (membership.Entries.TryGetValue(entry.Key, out var membershipEntry)
                && membershipEntry.Status is SiloStatus.Active or SiloStatus.Joining
                && !membershipEntry.HasMissedIAmAlives(clusterMembershipOptions.Value, now))
            {
                builder[entry.Key] = entry.Value;
            }
        }

        return builder.ToImmutable();
    }

    DisseminationApplyResult ISiloMetadataDisseminationParticipant.ApplyDisseminatedSiloMetadata(
        SiloAddress siloAddress,
        SiloMetadata metadata)
    {
        var membership = membershipManager.CurrentSnapshot;
        var now = DateTime.UtcNow;
        if (!membership.Entries.TryGetValue(siloAddress, out var membershipEntry)
            || membershipEntry.Status is not (SiloStatus.Active or SiloStatus.Joining)
            || membershipEntry.HasMissedIAmAlives(clusterMembershipOptions.Value, now))
        {
            return DisseminationApplyResult.Rejected;
        }

        return ApplyMetadata(siloAddress, metadata);
    }

    private DisseminationApplyResult ApplyMetadata(SiloAddress siloAddress, SiloMetadata metadata)
    {
        if (_metadata.TryAdd(siloAddress, metadata))
        {
            return DisseminationApplyResult.Applied;
        }

        return _metadata.TryGetValue(siloAddress, out var existing)
            && SiloMetadataDisseminationNamespace.SiloMetadataEquals(existing, metadata)
                ? DisseminationApplyResult.Duplicate
                : DisseminationApplyResult.Rejected;
    }

    private bool IsDisseminationEnabled()
    {
        var globalOptions = serviceProvider.GetService<IOptionsMonitor<DisseminationOptions>>();
        var disseminationNamespace = serviceProvider.GetService<SiloMetadataDisseminationNamespace>();
        return globalOptions?.CurrentValue.Enabled is true
            && disseminationNamespace?.Options.Enabled is true;
    }

    private async Task TryPublishLocalMetadataViaDissemination(CancellationToken cancellationToken)
    {
        var membershipVersion = membershipManager.CurrentSnapshot.Version.Value;
        if (_lastMetadataDisseminationMembershipVersion >= membershipVersion || !IsDisseminationEnabled())
        {
            return;
        }

        var disseminationService = serviceProvider.GetService<IDisseminationService>();
        var disseminationNamespace = serviceProvider.GetService<SiloMetadataDisseminationNamespace>();
        if (disseminationService is null || disseminationNamespace is null)
        {
            return;
        }

        try
        {
            if (await disseminationNamespace.PublishAsync(
                disseminationService,
                _localSiloAddress,
                cancellationToken))
            {
                _lastMetadataDisseminationMembershipVersion = membershipVersion;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogDebugSiloMetadataDisseminationFailed(logger, exception);
        }
    }

    public void Dispose() => _cts.Cancel();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Starting to process membership updates.")]
    private static partial void LogDebugStartProcessingMembershipUpdates(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error fetching metadata for silo {Silo}")]
    private static partial void LogErrorFetchingSiloMetadata(ILogger logger, Exception exception, SiloAddress silo);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates")]
    private static partial void LogErrorProcessingMembershipUpdates(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Stopping membership update processor")]
    private static partial void LogDebugStoppingMembershipProcessor(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Error disseminating local silo metadata. Falling back to direct metadata fetches.")]
    private static partial void LogDebugSiloMetadataDisseminationFailed(ILogger logger, Exception exception);
}
