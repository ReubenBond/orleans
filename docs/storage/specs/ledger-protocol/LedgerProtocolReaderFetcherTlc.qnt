// -*- mode: Bluespec; -*-
// Standalone TLC wrapper for LedgerProtocol (reader + fetcher coverage) so
// Apalache translates one module per file below its 20 MB JSON-RPC cap.
module LedgerProtocolReaderFetcherTlc {
  import LedgerProtocol(
    Replicas = Set(1, 2),
    Quorum = 2,
    Slots = 1,
    Window = 1,
    Recoverers = Set(1),
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
