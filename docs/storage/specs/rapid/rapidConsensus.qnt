// -*- mode: Bluespec; -*-
/**
 * Rapid membership cut detection and single-decree Fast + Classic Paxos
 * (docs/design/rapid.md).
 *
 * Models two coupled components for a fixed view of N members:
 * 1. CutDetector: K-ring expander alert aggregation with H/L watermarks,
 *    correlated-observer implicit edge invalidation, and unstable-mode cut
 *    stability.
 * 2. ConsensusCoordinator + PaxosAcceptor + PaxosLearner: leaderless 1-RTT
 *    Fast Paxos at rank (1, 0) with quorum N - f (f = floor((N - 1) / 4)),
 *    falling back to Classic Paxos at ranks (r >= 2, node) with majority
 *    quorum floor(N / 2) + 1 and safe fast-round value recovery.
 *
 * Mutants disable one guard at a time and are verified via ../check-mutants.sh.
 */
module rapidConsensus {
  // ---------------------------------------------------------------------------
  // Parameters
  // ---------------------------------------------------------------------------
  const N: int                // view size (e.g. 5 => f=1, fastQ=4, classicQ=3)
  const MAX_ROUND: int        // max classic round (e.g. 2)
  const CLASSIC_LEADERS: Set[int] // subset of MEMBERS acting as classic proposers
  const PROPOSALS: Set[int]   // non-zero candidate cut proposals (e.g. 1..2)
  const K_EFF: int            // effective expander rings (e.g. 4 for N=5)
  const H_EFF: int            // high watermark (e.g. 3)
  const L_EFF: int            // low watermark (e.g. 1)

  pure val MEMBERS: Set[int] = 1.to(N)
  pure val RINGS: Set[int] = 0.to(K_EFF - 1)
  pure val F: int = (N - 1) / 4
  pure val FAST_QUORUM: int = N - F
  pure val CLASSIC_QUORUM: int = (N / 2) + 1

  type Rank = { round: int, node: int }
  pure val ZERO_RANK: Rank = { round: 0, node: 0 }
  pure val FAST_RANK: Rank = { round: 1, node: 0 }

  type FastVote = { sender: int, proposal: int }
  type Phase1bMsg = { sender: int, rnd: Rank, vrnd: Rank, vval: int }
  type Phase2aMsg = { rnd: Rank, proposal: int }
  type Phase2bMsg = { sender: int, rnd: Rank, proposal: int }

  pure def rankGt(a: Rank, b: Rank): bool =
    a.round > b.round or (a.round == b.round and a.node > b.node)

  pure def rankGeq(a: Rank, b: Rank): bool =
    a == b or rankGt(a, b)

  pure val CLASSIC_RANKS: Set[Rank] =
    tuples(2.to(MAX_ROUND), CLASSIC_LEADERS).map(((r, n)) => { round: r, node: n })

  // Deterministic K-ring observer mapping: on ring r, observer of subject s is
  // ((s + r) % N) + 1, yielding K_EFF distinct observers per subject.
  pure def observerOf(subject: int, ring: int): int =
    ((subject + ring) % N) + 1

  // ---------------------------------------------------------------------------
  // Consensus state
  // ---------------------------------------------------------------------------
  var promised: int -> Rank
  var acceptedRank: int -> Rank
  var acceptedVal: int -> int        // 0 = None
  var fastVotes: Set[FastVote]
  var phase1b: Set[Phase1bMsg]
  var phase2a: Set[Phase2aMsg]
  var phase2b: Set[Phase2bMsg]
  var decided: Set[int]

  // ---------------------------------------------------------------------------
  // CutDetector state (single local detector for view of size N)
  // ---------------------------------------------------------------------------
  var reports: int -> Set[int]       // subject -> set of rings that reported it
  var preProposal: Set[int]          // subjects with L_EFF <= reports < H_EFF
  var proposalSet: Set[int]          // subjects with reports >= H_EFF
  var emittedCuts: Set[Set[int]]     // cuts emitted via normal watermark stability
  var badCutEmitted: bool            // set if emitted while unstable or under-threshold

  // ---------------------------------------------------------------------------
  // Init
  // ---------------------------------------------------------------------------
  action init = all {
    promised' = MEMBERS.mapBy(_ => ZERO_RANK),
    acceptedRank' = MEMBERS.mapBy(_ => ZERO_RANK),
    acceptedVal' = MEMBERS.mapBy(_ => 0),
    fastVotes' = Set(),
    phase1b' = Set(),
    phase2a' = Set(),
    phase2b' = Set(),
    decided' = Set(),
    reports' = MEMBERS.mapBy(_ => Set()),
    preProposal' = Set(),
    proposalSet' = Set(),
    emittedCuts' = Set(),
    badCutEmitted' = false,
  }

  action keepConsensus = all {
    promised' = promised,
    acceptedRank' = acceptedRank,
    acceptedVal' = acceptedVal,
    fastVotes' = fastVotes,
    phase1b' = phase1b,
    phase2a' = phase2a,
    phase2b' = phase2b,
    decided' = decided,
  }

  action keepCut = all {
    reports' = reports,
    preProposal' = preProposal,
    proposalSet' = proposalSet,
    emittedCuts' = emittedCuts,
    badCutEmitted' = badCutEmitted,
  }

  // ---------------------------------------------------------------------------
  // Consensus helpers
  // ---------------------------------------------------------------------------
  pure def maxRankOf(msgs: Set[Phase1bMsg]): Rank =
    msgs.fold(ZERO_RANK, (best, m) => if (rankGt(m.vrnd, best)) m.vrnd else best)

  // Fast-round recovery rule (docs/design/rapid.md §4):
  // Given a classic quorum of Phase1b messages, choose a value at maxVrnd.
  // If maxVrnd == FAST_RANK and any value has count > N / 4 (strictly > F),
  // that unique value MUST be selected. Otherwise any candidate is safe.
  pure def safeChooseValues(quorumMsgs: Set[Phase1bMsg], fallback: int): Set[int] = {
    val maxVrnd = maxRankOf(quorumMsgs)
    if (maxVrnd == ZERO_RANK) {
      Set(fallback)
    } else {
      val atMax = quorumMsgs.filter(m => m.vrnd == maxVrnd and m.vval != 0)
      val vals = atMax.map(m => m.vval)
      if (maxVrnd.round >= 2 or vals.size() <= 1) {
        if (vals.size() == 0) Set(fallback) else vals
      } else {
        val superVals = vals.filter(v => atMax.filter(m => m.vval == v).size() > N / 4)
        if (superVals.size() > 0) superVals else vals.union(Set(fallback))
      }
    }
  }

  // ---------------------------------------------------------------------------
  // Consensus actions
  // ---------------------------------------------------------------------------

  // Round 1 (Fast Paxos): acceptor votes once in (1, 0) if not yet promised > 1.
  action fastVote(a: int, p: int): bool = all {
    a.in(MEMBERS),
    p.in(PROPOSALS),
    promised.get(a).round <= 1,
    not(fastVotes.exists(v => v.sender == a)),
    promised' = promised.set(a, FAST_RANK),
    acceptedRank' = acceptedRank.set(a, FAST_RANK),
    acceptedVal' = acceptedVal.set(a, p),
    fastVotes' = fastVotes.union(Set({ sender: a, proposal: p })),
    phase1b' = phase1b,
    phase2a' = phase2a,
    phase2b' = phase2b,
    decided' = decided,
    keepCut,
  }

  // Fast decision: >= N - F distinct voters for the same proposal at (1, 0).
  action fastDecide(p: int): bool = {
    val voters = fastVotes.filter(v => v.proposal == p).map(v => v.sender)
    all {
      p.in(PROPOSALS),
      voters.size() >= FAST_QUORUM,
      decided' = decided.union(Set(p)),
      promised' = promised,
      acceptedRank' = acceptedRank,
      acceptedVal' = acceptedVal,
      fastVotes' = fastVotes,
      phase1b' = phase1b,
      phase2a' = phase2a,
      phase2b' = phase2b,
      keepCut,
    }
  }

  // Classic Phase 1a -> 1b: acceptor processes Prepare(r).
  action classicPrepare(a: int, r: Rank): bool = {
    val newPromised = if (rankGt(r, promised.get(a))) r else promised.get(a)
    val reply: Phase1bMsg = {
      sender: a,
      rnd: newPromised,
      vrnd: acceptedRank.get(a),
      vval: acceptedVal.get(a),
    }
    all {
      a.in(MEMBERS),
      r.in(CLASSIC_RANKS),
      promised' = promised.set(a, newPromised),
      acceptedRank' = acceptedRank,
      acceptedVal' = acceptedVal,
      fastVotes' = fastVotes,
      phase1b' = phase1b.union(Set(reply)),
      phase2a' = phase2a,
      phase2b' = phase2b,
      decided' = decided,
      keepCut,
    }
  }

  // Classic Phase 2a: proposer for rank r collects a classic quorum of matching
  // Phase1b replies and broadcasts one immutable Phase2a(r, chosen).
  action classicPropose(r: Rank, chosen: int): bool = {
    val matching = phase1b.filter(m => m.rnd == r)
    all {
      r.in(CLASSIC_RANKS),
      not(phase2a.exists(m => m.rnd == r)),
      matching.map(m => m.sender).size() >= CLASSIC_QUORUM,
      nondet quorumSenders = matching.map(m => m.sender).powerset()
        .filter(qs => qs.size() == CLASSIC_QUORUM).oneOf()
      val quorumMsgs = matching.filter(m => m.sender.in(quorumSenders))
      nondet fallback = PROPOSALS.oneOf()
      all {
        chosen.in(safeChooseValues(quorumMsgs, fallback)),
        phase2a' = phase2a.union(Set({ rnd: r, proposal: chosen })),
      },
      promised' = promised,
      acceptedRank' = acceptedRank,
      acceptedVal' = acceptedVal,
      fastVotes' = fastVotes,
      phase1b' = phase1b,
      phase2b' = phase2b,
      decided' = decided,
      keepCut,
    }
  }

  // Classic Phase 2a -> 2b: acceptor accepts if r >= promised[a].
  action classicAccept(a: int, msg: Phase2aMsg): bool = all {
    a.in(MEMBERS),
    msg.in(phase2a),
    rankGeq(msg.rnd, promised.get(a)),
    promised' = promised.set(a, msg.rnd),
    acceptedRank' = acceptedRank.set(a, msg.rnd),
    acceptedVal' = acceptedVal.set(a, msg.proposal),
    phase2b' = phase2b.union(Set({ sender: a, rnd: msg.rnd, proposal: msg.proposal })),
    fastVotes' = fastVotes,
    phase1b' = phase1b,
    phase2a' = phase2a,
    decided' = decided,
    keepCut,
  }

  // Classic decision: >= floor(N / 2) + 1 distinct acceptors at same classic rank.
  action classicDecide(r: Rank, p: int): bool = {
    val voters = phase2b.filter(m => m.rnd == r and m.proposal == p).map(m => m.sender)
    all {
      r.in(CLASSIC_RANKS),
      p.in(PROPOSALS),
      voters.size() >= CLASSIC_QUORUM,
      decided' = decided.union(Set(p)),
      promised' = promised,
      acceptedRank' = acceptedRank,
      acceptedVal' = acceptedVal,
      fastVotes' = fastVotes,
      phase1b' = phase1b,
      phase2a' = phase2a,
      phase2b' = phase2b,
      keepCut,
    }
  }

  // ---------------------------------------------------------------------------
  // CutDetector actions
  // ---------------------------------------------------------------------------
  action cutAlert(subject: int, ring: int): bool = {
    val prev = reports.get(subject)
    val nextReports = prev.union(Set(ring))
    val nextCount = nextReports.size()
    val nextPre =
      if (nextCount >= L_EFF and nextCount < H_EFF) preProposal.union(Set(subject))
      else preProposal.exclude(Set(subject))
    val nextProp =
      if (nextCount >= H_EFF) proposalSet.union(Set(subject))
      else proposalSet
    all {
      subject.in(MEMBERS),
      ring.in(RINGS),
      not(prev.contains(ring)),
      reports' = reports.set(subject, nextReports),
      preProposal' = nextPre,
      proposalSet' = nextProp,
      emittedCuts' = emittedCuts,
      badCutEmitted' = badCutEmitted,
      keepConsensus,
    }
  }

  // Correlated observer failure invalidation: if observer of s on ring r is
  // itself in proposalSet or preProposal, synthesize an implicit report.
  action cutInvalidateFailingEdge(subject: int, ring: int): bool = {
    val obs = observerOf(subject, ring)
    val inFluxOrProp = preProposal.union(proposalSet)
    val prev = reports.get(subject)
    val nextReports = prev.union(Set(ring))
    val nextCount = nextReports.size()
    val nextPre =
      if (nextCount >= L_EFF and nextCount < H_EFF) preProposal.union(Set(subject))
      else preProposal.exclude(Set(subject))
    val nextProp =
      if (nextCount >= H_EFF) proposalSet.union(Set(subject))
      else proposalSet
    all {
      subject.in(preProposal),
      ring.in(RINGS),
      not(prev.contains(ring)),
      obs.in(inFluxOrProp),
      reports' = reports.set(subject, nextReports),
      preProposal' = nextPre,
      proposalSet' = nextProp,
      emittedCuts' = emittedCuts,
      badCutEmitted' = badCutEmitted,
      keepConsensus,
    }
  }

  // Normal watermark-stable cut emission: fires only when preProposal is empty.
  action cutEmit: bool = all {
    preProposal == Set(),
    proposalSet != Set(),
    emittedCuts' = emittedCuts.union(Set(proposalSet)),
    proposalSet' = Set(),
    preProposal' = preProposal,
    reports' = reports,
    badCutEmitted' = badCutEmitted,
    keepConsensus,
  }

  // ---------------------------------------------------------------------------
  // Next-state relation
  // ---------------------------------------------------------------------------
  // Consensus and CutDetector operate on disjoint state variables (each keeps
  // the other's variables unchanged), so TLC can exhaust each component
  // independently without forming their Cartesian product.
  action stepConsensus = any {
    { nondet a = MEMBERS.oneOf(); nondet p = PROPOSALS.oneOf(); fastVote(a, p) },
    { nondet p = PROPOSALS.oneOf(); fastDecide(p) },
    { nondet a = MEMBERS.oneOf(); nondet r = CLASSIC_RANKS.oneOf(); classicPrepare(a, r) },
    { nondet r = CLASSIC_RANKS.oneOf(); nondet p = PROPOSALS.oneOf(); classicPropose(r, p) },
    { nondet a = MEMBERS.oneOf(); nondet msg = phase2a.oneOf(); classicAccept(a, msg) },
    { nondet r = CLASSIC_RANKS.oneOf(); nondet p = PROPOSALS.oneOf(); classicDecide(r, p) },
  }

  action stepCut = any {
    { nondet s = MEMBERS.oneOf(); nondet ring = RINGS.oneOf(); cutAlert(s, ring) },
    { nondet s = MEMBERS.oneOf(); nondet ring = RINGS.oneOf(); cutInvalidateFailingEdge(s, ring) },
    cutEmit,
  }

  action step = any {
    stepConsensus,
    stepCut,
  }

  // ---------------------------------------------------------------------------
  // Safety invariants
  // ---------------------------------------------------------------------------

  // At most one proposal is ever decided across Fast and Classic rounds.
  val agreement: bool = decided.size() <= 1

  // Only valid non-zero proposals are ever decided.
  val validity: bool = decided.subseteq(PROPOSALS)

  // Every classic rank proposes and accepts at most one value.
  val classicUniqueness: bool = and {
    CLASSIC_RANKS.forall(r => phase2a.filter(m => m.rnd == r).map(m => m.proposal).size() <= 1),
    CLASSIC_RANKS.forall(r => phase2b.filter(m => m.rnd == r).map(m => m.proposal).size() <= 1),
  }

  // If a proposal reached fast quorum in round 1, every classic Phase2a must pick it.
  val fastDecisionPreserved: bool =
    PROPOSALS.forall(p =>
      (fastVotes.filter(v => v.proposal == p).map(v => v.sender).size() >= FAST_QUORUM)
        implies phase2a.forall(m => m.proposal == p)
    )

  // Normal cut emission never emits while any subject is unstable (L <= count < H)
  // and every subject in an emitted cut reached H_EFF distinct ring reports.
  val cutStability: bool = and {
    not(badCutEmitted),
    emittedCuts.forall(cut =>
      cut != Set() and cut.forall(s => reports.get(s).size() >= H_EFF)
    ),
    preProposal.intersect(proposalSet) == Set(),
  }

  val consensusSafety: bool = and {
    agreement,
    validity,
    classicUniqueness,
    fastDecisionPreserved,
  }

  val cutSafety: bool = cutStability

  val safety: bool = and {
    consensusSafety,
    cutSafety,
  }

  // ---------------------------------------------------------------------------
  // Mutants (each disables one critical guard; checked by ../check-mutants.sh)
  // ---------------------------------------------------------------------------

  // breaks: agreement
  action mutantUndersizedFastQuorum = any {
    step,
    {
      nondet p = PROPOSALS.oneOf()
      val voters = fastVotes.filter(v => v.proposal == p).map(v => v.sender)
      all {
        voters.size() >= FAST_QUORUM - 1,
        decided' = decided.union(Set(p)),
        promised' = promised,
        acceptedRank' = acceptedRank,
        acceptedVal' = acceptedVal,
        fastVotes' = fastVotes,
        phase1b' = phase1b,
        phase2a' = phase2a,
        phase2b' = phase2b,
        keepCut,
      }
    },
  }

  // breaks: fastDecisionPreserved
  action mutantArbitraryFastRecovery = any {
    step,
    {
      nondet r = CLASSIC_RANKS.oneOf()
      val matching = phase1b.filter(m => m.rnd == r)
      all {
        not(phase2a.exists(m => m.rnd == r)),
        matching.map(m => m.sender).size() >= CLASSIC_QUORUM,
        nondet quorumSenders = matching.map(m => m.sender).powerset()
          .filter(qs => qs.size() == CLASSIC_QUORUM).oneOf()
        val quorumMsgs = matching.filter(m => m.sender.in(quorumSenders))
        val maxVrnd = maxRankOf(quorumMsgs)
        val atMax = quorumMsgs.filter(m => m.vrnd == maxVrnd and m.vval != 0).map(m => m.vval)
        all {
          maxVrnd == FAST_RANK,
          atMax.size() > 1,
          // Bug: picks any value at maxVrnd instead of the count > N / 4 winner.
          nondet chosen = atMax.oneOf()
          phase2a' = phase2a.union(Set({ rnd: r, proposal: chosen })),
        },
        promised' = promised,
        acceptedRank' = acceptedRank,
        acceptedVal' = acceptedVal,
        fastVotes' = fastVotes,
        phase1b' = phase1b,
        phase2b' = phase2b,
        decided' = decided,
        keepCut,
      }
    },
  }

  // breaks: agreement
  action mutantAcceptIgnoresPromise = any {
    step,
    {
      nondet a = MEMBERS.oneOf()
      nondet msg = phase2a.oneOf()
      all {
        // Bug: omits rankGeq(msg.rnd, promised.get(a)).
        promised' = promised.set(a, msg.rnd),
        acceptedRank' = acceptedRank.set(a, msg.rnd),
        acceptedVal' = acceptedVal.set(a, msg.proposal),
        phase2b' = phase2b.union(Set({ sender: a, rnd: msg.rnd, proposal: msg.proposal })),
        fastVotes' = fastVotes,
        phase1b' = phase1b,
        phase2a' = phase2a,
        decided' = decided,
        keepCut,
      }
    },
  }

  // breaks: fastDecisionPreserved
  action mutantFastVoteAfterClassicPromise = any {
    step,
    {
      nondet a = MEMBERS.oneOf()
      nondet p = PROPOSALS.oneOf()
      all {
        // Bug: allows fast vote even after acceptor promised a classic round >= 2.
        promised.get(a).round >= 2,
        not(fastVotes.exists(v => v.sender == a)),
        fastVotes' = fastVotes.union(Set({ sender: a, proposal: p })),
        promised' = promised,
        acceptedRank' = acceptedRank,
        acceptedVal' = acceptedVal,
        phase1b' = phase1b,
        phase2a' = phase2a,
        phase2b' = phase2b,
        decided' = decided,
        keepCut,
      }
    },
  }

  // breaks: agreement
  action mutantUndersizedClassicQuorum = any {
    step,
    {
      nondet r = CLASSIC_RANKS.oneOf()
      nondet p = PROPOSALS.oneOf()
      val voters = phase2b.filter(m => m.rnd == r and m.proposal == p).map(m => m.sender)
      all {
        voters.size() >= CLASSIC_QUORUM - 1,
        decided' = decided.union(Set(p)),
        promised' = promised,
        acceptedRank' = acceptedRank,
        acceptedVal' = acceptedVal,
        fastVotes' = fastVotes,
        phase1b' = phase1b,
        phase2a' = phase2a,
        phase2b' = phase2b,
        keepCut,
      }
    },
  }

  // breaks: cutStability
  action mutantEmitDuringUnstableCut = any {
    step,
    all {
      // Bug: emits proposalSet even while preProposal is non-empty.
      preProposal != Set(),
      proposalSet != Set(),
      emittedCuts' = emittedCuts.union(Set(proposalSet)),
      proposalSet' = Set(),
      preProposal' = preProposal,
      reports' = reports,
      badCutEmitted' = true,
      keepConsensus,
    },
  }
}

module rapidConsensusSim {
  import rapidConsensus(
    N = 5,
    MAX_ROUND = 2,
    CLASSIC_LEADERS = Set(1, 2),
    PROPOSALS = Set(1, 2),
    K_EFF = 4,
    H_EFF = 3,
    L_EFF = 1
  ).*

  // Directed scenario 1: Fast-round 1-RTT decision (4 of 5 agree on proposal 1)
  // followed by a competing classic round 2 recovering proposal 1.
  run fastDecisionRecoveredByClassicTest =
    init
      .then(fastVote(1, 1))
      .then(fastVote(2, 1))
      .then(fastVote(3, 1))
      .then(fastVote(4, 1))
      .then(fastVote(5, 2))
      .then(fastDecide(1))
      .then(classicPrepare(3, { round: 2, node: 1 }))
      .then(classicPrepare(4, { round: 2, node: 1 }))
      .then(classicPrepare(5, { round: 2, node: 1 }))
      .then(classicPropose({ round: 2, node: 1 }, 1))
      .then(classicAccept(3, { rnd: { round: 2, node: 1 }, proposal: 1 }))
      .then(classicAccept(4, { rnd: { round: 2, node: 1 }, proposal: 1 }))
      .then(classicAccept(5, { rnd: { round: 2, node: 1 }, proposal: 1 }))
      .then(classicDecide({ round: 2, node: 1 }, 1))
      .expect(safety and decided == Set(1))

  // Directed scenario 2: Split fast round (3 for p1, 2 for p2 — neither reaches
  // fast quorum 4) resolved cleanly by classic round 2.
  run splitFastRoundResolvedByClassicTest =
    init
      .then(fastVote(1, 1))
      .then(fastVote(2, 1))
      .then(fastVote(3, 1))
      .then(fastVote(4, 2))
      .then(fastVote(5, 2))
      .then(classicPrepare(3, { round: 2, node: 2 }))
      .then(classicPrepare(4, { round: 2, node: 2 }))
      .then(classicPrepare(5, { round: 2, node: 2 }))
      .then(classicPropose({ round: 2, node: 2 }, 2))
      .then(classicAccept(3, { rnd: { round: 2, node: 2 }, proposal: 2 }))
      .then(classicAccept(4, { rnd: { round: 2, node: 2 }, proposal: 2 }))
      .then(classicAccept(5, { rnd: { round: 2, node: 2 }, proposal: 2 }))
      .then(classicDecide({ round: 2, node: 2 }, 2))
      .expect(safety and decided == Set(2))

  // Directed scenario 3: CutDetector holds emission while a second subject is
  // unstable (L=1 <= reports < H=3), then uses correlated-observer invalidation
  // so both subjects emit together as one multi-process cut.
  run cutDetectorCorrelatedFailureTest =
    init
      // Subject 2 gets 3 alerts (reaches H=3 -> enters proposalSet)
      .then(cutAlert(2, 0))
      .then(cutAlert(2, 1))
      .then(cutAlert(2, 2))
      // Subject 1 gets 2 alerts (in preProposal: blocks emission of {2} alone)
      // Note observerOf(1, 0) == 2, which is already in proposalSet!
      .then(cutAlert(1, 1))
      .then(cutAlert(1, 2))
      // Invalidate failing observer 2 on ring 0 -> pushes subject 1 to H=3
      .then(cutInvalidateFailingEdge(1, 0))
      .then(cutEmit)
      .expect(safety and emittedCuts == Set(Set(1, 2)))
}

module rapidConsensusTlc {
  // Compact N=5 Fast + Classic Paxos recovery (fastQ=4, classicQ=3, 2 proposals)
  // for exhaustive TLC verification of consensusSafety (via --step=stepConsensus).
  import rapidConsensus(
    N = 5,
    MAX_ROUND = 2,
    CLASSIC_LEADERS = Set(1),
    PROPOSALS = Set(1, 2),
    K_EFF = 4,
    H_EFF = 3,
    L_EFF = 1
  ).*
}

module rapidConsensusClassicTlc {
  // Compact N=3 competing classic leaders in round 2 for exhaustive TLC
  // verification of consensusSafety (via --step=stepConsensus).
  import rapidConsensus(
    N = 3,
    MAX_ROUND = 2,
    CLASSIC_LEADERS = Set(1, 2),
    PROPOSALS = Set(1, 2),
    K_EFF = 2,
    H_EFF = 2,
    L_EFF = 1
  ).*
}

module rapidCutDetectorTlc {
  // Compact N=4 K=3 H=2 L=1 cut detector for exhaustive TLC verification of
  // cutSafety (via --step=stepCut), covering unstable preProposal holding,
  // correlated-observer invalidation, and multi-process cut emission.
  import rapidConsensus(
    N = 4,
    MAX_ROUND = 2,
    CLASSIC_LEADERS = Set(1),
    PROPOSALS = Set(1),
    K_EFF = 3,
    H_EFF = 2,
    L_EFF = 1
  ).*
}
