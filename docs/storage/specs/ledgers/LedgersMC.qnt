// -*- mode: Bluespec; -*-
/**
 * Checked-in Quint simulation and TLC instances for LedgerMap.qnt and
 * LedgerDrop.qnt (used by GitHub Actions CI).
 */
module LedgerMapSim {
  import LedgerMap(
    KeyBits = 2,
    MaxLedgers = 5,
    MaxJsn = 2,
    MaxMarkers = 1,
    Mutation = "none"
  ).* from "./LedgerMap"

  val safety: bool = and {
    OneOwner,
    IdsDense,
    HistoryOrdered,
    AckedInHistory,
    DropsInLineageOrder,
    ReadsSeeAcked,
    ReferencesResolve,
    TrimsAtSegmentStarts,
    TokensReplayPlan,
  }
}

module LedgerMapTlc {
  import LedgerMap(
    KeyBits = 1,
    MaxLedgers = 4,
    MaxJsn = 1,
    MaxMarkers = 1,
    Mutation = "none"
  ).* from "./LedgerMap"

  val safety: bool = and {
    OneOwner,
    IdsDense,
    HistoryOrdered,
    AckedInHistory,
    DropsInLineageOrder,
    ReadsSeeAcked,
    ReferencesResolve,
    TrimsAtSegmentStarts,
    TokensReplayPlan,
  }
}

module LedgerDropSim {
  import LedgerDrop(
    Replicas = Set(1, 2, 3),
    Quorum = 2,
    Mutation = "none",
    Crashes = true
  ).* from "./LedgerDrop"

  val safety: bool = and {
    DecisionsAgree,
    DurableIsDecided,
    FencedHasNoEffect,
    EndAddressSealed,
    NeverDropsListed,
    TombstoneExcludesReplica,
    DroppedIsFinal,
  }
}

module LedgerDropTlc {
  import LedgerDrop(
    Replicas = Set(1, 2, 3),
    Quorum = 2,
    Mutation = "none",
    Crashes = true
  ).* from "./LedgerDrop"

  val safety: bool = and {
    DecisionsAgree,
    DurableIsDecided,
    FencedHasNoEffect,
    EndAddressSealed,
    NeverDropsListed,
    TombstoneExcludesReplica,
    DroppedIsFinal,
  }
}
