// -*- mode: Bluespec; -*-
/**
 * Checked-in Quint model-checking and simulation instances for
 * QuorumCoverageSealing.qnt and LedgerProtocol.qnt (used by GitHub Actions CI).
 */
module QuorumCoverageSealingMC {
  import QuorumCoverageSealing(
    AllReplicas = Set(1, 2, 3, 4),
    Mutation = "none"
  ).* from "./QuorumCoverageSealing"
}

module QuorumCoverageSealingOffByOneMutant {
  import QuorumCoverageSealing(
    AllReplicas = Set(1, 2, 3, 4),
    Mutation = "off-by-one"
  ).* from "./QuorumCoverageSealing"
}

module LedgerProtocolSim {
  import LedgerProtocol(
    Replicas = Set(1, 2, 3),
    Quorum = 2,
    Slots = 3,
    Window = 2,
    Recoverers = Set(1, 2),
    Readers = Set(1),
    Fetchers = Set(1),
    TailWrites = true,
    Faults = Set("lose-node", "corrupt", "read-failure"),
    Duplicates = 1,
    SeparateAnswers = true,
    Mutation = "none"
  ).* from "./LedgerProtocol"

  val safety: bool = and {
    TypeOK,
    AcknowledgedSurvive,
    NoAcknowledgementAtOrAboveEnd,
    EndSealed,
    TombstoneExcludesReplica,
    NoRevokedAnswer,
    ReadersAgree,
    ReadableBelowEnd,
    RecoveriesConverge,
    OpenCheckSound,
    ReadersSeeClose,
    CheckTailBelowEnd,
    BelowTailDecided,
    RecoveryBounded,
    RetainedBelowEnd,
    RefinesAtomicLedger,
  }
}

module LedgerProtocolTlc {
  import LedgerProtocol(
    Replicas = Set(1, 2, 3),
    Quorum = 2,
    Slots = 2,
    Window = 1,
    Recoverers = Set(1, 2),
    Readers = Set(1),
    Fetchers = Set(1),
    TailWrites = false,
    Faults = Set(),
    Duplicates = 0,
    SeparateAnswers = false,
    Mutation = "none"
  ).* from "./LedgerProtocol"

  val safety: bool = and {
    TypeOK,
    AcknowledgedSurvive,
    NoAcknowledgementAtOrAboveEnd,
    EndSealed,
    TombstoneExcludesReplica,
    NoRevokedAnswer,
    ReadersAgree,
    ReadableBelowEnd,
    RecoveriesConverge,
    OpenCheckSound,
    ReadersSeeClose,
    CheckTailBelowEnd,
    BelowTailDecided,
    RecoveryBounded,
    RetainedBelowEnd,
    RefinesAtomicLedger,
  }
}
