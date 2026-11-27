# Durable Jobs Specifications (`specs/jobs/`)

Formal Quint models of Roux's two-layer durable jobs architecture (`R32` + `R37`):

1. **[`durableJobs.qnt`](durableJobs.qnt):** Cluster-wide backing scheduler grains (`JobShard` + `JobCursor`) — window closure, late-schedule fencing (`WindowClosed`), and atomic outbox rescheduling (`R32`). Serves cross-grain durable jobs and acts as the backing destination for `ReminderMessageProcessor`'s deterministic sealed `JobShard`s.
2. **[`grainJobQueue.qnt`](grainJobQueue.qnt):** Outbox-style grain-local `DurableJobQueue` in `roux-jobs` (L4), layered on `GrainReminderCoordinator` and the `"roux.reminder"` atomic `RowKind::Message` channel (`R37`, built on L2's general-purpose atomic message system `ST12`) — atomic schedule/cancel with `SetReminder`, `is_ephemeral_reminder` vs backing `JobShard` deduplication, monotonic `execution_generation` post-`.await` cancel/reschedule guard, and dirty-state handler failure fencing.

Authoritative design docs:
- [docs/design/runtime.md](../../docs/design/runtime.md) (§Durable jobs `R32`, §Atomic journal reminders and grain-local durable jobs `R37`)
- [docs/design/storage.md](../../docs/design/storage.md) (§3a `ST12`, general-purpose L2 atomic journal messages)
- [specs/message-drain/README.md](../message-drain/README.md) (L2 foundation: single-pass `DrainScan` message folding & deterministic idempotent durable sink commits `LG-I16`)

## `durableJobs.qnt` — `JobShard` + `JobCursor` invariants (`R32`)

- **`noStrandedJob`** — Every scheduled job that has not completed, been canceled, or been dead-lettered remains reachable strictly ahead of `JobCursor` (either pending in an open `JobShard(w)` with `w > cursor`, or in the durable outbox of an open shard `w > cursor` targeting `w' > w`).
- **`closedShardEmpty`** — Once `JobShard(w)` is durably marked `closed`, its `pending` set and `outbox` are both empty and never accept new jobs (late `schedule_job` calls receive `WindowClosed` and retry into an open window).
- **`cursorValid`** — `JobCursor` only advances past ended windows (`w < now`) strictly in ascending order, and every window `<= cursor` is durably closed.
- **`rescheduleTargetOpen`** — Because `JobCursor` seals windows strictly in order and never closes `w` while `w.outbox` is non-empty, every outbox reschedule to `w' > w` is guaranteed to arrive at an open destination shard.
- **`onlyScheduledActedOn`** — Only scheduled jobs are ever completed, canceled, or dead-lettered.

## Mutants (verified by `../check-mutants.sh`)

| Mutant | Guard disabled | Breaks |
| --- | --- | --- |
| `mutantScheduleIgnoresClosed` | `JobShard` accepts a late `schedule_job` after `closed == true` | `noStrandedJob` |
| `mutantCloseWithPendingOutbox` | `JobShard` closes while an outbox reschedule is still unacknowledged | `noStrandedJob` |
| `mutantCursorAdvancesWithoutClose` | `JobCursor` advances past an empty window without durably sealing `closed = true` | `noStrandedJob` |
| `mutantCursorSkipsWindow` | `JobCursor` closes a later window out of order while an earlier window still has jobs/outbox items | `rescheduleTargetOpen` |
| `mutantRescheduleDropsOnFail` | `JobShard` removes a failed job without durably staging its outbox reschedule | `noStrandedJob` |

## `grainJobQueue.qnt` — Grain-local `DurableJobQueue` invariants (`R37`)

- **`boundedPending`** — Pending jobs in the grain's journal stay within `MAX_PENDING`.
- **`reminderCoversPending`** — Whenever `pending` is non-empty, either the open-ledger `ReminderLedgerSession` (`ephemeralArmed`) or a durable backing `JobShard` reminder (`backingJobs`) covers the grain.
- **`noResurrectedOrClobberedJob`** — Post-`.await` handler `Completed` and `RescheduleAt` results verify `(jobId, execGen)` against the monotonic journaled `execution_generation`, never resurrecting a canceled job or clobbering a newly rescheduled job.
- **`noPartialDirtyCommit`** — When a job handler fails/panics after staging dirty state, the activation fences and terminates without committing partial state.
- **`uniqueJobPayload`** — Scheduling an existing pending `jobId` with a conflicting payload/due time is rejected (`DuplicateJobIdConflict`), while an identical re-schedule is idempotent.
- **`monotonicGen`** — Every pending job carries a strictly positive generation below `nextGen`.

### `grainJobQueue.qnt` mutants (verified by `../check-mutants.sh`)

| Mutant | Guard disabled | Breaks |
| --- | --- | --- |
| `mutantIgnoreExecGenOnComplete` | Handler `Completed` omits `(jobId, execGen)` check and clobbers a rescheduled job | `noResurrectedOrClobberedJob` |
| `mutantIgnoreExecGenOnReschedule` | Handler `RescheduleAt` omits `(jobId, execGen)` check and resurrects a canceled job | `noResurrectedOrClobberedJob` |
| `mutantCommitDirtyOnHandlerFail` | Handler throw after dirty staging commits partial state instead of fencing | `noPartialDirtyCommit` |
| `mutantDropReminderWithPending` | Queue clears `SetReminder` while other pending jobs remain uncovered | `reminderCoversPending` |
| `mutantAcceptConflictingJobId` | `scheduleJob` overwrites an existing pending `jobId` with a conflicting payload | `uniqueJobPayload` |

## Running locally

```bash
cd specs/jobs
quint typecheck durableJobs.qnt
quint test durableJobs.qnt --main=durableJobsSim
quint run durableJobs.qnt --main=durableJobsSim --invariant=safety --max-samples=20000 --max-steps=35 --backend=rust
bash ../check-mutants.sh durableJobs.qnt durableJobsSim safety --backend=rust --max-steps=35 --max-samples=20000 --seed=7
quint verify durableJobs.qnt --main=durableJobsTlc --invariant=safety --backend=tlc

quint typecheck grainJobQueue.qnt
quint test grainJobQueue.qnt --main=grainJobQueueSim --match=Test
quint run grainJobQueue.qnt --main=grainJobQueueSim --invariant=safety --max-samples=20000 --max-steps=35 --backend=rust
bash ../check-mutants.sh grainJobQueue.qnt grainJobQueueSim safety --backend=rust --max-steps=35 --max-samples=20000 --seed=7
quint verify grainJobQueue.qnt --main=grainJobQueueTlc --invariant=safety --backend=tlc
```
