// -*- mode: Bluespec; -*-
/**
 * Closed-ledger local-stream drain barrier (LG-I15, LG-I16, JS-I11, JS-I12):
 *
 * Models a ledger L carrying journal rows ("K") and ledger-local SetWakeup appends ("S" = set wakeup,
 * "C" = clear wakeup) on both archive backends:
 *   - Mode = "lsm": up-front ILedgerLocalDrainBarrier.EnsureLocalStreamsDrainedAsync(L, T) before
 *     DrainWorker scans/uploads and commits ManifestChange.Drain(..., complete: true), covering both
 *     a < T and a == T with zero extra manifest commits, followed by LedgerDropper Drop(L).
 *   - Mode = "logs": ArchiveLogDrainWorker keyed drain [0, T), before-drop EnsureLocalStreamsDrainedAsync(L, T),
 *     marking Complete, and submitting Drop(L), plus the dedicated eager-drain slot for Waiting closed
 *     ledgers blocked behind an older overlapping ledger (LG-I10).
 *
 * Also models:
 *   - Primary volatile WakeupLedgerSession timer delivery while L is open vs primary crash losing
 *     the volatile timer heap before L closes.
 *   - Last-write-wins SetWakeup fold over [0, T) in WakeupStreamProcessor, skipping backing
 *     ScheduleJobAsync calls when cleared before close (flushes == 0).
 *   - Mid-ledger chunked flushes when ByteBudget.TryGrow fails (flushing fold, clearing memory,
 *     releasing budget lease, and continuing without deadlock).
 *   - Concurrent racing drainers, backpressure, volatile backing writes before durability, and
 *     crashes at every step.
 */
module LocalDrain {
  // Archive backend: "lsm" or "logs".
  const Mode: str
  // Decided sequence of operations the primary may write to ledger L ("S", "C", "K").
  const Slots: List[str]
  // Initial LSM watermark a (0 <= a <= Slots.length()); setting a == Slots.length() models the LSM a == T path.
  const InitialLsmWatermark: int
  // Whether an older overlapping ledger is initially listed in LedgerMap (blocks Archive Logs keyed drain and Drop).
  const InitialOlderListed: bool
  // Whether the older overlapping ledger may drop during the run (false tests eager Waiting drain liveness).
  const OlderCanDrop: bool
  // Number of concurrent drain workers (1 or 2).
  const Drainers: int
  // Whether mid-ledger ByteBudget chunked flushes are enabled.
  const AllowChunkFlush: bool
  // Maximum chunked flushes per run (bounds state space while exercising mid-ledger flushes).
  const MaxFlushes: int
  // Maximum drainer/backing crashes and backpressure aborts per run (exhausts all crash cut points while keeping liveness finite).
  const MaxCrashes: int
  // Guard-disabling mutation name, or "none".
  const Mutation: str

  pure val T: int = Slots.length()

  assume ValidParams = and {
    Mode == "lsm" or Mode == "logs",
    T >= 1,
    InitialLsmWatermark >= 0,
    InitialLsmWatermark <= T,
    Drainers >= 1,
    MaxFlushes >= 0,
    MaxCrashes >= 0,
  }

  type DrainPhase =
    | DIdle
    | DScanning
    | DChunkFlushing
    | DCommitting
    | DCommitPending
    | DBarrierDone
    | DVolatileBarrierDone

  type Drainer = {
    phase: DrainPhase,
    pos: int,
    fold: bool,
  }

  type State = {
    // Open-ledger primary state.
    decided: int,
    closed: bool,
    primaryAlive: bool,
    primaryArmed: bool,
    deliveredSlot: int,

    // LedgerMap and lineage state.
    listed: bool,
    dropped: bool,
    olderListed: bool,

    // Archive backend state.
    lsmWatermark: int,
    lsmComplete: bool,
    logsKeyedDone: bool,
    logsComplete: bool,

    // Local-stream drain barrier and backing durable job manager state.
    drainers: int -> Drainer,
    durableBarrierDrained: bool,
    backingPending: bool,
    backingDurable: bool,
    backingWrites: int,
    flushes: int,
    drainerCrashes: int,
    racedDrainers: bool,
  }

  pure val idleDrainer: Drainer = { phase: DIdle, pos: 0, fold: false }

  // Index of the latest active "S" in Slots[0..n-1] not superseded by a later "C", or -1 if none.
  pure def activeSlot(n: int): int =
    range(0, n).foldl(-1, (acc, i) =>
      if (Slots[i] == "S") i
      else if (Slots[i] == "C") -1
      else acc)

  pure def isBarrierInProgress(phase: DrainPhase): bool =
    phase == DScanning or phase == DChunkFlushing or phase == DCommitting or phase == DCommitPending

  pure def isBarrierDone(phase: DrainPhase): bool =
    phase == DBarrierDone or phase == DVolatileBarrierDone

  pure def isBackingInFlight(phase: DrainPhase): bool =
    phase == DChunkFlushing or phase == DCommitPending

  var s: State

  def otherActive(d: int): bool =
    1.to(Drainers).exists(o => o != d and isBarrierInProgress(s.drainers.get(o).phase))

  def otherBackingInFlight(d: int): bool =
    1.to(Drainers).exists(o => o != d and isBackingInFlight(s.drainers.get(o).phase))

  def setDrainer(d: int, nextState: Drainer): int -> Drainer =
    s.drainers.set(d, nextState)

  action init = {
    val initDecided = InitialLsmWatermark
    s' = {
      decided: initDecided,
      closed: false,
      primaryAlive: true,
      primaryArmed: activeSlot(initDecided) >= 0,
      deliveredSlot: -1,
      listed: true,
      dropped: false,
      olderListed: InitialOlderListed,
      lsmWatermark: InitialLsmWatermark,
      lsmComplete: false,
      logsKeyedDone: false,
      logsComplete: false,
      drainers: 1.to(Drainers).mapBy(_ => idleDrainer),
      durableBarrierDrained: false,
      backingPending: false,
      backingDurable: false,
      backingWrites: 0,
      flushes: 0,
      drainerCrashes: 0,
      racedDrainers: false,
    }
  }

  // Primary writes the next decided slot on open ledger L and updates its volatile WakeupLedgerSession heap (JS-I11, JS-I12).
  action primaryWrite = {
    val op = Slots[s.decided]
    val nextArmed =
      if (op == "S") true
      else if (op == "C") false
      else s.primaryArmed
    all {
      not(s.closed),
      s.primaryAlive,
      s.decided < T,
      s' = {
        ...s,
        decided: s.decided + 1,
        primaryArmed: nextArmed,
      },
    }
  }

  // Primary's WakeupLedgerSession timer fires while L is open, delivering the wakeup with zero backing writes.
  action primaryFireWakeup = {
    val target = activeSlot(s.decided)
    all {
      not(s.closed),
      s.primaryAlive,
      s.primaryArmed,
      target >= 0,
      s' = {
        ...s,
        primaryArmed: false,
        deliveredSlot: target,
      },
    }
  }

  // Primary crashes while L is open, losing its volatile WakeupLedgerSession timer heap.
  action crashPrimary = all {
    not(s.closed),
    s.primaryAlive,
    s.decided > 0,
    s' = {
      ...s,
      primaryAlive: false,
      primaryArmed: false,
    },
  }

  // Ledger L closes in LedgerMap at its decided tail (roll, handoff, or takeover after primary crash).
  action closeLedger = all {
    not(s.closed),
    s.decided > 0,
    s' = {
      ...s,
      closed: true,
      primaryArmed: false,
    },
  }

  // Older overlapping ledger drops from LedgerMap, unblocking Archive Logs keyed drain and Drop(L).
  action dropOlderLedger = all {
    s.olderListed,
    OlderCanDrop,
    s' = {
      ...s,
      olderListed: false,
    },
  }

  // Drainer d starts EnsureLocalStreamsDrainedAsync(L, T) on closed ledger L.
  // In LSM mode this runs up front before keyed drain; in Archive Logs mode it runs before Drop and
  // also eagerly on a dedicated background slot while L is Waiting behind an older overlapping ledger.
  action startBarrier(d: int): bool = all {
    s.closed,
    s.listed,
    s.drainers.get(d).phase == DIdle,
    Mutation != "no-eager-waiting-drain" or Mode != "logs" or not(s.olderListed),
    s' = {
      ...s,
      drainers: setDrainer(d, { phase: DScanning, pos: 0, fold: false }),
      racedDrainers: s.racedDrainers or otherActive(d),
    },
  }

  // Drainer d scans one batch slot in [0, decided), folding RowKind.LocalAppend ("S" / "C") in slot order.
  // If L was already dropped from LedgerMap, the scan observes Dropped and returns without marking drained.
  action scanSlot(d: int): bool = {
    val cur = s.drainers.get(d)
    all {
      cur.phase == DScanning,
      cur.pos < s.decided,
      if (not(s.listed))
        s' = {
          ...s,
          drainers: setDrainer(d, idleDrainer),
        }
      else
        val op = Slots[cur.pos]
        val nextFold =
          if (op == "S") true
          else if (op == "C") (if (Mutation == "flush-cleared-entry") cur.fold else false)
          else cur.fold
        val nextPos = cur.pos + 1
        val nextPhase = if (nextPos == s.decided) DCommitting else DScanning
        s' = {
          ...s,
          drainers: setDrainer(d, { phase: nextPhase, pos: nextPos, fold: nextFold }),
        },
    }
  }

  // Drainer d hits replica backpressure during scan and aborts with LocalDrainBarrierOutcome.Backpressure.
  // Mutant barrier-complete-on-backpressure erroneously returns Drained / caches _completed on Backpressure.
  action scanBackpressure(d: int): bool = {
    val cur = s.drainers.get(d)
    all {
      cur.phase == DScanning,
      cur.pos < s.decided,
      s.drainerCrashes < MaxCrashes,
      s' = {
        ...s,
        drainers: setDrainer(
          d,
          if (Mutation == "barrier-complete-on-backpressure") { ...cur, phase: DVolatileBarrierDone }
          else idleDrainer),
        drainerCrashes: s.drainerCrashes + 1,
      },
    }
  }

  // Mid-ledger chunked flush when ByteBudget.TryGrow fails: commits current fold, clears memory,
  // releases budget lease, and continues scanning without deadlocking.
  action beginChunkFlush(d: int): bool = {
    val cur = s.drainers.get(d)
    all {
      AllowChunkFlush,
      cur.phase == DScanning,
      cur.pos > 0,
      cur.pos < s.decided,
      cur.fold,
      s.flushes < MaxFlushes,
      not(s.backingPending),
      s' = {
        ...s,
        drainers: setDrainer(d, { ...cur, phase: DChunkFlushing }),
        backingPending: true,
        flushes: s.flushes + 1,
      },
    }
  }

  // Backing ScheduleJobAsync completes durably for drainer d's mid-ledger chunked flush.
  action finishChunkFlush(d: int): bool = {
    val cur = s.drainers.get(d)
    all {
      cur.phase == DChunkFlushing,
      s.backingPending,
      s' = {
        ...s,
        drainers: setDrainer(d, { ...cur, phase: DScanning, fold: false }),
        backingPending: false,
        backingDurable: true,
        backingWrites: s.backingWrites + 1,
      },
    }
  }

  // Drainer d finishes scanning [0, decided) and enters CommitAsync.
  // When fold is false (cleared before close or no wakeups), CommitAsync performs zero backing writes.
  action beginCommit(d: int): bool = {
    val cur = s.drainers.get(d)
    all {
      cur.phase == DCommitting,
      if (not(cur.fold))
        s' = {
          ...s,
          drainers: setDrainer(d, { ...cur, phase: DBarrierDone }),
          durableBarrierDrained: true,
        }
      else
        all {
          not(s.backingPending),
          s' = {
            ...s,
            drainers: setDrainer(d, { ...cur, phase: DCommitPending }),
            backingPending: true,
          },
        },
    }
  }

  // Backing ScheduleJobAsync finishes for drainer d's final CommitAsync.
  // Mutant schedule-volatile answers Drained while backingPending is still volatile (not yet backingDurable).
  // Mutant skip-wakeup-commit answers Drained without issuing any backing write.
  action finishCommit(d: int): bool = {
    val cur = s.drainers.get(d)
    all {
      cur.phase == DCommitPending,
      s.backingPending,
      if (Mutation == "schedule-volatile")
        s' = {
          ...s,
          drainers: setDrainer(d, { ...cur, phase: DVolatileBarrierDone }),
          backingWrites: s.backingWrites + 1,
        }
      else if (Mutation == "skip-wakeup-commit")
        s' = {
          ...s,
          drainers: setDrainer(d, { ...cur, phase: DVolatileBarrierDone }),
          backingPending: false,
        }
      else
        s' = {
          ...s,
          drainers: setDrainer(d, { ...cur, phase: DBarrierDone }),
          backingPending: false,
          backingDurable: true,
          backingWrites: s.backingWrites + 1,
          durableBarrierDrained: true,
        },
    }
  }

  // Volatile backing write is lost on crash before becoming durable (harmless in the conforming model
  // because the barrier never answered Drained; breaks WakeupPreserved under schedule-volatile).
  action loseVolatileBacking = all {
    s.backingPending,
    not(s.backingDurable),
    s.drainerCrashes < MaxCrashes,
    s' = {
      ...s,
      backingPending: false,
      drainerCrashes: s.drainerCrashes + 1,
      drainers: 1.to(Drainers).mapBy(d =>
        if (isBackingInFlight(s.drainers.get(d).phase)) idleDrainer else s.drainers.get(d)),
    },
  }

  // Drainer d crashes at any non-idle step, losing its volatile scan/fold/cache state.
  action crashDrainer(d: int): bool = {
    val cur = s.drainers.get(d)
    val clearPending = isBackingInFlight(cur.phase) and not(otherBackingInFlight(d))
    all {
      cur.phase != DIdle,
      s.drainerCrashes < MaxCrashes,
      s' = {
        ...s,
        drainers: setDrainer(d, idleDrainer),
        backingPending: if (clearPending) false else s.backingPending,
        drainerCrashes: s.drainerCrashes + 1,
      },
    }
  }

  // LSM DrainWorker commits ManifestChange.Drain(..., complete: true) after the up-front barrier
  // answered Drained (covers both a < T and a == T in one manifest commit).
  action lsmCompleteDrain(d: int): bool = {
    val barrierOk = or {
      isBarrierDone(s.drainers.get(d).phase),
      Mutation == "lsm-complete-before-barrier",
      Mutation == "lsm-skip-barrier-at-end" and s.lsmWatermark == s.decided,
    }
    all {
      Mode == "lsm",
      s.closed,
      s.listed,
      not(s.lsmComplete),
      barrierOk,
      s' = {
        ...s,
        lsmWatermark: s.decided,
        lsmComplete: true,
      },
    }
  }

  // Archive Logs ArchiveLogDrainWorker drains keyed segments [0, T) once no older overlapping ledger is listed.
  action logsDrainKeyed = all {
    Mode == "logs",
    s.closed,
    s.listed,
    not(s.olderListed),
    not(s.logsKeyedDone),
    s' = {
      ...s,
      logsKeyedDone: true,
    },
  }

  // Archive Logs marks the closed ledger Complete after keyed segments [0, T) and the local drain barrier completed.
  action logsMarkComplete(d: int): bool = {
    val barrierOk = or {
      isBarrierDone(s.drainers.get(d).phase),
      Mutation == "logs-drop-before-barrier",
    }
    all {
      Mode == "logs",
      s.closed,
      s.listed,
      s.logsKeyedDone,
      not(s.logsComplete),
      barrierOk,
      s' = {
        ...s,
        logsComplete: true,
      },
    }
  }

  // LedgerMapChange.Drop(L): removes closed, complete ledger L from LedgerMap once lineage is clear (LG-I10, LG-I16).
  action dropLedger = all {
    s.closed,
    s.listed,
    not(s.olderListed),
    (Mode == "lsm" and s.lsmComplete) or (Mode == "logs" and s.logsComplete),
    s' = {
      ...s,
      listed: false,
      dropped: true,
    },
  }

  // Backing ILocalDurableJobManager fires the durable wakeup job and delivers it to the grain.
  action backingFireWakeup = {
    val target = activeSlot(s.decided)
    all {
      s.backingDurable,
      target >= 0,
      s.deliveredSlot < target,
      s' = {
        ...s,
        deliveredSlot: target,
      },
    }
  }

  // Stutter once no further progress is required, keeping infinite traces well-defined for TLC liveness checks.
  action stutter = all {
    s.closed,
    s' = s,
  }

  action step = any {
    primaryWrite,
    primaryFireWakeup,
    crashPrimary,
    closeLedger,
    dropOlderLedger,
    logsDrainKeyed,
    dropLedger,
    backingFireWakeup,
    loseVolatileBacking,
    stutter,
    all {
      nondet d = 1.to(Drainers).oneOf()
      any {
        startBarrier(d),
        scanSlot(d),
        scanBackpressure(d),
        beginChunkFlush(d),
        finishChunkFlush(d),
        beginCommit(d),
        finishCommit(d),
        crashDrainer(d),
        lsmCompleteDrain(d),
        logsMarkComplete(d),
      },
    },
  }

  // ---------------------------------------------------------------------------
  // Safety invariants
  // ---------------------------------------------------------------------------

  val TypeOK: bool = and {
    s.decided >= 0 and s.decided <= T,
    s.lsmWatermark >= 0 and s.lsmWatermark <= T,
    s.deliveredSlot >= -1 and s.deliveredSlot < T,
    s.backingWrites >= 0,
    s.flushes >= 0 and s.flushes <= MaxFlushes,
    s.drainerCrashes >= 0 and s.drainerCrashes <= MaxCrashes,
    s.dropped == not(s.listed),
    1.to(Drainers).forall(d =>
      s.drainers.get(d).pos >= 0 and s.drainers.get(d).pos <= T),
  }

  // Unconditional across chunked flushes and crashes: every surviving undelivered SetWakeup is either
  // armed on a live open primary, still readable in a listed ledger, or durably persisted in the backing job manager.
  val WakeupPreserved: bool = {
    val target = activeSlot(s.decided)
    (target >= 0 and s.deliveredSlot < target) implies or {
      not(s.closed) and s.primaryAlive and s.primaryArmed,
      s.listed,
      s.backingDurable,
    }
  }

  // LG-I16: a closed ledger L with T > 0 is neither marked complete in the LSM manifest nor marked complete
  // or dropped on Archive Logs until EnsureLocalStreamsDrainedAsync(L, T) has durably completed.
  val NoDropBeforeLocalDrained: bool = and {
    s.lsmComplete implies s.durableBarrierDrained,
    s.logsComplete implies s.durableBarrierDrained,
    s.dropped implies s.durableBarrierDrained,
  }

  // When a wakeup is cleared before ledger close and no mid-ledger chunked flush ran (flushes == 0),
  // closed-ledger drain issues zero ScheduleJobAsync calls to the backing job manager.
  val ClearedBeforeCloseWritesNothing: bool =
    (s.closed and activeSlot(s.decided) < 0 and s.flushes == 0) implies s.backingWrites == 0

  // ---------------------------------------------------------------------------
  // Weak-fairness liveness properties
  // ---------------------------------------------------------------------------

  temporal Fairness = and {
    1.to(Drainers).forall(d => and {
      startBarrier(d).weakFair(s),
      scanSlot(d).weakFair(s),
      finishChunkFlush(d).weakFair(s),
      beginCommit(d).weakFair(s),
      finishCommit(d).weakFair(s),
    }),
    backingFireWakeup.weakFair(s),
  }

  // Once a ledger closes, its local streams are eventually durably drained (even while Archive Logs is Waiting on an older ledger).
  temporal EventuallyLocalDrained =
    Fairness implies (s.closed).leadsTo(s.durableBarrierDrained)

  // Once a ledger closes with a surviving SetWakeup, that wakeup is eventually delivered to the grain.
  temporal EventuallyWakeupDelivered =
    Fairness implies (s.closed and activeSlot(s.decided) >= 0).leadsTo(s.deliveredSlot >= activeSlot(s.decided))

  // ---------------------------------------------------------------------------
  // Non-vacuity witnesses (each must be violated in the unmutated model)
  // ---------------------------------------------------------------------------

  // A primary crashes after writing "S"; closed-ledger drain schedules the backing job and delivers the wakeup.
  val NeverSurvivingCrashWakeup: bool =
    not(s.closed and not(s.primaryAlive) and s.backingDurable and s.deliveredSlot >= 0)

  // A wakeup set then cleared before close drains and drops with zero backing writes and zero flushes.
  val NeverClearedBeforeClose: bool =
    not(s.closed and s.dropped and activeSlot(s.decided) < 0 and s.flushes == 0 and s.backingWrites == 0)

  // LSM a == T path: watermark already at T before close; barrier runs, LSM marks complete, and L drops.
  val NeverLsmAlreadyDrainedPath: bool =
    not(Mode == "lsm" and InitialLsmWatermark == T and s.lsmComplete and s.durableBarrierDrained and s.dropped)

  // Two drainers concurrently scan/commit the same closed ledger.
  val NeverRacingDrainers: bool =
    not(s.racedDrainers and s.durableBarrierDrained)

  // A mid-ledger chunked flush executes and the closed ledger still drains and drops cleanly.
  val NeverChunkedFlush: bool =
    not(s.flushes > 0 and s.dropped)
}
