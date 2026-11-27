// -*- mode: Bluespec; -*-
/**
 * Roux Durable Jobs (`roux-jobs`, `R32`) safety model
 * (docs/design/runtime.md §Durable jobs).
 *
 * Models a single shard's `JobCursor` and time-windowed `JobShard(w)` grains
 * (single shard `s = 0` without loss of generality, since shards are
 * independent partitions):
 *
 * - Time `now` advances monotonically across windows `0 .. MAX_WINDOW - 1`.
 * - Clients call `scheduleJob(jobId, targetWindow)`. If `JobShard(w)` is
 *   already durably `closed`, it rejects the schedule with `WindowClosed`,
 *   requiring the caller/API to retry into an open window `> cursor`.
 * - Clients may call `cancelJob(jobId)` against the shard currently holding
 *   the job (returning `true` if removed before firing, `false` otherwise).
 * - `JobCursor` holds durable `cursor` (initialized to `-1`, the last fully
 *   drained and closed window) and polls windows in ascending order up to
 *   `now`:
 *   - On `fireJob(w, j)`: target executes (at-least-once).
 *     * Success: `JobShard(w)` removes `j` from `pending`.
 *     * Failure below `MAX_ATTEMPTS`: in one atomic turn, `JobShard(w)`
 *       removes `j` from `pending` and stages `Reschedule(j, attempt+1, w')`
 *       (with `w' > w`) in its durable `outbox`.
 *     * Failure at `MAX_ATTEMPTS`: `JobShard(w)` moves `j` to `deadLetter`.
 *   - `deliverReschedule(w, entry)`: delivers `JobShard(w)`'s outbox entry
 *     to `JobShard(entry.toWindow)` (deduped via `receivedIds` so outbox
 *     retries are effectively once) and removes it from `w`'s outbox on ack.
 *   - `closeWindow(w)`: when `w == cursor + 1`, `w < now` (window ended),
 *     `pending` is empty, AND `outbox` is empty, `JobShard(w)` durably sets
 *     `closed = true` and replies `Closed`, allowing `JobCursor` to durably
 *     advance `cursor` to `w`.
 *
 * Because `JobCursor` closes windows strictly in ascending order and refuses
 * to close `w` while `outbox` is non-empty, every reschedule target `w' > w`
 * is guaranteed to be open (`w' > cursor`) when delivered.
 *
 * Mutants disable one guard at a time and are verified via ../check-mutants.sh.
 */
module durableJobs {

  // ------------------------------------------------------------------ Consts
  const MAX_WINDOW: int       // windows 0 .. MAX_WINDOW - 1
  const MAX_JOBS: int         // job IDs 0 .. MAX_JOBS - 1
  const MAX_ATTEMPTS: int     // max attempts before dead-lettering

  pure val WINDOWS: Set[int] = 0.to(MAX_WINDOW - 1)
  pure val JOBS: Set[int] = 0.to(MAX_JOBS - 1)

  // ------------------------------------------------------------------ Types
  type OutboxEntry = {
    jobId: int,
    attempt: int,
    toWindow: int,
  }

  type ShardState = {
    // Active pending jobs: jobId -> current attempt count (0-based).
    pending: int -> int,
    // Durable outbox reschedules waiting for ack from a later window shard.
    outbox: Set[OutboxEntry],
    // Inbox dedup keys `(jobId, attempt)` already accepted by this shard.
    accepted: Set[{ jobId: int, attempt: int }],
    // Dead-lettered job IDs stored on this shard.
    deadLetter: Set[int],
    // Durable seal set by JobCursor only when ended, pending empty, outbox empty.
    closed: bool,
  }

  // ------------------------------------------------------------------ State
  var now: int                    // current window clock (0 .. MAX_WINDOW)
  var cursor: int                 // last fully drained & closed window (-1 initially)
  var shards: int -> ShardState
  // Ghost oracle sets for end-to-end job lifecycle verification
  var scheduled: Set[int]         // jobs whose schedule_job succeeded
  var completed: Set[int]         // jobs whose target succeeded at least once
  var canceled: Set[int]          // jobs whose cancel_job returned true
  var deadLettered: Set[int]      // jobs moved to dead-letter after MAX_ATTEMPTS
  var rescheduleRejectedClosed: bool

  pure val EMPTY_SHARD: ShardState = {
    pending: Map(),
    outbox: Set(),
    accepted: Set(),
    deadLetter: Set(),
    closed: false,
  }

  pure def removeKey(m: int -> int, k: int): int -> int =
    m.keys().exclude(Set(k)).mapBy(x => m.get(x))

  // ------------------------------------------------------------------ Init
  action init = all {
    now' = 0,
    cursor' = -1,
    shards' = WINDOWS.mapBy(_ => EMPTY_SHARD),
    scheduled' = Set(),
    completed' = Set(),
    canceled' = Set(),
    deadLettered' = Set(),
    rescheduleRejectedClosed' = false,
  }

  // ------------------------------------------------------------------ Actions
  // Logical clock advances to the next window.
  action advanceTime = all {
    now < MAX_WINDOW,
    now' = now + 1,
    cursor' = cursor, shards' = shards,
    scheduled' = scheduled, completed' = completed,
    canceled' = canceled, deadLettered' = deadLettered,
    rescheduleRejectedClosed' = rescheduleRejectedClosed,
  }

  // Client schedules a new job into an open window shard.
  // If the target shard is already `closed`, it rejects with WindowClosed
  // (guard: not(sh.closed)), so the caller must choose an open window.
  action scheduleJob(jobId: int, w: int): bool = {
    val sh = shards.get(w)
    all {
      not(scheduled.contains(jobId)),
      not(sh.closed),
      shards' = shards.set(w, {
        ...sh,
        pending: sh.pending.put(jobId, 0),
        accepted: sh.accepted.union(Set({ jobId: jobId, attempt: 0 })),
      }),
      scheduled' = scheduled.union(Set(jobId)),
      now' = now, cursor' = cursor,
      completed' = completed, canceled' = canceled, deadLettered' = deadLettered,
      rescheduleRejectedClosed' = rescheduleRejectedClosed,
    }
  }

  // Client cancels a job that is still pending in shard `w`.
  action cancelJob(w: int, jobId: int): bool = {
    val sh = shards.get(w)
    all {
      sh.pending.keys().contains(jobId),
      shards' = shards.set(w, {
        ...sh,
        pending: removeKey(sh.pending, jobId),
      }),
      canceled' = canceled.union(Set(jobId)),
      now' = now, cursor' = cursor,
      scheduled' = scheduled, completed' = completed, deadLettered' = deadLettered,
      rescheduleRejectedClosed' = rescheduleRejectedClosed,
    }
  }

  // Target executes and succeeds (at-least-once): shard removes job from pending.
  // (A crash before removal simply leaves the job in pending to fire again.)
  action fireJobSuccess(w: int, jobId: int): bool = {
    val sh = shards.get(w)
    all {
      w <= now,
      sh.pending.keys().contains(jobId),
      shards' = shards.set(w, {
        ...sh,
        pending: removeKey(sh.pending, jobId),
      }),
      completed' = completed.union(Set(jobId)),
      now' = now, cursor' = cursor,
      scheduled' = scheduled, canceled' = canceled, deadLettered' = deadLettered,
      rescheduleRejectedClosed' = rescheduleRejectedClosed,
    }
  }

  // Target executes and succeeds, but the shard crashes before persisting removal:
  // job remains pending and may fire again (explicit at-least-once modeling).
  action fireJobCrashBeforeCommit(w: int, jobId: int): bool = {
    val sh = shards.get(w)
    all {
      w <= now,
      sh.pending.keys().contains(jobId),
      completed' = completed.union(Set(jobId)),
      now' = now, cursor' = cursor, shards' = shards,
      scheduled' = scheduled, canceled' = canceled, deadLettered' = deadLettered,
      rescheduleRejectedClosed' = rescheduleRejectedClosed,
    }
  }

  // Target fails below attempt limit: in one atomic journal turn, remove from
  // pending and stage an outbox reschedule into a strictly later window w' > w.
  action fireJobFailReschedule(w: int, jobId: int, nextW: int): bool = {
    val sh = shards.get(w)
    all {
      w <= now,
      sh.pending.keys().contains(jobId),
      val attempt = sh.pending.get(jobId)
      all {
        attempt + 1 < MAX_ATTEMPTS,
        nextW > w,
        nextW < MAX_WINDOW,
        shards' = shards.set(w, {
          ...sh,
          pending: removeKey(sh.pending, jobId),
          outbox: sh.outbox.union(Set({ jobId: jobId, attempt: attempt + 1, toWindow: nextW })),
        }),
        now' = now, cursor' = cursor,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      }
    }
  }

  // Target fails at attempt limit (or no later window exists): dead-letter job.
  action fireJobDeadLetter(w: int, jobId: int): bool = {
    val sh = shards.get(w)
    all {
      w <= now,
      sh.pending.keys().contains(jobId),
      val attempt = sh.pending.get(jobId)
      all {
        attempt + 1 >= MAX_ATTEMPTS or w + 1 >= MAX_WINDOW,
        shards' = shards.set(w, {
          ...sh,
          pending: removeKey(sh.pending, jobId),
          deadLetter: sh.deadLetter.union(Set(jobId)),
        }),
        deadLettered' = deadLettered.union(Set(jobId)),
        now' = now, cursor' = cursor,
        scheduled' = scheduled, completed' = completed, canceled' = canceled,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      }
    }
  }

  // Outbox delivers a reschedule from shard `w` to shard `e.toWindow` (inbox-deduped)
  // and receives ack, removing the entry from `w`'s outbox.
  action deliverReschedule(w: int, e: OutboxEntry): bool = {
    val src = shards.get(w)
    val dst = shards.get(e.toWindow)
    val key = { jobId: e.jobId, attempt: e.attempt }
    val alreadyAccepted = dst.accepted.contains(key)
    all {
      src.outbox.contains(e),
      if (dst.closed) all {
        // Should be unreachable under the protocol invariants!
        rescheduleRejectedClosed' = true,
        shards' = shards,
        now' = now, cursor' = cursor,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
      } else all {
        val dstNext = if (alreadyAccepted) dst else {
          ...dst,
          pending: dst.pending.put(e.jobId, e.attempt),
          accepted: dst.accepted.union(Set(key)),
        }
        val srcNext = {
          ...src,
          outbox: src.outbox.exclude(Set(e)),
        }
        all {
          shards' = shards.set(e.toWindow, dstNext).set(w, srcNext),
          rescheduleRejectedClosed' = rescheduleRejectedClosed,
          now' = now, cursor' = cursor,
          scheduled' = scheduled, completed' = completed,
          canceled' = canceled, deadLettered' = deadLettered,
        }
      }
    }
  }

  // Outbox duplicate delivery (ack lost before removal): destination accepts
  // idempotently via `accepted` dedup key while outbox entry stays until acked.
  action deliverRescheduleAckLost(w: int, e: OutboxEntry): bool = {
    val src = shards.get(w)
    val dst = shards.get(e.toWindow)
    val key = { jobId: e.jobId, attempt: e.attempt }
    val alreadyAccepted = dst.accepted.contains(key)
    all {
      src.outbox.contains(e),
      not(dst.closed),
      val dstNext = if (alreadyAccepted) dst else {
        ...dst,
        pending: dst.pending.put(e.jobId, e.attempt),
        accepted: dst.accepted.union(Set(key)),
      }
      all {
        shards' = shards.set(e.toWindow, dstNext),
        now' = now, cursor' = cursor,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      }
    }
  }

  // JobCursor drains and durably closes the next ended window `w == cursor + 1`.
  // Requires: window ended (`w < now`), zero pending jobs, AND zero outbox reschedules.
  action closeAndAdvanceCursor = {
    val w = cursor + 1
    all {
      w < MAX_WINDOW,
      w < now,
      val sh = shards.get(w)
      all {
        sh.pending.keys().size() == 0,
        sh.outbox.size() == 0,
        shards' = shards.set(w, { ...sh, closed: true }),
        cursor' = w,
        now' = now,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      }
    }
  }

  // ------------------------------------------------------------------ Step
  action step = any {
    advanceTime,
    nondet j = oneOf(JOBS)
    nondet w = oneOf(WINDOWS)
    scheduleJob(j, w),
    nondet w = oneOf(WINDOWS)
    nondet j = oneOf(shards.get(w).pending.keys())
    cancelJob(w, j),
    nondet w = oneOf(WINDOWS)
    nondet j = oneOf(shards.get(w).pending.keys())
    fireJobSuccess(w, j),
    nondet w = oneOf(WINDOWS)
    nondet j = oneOf(shards.get(w).pending.keys())
    fireJobCrashBeforeCommit(w, j),
    nondet w = oneOf(WINDOWS)
    nondet j = oneOf(shards.get(w).pending.keys())
    nondet nextW = oneOf(WINDOWS)
    fireJobFailReschedule(w, j, nextW),
    nondet w = oneOf(WINDOWS)
    nondet j = oneOf(shards.get(w).pending.keys())
    fireJobDeadLetter(w, j),
    nondet w = oneOf(WINDOWS)
    nondet e = oneOf(shards.get(w).outbox)
    deliverReschedule(w, e),
    nondet w = oneOf(WINDOWS)
    nondet e = oneOf(shards.get(w).outbox)
    deliverRescheduleAckLost(w, e),
    closeAndAdvanceCursor,
  }

  // ---------------------------------------------------------------- Invariants
  // Every scheduled job that has not completed, been canceled, or been
  // dead-lettered is still reachable by JobCursor (strictly above `cursor`) —
  // either pending in an open shard w > cursor, or in the outbox of an open
  // shard w > cursor targeting an open shard w' > w.
  val noStrandedJob: bool =
    scheduled.forall(j =>
      completed.contains(j)
        or canceled.contains(j)
        or deadLettered.contains(j)
        or WINDOWS.exists(w =>
          w > cursor
            and not(shards.get(w).closed)
            and (
              shards.get(w).pending.keys().contains(j)
                or shards.get(w).outbox.exists(e => e.jobId == j and e.toWindow > cursor)
            )
        )
    )

  // Every closed shard has zero pending jobs and zero pending outbox entries.
  val closedShardEmpty: bool =
    WINDOWS.forall(w => {
      val sh = shards.get(w)
      not(sh.closed) or (sh.pending.keys().size() == 0 and sh.outbox.size() == 0)
    })

  // Cursor only covers ended, durably closed windows, strictly in order.
  val cursorValid: bool = all {
    cursor < now,
    WINDOWS.forall(w => if (w <= cursor) shards.get(w).closed else not(shards.get(w).closed)),
  }

  // An outbox reschedule never hits an already-closed destination window.
  val rescheduleTargetOpen: bool = all {
    not(rescheduleRejectedClosed),
    WINDOWS.forall(w =>
      shards.get(w).outbox.forall(e => e.toWindow > w and e.toWindow > cursor and not(shards.get(e.toWindow).closed))
    ),
  }

  // Only scheduled jobs ever complete or get dead-lettered.
  val onlyScheduledActedOn: bool =
    completed.subseteq(scheduled) and deadLettered.subseteq(scheduled) and canceled.subseteq(scheduled)

  val safety: bool = all {
    noStrandedJob,
    closedShardEmpty,
    cursorValid,
    rescheduleTargetOpen,
    onlyScheduledActedOn,
  }

  // ------------------------------------------------------------------ Mutants
  // Mutant 1: JobShard accepts a late schedule_job into an already-closed window.
  // breaks: noStrandedJob
  action mutantScheduleIgnoresClosed = any {
    step,
    nondet jobId = oneOf(JOBS)
    nondet w = oneOf(WINDOWS)
    val sh = shards.get(w)
    all {
      not(scheduled.contains(jobId)),
      sh.closed,  // BUG: accepts late schedule after window closed!
      shards' = shards.set(w, {
        ...sh,
        pending: sh.pending.put(jobId, 0),
        accepted: sh.accepted.union(Set({ jobId: jobId, attempt: 0 })),
      }),
      scheduled' = scheduled.union(Set(jobId)),
      now' = now, cursor' = cursor,
      completed' = completed, canceled' = canceled, deadLettered' = deadLettered,
      rescheduleRejectedClosed' = rescheduleRejectedClosed,
    }
  }

  // Mutant 2: JobShard reports empty / closes while an outbox reschedule is still unacked.
  // breaks: noStrandedJob
  action mutantCloseWithPendingOutbox = any {
    step,
    val w = cursor + 1
    all {
      w < MAX_WINDOW,
      w < now,
      val sh = shards.get(w)
      all {
        sh.pending.keys().size() == 0,
        sh.outbox.size() > 0,  // BUG: ignores pending outbox reschedules!
        shards' = shards.set(w, { ...sh, closed: true }),
        cursor' = w,
        now' = now,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      }
    }
  }

  // Mutant 3: JobCursor advances past an empty ended window WITHOUT sealing
  // `JobShard(w).closed = true`, allowing a late schedule to land behind cursor.
  // breaks: noStrandedJob
  action mutantCursorAdvancesWithoutClose = any {
    step,
    val w = cursor + 1
    all {
      w < MAX_WINDOW,
      w < now,
      val sh = shards.get(w)
      all {
        sh.pending.keys().size() == 0,
        sh.outbox.size() == 0,
        // BUG: advances cursor without setting sh.closed = true!
        cursor' = w,
        shards' = shards,
        now' = now,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      }
    }
  }

  // Mutant 4: JobCursor skips an unclosed window and closes a later ended window out of order.
  // breaks: rescheduleTargetOpen
  action mutantCursorSkipsWindow = any {
    step,
    nondet w = oneOf(WINDOWS)
    all {
      w > cursor + 1,
      w < now,
      val sh = shards.get(w)
      all {
        sh.pending.keys().size() == 0,
        sh.outbox.size() == 0,
        // BUG: closes w while an earlier window still has pending/outbox jobs!
        shards' = shards.set(w, { ...sh, closed: true }),
        cursor' = cursor,
        now' = now,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      }
    }
  }

  // Mutant 5: On failure, JobShard removes the job without durably staging
  // its outbox reschedule in the same turn.
  // breaks: noStrandedJob
  action mutantRescheduleDropsOnFail = any {
    step,
    nondet w = oneOf(WINDOWS)
    nondet jobId = oneOf(shards.get(w).pending.keys())
    val sh = shards.get(w)
    all {
      w <= now,
      sh.pending.get(jobId) + 1 < MAX_ATTEMPTS,
      shards' = shards.set(w, {
        ...sh,
        pending: removeKey(sh.pending, jobId),
        // BUG: drops job instead of staging outbox reschedule!
      }),
      now' = now, cursor' = cursor,
      scheduled' = scheduled, completed' = completed,
      canceled' = canceled, deadLettered' = deadLettered,
      rescheduleRejectedClosed' = rescheduleRejectedClosed,
    }
  }
}

module durableJobsSim {
  import durableJobs(MAX_WINDOW = 4, MAX_JOBS = 3, MAX_ATTEMPTS = 2).*

  // ----------------------------------------------------------- Directed tests

  // Normal lifecycle: schedule in window 0, fire with failure -> outbox
  // reschedule into window 1 -> ack lost retry -> ack -> close window 0 ->
  // succeed in window 1 -> close window 1.
  run rescheduleRetryAndCloseTest = {
    init
      .then(scheduleJob(0, 0))
      .then(fireJobFailReschedule(0, 0, 1))
      .then(advanceTime)
      .then(deliverRescheduleAckLost(0, { jobId: 0, attempt: 1, toWindow: 1 }))
      .then(deliverReschedule(0, { jobId: 0, attempt: 1, toWindow: 1 }))
      .then(closeAndAdvanceCursor)
      .then(fireJobSuccess(1, 0))
      .then(advanceTime)
      .then(closeAndAdvanceCursor)
      .then(all {
        assert(safety),
        assert(cursor == 1),
        assert(completed.contains(0)),
        assert(shards.get(0).closed),
        assert(shards.get(1).closed),
        now' = now, cursor' = cursor, shards' = shards,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      })
  }

  // Crash before removal: job fires at-least-once and is cleanly retired on retry.
  run crashBeforeRemovalAtLeastOnceTest = {
    init
      .then(scheduleJob(0, 0))
      .then(fireJobCrashBeforeCommit(0, 0))
      .then(fireJobSuccess(0, 0))
      .then(advanceTime)
      .then(closeAndAdvanceCursor)
      .then(all {
        assert(safety),
        assert(completed.contains(0)),
        assert(cursor == 0),
        now' = now, cursor' = cursor, shards' = shards,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      })
  }

  // Dead-lettering after MAX_ATTEMPTS failures and cancel of a second job.
  run deadLetterAndCancelTest = {
    init
      .then(scheduleJob(0, 0))
      .then(scheduleJob(1, 0))
      .then(cancelJob(0, 1))
      .then(fireJobFailReschedule(0, 0, 1))
      .then(deliverReschedule(0, { jobId: 0, attempt: 1, toWindow: 1 }))
      .then(advanceTime)
      .then(closeAndAdvanceCursor)
      .then(fireJobDeadLetter(1, 0))
      .then(advanceTime)
      .then(closeAndAdvanceCursor)
      .then(all {
        assert(safety),
        assert(canceled.contains(1)),
        assert(deadLettered.contains(0)),
        assert(cursor == 1),
        now' = now, cursor' = cursor, shards' = shards,
        scheduled' = scheduled, completed' = completed,
        canceled' = canceled, deadLettered' = deadLettered,
        rescheduleRejectedClosed' = rescheduleRejectedClosed,
      })
  }
}

module durableJobsTlc {
  import durableJobs(MAX_WINDOW = 3, MAX_JOBS = 2, MAX_ATTEMPTS = 2).*
}
