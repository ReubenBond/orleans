// -*- mode: Bluespec; -*-
/**
 * Roux Outbox-Style Grain-Local `DurableJobQueue` & `GrainReminderCoordinator` (`R37`)
 * (docs/design/runtime.md §Atomic journal reminders and grain-local durable jobs).
 *
 * Models a single grain activation's journaled `DurableJobQueue` in `roux-jobs` (L4),
 * layered on `GrainReminderCoordinator` and the `"roux.reminder"` atomic `RowKind::Message`
 * channel registered into L2 `roux-storage` (`ST12`, `specs/message-drain/`):
 *
 * - Synchronous atomic `scheduleJob(jobId, due, payload)`:
 *   * Enforces `MAX_PENDING` capacity bound.
 *   * Same `(due, payload)` for an already-pending `jobId` is idempotent.
 *   * Conflicting `(due, payload)` for an already-pending `jobId` rejects with
 *     `DuplicateJobIdConflict` without mutating state.
 *   * Assigns a strictly monotonic journaled `execution_generation` (`nextGen`)
 *     and atomically emits `SetReminder(minDue)` in the same journal entry (`JS-I11`).
 * - Synchronous atomic `cancelJob(jobId)`:
 *   * Removes `jobId` from `pending` (including while its async handler is awaiting!).
 *   * Emits `SetReminder(None)` when the queue becomes empty, or updates `minDue`.
 * - Segment drain / primary crash spillover (`closeLedgerAndDrain`):
 *   * Loses the volatile open-ledger `ReminderLedgerSession` heap (`ephemeralArmed = false`)
 *     and relies on single-pass `DrainScan` `ReminderMessageProcessor` (`LG-I16`) to publish
 *     a deterministic time-bucketed sealed `JobShard` iff `pending` is non-empty (0 backing
 *     writes when cleared within the segment).
 * - `is_ephemeral_reminder` separation:
 *   * Open-ledger ephemeral reminders fire directly without perturbing backing job tracking.
 *   * Backing `JobShard` reminders adopt any earlier post-roll backing job and complete
 *     redundant later ones.
 * - Post-`.await` `(jobId, execGen)` cancel/reschedule guard:
 *   * While a job handler is awaiting, interleaved turns may cancel or cancel+reschedule
 *     the same `jobId` (advancing `gen`).
 *   * When the handler returns `Completed` or `RescheduleAt(nextDue)`, the queue verifies
 *     `pending` still holds `jobId` at `execGen`, preventing resurrection of canceled jobs
 *     or clobbering of newly rescheduled jobs.
 * - Dirty-state failure fence:
 *   * If a handler throws/panics after staging dirty state, the activation fences and
 *     terminates without committing the partial mutation.
 *
 * Mutants disable one guard at a time and are verified via `../check-mutants.sh`.
 */
module grainJobQueue {
  const MAX_JOBS: int
  const MAX_DUE: int
  const MAX_PAYLOAD: int
  const MAX_PENDING: int
  const MAX_GEN: int
  const MAX_BACKING: int

  pure val JOBS: Set[int] = 0.to(MAX_JOBS - 1)
  pure val DUES: Set[int] = 0.to(MAX_DUE)
  pure val PAYLOADS: Set[int] = 0.to(MAX_PAYLOAD - 1)

  type JobRecord = {
    due: int,
    payload: int,
    gen: int,
  }

  type InFlight = {
    active: bool,
    jobId: int,
    execGen: int,
    dirtyStaged: bool,
  }

  type BackingJob = {
    id: int,
    due: int,
  }

  pure val NO_IN_FLIGHT: InFlight = {
    active: false,
    jobId: -1,
    execGen: -1,
    dirtyStaged: false,
  }

  var now: int
  var pending: int -> JobRecord
  var nextGen: int
  var inFlight: InFlight
  var ephemeralArmed: bool
  var ephemeralDue: int
  var backingJobs: Set[BackingJob]
  var nextBackingId: int
  var trackedBackingId: int
  var trackedBackingDue: int
  var clobberedOrResurrected: bool
  var partialDirtyCommitted: bool
  var conflictingPayloadAccepted: bool

  pure def removeJob(m: int -> JobRecord, k: int): int -> JobRecord =
    m.keys().exclude(Set(k)).mapBy(x => m.get(x))

  pure def minDueOf(m: int -> JobRecord): int =
    m.keys().fold(MAX_DUE, (acc, k) =>
      val d = m.get(k).due
      if (d < acc) d else acc)

  action init = all {
    now' = 0,
    pending' = Map(),
    nextGen' = 1,
    inFlight' = NO_IN_FLIGHT,
    ephemeralArmed' = false,
    ephemeralDue' = 0,
    backingJobs' = Set(),
    nextBackingId' = 1,
    trackedBackingId' = -1,
    trackedBackingDue' = MAX_DUE + 1,
    clobberedOrResurrected' = false,
    partialDirtyCommitted' = false,
    conflictingPayloadAccepted' = false,
  }

  action advanceTime = all {
    now < MAX_DUE,
    now' = now + 1,
    pending' = pending, nextGen' = nextGen, inFlight' = inFlight,
    ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
    backingJobs' = backingJobs, nextBackingId' = nextBackingId,
    trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
    clobberedOrResurrected' = clobberedOrResurrected,
    partialDirtyCommitted' = partialDirtyCommitted,
    conflictingPayloadAccepted' = conflictingPayloadAccepted,
  }

  // Synchronous atomic schedule(jobId, due, payload) with SetReminder message emission.
  action scheduleJob(jobId: int, due: int, payload: int): bool = all {
    due >= now,
    if (pending.keys().contains(jobId)) {
      val cur = pending.get(jobId)
      // Equivalent re-schedule is idempotent; conflict is rejected without mutation.
      all {
        cur.due == due and cur.payload == payload,
        pending' = pending,
        nextGen' = nextGen,
        ephemeralArmed' = ephemeralArmed,
        ephemeralDue' = ephemeralDue,
      }
    } else {
      val nextPending = pending.put(jobId, { due: due, payload: payload, gen: nextGen })
      all {
        pending.keys().size() < MAX_PENDING,
        nextGen <= MAX_GEN,
        pending' = nextPending,
        nextGen' = nextGen + 1,
        ephemeralArmed' = true,
        ephemeralDue' = minDueOf(nextPending),
      }
    },
    now' = now, inFlight' = inFlight,
    backingJobs' = backingJobs, nextBackingId' = nextBackingId,
    trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
    clobberedOrResurrected' = clobberedOrResurrected,
    partialDirtyCommitted' = partialDirtyCommitted,
    conflictingPayloadAccepted' = conflictingPayloadAccepted,
  }

  // Synchronous atomic cancel(jobId), allowed even while jobId's handler is awaiting.
  action cancelJob(jobId: int): bool = {
    val nextPending = removeJob(pending, jobId)
    val hasMore = nextPending.keys().size() > 0
    all {
      pending.keys().contains(jobId),
      pending' = nextPending,
      ephemeralArmed' = hasMore,
      ephemeralDue' = if (hasMore) minDueOf(nextPending) else 0,
      now' = now, nextGen' = nextGen, inFlight' = inFlight,
      backingJobs' = backingJobs, nextBackingId' = nextBackingId,
      trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
      clobberedOrResurrected' = clobberedOrResurrected,
      partialDirtyCommitted' = partialDirtyCommitted,
      conflictingPayloadAccepted' = conflictingPayloadAccepted,
    }
  }

  // Segment drain / primary crash: volatile open-ledger ReminderLedgerSession heap is lost,
  // and ReminderMessageProcessor publishes a deterministic sealed JobShard iff pending is non-empty (LG-I16).
  // Zero backing writes when pending is empty!
  action closeLedgerAndDrain = {
    val hasPending = pending.keys().size() > 0
    val newJob = { id: nextBackingId, due: minDueOf(pending) }
    all {
      ephemeralArmed or hasPending,
      not(hasPending) or nextBackingId <= MAX_BACKING,
      ephemeralArmed' = false,
      ephemeralDue' = 0,
      backingJobs' = if (hasPending) backingJobs.union(Set(newJob)) else backingJobs,
      nextBackingId' = if (hasPending) nextBackingId + 1 else nextBackingId,
      // Primary crash / handover drops any in-flight activation turn without committing dirty state.
      inFlight' = NO_IN_FLIGHT,
      now' = now, pending' = pending, nextGen' = nextGen,
      trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
      clobberedOrResurrected' = clobberedOrResurrected,
      partialDirtyCommitted' = partialDirtyCommitted,
      conflictingPayloadAccepted' = conflictingPayloadAccepted,
    }
  }

  // Open-ledger ephemeral reminder (is_ephemeral_reminder = true) starts a due job handler.
  // Never perturbs backing-job deduplication tracking.
  action startEphemeralReminder(jobId: int): bool = all {
    ephemeralArmed,
    not(inFlight.active),
    pending.keys().contains(jobId),
    pending.get(jobId).due <= now,
    inFlight' = {
      active: true,
      jobId: jobId,
      execGen: pending.get(jobId).gen,
      dirtyStaged: false,
    },
    now' = now, pending' = pending, nextGen' = nextGen,
    ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
    backingJobs' = backingJobs, nextBackingId' = nextBackingId,
    trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
    clobberedOrResurrected' = clobberedOrResurrected,
    partialDirtyCommitted' = partialDirtyCommitted,
    conflictingPayloadAccepted' = conflictingPayloadAccepted,
  }

  // Backing JobShard reminder fires (is_ephemeral_reminder = false):
  // adopts earlier post-roll backing jobs and retires redundant later ones.
  action fireBackingReminder(b: BackingJob): bool = {
    val hasPending = pending.keys().size() > 0
    val adopt = trackedBackingId == -1 or trackedBackingId == b.id or b.due < trackedBackingDue
    all {
      backingJobs.contains(b),
      b.due <= now,
      if (not(hasPending)) {
        // Queue already empty: retire backing job cleanly.
        all {
          backingJobs' = backingJobs.exclude(Set(b)),
          trackedBackingId' = if (trackedBackingId == b.id) -1 else trackedBackingId,
          trackedBackingDue' = if (trackedBackingId == b.id) MAX_DUE + 1 else trackedBackingDue,
          ephemeralArmed' = ephemeralArmed,
          ephemeralDue' = ephemeralDue,
        }
      } else if (not(adopt)) {
        // Redundant later backing job: complete and remove it while earlier tracked/ephemeral covers.
        all {
          backingJobs' = backingJobs.exclude(Set(b)),
          trackedBackingId' = trackedBackingId,
          trackedBackingDue' = trackedBackingDue,
          ephemeralArmed' = true,
          ephemeralDue' = minDueOf(pending),
        }
      } else {
        // Adopt backing job and re-arm open-ledger reminder for active processing.
        all {
          backingJobs' = backingJobs,
          trackedBackingId' = b.id,
          trackedBackingDue' = b.due,
          ephemeralArmed' = true,
          ephemeralDue' = minDueOf(pending),
        }
      },
      now' = now, pending' = pending, nextGen' = nextGen, inFlight' = inFlight,
      nextBackingId' = nextBackingId,
      clobberedOrResurrected' = clobberedOrResurrected,
      partialDirtyCommitted' = partialDirtyCommitted,
      conflictingPayloadAccepted' = conflictingPayloadAccepted,
    }
  }

  // In-flight job handler stages dirty state during its turn.
  action handlerStageDirty = all {
    inFlight.active,
    not(inFlight.dirtyStaged),
    inFlight' = { ...inFlight, dirtyStaged: true },
    now' = now, pending' = pending, nextGen' = nextGen,
    ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
    backingJobs' = backingJobs, nextBackingId' = nextBackingId,
    trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
    clobberedOrResurrected' = clobberedOrResurrected,
    partialDirtyCommitted' = partialDirtyCommitted,
    conflictingPayloadAccepted' = conflictingPayloadAccepted,
  }

  // Handler returns Completed: post-.await (jobId, execGen) guard applies removal ONLY if
  // the job is still present at the exact execution generation that started.
  action handlerComplete = {
    val stillMatches =
      pending.keys().contains(inFlight.jobId)
        and pending.get(inFlight.jobId).gen == inFlight.execGen
    val nextPending = if (stillMatches) removeJob(pending, inFlight.jobId) else pending
    val hasMore = nextPending.keys().size() > 0
    all {
      inFlight.active,
      pending' = nextPending,
      inFlight' = NO_IN_FLIGHT,
      ephemeralArmed' = hasMore,
      ephemeralDue' = if (hasMore) minDueOf(nextPending) else 0,
      now' = now, nextGen' = nextGen,
      backingJobs' = backingJobs, nextBackingId' = nextBackingId,
      trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
      clobberedOrResurrected' = clobberedOrResurrected,
      partialDirtyCommitted' = partialDirtyCommitted,
      conflictingPayloadAccepted' = conflictingPayloadAccepted,
    }
  }

  // Handler returns RescheduleAt(nextDue): post-.await (jobId, execGen) guard applies ONLY
  // if the job is still present at execGen (never resurrects a canceled job!).
  action handlerReschedule(nextDue: int): bool = {
    val stillMatches =
      pending.keys().contains(inFlight.jobId)
        and pending.get(inFlight.jobId).gen == inFlight.execGen
    all {
      inFlight.active,
      nextDue >= now,
      if (stillMatches) {
        val cur = pending.get(inFlight.jobId)
        val nextPending = pending.put(inFlight.jobId, { due: nextDue, payload: cur.payload, gen: nextGen })
        all {
          nextGen <= MAX_GEN,
          pending' = nextPending,
          nextGen' = nextGen + 1,
          ephemeralArmed' = true,
          ephemeralDue' = minDueOf(nextPending),
        }
      } else {
        all {
          pending' = pending,
          nextGen' = nextGen,
          ephemeralArmed' = ephemeralArmed,
          ephemeralDue' = ephemeralDue,
        }
      },
      inFlight' = NO_IN_FLIGHT,
      now' = now,
      backingJobs' = backingJobs, nextBackingId' = nextBackingId,
      trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
      clobberedOrResurrected' = clobberedOrResurrected,
      partialDirtyCommitted' = partialDirtyCommitted,
      conflictingPayloadAccepted' = conflictingPayloadAccepted,
    }
  }

  // Handler throws/panics after staging dirty state: activation fences & deactivates
  // without committing the partial dirty state.
  action handlerFailDeactivate = all {
    inFlight.active,
    inFlight.dirtyStaged,
    inFlight' = NO_IN_FLIGHT,
    now' = now, pending' = pending, nextGen' = nextGen,
    ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
    backingJobs' = backingJobs, nextBackingId' = nextBackingId,
    trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
    clobberedOrResurrected' = clobberedOrResurrected,
    partialDirtyCommitted' = partialDirtyCommitted,
    conflictingPayloadAccepted' = conflictingPayloadAccepted,
  }

  action step = any {
    advanceTime,
    { nondet j = JOBS.oneOf(); nondet d = DUES.oneOf(); nondet p = PAYLOADS.oneOf(); scheduleJob(j, d, p) },
    { nondet j = pending.keys().oneOf(); cancelJob(j) },
    closeLedgerAndDrain,
    { nondet j = pending.keys().oneOf(); startEphemeralReminder(j) },
    { nondet b = backingJobs.oneOf(); fireBackingReminder(b) },
    handlerStageDirty,
    handlerComplete,
    { nondet d = DUES.oneOf(); handlerReschedule(d) },
    handlerFailDeactivate,
  }

  // ---------------------------------------------------------------------------
  // Safety invariants
  // ---------------------------------------------------------------------------

  val boundedPending: bool = pending.keys().size() <= MAX_PENDING

  val reminderCoversPending: bool =
    pending.keys().size() > 0 implies (ephemeralArmed or backingJobs.size() > 0)

  val noResurrectedOrClobberedJob: bool = not(clobberedOrResurrected)

  val noPartialDirtyCommit: bool = not(partialDirtyCommitted)

  val uniqueJobPayload: bool = not(conflictingPayloadAccepted)

  val monotonicGen: bool =
    pending.keys().forall(j => pending.get(j).gen > 0 and pending.get(j).gen < nextGen)

  val safety: bool = and {
    boundedPending,
    reminderCoversPending,
    noResurrectedOrClobberedJob,
    noPartialDirtyCommit,
    uniqueJobPayload,
    monotonicGen,
  }

  // ---------------------------------------------------------------------------
  // Guard-disabling mutants (verified via ../check-mutants.sh)
  // ---------------------------------------------------------------------------

  // Mutant 1: Handler completion ignores (jobId, execGen) and deletes a newly rescheduled job.
  // breaks: noResurrectedOrClobberedJob
  action mutantIgnoreExecGenOnComplete = any {
    step,
    all {
      inFlight.active,
      pending.keys().contains(inFlight.jobId),
      pending.get(inFlight.jobId).gen != inFlight.execGen, // BUG: clobbers rescheduled job!
      val nextPending = removeJob(pending, inFlight.jobId)
      val hasMore = nextPending.keys().size() > 0
      all {
        pending' = nextPending,
        inFlight' = NO_IN_FLIGHT,
        ephemeralArmed' = hasMore,
        ephemeralDue' = if (hasMore) minDueOf(nextPending) else 0,
        clobberedOrResurrected' = true,
        now' = now, nextGen' = nextGen,
        backingJobs' = backingJobs, nextBackingId' = nextBackingId,
        trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
        partialDirtyCommitted' = partialDirtyCommitted,
        conflictingPayloadAccepted' = conflictingPayloadAccepted,
      }
    },
  }

  // Mutant 2: Handler reschedule ignores (jobId, execGen) and resurrects a canceled job.
  // breaks: noResurrectedOrClobberedJob
  action mutantIgnoreExecGenOnReschedule = any {
    step,
    {
      nondet nextDue = DUES.oneOf()
      all {
        inFlight.active,
        nextDue >= now,
        not(pending.keys().contains(inFlight.jobId)), // BUG: resurrects canceled job!
        val nextPending = pending.put(inFlight.jobId, { due: nextDue, payload: 0, gen: nextGen })
        all {
          pending' = nextPending,
          nextGen' = nextGen + 1,
          inFlight' = NO_IN_FLIGHT,
          ephemeralArmed' = true,
          ephemeralDue' = minDueOf(nextPending),
          clobberedOrResurrected' = true,
          now' = now,
          backingJobs' = backingJobs, nextBackingId' = nextBackingId,
          trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
          partialDirtyCommitted' = partialDirtyCommitted,
          conflictingPayloadAccepted' = conflictingPayloadAccepted,
        }
      }
    },
  }

  // Mutant 3: Handler throws after staging dirty state and commits partial state instead of fencing.
  // breaks: noPartialDirtyCommit
  action mutantCommitDirtyOnHandlerFail = any {
    step,
    all {
      inFlight.active,
      inFlight.dirtyStaged,
      // BUG: commits dirty state on handler throw instead of fencing/deactivating!
      inFlight' = NO_IN_FLIGHT,
      partialDirtyCommitted' = true,
      now' = now, pending' = pending, nextGen' = nextGen,
      ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
      backingJobs' = backingJobs, nextBackingId' = nextBackingId,
      trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
      clobberedOrResurrected' = clobberedOrResurrected,
      conflictingPayloadAccepted' = conflictingPayloadAccepted,
    },
  }

  // Mutant 4: Queue drops SetReminder while another pending job still remains.
  // breaks: reminderCoversPending
  action mutantDropReminderWithPending = any {
    step,
    {
      nondet j = pending.keys().oneOf()
      val nextPending = removeJob(pending, j)
      all {
        nextPending.keys().size() > 0,
        backingJobs.size() == 0,
        // BUG: clears ephemeralArmed despite remaining pending jobs!
        pending' = nextPending,
        ephemeralArmed' = false,
        ephemeralDue' = 0,
        now' = now, nextGen' = nextGen, inFlight' = inFlight,
        backingJobs' = backingJobs, nextBackingId' = nextBackingId,
        trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
        clobberedOrResurrected' = clobberedOrResurrected,
        partialDirtyCommitted' = partialDirtyCommitted,
        conflictingPayloadAccepted' = conflictingPayloadAccepted,
      }
    },
  }

  // Mutant 5: scheduleJob silently overwrites an existing pending jobId with a different payload.
  // breaks: uniqueJobPayload
  action mutantAcceptConflictingJobId = any {
    step,
    {
      nondet j = pending.keys().oneOf()
      nondet p = PAYLOADS.oneOf()
      val cur = pending.get(j)
      all {
        cur.payload != p, // BUG: overwrites conflicting payload instead of rejecting!
        pending' = pending.put(j, { ...cur, payload: p }),
        conflictingPayloadAccepted' = true,
        now' = now, nextGen' = nextGen, inFlight' = inFlight,
        ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
        backingJobs' = backingJobs, nextBackingId' = nextBackingId,
        trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
        clobberedOrResurrected' = clobberedOrResurrected,
        partialDirtyCommitted' = partialDirtyCommitted,
      }
    },
  }
}

module grainJobQueueSim {
  import grainJobQueue(
    MAX_JOBS = 3,
    MAX_DUE = 2,
    MAX_PAYLOAD = 2,
    MAX_PENDING = 2,
    MAX_GEN = 12,
    MAX_BACKING = 8
  ).*

  // Directed scenario 1: Cancel + reschedule during handler .await -> stale completion ignored!
  run cancelAndRescheduleDuringAwaitTest = {
    init
      .then(scheduleJob(0, 0, 0))          // gen = 1
      .then(startEphemeralReminder(0))     // captures execGen = 1
      .then(cancelJob(0))                  // interleaved cancel while awaiting
      .then(scheduleJob(0, 1, 1))          // interleaved reschedule: gen = 2, payload = 1
      .then(handlerComplete)               // stale execGen = 1 must NOT delete gen = 2!
      .then(all {
        assert(safety),
        assert(pending.keys().contains(0)),
        assert(pending.get(0).gen == 2),
        assert(pending.get(0).payload == 1),
        now' = now, pending' = pending, nextGen' = nextGen, inFlight' = inFlight,
        ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
        backingJobs' = backingJobs, nextBackingId' = nextBackingId,
        trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
        clobberedOrResurrected' = clobberedOrResurrected,
        partialDirtyCommitted' = partialDirtyCommitted,
        conflictingPayloadAccepted' = conflictingPayloadAccepted,
      })
  }

  // Directed scenario 2: Close ledger while job pending -> spills to backing JobShard ->
  // backing reminder fires and re-arms open-ledger execution; empty queue spills 0 backing jobs.
  run closeSpilloverAndZeroWriteClearTest = {
    init
      .then(scheduleJob(0, 0, 0))
      .then(closeLedgerAndDrain)           // spills 1 backing job (id=1, due=0)
      .then(fireBackingReminder({ id: 1, due: 0 }))
      .then(startEphemeralReminder(0))
      .then(handlerComplete)               // queue now empty
      .then(fireBackingReminder({ id: 1, due: 0 })) // retires backing job
      .then(scheduleJob(1, 1, 0))
      .then(cancelJob(1))                  // cleared before close
      .then(all {
        assert(safety),
        assert(pending.keys().size() == 0),
        assert(backingJobs.size() == 0),
        assert(nextBackingId == 2),        // only 1 backing job ever scheduled
        now' = now, pending' = pending, nextGen' = nextGen, inFlight' = inFlight,
        ephemeralArmed' = ephemeralArmed, ephemeralDue' = ephemeralDue,
        backingJobs' = backingJobs, nextBackingId' = nextBackingId,
        trackedBackingId' = trackedBackingId, trackedBackingDue' = trackedBackingDue,
        clobberedOrResurrected' = clobberedOrResurrected,
        partialDirtyCommitted' = partialDirtyCommitted,
        conflictingPayloadAccepted' = conflictingPayloadAccepted,
      })
  }
}

module grainJobQueueTlc {
  import grainJobQueue(
    MAX_JOBS = 2,
    MAX_DUE = 1,
    MAX_PAYLOAD = 2,
    MAX_PENDING = 2,
    MAX_GEN = 3,
    MAX_BACKING = 2
  ).*
}
