# Archive-log model

TLC exhaustively checks a finite [Quint](https://quint-lang.org) model of
[archive logs](../../docs/spec/components/archive-logs.md): one key's log on Azure's layout, written a segment at a
time by racing and straggler drains of whole closed segments, whose requests may land after their drainer crashed, the
drain markers and drops which follow them, the sweeper of checkpoints and markers, readers on a stale map and at a
lagging tail, and Journals' loader ([journals §8.3](../../docs/spec/components/journals.md#83-warm-state)).
Each `Create`, `Append Block`, and `Put Blob` is one action, so crash cut points match model states. Every invariant
has at least one mutant, a configuration which disables the guard which establishes it, and TLC must produce that
invariant's counterexample.

## Run

The [Quint runner](../ledgers/README.md#run) checks this model with the other Quint models, and checks it alone with:

```powershell
.\eng\model-check-quint.ps1 -Model ArchiveLogs
.\eng\model-check-quint.ps1 -Model ArchiveLogs -Extended -Nightly -Workers 12
```

`just archive-log-traces` validates the drain simulation's traces against it (see [trace
validation](#trace-validation)).

## Model and implementation mapping

| Model | Behavior | Spec | Implementation |
| --- | --- | --- | --- |
| `ArchiveLogs.qnt` | One key's decided history in a closed ledger and its open successor, which is decided a segment at a time and closes once every batch is decided, with `Append`, `Replace`, and `Delete` entries, another key's rows, and marker batches, each ledger cut into segments; every write's token replayed by `Plan` from `Origin`; drainers which drain a whole segment closed below the decided prefix, from the end of their ledger's highest marker, only while no older ledger is listed, as a first run's `Create`, `Append Block`s conditioned on the generation's tag and the offset, or an opener's checkpoint and write-ahead `Put Blob`s, each failed condition classified from one properties read, then create the segment's marker, crash at any state, and land as stragglers; drops in lineage order once every segment holding rows is marked; a sweeper listing checkpoints, reading the log's properties, then deleting below the current generation, and deleting dropped ledgers' markers; a loader which reads the map, then the log, then takes the last live row's version or rebases to the log's position | [archive logs](../../docs/spec/components/archive-logs.md) §2.3–§2.5, §5, §6, §8.1–§8.5; [archive](../../docs/spec/components/archive.md) §2.3; [journals](../../docs/spec/components/journals.md) §8.3 | `ArchiveLogDrainWorker`, `ArchiveLogWriter`, `LogArchiveStore` (`Holds`), `ArchiveLogSweeper`, `ArchiveLogNames`, Journals' loader |

A crashed or stalled drainer's request lands later, as a straggler's does, at most one at a time (`Stragglers`); any
number of drainers may drain one segment. A position's base rows keep every state of a key's chain a distinct token, so
a lagging reader which meets one of its key's rows inside a drained segment reads again; one which meets none may run
ahead, which a read that is not linearizable may.

## Invariants

| Invariant | Guarantee |
| --- | --- |
| `NoLostCommand` | LOG-I1, LOG-I3, HI-I2: the log's content and the decided rows of the listed ledgers which `Holds` does not mark held fold to the decided history |
| `StaleReaders` | HI-I1: a reader whose stale map lists any superset of the listed ledgers folds to the same history, the merge by `Holds` marking the dropped ledgers' rows held |
| `ReadsAtTheirCut` | HI-I1: a reader whose tail lies at or below the open ledger's decided tail, folding only the rows below it, answers exactly its cut when its tail is the decided one, as a linearizable read, and otherwise a real prefix of the history through at least its cut |
| `NoUndecidedCommand` | HI-I2: the log holds only decided rows |
| `HoldsSound` | LOG-I1, HI-I4: the log's position is a point of the token chain whose content matches it, and `Holds` claims no row past it |
| `ReferencedExist` | LOG-I2: the checkpoint the write-ahead object names exists |
| `VersionsUnique` | HI-I4, JS-I10: the state a load's version names is the key's state when the load took it |
| `NoFalseCorruption` | The lineage rule: no conforming run blocks a drain as corrupt |

## Mutants

| Mutant | Disabled guard | Counterexample |
| --- | --- | --- |
| `replace-over-newer` | An opener's write-ahead `Put Blob` is conditioned on the generation it starts from | `NoLostCommand` |
| `checkpoint-overwrite` | Checkpoints are named by generation and created only when absent | `NoLostCommand` |
| `delete-removes-log` | A `Delete` opener writes an empty generation rather than removing the log | `NoLostCommand` |
| `partial-segment-drain` | A drain takes one whole closed segment | `NoLostCommand` |
| `segments-out-of-order` | A drain starts at its ledger's highest marker's end | `NoLostCommand` |
| `wait-as-held` | A ledger drops only once complete | `NoLostCommand` |
| `complete-early` | A ledger is complete only once its last segment holding rows is marked and it is closed | `NoLostCommand` |
| `404-as-held-without-properties-read` | A failed condition is classified from a properties read, not taken as held | `NoLostCommand` |
| `drain-past-tail` | A drain takes a segment only once the next segment's decided start, or the end, closes it below the decided prefix | `NoUndecidedCommand` |
| `append-without-generation-fence` | An `Append Block` is conditioned on its generation's tag | `HoldsSound` |
| `append-without-offset-fence` | An `Append Block` is conditioned on its offset (`appendpos`) | `HoldsSound` |
| `first-run-as-opener` | `Holds` judges a first run from `Origin` as a run, not as an opener of generation 1 | `HoldsSound` |
| `wal-before-checkpoint` | An opener writes its checkpoint before the write-ahead object which names it | `ReferencedExist` |
| `gc-at-or-above-current` | The sweeper deletes only below the current generation | `ReferencedExist` |
| `referenced-missing` | The sweeper takes the current generation from its properties read, not its listing | `ReferencedExist` |
| `no-zero-pad` | Generation names are zero-padded, so they sort as numbers; the mutant sorts generation 3 (`"10"` in base 3) below 2 | `ReferencedExist` |
| `rebase-with-live-rows` | A load rebases only when no live row remains | `VersionsUnique` |
| `rebase-from-map-after-archive` | A load reads the map before the log | `VersionsUnique` |
| `rebase-on-stale-token` | A rebase takes the log's position, not the last write's token | `VersionsUnique` |
| `lagging-read-as-linearizable` | Only a read whose tail is the decided one is held to its exact cut | `ReadsAtTheirCut` |
| `drain-ignores-lineage` | A drain runs only once no older overlapping ledger is listed | `NoFalseCorruption` |

## Bounds

The default exhaustive cases run two drainers over four histories, with the sweeper and the loader. The first appends
twice in one segment of the closed ledger, so a crash splits a run, then opens a generation with a `Replace`, and its
successor appends and deletes in two segments. The second passes a segment of markers alone and one of another key's
rows, and its successor replaces and appends in one segment. The third opens three generations, so the sweeper deletes
below a current one beside an opener in progress. The fourth puts an opener with an append after it in the open ledger's
first segment, which drains while the ledger is open, and a reader whose tail lies inside it sees the opener alone. A
fifth case runs the first history with a straggler request which lands after its drainer crashed, and the loader off.
Each mutant runs under the smallest history which reaches its counterexample. Three drainers race the first history with
stragglers: exhaustively with the sweeper and the loader off, as a seeded simulation of 2,000 runs of 60 steps with both
on, and, with `-Nightly` only, exhaustively with the sweeper on. Generations are bounded by six; there are no depth
cutoffs or state constraints which discard reachable states. With 12 workers, the default cases reach 3,057,772,
194,552, 6,205,956, 237,092, and 3,058,580 distinct states, in about 5, 0.5, 11, 0.7, and 5 minutes; the three-drainer
case 709,204 in about 1.5 minutes; each mutant finds its counterexample within seconds, after at most 11,044 distinct
states (`no-zero-pad`). The nightly three-drainer case with the sweeper reaches 56,405,388 distinct states in about 1
hour 55 minutes.

## Trace validation

`ArchiveLogTraceTests` (`Orleans.Durable.Storage.Simulation.Tests`) records the drain simulation of one key over a
closed ledger and its open successor as the model's actions, with the run's decided entries and segment starts as its
constants: each request of the key's log, a segment's start, a marker, a drop, and a sweep, while two drainers race,
crash, and restart. With `DURABLE_STORAGE_ARCHIVE_LOG_TRACES` set, each seed writes a module
`ArchiveLogsTrace{seed}.qnt` whose `traceReplay` run applies every action in order and checks every invariant, and after
each store request the log's position, the store's `(g, r, units)`, against the model's. The ledger writer's decisions
give the decided tail, `ReplaceAsync` the close, and a blocked drain is a violation. `.\eng\model-check-quint.ps1
-Traces <directory>` replays each with `quint test`, failing at an action the model does not enable, and checks two
negative controls fail: a copy which repeats a segment's start, and the trace of a `marks-unheld` drain whose every
create fails (`ArchiveLogsTraceUnheld{seed}.qnt`). `just archive-log-traces` runs both, over seeds 1 to 20 by default,
and CI's `archive-log-traces` job on every change ([verification
§5.1](../../docs/spec/verification.md#51-trace-validation)).

## Scope of the result

A successful run establishes the listed properties for every reachable state of each configured finite model, subject
to TLC's fingerprint collision probability. The model holds one key, so another key's writes appear only as rows which
fill a segment; per-key writes are independent, and the drain completes a segment only once every key's write is held,
which `NoLostCommand` checks for this key. The memory budget, request pacing, and the classifier's refusals and
corruption are covered by the drain worker's and the store's tests
([archive logs §10](../../docs/spec/components/archive-logs.md#10-verification)), not this model.
