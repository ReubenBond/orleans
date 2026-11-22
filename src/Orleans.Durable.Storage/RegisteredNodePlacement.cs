using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Durable.Storage;

/// <summary>
/// Places a node's grains, keyed by its node identity, only on the silo the node registry names for the node, while
/// membership shows that silo <see cref="SiloStatus.Active"/> (orleans.md §5.3, §5.12, R1).
/// </summary>
[GenerateSerializer]
[Immutable]
[SuppressReferenceTracking]
internal sealed class RegisteredNodePlacement : PlacementStrategy
{
    /// <summary>Gets the one instance.</summary>
    internal static RegisteredNodePlacement Instance { get; } = new();
}

/// <summary>Marks a grain class, keyed by a node identity, as placed by <see cref="RegisteredNodePlacement"/>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal sealed class RegisteredNodePlacementAttribute : PlacementAttribute
{
    /// <summary>Initializes a new instance of the <see cref="RegisteredNodePlacementAttribute"/> class.</summary>
    public RegisteredNodePlacementAttribute()
        : base(RegisteredNodePlacement.Instance)
    {
    }
}

/// <summary>
/// The placement director of <see cref="RegisteredNodePlacement"/>, running on the caller's silo over its registry and
/// membership views: it returns the node's registered silo while the node is active and membership shows the silo active,
/// and otherwise refuses with <see cref="NodePlacementRefusedException"/>, before any activation exists, and requests a
/// re-read when the cache is stale against membership.
/// </summary>
/// <remarks>
/// It places on the registered silo even when the placing silo's grain-manifest view omits it, as it can for a silo
/// restarted at the same endpoint: every silo defines the Durable Storage grains. It decides from the cached registry alone, and
/// refuses before the first read lands.
/// </remarks>
internal sealed partial class RegisteredNodePlacementDirector(
    NodeRegistryResolver resolver,
    IClusterMembershipService membership,
    ILogger<RegisteredNodePlacementDirector> logger) : IPlacementDirector
{
    /// <inheritdoc/>
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var key = target.GrainIdentity.GetGuidKey();
        if (key == Guid.Empty)
        {
            return Task.FromException<SiloAddress>(new ArgumentException("A node's grain is keyed by its node identity.", nameof(target)));
        }

        var node = new NodeId(key);
        var silo = resolver.Resolve(node);
        if (silo is not null && membership.CurrentSnapshot.GetSiloStatus(silo) == SiloStatus.Active)
        {
            return Task.FromResult(silo);
        }

        LogRefused(logger, node, silo, resolver.Current?.Revision);
        resolver.RequestRereadIfStale();
        return Task.FromException<SiloAddress>(new NodePlacementRefusedException(node, silo));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Node {Node} has no registered active silo (registered: {Silo}, registry revision {Revision}); placement refused.")]
    private static partial void LogRefused(ILogger logger, NodeId node, SiloAddress? silo, ulong? revision);
}
