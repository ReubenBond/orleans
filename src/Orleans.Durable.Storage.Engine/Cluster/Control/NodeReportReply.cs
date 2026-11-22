using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The controller's reply to a node's report (orleans.md §5.6): the epoch of the newest ledger map the controller has
/// committed or read, to which a node whose view is older refreshes; the closed ledgers the node is to drain too, whose
/// drains have made no progress for <c>ClosedDrainTimeout</c>; and, on archive logs, the node's share of the cluster's
/// archive-log request budget.
/// </summary>
/// <param name="MapEpoch">The newest map epoch the controller knows durable.</param>
/// <param name="DrainToo">The closed ledgers the node drains besides those it drains first.</param>
/// <param name="ArchiveLogShare">On archive logs, the requests a second the node's drains make at most, positive and finite
/// (<see cref="RequestBudgetShares"/>); <see langword="null"/> on the LSM, in the reply to a replaced process's report, while a
/// new controller awaits its nodes' reports, and when the budget has no room, and the node keeps its last share.</param>
/// <param name="ObjectStoreShare">When object-store ledgers are configured, the requests a second the node's object-store
/// operations make at most, positive and finite (orleans.md §5.14); <see langword="null"/> otherwise or when no new share is
/// assigned.</param>
internal sealed record NodeReportReply(ulong MapEpoch, ImmutableArray<LedgerId> DrainToo, double? ArchiveLogShare, double? ObjectStoreShare = null);
