// -*- mode: Bluespec; -*-
/**
 * L2 ledger-replica quorum read lease (system-model.md §2, ledger-replicas.md LR-I10,
 * ledger-protocol.md LP-I18, ledgers.md LG-I14, orleans.md OR-I24).
 *
 * A primary holds a per-ledger quorum read lease backed by RAM-only replica grants
 * (zero WAL writes) and monotonic clock-rate drift bounded by VMin..VMax
 * (F = VMax / VMin). While now + GuardBand < localLeaseUntil, the primary serves
 * 0-RTT linearizable local reads (ReadTailAsync) without contacting replicas.
 *
 * Safety (NoStaleLocalRead): no 0-RTT local read ever runs after a successor ledger
 * has committed in LedgerMap (equivalently, once successorCommitted holds, the
 * outgoing primary's local read guard is false).
 *
 * Mechanisms checked:
 *  - Primary records monotonic t_send before dispatching a write/heartbeat request;
 *    upon Q unsealed grants of D, advances localLeaseUntil = max(prev, t_send + floor(D * VMin / VMax))
 *    unless permanently surrendered.
 *  - Replicas keep grantedLeaseUntil in RAM only, preserve durable isSealed across
 *    crash/restart, and latch bootLeaseHorizon = bootClock + D on restart before
 *    serving RPCs.
 *  - Sealing ReadTailAsync(seal: true) persists isSealed and returns
 *    leaseRemaining = max(0, max(grantedLeaseUntil, bootLeaseHorizon) - rClock).
 *  - Sealer (TransitionExecutor) collects N - Q + 1 seal answers and sleeps
 *    max(leaseRemaining) on its monotonic clock before committing LedgerMap CAS
 *    (ReplaceTakenOver, Split, Merge), EXCEPT on ReplaceCooperative after
 *    PrepareRelease permanently surrendered the primary's lease (0 ms handoff).
 */
module LedgerLease {
  const Replicas: Set[int]
  const Quorum: int
  // Monotonic clock speed bounds: each real-time tick advances an actor's clock
  // by VMin..VMax ticks, so F = VMax / VMin bounds relative rate drift.
  const VMin: int
  const VMax: int
  // Replica lease grant duration D (in local clock ticks).
  const LeaseDuration: int
  // Primary read guard band delta (in local clock ticks); safety holds even at 0.
  const GuardBand: int
  // Maximum real-time ticks explored.
  const MaxTime: int
  // Number of lease-carrying request rounds (1 or 2).
  const MaxReqs: int
  // Guard-disabling mutation name, or "none".
  const Mutation: str

  type TransitionMode =
    // Replace(TakenOver), Split, or Merge: must wait out max(leaseRemaining).
    | Unplanned
    // Replace(Cooperative) after PrepareRelease returned Released: 0 ms handoff.
    | Cooperative

  type State = {
    // Discrete real time driving actor monotonic clocks (0..MaxTime).
    rt: int,
    // Fixed per-actor monotonic clock rates chosen in {VMin, VMax} at init.
    // Local clocks are derived as rt * rate, avoiding redundant state variables.
    pRate: int,
    sRate: int,
    rRate: int -> int,

    // Primary state: lease-carrying request rounds (1..MaxReqs), so a late reply
    // can race with PrepareRelease or renew an earlier lease.
    reqSentCount: int,
    reqSentAt: int -> int,
    reqGrants: int -> Set[int],
    localLeaseUntil: int,
    surrendered: bool,
    // Primary returned Released from PrepareRelease.
    released: bool,

    // Replica state: durable seal, RAM-only grantedUntil and bootHorizon.
    sealed: Set[int],
    grantedUntil: int -> int,
    bootHorizon: int -> int,
    restarted: Set[int],

    // Sealer (TransitionExecutor) state.
    sealAnswers: int -> int,
    sealerReady: bool,
    sealerMode: TransitionMode,
    sealerWaitDur: int,
    sealerWaitUntil: int,
    successorCommitted: bool,
  }

  var s: State

  pure def coverage: int = Replicas.size() - Quorum + 1
  pure def grantQuorum: int = if (Mutation == "short-lease-quorum") Quorum - 1 else Quorum
  pure def sealCoverage: int = if (Mutation == "short-seal-coverage") coverage - 1 else coverage
  pure def effectiveGrant: int =
    if (Mutation == "no-drift-divisor") LeaseDuration
    else (LeaseDuration * VMin) / VMax
  pure def maxInt(a: int, b: int): int = if (a >= b) a else b
  pure def rates: Set[int] = Set(VMin, VMax)

  def pClock(t: State): int = t.rt * t.pRate
  def sClock(t: State): int = t.rt * t.sRate
  def rClock(t: State, r: int): int = t.rt * t.rRate.get(r)
  def canLocalRead(t: State): bool = pClock(t) + GuardBand < t.localLeaseUntil

  // Initial steady state: replicas booted long ago (bootHorizon = 0), no lease granted yet.
  action init = {
    nondet pr = rates.oneOf()
    nondet sr = rates.oneOf()
    nondet rr = Replicas.setOfMaps(rates).oneOf()
    s' = {
      rt: 0,
      pRate: pr,
      sRate: sr,
      rRate: rr,
      reqSentCount: 0,
      reqSentAt: Map(),
      reqGrants: Map(),
      localLeaseUntil: 0,
      surrendered: false,
      released: false,
      sealed: Set(),
      grantedUntil: Replicas.mapBy(_ => 0),
      bootHorizon: Replicas.mapBy(_ => 0),
      restarted: Set(),
      sealAnswers: Map(),
      sealerReady: false,
      sealerMode: Unplanned,
      sealerWaitDur: 0,
      sealerWaitUntil: 0,
      successorCommitted: false,
    }
  }

  // Advance real time by 1 tick; every actor's monotonic clock advances by its bounded rate.
  action tick = all {
    s.rt < MaxTime,
    s' = { ...s, rt: s.rt + 1 },
  }

  // Primary sends the next non-recovery write or batched heartbeat, recording monotonic t_send.
  action primarySend = {
    val reqId = s.reqSentCount + 1
    all {
      s.reqSentCount < MaxReqs,
      not(s.surrendered),
      not(s.released),
      s' = {
        ...s,
        reqSentCount: reqId,
        reqSentAt: s.reqSentAt.put(reqId, pClock(s)),
        reqGrants: s.reqGrants.put(reqId, Set()),
      },
    }
  }

  // Unsealed replica processes a non-recovery write or heartbeat, extending RAM-only
  // grantedUntil without writing to the WAL (LR-I10).
  action replicaGrant(reqId: int, r: int): bool = {
    val nextGrant = maxInt(s.grantedUntil.get(r), rClock(s, r) + LeaseDuration)
    all {
      s.reqGrants.keys().contains(reqId),
      not(s.sealed.contains(r)),
      not(s.reqGrants.get(reqId).contains(r)),
      s' = {
        ...s,
        grantedUntil: s.grantedUntil.set(r, nextGrant),
        reqGrants: s.reqGrants.set(reqId, s.reqGrants.get(reqId).union(Set(r))),
      },
    }
  }

  // Primary observes Q unsealed grants for reqId and advances localLeaseUntil (LP-I18).
  // Rejected once permanently surrendered (unless extend-after-surrender mutant).
  action primaryApplyGrant(reqId: int): bool = {
    val candidate =
      if (s.reqSentAt.keys().contains(reqId)) s.reqSentAt.get(reqId) + effectiveGrant
      else 0
    all {
      s.reqGrants.keys().contains(reqId),
      s.reqGrants.get(reqId).size() >= grantQuorum,
      Mutation == "extend-after-surrender" or not(s.surrendered),
      candidate > s.localLeaseUntil,
      s' = {
        ...s,
        localLeaseUntil: candidate,
      },
    }
  }

  // Cooperative PrepareRelease: permanently surrenders localLeaseUntil before returning Released (OR-I24).
  action primaryPrepareRelease = all {
    not(s.released),
    s' = {
      ...s,
      surrendered: if (Mutation == "no-surrender-before-release") s.surrendered else true,
      localLeaseUntil: if (Mutation == "no-surrender-before-release") s.localLeaseUntil else 0,
      released: true,
    },
  }

  // Replica crashes and restarts: loses RAM-only grantedUntil, preserves durable sealed,
  // and latches bootLeaseHorizon = rClock + D before opening RPCs (LR-I10).
  action replicaRestart(r: int): bool = {
    val nextBoot =
      if (Mutation == "no-boot-horizon") 0
      else maxInt(s.bootHorizon.get(r), rClock(s, r) + LeaseDuration)
    all {
      not(s.restarted.contains(r)),
      s.grantedUntil.get(r) > 0,
      s' = {
        ...s,
        grantedUntil: s.grantedUntil.set(r, 0),
        bootHorizon: s.bootHorizon.set(r, nextBoot),
        restarted: s.restarted.union(Set(r)),
      },
    }
  }

  // Sealer seals replica r: durably sets sealed[r] and receives remaining lease duration
  // max(0, max(grantedUntil[r], bootHorizon[r]) - rClock[r]) (LR-I10).
  action sealerSealReplica(r: int): bool = {
    val horizon = maxInt(s.grantedUntil.get(r), s.bootHorizon.get(r))
    val rem = maxInt(0, horizon - rClock(s, r))
    all {
      not(s.sealAnswers.keys().contains(r)),
      s' = {
        ...s,
        sealed: s.sealed.union(Set(r)),
        sealAnswers: s.sealAnswers.put(r, rem),
      },
    }
  }

  // Sealer completes seal coverage (N - Q + 1 replicas) and schedules its pre-CAS wait (LG-I14).
  action sealerCompleteSeal(mode: TransitionMode): bool = {
    val maxRem = s.sealAnswers.keys().fold(0, (acc, r) => maxInt(acc, s.sealAnswers.get(r)))
    val waitDur =
      if (mode == Cooperative or Mutation == "no-seal-wait") 0
      else maxRem
    all {
      not(s.sealerReady),
      s.sealAnswers.keys().size() >= sealCoverage,
      mode != Cooperative or s.released,
      s' = {
        ...s,
        sealerReady: true,
        sealerMode: mode,
        sealerWaitDur: waitDur,
        sealerWaitUntil: sClock(s) + waitDur,
      },
    }
  }

  // Sealer commits LedgerMap CAS once its monotonic wait elapses (LG-I14).
  action sealerCommit = all {
    s.sealerReady,
    not(s.successorCommitted),
    sClock(s) >= s.sealerWaitUntil,
    s' = {
      ...s,
      successorCommitted: true,
    },
  }

  action step = any {
    tick,
    primarySend,
    nondet reqId = s.reqGrants.keys().oneOf()
    nondet r = Replicas.oneOf()
    replicaGrant(reqId, r),
    nondet reqId = s.reqGrants.keys().oneOf()
    primaryApplyGrant(reqId),
    primaryPrepareRelease,
    nondet r = Replicas.oneOf()
    replicaRestart(r),
    nondet r = Replicas.oneOf()
    sealerSealReplica(r),
    nondet mode = Set(Unplanned, Cooperative).oneOf()
    sealerCompleteSeal(mode),
    sealerCommit,
  }

  // --- Safety invariants ---

  // Whenever a successor ledger has committed in LedgerMap, the outgoing primary's
  // 0-RTT local read guard is false, so no stale local read can ever execute.
  val NoStaleLocalRead: bool =
    s.successorCommitted implies not(canLocalRead(s))

  // A sealed replica stays sealed across crashes and restarts.
  val SealedStaysSealed: bool =
    s.sealAnswers.keys().forall(r => s.sealed.contains(r))

  // --- Non-vacuity witnesses (each must be violated in the unmutated model) ---
  val NeverLocalRead: bool = not(canLocalRead(s))
  val NeverUnplannedWaitCommit: bool =
    not(s.successorCommitted and s.sealerMode == Unplanned and s.sealerWaitDur > 0)
  val NeverCooperativeZeroWaitCommit: bool =
    not(s.successorCommitted and s.sealerMode == Cooperative and s.sealerWaitDur == 0)
  val NeverRestartBeforeSeal: bool =
    not(s.restarted.size() > 0 and s.successorCommitted)
}
