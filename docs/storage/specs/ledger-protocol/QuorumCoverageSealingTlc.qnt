// -*- mode: Bluespec; -*-
// Standalone TLC wrapper for QuorumCoverageSealing so Apalache translates only
// this module without unrelated LedgerProtocol modules in the same file.
module QuorumCoverageSealingTlc {
  import QuorumCoverageSealing(
    AllReplicas = Set(1, 2, 3, 4),
    Mutation = "none"
  ).* from "./QuorumCoverageSealing"
}
