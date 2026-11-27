// -*- mode: Bluespec; -*-
/**
 * Four equivalent ways of measuring quorum coverage.
 *
 * Adapted from Jack Vanlightly's QuorumCoverage.tla
 * (https://github.com/Vanlightly/bookkeeper-tlaplus, commit
 * fe199dc717b5723f5308aca74eda6ab3b03917e5), MIT License; see LICENSE.
 * Paired with QuorumCoverage.tla in Clog's terms: the cohort is a ledger's
 * N replicas and the ack quorum is its quorum Q (Clog has no striping, so
 * every position's write set is the whole cohort).
 *
 * The four definitions can be classified as either:
 * - measuring the set of replicas that satisfy a property (affirmative 1 and 2)
 * - measuring the set of replicas that do not satisfy a property (non-affirmative 1 and 2)
 *
 * Definitions:
 * - Affirmative 1: at least (cohortSize - q) + 1 replicas satisfy the property
 * - Affirmative 2: at least one replica in every quorum of size q satisfies the property
 * - Non-affirmative 1: fewer than q replicas do not satisfy the property
 * - Non-affirmative 2: no quorum of size q consists solely of replicas failing the property
 */
module QuorumCoverage {
  pure def hasQuorumCoverageAffirmative(
    satisfies: Set[int],
    cohortSize: int,
    q: int
  ): bool =
    satisfies.size() >= (cohortSize - q) + 1

  pure def hasQuorumCoverageAffirmative2(
    satisfies: Set[int],
    cohort: Set[int],
    q: int
  ): bool =
    satisfies != Set() and
    cohort.powerset().filter(quorum => quorum.size() == q).forall(quorum =>
      satisfies.intersect(quorum) != Set()
    )

  pure def hasQuorumCoverageNonAffirmative(
    notSatisfies: Set[int],
    q: int
  ): bool =
    notSatisfies.size() < q

  pure def hasQuorumCoverageNonAffirmative2(
    notSatisfies: Set[int],
    cohort: Set[int],
    q: int
  ): bool =
    not(cohort.powerset().filter(quorum => quorum.size() == q).exists(quorum =>
      quorum.subseteq(notSatisfies)
    ))
}
