// -*- mode: Bluespec; -*-
/**
 * Roux Range Ownership & Ledger-Replica Lease Handoff (`ST8`, `R33`, `M39`)
 * (docs/design/storage.md, docs/design/runtime.md).
 *
 * Models the cross-layer ownership seam for a single dyadic KeyRange (and a
 * representative grain inside it) with leases implemented directly by Clog's
 * ledger replicas (`LedgerReplica`, Target 97) rather than Rapid:
 *
 * - Static replica ensemble (`N = 3`, quorum `Q = 2`, `2Q > N`) per open
 *   ledger `L`, with one immutable primary (`openPrimary`) and a one-way
 *   lifecycle (`Open -> Closed`) in MetaStore `LedgerMap` (`CLLM`).
 * - Bounded clock-rate drift (`F = 2`, i.e., `rho = 1`): every `tick(fast)`
 *   advances each node's monotonic clock by `1` or `2`.
 * - Active & idle lease renewal (zero WAL writes):
 *   - Primary `P` of open ledger `L` starts a `ReplicaWrite` or idle
 *     `ReplicaHeartbeat` round at its local clock `sentAt`.
 *   - Each unsealed replica `r` extends in-memory
 *     `grantedUntil[L] = max(grantedUntil[L], r.clock + D)` and replies.
 *   - Once `Q` replicas ack, `P` advances `localLeaseUntil[L]` to
 *     `sentAt + D / F`.
 * - Replica crash/restart safety (`bootHorizon`, zero disk I/O):
 *   - On `LedgerStore` restart, in-memory `grantedUntil` is lost and
 *     `bootHorizon = r.clock + D` conservatively bounds any pre-crash grant.
 *   - Durable `sealedLedgers` (`LP-I8`) survives restart.
 * - Revocation IS ledger sealing (`ReplicaReadTail { seal: true }`):
 *   - Replica `r` durably marks `L` sealed (refusing future writes and lease
 *     grants), computes `rem = max(0, max(grantedUntil[L], bootHorizon) - r.clock)`,
 *     and returns `SealReply(L, r, rem)`.
 *   - On unplanned takeover (`TakenOver`), `TransitionExecutor` sleeps
 *     `max_{r in Q_seal} rem_r` on its own clock *before* committing
 *     `LedgerMap` CAS (`L -> L + 1`). Because `P` already divided `D` by `F`
 *     (`D / F`), any worst-case clock drift (`v_max / v_min <= F`) satisfies
 *     `T_commit >= T_seal + (D - v_max * T_seal) / v_max = D / v_max >= T_P_exp`,
 *     so waiting `max_{r in Q_seal} rem_r` is both necessary and sufficient.
 *     By the time `L` is closed in `LedgerMap`, `L`'s replica-quorum lease is
 *     already expired — zero dependency on Rapid `Defunct` and zero
 *     chained-takeover predecessor tracking.
 *   - On planned handoff (`Cooperative`), outgoing primary completes
 *     `PrepareRelease(L)` (fences L4, drains activations & flushes/drops held
 *     responses, permanently surrenders its local lease on `L`, stops admitting,
 *     and replies `Released(L)`). `TransitionExecutor` still quorum-seals `L`
 *     and skips the `lease_remaining` sleep (0 ms wait).
 *
 * Mutants disable one guard at a time and are verified via ../check-mutants.sh.
 */
module rangeHandoff {

  // ------------------------------------------------------------------ Consts
  const NODES: Set[int]
  const MAX_LEDGER: int
  // Replica lease grant duration on the replica's monotonic clock.
  const D: int
  const MAX_CLOCK: int
  const MAX_RENEWS: int
  const MAX_PENDING_TXS: int

  // Bounded clock-rate drift factor F = 2 (each tick advances a clock by 1 or 2).
  pure val F: int = 2
  pure val LEASE_LEN: int = D / F
  pure val QUORUM: int = 2
  pure val INIT_PRIMARY: int = 1

  // ------------------------------------------------------------------ Types
  type HandoffKind = Cooperative | TakenOver

  type RenewReq = {
    id: int,
    ledger: int,
    primary: int,
    sentAt: int,
  }

  type GrantAck = {
    reqId: int,
    replica: int,
  }

  type SealReply = {
    ledger: int,
    replica: int,
    remaining: int,
  }

  type PrepReq = {
    ledger: int,
    target: int,
  }

  type PendingTransition = {
    sealer: int,
    oldLedger: int,
    newOwner: int,
    kind: HandoffKind,
    waitUntil: int,
  }

  type NodeState = {
    // Monotonic local clock (drifts at rate 1..F relative to other nodes).
    clock: int,
    // Clog LedgerStore (Target 97) replica state.
    bootHorizon: int,               // boot_clock + D on restart (RAM only)
    sealedLedgers: Set[int],        // durable LP-I8 seal flags (survives restart)
    grantedUntil: int -> int,       // RAM-only lease grant per ledger (wiped on restart)
    // Primary lease & handoff state (clog-ledgers + roux-storage).
    leaseUntil: int -> int,         // local monotonic expiry per ledger
    surrendered: Set[int],          // ledgers whose local lease was surrendered via PrepareRelease
    releasing: Set[int],
    drainedCleanly: Set[int],
    pendingAcquire: Set[int],       // epoch-ordered Acquired events queued for L4
    // L4 runtime state for this range's representative grain.
    l4ServedLedger: int,            // 0 when not serving
    l4FencedLedger: int,            // highest ledger fenced by Releasing or Lost
    hasActiveGrain: bool,
    pendingRelease: bool,           // un-flushed durability-gated outbound response
    activeAtLedger: int,            // ledger epoch that created active/pending state
  }

  // ------------------------------------------------------------------ State
  var nodes: int -> NodeState
  // MetaStore LedgerMap (CLLM)
  var openLedger: int
  var openPrimary: int
  var ledgerPrimary: int -> int     // immutable primary for every ledger ever opened
  var closedLedgers: Set[int]
  // Network messages
  var nextReqId: int
  var renewReqs: Set[RenewReq]
  var grantAcks: Set[GrantAck]
  var sealReplies: Set[SealReply]
  var prepReqs: Set[PrepReq]
  var releasedAcks: Set[PrepReq]
  var pendingTxs: Set[PendingTransition]
  // Ghost observability
  var highestEffectLedger: int
  var zombieEffectObserved: bool

  // ------------------------------------------------------------------ Helpers
  pure def maxInt(a: int, b: int): int = if (a >= b) a else b

  pure def mapGetOr(m: int -> int, k: int, fallback: int): int =
    if (m.keys().contains(k)) m.get(k) else fallback

  pure def initNode(n: int): NodeState = {
    clock: 0,
    bootHorizon: 0,
    sealedLedgers: Set(),
    grantedUntil: Map(),
    leaseUntil: Map(),
    surrendered: Set(),
    releasing: Set(),
    drainedCleanly: Set(),
    pendingAcquire: Set(),
    l4ServedLedger: if (n == INIT_PRIMARY) 1 else 0,
    l4FencedLedger: 0,
    hasActiveGrain: false,
    pendingRelease: false,
    activeAtLedger: 0,
  }

  // True iff node `n` currently holds a valid local replica-quorum lease on `ledger`.
  def hasValidLease(n: int, ledger: int): bool = {
    val ns = nodes.get(n)
    ledger > 0
      and not(ns.surrendered.contains(ledger))
      and mapGetOr(ns.leaseUntil, ledger, 0) > ns.clock
  }

  // ------------------------------------------------------------------ Init
  action init = all {
    nodes' = NODES.mapBy(n => initNode(n)),
    openLedger' = 1,
    openPrimary' = INIT_PRIMARY,
    ledgerPrimary' = Map(1 -> INIT_PRIMARY),
    closedLedgers' = Set(),
    nextReqId' = 1,
    renewReqs' = Set(),
    grantAcks' = Set(),
    sealReplies' = Set(),
    prepReqs' = Set(),
    releasedAcks' = Set(),
    pendingTxs' = Set(),
    highestEffectLedger' = 0,
    zombieEffectObserved' = false,
  }

  // ------------------------------------------------------------------ Clocks
  // Advance every node's monotonic clock by 1 or 2 (`F = 2`):
  // - `fast == 0`: every node advances by 1
  // - `fast > 0`: node `fast` advances by 2, others by 1
  // - `fast < 0`: every node except `-fast` advances by 2, `-fast` by 1
  action tick(fast: int): bool = all {
    NODES.forall(n => nodes.get(n).clock < MAX_CLOCK),
    nodes' = NODES.mapBy(n => {
      val ns = nodes.get(n)
      val delta = if (n == fast or (fast < 0 and n != 0 - fast)) F else 1
      { ...ns, clock: ns.clock + delta }
    }),
    openLedger' = openLedger, openPrimary' = openPrimary,
    ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
    nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
    sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
    pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
    zombieEffectObserved' = zombieEffectObserved,
  }

  // ------------------------------------------------ Replica Crash & Restart
  // A LedgerStore process crashes and restarts: in-memory `grantedUntil` is
  // lost, durable `sealedLedgers` (LP-I8) survives, and `bootHorizon` is set
  // to `clock + D` in RAM so any subsequent seal conservatively covers any
  // pre-crash lease grant without fsyncing renewals.
  action replicaRestart(r: int): bool = {
    val rs = nodes.get(r)
    all {
      // Only model replica-store restart when not mid-release as primary
      // (a full node restart would also drop volatile primary state).
      nodes' = nodes.set(r, {
        ...rs,
        bootHorizon: rs.clock + D,
        grantedUntil: Map(),
        // Process restart also clears volatile primary & L4 state on this node.
        leaseUntil: Map(),
        l4ServedLedger: 0,
        hasActiveGrain: false,
        pendingRelease: false,
        activeAtLedger: 0,
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // ------------------------------------------------ Ledger-Replica Leases
  // Primary starts a ReplicaWrite or idle ReplicaHeartbeat round on its open ledger.
  action primaryStartRenew(n: int): bool = {
    val ns = nodes.get(n)
    all {
      n == openPrimary,
      not(ns.surrendered.contains(openLedger)),
      not(ns.releasing.contains(openLedger)),
      not(ns.drainedCleanly.contains(openLedger)),
      renewReqs.size() < MAX_RENEWS,
      renewReqs' = renewReqs.union(Set({
        id: nextReqId,
        ledger: openLedger,
        primary: n,
        sentAt: ns.clock,
      })),
      nextReqId' = nextReqId + 1,
      nodes' = nodes, openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      grantAcks' = grantAcks, sealReplies' = sealReplies,
      prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Unsealed replica `r` accepts ReplicaWrite / ReplicaHeartbeat for `req.ledger`,
  // extending in-memory `grantedUntil[ledger]` to `r.clock + D`.
  action replicaGrantLease(r: int, req: RenewReq): bool = {
    val rs = nodes.get(r)
    val prev = mapGetOr(rs.grantedUntil, req.ledger, 0)
    val nextGrant = maxInt(prev, rs.clock + D)
    all {
      renewReqs.contains(req),
      not(rs.sealedLedgers.contains(req.ledger)),
      not(grantAcks.contains({ reqId: req.id, replica: r })),
      nodes' = nodes.set(r, {
        ...rs,
        grantedUntil: rs.grantedUntil.put(req.ledger, nextGrant),
      }),
      grantAcks' = grantAcks.union(Set({ reqId: req.id, replica: r })),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Primary collects a quorum (`QUORUM`) of grant acks for `req` and extends its
  // local monotonic lease expiry to `req.sentAt + LEASE_LEN`.
  action primaryAcceptLease(req: RenewReq): bool = {
    val n = req.primary
    val ns = nodes.get(n)
    val ackedReplicas = grantAcks.filter(a => a.reqId == req.id).map(a => a.replica)
    val prevExpiry = mapGetOr(ns.leaseUntil, req.ledger, 0)
    val newExpiry = maxInt(prevExpiry, req.sentAt + LEASE_LEN)
    all {
      renewReqs.contains(req),
      ackedReplicas.size() >= QUORUM,
      not(ns.surrendered.contains(req.ledger)),
      newExpiry > prevExpiry,
      nodes' = nodes.set(n, {
        ...ns,
        leaseUntil: ns.leaseUntil.put(req.ledger, newExpiry),
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // ------------------------------------------------ L4 Grain Activity & Lapse
  // L4 activates a grain on a node currently serving a ledger with a valid lease.
  action activateGrain(n: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.l4ServedLedger > 0,
      ns.l4ServedLedger > ns.l4FencedLedger,
      hasValidLease(n, ns.l4ServedLedger),
      not(ns.hasActiveGrain),
      nodes' = nodes.set(n, {
        ...ns,
        hasActiveGrain: true,
        activeAtLedger: ns.l4ServedLedger,
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // An active grain stages an outbound durability-gated response.
  action stageResponse(n: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.hasActiveGrain,
      hasValidLease(n, ns.activeAtLedger),
      not(ns.pendingRelease),
      nodes' = nodes.set(n, { ...ns, pendingRelease: true }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // An activation or held response emits an externally visible effect gated on
  // `ledger_lease.check(now)` (R3 / R33 / ST8).
  action emitEffect(n: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.hasActiveGrain or ns.pendingRelease,
      ns.activeAtLedger > 0,
      hasValidLease(n, ns.activeAtLedger),
      nodes' = nodes.set(n, { ...ns, pendingRelease: false }),
      highestEffectLedger' = maxInt(highestEffectLedger, ns.activeAtLedger),
      zombieEffectObserved' = (
        zombieEffectObserved
        or (ns.activeAtLedger < highestEffectLedger)
        or closedLedgers.contains(ns.activeAtLedger)
      ),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs,
    }
  }

  // Per-ledger lease lapse in L4: if the served ledger's lease has lapsed, L4
  // suspends turns/effects and drops volatile state for that range only.
  action lapseRange(n: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.hasActiveGrain or ns.pendingRelease,
      ns.activeAtLedger > 0,
      not(hasValidLease(n, ns.activeAtLedger)),
      nodes' = nodes.set(n, {
        ...ns,
        hasActiveGrain: false,
        pendingRelease: false,
        activeAtLedger: 0,
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // ------------------------------------------------ Cooperative PrepareRelease
  action controllerSendPrepareRelease(ledger: int, target: int): bool = all {
    ledger == openLedger,
    target == openPrimary,
    openLedger < MAX_LEDGER,
    prepReqs' = prepReqs.union(Set({ ledger: ledger, target: target })),
    nodes' = nodes, openLedger' = openLedger, openPrimary' = openPrimary,
    ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
    nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
    sealReplies' = sealReplies, releasedAcks' = releasedAcks,
    pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
    zombieEffectObserved' = zombieEffectObserved,
  }

  // Primary fences L4 on `req.ledger` (emits RangeEvent::Releasing).
  action beginReleasing(req: PrepReq): bool = {
    val n = req.target
    val ns = nodes.get(n)
    all {
      prepReqs.contains(req),
      not(closedLedgers.contains(req.ledger)),
      req.ledger == openLedger,
      not(ns.drainedCleanly.contains(req.ledger)),
      not(ns.releasing.contains(req.ledger)),
      ns.pendingAcquire.filter(l => l <= req.ledger).size() == 0,
      nodes' = nodes.set(n, {
        ...ns,
        releasing: ns.releasing.union(Set(req.ledger)),
        l4ServedLedger: if (ns.l4ServedLedger <= req.ledger) 0 else ns.l4ServedLedger,
        l4FencedLedger: maxInt(ns.l4FencedLedger, req.ledger),
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // L4 completes drain & flushes/drops held responses (`ticket.complete()`),
  // permanently surrenders `ledger`'s local lease, stops admitting, and replies `Released`.
  action completePrepareRelease(n: int, ledger: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.releasing.contains(ledger),
      not(ns.drainedCleanly.contains(ledger)),
      nodes' = nodes.set(n, {
        ...ns,
        releasing: ns.releasing.exclude(Set(ledger)),
        drainedCleanly: ns.drainedCleanly.union(Set(ledger)),
        surrendered: ns.surrendered.union(Set(ledger)),
        hasActiveGrain: if (ns.activeAtLedger <= ledger) false else ns.hasActiveGrain,
        pendingRelease: if (ns.activeAtLedger <= ledger) false else ns.pendingRelease,
        activeAtLedger: if (ns.activeAtLedger <= ledger) 0 else ns.activeAtLedger,
      }),
      releasedAcks' = releasedAcks.union(Set({ ledger: ledger, target: n })),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Idempotent retry of PrepareRelease for an already-drained ledger.
  action retryPrepareReleaseIdempotent(req: PrepReq): bool = {
    val ns = nodes.get(req.target)
    all {
      prepReqs.contains(req),
      ns.drainedCleanly.contains(req.ledger),
      releasedAcks' = releasedAcks.union(Set(req)),
      nodes' = nodes, openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // ------------------------------------------------ Quorum Seal & Transition
  // Replica `r` handles `ReplicaReadTail { seal: true }` for `ledger`:
  // durably sets `sealed` (refusing future writes & renewals) and returns
  // `lease_remaining = max(0, max(grantedUntil[ledger], bootHorizon) - r.clock)`.
  action replicaSeal(r: int, ledger: int): bool = {
    val rs = nodes.get(r)
    val horizon = maxInt(mapGetOr(rs.grantedUntil, ledger, 0), rs.bootHorizon)
    val rem = maxInt(0, horizon - rs.clock)
    all {
      ledger == openLedger,
      openLedger < MAX_LEDGER,
      nodes' = nodes.set(r, {
        ...rs,
        sealedLedgers: rs.sealedLedgers.union(Set(ledger)),
      }),
      sealReplies' = sealReplies.union(Set({
        ledger: ledger,
        replica: r,
        remaining: rem,
      })),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // TransitionExecutor on `sealer` collects a seal quorum (`>= QUORUM` distinct
  // replicas) for `openLedger` and computes the required holdoff before CAS:
  // - `Cooperative` (requires `Released` ack from `openPrimary`): 0 wait.
  // - `TakenOver` (unplanned): `max_{r in Q_seal} remaining_r` (sufficient under
  //   drift factor `F` because primary already divides `D` by `F`).
  action executorPrepareTransition(
    sealer: int,
    newOwner: int,
    kind: HandoffKind,
    replies: Set[SealReply]
  ): bool = {
    val sealerClock = nodes.get(sealer).clock
    val maxRem = replies.fold(0, (acc, sr) => maxInt(acc, sr.remaining))
    val waitUntil = if (kind == Cooperative) sealerClock else sealerClock + maxRem
    val tx = {
      sealer: sealer,
      oldLedger: openLedger,
      newOwner: newOwner,
      kind: kind,
      waitUntil: waitUntil,
    }
    all {
      openLedger < MAX_LEDGER,
      newOwner != openPrimary,
      replies.subseteq(sealReplies),
      replies.forall(sr => sr.ledger == openLedger),
      replies.map(sr => sr.replica).size() >= QUORUM,
      if (kind == Cooperative)
        releasedAcks.contains({ ledger: openLedger, target: openPrimary })
      else
        true,
      not(pendingTxs.contains(tx)),
      pendingTxs.size() < MAX_PENDING_TXS,
      pendingTxs' = pendingTxs.union(Set(tx)),
      nodes' = nodes, openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Once `sealer.clock >= tx.waitUntil`, TransitionExecutor commits `LedgerMap`
  // CAS (`openLedger -> openLedger + 1`) and notifies `newOwner` / `oldOwner`.
  action commitTransition(tx: PendingTransition): bool = {
    val newLedger = openLedger + 1
    val oldOwner = openPrimary
    all {
      pendingTxs.contains(tx),
      tx.oldLedger == openLedger,
      openLedger < MAX_LEDGER,
      nodes.get(tx.sealer).clock >= tx.waitUntil,
      openLedger' = newLedger,
      openPrimary' = tx.newOwner,
      ledgerPrimary' = ledgerPrimary.put(newLedger, tx.newOwner),
      closedLedgers' = closedLedgers.union(Set(tx.oldLedger)),
      nodes' = NODES.mapBy(n => {
        val ns = nodes.get(n)
        val afterAcquire = if (n == tx.newOwner) {
          ...ns,
          pendingAcquire: ns.pendingAcquire.union(Set(newLedger)),
        } else ns
        if (n == oldOwner) {
          // Seal/close fires RangeEvent::Lost(oldLedger) on oldOwner (may be delayed,
          // so only fence if already observed or drop releasing flag; L4's lease check
          // already stops oldOwner once its replica lease expires).
          {
            ...afterAcquire,
            releasing: afterAcquire.releasing.exclude(Set(tx.oldLedger)),
          }
        } else afterAcquire
      }),
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Old primary eventually learns of close/seal and processes RangeEvent::Lost.
  action deliverLost(n: int, ledger: int): bool = {
    val ns = nodes.get(n)
    all {
      closedLedgers.contains(ledger),
      ns.l4FencedLedger < ledger,
      nodes' = nodes.set(n, {
        ...ns,
        releasing: ns.releasing.exclude(Set(ledger)),
        l4ServedLedger: if (ns.l4ServedLedger <= ledger) 0 else ns.l4ServedLedger,
        l4FencedLedger: maxInt(ns.l4FencedLedger, ledger),
        hasActiveGrain: if (ns.activeAtLedger <= ledger) false else ns.hasActiveGrain,
        pendingRelease: if (ns.activeAtLedger <= ledger) false else ns.pendingRelease,
        activeAtLedger: if (ns.activeAtLedger <= ledger) 0 else ns.activeAtLedger,
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // New primary delivers RangeEvent::Acquired(ledger) in epoch order.
  action deliverAcquire(n: int, ledger: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.pendingAcquire.contains(ledger),
      ns.pendingAcquire.filter(l => l < ledger).size() == 0,
      nodes' = nodes.set(n, {
        ...ns,
        pendingAcquire: ns.pendingAcquire.exclude(Set(ledger)),
        l4ServedLedger: if (ledger > ns.l4FencedLedger) ledger else ns.l4ServedLedger,
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // ------------------------------------------------------------------ Step
  action step = any {
    nondet fast = Set(0, 1, 2, 3, -1, -2, -3).oneOf()
    tick(fast),
    nondet r = NODES.oneOf()
    replicaRestart(r),
    nondet n = NODES.oneOf()
    primaryStartRenew(n),
    all {
      renewReqs.size() > 0,
      nondet r = NODES.oneOf()
      nondet req = renewReqs.oneOf()
      replicaGrantLease(r, req),
    },
    all {
      renewReqs.size() > 0,
      nondet req = renewReqs.oneOf()
      primaryAcceptLease(req),
    },
    nondet n = NODES.oneOf()
    activateGrain(n),
    nondet n = NODES.oneOf()
    stageResponse(n),
    nondet n = NODES.oneOf()
    emitEffect(n),
    nondet n = NODES.oneOf()
    lapseRange(n),
    controllerSendPrepareRelease(openLedger, openPrimary),
    all {
      prepReqs.size() > 0,
      nondet req = prepReqs.oneOf()
      beginReleasing(req),
    },
    nondet n = NODES.oneOf()
    nondet l = nodes.get(n).releasing.oneOf()
    completePrepareRelease(n, l),
    all {
      prepReqs.size() > 0,
      nondet req = prepReqs.oneOf()
      retryPrepareReleaseIdempotent(req),
    },
    nondet r = NODES.oneOf()
    replicaSeal(r, openLedger),
    all {
      sealReplies.filter(sr => sr.ledger == openLedger).size() >= QUORUM,
      nondet sealer = NODES.oneOf()
      nondet newOwner = NODES.oneOf()
      nondet kind = Set(Cooperative, TakenOver).oneOf()
      nondet r1 = sealReplies.filter(sr => sr.ledger == openLedger).oneOf()
      nondet r2 = sealReplies.filter(sr => sr.ledger == openLedger).oneOf()
      executorPrepareTransition(sealer, newOwner, kind, Set(r1, r2)),
    },
    all {
      pendingTxs.size() > 0,
      nondet tx = pendingTxs.oneOf()
      commitTransition(tx),
    },
    all {
      closedLedgers.size() > 0,
      nondet n = NODES.oneOf()
      nondet l = closedLedgers.oneOf()
      deliverLost(n, l),
    },
    nondet n = NODES.oneOf()
    nondet l = nodes.get(n).pendingAcquire.oneOf()
    deliverAcquire(n, l),
  }

  // ------------------------------------------------------------------ Mutants
  // Mutant 1: replica grants a lease renewal even after sealing the ledger.
  // breaks: closedLedgerHasNoValidLease
  action doMutantRenewAfterSeal(r: int, req: RenewReq): bool = {
    val rs = nodes.get(r)
    val prev = mapGetOr(rs.grantedUntil, req.ledger, 0)
    val nextGrant = maxInt(prev, rs.clock + D)
    all {
      renewReqs.contains(req),
      // BUG: missing `not(rs.sealedLedgers.contains(req.ledger))`
      rs.sealedLedgers.contains(req.ledger),
      not(grantAcks.contains({ reqId: req.id, replica: r })),
      nodes' = nodes.set(r, {
        ...rs,
        grantedUntil: rs.grantedUntil.put(req.ledger, nextGrant),
      }),
      grantAcks' = grantAcks.union(Set({ reqId: req.id, replica: r })),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Mutant 2: unplanned takeover skips the `lease_remaining` holdoff sleep.
  // breaks: closedLedgerHasNoValidLease
  action doMutantTakeoverSkipsLeaseWait(
    sealer: int,
    newOwner: int,
    replies: Set[SealReply]
  ): bool = {
    val sealerClock = nodes.get(sealer).clock
    val tx = {
      sealer: sealer,
      oldLedger: openLedger,
      newOwner: newOwner,
      kind: TakenOver,
      waitUntil: sealerClock, // BUG: ignores maxRem * F
    }
    all {
      openLedger < MAX_LEDGER,
      newOwner != openPrimary,
      replies.subseteq(sealReplies),
      replies.forall(sr => sr.ledger == openLedger),
      replies.map(sr => sr.replica).size() >= QUORUM,
      not(pendingTxs.contains(tx)),
      pendingTxs' = pendingTxs.union(Set(tx)),
      nodes' = nodes, openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Mutant 3: primary forgets clock-rate drift factor `F` (uses `sentAt + D` instead of `sentAt + D / F`).
  // breaks: closedLedgerHasNoValidLease
  action doMutantPrimaryForgetsDriftFactor(req: RenewReq): bool = {
    val n = req.primary
    val ns = nodes.get(n)
    val ackedReplicas = grantAcks.filter(a => a.reqId == req.id).map(a => a.replica)
    val prevExpiry = mapGetOr(ns.leaseUntil, req.ledger, 0)
    val newExpiry = maxInt(prevExpiry, req.sentAt + D) // BUG: missing / F
    all {
      renewReqs.contains(req),
      ackedReplicas.size() >= QUORUM,
      not(ns.surrendered.contains(req.ledger)),
      newExpiry > prevExpiry,
      nodes' = nodes.set(n, {
        ...ns,
        leaseUntil: ns.leaseUntil.put(req.ledger, newExpiry),
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Mutant 4: replica restarts without setting `bootHorizon = clock + D`.
  // breaks: closedLedgerHasNoValidLease
  action doMutantReplicaRestartForgetsBootHorizon(r: int): bool = {
    val rs = nodes.get(r)
    all {
      nodes' = nodes.set(r, {
        ...rs,
        bootHorizon: 0, // BUG: forgets startup holdoff after RAM wipe
        grantedUntil: Map(),
        leaseUntil: Map(),
        l4ServedLedger: 0,
        hasActiveGrain: false,
        pendingRelease: false,
        activeAtLedger: 0,
      }),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Mutant 5: executor runs Cooperative (0 ms wait) without receiving `Released`.
  // breaks: closedLedgerHasNoValidLease
  action doMutantCooperativeWithoutReleased(
    sealer: int,
    newOwner: int,
    replies: Set[SealReply]
  ): bool = {
    val sealerClock = nodes.get(sealer).clock
    val tx = {
      sealer: sealer,
      oldLedger: openLedger,
      newOwner: newOwner,
      kind: Cooperative,
      waitUntil: sealerClock, // BUG: skips wait without Released ack
    }
    all {
      openLedger < MAX_LEDGER,
      newOwner != openPrimary,
      replies.subseteq(sealReplies),
      replies.forall(sr => sr.ledger == openLedger),
      replies.map(sr => sr.replica).size() >= QUORUM,
      not(releasedAcks.contains({ ledger: openLedger, target: openPrimary })),
      not(pendingTxs.contains(tx)),
      pendingTxs' = pendingTxs.union(Set(tx)),
      nodes' = nodes, openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Mutant 6: PrepareRelease replies `Released` without surrendering its local lease.
  // breaks: closedLedgerHasNoValidLease
  action doMutantCooperativeForgetsSurrender(n: int, ledger: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.releasing.contains(ledger),
      not(ns.drainedCleanly.contains(ledger)),
      nodes' = nodes.set(n, {
        ...ns,
        releasing: ns.releasing.exclude(Set(ledger)),
        drainedCleanly: ns.drainedCleanly.union(Set(ledger)),
        // BUG: omits `surrendered: ns.surrendered.union(Set(ledger))`
        hasActiveGrain: if (ns.activeAtLedger <= ledger) false else ns.hasActiveGrain,
        pendingRelease: if (ns.activeAtLedger <= ledger) false else ns.pendingRelease,
        activeAtLedger: if (ns.activeAtLedger <= ledger) 0 else ns.activeAtLedger,
      }),
      releasedAcks' = releasedAcks.union(Set({ ledger: ledger, target: n })),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs,
      pendingTxs' = pendingTxs, highestEffectLedger' = highestEffectLedger,
      zombieEffectObserved' = zombieEffectObserved,
    }
  }

  // Mutant 7: L4 emits an effect without checking `hasValidLease(n, activeAtLedger)`.
  // breaks: noPostReleaseZombie
  action doMutantEffectWithoutLeaseCheck(n: int): bool = {
    val ns = nodes.get(n)
    all {
      ns.hasActiveGrain or ns.pendingRelease,
      ns.activeAtLedger > 0,
      // BUG: omits `hasValidLease(n, ns.activeAtLedger)`
      nodes' = nodes.set(n, { ...ns, pendingRelease: false }),
      highestEffectLedger' = maxInt(highestEffectLedger, ns.activeAtLedger),
      zombieEffectObserved' = (
        zombieEffectObserved
        or (ns.activeAtLedger < highestEffectLedger)
        or closedLedgers.contains(ns.activeAtLedger)
      ),
      openLedger' = openLedger, openPrimary' = openPrimary,
      ledgerPrimary' = ledgerPrimary, closedLedgers' = closedLedgers,
      nextReqId' = nextReqId, renewReqs' = renewReqs, grantAcks' = grantAcks,
      sealReplies' = sealReplies, prepReqs' = prepReqs, releasedAcks' = releasedAcks,
      pendingTxs' = pendingTxs,
    }
  }

  // ------------------------------------------------------------------ Mutated steps
  // Named `mutant*` so ../check-mutants.sh discovers each 0-arg mutant step.
  action mutantRenewAfterSeal = any {
    step,
    all {
      renewReqs.size() > 0,
      nondet r = NODES.oneOf()
      nondet req = renewReqs.oneOf()
      doMutantRenewAfterSeal(r, req),
    },
  }

  action mutantTakeoverSkipsLeaseWait = any {
    step,
    all {
      sealReplies.filter(sr => sr.ledger == openLedger).size() >= QUORUM,
      nondet sealer = NODES.oneOf()
      nondet newOwner = NODES.oneOf()
      nondet r1 = sealReplies.filter(sr => sr.ledger == openLedger).oneOf()
      nondet r2 = sealReplies.filter(sr => sr.ledger == openLedger).oneOf()
      doMutantTakeoverSkipsLeaseWait(sealer, newOwner, Set(r1, r2)),
    },
  }

  action mutantPrimaryForgetsDriftFactor = any {
    step,
    all {
      renewReqs.size() > 0,
      nondet req = renewReqs.oneOf()
      doMutantPrimaryForgetsDriftFactor(req),
    },
  }

  action mutantReplicaRestartForgetsBootHorizon = any {
    step,
    nondet r = NODES.oneOf()
    doMutantReplicaRestartForgetsBootHorizon(r),
  }

  action mutantCooperativeWithoutReleased = any {
    step,
    all {
      sealReplies.filter(sr => sr.ledger == openLedger).size() >= QUORUM,
      nondet sealer = NODES.oneOf()
      nondet newOwner = NODES.oneOf()
      nondet r1 = sealReplies.filter(sr => sr.ledger == openLedger).oneOf()
      nondet r2 = sealReplies.filter(sr => sr.ledger == openLedger).oneOf()
      doMutantCooperativeWithoutReleased(sealer, newOwner, Set(r1, r2)),
    },
  }

  action mutantCooperativeForgetsSurrender = any {
    step,
    nondet n = NODES.oneOf()
    nondet l = nodes.get(n).releasing.oneOf()
    doMutantCooperativeForgetsSurrender(n, l),
  }

  action mutantEffectWithoutLeaseCheck = any {
    step,
    nondet n = NODES.oneOf()
    doMutantEffectWithoutLeaseCheck(n),
  }

  // ------------------------------------------------------------------ Invariants
  // Core ledger-replica lease theorem: once a ledger is Closed in LedgerMap,
  // its primary can never hold a valid local lease on it. (Consequently, no
  // two ledgers can ever hold overlapping valid leases, even across chained
  // takeovers.)
  val closedLedgerHasNoValidLease: bool =
    closedLedgers.forall(l => not(hasValidLease(ledgerPrimary.get(l), l)))

  // Single-activation: at most one node with a valid lease on its active ledger
  // holds an active grain activation at any instant.
  val singleActiveOwner: bool =
    NODES.filter(n => {
      val ns = nodes.get(n)
      ns.hasActiveGrain and hasValidLease(n, ns.activeAtLedger)
    }).size() <= 1

  // No zombie activation, unreleased response, or out-of-epoch effect from an
  // older ledger under a valid lease.
  val noPostReleaseZombie: bool = all {
    not(zombieEffectObserved),
    NODES.forall(n => {
      val ns = nodes.get(n)
      if ((ns.hasActiveGrain or ns.pendingRelease) and hasValidLease(n, ns.activeAtLedger))
        ns.activeAtLedger == openLedger
      else
        true
    }),
  }

  // Once L4 fences a ledger via Releasing or Lost, it never serves that ledger
  // (or older) again.
  val fencedLedgerNeverServed: bool =
    NODES.forall(n => {
      val ns = nodes.get(n)
      ns.l4ServedLedger == 0 or ns.l4ServedLedger > ns.l4FencedLedger
    })

  val safety: bool = all {
    closedLedgerHasNoValidLease,
    singleActiveOwner,
    noPostReleaseZombie,
    fencedLedgerNeverServed,
  }
}

module rangeHandoffSim {
  import rangeHandoff(
    NODES = Set(1, 2, 3),
    MAX_LEDGER = 4,
    D = 4,
    MAX_CLOCK = 24,
    MAX_RENEWS = 6,
    MAX_PENDING_TXS = 6
  ).*

  // Test 1: Planned cooperative PrepareRelease achieves 0 ms wait handoff.
  run cooperativeZeroWaitTest =
    init
      .then(primaryStartRenew(1))
      .then(replicaGrantLease(1, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(replicaGrantLease(2, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(primaryAcceptLease({ id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(activateGrain(1))
      .then(stageResponse(1))
      .then(controllerSendPrepareRelease(1, 1))
      .then(beginReleasing({ ledger: 1, target: 1 }))
      .then(completePrepareRelease(1, 1))
      .then(replicaSeal(1, 1))
      .then(replicaSeal(2, 1))
      .then(executorPrepareTransition(2, 2, Cooperative, Set(
        { ledger: 1, replica: 1, remaining: 4 },
        { ledger: 1, replica: 2, remaining: 4 }
      )))
      // 0 ticks elapsed: commits immediately!
      .then(commitTransition({ sealer: 2, oldLedger: 1, newOwner: 2, kind: Cooperative, waitUntil: 0 }))
      .then(deliverAcquire(2, 2))
      .then(primaryStartRenew(2))
      .then(replicaGrantLease(2, { id: 2, ledger: 2, primary: 2, sentAt: 0 }))
      .then(replicaGrantLease(3, { id: 2, ledger: 2, primary: 2, sentAt: 0 }))
      .then(primaryAcceptLease({ id: 2, ledger: 2, primary: 2, sentAt: 0 }))
      .then(activateGrain(2))
      .then(emitEffect(2))
      .expect(safety and openLedger == 2 and highestEffectLedger == 2)

  // Test 2: Unplanned takeover of a LIVE primary under worst-case clock drift
  // (primary slow rate 1, sealer fast rate 2) waits maxRem * F and preserves safety.
  run unplannedTakeoverWorstCaseDriftTest =
    init
      .then(primaryStartRenew(1))
      .then(replicaGrantLease(1, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(replicaGrantLease(2, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(primaryAcceptLease({ id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(activateGrain(1))
      .then(emitEffect(1))
      // Seal replicas 2 and 3 (intersects {1, 2} at replica 2 with rem = 4).
      .then(replicaSeal(2, 1))
      .then(replicaSeal(3, 1))
      .then(executorPrepareTransition(3, 2, TakenOver, Set(
        { ledger: 1, replica: 2, remaining: 4 },
        { ledger: 1, replica: 3, remaining: 0 }
      )))
      // Sealer 3 runs at max rate 2 (fast = -1 means nodes 2 & 3 advance by 2, node 1 by 1).
      // Sealer must wait until clock 4 (2 ticks); node 1's lease expires at clock 2 (2 ticks).
      .then(tick(-1))
      .then(tick(-1))
      .then(commitTransition({ sealer: 3, oldLedger: 1, newOwner: 2, kind: TakenOver, waitUntil: 4 }))
      .then(deliverAcquire(2, 2))
      .then(primaryStartRenew(2))
      .then(replicaGrantLease(2, { id: 2, ledger: 2, primary: 2, sentAt: 4 }))
      .then(replicaGrantLease(3, { id: 2, ledger: 2, primary: 2, sentAt: 4 }))
      .then(primaryAcceptLease({ id: 2, ledger: 2, primary: 2, sentAt: 4 }))
      .then(activateGrain(2))
      .then(emitEffect(2))
      .expect(safety and openLedger == 2 and highestEffectLedger == 2)

  // Test 3: Dead/idle primary whose lease expired > D ago seals with remaining = 0
  // (0 ms wait on unplanned takeover!).
  run deadPrimaryZeroWaitTakeoverTest =
    init
      .then(primaryStartRenew(1))
      .then(replicaGrantLease(1, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(replicaGrantLease(2, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(primaryAcceptLease({ id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(activateGrain(1))
      // Advance 4 ticks (D = 4): all replicas' grants have expired.
      .then(tick(0)).then(tick(0)).then(tick(0)).then(tick(0))
      .then(replicaSeal(2, 1))
      .then(replicaSeal(3, 1))
      .then(executorPrepareTransition(3, 2, TakenOver, Set(
        { ledger: 1, replica: 2, remaining: 0 },
        { ledger: 1, replica: 3, remaining: 0 }
      )))
      // Commits immediately with 0 extra wait!
      .then(commitTransition({ sealer: 3, oldLedger: 1, newOwner: 2, kind: TakenOver, waitUntil: 4 }))
      .expect(safety and openLedger == 2)

  // Test 4: Replica crashes & restarts right after granting a lease — bootHorizon
  // conservatively preserves safety without any WAL write for the renewal!
  run replicaRestartBootHorizonTest =
    init
      .then(primaryStartRenew(1))
      .then(replicaGrantLease(1, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(replicaGrantLease(2, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(primaryAcceptLease({ id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(activateGrain(1))
      // Replica 2 crashes & restarts (wipes RAM grantedUntil, sets bootHorizon = clock + 4).
      .then(replicaRestart(2))
      // Sealer seals replicas 2 and 3; replica 2 reports rem = 4 from bootHorizon.
      .then(replicaSeal(2, 1))
      .then(replicaSeal(3, 1))
      .then(executorPrepareTransition(3, 2, TakenOver, Set(
        { ledger: 1, replica: 2, remaining: 4 },
        { ledger: 1, replica: 3, remaining: 0 }
      )))
      .then(tick(-1)).then(tick(-1))
      .then(commitTransition({ sealer: 3, oldLedger: 1, newOwner: 2, kind: TakenOver, waitUntil: 4 }))
      .expect(safety and openLedger == 2)

  // Test 5: Chained takeover A -> B -> C with zero predecessor tracking in LedgerMap.
  run chainedTakeoverTest =
    init
      .then(primaryStartRenew(1))
      .then(replicaGrantLease(1, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(replicaGrantLease(2, { id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(primaryAcceptLease({ id: 1, ledger: 1, primary: 1, sentAt: 0 }))
      .then(activateGrain(1))
      .then(emitEffect(1))
      // Takeover 1 -> 2
      .then(replicaSeal(2, 1))
      .then(replicaSeal(3, 1))
      .then(executorPrepareTransition(3, 2, TakenOver, Set(
        { ledger: 1, replica: 2, remaining: 4 },
        { ledger: 1, replica: 3, remaining: 0 }
      )))
      .then(tick(-1)).then(tick(-1))
      .then(commitTransition({ sealer: 3, oldLedger: 1, newOwner: 2, kind: TakenOver, waitUntil: 4 }))
      .then(deliverAcquire(2, 2))
      .then(primaryStartRenew(2))
      .then(replicaGrantLease(2, { id: 2, ledger: 2, primary: 2, sentAt: 4 }))
      .then(replicaGrantLease(3, { id: 2, ledger: 2, primary: 2, sentAt: 4 }))
      .then(primaryAcceptLease({ id: 2, ledger: 2, primary: 2, sentAt: 4 }))
      .then(activateGrain(2))
      .then(emitEffect(2))
      // Immediate cooperative handoff 2 -> 3
      .then(controllerSendPrepareRelease(2, 2))
      .then(beginReleasing({ ledger: 2, target: 2 }))
      .then(completePrepareRelease(2, 2))
      .then(replicaSeal(1, 2))
      .then(replicaSeal(3, 2))
      .then(executorPrepareTransition(1, 3, Cooperative, Set(
        { ledger: 2, replica: 1, remaining: 0 },
        { ledger: 2, replica: 3, remaining: 4 }
      )))
      .then(commitTransition({ sealer: 1, oldLedger: 2, newOwner: 3, kind: Cooperative, waitUntil: 2 }))
      .then(deliverAcquire(3, 3))
      .then(primaryStartRenew(3))
      .then(replicaGrantLease(1, { id: 3, ledger: 3, primary: 3, sentAt: 4 }))
      .then(replicaGrantLease(3, { id: 3, ledger: 3, primary: 3, sentAt: 4 }))
      .then(primaryAcceptLease({ id: 3, ledger: 3, primary: 3, sentAt: 4 }))
      .then(activateGrain(3))
      .then(emitEffect(3))
      .expect(safety and openLedger == 3 and highestEffectLedger == 3)
}

module rangeHandoffTlc {
  // Compact bounds for exhaustive TLC verification: N=2 with QUORUM=2 and D=2
  // (LEASE_LEN = D/F = 1) preserves lease renewal, expiry, clock-skew wait,
  // replica bootHorizon, and both forced and cooperative handoff while
  // exhausting the full state space.
  import rangeHandoff(
    NODES = Set(1, 2),
    MAX_LEDGER = 2,
    D = 2,
    MAX_CLOCK = 2,
    MAX_RENEWS = 1,
    MAX_PENDING_TXS = 1
  ).*
}
