using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Places the controller grain on a silo whose archive gate—and, when object-store ledgers are configured or selected,
/// whose object-ledger gate—passed (orleans.md §5.6, §5.13, §5.14, OR-I20, OL-I1): the controller's archive work and
/// object-ledger sweeps follow the gates of the silo which hosts it.
/// </summary>
[GenerateSerializer]
[Immutable]
[SuppressReferenceTracking]
internal sealed class ControllerPlacement : PlacementStrategy
{
    /// <summary>Gets the one instance.</summary>
    internal static ControllerPlacement Instance { get; } = new();
}

/// <summary>Marks the controller grain's class as placed by <see cref="ControllerPlacement"/>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal sealed class ControllerPlacementAttribute : PlacementAttribute
{
    /// <summary>Initializes a new instance of the <see cref="ControllerPlacementAttribute"/> class.</summary>
    public ControllerPlacementAttribute()
        : base(ControllerPlacement.Instance)
    {
    }
}

/// <summary>
/// The placement director of <see cref="ControllerPlacement"/>, running on the caller's silo: it places the controller grain
/// on that silo when its archive gate (and object-ledger gate, when configured or required by
/// <see cref="ControlOptions.LedgerPlacement"/>) passed, so a healthy caller hosts it locally, and otherwise on another
/// compatible silo, at random, so a silo whose gate is refused, pending, or unconfigured for object-store ledgers never
/// hosts it while another can; the caller's silo is the one place left only when no other is compatible. A placement hint,
/// which a live migration's target carries, names the silo when it is compatible: a migration is an operator's or a test's
/// explicit choice.
/// </summary>
/// <remarks>
/// Placement is a hint read from the caller's own gates, with no I/O: a grain placed on a silo whose gate is later refused
/// deactivates (<see cref="ClusterControllerGrain"/>), and its next activation is placed anew.
/// </remarks>
internal sealed class ControllerPlacementDirector(SiloArchive archive, SiloObjectLedgers? objectLedgers = null, ControlOptions? control = null, NodeRegistryResolver? registry = null) : IPlacementDirector
{
    /// <inheritdoc/>
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatible = context.GetCompatibleSilos(target);
        var needsObjectLedgers = control?.LedgerPlacement == LedgerPlacement.ObjectStore
            || objectLedgers is { IsConfigured: true }
            || registry?.Current?.ObjectLedgerStore is not null;
        var objectLedgersOk = !needsObjectLedgers || objectLedgers is { IsConfigured: true } && objectLedgers.Gate.IsPassed;
        var passed = archive.Gate.IsPassed && objectLedgersOk;
        return Task.FromResult(
            IPlacementDirector.GetPlacementHint(target.RequestContextData, compatible)
            ?? Choose(passed, context.LocalSilo, compatible, Random.Shared));
    }

    /// <summary>
    /// Returns <paramref name="local"/> when its gate passed and it is compatible, and otherwise a random silo of
    /// <paramref name="compatible"/> other than it, or it when no other is compatible.
    /// </summary>
    internal static SiloAddress Choose(bool passed, SiloAddress local, SiloAddress[] compatible, Random random)
    {
        var isCompatible = Array.IndexOf(compatible, local) >= 0;
        if (passed && isCompatible)
        {
            return local;
        }

        var others = Array.FindAll(compatible, silo => !silo.Equals(local));
        return others.Length > 0 ? others[random.Next(others.Length)] : local;
    }
}
