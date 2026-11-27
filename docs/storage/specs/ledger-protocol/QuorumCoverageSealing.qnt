// -*- mode: Bluespec; -*-
/**
 * Equivalence of the four quorum-coverage definitions in the context of
 * sealing a ledger (paired with QuorumCoverageSealing.tla).
 *
 * Adapted from Jack Vanlightly's QuorumCoverageFencing.tla
 * (https://github.com/Vanlightly/bookkeeper-tlaplus, commit
 * fe199dc717b5723f5308aca74eda6ab3b03917e5), MIT License; see LICENSE.
 *
 * A ledger's seal completes when enough of its replicas are sealed that no
 * quorum of Q replicas can remain unsealed. For each replica the sealer either
 * knows it is sealed (Sealed) or does not know either way because no response
 * arrived yet (Pending) or a timeout/error occurred (Unknown).
 *
 * The "off-by-one" mutant counts N - Q sealed replicas as coverage, which
 * violates Equivalence.
 */
module QuorumCoverageSealing {
  import QuorumCoverage.* from "./QuorumCoverage"

  // Pool of replica IDs; every non-empty cohort and valid quorum 1..|cohort| is explored.
  const AllReplicas: Set[int]
  // "none", or "off-by-one" to test that Equivalence detects a short count.
  const Mutation: str

  type SealResponse = Pending | Sealed | Unknown

  type State = {
    cohortSize: int,
    cohort: Set[int],
    quorum: int,
    responses: int -> SealResponse,
  }

  var s: State

  pure val terminalResponses: Set[SealResponse] = Set(Sealed, Unknown)

  // Every non-empty cohort and valid quorum in the replica pool.
  action init = {
    nondet replicas = AllReplicas.powerset().filter(rs => rs.size() >= 1).oneOf()
    nondet q = 1.to(replicas.size()).oneOf()
    s' = {
      cohortSize: replicas.size(),
      cohort: replicas,
      quorum: q,
      responses: replicas.mapBy(_ => Pending),
    }
  }

  // Receive a seal response from any replica that is still Pending.
  action step = {
    val pending = s.cohort.filter(r => s.responses.get(r) == Pending)
    all {
      pending != Set(),
      nondet r = pending.oneOf()
      nondet res = terminalResponses.oneOf()
      s' = { ...s, responses: s.responses.set(r, res) },
    }
  }

  // Replicas known to be sealed vs. replicas not known to be sealed.
  val sealedReplicas: Set[int] = s.cohort.filter(r => s.responses.get(r) == Sealed)
  val notSealedReplicas: Set[int] = s.cohort.filter(r => s.responses.get(r) != Sealed)

  // Four ways of deciding whether the responses so far reach quorum coverage.
  val enoughSealed1: bool =
    if (Mutation == "off-by-one") sealedReplicas.size() >= s.cohortSize - s.quorum
    else hasQuorumCoverageAffirmative(sealedReplicas, s.cohortSize, s.quorum)

  val enoughSealed2: bool =
    hasQuorumCoverageAffirmative2(sealedReplicas, s.cohort, s.quorum)

  val enoughSealed3: bool =
    hasQuorumCoverageNonAffirmative(notSealedReplicas, s.quorum)

  val enoughSealed4: bool =
    hasQuorumCoverageNonAffirmative2(notSealedReplicas, s.cohort, s.quorum)

  // All four definitions agree in every reachable state.
  val Equivalence: bool =
    (enoughSealed1 == enoughSealed2) and
    (enoughSealed1 == enoughSealed3) and
    (enoughSealed1 == enoughSealed4)
}
