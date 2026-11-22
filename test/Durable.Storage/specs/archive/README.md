# LSM model

TLC exhaustively checks a finite [Quint](https://quint-lang.org) model of the
[LSM](../../docs/spec/components/archive-lsm.md): its manifest, compaction, and garbage collection over each key's
history, and the history reads over it ([archive §8.1](../../docs/spec/components/archive.md#81-history-reads)). The
model makes requests, deliveries, responses, and decisions separate transitions, so their interleavings remain visible.
Every invariant has at least one mutant, a configuration which disables the guard which establishes it, and TLC must
produce that invariant's counterexample.

## Run

The [Quint runner](../ledgers/README.md#run) checks this model with the Ledgers models, and checks it alone with:

```powershell
.\eng\model-check-quint.ps1 -Model Archive
.\eng\model-check-quint.ps1 -Model Archive -Extended -Workers 8
```

`just archive-traces` validates the LSM simulation's traces against it (see [trace validation](#trace-validation)).

## Model and implementation mapping

| Model | Behavior | Spec | Implementation |
| --- | --- | --- | --- |
| `Archive.qnt` | One key's history in a closed ledger and its open successor, which closes once every batch is decided, with `Append`, `Replace`, and `Expired` entries and markers, each ledger cut into segments, the open one decided a segment at a time; drains of whole closed segments, of the open ledger too once a decided start of a later segment lies below a tail the drainer learns, and compactions as separate uploads and conditional commits by racing stateless workers; the reducer's drops, superseding only by an entry of a ledger the manifest shows complete, by a worker which may judge by older knowledge, of fewer complete ledgers and an `Expired` it keeps, and so keep more; the ledger map dropping complete ledgers in lineage order; a sweeper listing the store, then snapshotting the manifest, then deleting; readers which take the map, then the manifest, starting again when the map predates the open ledger's completion, and fold the prefix of the ledgers the map no longer lists, then each listed ledger from slot 0, the open one to a tail they choose, retrying on missing objects | [LSM](../../docs/spec/components/archive-lsm.md) §5, §8.1, §8.2, §8.3, §8.5, §8.6; [archive](../../docs/spec/components/archive.md) §8.1 | `DrainWorker`, `CompactionWorker` (`FirstKeptIndexAsync`), `ManifestCommitter`, `GarbageCollector`, `LsmArchiveStore` (`PredatesView`, `LedgerMapCoverage`), `ArchiveReader` |

Pausing an actor models arbitrary delay or loss of its response. A request may land after its
sender crashed, was fenced, or gave up.

## Invariants

| Invariant | Guarantee |
| --- | --- |
| `ReferencedExist` | LSM-I6, LSM-I7: every object the manifest references exists |
| `FoldPreserved` | LSM-I5: compaction preserves the fold of the manifest's and the undrained entries |
| `WatermarksMonotone` | LSM-I1: watermarks never decrease |
| `WatermarksAtSegmentStarts` | LSM-I2: every watermark is a segment start or its ledger's end |
| `NoDrainSuperseded` | LSM-I2: every drain output not referenced has its ledger's watermark at its start or past its end, so its commit applies or finds its goal held, and is never superseded |
| `ReadsCorrect` | LSM-I4, LSM-I5, HI-I1: a read which finds every object it needs returns the fold of the decided history at its cut, from the prefix of the ledgers its map no longer lists and every listed ledger from slot 0 |

## Mutants

| Mutant | Disabled guard | Counterexample |
| --- | --- | --- |
| `sweep-drain` | The sweeper deletes a drain output only once the watermark passed its start | `ReferencedExist` |
| `sweep-compaction` | The sweeper deletes a compaction output only once an input left the manifest | `ReferencedExist` |
| `snapshot-before-list` | The sweeper lists the store before it snapshots the manifest | `ReferencedExist` |
| `drop-ignoring-sections` | An `Expired` entry is dropped only if no committed object outside the compaction holds an older entry | `FoldPreserved` |
| `drop-ignoring-live` | An `Expired` entry is dropped only if no undrained live-ledger entry is older | `FoldPreserved` |
| `stale-drain` | A drain commits only while the watermark equals its start | `WatermarksMonotone` |
| `lsm-mid-segment-watermark` | A drain takes one whole closed segment, from the watermark to the next segment's start or the closed ledger's end; the mutant drains below any decided slot, inside a segment | `WatermarksAtSegmentStarts`, and, as a second case, `NoDrainSuperseded` |
| `ignore-missing` | A reader retries when an object it needs is missing | `ReadsCorrect` |
| `hold-by-watermark` | A reader's prefix is the entries of the ledgers its map no longer lists, and it scans each listed ledger from slot 0; the mutant, the rejected merge, takes every committed entry and scans each listed ledger from the watermark its manifest shows | `ReadsCorrect` |
| `supersede-by-open` | A `Replace` or `Expired` supersedes older entries only once the manifest shows its ledger complete: one in the open ledger deletes a closed ledger's entries which a reader, scanning the open ledger below its tail, still needs | `ReadsCorrect` |
| `no-predates-check` | A reader starts again when its map lists the open ledger which the manifest shows complete | `ReadsCorrect` |

## Bounds

The default exhaustive cases hold four objects over three histories: two `Append`s in one segment, then an `Expired` and
an `Append` in two segments of the successor; an `Append` and a `Replace`, then an `Append` and an `Expired`, each
ledger in two segments; and the first with the closed ledger's second entry a marker, a segment which holds nothing
else. The mutants run under the first history, where the closed ledger is one segment of two entries, so a drain inside
it is possible: under the second every slot starts a segment. The extended cases add a fifth object. There are no
depth cutoffs, sampling, or state constraints that discard reachable states. With compactions which keep more as
steps, the default positive cases reach 1,475,761, 1,543,321, and 1,330,836 distinct states, and took 54, 58, and 48
minutes, run together with 10 workers each on a 32-core machine, which other work loaded; the twelve mutant cases
found their counterexamples within 6.5 minutes together with 16 workers, after at most 571,339 distinct states
(`no-predates-check`).

## Compactions which keep more

`compactKeep` is a step: it uploads what a worker which judged by older knowledge keeps, `reduceAs(inputs, known,
dropExpired)`, superseding only by entries of the ledgers in `known`, any subset of those complete now, which a worker
which read an older manifest knew complete, and dropping an `Expired` only when `dropExpired`, which a worker that read
an older manifest, met a Bloom filter's false positive, or kept the `Expired` by the code's rule while an older ledger
containing its key was incomplete does not. `known` takes the complete ledgers and `dropExpired` true for the most a
compaction drops, the reduction; every other choice keeps a superset of it, from the inputs' rows, as the action's guard
requires. The choice is bounded, at most eight outputs a step, and faithful: an arbitrary superset is not a compaction
the code makes, and is unsafe, since one which keeps an `Append` an `Expired` superseded while it drops the `Expired`
resurrects the `Append` and breaks `FoldPreserved`. Trace validation maps each compaction output onto `compactKeep` with
the rows it holds, which the guard bounds and the invariants judge.

## Scope of the result

A successful run establishes the listed properties for every reachable state of each configured finite model, subject
to TLC's fingerprint collision probability. `Archive` models one key, so keys and partitions are absent: dyadic
partitions, grid depths, and pushes are compaction policy, and the commit, drop, and sweep rules it checks are stated per
key and per object. Unreadable footers and the sweeper's footer cache are covered by the LSM's tests
([LSM §10](../../docs/spec/components/archive-lsm.md#10-verification)), not this model.
[`LedgerMap.qnt`](../ledgers/README.md) checks the LSM's drains against the ledger map and each key's history.

## Trace validation

`ArchiveTraceTests` (`Orleans.Durable.Storage.Simulation.Tests`) records an LSM simulation of one key, beside another,
over a closed ledger and its open successor as the model's actions, with the run's decided entries and segment starts as
its constants: racing drainers which crash and restart, racing compactors which push and merge, two manifest committers,
a sweeper which restarts, a dropper, and a history reader, over the kit's `SimulatedObjectStore`, which reports each
request's effect at the instant it takes effect. A drain output's create from its ledger's watermark is `drainUpload`; a
compaction output's whose inputs the manifest lists is `compactKeep`, with the key's rows it holds; a manifest replace
is the `drainCommit`s and `compactCommit`s it applied, with the empty upload a drain of a segment without rows, or a
compaction which kept none, stands for; the sweeper's listing, manifest read, deletes, and end are `sweepFirst`,
`sweepSecond`, `sweepDelete`, and `sweepDone`; and a history read is `readMap`, `readManifest`, and `readFinish` at its
end. `compactKeep` uploads the rows a compaction output holds when they hold every row the reduction keeps, and only
rows of its inputs: a compactor which judged by an older manifest value, or by the code's coarser rule for an empty
`Delete`, keeps more, and the invariants judge the rows it kept. With `DURABLE_STORAGE_ARCHIVE_TRACES` set, each seed writes a
module `ArchiveTrace{seed}.qnt` whose `traceReplay` run applies every action in order and checks every invariant, each
upload's rows, the manifest's watermarks and references after each replace, and each read's answer against `reduced` of
the decided history at its cut. `.\eng\model-check-quint.ps1 -Traces <directory>` replays each trace with `quint test`,
then five trace mutants, each one step of the first validated trace which holds what it looks for, changed into a step
the model refuses, by its action or by the state it expects, while the replay through the step before passes:
`referenced-delete`, a sweep's delete of a referenced object, which `sweepDelete` refuses (`ReferencedExist`);
`over-dropping-compaction`, an output without its inputs' newest row, which `compactKeep` refuses (`FoldPreserved`);
`lowered-watermark`, a manifest whose watermark falls below the one before (`WatermarksMonotone`); `mid-segment-drain`,
a drain which ends inside its segment, which `drainUpload` refuses (`WatermarksAtSegmentStarts`); and `wrong-read`, a
read answered without its newest row (`ReadsCorrect`). `just archive-traces` runs both, over seeds 1 to 20 by default,
and CI's `archive-traces` job on every change ([verification
§5.1](../../docs/spec/verification.md#51-trace-validation)).
