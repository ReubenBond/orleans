// -*- mode: Bluespec; -*-
// Standalone TLC wrapper for LedgerProtocol (concurrent recovery) so Apalache
// translates only one module per file, staying well below its 20 MB JSON-RPC cap.
module LedgerProtocolTlc {
  import LedgerProtocol(
    Replicas = Set(1, 2),
    Quorum = 2,
    Slots = 1,
    Window = 1,
    Recoverers = Set(1, 2),
    Readers = Set(),
    Fetchers = Set(),
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
