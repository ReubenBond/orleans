using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>An open ledger the node is primary of, as its control work knows it (orleans.md §5.9).</summary>
/// <param name="Ledger">The ledger.</param>
/// <param name="AppliedBelow">
/// The slot below which the node's Journals applied every batch to warm state, read first, so it is at most
/// <paramref name="ContiguousTail"/>; <see langword="null"/> before their first write to the ledger.
/// </param>
/// <param name="ContiguousTail">The contiguous tail its writer knows.</param>
/// <param name="Watermark">The drain watermark the node knows, which follows the tail the node's reader learns, so it relates to neither slot above.</param>
/// <param name="Undrained">What its writer proposed in the segments at or above the watermark.</param>
internal sealed record PrimaryObservation(LedgerId Ledger, Slot? AppliedBelow, Slot ContiguousTail, Slot Watermark, LedgerUndrained Undrained);
