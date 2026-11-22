using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Journaling;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Durable.Storage;

/// <summary>Places a journaled grain on the silo of its journal's primary (orleans.md §5.4).</summary>
[GenerateSerializer]
[Immutable]
[SuppressReferenceTracking]
public sealed class JournalPlacement : PlacementStrategy
{
    /// <summary>Gets the one instance.</summary>
    internal static JournalPlacement Instance { get; } = new();
}

/// <summary>
/// Marks a journaled grain class as placed by <see cref="JournalPlacement"/>. Orleans takes a placement attribute on a base
/// class over this one on the derived class, so a grain class placed by journal inherits no other placement attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class JournalPlacementAttribute : PlacementAttribute
{
    /// <summary>Initializes a new instance of the <see cref="JournalPlacementAttribute"/> class.</summary>
    public JournalPlacementAttribute()
        : base(JournalPlacement.Instance)
    {
    }
}

/// <summary>
/// The placement director of <see cref="JournalPlacement"/>, running on the placing silo over its node's map view and its
/// registry and membership views (orleans.md §5.4): it maps the grain to its journal's row key by Orleans' own mapping,
/// <see cref="JournalId.FromGrainId"/>, the one Orleans' journaled state gives its storage, finds the key's open
/// ledger, and places the grain on the silo the registry names for the ledger's primary, while that entry is active,
/// membership shows the silo <see cref="SiloStatus.Active"/>, and the silo hosts the grain's type, over any placement hint;
/// otherwise as Orleans' random placement does: on the hinted silo when it hosts the grain's type, as a live migration's
/// target is hinted, and else on a random compatible silo.
/// </summary>
/// <remarks>
/// Placement is a hint, read from cached views with no I/O: a grain placed elsewhere works through forwarding
/// (orleans.md §5.5), so a stale view costs a forwarded call per entry, never an answer.
/// </remarks>
internal sealed class JournalPlacementDirector(
    LocalRouting routing,
    NodeRegistryResolver resolver,
    IClusterMembershipService membership) : IPlacementDirector
{
    /// <inheritdoc/>
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatible = context.GetCompatibleSilos(target);
        return Task.FromResult(
            PrimarySilo(target.GrainIdentity, compatible)
            ?? IPlacementDirector.GetPlacementHint(target.RequestContextData, compatible)
            ?? compatible[Random.Shared.Next(compatible.Length)]);
    }

    /// <summary>
    /// Returns the silo of the primary of <paramref name="grain"/>'s journal when the placing silo's views name an active
    /// one among <paramref name="compatible"/>, or <see langword="null"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The grain's journal key is no journal key, which its journal storage refuses too.</exception>
    internal SiloAddress? PrimarySilo(GrainId grain, SiloAddress[] compatible) =>
        routing.Current is { } current
        && current.View.FindOpen(JournalRowKey.Encode(JournalId.FromGrainId(grain).Value).Span) is { } open
        && resolver.Current?.Find(open.Primary.Node) is { IsRetired: false, Silo: var silo }
        && membership.CurrentSnapshot.GetSiloStatus(silo) == SiloStatus.Active
        && Array.IndexOf(compatible, silo) >= 0
            ? silo
            : null;
}
