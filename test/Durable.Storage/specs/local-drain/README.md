# Local-stream drain barrier model

TLC exhaustively checks a finite [Quint](https://quint-lang.org) model of closed-ledger local-stream draining
([ledgers LG-I15, LG-I16](../../docs/spec/components/ledgers.md#5-invariants),
[journals JS-I11, JS-I12](../../docs/spec/components/journals.md#5-invariants),
[archive §2.3](../../docs/spec/components/archive.md#23-coverage-and-drops),
[archive LSM §8.1](../../docs/spec/components/archive-lsm.md#81-drain),
[archive logs §8.1](../../docs/spec/components/archive-logs.md#81-drain), and
[orleans §5.6](../../docs/spec/compositions/orleans.md#56-controllers)): an open ledger carrying journal rows and
`RowKind.LocalAppend` `SetWakeup` records (`"S"` set, `"C"` clear), primary volatile timer delivery vs primary crash,
closing at the decided tail, concurrent racing drainers scanning `[0, T)` with last-write-wins folding and mid-ledger
chunked flushes under `ByteBudget`, volatile backing writes and crashes at every step, and `ILedgerLocalDrainBarrier`
gating both LSM completion (up-front barrier before `ManifestChange.Drain(..., complete: true)`, including the `a == T`
path) and Archive Logs completion (before-drop barrier plus the eager `Waiting` drain slot). Every invariant and
liveness property has a guard-disabling mutant that produces its counterexample, plus volatile-answer mutants and
non-vacuity witnesses.

## Run

The [Quint runner](../ledgers/README.md#run) checks this model with the other Quint models, and checks it alone with:

```powershell
.\eng\model-check-quint.ps1 -Model LocalDrain
.\eng\model-check-quint.ps1 -Model LocalDrain -Extended -Workers 8
```

## Model and implementation mapping

| Model | Behavior | Spec | Implementation |
| --- | --- | --- | --- |
| `LocalDrain.qnt` | One ledger `L` writing `"K"`, `"S"` (`SetWakeup(due)`), and `"C"` (`SetWakeup(null)`), with an open primary's volatile `WakeupLedgerSession` timer delivery or crash before close; closing at the decided tail; racing `LocalStreamDrainCoordinator` barrier drainers scanning `[0, T)` with last-write-wins fold, mid-ledger `ByteBudget` chunked flushes, backpressure aborts, volatile backing `ScheduleJobAsync` writes, and crashes at every step; LSM up-front barrier before `ManifestChange.Drain(..., complete: true)` (both `a < T` and `a == T`) and `LedgerDropper` drop; Archive Logs keyed drain, eager `Waiting` local drain behind an older overlapping ledger (LG-I10), before-drop barrier, `Complete`, and `Drop(L)`; and backing `ILocalDurableJobManager` wakeup delivery | [Ledgers](../../docs/spec/components/ledgers.md) §2.2, §5 (LG-I15, LG-I16); [Journals](../../docs/spec/components/journals.md) §5 (JS-I11, JS-I12); [Archive](../../docs/spec/components/archive.md) §2.3, §4; [Archive LSM](../../docs/spec/components/archive-lsm.md) §2.3, §8.1; [Archive logs](../../docs/spec/components/archive-logs.md) §2.3, §8.1; [Orleans](../../docs/spec/compositions/orleans.md) §5.6, §5.15 | `ILedgerLocalDrainBarrier`, `LocalStreamDrainCoordinator`, `DrainWorker`, `ArchiveLogDrainWorker`, `WakeupStreamHandler`, `WakeupStreamProcessor` (*Not yet in code*; J1–J2) |

## Invariants and liveness

| Property | Kind | Guarantee |
| --- | --- | --- |
| `TypeOK` | Safety | Every cursor, watermark, delivery index, flush count, and crash count stays within bounds |
| `WakeupPreserved` | Safety | Unconditional across chunked flushes and crashes: every surviving undelivered `SetWakeup` is armed on a live open primary, still readable in a listed ledger, or durably persisted in the backing job manager |
| `NoDropBeforeLocalDrained` | Safety | LG-I16: a closed ledger with `T > 0` is neither marked complete in the LSM manifest nor marked complete or dropped on Archive Logs until `EnsureLocalStreamsDrainedAsync(L, T)` has durably completed |
| `ClearedBeforeCloseWritesNothing` | Safety | When a wakeup is cleared before ledger close and no mid-ledger chunked flush ran (`flushes == 0`), closed-ledger drain issues zero `ScheduleJobAsync` calls to the backing job manager |
| `EventuallyLocalDrained` | Liveness | Under weak fairness of barrier progress and backing completion, a closed ledger's local streams are eventually durably drained (including on Archive Logs while `Waiting` behind an older overlapping ledger) |
| `EventuallyWakeupDelivered` | Liveness | Under weak fairness of barrier progress and backing delivery, every surviving `SetWakeup` on a closed ledger is eventually delivered to the grain |
| `NeverSurvivingCrashWakeup` | Witness | A primary crashes after `"S"`, closed-ledger drain schedules the backing job, and the wakeup is delivered |
| `NeverClearedBeforeClose` | Witness | A wakeup set then cleared before close drains and drops with zero backing writes and zero flushes |
| `NeverLsmAlreadyDrainedPath` | Witness | On the LSM `a == T` path, the up-front barrier runs, LSM marks complete in one commit, and the ledger drops |
| `NeverRacingDrainers` | Witness | Two drainers concurrently scan and commit the same closed ledger |
| `NeverChunkedFlush` | Witness | A mid-ledger chunked flush commits, clears fold memory, and the ledger still drains and drops cleanly |

## Mutants

| Mutant | Disabled guard | Counterexample |
| --- | --- | --- |
| `lsm-complete-before-barrier` | LSM `DrainWorker` runs the up-front barrier before `ManifestChange.Drain(..., complete: true)` | `NoDropBeforeLocalDrained` |
| `lsm-skip-barrier-at-end` | LSM `DrainWorker` still runs the up-front barrier when `a == T` | `NoDropBeforeLocalDrained` |
| `logs-drop-before-barrier` | Archive Logs `ArchiveLogDrainWorker` runs the barrier before marking `Complete` and submitting `Drop(L)` | `NoDropBeforeLocalDrained` |
| `skip-wakeup-commit` | Barrier commits surviving folded wakeups before returning `Drained` | `WakeupPreserved` |
| `schedule-volatile` | `CommitAsync` answers success only after `ScheduleJobAsync` is durable (volatile-answer mutant) | `WakeupPreserved` |
| `barrier-complete-on-backpressure` | Barrier returns `Backpressure` without caching `_completed` or answering `Drained` (volatile-answer mutant) | `NoDropBeforeLocalDrained`, `WakeupPreserved` |
| `flush-cleared-entry` | Last-write-wins fold clears the entry on `SetWakeup(null)` and skips backing writes when empty | `ClearedBeforeCloseWritesNothing` |
| `no-eager-waiting-drain` | Archive Logs runs the eager local-drain slot while a closed ledger is `Waiting` behind an older ledger | `EventuallyLocalDrained`, `EventuallyWakeupDelivered` |

## Bounds

Default exhaustive cases run two racing drainers over three-slot histories (`["S", "C", "S"]` with chunked flushes and
crashes; `["S", "C"]` cleared before close; `["K", "S"]` on LSM `a == T`; and Archive Logs both with a droppable older
ledger and permanently `Waiting` behind an older ledger), plus weak-fairness liveness checks, all five witnesses, and
all ten mutant checks. There are no depth cutoffs or state constraints discarding reachable states. With 8 workers, the
default cases exhaust 10,787 (`local-drain-lsm`), 1,877 (`local-drain-lsm-cleared`), 1,392 (`local-drain-lsm-at-end`),
20,746 (`local-drain-logs`), and 65 (`local-drain-logs-waiting`) distinct states, and liveness exhausts 959
(`local-drain-liveness-lsm`) and 65 (`local-drain-liveness-logs-waiting`) distinct states. Extended cases add
`MaxCrashes = 2` on LSM (23,987 distinct states) and Archive Logs (44,650 distinct states) and racing-drainer Archive
Logs liveness (8,610 distinct states).

## Trace validation

Planned in Phase J2: deterministic Clockwork simulations of both LSM and Archive Logs export `DURABLE_STORAGE_LOCAL_DRAIN_TRACES`
and replay them against `LocalDrain.qnt` via `just local-drain-traces` (`eng/model-check-quint.ps1 -Traces`), together
with named negative-control trace mutants (`drop-before-barrier`, `complete-on-dropped`, `skip-unknown-stream`).
