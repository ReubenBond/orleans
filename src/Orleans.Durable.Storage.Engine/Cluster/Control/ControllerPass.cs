using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// What one controller pass decided (orleans.md §5.3, §5.6): the transitions to send, the compactions to assign, the
/// recorded retirements to submit to the node registry, the retirement floor to raise the ledger map to, and whether its
/// registry view lies below the map's floor. The host sends each without awaiting its reply, and reports each reply back.
/// </summary>
/// <param name="Transitions">The transitions to send, each now in progress.</param>
/// <param name="Compactions">The compactions to assign, each now in progress.</param>
/// <param name="Retirements">The recorded retirements which the controller's registry view does not show retired, none in
/// flight, which the host submits to the node registry committer; each is in flight until the host reports it settled or
/// failed.</param>
/// <param name="FloorRaise">The newest retirement in the controller's registry view, when it lies above its map view's
/// floor and no raise is in flight, which the host submits as <c>RaiseFloor</c> to the ledger map committer, reporting its
/// end; otherwise <see langword="null"/>.</param>
/// <param name="RegistryBehind">Whether the map's floor lies above the registry view's revision, so the pass sent no
/// transition: the host reads the registry.</param>
/// <param name="CompleteDrainAllRevision">The active <c>DrainAllRevision</c> whose phase 2 completion the host submits
/// once to the node registry committer (atomically retiring every active diskful node and resetting
/// <c>DrainAllRevision</c> to 0) when zero node-backed ledgers remain, the map's floor covers
/// <c>DrainAllRevision</c>, and no completion is in flight; otherwise <see langword="null"/>.</param>
/// <param name="PrepareReleases">Cooperative <c>PrepareRelease</c> requests to send to outgoing primaries before planned
/// replacements, each now in progress until its reply or timeout is reported back (orleans.md §5.6, OR-I24).</param>
internal sealed record ControllerPass(
    ImmutableArray<SentTransition> Transitions,
    ImmutableArray<CompactionAssignment> Compactions,
    ImmutableArray<NodeId> Retirements,
    ulong? FloorRaise,
    bool RegistryBehind,
    ulong? CompleteDrainAllRevision = null,
    ImmutableArray<SentPrepareRelease> PrepareReleases = default)
{
    /// <summary>Gets the prepare-release requests to send, never default.</summary>
    public ImmutableArray<SentPrepareRelease> PrepareReleasesOrEmpty => PrepareReleases.IsDefault ? [] : PrepareReleases;
}
