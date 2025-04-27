#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.Placement;

// See: https://www.ledjonbehluli.com/posts/orleans_resource_placement_kalman/
internal sealed class ResourceOptimizedPlacementDirector : IPlacementDirector, ISiloStatisticsChangeListener
{
    private const int FourKiloByte = 4096;
    private readonly SiloAddress _localSilo;
    private readonly NormalizedWeights _weights;
    private readonly float _localSiloPreferenceMargin;
    private readonly float _directoryPartitionPreferenceMargin;
    private readonly ConcurrentDictionary<SiloAddress, ResourceStatistics> _siloStatistics = [];
    private readonly Task<SiloAddress> _cachedLocalSilo;
    private readonly DirectoryMembershipService _directoryMembershipService;

    public ResourceOptimizedPlacementDirector(
        ILocalSiloDetails localSiloDetails,
        DeploymentLoadPublisher deploymentLoadPublisher,
        IOptions<ResourceOptimizedPlacementOptions> options,
        DirectoryMembershipService directoryMembershipService)
    {
        _localSilo = localSiloDetails.SiloAddress;
        _cachedLocalSilo = Task.FromResult(_localSilo);
        _weights = NormalizeWeights(options.Value);
        _localSiloPreferenceMargin = (float)options.Value.LocalSiloPreferenceMargin / 100;
        _directoryPartitionPreferenceMargin = (float)options.Value.DirectoryPartitionSiloPreferenceMargin / 100;
        deploymentLoadPublisher.SubscribeToStatisticsChangeEvents(this);
        _directoryMembershipService = directoryMembershipService;
    }

    private static NormalizedWeights NormalizeWeights(ResourceOptimizedPlacementOptions input)
    {
        int totalWeight = input.CpuUsageWeight + input.MemoryUsageWeight + input.AvailableMemoryWeight + input.MaxAvailableMemoryWeight + input.ActivationCountWeight;

        return totalWeight == 0 ? new(0f, 0f, 0f, 0f, 0f) :
            new(
                CpuUsageWeight: (float)input.CpuUsageWeight / totalWeight,
                MemoryUsageWeight: (float)input.MemoryUsageWeight / totalWeight,
                AvailableMemoryWeight: (float)input.AvailableMemoryWeight / totalWeight,
                MaxAvailableMemoryWeight: (float)input.MaxAvailableMemoryWeight / totalWeight,
                ActivationCountWeight: (float)input.ActivationCountWeight / totalWeight);
    }

    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatibleSilos = context.GetCompatibleSilos(target);

        if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
        {
            return Task.FromResult(placementHint);
        }

        if (compatibleSilos.Length == 0)
        {
            throw new SiloUnavailableException($"Cannot place grain '{target.GrainIdentity}' because there are no compatible silos.");
        }

        if (compatibleSilos.Length == 1)
        {
            return Task.FromResult(compatibleSilos[0]);
        }

        if (_siloStatistics.IsEmpty)
        {
            return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
        }

        // Find the silo which owns the grain's directory entry.
        // This is a good second pick after the local silo because we can at potentially save a remote
        // call to the directory during activation. External clients will choose the corresponding
        // gateway to route to when they have no prior knowledge of grain placement.
        var directoryMembershipSnapshot = _directoryMembershipService.CurrentView;
        directoryMembershipSnapshot.TryGetOwner(target.GrainIdentity, out var directorySilo, out _);

        // It is good practice not to allocate more than 1[KB] on the stack
        // but the size of ValueTuple<int, ResourceStatistics> = 24 bytes, by increasing
        // the limit to 4[KB] we can stackalloc for up to 4096 / 24 ~= 170 silos in a cluster.
        (int Index, float Score, float? LocalSiloScore, float? DirectorySiloScore) pick;
        int compatibleSilosCount = compatibleSilos.Length;
        if (compatibleSilosCount * Unsafe.SizeOf<(int, ResourceStatistics)>() <= FourKiloByte)
        {
            pick = MakePick(stackalloc (int, ResourceStatistics)[compatibleSilosCount], directorySilo);
        }
        else
        {
            var relevantSilos = ArrayPool<(int, ResourceStatistics)>.Shared.Rent(compatibleSilosCount);
            pick = MakePick(relevantSilos.AsSpan(), directorySilo);
            ArrayPool<(int, ResourceStatistics)>.Shared.Return(relevantSilos);
        }

        var localSiloScore = pick.LocalSiloScore;
        if (localSiloScore.HasValue && context.LocalSiloStatus == SiloStatus.Active && localSiloScore.Value - _localSiloPreferenceMargin <= pick.Score)
        {
            return _cachedLocalSilo;
        }

        var directorySiloScore = pick.DirectorySiloScore;
        if (directorySilo is not null && directorySiloScore.HasValue && directorySiloScore.Value - _directoryPartitionPreferenceMargin <= pick.Score)
        {
            return Task.FromResult(directorySilo);
        }

        var bestCandidate = compatibleSilos[pick.Index];
        return Task.FromResult(bestCandidate);

        (int PickIndex, float PickScore, float? LocalSiloScore, float? DirectorySiloScore) MakePick(scoped Span<(int, ResourceStatistics)> relevantSilos, SiloAddress? directorySilo)
        {
            // Get all compatible silos which aren't overloaded
            int relevantSilosCount = 0;
            float maxMaxAvailableMemory = 0;
            int maxActivationCount = 0;
            ResourceStatistics? localSiloStatistics = null;
            ResourceStatistics? directorySiloStatistics = null;
            for (var i = 0; i < compatibleSilos.Length; ++i)
            {
                var silo = compatibleSilos[i];
                if (_siloStatistics.TryGetValue(silo, out var stats))
                {
                    if (!stats.IsOverloaded)
                    {
                        relevantSilos[relevantSilosCount++] = new(i, stats);
                    }

                    if (stats.MaxAvailableMemory > maxMaxAvailableMemory)
                    {
                        maxMaxAvailableMemory = stats.MaxAvailableMemory;
                    }

                    if (stats.ActivationCount > maxActivationCount)
                    {
                        maxActivationCount = stats.ActivationCount;
                    }

                    if (silo.Equals(directorySilo))
                    {
                        directorySiloStatistics = stats;
                    }
                }
            }

            // Limit to the number of candidates added.
            relevantSilos = relevantSilos[0..relevantSilosCount];
            Debug.Assert(relevantSilos.Length == relevantSilosCount);

            // Pick K silos from the list of compatible silos, where K is equal to the square root of the number of silos.
            // Eg, from 10 silos, we choose from 4.
            int candidateCount = (int)Math.Ceiling(Math.Sqrt(relevantSilosCount));
            ShufflePrefix(relevantSilos, candidateCount);
            var candidates = relevantSilos[0..candidateCount];

            (int Index, float Score) pick = (0, 1f);

            foreach (var (index, statistics) in candidates)
            {
                float score = CalculateScore(in statistics, maxMaxAvailableMemory, maxActivationCount);

                // It's very unlikely, but there could be more than 1 silo that has the same score,
                // so we apply some jittering to avoid pick the first one in the short-list.
                float scoreJitter = Random.Shared.NextSingle() / 100_000f;

                if (score + scoreJitter < pick.Score)
                {
                    pick = (index, score);
                }
            }

            float? localSiloScore = null;
            if (localSiloStatistics.HasValue && !localSiloStatistics.Value.IsOverloaded)
            {
                var localStats = localSiloStatistics.Value;
                localSiloScore = CalculateScore(in localStats, maxMaxAvailableMemory, maxActivationCount);
            }

            float? directorySiloScore = null;
            if (directorySiloStatistics.HasValue && !directorySiloStatistics.Value.IsOverloaded)
            {
                var directoryStats = directorySiloStatistics.Value;
                directorySiloScore = CalculateScore(in directoryStats, maxMaxAvailableMemory, maxActivationCount);
            }

            return (pick.Index, pick.Score, localSiloScore, directorySiloScore);
        }

        // Variant of the Modern Fisher-Yates shuffle which stops after shuffling the first `prefixLength` elements,
        // which are the only elements we are interested in.
        // See: https://en.wikipedia.org/wiki/Fisher%E2%80%93Yates_shuffle
        static void ShufflePrefix(Span<(int SiloIndex, ResourceStatistics SiloStatistics)> values, int prefixLength)
        {
            Debug.Assert(prefixLength >= 0 && prefixLength <= values.Length);

            var max = values.Length;
            for (var i = 0; i < prefixLength; i++)
            {
                var chosen = Random.Shared.Next(i, max);
                if (chosen != i)
                {
                    (values[chosen], values[i]) = (values[i], values[chosen]);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float CalculateScore(ref readonly ResourceStatistics stats, float maxMaxAvailableMemory, int maxActivationCount)
    {
        float normalizedCpuUsage = stats.CpuUsage / 100f;
        float score = _weights.CpuUsageWeight * normalizedCpuUsage;

        if (stats.MaxAvailableMemory > 0)
        {
            float maxAvailableMemory = stats.MaxAvailableMemory; // cache locally

            float normalizedMemoryUsage = stats.MemoryUsage / maxAvailableMemory;
            float normalizedAvailableMemory = 1 - stats.AvailableMemory / maxAvailableMemory;
            float normalizedMaxAvailableMemory = maxAvailableMemory / maxMaxAvailableMemory;

            score += _weights.MemoryUsageWeight * normalizedMemoryUsage +
                     _weights.AvailableMemoryWeight * normalizedAvailableMemory +
                     _weights.MaxAvailableMemoryWeight * normalizedMaxAvailableMemory;
        }

        score += _weights.ActivationCountWeight * stats.ActivationCount / maxActivationCount;

        Debug.Assert(score >= 0f && score <= 1.01f);

        return score;
    }

    public void RemoveSilo(SiloAddress address)
         => _siloStatistics.TryRemove(address, out _);

    public void SiloStatisticsChangeNotification(SiloAddress address, SiloRuntimeStatistics statistics)
        => _siloStatistics.AddOrUpdate(
            key: address,
            factoryArgument: statistics,
            addValueFactory: static (_, statistics) => ResourceStatistics.FromRuntime(statistics),
            updateValueFactory: static (_, _, statistics) => ResourceStatistics.FromRuntime(statistics));

    private record NormalizedWeights(float CpuUsageWeight, float MemoryUsageWeight, float AvailableMemoryWeight, float MaxAvailableMemoryWeight, float ActivationCountWeight);
    private readonly record struct ResourceStatistics(bool IsOverloaded, float CpuUsage, float MemoryUsage, float AvailableMemory, float MaxAvailableMemory, int ActivationCount)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResourceStatistics FromRuntime(SiloRuntimeStatistics statistics)
            => new(
                IsOverloaded: statistics.IsOverloaded,
                CpuUsage: statistics.EnvironmentStatistics.CpuUsagePercentage,
                MemoryUsage: statistics.EnvironmentStatistics.MemoryUsageBytes,
                AvailableMemory: statistics.EnvironmentStatistics.AvailableMemoryBytes,
                MaxAvailableMemory: statistics.EnvironmentStatistics.MaximumAvailableMemoryBytes,
                ActivationCount: statistics.ActivationCount);
    }
}
