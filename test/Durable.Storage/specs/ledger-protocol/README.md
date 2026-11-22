# Ledger model

This directory holds the Quint specifications ([`QuorumCoverage.qnt`](QuorumCoverage.qnt),
[`QuorumCoverageSealing.qnt`](QuorumCoverageSealing.qnt), [`AtomicLedger.qnt`](AtomicLedger.qnt),
[`LedgerProtocol.qnt`](LedgerProtocol.qnt), [`LedgerProtocolTrace.qnt`](LedgerProtocolTrace.qnt), and
[`ObjectLedger.qnt`](ObjectLedger.qnt)) of Apache BookKeeper's ledger protocol and the
[object-store ledger](../../docs/spec/components/object-ledgers.md) (per-slot and append layouts) as Clog uses them
for one [ledger](../../docs/spec/components/ledgers.md). Quint compiles each model to TLA+ via Apalache so TLC
exhaustively checks a finite model of the protocol: one writer, the ledger's primary, appending through the
[quorum ledger replica](../../docs/spec/components/ledger-protocol.md#83-quorum-operations) to `N` replicas with quorum
`Q`; replicas created by their first write, recovery write, or seal unless the node holds the ledger's tombstone;
recovery executors which seal the replicas, read forward, re-replicate without waiting, and close the ledger with a
conditional write of the ledger map; executors which learn `T` from the map; drops which leave a tombstone; replica
restarts; lost disks and corrupt values within a budget of `Q − 1` replicas; reads which fail; duplicated requests;
readers' tail checks; fetchers which read one replica and drain to the LSM below a contiguous tail they learned; and
the primary's explicit contiguous-tail writes. Requests, deliveries, and acknowledgements are separate transitions, so
their interleavings remain visible. TLC checks all safety invariants, step-by-step refinement of
[`AtomicLedger.qnt`](AtomicLedger.qnt) (the ledger with atomic appends and an atomic close which
[`LedgerMap.qnt`](../ledgers/README.md) models and the ledger storage contract's model), guard-disabling mutants,
assumption counterexamples, reachability witnesses, and weak-fairness liveness (`RecoveriesEnd`). Trace validation
([trace validation](#trace-validation)) replays each trace the implementation's simulations record against
`LedgerProtocol.qnt` through `LedgerProtocolTrace.qnt` and `quint test`.

## Origin and license

`LedgerProtocol.qnt`, `QuorumCoverage.qnt`, and `QuorumCoverageSealing.qnt` are adapted from Jack Vanlightly's
TLA+ specifications of BookKeeper, <https://github.com/Vanlightly/bookkeeper-tlaplus>, at commit
`fe199dc717b5723f5308aca74eda6ab3b03917e5` (`BookKeeperProtocol.tla`, `MessagePassing.tla`, `QuorumCoverage.tla`,
`QuorumCoverageFencing.tla`). They are distributed under the MIT License, whose text and copyright notice are in
[`LICENSE`](LICENSE). BookKeeper's fencing is Clog's seal, and the adapted models name it so throughout.
`LedgerChaining.tla`, which chains ledgers into a log through a leader and a compare-and-swap, informs the rationale
for sealing only open ledgers: Clog's chain is the ledger map, whose one write closes and opens ledgers together,
and [`LedgerMap.qnt`](../ledgers/README.md) models it.

Local changes from `BookKeeperProtocol.tla`:

- **No ensembles or fragments.** `E = Qw = N`, with no striping and no ensemble changes: Clog rolls a ledger onto
  other replicas instead. The `IN_RECOVERY` status and the recovery ensemble go with them; the map is `Open`,
  `Closed` at an end, or `Dropped`. Vanlightly's counterexample in which BookKeeper 4.13 creates an invalid fragment
  during recovery is excluded by construction: a ledger is one fragment, and its replicas are fixed for its
  life.
- **Positions from 0.** Clog's slots start at 0, and the contiguous tail is the last add confirmed plus one.
- **Clog's recovery reads.** BookKeeper's fix for its 4.13 defect fences recovery reads. Clog's reads do not seal;
  an absent answer counts only from a replica whose seal answer the executor held when it sent the read, which
  makes every negative final in the same way. The `unsealed-negatives` mutant restores the 4.13 behavior.
- **A writer which never stops.** The writer keeps acquiring, writing, and acknowledging after a replica answers
  `Sealed`, where BookKeeper's client withdraws, and after a seal stops the `LedgerWriter`'s sequencer, where the stopped
  lifetime grants and completes nothing: both only add behaviors, a sound over-approximation. The safety properties
  therefore rest on the quorum rule alone.
- **Window and contiguous tails.** The writer keeps at most `K` positions in flight (the sequencer's window), and every
  write carries its contiguous tail, which replicas retain and return with their seal answers. Concurrent appends
  send their writes in any order, so the writer may defer a position's send past a later position's acquisition
  while the window holds both, and a deferred send carries the tail current when it goes. Every other request
  carries its client's tail too, and a held replica retains it (LR-I6): a recovery executor's seals carry the tail
  it knows, any tail up to the writer's completed prefix, and recovery starts from the highest of it and the seal
  answers' tails (LP-I6), then carries that `c` with its reads and recovery writes; a reader's tail check carries a
  tail it learned, up to the completed prefix. A finished executor learns `T` from the map and carries it as a known
  contiguous tail (`LearnEnd`); a recovery's own end is a candidate, which no replica retains. With `TailWrites`, the
  primary sends its contiguous tail to every replica once its completions outrun what its writes carried, as
  the `LedgerWriter` does while idle, so a replica retains the tail of the last writes. A write reaches its replica
  before it answers (`AdmitWrite`): the node creates the replica if it holds none, with a durable create record (LS-I1),
  and the replica retains the write's tail at once, as `LiveReplica` admits every operation, so a seal or tail check
  served before the write answers reports that tail.
- **Pipelined recovery.** The executor reads every replica at each position from `c` up to `K` ahead of the next
  undecided one, through `c + 2K`, as the implementation's read window does, and decides positions in order. Every
  replica which returns a position counts as holding it. A position some replica returned is decided once the
  positions before it are, and recovery writes then go to every replica not known to hold it, while fewer than `Q`
  do, whether or not its read answered; the first position at which `N − Q + 1` sealed replicas' absence counted
  ends the recovery, and it closes once every position below that end is on `Q` replicas. A position the window's
  reads found at or above the end waits for nothing: the recovery closes with it on fewer than `Q` replicas, as the
  implementation's does (`NeverClosedWithFewCopiesAboveEnd`), and `AcknowledgedSurvive` shows the writer acknowledged
  none of them. The `no-late-copy`
  mutant sends recovery writes only to the replicas whose absence had counted when the position was decided. A
  recovery which meets a dropped replica is superseded: the map no longer holds the ledger open.
- **Creation unless dropped.** A node holds no state for the ledger until a write, recovery write, or seal creates its
  replica, a seal creating it sealed, which it does unless it holds the ledger's tombstone; the decision and the
  creation are one step, serialized with the drop. Once the map drops the ledger, each node which holds a replica reads
  the drop at its own pace and drops the replica, recording the tombstone in the same step. A node which holds the
  tombstone answers every request `Dropped`, a seal included: the writer and a tail check count that answer as sealed,
  and a recovery meeting it is superseded. A node which held no replica holds no tombstone, so a request delayed past
  the drop creates a stray replica there, which the node drops in turn. The `split-create` mutant decides a creation
  before the node's drop and applies it after; one token, spent by the first creation over a tombstone, bounds it to one
  such creation.
- **Restarts.** A node restarts with its DATA and seal, which are durable, and rebuilds its retained contiguous
  tail as `max(0, h + 1 − K)` from its highest DATA position `h`. Every answer a replica gives is durable when it
  leaves (LR-I4, conventions §13), and `NoRevokedAnswer` holds every replica but an amnesiac one to what it answered
  across every restart: each position it answered `Written` or returned as DATA, intact or corrupt, and its seal once
  it reported itself sealed. A mutant per answer gives it from state whose record is not yet durable, kept in
  `volatile` until `Flush` makes every such record durable, as a WAL's durable frontier does; a restart before
  then loses the state and revokes the answer. `write-volatile` answers a writer write `Written`, `copy-volatile` a
  recovery write, `create-volatile` a write or seal which created the replica, and `seal-volatile` a seal.
  Under three of them the correct path applies a change at once and answers once its record is durable, leaving
  the request in flight until `Flush`: `read-volatile`'s writer writes, whose DATA its reads return early;
  `repeat-seal-volatile`'s seals, which another executor's seal answers from the mark; and the seals which
  `write-sealed-volatile`'s writer writes answer `Sealed` from and `check-sealed-volatile`'s tail checks report
  sealed from. Every other operation which meets a record not yet durable waits for it, as the implementation's
  seal waits for the writer writes before its mark (LS-I11) and a slot's stripe orders its writes. `Missing`
  counts only from a replica sealed before the read, so its durability is the seal's, which `seal-volatile`,
  `repeat-seal-volatile`, and `restart-drops-seal` cover; `restart-drops-seal` replays the WAL without the seal, the
  analogue of `FenceOnUncleanShutdown = FALSE` in `BookKeeperProtocolWithLimbo.tla`. A drop's `Dropped` is
  [`LedgerDrop.qnt`](../ledgers/README.md)'s `drop-volatile`. A contiguous tail is no durable answer: a
  restart rebuilds it from the window (LR-I6), and `reopen-h1` breaks that rebuild.
- **Faults.** The `Faults` constant enables them, within a budget of `Q − 1` replicas. A node whose disk is lost returns
  as a new node, so the old replica never answers again (LS-I6, OR-I14). A slot which fails its integrity check, a
  stored value or, after damage to the WAL, a slot which holds no record (LS-I5), answers neither DATA nor `Missing`
  (LR-I2) until a recovery write replaces it (LR-I1, LR-I5); the `corrupt-as-missing` mutant
  answers `Missing`, as a bookie which answered `NoSuchEntry` for a corrupt entry log did (Vanlightly, *Apache
  BookKeeper Insights Part 2*). Amnesia, a replica emptied under the same identity, is the fault LS-I6 and OR-I14
  exclude; the `assumption-amnesia` case is its expected counterexample, as `BookieRestartsWithDataLoss` in the Limbo
  specification shows for a bookie without its cookie. Tombstone loss, one node's tombstone cleared under its identity,
  is the fault LS-I5's detection excludes: the `assumption-tombstone-loss` case is its expected counterexample, in which
  the node recreates the dropped replica and the deposed writer reaches a quorum, so detection, not the `Q − 1` budget,
  carries it. A read failure, outside the budget, returns no answer to the executor (`FailRead`); a live replica's read
  fails only once its position was found, so its requests are still eventually delivered.
- **Duplicates.** The `Duplicates` constant bounds repeated deliveries and the writer's repeats, together. A
  delivery may leave its request in flight, so it lands again after any later step, and the writer repeats a
  position's write to a replica which has not answered `Written`, with the contiguous tail it holds when the repeat
  starts (LP-I12); at `K = 2` that tail can be fresher than the first write's (`NeverFresherRepeat`). A repeat which
  meets its own landed write on an unsealed replica answers `Written` (ledger replica §6.1), and the writer counts each
  replica's answers once (LP-I4); the `count-answers` mutant counts a repeated answer again.
- **Readers.** A reader's tail check, a `Ledger`'s seal check through which the primary checks that its
  ledger is open and other readers find its contiguous tail, takes `Q` replicas' answers one at a time and decides
  while the others are outstanding. It is the counterpart of the quorum tail check over positional SEAL entries, which
  once reported a completed seal as unsealed ([#102](https://github.com/ReubenBond/clog/issues/102)). Readers begin at
  any time: a check which began once a recovery decided its end, or after the close, must report the ledger sealed,
  and every check's tail must be at most `T`, which for a check that reported the ledger open is the Delos erratum's
  `tailpos1 ≤ tailpos2`. A check's tail may exceed a losing recovery's candidate end
  (`NeverCheckTailAboveCandidate`): only the recorded `T` bounds it.
- **Fetchers.** A fetcher is a reader of the ledger other than its primary, or a drain to the LSM. It learns a
  contiguous tail from one replica's contiguous-tail read, which also carries its own, or `T` from the map once
  recorded, and takes the positions below it as decided: a read returns the first DATA a replica holds and repairs a
  preferred replica which answered `Missing` or a corrupt value with a recovery write; a drain raises the LSM
  watermark to the tail. Every position it takes is decided: it and every lower position reached `Q` `Written`
  answers, and once the ledger ends it lies below `T`.
- **Liveness.** `LiveSpec` adds weak fairness to deliveries to a set of live replicas, to recovery's retries of
  reads which have not counted, and to the close, and `RecoveriesEnd` requires every recovery which began to end
  closed or superseded.
- **Reductions.** A replica's answer reaches its client in the step which processes the request: no other actor
  reads a client's state, so a delayed answer is a delayed next step of its client, and a lost answer only
  withholds evidence. Message counts and the delivery-count bookkeeping of `MessagePassing.tla` are replaced by sets
  of requests in flight, each delivered at any time or never, and again within the duplicate budget. Standard modules
  replace `FiniteSetsExt` and `SequencesExt`.
- **Separate answers.** With `SeparateAnswers`, which trace validation uses, a replica's answer joins `answers` and
  reaches its client in a later step, in any order, or never, as the implementation's quorum handles its replicas'
  answers in the order they complete. A client then repeats, within the duplicate budget, a request whose answer it
  lacks: the writer a write to any replica, and a recovery a seal, a read of an undecided position in its window, or a
  recovery write of a position fewer than `Q` replicas are known to hold, as its retries do (ledger replica §6.5); a
  read repeated once the recovery holds the replica's seal answer follows the seal, while the read sent before it may
  still be in flight. A recovery can therefore reach its seal coverage while a sealed replica's answer is still on its
  way (`NeverCoverageWithAnswerInFlight`).
- **Properties.** The invariants below replace `NoDivergenceBetweenClientAndMetaData`,
  `AllAckedEntriesAreReadable`, and `AllCommittedEntriesReachAckQuorum`. Each has mutants, and every mutant changes
  the protocol, never the property it violates.

Local changes from `QuorumCoverage.tla` and `QuorumCoverageFencing.tla`: replicas and the quorum `Q` in place of
bookies and the ack quorum; the module `QuorumCoverageSealing`, whose `SEALED`, `SealResponses`, and
`EnoughSealed1` – `EnoughSealed4` are Vanlightly's `FENCED`, `FencingResponses`, and `EnoughFenced1` –
`EnoughFenced4`; and a `Mutation` constant for the coverage mutant.

[`ObjectLedger.qnt`](ObjectLedger.qnt) models both layouts (`Layout = "per-slot"` and
`Layout = "append"`) of the [object-store ledger](../../docs/spec/components/object-ledgers.md) over one strongly
consistent object store, refining [`AtomicLedger.qnt`](AtomicLedger.qnt): one writer acquiring within window `K` and
issuing either conditional per-slot batch creates (which may land late or with unknown outcomes and settle by occupant
read) or append-layout single-in-flight coalesced appends and `ROLL` frames across extents (creating the next extent
only after its `ROLL` is written, OL-I2, OL-I10); concurrent recoveries which check `dropped`, create `sealed`, either
list from the newest slot down to `max(a, h + 1 − K)` and place conditional SEALs over `[s, s + K)` and `(T, T + K)`
(looping when the window is filled) or list the newest extent, seal it, and follow any `ROLL` frame by creating and
sealing the named extent, re-read `dropped` before answering, and close the ledger in the map (OL-I3, OL-I4, OL-I6);
readers with stale map views whose checks fix their bound before reading `sealed` (OL-I5); fetchers reading below
learned tails whose reads check `dropped` on absent slots and SEALs—including a late SEAL delayed past a sweep
(OL-I7, OL-I8); and the sweeper creating `dropped` before deleting batch objects or replacing the newest extent with a
non-appendable tombstone and deleting older extents while preserving markers, SEALs, and tombstones and reclaiming
straggler batches on later sweeps (OL-I4, OL-I8, OL-I9).

## Run

Java 21, Node.js, and PowerShell 7 are required:

```powershell
.\eng\model-check-quint.ps1 -Model QuorumCoverageSealing,AtomicLedger,LedgerProtocol,ObjectLedger
.\eng\model-check-quint.ps1 -Model QuorumCoverageSealing,AtomicLedger,LedgerProtocol,ObjectLedger -Tier Quick -Since main
.\eng\model-check-quint.ps1 -Model QuorumCoverageSealing,AtomicLedger,LedgerProtocol,ObjectLedger -Tier Extended -Workers 8
.\eng\model-check-quint.ps1 -Model LedgerProtocol -Tier Quick -Only 'ledger-protocol-mutant-*'
.\eng\model-check-quint.ps1 -Model ObjectLedger
.\eng\ledger-traces.ps1 -Workers 4
.\eng\model-check-quint.ps1 -Traces artifacts\ledger-traces -Only 'quorum.*'
.\eng\model-check-quint.ps1 -Model QuorumCoverageSealing
.\eng\model-check-quint.ps1 -Model AtomicLedger
.\eng\model-check-quint.ps1 -Model LedgerProtocol
.\eng\model-check-quint.ps1 -Model LedgerProtocol -Extended
.\eng\model-check-quint.ps1 -Model ObjectLedger
.\eng\model-check-quint.ps1 -Model ObjectLedger -Extended
```

The Quint runner (`test/Durable.Storage/eng/model-check-quint.ps1`, `just quint-models`, `just models`, `just models-quick`,
`just models-changed`, `just models-extended`) type-checks `QuorumCoverage.qnt`, `QuorumCoverageSealing.qnt`,
`AtomicLedger.qnt`, `LedgerProtocol.qnt`, `LedgerProtocolTrace.qnt`, and `ObjectLedger.qnt` with the pinned Quint
toolchain (`test/Durable.Storage/eng/quint/package-lock.json`) and exhaustively verifies them through the pinned, SHA-256-verified
Apalache/TLC backend. `-Tier` selects `Quick`, `Default` (Quick plus the nightly cases), or `Extended` (`-Extended` is
a shorthand for `-Tier Extended`), and `-Since` skips unchanged models when their files and the runner did not change
since a commit's merge base. Each TLC process uses `-Workers` workers (2 by default) and a 4 GiB heap. Results,
generated Quint instance modules, complete checker logs, and counterexamples are written under
`artifacts/model-checks/quint`.

A positive case succeeds only when TLC reports success, exits zero, and exhausts its queue. An expected invariant or
witness counterexample succeeds only with TLC's violation exit status and the named invariant or temporal property. For
weak-fairness liveness (`RecoveriesEnd`, `no-late-copy`, `ignore-dropped`), `test/Durable.Storage/eng/model-check-quint.ps1` appends
`Fairness` (`WF_vars(...)`) and `LiveSpec` to the Quint-compiled TLA+ module and checks `RecoveriesEnd` with TLC.
One expected counterexample, the `seal-short` mutant at `K = 2` (`ledger-protocol-mutant-seal-short-k2-RecoveryBounded`),
lies 44 steps deep beneath billions of states, so it runs as a seeded random simulation (`quint run --seed=1`), which
replays the same counterexample on every run; a simulation never stands in for an exhaustive positive case.

## Protocol and implementation mapping

| Model | Behavior | Spec | Implementation |
| --- | --- | --- | --- |
| `LedgerProtocol.qnt` writer | Acquisition within the window, writer writes to every replica carrying the contiguous tail, repeats carrying the tail when they start, `Q` `Written` answers from distinct replicas, in-order completion | [Ledger protocol](../../docs/spec/components/ledger-protocol.md) §8.1, §8.2, §8.3, LP-I4, LP-I12; [Ledger replicas](../../docs/spec/components/ledger-replicas.md) §6.1 | `LedgerWriter.AppendAsync`, `Sequencer`, `Ledger`'s writer write |
| `LedgerProtocol.qnt` replicas | Creation unless dropped, the seal, writer and recovery writes, drops which leave a tombstone, stray replicas | [Ledger replicas](../../docs/spec/components/ledger-replicas.md) LR-I7, LR-I8; [LedgerStore](../../docs/spec/components/ledgerstore.md) LS-I2, LS-I11 | `LedgerStore`, `LiveReplica`, `InMemoryLedgerReplicaTransport` |
| `LedgerProtocol.qnt` recovery | Seal coverage, forward reads from the highest contiguous tail, pipelined re-replication, absence from sealed replicas, retries, the conditional close, and learning `T` | [Ledger protocol](../../docs/spec/components/ledger-protocol.md) §8.3, LP-I6, LP-I7, LP-I11 | `Ledger.RecoverAsync`, `Ledger.ObserveRecordedTail`; the conditional close *not yet in code* (row 1.4) |
| `LedgerProtocol.qnt` readers | Tail checks begun at any time: `Q` answers, each a sealed flag and a contiguous tail, taken at different instants | [Ledger protocol](../../docs/spec/components/ledger-protocol.md) §2.1 `ReadTailAsync`, LP-I9 | `Ledger.ReadTailAsync`, `LedgerWriter.ReadTailAsync` |
| `LedgerProtocol.qnt` fetchers and tail writes | Contiguous-tail reads of one replica, the map's `T`, reads of one replica below the learned tail with fallback and read repair, drains to the LSM below it; the primary's explicit contiguous-tail writes | [Ledger replicas](../../docs/spec/components/ledger-replicas.md) LR-I6; [Ledger protocol](../../docs/spec/components/ledger-protocol.md) §8.2, LP-I10; [Ledgers](../../docs/spec/components/ledgers.md) LG-I8; [LSM](../../docs/spec/components/archive-lsm.md) §8.1, LSM-I2 | `Ledger.ReadAsync`; single-replica reads, explicit tail writes, `LedgerReader`, and drains *not yet in code* (rows 5.1, 1.3, 3.2) |
| `LedgerProtocol.qnt` restarts | Durable DATA and seal; the retained tail rebuilt from the window | [Ledger replicas](../../docs/spec/components/ledger-replicas.md) LR-I6; [LedgerStore](../../docs/spec/components/ledgerstore.md) LS-I11 | `LedgerStore.OpenAsync`, `LiveReplica.FinishRecoveryAsync` |
| `QuorumCoverageSealing.qnt` (adapted from Vanlightly's `QuorumCoverageFencing.tla`) | Every cohort and quorum of up to five replicas, responses arriving in any order | [Ledger protocol](../../docs/spec/components/ledger-protocol.md) §1 rationale | — |
| `LedgerProtocolTrace.qnt` | One recorded trace: the module's events, in order, each bound to the action and arguments it names | [Verification](../../docs/spec/verification.md) §5.1 | `LedgerTrace`, `LedgerTraceExport`, the recovery observer of `Ledger`, and the `Sequencer` observer of a `LedgerWriter` |
| `AtomicLedger.qnt` | The ledger `LedgerMap.qnt` models, and the ledger storage contract's model: slots granted at any time (`issued`), acknowledged in slot order while it is open, a seal which begins with a recovery and is fenced once one decides its end, one close which records `T` between the acknowledged and granted prefixes once fenced, acknowledgements below `T` after it, and a drop; readers' checks, which carry and report a tail at most the bound and report sealed only once a seal began, and sealed when they began fenced; and fetchers' tails, reads, and drains below a tail at most the bound | [Ledgers](../../docs/spec/components/ledgers.md) LG-I5; [Ledger protocol](../../docs/spec/components/ledger-protocol.md) LP-I8, LL-I5 to LL-I7 | `LedgerWriter`, `ILedgerStorage`, `LedgerTransitions` (*not yet in code*) |
| `ObjectLedger.qnt` | Per-slot and append object-store ledgers: conditional per-slot batch creates with delayed and unknown landings settled by occupant read, or single-in-flight coalesced appends and `ROLL` frames across extents (creating the next extent only after its `ROLL` is written); concurrent recoveries placing conditional SEALs over `[s, s + K)` and `(T, T + K)` or sealing the newest extent and following `ROLL` frames; `sealed` and `dropped` markers; stale readers checking `sealed` and reading slots with drop-marker checks (including late SEALs delayed past a sweep); fetchers; and the sweeper reclaiming batches or replacing the fence extent with a non-appendable tombstone and deleting older extents while preserving markers, SEALs, and tombstones | [Object-store ledgers](../../docs/spec/components/object-ledgers.md) §5, §8 (OL-I2 to OL-I10); [Ledger protocol](../../docs/spec/components/ledger-protocol.md) LL-I1 to LL-I9 | `ObjectLedgerStorage`, `AppendObjectLedgerStorage`, `ObjectLedgerSweeper` |

The open check reads the sealed flag of `Q` replicas, each at some instant after it began. Seals are durable
until a drop, whose tombstone answers sealed for the node's life, so a check which began after a recovery decided
its end reads every flag at or after that point, and its soundness is also a property of each state from then on:
every `Q` replicas which answer include a sealed or dropped one (`OpenCheckSound`).

## Trace validation

[`LedgerProtocolTrace.qnt`](LedgerProtocolTrace.qnt) checks that a run of the implementation is a behavior of the model
([verification §5.1](../../docs/spec/verification.md#51-trace-validation)). With `DURABLE_STORAGE_LEDGER_TRACES` set, the quorum
simulation (`QuorumSimulationTests`), the crash simulation (`LedgerStoreSimulationTests`), the `Ledger` simulation
(`LedgerSimulationTests`), and the scripted runs of serve-time landings (`ServedLandingSimulationTests`), of a reader's
read repair (`ReadRepairSimulationTests`), and of a reader's learn from its own replica (`LearnTailSimulationTests`)
write each run's `LedgerTrace` of its ledger as a Quint trace module (`LedgerTraceExport`): the trace and the constants
the run fixes, its replicas, `Q`, `K`, positions, recovery executors, tail checks, the map views of the clients which
learn the recorded `T` (fetchers), duplicate budget, and faults. The trace module imports `LedgerProtocolTrace.qnt`
(which instantiates `LedgerProtocol.qnt` with `SeparateAnswers`) and replays the module's events in order under
`quint test`:

| Event | Logged where | Model step |
| --- | --- | --- |
| `acquire` | The writer acquires a position | `Acquire`, or `Send` of a position a later acquisition passed |
| `call` | A client sends a request | None while the request is in flight; otherwise `Resend`, `ResendSeal`, `ResendRead`, or `ResendCopy`; a writer write which carries the `T` its client learned stands for the model's repeat, which carries the completed prefix |
| `serve` | A node answers a request | The requests whose records it shows landed without an answer, then `DeliverWrite`, `DeliverSeal`, `DeliverRead`, `DeliverCopy`, or a check's answer, whose answer must be the model's; a reader's read is checked against the replica's state, and one of its preferred replica, which lacked the position, whose read repair followed, is its map view's `ReadBelowTail`, which sends the repair, so the repair's `call` is a fetcher's copy in flight; a contiguous-tail read by a client other than the writer outside any check is its map view's learn, `LearnTail`, whose answer must be the replica's retained tail |
| `receive` | The writer receives an answer; a recovery handles one, in its order | `Receive` |
| `arrive` | A tail check's answer reaches it | A `Receive` the check takes in any order |
| `appended`, `recovered`, `checked` | A client step ends | None; the model's state must match the outcome; the writer's own check reports its ledger's known contiguous tail, which its completions and the `T` it learned raise meanwhile |
| `completed` | A `LedgerWriter`'s slot completes | `Complete`, through the slot |
| `stopped` | A `LedgerWriter`'s sequencer lifetime stops | None: the model's writer never stops, which only adds behaviors |
| `recover`, `check` | A recovery or tail check begins | `BeginRecovery`, `BeginCheck`; a check which carries a learned `T` begins as one carrying the lesser of `T` and the completed prefix |
| `tail` | The idle writer sends its contiguous tail explicitly, at its first request | `WriteTail`, with `TailWrites` set; a node's `serve` of it is `DeliverTail`, whose answer the writer ignores |
| `close` | A recovery's conditional write closes the ledger in the map | `Close`, on the map version the recovery read, at exactly its candidate end, once the completed prefix reaches the prefix the writer acknowledged by then |
| `superseded` | A recovery's close loses to another's | `Close`'s superseded branch |
| `learn` | A client's `Ledger` learns the recorded `T` (`ObserveRecordedTail`) | `ReadMap` of its map view |
| `retain` | A replica admits a request which carries a learned `T` | `LearnTail`, when it raises the replica's tail (LR-I6) |
| `restart` | A node restarts | The writes, recovery writes, and seals which landed without an answer, then `Restart` |
| `damage` | Media damage reaches a node's WAL | None: damage alone changes nothing the model holds, and a later read or recovery finds it; the node joins the damaged nodes (`dmg`) |

Between events the trace takes the steps no event logs, each a step the implementation takes without logging it
(`Silent`, which cites the guarantee behind each): acquisitions a later acquisition passed (LP-I12); `Complete` up to the
tail the next event carries, a logged completion, or the acknowledged prefix at the close (LP-I3), since a
`LedgerWriter` raises the tail it carries as its sequencer completes a slot, before the completion is logged; `Corrupt`
just before a node answers a slot corrupt, since a read finds damage (LR-I2, LS-I5); `AdmitWrite` before a seal or
check whose tail it raised, since a replica retains a write's tail when it admits it (LR-I6), or before a corrupt answer
from a node whose replica only that admission created (LS-I1); a check's admission, a delivery whose answer is lost,
before a seal or check whose tail it raised, for the same reason; the requests in flight to a node whose records
reached its disk before it crashed, without an answer, at the first event which shows them: a writer write's or a
recovery write's `DeliverWrite` or `DeliverCopy` before the restart which recovers its DATA or a read the crashed
process served which returned it (LR-I4), and a seal's `DeliverSeal` before the restart which recovers the replica
sealed or an answer the crashed process gave which reported it sealed (LS-I11), each with its answer lost; and, at a
restart, a creation whose record landed without its write's, and the faults the node's recovery found (LS-I5). The
export names each request which landed in the event which shows it (`landed`, `copied`, `sealedBy`), so the
restart's check, or the answer's, decides whether it landed, and no other event delivers one; the quorum and crash
simulations crash a node while it serves a recovery's seal and another while the recovery runs, with the request's WAL
write in flight or just durable, so their traces hold such requests. Those crashes rarely leave the crashed process time
to serve another request first, so a scripted run, at its own seed, takes each serve-time step on every run: one node
holds its answers to a writer write, a recovery's seal, and the recovery's copies once their records are durable,
returns the write's and the copy's DATA to reads and answers a writer write `Sealed`, and then crashes, which discards
the held answers, and restarts; its test requires the rendered trace to name the write, the copy, and the seal at those
serves. Each simulated node logs every media damage it
suffers as it happens, so a
silent `Corrupt`, at a corrupt answer or a restart's faults, and the `AdmitWrite` before a corrupt answer, are allowed
only at a node an earlier `damage` event named. A restart logs what the node's recovery established: whether it holds the
replica, its seal, and the slots which hold DATA and which are faulty. The model's replica then matches it
(`Recovered`): the logged seal, every value it holds intact among that DATA, that DATA among the values it holds, and
every value it holds DATA or faulty, never missing; a slot whose damage a read found reports DATA from the recovered
slot table until a read finds the damage again. Whether a delivery reached its client (`deliver`) and whether it left
its request in flight for another delivery (`keep`) are derived from the logged answers. A node logs its answers in the
order its lane completes them, so `LedgerTraceExport` orders a seal before the `Sealed` answers it caused, and a write
before a read which returned its DATA, within one process of the node: a restart ends the order it corrects, so an
answer logged before a crash keeps its place, and the model checks that its record was durable; a restart which
recovers the replica sealed counts as its seal. A `run` test (`traceValid`) replays every event in order and checks the
model invariants at every step. When a trace fails, `quint test` reports the failing event and
`test/Durable.Storage/eng/model-check-quint.ps1 -Traces` locates the failing step. Every step is checked against `TypeOK`, `NoRevokedAnswer`,
`ReadersSeeClose`, `RecoveryBounded`, and `OpenCheckSound`, so each trace is a bounded check of them at the
implementation's scale, with every freedom trace validation adds. Each trace
maps one recovery's close of the ledger: the quorum simulation's last recovery, the crash simulation's final one, and
the `Ledger` simulation's first executor to return, whose rival's close then loses. The invariants which hold once the
ledger ends (`AcknowledgedSurvive`, `NoAcknowledgementAtOrAboveEnd`, `EndSealed`, `CheckTailBelowEnd`,
`RetainedBelowEnd`, `RecoveriesConverge`) check every state from the close on, with the
writer's completed prefix at the close at least the prefix it had acknowledged: a `LedgerWriter`'s logged completions,
or the writes a quorum acknowledged with every lower one. An executor which begins its recovery after the close read
the map before it, as one with a stale view does, so the export places its begin before the close, where its seals wait
in flight. A recovery executor's reads outside its recoveries are a reader's, and a primary's recovery, which logs no
`c`, begins with the tail its seals carry. Each trace replays in seconds.

`.\eng\ledger-traces.ps1` (`just ledger-traces`) runs the three simulations over their seed corpora, or over
`DURABLE_STORAGE_SIM_SEEDS`, and the scripted runs at their seeds, and `.\eng\model-check-quint.ps1 -Traces` validates each
trace under `artifacts/ledger-traces`. A run which exports its trace sets `LedgerTrace.LogsRecoveredReplicas`, whose
reading of a
restarted node's slot table reads each unloaded leaf it covers into a scratch copy before the node serves, so the node
loads the leaves an untraced run's would, as their requests reach them. Nineteen trace mutants then each change one
event of the first validated trace which holds
the event the mutant needs, and the model rejects each at that event, with every event before it consumed: a recovery's
end moved by one (`mutant-recovered-end`); a close at an end one past its recovery's (`mutant-close-at-another-end`); a
writer write's `Written` answer reported `Sealed`
(`mutant-written-as-sealed`); after a restart, a read of a position the node had answered reported `Missing`
(`mutant-revoked-data`); after a sealed replica's restart, its first answer which reports it sealed reported unsealed
(`mutant-revoked-seal`); a recovery's `Missing` read counted as sent after the replica's seal answer when it was sent
before (`mutant-early-mark`); a seal answering a tail above every tail any request carried, which bounds `AdmitWrite`
(`mutant-raised-tail`); the writer's explicit contiguous-tail write carrying one past its completed prefix
(`mutant-eager-explicit-tail`); a reader's read repair carrying one past its fetcher's tail (`mutant-eager-repair-tail`);
a reader's learn of one past its replica's retained tail (`mutant-eager-learn`);
a `Q`-th replica answering corrupt, which bounds `Corrupt` to the fault budget
(`mutant-corrupt-quorum`); a read of DATA from a node no `damage` event named answered corrupt while the fault budget
still admits it, which binds `Corrupt` to the logged damage (`mutant-corrupt-from-undamaged`); and five which test the
restart's check itself, a restart which leaves out a position the
node answered intact (`mutant-restart-drops-data`), reports DATA no client wrote (`mutant-restart-adds-data`), reports a
slot it answered corrupt as missing (`mutant-restart-damage-as-missing`), loses a seal an answer reported
(`mutant-restart-unseals`), or reports the replica sealed where no seal landed without an answer
(`mutant-restart-seals`), which bounds the silent `DeliverSeal`; and two which drop the request a crashed process's
serve names, its `Sealed` answer's seal (`mutant-sealed-without-seal`) and a read's landed write or copy
(`mutant-data-without-write`), so the answer has no landed record to show.
A mutant whose event no validated trace holds fails the run.

## Invariants

| Invariant | Guarantee | Mutants |
| --- | --- | --- |
| `AcknowledgedSurvive` | No acknowledged entry is lost by recovery and close: every position the writer acknowledged before the close lies below `T` (LP-I7, LP-I8), with up to `Q − 1` replicas lost or corrupt | `short-negatives`: recovery ends on `N − Q` absent answers; `sealed-accepts`: a sealed replica accepts writer writes; `corrupt-as-missing`: a corrupt value answers `Missing`; `count-answers`: the writer counts a replica's repeated `Written` answer again; assumption `amnesia`: a replica emptied under its identity |
| `NoAcknowledgementAtOrAboveEnd` | A fenced writer acknowledges nothing at or above `T`, then or later, including after the drop (LP-I4, LP-I8) | `count-sealed`: the writer counts a `Sealed` answer; `sealed-accepts`; `seal-no-create`: a seal answers for a replica it does not create; `seal-volatile`: a replica answers a seal before its record is durable; `restart-drops-seal`: WAL replay leaves the seal out; `no-tombstone`: a drop records no tombstone, so delayed writes recreate dropped replicas; `split-create`: a creation decided before the node's drop is applied after it; `copy-ignores-tombstone`: a late recovery write recreates an unsealed replica on a dropped node, with two positions; `count-dropped`: the writer counts a `Dropped` answer toward the write quorum; regression `unsealed-negatives`; assumption `tombstone-loss`: one node's tombstone cleared under its identity |
| `EndSealed` | Once closed, fewer than `Q` replicas would accept a writer write, before and after the drop (LR-I7, LR-I8) | `copy-unseals`: a recovery write clears the seal; `restart-drops-seal`; `no-tombstone`; `split-create` |
| `ReadersAgree` | Every reader agrees on a closed ledger's entries: each position below `T` is held by `Q` replicas, and each position has its one writer's value (LP-I7) | `no-rereplication`: recovery sends no recovery writes and closes without them |
| `ReadableBelowEnd` | Within the fault budget, every position below `T` has an intact copy on a node not lost (LR-I2, LP-I7) | `no-rereplication`, with one replica's disk lost: an entry recovery found on one replica is lost with it |
| `RecoveriesConverge` | Concurrent recoveries converge: the map records one `T` | `unconditional-close`: the close ignores the map's version |
| `ReadersSeeClose` | No reader observes an open ledger once a recovery decided its end: a tail check begun then reports sealed (LP-I9) | `open-check-short`: the check decides on `Q − 1` answers; `sealed-quorum`: it reports sealed only when `Q` answers are, as the tail check over positional SEAL entries of #102 read a minority SEAL as unsealed; `check-ignores-seal`; `check-counts-dropped`: a dropped replica's answer counts as unsealed |
| `CheckTailBelowEnd` | No reader observes a tail past `T`, whenever its check began: a check which reported the ledger open and the recovery which then closed it meet the Delos erratum's `tailpos1 ≤ tailpos2` (LR-I6, LP-I9) | `check-stored-tail`: a replica answers its highest DATA position plus one, a tail which zombie appends raise; `retain-recovered` |
| `OpenCheckSound` | The same soundness over every state once a recovery decided its end: every `Q` replicas which answer include a sealed or dropped one. The extended cases, which run no reader, check it | `seal-no-create`; `copy-unseals`; `no-tombstone` |
| `TombstoneExcludesReplica` | No node holds both a replica of the ledger and its tombstone (LR-I7, LS-I2). Every protocol case checks it | None: it bounds the model's state, and the mutants which create over a tombstone clear it, so each breaks the properties above instead |
| `NoRevokedAnswer` | A crash never revokes an answer: every replica but one amnesia emptied still holds each position it answered `Written` or returned as DATA, intact or corrupt, and its seal once it reported itself sealed, across every restart (LR-I4, LS-I11, conventions §13). Every safety case and every trace checks it | `write-volatile`: a writer write answers `Written` before its record is durable; `read-volatile`: a read returns DATA whose record is not yet durable; `copy-volatile`: a recovery write answers before its record is durable; `create-volatile`: a seal or write which created the replica answers before the creation's record is durable; `seal-volatile`; `repeat-seal-volatile`: a seal which meets another executor's mark answers before the mark's record is durable; `write-sealed-volatile`: a writer write which meets a mark answers `Sealed` before its record is durable; `check-sealed-volatile`: a tail check reports a marked replica sealed before its record is durable. Each counterexample is the answer followed by the replica's restart |
| `RecoveryBounded` | Every position a recovery finds lies below its starting contiguous tail plus `2K` (LP-I7, LP-I2) | `seal-short`: recovery starts after `N − Q` seal answers, at `K` of 1 and 2 |
| `RetainedBelowEnd` | Every retained contiguous tail is at most `T` (LR-I6, LP-I11) | `retain-recovered`: recovery writes carry the positions they copied; `learn-own-end`: an executor carries its own candidate end where the map recorded a lower one; `reopen-h1`: a restart rebuilds `h + 1` |
| `BelowTailDecided` | Every position a fetcher read or drained below a learned contiguous tail is decided: while the ledger is open it and every lower position reached `Q` `Written` answers, and once it ends it lies below `T` (LR-I6, LP-I10, LP-I11, LG-I8, LSM-I2) | `eager-tail`: writes and the primary's contiguous-tail writes carry one past the highest position `Q` replicas answered, at `K = 2`, with a gap below it; `fetch-stored-tail`: a fetcher learns one past a replica's highest DATA position, with every writer path intact; `drain-max-stored`: a drain takes one replica's highest DATA position, as an archive of durable entries which are not yet decided does |
| `RecoveriesEnd` (liveness) | Every recovery which began ends closed or superseded, with requests to `max(Q, N − Q + 1)` live replicas delivered (LP-I7) | `ignore-dropped`: a recovery waits on dropped replicas; `no-late-copy`: a decided position's recovery writes go only to the replicas whose absence had counted, none to a replica whose read had not answered |
| `Equivalence` (`QuorumCoverageSealing`) | The four definitions of quorum coverage agree | `off-by-one`: `N − Q` sealed replicas count as coverage |
| `RefinesAtomicLedger` (refinement) | The map's status, the writer's granted and acknowledged prefixes, the recorded end, the seal, readers' checks, and fetchers' tails, reads, and drains move only by `AtomicLedger.qnt`'s steps, so `LedgerMap.qnt`'s atomic appends and close stand for this protocol (LG-I5, LP-I8), and the contract's properties below hold of it. The seal is `Sealing` once a recovery began and `Sealed` once one decided its end or the ledger closed; a check is `Reading` until it decides. Every safety case checks it | `short-negatives`: a close below an acknowledged slot; `unconditional-close`: a second close; `no-tombstone`: an acknowledgement at or above `T` after the drop; `drop-open`: the drop of an open ledger; `check-ignores-seal`, `check-stored-tail`, and `fetch-stored-tail`, one per contract property. Close's upper bound, `T` at most the granted prefix, has no mutant: no recovery finds a position the writer never acquired |
| `AtomicLedger` `OpenCheckSound` | A check begun once a recovery decided its end, or after the close, reports sealed: an unsealed answer proves no recovery completed before the check began (LL-I5) | `atomic-late-open`: a check begun fenced may report the ledger open |
| `AtomicLedger` `CheckTailBelowEnd` | No check reports a tail past `T`, whenever it began (LL-I7) | `atomic-check-issued`: a check may report a tail up to the granted prefix |
| `AtomicLedger` `BelowTailDecided` | Every position a fetcher read or drained is acknowledged while the ledger is open and below `T` once it closed (LL-I6) | `atomic-learn-issued`: a fetcher may learn a tail up to the granted prefix |
| `ObjectLedger` `AcknowledgedSurvive`, `NoAcknowledgementAtOrAboveEnd`, `DecidedBelowEnd`, `EndSealed`, `SealsPermanent`, `RecoveriesAgree`, `NoRevokedAnswer`, `OpenCheckSound`, `CheckTailBelowEnd`, `BelowTailDecided`, `NoFalseCorruption`, `OnlySweeperDeletes`, `StoreBatchesIssued`, `AppendExtentShape`, `RefinesAtomicLedger` | Per-slot and append object-store ledger guarantees (OL-I2 to OL-I10, LL-I1 to LL-I9) and refinement of `AtomicLedger`: conditional placement, `[s, s + K)` and `(T, T + K)` per-slot fences, contiguous append extents with `ROLL`-before-create and tag seals, permanent `sealed`/`dropped` markers, SEAL objects, and non-appendable tombstones, checks ordered after bound capture, below-bound reads checking `dropped` on absent slots and SEALs (including late SEALs after a sweep), and reclamation of straggler batches on later sweeps | `seal-unconditional`: SEAL create overwrites DATA (`AcknowledgedSurvive`, `RefinesAtomicLedger` at `Close`); `check-without-marker`: recovery omits `sealed` marker (`OpenCheckSound`, `RefinesAtomicLedger` at `AnswerCheck`); `drop-without-marker`: sweeper deletes without creating `dropped` (`NoFalseCorruption`, `RecoveriesAgree`); `recover-from-gap`: recovery starts past an unobserved slot (`DecidedBelowEnd`, `NoFalseCorruption`); `drop-deletes-seal`: sweeper deletes SEAL objects (`NoAcknowledgementAtOrAboveEnd`, `RefinesAtomicLedger` at `Complete`); `read-ignores-drop`: read answers without checking `dropped` (`NoFalseCorruption`); `tombstone-appendable`: sweeper leaves the tombstoned extent appendable (`NoAcknowledgementAtOrAboveEnd`, `RefinesAtomicLedger` at `Complete`); `roll-before-written`: writer creates the next extent before its `ROLL` is written (`AppendExtentShape`, `NoAcknowledgementAtOrAboveEnd`, `DecidedBelowEnd`, `RefinesAtomicLedger` at `Close`) |

The regression `unsealed-negatives` reproduces Vanlightly's BookKeeper 4.13 defect: recovery counts an absent answer
from a replica it has not sealed, closes the ledger below an entry, and the writer then completes the entry's quorum on
that replica. Negated reachability properties require behaviors in which two recoveries compute different ends
(`NeverDivergentEnds`), the writer acknowledges after a replica answered `Sealed`
(`NeverAcknowledgementAfterSealedAnswer`), DATA lies at or above a closed ledger's end (`NeverDataAtOrAboveEnd`), a
reader decides after a node dropped its replica (`NeverCheckAfterDrop`), a request delayed past the drop creates a stray
replica on a node which held none while another holds the tombstone (`NeverStrayAfterDrop`), and that node's drop
records its tombstone in turn (`NeverStrayDroppedAgain`), a check reports the ledger open with a positive tail which
the close then records as `T` (`NeverOpenCheckAtEnd`), a recovery finds a position at its `c + 2K − 1` bound
(`NeverFoundAtBound`, at `K` of 1 and 2), a recovery decides its end with recovery writes outstanding
(`NeverCopyPendingAtEnd`), a ledger closes with an acknowledged entry after a node lost its disk
(`NeverClosedAfterNodeLoss`), a recovery closes after its recovery write replaced a corrupt value
(`NeverRepairedCorruption`), a writer's repeat meets its own landed write and answers `Written`
(`NeverRepeatedWritten`), a fetcher reads or drains below a learned tail while the ledger is open
(`NeverTakenWhileOpen`), a recovery which starts at the primary's explicit contiguous tail ends at its first read, as a
handoff's does (`NeverEndsAtExactTail`), a check's tail exceeds a losing recovery's candidate end
(`NeverCheckTailAboveCandidate`), a writer's repeat at `K = 2` carries a fresher tail than its first write to a replica
which holds the position (`NeverFresherRepeat`), a fetcher's read repair lands (`NeverRepairLanded`), a position read
while the ledger was open lies below the `T` its close records (`NeverServedOpenThenClosed`), a writer's send deferred
past a later position's acquisition (`NeverDeferredSend`), a recovery's read of a position sent after the replica's seal
answer while its earlier read is still in flight (`NeverMarkedReadWhileUnmarkedInFlight`), a seal answering a tail which
only a write's admission raised (`NeverSealTailFromAdmittedWrite`), a slot no write reached answering corrupt
(`NeverCorruptUnwritten`), a recovery closing with a position it found at or above its end on fewer than `Q` replicas
(`NeverClosedWithFewCopiesAboveEnd`), and the refinement's steps beyond `LedgerMap.qnt`'s occur: a close above the
acknowledged prefix (`NeverClosedAboveAcked`) and an acknowledgement after the close (`NeverAcknowledgedAfterClose`);
they show the bounded scenarios reach the overlapping operations the properties concern. `NeverOpenCheckAtEnd` records
whether the map was open when the check decided, so its counterexample is a check which reported the ledger open before
the close.

The `ObjectLedger` model adds 14 reachability witnesses: 10 per-slot (`NeverLateSealLanded`, `NeverReadSeesLateSeal`,
`NeverStragglerSwept`, `NeverRecoveryDropped`, `NeverBothRecoveriesFinish`, `NeverRecoveryLoops`,
`NeverAckAfterSealed`, `NeverClosedAboveAcked`, `NeverAcknowledgedAfterClose`, `NeverDataAboveEnd`) verifying that
delayed SEALs land after a sweep and are read as `Dropped`, straggler batches are swept again, concurrent recoveries
race with drops and loops, and the refinement exercises all `AtomicLedger` transitions, plus 4 append-layout witnesses
(`NeverRecoveryFollowedRoll`, `NeverRolledAndTombstoned`, `NeverCoalescedAppend`, and `NeverRecoveryDropped` under
`Layout = "append"`) verifying that recovery creates and seals a rolled-to extent, a rolled ledger is tombstoned with
its older extent deleted, multiple queued slots coalesce into one append, and a concurrent recovery meets a drop.

## Bounds

`test/Durable.Storage/eng/model-check-quint.ps1` runs the ledger protocol models in three tiers (`Quick`, `Default`, and `Extended`),
together with all mutants, reachability witnesses, the `unsealed-negatives` regression, the `amnesia` and
`tombstone-loss` assumptions, and the simulated `seal-short` mutant at `K = 2`. Quint compiles finite integer sets
without TLC symmetry reduction, so every state count below is unsymmetrized. The `Quick` tier (`just models-quick`,
and `just models-changed` inside `just check`) holds the fast exhaustive cases, every mutant, every assumption, and
every reachability witness. The `Default` tier (`just models`) adds `ledger-protocol-n3-q2-s1`,
`ledger-protocol-n2-q2-answers`, `ledger-protocol-n2-q1-answers-duplicates`, and the three weak-fairness liveness
checks (`ledger-protocol-liveness`, `ledger-protocol-liveness-concurrent`, and
`ledger-protocol-liveness-read-failures`). Measured `Quick` and `ObjectLedger` positive cases:

| Case | Tier | Configuration | Distinct states |
| --- | --- | --- | ---: |
| `quorum-coverage-4` | Quick | Quorum coverage over every cohort and quorum of four replicas | 768 |
| `atomic-ledger` | Quick | The ledger storage contract's model: two positions, two readers, a fetcher | 6,836 |
| `ledger-protocol-n2-q2-s1` | Quick | Two replicas, `Q = 2`, one recovery, one position, `K = 1` | 7,452 |
| `ledger-protocol-n2-q1-s1` | Quick | As above, `Q = 1` | 1,339 |
| `ledger-protocol-n3-q1-s1` | Quick | Three replicas, `Q = 1`, one recovery, one position, `K = 1` | 28,183 |
| `ledger-protocol-n2-q1-concurrent` | Quick | Two replicas, `Q = 1`, two concurrent recoveries, one position | 128,779 |
| `ledger-protocol-n2-q1-concurrent-reader` | Quick | Two replicas, `Q = 1`, two concurrent recoveries, a reader, one position | 3,177,554 |
| `ledger-protocol-n2-q1-concurrent-duplicates` | Quick | Two replicas, `Q = 1`, two concurrent recoveries, one position, one duplicated request | 1,725,715 |
| `ledger-protocol-n2-q2-reader` | Quick | Two replicas, `Q = 2`, one recovery, a reader, one position | 207,032 |
| `ledger-protocol-n2-q2-reader-tail-writes` | Quick | As above, with the primary's explicit contiguous-tail writes | 797,199 |
| `ledger-protocol-n2-q2-fetcher` | Quick | Two replicas, `Q = 2`, one recovery, a fetcher with read repair, tail writes | 153,181 |
| `ledger-protocol-n2-q2-fetcher-faults` | Quick | As above, with a lost node and corrupt values | 1,924,829 |
| `ledger-protocol-n2-q2-read-failures` | Quick | Two replicas, `Q = 2`, one recovery, one position, reads which fail | 7,859 |
| `ledger-protocol-n2-q2-k2` | Quick | Two replicas, `Q = 2`, one recovery, two positions, `K = 2`, deferred sends | 1,164,312 |
| `ledger-protocol-n2-q1-answers` | Quick | Two replicas, `Q = 1`, one recovery, one position, `SeparateAnswers` | 354,782 |
| `object-ledger-s1-k1` | Quick | Per-slot `ObjectLedger`: one slot, `K = 1`, one recovery, reader, fetcher, sweeper | 13,633 |
| `object-ledger-s2-k1` | Quick | Two slots, `K = 1`, one recovery, reader, fetcher, sweeper | 1,511,093 |
| `object-ledger-s2-k2-core` | Quick | Two slots, `K = 2`, one recovery, fetcher, sweeper | 214,955 |
| `object-ledger-s1-k1-concurrent` | Quick | One slot, `K = 1`, two concurrent recoveries, fetcher, sweeper | 61,010 |
| `object-ledger-s2-k2-concurrent` | Quick | Two slots, `K = 2`, two concurrent recoveries, sweeper | 223,589 |
| `object-ledger-append-s1-k1` | Quick | Append `ObjectLedger`: one slot, `K = 1`, one recovery, reader, fetcher, sweeper | 2,441 |
| `object-ledger-append-s2-k2-core` | Quick | Two slots, `K = 2`, one recovery, fetcher, sweeper | 19,969 |
| `object-ledger-append-s1-k1-concurrent` | Quick | One slot, `K = 1`, two concurrent recoveries, fetcher, sweeper | 5,941 |
| `object-ledger-append-s2-k2-concurrent` | Quick | Two slots, `K = 2`, two concurrent recoveries, sweeper | 30,520 |

`Extended` cases (`just models-extended`, `just quint-models-extended`) add larger replica cohorts (`N = 3, 4, 5` and
`quorum-coverage-5`), multi-slot and `K = 2` combinations (`Slots = 2, 3, 4`), concurrent readers and duplicates over
`N = 2, Q = 2` and `N = 3, Q = 2`, `SeparateAnswers` with duplicates and concurrency, the
`NeverCheckTailAboveCandidate` and `NeverFoundAtBound` (`K = 2`) witnesses, and the larger `ObjectLedger`
configurations (`object-ledger-s2-k2`, `object-ledger-append-s2-k2`, `object-ledger-s2-k2-concurrent-reader-fetcher`,
`object-ledger-s3-k2`, and `object-ledger-append-s3-k2`). Mutants and witnesses run on the smallest configuration which
reaches them. Apart from the one simulated `seal-short` mutant at `K = 2`, there are no depth cutoffs, sampling, or
state constraints that discard reachable states.

## Scope of the result

A successful run establishes the listed properties for every reachable state of each configured finite model,
subject to TLC's fingerprint collision probability. It supplies mutation-tested evidence for the protocol the
specification states, and trace validation shows that each recorded run of the implementation is one of its
behaviors: the quorum, crash, and `Ledger` simulations' runs, over their seed corpora by default, and the scripted run of
serve-time landings.

The model abstracts payloads into positions, since each position has one writer, and models one ledger: the
ledger map's transitions are [`LedgerMap.qnt`](../ledgers/README.md)'s, and a drop's interaction with
the LSM and reconciliation is [`LedgerDrop.qnt`](../ledgers/README.md)'s. `RefinesAtomicLedger` joins the
two: projected onto the map's status, the writer's granted and acknowledged prefixes, and the recorded end, every
behavior of this model is one of `AtomicLedger.qnt`, whose acknowledgement while open is `LedgerMap.qnt`'s write and
whose close is its `closing`; an acknowledgement after the close is a row `LedgerMap.qnt` acknowledged when it landed.
Projected also onto the seal, the readers' checks, and the fetchers' tails, reads, and drains, each behavior is one of
the ledger storage contract's model, so the readers' and fetchers' properties `AtomicLedger.qnt` establishes by its
guards hold of the quorum, and of every other implementation whose model refines it.
That the slots below `T` land, on `Q` replicas, is `ReadersAgree`'s. Replica failures are absences of
delivery; a replica's crash is its restart, after which DATA and seals survive and only the retained contiguous
tail is rebuilt; a lost disk or a corrupt value reaches at most `Q − 1` replicas; and requests are duplicated within
a budget of at most two. Liveness is checked for recovery only, over two
recoveries and one position. Fetchers read at most two positions: an open ledger's under the primary's
contiguous-tail writes, at `K` of 1 and 2, and a closed ledger's whose replicas hold corrupt values.
