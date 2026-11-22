# Verification

Every guarantee has an owner, an oracle, and evidence. This file is the testing methodology: the principles, the
test layers and when each runs, the oracles which decide each verdict, and the gates a change passes. The
[invariant traceability table](invariants.md) maps each invariant to its owning section, enforcement, and tests
and models. The [format registry](formats.md) maps each durable or wire format to exact-byte vectors or
descriptor tests. Those indexes distinguish intended guarantees from missing or partial evidence.
[Build and packaging](build-packaging.md) owns the build and artifact contracts.

## 1. Principles

- **Oracles come from the spec.** Each contract has one executable oracle, written once and reused at every
  layer: an Accordant spec for each stateful contract, invariant checkers for global properties, the fold of the
  history for every optimization, and per-journal linearizability for clients (§3).
- **The sequence is separate from the verdict.** Generators (Accordant's exploration, CsCheck, Clockwork workloads,
  hand-written scenarios) choose what to run; the oracle decides whether the observed results are allowed.
- **Everything nondeterministic replays.** Every randomized or simulated test reproduces from its seed, and a
  failing simulation leaves a Clockwork replay artifact in the results directory, which its minimizer can shrink (§7.4).
- **The tests are tested.** Each Quint invariant has a mutant which must fail (§5); production code is
  mutation-tested with Stryker.NET (§9); simulation oracles catch seeded known bugs (§9).
- **Cheap first.** Fast suites gate every change; exploration and full mutation runs gate milestones; soak runs gate
  only the finished system (§12).
- **The simplest sufficient technique.** Each property is established by the simplest technique which can
  establish it, with its oracle derived from the contract.
- **Evidence is planned with the component.** Each component's Verification section names its Accordant spec, its
  properties (including metamorphic ones), its simulation scenarios, its models and trace validation, the fuzz
  targets for its formats, and its snapshots.

## 2. Layers

| Layer | Establishes | Technique and tools | Runs |
| --- | --- | --- | --- |
| Formal models | Protocol safety and liveness in every reachable state of a finite model | Quint (TLC through Apalache), with a mutant per invariant (§5) | When a protocol changes; extended bounds at milestones |
| Unit | Pure logic and formats | xUnit v3 examples on Microsoft.Testing.Platform; golden vectors for every durable and wire format (§6) | Every change |
| Snapshot | Reviewed renderings of structured output stay as approved | Verify, pinned at 32.0.0 before its maintenance fee: decoded formats, ledger maps and manifests, diagnostics and log events, Accordant state graphs, deterministic simulation transcripts (§8) | Every change |
| Property | Algebraic laws and agreement with reference functions | CsCheck: range algebra, codec round-trip, canonicality and rejection, fold and compaction against reference folds, metamorphic relations (§4) | Every change |
| Contract conformance | Each component meets its stateful contract, including ambiguous outcomes and background work | Accordant specs as oracles (`Expect.OneOf` for ambiguity, step functions for background work, state profiles); sequences from Accordant's exhaustive small-depth generation and from CsCheck (long random sequences with shrinking); concurrent windows through `GenerateConcurrentTests` and `AllowsConcurrent` (§3.1, §4.1) | Every change |
| Deterministic simulation | The composed system under faults: every invariant at every step, acknowledged writes in the history, linearizable journals | Clockwork clusters in one process; fault injection; invariant checkers, the history oracle, Accordant over client sequences, per-journal linearizability with the Porcupine algorithm (§7) | A fixed seed corpus on every change; exploration and minimization at milestones |
| Integration | Real boundaries behave as their simulated doubles assume | Shared conformance suites over real Orleans grain calls, real WAL files (and io_uring on Linux), RocksDB, and cloud providers against emulators (§10) | Every change for local boundaries; emulators at milestones |
| End-to-end | The Orleans composition delivers the guarantees to grains | `Orleans.TestingHost` multi-silo clusters over real LedgerStores and real grain calls, with the history and linearizability oracles (§10) | Milestones, once the Orleans composition exists |
| Mutation | The suites above detect behavioral change | Stryker.NET with the Microsoft.Testing.Platform runner, over unit, property, and conformance tests (§9) | Incrementally (`just mutation-since`) for changed core units; full at milestones |
| Soak | Steady throughput and latency, and no leaks of memory, handles, disk, WAL retention, or object counts | Long multi-node runs with the oracles enabled (§10) | Final validation only |

Beside the layers, `Orleans.Durable.Storage.Architecture.Tests` enforces downward-only dependencies between components,
over project references and every compiled type's references, each component's internals, namespace boundaries, the
mutation units, and the separation of each layer's data plane from its `.Control` namespace, and BenchmarkDotNet
compares throughput, latency distributions, and allocations against explicit baselines (§12). Metamorphic properties
(§4.2), trace validation against the models (§5.1), coverage-guided fuzzing of decoders (§6.1), crash-state enumeration
(§7.5), and leak checks (§11.1) complete the methodology. §13 lists the parts not yet in code.

## 3. Oracles

An oracle is written once per contract and judges every sequence which reaches it: generated, shrunk,
hand-written, or observed in simulation and end-to-end runs.

### 3.1 Contract specs

One Accordant spec per stateful contract is its reference state machine: `ILedgerReplica` (registers, seals, recovery
writes, creation unless dropped, drops, contiguous tails), the LedgerStore across crashes, the ledger storage contract
as one ledger's processes use it through the shared front (`LedgerStorageSpec`, with its acquire operation: a slot
acquired once and appended at most once; and the quorum's recording oracle, `LedgerQuorumOracle`),
`IConditionalRegister`, `IObjectStore`, the ledger map and its committers, the manifest's commit rules and committers,
`IArchiveLogStore`, whose state is the reference against which the classifier of a failed condition is checked, and the
Journals. The Journals spec is the largest: admission by expected version, `ReplaceRequired`, `Moved`, and `Fenced`
without effect, and `Ambiguous` as `Expect.OneOf` of its possible effects. Its state is `JournalModelState`, the state
of `JournalModel` (§3.4), so one oracle judges both generated sequences and observed histories. An ambiguous outcome is
a `OneOf`; background work (drains, compactions, sweeps, a delayed landing) is a step function the spec may take between
operations; a state profile abstracts what the contract leaves open. The same spec validates generated sequences,
hand-written scenarios, and the client sequences observed in simulation.

`LedgerReplicaConformanceTests` (`Orleans.Durable.Storage.Engine.Ledgers.Tests`) checks every `ILedgerReplica`, and a
`Ledger` in the spec's composition role, against `LedgerReplicaSpec` ([ledger-replicas
§10](components/ledger-replicas.md#10-verification)); `LedgerConformanceTests`, the fold oracle, checks a `Ledger` and
its `LedgerWriter` over memory replicas, and over LedgerStore replicas, against `LedgerStorageSpec`, which judges
answers alone, with `LedgerQuorumOracle` (`test/Durable.Storage/shared/Conformance/Ledger`), from what
`CallRecordingLedgerReplica` (`test/Durable.Storage/shared/Recording`) records of each replica request, an invocation and a response on
one clock, each request its own client and a lost answer's response unbounded: every contiguous tail a request carries
is at most the completed prefix while the ledger is open and the recorded `T` after, a recovery raises the known
contiguous tail to at most the greater of its prior value and its `c`, which the harness takes from the recorded seal
answers, and the writer's lifetime stops before a recovery's first seal request ([ledger-protocol
§10](components/ledger-protocol.md#10-verification)); `MetaStoreModelTests` (`Orleans.Durable.Storage.Model.Tests`)
checks `InMemoryConditionalRegister` against `ConditionalRegisterSpec` (`test/Durable.Storage/shared/Conformance/MetaStore`), the spec
of `IConditionalRegister`, which also judges the file-system MetaStore's crash states (§7.5).
`ObjectStoreConformanceTests` (`Orleans.Durable.Storage.Engine.Archive.Lsm.Tests`) checks every `IObjectStore` provider,
with its `IAppendableObjectStore` where it has one, and the in-process fault double `FaultInjectingObjectStore`, against
`ObjectStoreSpec`
(`test/Durable.Storage/shared/Conformance/Archive/Lsm`), which also judges the file-system object store's crash states (§7.5), and
`CloudObjectStoreConformanceTests` (`Orleans.Durable.Storage.Cloud.Tests`) the cloud providers on their emulators (§10).
A write with an unknown outcome may land at any later point or never, so the spec keeps both possibilities for the rest
of the case, and the checker's search grows about tenfold for every two such writes; the fault double therefore throws a
bounded number of faults per case, which bounds the case's unknown writes and its search. Shared contract suites
(`ConditionalRegisterContract`, `ObjectStoreContract`) run each provider of a layer through the
same examples.

`JournalStoreConformanceTests` (`Orleans.Durable.Storage.Engine.Journals.Tests`) checks `JournalStore` over live ledgers
on memory replicas and `InMemoryArchiveStore` against `JournalStoreSpec` (`test/Durable.Storage/shared/Conformance/Journals`), the
Journals spec ([journals §10](components/journals.md#10-verification)). Its state holds each journal's
`JournalModelState`, with `Appends` and `BaseLength`, the facts the store's limits and a read's replace request judge,
and whether the process serves the journals' open ledger. Every answer of a write, a read, and the expiry scan's
`Expired`s goes through `JournalModel.Step`; the spec adds what the model leaves open and this composition decides: a
serving process answers a steady write `Accepted`, `Rejected`, or `ReplaceRequired`, and one serving none answers every
call `Moved`; the store's limits refuse an `Append` exactly at the generation's limit, and each answer carries the replace
request of journals §2.4. The environment's steps are spec operations: a takeover, which replaces the open ledger
through the process's executor; a write held at its load across a takeover, which is `Fenced`, or `Moved` while the
registry does not yet serve the successor's writer, or answered by the successor; and a write whose batch reaches its
replica as the process crashes, `Ambiguous`, an `Expect.OneOf` beside its answered outcomes whose entry is a pending step
function which lands once where its expected version still names the journal's state, or never, after which the next
process serves nothing until a takeover. Its state profile is strict about names: no ledger of the composition drops,
so no load rebases a key, and an answer names a state by one of its names, or by a fresh version only while an ambiguous
landing or an `Expired` left them unknown; a rejected write applies to no state. A write names its expected version by
how many versions back the caller learned it, from an acceptance, a rejection, or a read, so stale writes are drawn
without the state. CsCheck's sequences and concurrent groups and every sequence of three of nine calls run against it;
Accordant's generation, which runs the spec without a system, would reach only whether the process serves, since its
placeholder answers name no version. A mutant per contract clause, each a wrapper of the store's answers or effects or a
memory store whose limits differ from the spec's, fails the spec at the call which shows it: an `Append` accepted at a
stale version; `ReplaceRequired` where the store admits the `Append`, and an `Append` accepted past the limits; a `Moved`
or a `Fenced` entry which lands when sent again; an `Ambiguous` `Append` written again after the takeover; an `Accepted`
and a `Rejected` version which names no state; a read which misses an acknowledged entry or ends with an undecided one;
a deleted journal read as existing; and a `Replace` folded as an `Append`. `SimulatedJournalStoreConformanceTests`
(`Orleans.Durable.Storage.Simulation.Conformance.Tests`) runs the same spec over Clockwork-rewritten `JournalStore`
instances backed by `SimulatedConditionalRegister`, yielding memory replicas, and `SimulatedArchiveStore`, with explored
concurrent windows, seeded 3-replica quorum sequences, and admission race-mutant witnesses (§4.1). Drains, compactions,
and sweeps, which the memory composition does not run, are not yet in code (§13).

### 3.2 Invariant checkers

Global predicates are evaluated after every simulation step: open ranges partition the keyspace, ledger IDs
increase, closed ledgers were recovered, watermarks never decrease, referenced objects exist, the retirement floor
only grows, every watermark is a segment start, and the rest of
[LG-I1–I13](components/ledgers.md#5-invariants) and [LSM-I1–I7](components/archive-lsm.md#5-invariants). The **chain
checker** (`TokenChainChecker`, `test/Durable.Storage/shared/Archive`) replays `Plan` per key across segments and ledgers, through
splits and merges, from the archive's `prefix.Token` for the key's earliest write held in none of its commands, and
checks every stored token and every defining version of a write held in none against it, a write held in part at
`Plan` of its held part, and no write held after one not held whole ([HI-I4](components/archive.md#5-invariants),
[JS-I10](components/journals.md#5-invariants)); a count reset fails it, except a rebase, which follows a row of a
dropped ledger and takes as its base the token `Plan` gives after that row's write, other than the previous base, and
the restart from `Origin` of a key the LSM forgot after its `Expired`. Debug assertions pair it where admission writes
a defining row and where drains and loads read one. The **version checker** (`VersionChecker`,
`test/Durable.Storage/shared/Archive`) asserts that a key's versions never repeat, except along the `Origin`-based versions of a new
lifetime of a key the LSM has forgotten entirely ([journals §8.3](components/journals.md#83-warm-state)), and that
every version a read, an acceptance, a rejection, or a first touch answers names one state of its key, each state
identified by its newest row in the reference history; a rebased version's base must be the store's `VersionBase` at
its load (`Rebased`), so a rebase to it is fresh. The archive simulations run both after every step, over the
reads and drains since the last. The retirement
gate's checker records the epoch of each map value from which the gate answered a disk releasable, and asserts that
every later map value names the node in no ledger. A checker reads the state the system exposes (the ledger map, the
manifest, replicas, objects, and the gate's answers) and holds no model of its own. A map value records no install's
`ChosenAt`, so the commit-rule properties, not a checker, establish that no install chosen below the floor applies
([ledgers §10](components/ledgers.md#10-verification)).

### 3.3 The history fold

Journal state is a pure function of decided history ([overview §2.7](README.md#27-design-rationale)). The slow path
reads the ledger map, then the archive store, and folds the key's archive prefix and each live ledger's decided rows of
the key which the prefix does not hold, scanned from slot 0 with `Holds` marking the rows the prefix holds
([archive §8.1](components/archive.md#81-history-reads)), over the LSM or archive logs. The fold covers versions,
tokens, and content, and is compared with each prefix plus the live rows `Holds` keeps, over every store.
It is the reference implementation, and every optimization is checked against it: warm state and key indexes, absence
answers from `ArchiveReader.ReadStoreAsync`, single-replica reads below the contiguous tail, section metadata and block
caches, and the owner's reads ([JS-I6, JS-I9](components/journals.md#5-invariants)). An optimization is correct exactly
when it answers as the fold of the history at the same cut would, and every acknowledged entry is in the history.

The oracle runs in deterministic simulation over in-memory node replicas, conditional registers, and object
stores. Schedules mix writers and readers with takeovers, handoffs, splits, merges, rolls, drains, compactions,
sweeps, drops, and restarts, and inject unknown outcomes, delayed creates and landings, and stale caches. After
every step each optimized answer is compared with the fold, and the LG, HI, LSM or LOG, and JS invariants are checked.

### 3.4 Linearizability

Client histories (each operation's invocation, response, and real or virtual time) are checked per journal with
the Porcupine algorithm. Journals are independent, so a history partitions by key (P-compositionality) and each
partition is checked alone. Accordant covers small concurrent windows exhaustively (§4.1): Accordant chooses each
window's operations to a depth, the window explorer chooses their interleavings in the implementation within a bound,
and `AllowsConcurrent` judges each; the Porcupine checker covers long histories from simulation and end-to-end runs.

The checker is Porcupine's search ported to C# in `test/Durable.Storage/shared/Linearizability` (namespace
`Orleans.Durable.Storage.Testing`), with Porcupine's MIT license beside it:

| Part | Role |
| --- | --- |
| `HistoryRecorder` | Records each client's invocation and response with a `TimeProvider`'s timestamps, made strictly increasing in record order, so an operation which returned before another was invoked precedes it even when virtual time stood still; `CompleteUnbounded` records an outcome whose effect may land at any later time |
| `LinearizabilityModel` | A sequential specification: `Init`, `Step`, state equality and hashing, a partition comparer, and descriptions for the visualizer |
| `JournalModel` | One journal ([journals §2](components/journals.md#2-contract-upward)), with opaque versions: an `Append`, `Replace`, or `Delete` applies only at an expected version which names the current state, and every accepted entry gives the state a new name (JS-I1); a state may have two names, its version and the rebased version a read gives once every row of the key has dropped, and no version names two states ([JS-I10](components/journals.md#5-invariants)), which the model holds as a set of names per state; an entry is accepted only at an expected version the journal has held, on the premise that a writer writes only at a version an answer bound, an acceptance, a strict rejection, or a read, and reads before writing again after a deposed rejection, whose hint binds nothing; `Rejected`, `ReplaceRequired`, `Moved`, and `Fenced` have no effect (JS-I4); a rejection's `CurrentVersion`, and the state a `ReplaceRequired` is decided at, name the current state, relaxed only when the output carries the test-only `Deposed` flag, which the simulation sets when the answering owner's ledger was no longer listed open: a deposed rejection reports a hint which binds nothing (a version the journal held or a rebase no answer named; a rejection's version is a hint, [journals §2.2](components/journals.md#22-writes)), and a deposed `ReplaceRequired`'s expected version is one the journal held; and an older name of the state may be rejected once the key has rebased; once an `Expired` has made the journal absent, a forget step, the LSM forgetting the key, starts a new lifetime at `(Origin, 0)` whose names may repeat the earlier lifetime's; an `Expired` applies only to a deleted state at its expected version whose retention has passed, so `JournalModelTests` reject a stale `Expired` after a later `Append`; a rebase may give the state a fresh name only once the key has no live rows, which the model takes as an environment step; an answered read returns `Exists`, `CurrentVersion`, and the content, reflecting every write acknowledged before it began and none whose effect the history lacks (JS-I3); an `Ambiguous` write, recorded with an unbounded response, takes effect wherever it applies or, linearized after everything, never |
| `LinearizabilityChecker` | `Check` returns `Ok`, `Illegal`, or `Unknown` for a cancelled search (a timeout is a cancellation token); `CheckVerbose` also returns each partition's verdict and longest partial linearizations |
| `PorcupineVisualization` | Writes Porcupine's own page, stylesheet, and script with the history's data, the format its `Visualize` writes |
| `LinearizabilityAssert` | Fails a test whose history is not linearizable, or whose check is cancelled, naming the partitions which are not and their longest partial linearizations, and writes the visualization to the results directory (§11) |

Partitions are searched round robin in fixed turns on the calling thread, so a check is deterministic, and the first
illegal partition ends it, as Porcupine's parallel search does. Porcupine's published key-value histories with their
verdicts are vectors under `test/Durable.Storage/shared-vectors/linearizability`; its Jepsen etcd histories carry no license, so
generated etcd-shaped histories replace them. Properties check that histories generated from sequential executions
with random overlapping intervals are accepted, that an injected stale read or lost write is rejected, and that
partitioned, unpartitioned, and event-ordered checks agree (`Orleans.Durable.Storage.Testing.Tests`).

## 4. Properties and conformance

CsCheck generates inputs, shrinks a failure to a minimal case, and replays it from its seed. Properties state
algebraic laws and agreement with a reference function: the dyadic range algebra, codec round trips, canonical
encoding and the rejection of every non-canonical form, the fold and compaction against reference folds, and the union
laws, which `UnionLaws` (`test/Durable.Storage/shared/UnionLaws`) checks over an adapter for each hand-written union
([conventions §15](conventions.md#15-outcomes-unions-and-errors)).
`AdmissionGatePropertyTests` and the WAL and LedgerStore properties are existing examples.

### 4.1 The conformance kit

A shared kit (`ConformanceKit` in `test/Durable.Storage/shared/Conformance`) bridges CsCheck and Accordant. It generates operation
sequences, and concurrent groups of operations, from a spec's operations and their request generators; runs each against
a fresh system; folds the results through `spec.Allows` (or `AllowsConcurrent` for a concurrent group) over the state
profile; and lets CsCheck shrink a failing sequence. The kit then removes calls while the case still fails, and reports
the minimal trace, the spec's explanation, the possible states before the failing round, and the CsCheck seed which
replays the shrunk case through `DURABLE_STORAGE_TEST_SEED`, as a seeded simulation replays (§7.4). Each case carries a seed for
its system, such as the seed of random faults, which the report prints; an Accordant case's seed derives from its
description. Each case runs inside a scope its caller gives the kit; the `ILedgerReplica` spec's cases each run in their
own page leak scope (§11.1), so a case which leaks a page fails, is minimized, and reports its seed. Without a scheduler
a case runs on the calling thread and a concurrent group's calls on the thread pool, so the group's interleaving neither
replays nor shrinks.

A scheduler (`IConformanceScheduler`) runs each case and starts each call of its groups. `SimulatedConformance`
(`Orleans.Durable.Storage.Simulation.Conformance.Tests`) runs each case in a Clockwork simulation of its own
(`SeededSimulation.RunCase`, §7.4), seeded by the case's seed, and starts each call of a group as an operation of the
simulation's scheduler, so a case's operations, its faults, and its interleaving replay together from its seed, and
CsCheck shrinks them together. Each case is independent of every other: a fresh cluster, virtual clock, and page scope,
with every check of the driver. CsCheck and Accordant run in the test host, outside every simulation, and are excluded
from Clockwork's rewrite (§7.1); within a case a spec only computes. The `IObjectStore` spec over
`SimulatedObjectStore`, the `IArchiveLogStore` spec over `AzureArchiveLogStore` on the fake Azure transport and over
`ChunkArchiveLogStore` on `SimulatedObjectStore` (`SimulatedChunkArchiveLogStoreConformanceTests`), and the replica
role of the `ILedgerReplica` spec over the memory provider's replicas and over a LedgerStore across restarts
(`SimulatedLedgerReplicaConformanceTests`), its composition role over a `Ledger` of three memory replicas, healthy and
faulty (`SimulatedLedgerCompositionConformanceTests`), and the `LedgerStorageSpec` fold oracle over `LedgerSystem`
(`SimulatedLedgerConformanceTests`) run so, as do the `IArchiveLogStore` spec over `MemoryArchiveLogStore` and the
`IObjectStore` spec over `InMemoryObjectStore`, healthy and behind `FaultInjectingObjectStore`
(`SimulatedMemoryStoresConformanceTests`), and over `FileSystemObjectStore` (both `IObjectStore` and
`IAppendableObjectStore`) on the recorded file system, in memory, which yields to the scheduler at each read and write
(`RecordingFileSystem.YieldsAtIo`) so its I/O steps interleave, and which the store always takes, so no simulation
reaches the real disk (`SimulatedFileSystemObjectStoreConformanceTests`), and the `IConditionalRegister` spec over
`SimulatedConditionalRegister` with unknown outcomes and over `FileSystemMetaStore` on `RecordingFileSystem.YieldsAtIo`
(`SimulatedConditionalRegisterConformanceTests`). The ledger map committer's and manifest committer's generated
histories, which their history oracles judge rather than a spec, run each in a simulation of its own through the same
scheduler, which opens an explored window over each round of concurrent submissions
(`SimulatedLedgerMapCommitterConformanceTests`, `SimulatedManifestCommitterConformanceTests`), `GroupCommitter`'s
concurrent enqueue and merging, leader handoff, cancellation and disposal, and failure propagation run under
`WindowScheduleExplorer` (`SimulatedGroupCommitterTests`), and `JournalStoreSpec` runs over Clockwork-rewritten
`JournalStore` instances backed by `SimulatedConditionalRegister`, yielding memory replicas, and
`SimulatedArchiveStore` (`SimulatedJournalStoreConformanceTests`), covering warm and cold primaries, concurrent
appends, replaces, deletes, reads, cross-key batching, append limits, expiry scans, takeovers, fenced writes, and
crashing writes that leave `Ambiguous` outcomes. The ledger harnesses run on the virtual clock, which advances once
nothing else can run, so a held replica operation waits for its release or its hold limit and a read hedges without a
poller advancing a fake clock.

With a bound, the scheduler explores: it runs each case under every schedule of its concurrent windows within the bound,
as Stateright's and Coyote's model checkers explore an implementation's interleavings rather than a model's. A window
opens as a group's first call starts and closes once its last answers; a choice point is a step at which two or more
operations are runnable while a window is open. `WindowSchedulingStrategy`, a Clockwork scheduling strategy, takes its
choices from a prefix and then the default, the newest runnable operation, which runs each call on until it waits, so a
deviation is a preemption; outside every window the case's seeded strategy chooses. `WindowScheduleExplorer` enumerates
the schedules by re-execution, the fewest preemptions first: each schedule with `d` preemptions extends its parent with
`d − 1` by one preemption after its parent's last, so each runs once, and an exploration ends complete when a level adds
none, or at the bound. The gate's bound (`ScheduleBound.Gate`) runs every schedule with at most one preemption, up to
128 per case; `DURABLE_STORAGE_SCHEDULE_BOUND=deep` runs up to two, up to 2,000. Stateless exploration rests on a run's choice
points depending only on its earlier choices: a schedule whose prefix offers other operations than the run it extends
diverges, and fails its exploration. A failure reports the case, its seed, the schedule's index among those explored,
and its preemptions, which `DURABLE_STORAGE_SCHEDULE` replays with the case's seed; every exploration reports how many schedules it
ran, how many choice points its windows held, and whether it ran every schedule. Doubles at the store boundaries yield
to the scheduler where they would wait a random latency
(`SimulatedObjectStore.YieldsForLatency`, `SimulatedConditionalRegister.YieldsForLatency`, the fake Azure transport's
schedule), so each interleaving of a group's requests and answers is a scheduling choice rather than the order of
latency draws. A component of `src` whose operations complete at once yields through a conditional seam (§7.2),
such as the memory replica provider's `memory-replica-yields`. A LedgerStore runs over its simulated node's disk
(`LedgerStoreNode`), which completes each WAL write as an operation of its own; an explored case's disk completes each
after no latency, so the scheduler chooses when each write completes among the window's other steps. Its target makes
the spec's restart one atomic step: a restart waits for the calls in flight and holds back new ones until the store
serves again. A choice is a step's operation, so the interleavings are those of Clockwork's controlled scheduling, at
every `await`, yield, task start, lock, and timer.

Each explored contract carries a race mutant which only an interleaving reveals, and its test shows that the explored
schedules catch it while the default schedule does not: `SimulatedObjectStore.ChecksBeforeWritingMutant`, which checks a
conditional write's condition in one request and writes in a second (caught both under `ObjectStoreSpec` and under
`ChunkArchiveLogStore`'s `ArchiveLogStoreSpec`); `FileSystemObjectStore.SkipsReplaceLockMutant`, which performs a
conditional replace without holding the store's replace lock so two racing replaces on the same ETag both succeed;
`SimulatedConditionalRegister.ChecksBeforeWritingMutant`, which checks a conditional register write's version in one
step and writes in a second; `FakeAzureBlobService`'s `ChecksAppendConditionsBeforeWritingMutant`, which evaluates an
`Append Block`'s conditions before its schedule's wait and appends after it; the memory replica's
`write-checks-seal-before-landing` seam, a write which answers from a check made before a concurrent seal (LR-I8); over
a LedgerStore, a replica wrapper which makes the same check in a step before its write
(`ChecksSealBeforeWritingReplica`); under the composition and the fold oracle,
`AcknowledgesBeforeLandingReplica`, a replica which answers a writer write written and lands it a step later, so a
concurrent recovery's seal refuses an acknowledged write (LP-I3) or a concurrent read of the completed prefix finds it
missing (LP-I10); under the ledger map committer, `ChecksVersionBeforeWritingRegister`, a register writer which checks
the version in one step, writes in a later one, and answers applied from its check, so a change another committer's
write superseded between them is answered applied and never reaches the register; under the manifest committer,
`ChecksVersionBeforeReplacingStore`, a manifest store wrapper which checks the manifest's version in one step, replaces
in a later one, and answers applied from its earlier check, so a change another committer's replace superseded between
them is answered applied and never reaches the manifest; under `GroupCommitter`,
`GroupCommitter.RecordsSequenceAfterIoMutant`, which snapshots the submission sequence after a read or write completes
rather than before dispatching it, so a concurrent submission arriving during I/O is answered from a state that predates
it; and, under `JournalStore`, two `LedgerAdmission.RejectionMutant` races: `DecidesWhileLoading`, which decides waiting
entries while a cold key's load is still in flight (`KeyLoad.Loading`) rather than waiting for the loaded state, and
`RejectsWaiting`, which rejects a conflicting entry while an earlier write of the same key is still in flight (JS-I1).
The explored schedules catch each on every run, by one preemption; seeded
schedules and the thread pool catch them by chance (§12); the fold oracle also catches
`AcknowledgesBeforeLandingReplica` without concurrency, where a later round's read runs before the landing. The
committer's fence mutants (`LedgerMapCommitter.FenceMutant`) need a sequence of unknown outcomes and landings rather
than an interleaving, which the generator draws: the thread pool's histories catch `NoFence`, and only the scripted
schedule of `LedgerMapCommitterTests` catches `AnswerWhileHeld`. `WindowScheduleExplorerTests` qualify the explorer:
unbounded, it reaches each of the `C(a + b, a)` interleavings of two chains of yields and ends complete; a failing
schedule replays from its preemptions; and a run which depends on more than its choices fails as divergent.

A request runs only where the caller's precondition holds in every possible state, so generators draw requests
without knowing the state; the trace records each call it skips. Harness actions (fault injection, restarts,
refusals) change the system between operations without a spec operation, and calls the kit appends to every case
can end it with a recovery whose operations must succeed. A system may report events, such as the injected faults
which fired, which the trace records after each round. The kit runs Accordant's generated cases and hand-written
ones the same way: Accordant's exhaustive generation covers every sequence to a small depth, and CsCheck covers long
random sequences. One kit serves every contract in §3.1 and every provider of each; a contract's spec and its
provider-neutral executor are shared test source beside the kit (`test/Durable.Storage/shared/Conformance/LedgerReplica`,
`test/Durable.Storage/shared/Conformance/Ledger`), and each provider's target classifies the failures the contract leaves to it.

### 4.2 Metamorphic properties

A transformation which must not change an answer is checked by comparing the answers with and without it: any
compaction order or fan-out, a split followed by a merge, drains at different cuts, caches on or off, any
replica for a contiguous-tail read, and any division of a batch's bytes into pages. Each component adds its relations
with its implementation, as CsCheck properties.

## 5. Models

`test/Durable.Storage/eng/model-check-quint.ps1` type-checks every Quint model of the safety core and exhaustively checks their finite state
spaces through the Apalache/TLC backend ([ledger model](../../specs/ledger-protocol/README.md),
[Ledgers models](../../specs/ledgers/README.md), [LSM model](../../specs/archive/README.md),
[archive-log model](../../specs/archive-logs/README.md)):

| Model | Behavior | Invariants | Guarantees |
| --- | --- | --- | --- |
| `QuorumCoverageSealing.qnt` (over `QuorumCoverage.qnt`) | Exhaustive check over every cohort and quorum of replicas that the four definitions of quorum coverage agree; `off-by-one` mutant | `Equivalence` | LP-I5 |
| `AtomicLedger.qnt` | Abstract single-ledger storage contract (`AtomicLedgerContract` + `AtomicLedger`): atomic appends, close, seal, drop, reader tail checks, and fetcher tails, reads, and drains; mutants `atomic-late-open`, `atomic-check-issued`, `atomic-learn-issued` | `TypeOK`, `OpenCheckSound`, `CheckTailBelowEnd`, `BelowTailDecided` | LL-I5 to LL-I7 |
| `LedgerProtocol.qnt` | BookKeeper-style quorum ledger protocol, adapted from Jack Vanlightly's MIT-licensed specifications: a writer which never stops, `N` replicas with any quorum `Q`, writer window and contiguous tails, creation unless dropped, tombstones, restarts and volatile durability, lost disks and corrupt slots within a budget of `Q − 1` replicas, reads which fail, duplicates, separate answers, concurrent pipelined recovery, readers' tail checks (the seal check's counterpart of [#102](https://github.com/ReubenBond/clog/issues/102)), fetchers with read repair and drains, the primary's explicit contiguous-tail writes, step-by-step refinement of `AtomicLedger.qnt`, and weak-fairness liveness (`RecoveriesEnd`) | `TypeOK`, `AcknowledgedSurvive`, `NoAcknowledgementAtOrAboveEnd`, `EndSealed`, `TombstoneExcludesReplica`, `NoRevokedAnswer`, `ReadersAgree`, `ReadableBelowEnd`, `RecoveriesConverge`, `OpenCheckSound`, `ReadersSeeClose`, `CheckTailBelowEnd`, `BelowTailDecided`, `RecoveryBounded`, `RetainedBelowEnd`, `RefinesAtomicLedger`, `RecoveriesEnd` | LR-I2, LR-I3, LR-I5 to LR-I8, LP-I4 to LP-I12, LG-I8, LSM-I2, LS-I2, LS-I11 |
| `ObjectLedger.qnt` | Per-slot and append object-store ledgers over one strongly consistent store: single writer with window `K`, delayed and unknown conditional creates settled by occupant read or single-in-flight coalesced appends and `ROLL` frames across extents, concurrent recoveries placing conditional SEALs over `[s, s + K)` and `(T, T + K)` or sealing the newest extent and following `ROLL` frames, permanent `sealed` and `dropped` markers, SEALs, and tombstones, stale readers checking `sealed` and reading slots with drop-marker checks (including late SEALs delayed past a sweep), fetchers, the sweeper reclaiming straggler batches or deleting older extents, and step-by-step refinement of `AtomicLedger.qnt`; mutants `seal-unconditional`, `check-without-marker`, `drop-without-marker`, `recover-from-gap`, `drop-deletes-seal`, `read-ignores-drop`, `tombstone-appendable`, `roll-before-written` | `TypeOK`, `AcknowledgedSurvive`, `NoAcknowledgementAtOrAboveEnd`, `DecidedBelowEnd`, `EndSealed`, `SealsPermanent`, `RecoveriesAgree`, `NoRevokedAnswer`, `OpenCheckSound`, `CheckTailBelowEnd`, `BelowTailDecided`, `NoFalseCorruption`, `OnlySweeperDeletes`, `StoreBatchesIssued`, `RefinesAtomicLedger` | OL-I2 to OL-I10, LL-I1 to LL-I9 |
| `LedgerMap.qnt` | Ledgers over dyadic ranges; takeovers, splits, and merges; admission over the history by primaries of installed ledgers; drains, drops in lineage order, and readers; segments, trims at segment starts, and the token chain across ledgers | `OneOwner`, `HistoryOrdered`, `IdsDense`, `DropsInLineageOrder`, `AckedInHistory`, `ReadsSeeAcked`, `ReferencesResolve` (mutants `withdrawn-defining-batch`, `trim-mid-segment`; witness `NeverReferencingRow`), `TrimsAtSegmentStarts` (mutant `trim-mid-segment`), `TokensReplayPlan` (mutants `token-from-wrong-parent`, `token-includes-fenced`), over segments which may hold only a marker (witness `NeverMarker`) | LG-I1 to LG-I4, LG-I5 (appends only to an installed ledger), LG-I6, LG-I8, LG-I10, LG-I12, LSM-I3, LSM-I4, JS-I1 to JS-I3, JS-I10 |
| `Archive.qnt` | One key's history in a closed ledger and its open successor, each cut into segments; racing drain workers, which drain whole closed segments, and compaction workers, the sweeper, and readers | `ReferencedExist`, `FoldPreserved`, `WatermarksMonotone`, `WatermarksAtSegmentStarts` (mutant `lsm-mid-segment-watermark`), `ReadsCorrect` | LSM-I1, LSM-I2, LSM-I4 to LSM-I7 |
| `ArchiveLogs.qnt` | Per-segment writes of archive logs with base rows, racing and straggler drains, requests which land after their drainer crashed, unknown outcomes, progress markers, drops, the sweeper, readers on a stale map and at a lagging tail, and Journals' loader (a map read, then an archive read, then the rebase); each `Create`, `Append Block`, and `Put Blob` one action; mutants among them `404-as-held-without-properties-read`, `no-zero-pad`, `lagging-read-as-linearizable`, and `drain-ignores-lineage`; three drainers exhausted on every change without the sweeper and nightly with it | `NoLostCommand`, `StaleReaders`, `ReadsAtTheirCut`, `NoUndecidedCommand`, `HoldsSound`, `ReferencedExist`, `VersionsUnique`, `NoFalseCorruption` | LOG-I1 to LOG-I3, LOG-I6, HI-I1, HI-I4, JS-I10 |
| `LedgerDrop.qnt` | One final-window address of a closed ledger on its replicas; the deposed writer's delayed writes, recovery's seals and recovery writes, creation unless dropped, and drops which leave a tombstone; each node's reconciliation from its own view of the map, which may predate the ledgers' installation; a lower ledger's first write after a higher one's drop, and stray replicas on nodes which held none, dropped again; crashes between the two catalog writes of the drop-split mutants; and the writer's outcome | `DecisionsAgree`, `DurableIsDecided`, `FencedHasNoEffect`, `EndAddressSealed`, `NeverDropsListed`, `TombstoneExcludesReplica` | LG-I4, LG-I5, LG-I7, LR-I3, LR-I7, LR-I8, LP-I5, LP-I7, LS-I2, JS-I4 |

Quint is pinned in `test/Durable.Storage/eng/quint/package-lock.json`; the Apalache archive which carries the translation and TLC is
pinned and hash-verified. A positive case exhausts its state space. Each invariant has a guard-disabling mutant
which must produce that invariant's counterexample, checked separately so the failing property is identifiable. Each
answer a modeled protocol counts has a mutant which gives the answer before its record is durable
([durable answers](conventions.md#13-durable-answers)): the ledger model's `NoRevokedAnswer` holds every replica to
what it answered across restarts, and its volatile-answer mutants break it for each answer, a writer write's `Written`
and `Sealed`, a read's DATA, a recovery write's `Written`, a creation, a seal, a repeated seal, and a tail check's
sealed flag; `LedgerDrop.qnt`'s `drop-volatile` does the same for a drop.
CI gives each model its own job and retains logs and counterexamples. The models check the core before its
implementation exists; a modeled protocol change updates the model, its mutants, and the trace mapping (§5.1)
together.

### 5.1 Trace validation

Trace validation establishes that the code implements the modeled protocol. Random ITF traces from `LedgerMap.qnt`,
`Archive.qnt`, and the drains of `ArchiveLogs.qnt` are replayed against the implementation through a mapping
from the implementation's state to the model's, and traces recorded in deterministic simulation are checked against the
models' actions. A trace driver per model lives in the conformance kit (§4.1) and lands with each modeled component.

For the ledger model, simulation records a `LedgerTrace` (`test/Durable.Storage/Orleans.Durable.Storage.Simulation`). It logs each
replica event at the replica's node, in the order the node served it: a writer write, recovery write, seal, read, or
contiguous-tail read, with the call it answers, the client which sent it (the writer, a recovery executor, or a reader),
the address, the contiguous tail it carried, the recovery and seal flags, and the answer with its sealed flag and
contiguous tail. It logs each replica call and answer at the client, from a replica which wraps each replica a client's
`Ledger` uses, and marks a read sent after the recovery handled that replica's seal answer, the rule recovery applies. A
recovery reports each replica answer it handles, in the order it handles them, through the observer `Ledger`'s internal
`RecoverAsync` overload takes for that recovery, and the trace logs it. It logs the client steps the model names
(acquisition, completion, recovery's start and end with `c` and the candidate end, a contiguous-tail read's start and
answer, the map's close by a recovery's conditional write, a rival recovery's lost close, a client's learning of the
recorded `T`, a node's drop, and a restart) where they happen. C# oracles check each trace in the simulation: no replica
answers a writer write `Written` after it answered a seal (LR-I8); every reported contiguous tail is at most the
writer's completed prefix, or the recorded `T` once the map closes the ledger, and a replica's never decreases between
restarts (LR-I6, LP-I11); the writer never sees a recovery's candidate end answered `Written`, then or later, and every
end lies in `[c, c + 2K]` (LP-I7); no writer slot at or above a candidate end completes (LP-I8); a contiguous-tail read
begun after a recovery completed reports sealed (LP-I9); the map closes a ledger only at a candidate end of a completed
recovery (LG-I9), a second close names the same end, a close loses only once the map recorded one, and a client learns
only the recorded `T`; and a seal check in the writer's process which finds the ledger
sealed stops the writer's lifetime with `Sealing` before it answers ([ledgers
§8.3](components/ledgers.md#83-the-primarys-open-check)). A `LedgerWriter`'s steps come from the observer the trace
installs on its `Sequencer`: its acquisitions (`Acquired`), the completions it calls (`Completing`), which a writer
calls only once the slot's write reached `Q` `Written` answers, the completions which return (`Completed`), and its
stops (`Stopped`). A recovery executor's `Ledger` serves that recovery alone while it runs, and the recovery raises its
known contiguous tail to `c` and no further (LP-I11), so the trace logs the recovery's exact `c`. A primary which helps
recovers through its `LedgerWriter`, whose other operations can raise its `Ledger`'s known contiguous tail during the
recovery, to at most the recorded `T`, so the trace logs that tail before and after the recovery: the first bounds `c`
from below, and the second from above. The trace checks that the end lies in `[c, c + 2K]` for some `c` between them.
The trace is kept with the run's diagnostics.

Quint (`quint test`) then checks each trace against the ledger model ([trace
validation](../../specs/ledger-protocol/README.md#trace-validation)). With `DURABLE_STORAGE_LEDGER_TRACES` set, the quorum, crash,
and `Ledger` simulations, and the scripted run of serve-time landings, write each run's trace of its ledger as a Quint
module (`LedgerTraceExport`), and `LedgerProtocolTrace.qnt` drives `LedgerProtocol.qnt`, with `SeparateAnswers`, through
the module's events in order, binding each to the action and arguments it names. The trace validates when `quint test`
executes every event and its invariants without failure. A replica's answer is served by its node and received by its
client in separate steps: the writer receives its answers as they arrive, a recovery in the order it handles them, and
a check in any order. A retry maps to a repeat of
the same request, and a request the node serves twice to a delivery which leaves it in flight. A `LedgerWriter`'s
acquisition carries no contiguous tail the trace sees, so the export maps it at its slot's first write, which carries
one. Its `Completed` steps map to `Complete` through the slot, and its `Stopped` steps stutter: the model's writer never
stops, which only adds behaviors. Each trace maps one recovery's close: the quorum simulation's last recovery, the crash
simulation's final one, and the `Ledger` simulation's first executor to return, whose `close` maps to `Close` on the map
version its recovery read, at exactly the end it decided, once the writer's completed prefix reaches the prefix it had
acknowledged by then (a `LedgerWriter`'s logged completions, or the writes a quorum acknowledged with every lower one),
so `ackedAtClose` is at least the acknowledged prefix; the rival executor's close loses (`Close`'s superseded branch).
An executor which begins its recovery after the close read the map before it, so the export places its begin before
the close. A client which learns the recorded `T` (`ObserveRecordedTail`, with no I/O) is a fetcher which reads the map
(`ReadMap`), and each replica its later requests reach retains `T` as it admits one (`LearnTail`); its writer's
repeated writes and its checks, which carry `T` past the completed prefix, stand for the model's, which carry the
prefix. A recovery executor's reads outside its recoveries are a reader's; a primary's recovery begins with the tail
its seals carry; and the writer's own checks report its ledger's known contiguous tail, which its completions and the
`T` it learned raise meanwhile. The writer's explicit contiguous-tail write while idle, its contiguous-tail reads outside
any check, maps to the model's `WriteTail` at its first request, with `TailWrites` set, carrying the writer's completed
prefix, and each node's serving of one to its delivery, whose answer the writer ignores, as the model's does. Between
events the trace takes the model's steps the implementation takes without
logging them, each named with the guarantee which makes it real: acquiring the positions a later acquisition passed,
completing positions to the tail the next event carries, admitting a write or a check before a seal or check whose
tail it raised, or a write before a corrupt answer from a node whose replica it created, a slot failing its integrity
check before its node answers it corrupt, the requests in flight to a node whose records reached its disk before it
crashed, without an answer (a writer write, a recovery write, or a seal), at the first event which shows them: the
node's restart, or an answer the crashed process gave, a read which returned their DATA or an answer which reported the
seal, and, at a restart, a creation which landed without its write and the faults the node's recovery found. The export
names each such request in the event which shows it, so the restart's check, or the answer's, decides whether it
landed, and no other event delivers one. Each simulated node logs every media damage
it suffers, as it happens, as a `damage` event of its own, which names the node and no ledger and stutters, since
damage changes nothing the model holds until a read or a recovery finds it; a slot turns corrupt, at a corrupt answer
or a restart's faults, and a write is admitted before a corrupt answer, only at a node an earlier `damage` event named,
so a corruption at an undamaged node fails the trace. A restart
logs what the node's recovery established, whether it holds the replica, its seal, and the slots which hold DATA and
which are faulty, and the model's replica must match it, every value it holds DATA or faulty, so a restart which loses a
durable answer, or reports damage as missing, fails the trace. The restart reads each unloaded slot-table leaf into a
scratch copy, so a traced node loads the leaves an untraced one would. A node logs its answers in the order its lane
completes them, so the export orders a seal before the `Sealed` answers it caused, and a write before a read which
returned its
DATA, within one process of the node; an answer logged before a crash keeps its place, and a restart which recovers the
replica sealed counts as its seal. The quorum and crash simulations crash a node while it serves a recovery's seal and
another while the recovery runs (§7.1), so their traces hold seals and recovery writes which landed without an answer.
Those crashes rarely leave the crashed process time to serve another request first, so a scripted run
(`ServedLandingSimulationTests`, at its own seed) fixes the order: one node holds its answers to a writer write, a
recovery's seal, and the recovery's copies once their records are durable (`ReplicaServing.HoldAnswer`), returns the
write's and the copy's DATA to reads and answers a writer write `Sealed`, and then crashes, which discards the held
answers, and restarts. Its test requires the rendered trace to name the write, the copy, and the seal at those serves, so
every run of `just ledger-traces` takes each serve-time `DeliverWrite`, `DeliverCopy`, and `DeliverSeal`. A reader's read
repair is its map view's fetcher's: the read of its preferred replica which lacked the position, below the `T` the
reader learned, is the model's `ReadBelowTail`, which reads the position from another replica and sends the repair in
one step, so the repair's own request is a copy already in flight, whose serve is its `DeliverCopy`. The randomized
corpora's readers prefer no replica, so a scripted run (`ReadRepairSimulationTests`, at its own seed) writes and closes
the ledger while one node is partitioned, and a reader on that node, which learned `T`, reads a position below it and
repairs its replica; its test requires the rendered trace to name the repair, and the trace mutant
`mutant-eager-repair-tail`, a repair carrying one past the fetcher's tail, is rejected at the repair's request. A
reader's learn of the tail from its own replica, a contiguous-tail read outside any check by a client other than the
writer, is its map view's fetcher's `LearnTail`, whose answer must be the replica's retained tail. The randomized
corpora's readers learn no tail from one replica, so a scripted run (`LearnTailSimulationTests`, at its own seed) has a
`LedgerWriter` append three batches and write its tail explicitly once idle, and a reader on a replica's node learn that
tail from its own replica and read below it; its test requires the rendered trace to name the learn, and the trace
mutant `mutant-eager-learn`, a learn one past the replica's retained tail, is rejected at its event.
Every state of each trace's
search is checked against `TypeOK`, `NoRevokedAnswer`, `ReadersSeeClose`, `RecoveryBounded`, and `OpenCheckSound`, and
from the close on against `AcknowledgedSurvive`, `NoAcknowledgementAtOrAboveEnd`, `EndSealed`, `CheckTailBelowEnd`,
`RetainedBelowEnd`, and `RecoveriesConverge`, so each trace is a bounded check of them at the implementation's scale.
`test/Durable.Storage/eng/model-check-quint.ps1 -Traces` validates every trace with `quint test`, then nineteen trace mutants, each one changed
event of a validated trace which the model rejects at that event: a recovery's end moved by one, a close at an end one
past its recovery's, a write's `Written` answer reported `Sealed`, a revoked DATA answer and a revoked seal after a
restart, a `Missing` read counted as sent after its seal answer, a seal's tail raised above every carried tail, an
explicit contiguous-tail write, a read repair, and a learn each one past the tail its evidence allows, a `Q`-th replica
answering corrupt, a read of DATA from a node no damage event named answered corrupt while the fault budget still
admits a corruption, and a restart which leaves out a position its node answered, reports DATA no client wrote, reports
damage as missing, loses a seal an answer reported, or reports a seal no landed seal explains, and a crashed process's
`Sealed` answer or read of DATA whose seal, write, or copy that landed without an answer is no longer named.
Every crash trace holds the event the damage mutant changes: once the faults end, a scripted step damages the WAL page
of a slot on the node whose media the faults damage, the node answers the slot corrupt, and the node restarts holding
it.
`just ledger-traces` runs the simulations and the check, over a seed range when one is given.

For archive logs, `ArchiveLogTraceTests` (`Orleans.Durable.Storage.Simulation.Tests`) records the drain simulation as a
run of the archive-log model ([archive logs §10](components/archive-logs.md#10-verification)). One key's history over a
closed ledger and its open successor, whose decided entries and segment starts become the model's constants, is drained
by two drainers which race, crash, and restart, and swept, over the memory storage behind `SimulatedArchiveLogStore`,
whose failures strike before a request lands. Each request of the key's log maps to the model's action for it, a
drainer's `Create` and `Put Blob` of an opener among them; a segment's start, a marker, and a drop map to theirs; the
ledger writer's decisions give the model's decided tail, and `ReplaceAsync` its close; a request for another key and a
repeat of a request which already landed in the model map to no step. After each store request the trace expects the
model's position to be the store's, and a drain which blocks is a violation. With `DURABLE_STORAGE_ARCHIVE_LOG_TRACES` set, each
seed writes a Quint module (`ArchiveLogsTrace{seed}.qnt`) which imports the model with those constants and runs its
actions in order. `test/Durable.Storage/eng/model-check-quint.ps1 -Traces` replays each with `quint test`, which fails at an action not
enabled, a position the store did not reach, or a state which breaks an invariant, and checks two negative controls
fail: a copy which repeats a segment's start, which the model does not enable, and a trace a `marks-unheld` drain
records while every create of the key fails (`ArchiveLogsTraceUnheld{seed}.qnt`), whose marker the model refuses.
`just archive-log-traces` runs the simulation and the check, over a seed range when one is given, and CI's
`archive-log-traces` job runs it on every change.

For the ledger map, a `LedgerMapTrace` (`test/Durable.Storage/Orleans.Durable.Storage.Simulation`) records every value the ledger-map
register applied, in order, interleaved with the proposals answered durable, and each ledger's decided batches, in the
ledger map committer simulation (`LedgerMapCommitterSimulationTests`), whose ledgers take no writes, and the transition
simulation (`TransitionSimulationTests`), whose scans read each closed ledger's batches below its recorded `T` for its
LG-I5 check and, only when the trace is exported and after every check, each open ledger's below its highest durable
answer. Recording takes no step, so an untraced run's schedule is unchanged. With `DURABLE_STORAGE_LEDGER_MAP_TRACES` set, each
seed writes a Quint module (`LedgerMapTraceCommitter{seed}.qnt` or `LedgerMapTraceTransition{seed}.qnt`, rendered by
`LedgerMapTraceExport`) which imports `LedgerMap.qnt` with the constants its run fixes. A write's value becomes the
model's conditional steps it shows against the value before it, a group commit several: its installs (`takeover`,
`split`, or `merge`) in the order of the IDs they open, which is the order the committer's fold applied them, then its
drops in ID order; the bootstrap is the model's `init`. A decided batch becomes `write` or `marker` at its slot, applied
no later than its durable answer and before the step which closes its ledger, since an atomic append lands between its
call and its answer, before the seal. The run checks every invariant after each step, the model's map, ranges, status,
`next_ledger_id`, and, with writes, each closed ledger's tail against each written value, and each batch's slot, key,
and row form against the model's. The model omits node identities, replicas, primaries, and the retirement floor
(LG-I11), so a floor raise is no step; a range or key keeps its top `KeyBits` bits; bodies, JSNs, and tokens are not
compared; a drop of a ledger with batches follows the model's drain to its `T`, the Archive's completion the
simulations do not run; and trims and reads are not traced ([ledgers models](../../specs/ledgers/README.md#trace-validation)).
`test/Durable.Storage/eng/model-check-quint.ps1 -Traces` replays each module with `quint test`, then eight trace mutants, each a change of
one step of a validated trace which the model refuses at that step, by the step's action guard or by the state it
expects, while the replay through the step before passes: a takeover's closed ledger listed open (`OneOwner`), an ID
skipped (`IdsDense`) or reused (`HistoryOrdered`), a write before its ledger's install (`AckedInHistory`) or after its
close (`HistoryOrdered`), a drop without its drain (`AckedInHistory`) or before a lower overlapping ledger's
(`DropsInLineageOrder`), and a referencing row read as defining (`ReferencesResolve`). `just ledger-map-traces` runs
the simulations and the check over seeds 1 to 10, or a seed range when one is given, and CI's `ledger-map-traces` job
runs it on every change.

For the LSM, `ArchiveTraceTests` (`Orleans.Durable.Storage.Simulation.Tests`) records an LSM simulation as a run of the
LSM model ([archive LSM §10](components/archive-lsm.md#10-verification)). One key's history beside another's, over a
closed ledger and its open successor, whose decided entries and segment starts become the model's constants, is drained
by two drainers which race, crash, and restart, compacted by two compactors which race to push and merge, committed
through two manifest committers, swept by a sweeper which restarts, dropped, and read through the history reader, over
the kit's `SimulatedObjectStore`, which fails requests, leaves outcomes unknown, and lands writes late; once the
successor closes, the faults stop, both ledgers drain, drop, and compact, and the sweeper sweeps until it deletes what it
marked. The store reports each request's effect at the instant it takes effect (`SimulatedObjectStore.Effects`), and the
trace maps it onto the model's action: a drain output's create from its ledger's watermark to `drainUpload`, a
compaction output's create whose inputs the manifest lists to `compactKeep`, with the key's rows the output holds, a
manifest replace to the drains' `drainCommit`s, a ledger at a time from its watermark, and the compactions'
`compactCommit`s it applied, a drain of a segment with no row and a compaction which kept none to the model's upload of
an empty object and its commit, the sweeper's listing, manifest read, deletes, and end to its four steps, and a history
read, at its end, to the model's reader with the successor read to the tail the read reached; the writer's decisions,
and the drains and reads which show them, give the model's decided tail, `ReplaceAsync` its close, and the dropper's
drops, or a read whose map no longer lists a ledger, the map's drops. An output which can never be committed when it
lands, a drain's from a start the watermark passed or a compaction's whose input left the manifest, maps to no step, as
its delete and the commit which finds it superseded or its goal held do; so does an output of a partition which does not
contain the key, and every other read. `compactKeep` is the model's compaction with the rows the implementation kept,
at least the reduction's: a compactor which judged by an older manifest value, or by the implementation's coarser rule,
which keeps an empty `Delete` while an older ledger is incomplete, keeps rows the reduction would drop, and the
invariants judge what it kept; the model's exhaustive cases take such compactions as steps too. With
`DURABLE_STORAGE_ARCHIVE_TRACES` set, each seed writes a Quint module (`ArchiveTrace{seed}.qnt`)
which imports the model with those constants and runs its actions in order, expecting after each step the model's
invariants, after each upload the object's rows to be the output's, after each manifest replace the model's watermarks
and references to be the manifest's, and of each read the answer, folded from its prefix and its live rows, to be the
fold of the decided history at its cut. `test/Durable.Storage/eng/model-check-quint.ps1 -Traces` replays each module with `quint test`, then
five trace mutants, each a change of one step of the first validated trace which holds what it looks for, which the
model refuses at that step, by the step's action guard or by the state it expects, while the replay through the step
before passes: a sweep's delete of an object the manifest references (`ReferencedExist`), a compaction output without its
inputs' newest row (`FoldPreserved`), a manifest whose watermark falls below the one before (`WatermarksMonotone`), a
drain which ends inside its segment (`WatermarksAtSegmentStarts`), and a read which misses its
newest row (`ReadsCorrect`). `just archive-traces` runs the simulation and the check over seeds 1 to 20, or a seed range
when one is given, and CI's `archive-traces` job runs it on every change.

## 6. Formats

Golden vectors in `test/Durable.Storage/shared-vectors` encode each owning component's authoritative layouts, including
rejection cases. For formats with an encoder, encoding the structured value yields exactly the vector bytes and
decoding yields the structured value. Canonical formats' rejection evidence is stated by their owner;
slot-index leaves have a canonical encoder and a validating decoder which accepts some alternate encodings.
Formats without a Clog encoder use their descriptor or backend contract as the relevant evidence. A format gains its
vectors with its codec, whose byte layout its owning section may give earlier
([formats §7](formats.md#7-rules-for-format-changes)). A change updates codec, callers, vectors, and owning spec
sections together.

### 6.1 Decoder fuzzing

Coverage-guided fuzzing establishes that every durable and wire decoder rejects or canonically accepts any bytes,
without crashing or hanging: WAL pages and records, the LedgerStore catalog and checkpoints, the ledger map,
batches and segments, the manifest, run objects, entry records, and archive-log tokens and metadata. Each format gains
its target with its decoder, and campaigns run at milestones.

**Targets.** Each layer's targets live beside its code's layer: `test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.Wal`,
`test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.MetaStore`, `test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.Ledgers`,
`test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.Archive`, `test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.Archive.Logs`, and
`test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.Journals` each use the types of the components whose formats they decode (their
layer's, or for the MetaStore's register file the `Orleans.Durable.Storage.FileSystem` provider package, and for the
archive-log targets `Orleans.Durable.Storage.Engine.Archive.Logs` and, for the Azure metadata reader,
`Orleans.Durable.Storage.AzureStorage`), of what those reach, and of the oracle framework
`test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.Abstractions` only, which `Orleans.Durable.Storage.Architecture.Tests` checks over
each target assembly's compiled references; the executable `test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz` gathers every layer's
targets for campaigns. Each target covers one decoder with one oracle, which applies the format's rules to the input
bytes independently of the decoder. The decoder rejects an input exactly when the input breaks a rule its format
documents, naming that rule's reason code, which the oracle loads from the format's rejection vectors, and the oracle
checks that the input breaks the rule named; otherwise the decoder accepts the input with the value the rules give. A
canonical format's value then encodes back to exactly the input; a structurally validated format's value decodes to the
same value from the canonical encoding. A structural decoder over a tree, such as checkpoint loading, is also held to
completeness: the oracle walks the input itself and requires exactly the objects its walk reaches, so a decoder which
drops or misplaces an object breaks the oracle even when its writer round trip holds. Any other exception is a finding.

Each target decodes its seeds once, unmeasured, before its first input, so one-time costs (static tables and buffer
pool slabs) stay outside every measurement. A decoding then allocates at most 64 KiB plus 64 bytes per byte of the
objects it decodes, measured per object (a checkpoint leaf by its reference's length), so no length field drives an
allocation beyond the bytes which carry it. The decoding thread's allocations are always measured; a decoder which
continues on other threads is measured process-wide, which the harness checks in its own process, where nothing
else runs. A campaign reports an input which runs longer than its timeout (10 seconds by default) as a hang, and a
replayed input has the same deadline and fails by its file name. A target whose format carries checksums also
decodes the input with its checksums recomputed, so the fuzzer reaches the rules behind them.

| Target | Decoder | Input | Oracle |
| --- | --- | --- | --- |
| `wal-header` | `WalFormat.NamedPageSize` and `DecodeHeader`, as the WAL opens a file | The start of a log file | The page size the fixed fields name, and the header page, as much of it as the input holds, rejected with the reason of the first rule it breaks, or the fields the rules give, which `WriteHeader` writes as the header page |
| `wal-page` | `WalFormat.ValidatePage` | One data page, zero-filled to the next page size | The reason of the first rule the page breaks, or the hole page or frames the rules give, which encode to the page |
| `wal-scan` | `WriteAheadLog.OpenAsync`, `ReadAllAsync`, and `ReadAsync` over `SimulatedWalStorage` | One log file: a header page and data pages | The file recovery's rules leave; the records the rules give, each a Full frame or a First fragment and the Continuations which finish it; each maximal run of hole pages as one `WalTorn`, each maximal run of invalid pages as one `WalCorruption`, and each valid page breaking the frame rules as a `WalCorruption` of its own; each record read at its position as the scan yields it, and no record read at a First frame whose record the rules abandon |
| `metastore-register` | `FileSystemConditionalRegister.Decode` | A `CLFM` register file | The reason of the first rule the file breaks (short, another magic, or another format version), or the version and value which encode to the file |
| `ledgerstore-catalog` | `ReplicaCatalog.Decode` | A `CLSC` catalog checkpoint | The reason of the first rule of ledgerstore §7.2 the input breaks, or the catalog which encodes to the input |
| `ledgerstore-record` | `LedgerRecords.Classify` and `DecodeStore` | The next handle (`u32`), then a WAL record | Damage for a short record, an empty slot record, or an unallocated handle; a damaged store record rejected with the reason of the first rule of ledgerstore §7.3 it breaks; or the key and value, or the create, seal, or drop, which lay out the record |
| `ledgerstore-leaf` | `FrozenLeaf.Decode` | A slot-index leaf | The reason of the first rule of ledgerstore §7.4 the leaf breaks, or entries which random access and full decoding agree on and which the canonical encoder preserves |
| `ledgerstore-root` | `CheckpointRoot.Decode` | A root slot, zero-filled to the next slot size | The reason of the first rule of ledgerstore §7.4 the slot breaks, or the root which writes the slot |
| `ledgerstore-generation` | `SlotCheckpoints.OpenAsync` and `ReadObjectAsync` over `SimulatedWalStorage` | A root slot's 80 bytes of fields; then file A's length (`u32`), file A in the root reference's generation, and file B, the rest, in another generation | No usable root exactly when the root or its tree breaks a rule of ledgerstore §7.4 and §8.8, a usable root's tree rejected for the reason of the first rule the walk finds, one `tree-rejections.json` names, or exactly the leaves an independent walk of the tree reaches, with damage reported exactly when a rule is broken or a generation is out of range; pairwise disjoint byte ranges read while loading, so no node is read twice and no read covers bytes another object owns; each leaf read as its bytes or failing its CRC; and leaves the checkpoint writer preserves |
| `ledgers-batch` | `BatchView.Decode`, and a walk of every row it lends | A batch, decoded from one page and from pages of 7 to 63 bytes | The reason `BatchRules`, an independent reading of ledgers §7, gives for the first rule the batch breaks, one of the batch rejection vectors' reasons; or the rows the rules give, which `BatchView.Encode` writes as the input |
| `ledgers-segment` | `LedgerSegmentView.Advance` over `BatchView.Decode`, in slot order, and a rewind from any slot | A batch count (`u16`, at most 1,024), then that many batches at slots 0 to `n − 1`, each framed by its `u32` length, each CRC recomputed, decoded contiguous and from pages of 7 to 63 bytes; an input whose frames are short of their count or lengths is outside the domain | The reason `LedgerSegmentRules`, an independent reading of ledgers §7's batch and then segment rules, gives for the first failure; or the resolved rows, versions, and tokens the rules give, which `LedgerBatch` re-encodes to the exact input, and whose suffix a decoding after a rewind from any slot gives |
| `ledgers-map` | `LedgerMapValue.Decode` | A ledger map value, decoded from contiguous bytes and from pages of 7 to 63 bytes | The reason `LedgerMapFormatRules`, an independent reading of ledgers §7, gives for the first rule the value breaks, one of the map rejection vectors' reasons; or the value the rules give, which encodes as the input |
| `archive-manifest` | `ManifestFormat.Decode` | A manifest | The reason `ManifestRules`, an independent reading of archive §7, gives for the first rule the manifest breaks, one of the manifest rejection vectors' reasons; or the value the rules give, which `ManifestFormat.Encode` writes as the input |
| `archive-store-record` | `ArchiveStoreRecord.Decode` | A store's record | The reason `StoreRecordRules`, an independent reading of archive §7 over the bytes rather than decoded text, gives for the first rule the record breaks, one of the store record rejection vectors' reasons; or the record the rules give, its ID, layout, store fields, and caller parameters, whose encoding decodes back to it. A record's fields are maps, so it is not canonical, and the vectors check the writer's bytes |
| `archive-run-object` | `RunObjectFormat.Decode` | A whole run object | The reason `RunObjectRules`, an independent reading of archive §7, gives for the first rule the object breaks, one of the run object rejection vectors' reasons; or the object the rules give: its condition, and each section's trailer entry, block index, filter, and rows; the trailer and tail decoders agree with the whole decode, every key reads back by itself, and the reads of each section's ranges one to four bits below its partition, as a push reads its children, give the section's rows, each only rows of its range; a rejected object whose tail decodes is rejected too by its sections' halves' reads and totals. Run objects are not canonical, so the writer's round trip is the vectors' and properties' to check |
| `journals-entry` | `JournalEntry.Decode`, and `JournalEntryRecord.Decode` as a forwarded record arrives | A row key and an entry record, framed as the key's length (`u16`), the key, and the record, since the record's CRC-32C covers its key; the record decoded under the key from contiguous bytes, from segments of 1 to 29 bytes, and, when the key is a journal's row key, as a forwarded record; an input which frames no key is outside the domain | The reason `EntryRules`, an independent reading of journals §7, gives for the first rule the record breaks, one of the entry rejection vectors' reasons; or the fields the rules give, which the codec's factories encode as the input |
| `journals-version` | `JournalVersion.TryParse` and `Parse`, as a grain's ETag arrives from Orleans | A version's text, the input's UTF-8 | `VersionRules`, an independent reading of journals §7's text form (base64url without padding, unused bits zero, of a base length of at most 32, the base, and a shortest varint count which ends the bytes), accepts the text or not; the parser accepts exactly those texts, as the version the rules give, which formats back to the text, allocating nothing, and `Parse` refuses every other with `FormatException` |
| `orleans-registry` | `NodeRegistryCodec.Decode` | A `CLNR` value, decoded from contiguous bytes and from segments of 3 to 13 bytes | The reason `NodeRegistryFormatRules`, an independent reading of orleans §7.3 which accepts the canonical body directly rather than by re-encoding it, gives for the first rule the value breaks, one of the registry rejection vectors' reasons; or the value the rules give, which `NodeRegistryCodec.Encode` writes as the input |
| `orleans-journal-write`, `orleans-journal-read`, `orleans-journal-page` | The journal node grain's answers' converters (`JournalForwardedWriteOutcomeConverter`, `JournalForwardedReadConverter`, `JournalForwardedPageConverter`), after Orleans' codec reads the input into the answer's surrogate | A grain reply's encoding of the answer, its surrogate's wire; one Orleans' codec refuses is outside the domain, since Orleans owns that framing | The converter refuses the surrogate with `InvalidDataException`, releasing the bytes it carried, or accepts a value whose encoding is that same surrogate, each value having exactly one accepted encoding (orleans §7.2) |
| `orleans-replica-write`, `orleans-replica-read`, `orleans-replica-tail` | The replica node grain's answers' converters (`RemoteReplicaWriteOutcomeConverter`, `RemoteReplicaReadConverter`, `RemoteReplicaTailReadConverter`), after Orleans' codec reads the input into the answer's surrogate, and then the caller's reply checks (`LedgerReplicaCallResults`) | A grain reply's encoding of the answer, its surrogate's wire; one Orleans' codec refuses is outside the domain | An independent reading of orleans §7.2's replica answers (each answer's tags, a refusal's kind among 1 to 4 and its message, a corruption's message, a too-large answer's positive length, and each case's own fields, DATA bytes only on DATA) refuses the surrogate, which the converter then refuses with `ReplicaReplyDecodingException`, releasing the bytes it carried, or gives the value the converter accepts, whose encoding is that same surrogate, each value having exactly one accepted encoding; the reply checks then answer it as §5.12's table gives, for a writer and a recovery write, a whole read which reserved 8 bytes, and a tail read which sent 5, or throw the exception the table names, releasing the DATA's received bytes |
| `archive-log-token` | `ArchiveLogTokens.Decode`, `LogArchiveStore`'s token decoder | A token's bytes | The reason an independent reading of archive logs §7.2 gives for the first rule the token breaks, one of `archive-logs/tokens.json`'s rejection reasons, raised as `ArchiveCorruptException`; or the position `(g, r, P, b)` the rules give, which encodes to the input |
| `archive-log-chunk-base` | `ArchiveLogChunkBase.Decode`, `ChunkArchiveLogStore`'s base reader | A base's bytes, its header and content | The reason an independent reading of archive logs §7.4 gives for the first rule the base breaks, one of `archive-logs/chunk-bases.json`'s rejection reasons; or the key hash, opener, tombstone, rows, and content length the rules give, which encode back to the input |
| `archive-log-chunk` | `ArchiveLogChunk.Check`, `ChunkArchiveLogStore`'s chunk check | A chunk's bytes | `truncated` when no byte of content precedes the 4-byte trailer, `bad-crc` when the trailer is not the content's CRC-32C, each one of `archive-logs/chunks.json`'s rejection reasons; or acceptance of exactly the encoding of the content |
| `archive-log-metadata` | `AzureArchiveLogMetadata.ReadWal` and `ReadCheckpoint`, `AzureArchiveLogStore`'s metadata and tag reader | A set of metadata names and values and a `durable-gen` tag, framed as counted, length-prefixed pairs | The reason an independent reading of archive logs §7.3 gives for the first rule the set breaks, one of `archive-logs/metadata-rejections.json`'s reasons, raised as `ArchiveCorruptException`: a value missing or malformed, a name repeated or differing only in case, a `wal_generation` other than the tag's, a `checkpoint`, `durable_kind`, `durable_base_md5`, or `durable_tomb` which contradicts the generation, a later generation with no opener, a `checkpoint` naming another log or generation, or a `durable_key` other than the key's; or the opener metadata, base MD5, and generation the rules give |

**Corpora.** Each target's seeds are its format's golden vectors, accepted and rejected, read from
`test/Durable.Storage/shared-vectors` at run time; `CLFM` has rejection vectors only, so its accepted seeds are files its encoder
writes. `test/Durable.Storage/fuzz/corpus/<target>` holds the inputs campaigns found which reach coverage beyond the seeds, and
`test/Durable.Storage/fuzz/corpus/<target>/regressions` every input which once broke an oracle. Each layer's tests replay that layer's
targets on every run, without libFuzzer (`WalCorpusReplayTests`, `MetaStoreCorpusReplayTests`,
`LedgerStoreCorpusReplayTests`, `LedgersCorpusReplayTests`, `ArchiveCorpusReplayTests`, `ArchiveLogsCorpusReplayTests`
(`Orleans.Durable.Storage.Engine.Archive.Logs.Tests`, the `archive-log-token`, `archive-log-chunk-base`,
`archive-log-chunk`, and `archive-log-metadata` targets),
`JournalsCorpusReplayTests`, and `OrleansCorpusTests` (`Orleans.Durable.Storage.Tests`), over
`test/Durable.Storage/shared/CorpusReplay.cs`): every seed and every committed input passes through its oracle within the deadline, the
seeds reach both a rejection and an acceptance, and no committed input is a copy of a seed. The WAL, LedgerStore, batch,
LSM, and Journals replays check each input in its own page leak scope (§11.1), so an input whose decoding keeps an
`ArcBuffer` page fails by name. `Orleans.Durable.Storage.Fuzz.Tests` checks that the campaign harness names each target
once and that every corpus belongs to a target.

**Campaigns.** `test/Durable.Storage/eng/fuzz.ps1 -Target <name>|all -Duration <seconds>` publishes the harness, instruments the Engine's
`Orleans.Durable.Storage.Engine.Wal`, `Orleans.Durable.Storage.Engine.Ledgers`,
`Orleans.Durable.Storage.Engine.Archive`, and `Orleans.Durable.Storage.Engine.Journals` namespaces, and the
`Orleans.Durable.Storage.FileSystem`, `Orleans.Durable.Storage.AzureStorage`, and `Orleans.Durable.Storage` assemblies,
with SharpFuzz, checks that each assembly then references `SharpFuzz.Common`, since SharpFuzz exits successfully on a
file it cannot instrument, and runs each target under libfuzzer-dotnet, which passes inputs to the .NET process through
shared memory. Each campaign passes libFuzzer a seed (`-Seed`, or a random one) and reports it, and generates inputs up
to the target's longest seed or its own bound, whichever is longer. `-Merge` replaces the committed corpus with the
inputs of itself and the campaign which reach coverage beyond the seeds and the regressions. Campaigns run on x64
Windows and x64 Linux, the platforms of the pinned libfuzzer-dotnet release; elsewhere a driver built from its source
runs them. A finding stops its campaign and leaves the input in `artifacts/fuzz/<target>/findings`;
`Orleans.Durable.Storage.Fuzz replay <target> <file>` reproduces it. The input joins the target's `regressions`, and the
decoder's fix lands in its own commit with a regression test. A change to a format re-merges its target's corpus from
the new seeds in the same row, as `ledgers-batch`'s was with row 1.7's layout and `journals-entry`'s with row 2.3b's
record.

## 7. Deterministic simulation

Deterministic simulation runs the composed system, several nodes in one process, under faults, with every
nondeterministic choice drawn from a seed. Every invariant checker (§3.2) runs after every step, every
acknowledged write is checked against the history (§3.3), and each journal's client history is checked for
linearizability (§3.4) and against the Journals spec (§3.1).

### 7.1 Clockwork

[Clockwork](https://github.com/ReubenBond/Clockwork) controls the simulation's scheduling, time, randomness, and
network. It is the repository's `Orleans.Testing.Simulation.*` family under `src/Simulation/`, the
`Microsoft.Orleans.Testing.Simulation.*` packages, built from source with its own build settings
([build and packaging §2](build-packaging.md#2-source-and-dependency-policy)). Fast CASPaxos' simulation builds against
the same projects, so one simulation kernel serves every build: `SimulationClosureTests`,
over every Storage project's restore graph, checks that each project reaches only the repository's kernel, and over every
Storage output, that none holds the legacy kernel's `Clockwork.dll` beside `Orleans.Testing.Simulation.dll`. Renaming the
packages kept the replay format (version 2), the `.cwr.json` artifacts, and the rule, diagnostic, and manifest IDs, so
the pinned traces replay unchanged.

Simulation test projects (for example `Orleans.Durable.Storage.Simulation.Tests`) opt into Clockwork's controlled
rewriting of the code under test; every other test project stays uninstrumented. Clockwork evaluates its rules against
the runtime which each simulation executable's `runtimeconfig.json` names, .NET 11 for every Clog project, which its
built-in rules cover; Clog sets no `OrleansSimulationTargetRuntime`. Each rewritten assembly records the runtimes its
rules support, and every simulation checks the running runtime against them as it starts, so a roll-forward to another
runtime fails it with `UnsupportedRuntimeException`. A simulation project sets
`OrleansSimulationInstrumentedTestProject`, which brings in the Microsoft Testing Platform integration
(`Orleans.Testing.Simulation.Testing.MTP`) through `eng/OrleansSimulation.targets`, and selects the `clockwork.bcl.deterministic` and
`clockwork.tasks.controlled` rule sets. It builds, with the kit and the `src` projects its closure rewrites, in
`SimulationRelease` or `SimulationDebug`, which compile in the conditional seams of §7.2 ([build and packaging
§1](build-packaging.md#1-framework-and-toolchain-boundaries)). After an ordinary build, its dependency closure, Clog's
assemblies among it, is rewritten out of place and deployed to the project's output, so `dotnet test
--no-build` runs the rewritten copy. The test assembly and the test host, which run before and outside every simulation,
keep ordinary IL, as does `Orleans.Durable.Storage.Diagnostics`, the fatal-error types' assembly, whose fatal-error
handler the test host intercepts before every simulation and which within one only calls the interceptor: test code
which the rewritten code calls only schedules work on Clockwork's scheduler lanes, where cooperative `await`s resume on
the lane. CsCheck and Accordant keep ordinary IL too: the conformance kit samples, shrinks, and generates cases in the
test host, outside every simulation, and runs each case in a simulation of its own (§4.1), within which a generator only
draws and a spec only judges; a scenario which draws CsCheck values draws them before its simulation starts. Race
exploration is opt-in for targeted tests.

The simulation kit, `test/Durable.Storage/Orleans.Durable.Storage.Simulation`, is a library which `test/Durable.Storage/eng/OrleansSimulation.targets`
references from every simulation project. It is rewritten with Clog's assemblies, so its nodes, transport, and doubles
await, delay, and complete tasks under Clockwork's control as the code under test does; they keep their state without
locks, since Clockwork runs one operation at a time. Code in the kit and in Clog reads time from `TimeProvider.System`,
which the rewriting redirects to the virtual clock. A run's `SimulationEnvironment` holds its cluster, transport, trace,
and checks, and owns the lifetime of every kit component, which tracks itself when it is created: disposal stops a
component and disarms every timer it armed, so a run leaves no work behind. The `SeededSimulation` driver (§7.4), in
`test/Durable.Storage/shared/Simulation`, is compiled into each simulation test assembly and drives the cluster from the test thread,
which joins the simulation under its control scope; the operations it dispatches, their continuations among them, run on
the kernel's operation workers (`SimulationOperationWorker-*`). Simulation affinity is logical:
`SimulationScheduler.IsSimulationThread`, not the managed thread, says whether code runs inside a simulation, so a check
of a continuation's thread asserts it, with the run's `SynchronizationContext`. A simulation project runs its tests one
at a time: concurrent simulations would share the code under test's process-wide state, such as the `ArcBuffer` page
pool, whose controlled lock one simulation can find held by another's thread, so a seed would take different steps in
different processes.

`LedgerStoreInvariants` checks simulated LedgerStore nodes against [ledgerstore
§5](components/ledgerstore.md#5-invariants) after every step. Whenever a node publishes its store, the checks project
the store's catalog ([ledgerstore §7.2](components/ledgerstore.md#72-catalog-checkpoint-clsc-version-1)), and they
read that projection with what `SimulatedLedgerReplicaTransport` records in the node's `ReplicaAudit`: the replicas
clients requested and the store acknowledged (LS-I1, both directions), the tombstone of every drop the store
acknowledged, which no restart loses (LS-I2), the node identity (LS-I6), and, after every restart, every entry the node
acknowledged read back from the recovered store as itself or corrupt, never missing (LS-I5, LR-I2). The checks and the
projection follow §5 and §7.2, and the node itself is lifecycle only: start, crash, restart, serve, and publish its
store.

`Orleans.Durable.Storage.Simulation.Tests` qualifies the kit (deterministic replay, failure reports, a run aborted by a
callback run outside its scheduler, teardown, the transport's faults, the replica transport ending a cancelled caller's
wait at once while its request still lands, and returning every page it rents under loss, duplication, and crashes, the
register's contract under its faults with its histories checked for linearizability, and each LedgerStore check firing
on a broken projection) and runs LedgerStore nodes under faults. In the quorum scenario (`QuorumSimulationTests`), a
writer with window `K = 4` appends through a `LedgerWriter` over its `Ledger` of three nodes, each write carrying the
known contiguous tail, while a recovery executor with a `Ledger` of its own over the same nodes recovers the ledger,
seal checks run, and reads cover every slot below each recovered end, under lost and duplicated messages, a partitioned
node, and a paused one. It checks that the writer sees no recovered end acknowledged, that every position below an end
reads DATA, that a seal check begun after a recovery completed reports sealed, that no reported contiguous tail passes
the writer's completed prefix (LR-I6, LR-I8, LP-I7, LP-I9, through the `LedgerTrace` of §5.1), and that each `Ledger`
disposes each only once every exchange with it has settled ([ledger-protocol
§6.3](components/ledger-protocol.md#63-cancellation-and-disposal)). In the crash scenario, three nodes whose processes
and operating systems crash with WAL writes in flight, up to every node at once, restart from what their disks kept,
while writers and readers use `Ledger`s, a controller creates and drops other replicas, one node's disk suffers media
damage, and the network loses, duplicates, and partitions messages; the scenario then recovers the ledger, reads every
position below the end, crashes every node's operating system, and reads them again and checks that the seal survived.
Its client history is checked as a single-value register per address once a write reached `Q` replicas, since a read at
or above the known contiguous tail decides nothing (ledger-protocol §8.3).

In the `Ledger` scenario (`LedgerSimulationTests`), a deposed primary's `LedgerWriter`, whose process holds the claim's
sequencer lifetime, keeps appending with up to `K = 4` appends in flight and checks at random times that its ledger is
open, while two executors, in other processes, recover the ledger through `Ledger`s of their own over the same three
nodes, under lost and duplicated messages, a partitioned node, and a paused one. The primary helps the recovery once its
lifetime stops; the first executor whose recovery returns closes the ledger in the map at its candidate end `T`, and
every process's `Ledger` learns `T` at a random time after (`ObserveRecordedTail`). The replay check's executors start
only once every append has completed, so there the open check, not a seal, is what stops the lifetime. Besides the
`LedgerTrace` checks (LP-I8, LR-I6, LR-I8, LP-I7, LP-I9, LG-I9, and the open check's stop), the scenario checks
that the acknowledged appends hold a prefix of slots below `T` (LP-I3, LP-I8); that every slot below `T` holds
the payload of the append which acquired its slot, so an append there which met a seal or could not complete is durable
([ledgers §8.2](components/ledgers.md#82-appends)); that every tail a `Ledger` reports is at most `T`, and each
which learned `T` reports it (LP-I11); and that once the primary's open check finds the ledger sealed, its next append
acquires no slot ([ledgers §8.3](components/ledgers.md#83-the-primarys-open-check)). That run replays from its seed and
from its artifact, so the writer's jittered write backoff and recovery's backoff run on the virtual
clock.

In the proposal scenario (`LedgerProposalSimulationTests`), the same deposed primary proposes batches through
`ProposeAsync` and checks that its ledger is open through `ConfirmOpenAsync`, and each process, the primary, two
executors, and two readers, has its own `LedgerMapView` of a `SimulatedConditionalRegister`. The first executor whose
recovery returns closes the ledger in the register at its candidate end and opens a successor. The primary's process
creates its writer through its reader's lease of the ledger, reads the map from time to time, as its composition does,
and its writer re-reads the map until its view decides each proposal which met a seal. Each reader's view reads the map
once at the start and again only when a sealed answer makes its
`LedgerReader` re-read it: it scans the ledger below the contiguous tail its seal checks learn while its view lists the
ledger open, and below `T` once it lists it closed. Besides the `LedgerTrace` checks, the scenario checks LG-I5, that
the durable proposals are exactly the slots below `T`, each at its call's index, a prefix in call order after which
every proposal is `Fenced`, and none `Ambiguous`, that each durable slot holds its proposal's batch and was written on
`Q` replicas, which the ledger trace's served answers show; and LG-I8, that every batch a reader saw lies below `T` and
is its slot's decided batch, and that each reader reaches `T`. A corpus-wide test runs every seed and checks that some
proposal whose write met a seal was decided from `T` and some reader re-read the map after a sealed answer, so the
corpus covers both paths.

In the transition scenario (`TransitionSimulationTests`), a process on each of four nodes, each with its own map view,
`LedgerWriterRegistry`, and `TransitionExecutor`, runs racing and interrupted transitions (replaces, splits, and merges,
each placing its ledgers on three of the four nodes, the executor among them) through duplicate `LedgerMapCommitter`s
over a `SimulatedConditionalRegister` with unknown outcomes, while the served primaries propose and nodes drop and
reconcile replicas under faults. After every step the map value passes `LedgerMapReference`, and once faults stop every
range is served by its registry's own following. It checks each committer answer against the map's history
(`LedgerMapHistoryOracle`), that every slot a proposal was answered `Durable` at below a closed ledger's `T` holds its
batch, the tag order of every key's rows across its ledgers against real time (LG-I6), each close against the seals
which covered it (LG-I9), that a reconciliation drops exactly the replicas its view revokes and keeps every listed
replica, and at the end that each registry's `Unserved` agrees with `TryGet` and that no pending lifetime remains whose
change can no longer apply. A corpus-wide test checks that each path the scenario aims at occurs on some seed.

In the history scenario (`ArchiveSimulationTests`), two processes, each able to serve primaries, on two of three nodes
propose rows of four keys, of every kind, through the ledgers they serve and execute replaces, splits, and merges, which
move ranges' primaries between them, while a drainer in the third process drains each closed ledger, in lineage order,
into the kit's `SimulatedArchiveStore`, sometimes reporting a bound below `T`, drops it, sometimes after a read of one
of its keys merges rows the store and the ledger both hold, and reconciles every node; the store answers `Stale` at
random, and replica reads answer backpressure at random. Readers in all three processes read histories and first touches
throughout; a takeover moves a key's primary to the other process during a read, after which the old process's reads
report themselves not linearizable and the new one's linearizable; and a read in the drainer's process meets a ledger's
drain, drop, and a refresh of its view after the store answered it. The reference history is each ledger's decided rows,
as its drain read them or, at the end, as the processes read them to `T` or to the open ledger's tail in its primary's
process. Each read equals exactly the reference's rows from the base over its `Through`, whose ends are decided; over it
no row was proposed after the read ended, every row of a ledger closed before it began is held, and, when the read
reports itself linearizable, every row answered `Durable` before it began (HI-I1). Each first touch's prefix, merged
with the reference rows of its map value's ledgers which the prefix does not hold, scanned from slot 0 with `Holds`
marking held rows, gives the reference's history below the value's next ID. A corpus-wide test checks that reads restart
on each cause, resume after backpressure, and merge rows the store and the live ledgers both held, and the read-order
mutant, which takes the map after the store, fails the oracle on every seed. Every change runs one seed, about 19 s of
wall time, and the mutant on it; exploration runs more through `DURABLE_STORAGE_SIM_SEEDS`, and the replay check runs a smaller
workload, since it runs the scenario three times.

Production code follows Clockwork's rules: it awaits a `ValueTask` rather than blocking on it, runs on the default task
scheduler, synchronizes within its process through unnamed primitives, creates no task with `TaskFactory.FromAsync`,
calls no other API whose call sites Clockwork turns into a `SimulationApiException`, and compiles no method with runtime
async. `ClockworkCompatibilityTests` (`Orleans.Durable.Storage.Architecture.Tests`) enforces these rules statically over
every rewritten assembly each simulation project's Clockwork manifest lists — Clog's assemblies, the simulation kit, and
rewritten third-party packages — checking `ValueTask<T>.Result` everywhere, `ValueTask` awaiter `GetResult` outside
compiler-generated `IAsyncStateMachine` types, `TaskScheduler` subclasses, `ConcurrentExclusiveSchedulerPair`,
non-`Default` `TaskScheduler` members, `TaskFactory` constructors taking a `TaskScheduler`, named constructors and
`OpenExisting`/`TryOpenExisting` on `Mutex`, `Semaphore`, and `EventWaitHandle`, `TaskFactory.FromAsync`, Clockwork's
other call-site rejection shims, and `runtime-async` methods (`MethodImplAttributes` `0x2000`), and verifying that no
Clog build file enables `runtime-async` in `<Features>`. No Clog assembly may appear in its reviewed exception list for
unreachable third-party package code, and planted fixtures (`SimulationFixtures.ClockworkCompatibility`) verify every
rule and the allowed forms. A loop which waits out the rest of an interval rounds the wait up to whole milliseconds,
because a delay truncates to whole milliseconds, a zero delay completes at once, and virtual time advances only when
nothing can run: a shorter wait would spin at one instant. The rewrite fails (`CWR0001`) when rewritten and excluded
code exchange a type it substitutes; Clog's closure, its own assemblies, crosses no such boundary.

Code under simulation compiles each async method to a state machine, whose builder and awaiters Clockwork substitutes.
Clockwork refuses to rewrite a method compiled with runtime async (`CWR0013`), so no Clog project enables the compiler's
`runtime-async` feature. The .NET 11 shared framework carries the Microsoft.Extensions abstractions, which a simulation
therefore runs unrewritten, and some of them run work signalled from outside the scheduler:
`BackgroundService.StartAsync` starts its loop through the real `Task.Run`, and a change token, through which an options
monitor reloads, runs its callbacks on the thread which signals it. The seed does not decide such work, and two layers
keep it out. At run time, Clockwork's simulation-thread check fails work which reaches a simulation from a thread
outside its scheduler and carries the simulation's context, as the real `Task.Run` passes it on: a controlled API
which runs with a simulation's context on a thread other than one of its scheduler's operation threads or the thread
holding its control scope throws `SimulationThreadAffinityException`, and the scheduler reports the first such call to
the thread driving it, which fails the seed (§7.4). Work which carries no context, such as a change token's callbacks,
which register without the execution context, fails only on its own thread, unseen by the driver, so the static layer
alone keeps it out. A test host's cancellation token is work of the first kind, since the test host cancels it on a
thread of its own, which runs there the callbacks rewritten code registered on it with the simulation's context; a
scenario therefore passes rewritten code only tokens it owns, which only simulation work cancels. Simulation projects
ban xUnit's test token (`test/Durable.Storage/eng/BannedSymbols.Simulation.txt`), and each use in test-host code, such as the
`SeededSimulation` driver, which observes it between steps, states why it stays outside the simulation. The driver
holds the control scope while it builds the environment and steps the cluster. Statically, `SimulationClosureTests`
reads each simulation project's Clockwork manifest and checks every assembly it rewrites, Clog's, the kit's, and
packages' alike, against a reviewed allow-list which fails closed: of the Microsoft.Extensions assemblies they
reference only `Logging.Abstractions`, `Options`, `Primitives`, `DependencyInjection.Abstractions`, and
`Configuration.Abstractions`, and of those no `IOptionsMonitor<T>`, `IOptionsSnapshot<T>`, or `OptionsMonitor<T>` and no
change token (`IChangeToken`, `ChangeToken`, `CancellationChangeToken`, `CompositeChangeToken`); a layer's `.Hosting`
namespace, in the layer's own assembly, may reference `Microsoft.Extensions.Hosting.Abstractions`, and the kit
references no `.Hosting` type. Package types reviewed as never running in a simulation are listed by name with why:
Azure.Core's binding of clients from configuration, whose section answers a reload token that never fires and whose
extension registers clients on a host's builder; a simulation builds the SDK's clients directly. A component inside a
simulation starts its loops through rewritten code (`Task.Run`, `TimeProvider`), logs through `ILogger`, and reads its
options once, at construction; a host adapter, such as a `BackgroundService`, lives in a layer's `.Hosting` namespace.
Orleans stays out of the rewritten closure too: constructing an Orleans `SiloAddress` arms Orleans' interning timer,
about ten virtual minutes out, which outlives every simulation's owners and fails the §11.1 teardown check, and Orleans
brings the ASP.NET Core connection and configuration assemblies the allow-list rejects. So the Orleans composition's
control decisions live in an Orleans-free component, `Orleans.Durable.Storage.Engine.Cluster.Control`, which the Engine
compiles and which decides from a projection of the node registry (`RegisteredNodes`) rather than its values, and only
the grains, which simulation does not rewrite, touch `NodeRegistryValue` and `SiloAddress`; `ComponentDependencyTests`
checks that the decisions' types use no assembly beyond the platform's, the fatal-error types', and their layers'
technologies. A simulation also stores what it writes in its simulated file system: `SimulationClosureTests` checks that
the kit and every simulation project name no entry point which reaches the real disk, the WAL's operating-system file
system (`WalFileSystem.Os` and `CreateOs`), its portable and io_uring storages, the LedgerStore's open and node-root
claim which default to that file system, the file-system providers' constructors which do, and the RocksDB storage, each
entry point declared in production and each forbidden use planted in a fixture. The portable storage's files issue their
I/O through the file system they are given, so a simulation may open them over a simulated or recording file system, as
the crash-state enumeration's recording file system does, which the check passes. So a simulation never reaches the
fail-stop answers of a real file system's contention (`FileSystemAnswers`), which only those entry points lead to.

Under simulation the code under test awaits as it does in production: a configured `await using` or `await foreach` and
a `SuppressThrowing` await run controlled, and task combinators, `ContinueWith` continuations, and completion sources
complete through Clockwork's controlled completion, as on a thread-pool thread with no synchronization context: a
continuation which captured a context is posted to it rather than run inline, and a `SemaphoreSlim.WaitAsync` waiter
resumes in a controlled operation of its own, so no continuation resumes on the real thread pool and a seed takes the
same steps in
every process. `ClockworkConformance`, in the kit, writes each of these constructs as Clog's code does, and
`SeededSimulationTests` runs it under the rewrite: a `SuppressThrowing` wait returns at its grace on virtual time, a
configured asynchronous sequence and disposable yield every item and are disposed, raced and continued completion
sources take one schedule from one seed, and a wait which times out, or whose token is cancelled first, ends at that
instant and leaves the run completed. A `Ledger` therefore decides on its quorum, tracks a straggler until it
completes and its buffers are released, and times recovery's backoff on the virtual clock: the quorum scenario's
replay seed repeats recovery requests after missed deadlines, and its runs record identical traces, so Polly's
jittered backoff is deterministic under Clockwork.

A simulation's test assembly is not rewritten, so its own code awaits through the BCL. A scenario's `await` resumes
through the lane's synchronization context, which the completing operation posts to, but a BCL task combinator is a
promise of its own: over a task which runs its continuations asynchronously, as a task completion source created so
does, the BCL runs the promise on the real thread pool, which then posts its waiter's continuation to the simulation
from outside it, in an order no seed decides and possibly after the driver has found nothing runnable, which it reports
as a stall. That work carries no simulation context, so the run-time check does not see it. Simulation projects
therefore ban the BCL's `Task.WhenAll`, `Task.WhenAny`, and `Task.WhenEach` (`test/Durable.Storage/eng/BannedSymbols.Simulation.txt`), and a
test's code awaits several tasks through the kit's `SimulationTasks`, whose combinators the build rewrites to
Clockwork's controlled ones; source which ordinary test projects share awaits its tasks one by one.
`SeededSimulationTests.TheKitsCombinatorsCompleteInsideTheSimulation` checks that each completes on a simulation
thread over such a task, where the BCL's completes on the thread pool. The same holds for the test assembly's other
hand-offs to the BCL: `ConfigureAwait(false)`, which resumes off the lane; a `Task.Yield` or `Task.Delay` and a
`WaitAsync` timeout, whose timer and cancellation callbacks the BCL may run on the thread pool; and
`CancellationTokenSource.CancelAsync`. Shared test source which also runs inside a simulation, such as the ledger
harnesses (`test/Durable.Storage/shared/Conformance/LedgerComposition`), uses none of them and, under `DURABLE_STORAGE_SIMULATION_TESTS`, which
the simulation test projects define in every configuration, yields, delays, and waits for a release
through `SimulationTasks.Yield`, `Delay`, and `CompletesWithinAsync`. A rewritten caller's wait on a task of the test
assembly which later operations complete is a wait the scheduler cannot attribute, so it reports a stall when nothing
else is runnable: a harness settles such work itself before rewritten code waits on it, as `LedgerSystem` lets its
replicas' late landings finish before the quorum's disposal waits on them. Likewise a rewritten caller's `await` on a
task of the test assembly which a later continuation of the test assembly completes escapes the coordinator: under
`DURABLE_STORAGE_SIMULATION_TESTS` the scripted register (`ScriptedConditionalRegister`) decides each held write as it arrives and
applies the decision a scheduler step later, through `SimulationTasks.Yield`, rather than waiting for a test's decision.
A scenario whose clients race runs them in the kit, on its client node's scheduler lane, as the quorum scenario's
writer, recoveries, seal checker, and reader run in `RecoveryRace`: each of their awaits, timers, and combinators is
then a controlled operation of that lane, and the test only awaits the race's end.

The scenarios catch only the failures their faults explain (`QuorumFailures`): an `AggregateException` when no write or
read reaches a quorum ([ledger-replicas §6.4](components/ledger-replicas.md#64-failures)), each of whose replica
failures is a missed deadline or, where the scenario damages media, corruption. A recovery admits no failure at all: it
repeats each replica's missed deadline until the replica answers. Any other failure fails the seed.

Once a scenario's faults heal, a recovery needs `max(Q, N − Q + 1)` responsive replicas ([ledger-protocol
§6.5](components/ledger-protocol.md#65-liveness)), each of which answers within one round
trip, so it ends within `RecoveryLiveness`'s bound: one round trip for its seals, two for its first window of reads and
the reads it repeats after a seal answer, one per further window of the `2K + 1` positions from `c`, and one for the
last position's recovery writes. A recovery already running at the healing may first wait out the transport's deadline
and the retry's maximum delay. Both scenarios crash a node while it serves a recovery's seal and, once that node
restarts, a node while the recovery runs, at its first recovery write or one of its first reads (`RecoveryCrashes`),
each as the request's WAL write leaves for the node's disk or just after it completes, so the seal or recovery write
may land without an answer, which an operating-system crash with the write in flight loses instead; one node down at a
time leaves the recovery the replicas it needs.
The quorum scenario crashes nodes during its first recovery and stops its loss and duplication at a random time after
its writes, partition, pause, and crashes end, and every recovery succeeds within the bound; the crash scenario
recovers once its nodes restart and reconnect and its network loses nothing, crashes nodes during that recovery, and
measures its bound from the last crash or restart before the recovery ends, as from a healing.

### 7.2 Seams

Every source of nondeterminism or I/O crosses an interface with a simulated implementation:

| Seam | Simulated implementation |
| --- | --- |
| Time | Clockwork's virtual clock: `TimeProvider.System` in rewritten code, the cluster's `SimulationTimeProvider` in test code |
| Random draws | One seeded random source per run, from which every draw a component makes under simulation comes, the jitter of each ledger's `MaxSegmentAge` among them |
| The WAL | `SimulatedWalStorage`, with crash cuts and damage |
| A storage node | `LedgerStoreNode`: a LedgerStore over its own `SimulatedWalStorage` on a Clockwork node, whose disk completes writes out of order after random latencies; it crashes (process or operating system), at once, as its next WAL write leaves for its disk, or as that write completes, and restarts from what its disk kept, pauses, isolates, suffers media damage on WAL pages its recovery classified, and fails a local storage operation of the kind armed, which stops its process |
| The fatal-error handler | `FatalErrors`, the test host's interceptor of `FatalErrorHandler`, which records each report with its test in place of the process stop; the driver takes the run's reports after each step and fails the run on any its fault injection did not cause (§7.4) |
| Replica transport | `SimulatedLedgerReplicaTransport`, an `ILedgerReplicaTransport` over `SimulatedRpc` on Clockwork's network: partitions, loss, duplication, delay, reordering, and pauses; a caller's deadline or cancellation ends its wait while the request may still land; an observer sees each delivery a node begins to serve, where a fault injector crashes the node |
| The MetaStore | `SimulatedConditionalRegister`, an `IConditionalRegister` with outages, failed reads, unknown outcomes (lost requests, lost answers, and delayed landings) reported by exceptions whose type does not reveal the outcome, and writes which apply and answer `Mismatch`, as a backend-SDK retry of an attempt which applied does ([metastore §2.2](components/metastore.md#22-iconditionalregister)); cancellation after dispatch ends the wait while the write may still apply |
| The object store | `SimulatedObjectStore`, an `IObjectStore` with failed requests, unknown outcomes (lost requests, lost answers, and delayed landings) reported by exceptions whose type does not reveal the outcome, and writes which applied answered as a retry meeting its own landing, `Exists`, `Mismatch`, or `LengthMismatch`; and an `IAppendableObjectStore` over the same objects, whose appends land late only at their length on an open object, and whose append may land, meet a seal, and be retried, answering `Appended` ([object store](components/object-store.md#2-contract-upward)) |
| The archive-log storage | The memory `IArchiveLogStore`, Azure's model of generations, offsets, blocks, and conditions, with injectable failures; and `AzureArchiveLogStore` over a fake Azure transport which Clockwork schedules, which injects failures before and after a request lands, requests which land after their own retry or after their call gave up, server errors after landing, bodies which break mid-read, retries which meet their own landing (409 or 412), failed properties reads, and crashes mid-run and between an opener's two requests, and checks what archive logs keep of each blob after every request, with leak checks on its response streams ([archive logs §10](components/archive-logs.md#10-verification)) |
| The archive store | `SimulatedArchiveStore`, over `InMemoryArchiveStore`, with latency, random `Stale` answers, a seeded ID source, and a hook after a read's answer ([archive §9](components/archive.md#9-providers-and-implementations)) |
| The journal index | `InMemoryJournalIndex` |
| Failure detection | Simulated membership with injected suspicion |

Rewritten code orders its work by values. An identity hash code (`RuntimeHelpers.GetHashCode`,
`ReferenceEqualityComparer`) differs between runs of one seed, so a hashed collection keyed by one enumerates in an
order no seed replays; `LedgerReader` keys its holdings by the order in which it opened them, so its disposal stops
their reads in an order those values fix.

#### Conditional seams

Where a seam belongs inside `src`, it is code which only the simulation configurations compile, under `#if
ORLEANS_DURABLE_STORAGE_SIMULATION`, so Debug and Release builds, and every shipped artifact, hold none of it
([build and packaging §1](build-packaging.md#1-framework-and-toolchain-boundaries)). Each block names its seam on its
`#if` line (`#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: <name>`), and changes nothing until a test turns it on;
`ConditionalSeamTests` checks that every block names a seam this table lists, and that every seam listed has a block.

| Seam | Code | Purpose |
| --- | --- | --- |
| `memory-replica-yields` | `InMemoryLedgerReplicaTransport.YieldsAtLandingAndAnswer`; `InMemoryLedgerReplica`'s writes, reads, and contiguous-tail reads | The memory provider applies each operation at once; with the seam on, each yields to the scheduler as its request lands and before its answer returns, so the interleavings of concurrent operations are scheduling choices an exploration enumerates (§4.1) |
| `write-checks-seal-before-landing` | `InMemoryLedgerReplicaTransport.WriteChecksSealBeforeLandingMutant`; `InMemoryLedgerReplica`'s writes | A race mutant of LR-I8: a write checks the seal in one step, lands in a later one, and answers from its check, so a seal between them leaves a write answered written whose batch the replica never holds; `SimulatedLedgerReplicaConformanceTests` shows the explored schedules catch it |

### 7.3 Faults and workloads

Faults: process crash and restart (memory lost, the WAL cut at a crash point), among them crashes while a replica
serves a recovery's seal and while a recovery runs, with the request's WAL write in flight or just durable, disk damage,
local storage failures (a
WAL write, torn or not, a WAL read, a named file's write, read, or deletion, a WAL file's deletion, and a directory
flush), each of which stops its node's process
([conventions §14](conventions.md#14-local-storage-failures-stop-the-process)) and each of which
`LedgerStoreNodeTests.EveryStorageFaultFiresAndStopsTheProcess` fires, partitions, message loss and delay,
MetaStore and object-store outages and ambiguous outcomes, slow and suspended nodes, false failure suspicion (a takeover
of a live primary), duplicated controllers and committers, lost commit replies, and clock jumps, which affect no safety
property ([system model](system-model.md#2-clocks)).

Workloads: concurrent journal clients with writes, reads, resends, maximal bodies, and replace requests, while
writers end segments and controllers split, merge, roll, drain, compact, drop, and sweep.

The Journals simulation (`JournalSimulationTests`, [journals §10](components/journals.md#10-verification)) runs these
workloads over ledger-store nodes under a fault plan, which fires each of its faults once per seed, in an order the seed
draws, while the clients, the controllers, and the drainer run, and heals each before the next: a journal process
crashes with its node, as a process or an operating system, while a write is in flight on it, and both restart, the
process from the map, the replicas, and the store; a node is partitioned, another paused; messages are lost, requests
duplicated, and journal answers lost; a restarted node's media is damaged; the ledger map register leaves writes'
outcomes unknown, one which never applies and one which applied; two committers race one transition; and an executor is
interrupted once it submits, and another's transition applies or finds it superseded. Each fault records that it took its
path, and a corpus check requires every fault on every seed. A call the faults leave unanswered is unbounded in the
client history, a read is sent again, and once the faults heal every call is answered and every open range served.

### 7.4 Seeds and replay

A seed controls a simulation's work, scheduling, and virtual time. `SeededSimulation` wraps Clockwork's seeds and
replay artifacts: it prints the seed, honors `DURABLE_STORAGE_TEST_SEED`, and on failure records the replay command and the
replay artifact in the results directory. A fixed seed corpus gates every change within a time budget (§12);
milestones add seed exploration, schedule exploration (the conformance kit's explored windows at
`DURABLE_STORAGE_SCHEDULE_BOUND=deep`, §4.1, which `just explore-schedules` runs), and minimization.

`SeededSimulation.Run` records a scenario in a Clockwork cluster seeded from the run, with Clockwork's seeded-random
scheduling. A step is one operation the scheduler dispatches; after each, the driver runs every invariant check the
environment holds (§3.2) and observes the run. Once the scenario succeeds, the driver tears the environment down,
healing its partitions, resuming its paused nodes, and disposing its components, the last created first; runs the work
due at that instant without advancing virtual time, so a timer a component left armed stays pending; and runs the
environment's teardown checks: first the quiescence leak check and then the page leak check, which the driver registers
for every run, the latter started in the scenario's execution context, and then those the scenario registered (§11.1).
The checks run in the order they were added, and the first which fails is the violation; a check which throws cannot
vouch for its invariant, so its exception is its violation, named for the check. After each step the driver first checks
for a debug assertion the run raised, whether or not the code under test caught its exception (§11.2), then takes the
fatal errors the step reported. A failed local storage operation names the node whose root holds its path, every node's
root being unique within a run, and the driver records the stop in the trace when that node's fault injection caused the
failure and its storage stopped. Any other report fails the run: a defect, a failure which no node's root holds, and one
which no injection caused. A run's reports which no step took are judged when it completes. The failure stopped the
node's storage, so the node answers nothing more: its process crashes, as a process or an operating system (chosen by
the seed), and restarts after a short downtime from what its disk kept, and the node's recovery checks run over the
restarted store, so recovery from the device's state is exercised wherever a failure is injected
([conventions §14](conventions.md#14-local-storage-failures-stop-the-process)). A process which is starting runs its
open's operations unfailed; the crash-state enumeration fails those (§7.5). A failed debug assertion, a violated
invariant or teardown check, a failed scenario or teardown, a run which can make no progress, and a run past its budgets
(200,000 steps, 10,000 steps at one virtual instant, which identifies a livelock, or five virtual minutes) fail it: the
driver raises the failure on the cluster's scheduler lane, so Clockwork records a faulted run whose identity is the
failure's type. Work which reaches the simulation from a thread outside its scheduler fails it with Clockwork's
`SimulationThreadAffinityException`, which the driver passes on to Clockwork whether its own step or a check which runs
the work due, such as the quiescence check, meets it, and Clockwork records the run as aborted, since that work ran
outside the recorded schedule (§7.1). The driver records through Clockwork's synchronous entry
(`ReplayRunner.RecordCluster`), whose callback is the driver's step loop, because Clockwork's entry-task form
(`RecordClusterEntry`) drives the steps itself and runs no checks between them. Clockwork completes a synchronous
entry's run unless an operation faulted, raced, or deadlocked, or the run aborted; a run whose scenario and checks
passed but which Clockwork recorded otherwise fails with an identity which carries Clockwork's outcome and failure
identity, so minimization keeps to that failure. A failure prints the seed, the step, the failure, the commands which
replay the seed and, unless Clockwork aborted the run, whose schedule does not replay, the recorded schedule for the
failing test case alone, and the last trace entries, each stamped with its virtual time; and writes to the results
directory the run's trace (`<test>.seed-<seed>.history.log`) and Clockwork's replay artifact (`.cwr.json`).
`ORLEANS_SIMULATION_REPLAY_ARTIFACT` replays an artifact's exact schedule in place of the seed. Every execution logs its
steps, virtual time, and wall time.

`DURABLE_STORAGE_SIM_MINIMIZE=1` also minimizes a failure's schedule with Clockwork's minimizer, a milestone activity: a candidate
counts only when its exact replay fails within the failing run's steps with the same failure: by type, and by the
invariant it violated, the site of the debug assertion it failed (its first frame's method and IL offset, since sites
may share a message) and the assertion's message, or its message. The minimized artifact (`.min.cwr.json`), its
rendered schedule (`.min.trace.txt`), and its trace (`.min.history.log`) join the evidence. A stalled run is not
minimized, since a longer schedule fails it too.

A corpus theory takes its seeds from `SeededSimulation.Corpus`, which `DURABLE_STORAGE_SIM_SEEDS` replaces with seeds or ranges
(`1..500`) for seed exploration, and through which a failure's replay command runs its failing seed alone. The
corpus theories record each seed once with `Run`; one `RunAndReplay` test per scenario also records its seed a
second time and replays the first run's artifact exactly, and fails unless all three record the same trace, and a
Verify snapshot pins one seed's trace, step count, and schedule identity (a hash of its decisions and outcome), so a
change which makes a seed run differently across processes fails the gate, and a change to the code under simulation
re-approves it. Simulations record client histories with `HistoryRecorder` on virtual time (§3.4).

`SeededSimulation.RunCase` runs one conformance case's scenario (§4.1) with every check `Run` makes, seeded with the
case's seed exactly, which no environment variable replaces, and returns the run, failed or not, without writing
evidence: the kit runs many cases while it shrinks and explores, and owns their replay through `DURABLE_STORAGE_TEST_SEED` and
`DURABLE_STORAGE_SCHEDULE`. It may wrap the run's seeded scheduling strategy before the first step, as the window explorer does.

Projects without Clockwork use `SeededSimulation.RunAsync`, which runs a scenario over `FakeTimeProvider` and
simulated storage: it prints the seed and honors `DURABLE_STORAGE_TEST_SEED`. A run which fails, or whose scenario raised a debug
assertion (§11.2), records the replay command and `<test>.seed-<seed>.history.log`. `RunAndReplayAsync` compares two
histories from the same seed. Thread-pool timing, real clocks, unseeded randomness, and cross-test shared state fall
outside such a schedule; a simulation retaining them identifies that limitation.

### 7.5 Crash-state enumeration

Crash-state enumeration establishes that every crash state the file system's persistence model allows recovers without
losing acknowledged data, and that every answer given before the crash point holds after recovery ([durable
answers](conventions.md#13-durable-answers)): unsynced writes reordered or lost, and renames and directory fsyncs
applied or not, after ALICE (Pillai et al., OSDI '14) and CrashMonkey (Mohan et al., OSDI '18). Its subjects are the
LedgerStore's node file, catalog and slot-index checkpoint, and WAL writes ([LS-I1, LS-I2, LS-I6, LS-I8, LS-I9,
LS-I11](components/ledgerstore.md#5-invariants)), the cluster claim's publication and reads
([LS-I12](components/ledgerstore.md#5-invariants)), the file-system MetaStore's writes and reads
([MS-I1](components/metastore.md#5-invariants)), and the WAL's directory publication, its answers from a directory's
names, and the portable storage's files ([WAL-I1, WAL-I3, WAL-I10, WAL-I12](components/wal.md#5-invariants)). Its
workloads include the store's creation, whose crash states before the node file hold no store, which an open with create
completes; a first write whose create record is held beneath later records; a batch of creations behind a held create
record, all of whose pairs a crash keeps or none; and a catalog checkpoint captured while a held write keeps the
frontier below a drop record, cut strictly past it; and seal records: a seal whose record's fsync is held while a writer
write and a contiguous-tail read meet it, a recovery write on the sealed replica, a seal which creates an unlisted
replica sealed, catalog and slot-index checkpoints past the seal records, and a drop of the sealed replica. In every
state, every slot and seal record names a listed or dropped replica's handle below the next handle; a seal which a seal
answer, a `Sealed` answer, or a sealed report asserted holds, recovered from its record or from the root which records
it; a replica no seal had reached is unsealed; a writer write the seal refused stored nothing; and each replica's
rebuilt contiguous tail is `max(0, h + 1 − K)` for its recorded window
([LR-I6](components/ledger-replicas.md#5-invariants)). The file-system object store's creates, replaces, deletes, reads,
and listings ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations)) run through both publish
paths, a hard link and a no-replace move, with one store instance crashed and one live over the same directory, and the
Accordant `IObjectStore` spec (§3.1) judges every answer given before the crash point against the state after it
(`FileSystemObjectStoreCrashStateTests`); mutants which skip the flush before `Created`, `Exists`, an absent ranged
read, a versioned read (present and absent), a listing, a delete's completion, a replace's version check, or the
`Mismatch` of a replace which lost its publication to a concurrent create each fail, and judging the answer the flush
protects as unknown explains every state they leave. Appendable objects' creates, appends, seals, tombstones, and a
delete and create again over them hold every answer in every crash state, with either process crashed at each of its
operations. Archive logs' chunk layout over the same store runs a key's first
run, appends, openers, reads, a listing, and the sweeper's deletion, and the Accordant `IArchiveLogStore` spec judges a
fresh store's properties, read, and listing in every crash state (`ChunkArchiveLogCrashStateTests`,
[archive logs §7.4](components/archive-logs.md#74-chunk-layout)).

**The recording file system.** The portable and io_uring storages' directory implementation, the portable storage's
files, the cluster claim of `FileNodeRootClaim`, the file-system MetaStore, and the file-system object store issue
every file-system call through the internal `WalFileSystem` ([wal §9](components/wal.md#file-system-directories)).
`RecordingFileSystem` (`test/Durable.Storage/shared/CrashStates`) implements it in memory beneath the real directory implementation,
the real `PortableWalFile`, the real claim, and the real register, rooted at the caller's root (the node root, for the
LedgerStore and the claim, and the MetaStore's root, for its registers), which exists durably; a flush of a directory
above the root throws, so every trace checks that no flush leaves the caller's root ([wal
§3](components/wal.md#3-requirements-downward)). It records each mutating operation in issue order: file and directory
creation, writes, truncation, renames, hard links, unlinks, directory removal, file fsyncs, and directory fsyncs. A test
chooses the portable file's shape: on macOS, every write is followed by an fsync of its file; on Linux and Windows,
writes are direct and write-through, and an fsync follows only a write which extends its file. The recorded page cache
stands for what reads return, for direct I/O the device's bytes. The io_uring storage's file I/O lies outside the seam
(§13). A test holds write-through writes to complete them in a chosen order or to fail them, as a device error does once
their bytes reach the device, fails a directory's flush, holds a file's fsync to keep a write which is not write-through
in the page cache for as long as it chooses, holds a file's creation to fix the step at which the WAL prepares its next
file, and crashes the process after any operation: every later call of the crashed process fails, its held writes end
without completing, and a restarted process sees the same page cache and continues the same trace. A test also fails any
recorded operation (`FailOperation`) as the device would: the seam reports the failure, which the test takes, and the
failure crashes the process at that operation, as the stop does ([conventions
§14](conventions.md#14-local-storage-failures-stop-the-process)), so every crash state of the restarted process is
judged as after any other crash.

**The persistence model.** `CrashStateEnumerator` applies the POSIX fsync contract over 512-byte sectors:

1. Directories map names to inodes. Every operation names the inodes it changes: an entry operation (create, mkdir,
   link, rename within a directory, unlink, rmdir) changes one directory, and a content operation (write, truncate)
   one file.
2. A content operation is durable once a later fsync of its file completes; an fsync covers the writes which completed
   before it. A write-through write's bytes are durable once it completes, and the length it extends once a later fsync
   of its file completes, as Windows requires; `O_DSYNC` also makes that length durable, which the model does not rely
   on. A write-through write which fails, or whose process crashes, never completes; it is direct, so the device holds
   what reads of it return, and it is durable once a later fsync of its file completes. An entry operation is
   durable once a later fsync of its directory completes. A file's fsync leaves its name unsynced, and a directory's
   fsync leaves its own name in its parent unsynced.
3. A crash after operation `k` keeps every operation durable by then and any subset of the others, which take effect
   in issue order: each unsynced operation is independently lost or kept, so a later operation can survive an earlier
   one's loss.
4. An entry operation is atomic; a rename replaces its target atomically. A kept write persists whole, zero-extended,
   or torn. A zero-extended write persists its file's new length with zeros in place of its bytes, as ext4's
   `data=writeback` mode allows. A torn write persists a proper, non-empty subset of the sectors it covers: every such
   subset of a two-sector write, and of a longer write each prefix, the last sector alone, and one further subset
   chosen by the seed. A completed write-through write whose length is unsynced persists with or without that
   extension.
5. The crash state is what the root reaches.

The crash the model enumerates is the operating system's. A process crash loses nothing the process issued: the page
cache survives it, so after a restart the model applies to the whole trace, and only the restarted process's flushes
make durable what the crashed process left unsynced. The model's flushes are the WAL's on every platform: `fsync` on
Linux and macOS, and `FlushFileBuffers` of the file's or the directory's handle on Windows, on local NTFS volumes
([wal §9](components/wal.md#file-system-directories)). On Windows the model's directory rule, that a directory
flush makes the directory's entries durable, is an assumption grounded in MS-FSA §2.1.5.7, which specifies that the
flush "ensures that the directory structure is persisted to stable storage" and that only NTFS implements it
([wal §1](components/wal.md#design-rationale)); the WAL refuses every other volume. The enumeration checks where Clog
places its flushes; `WalDirectorySyncTests` tie each recorded flush to the operating system's call, and no test cuts
power (§13).

**Enumeration.** A crash point lies between any two recorded operations. At each point the enumerator takes every
combination of the unsynced operations' outcomes; at a point with more than 1,024 combinations it takes the point's
strictest state (every unsynced operation lost), its most complete state (every one kept), and a sample chosen by the
seed, and a test reports how many points it sampled. It skips operations in a directory whose removal is durable,
which no state reaches, and deduplicates the resulting images within the point. Each image is judged against what its
own point acknowledged, so an image two points reach is checked at each. A test's cap on the states it recovers is at
least two per point: beyond it, the enumerator keeps every point's strictest and most complete states and samples the
others with the seed. The LedgerStore workloads' caps exceed their distinct images, so they recover every distinct
image they enumerate, including each sampled point's sample. For a seeded sample of the states whose recovery wrote,
every crash state of the recovery's own trace recovers again and is judged at the outer point. The seed follows
`DURABLE_STORAGE_TEST_SEED` (§7.4); a failure reports the crash point, the fate of each unsynced operation, the image, and the
trace.

**Oracles.** Each state recovers through the real code and is judged against the acknowledged history at its crash
point: an operation acknowledged at or before the point survives, and one in progress may or may not.
`CrashStateTests` (`Orleans.Durable.Storage.Engine.Wal.Tests`) check the model on hand-made traces, including that an
acknowledged unsynced write is lost at its last point, that a write-through write's bytes and length become durable as
rule 2 says, and that a cap below two states per point keeps every point's strictest and most complete images. They
check that the portable file keeps every completed write in every state, in both shapes (WAL-I1), and the directory
implementation's publication: a created log file and a created, replaced, or deleted named file name complete contents
in every state (WAL-I3); a created directory survives, however its path ends, and a creation under a missing parent
creates nothing; and a directory a crashed process created survives once a restarted process creates it again (WAL-I10).
After a creation or deletion whose directory flush failed stopped its process, or a crashed directory creation, a
restarted process's listing, open which finds no file, and refusal of an existing name each hold in every state
(WAL-I12). `FileNodeRootClaimTests` check that a flushed cluster claim is complete in every state and durable once its
creation returns, and that a claim a flushed read returns is durable after a crash between its publication and its
flush, and when a competing process links it just before the read (LS-I12). `FileSystemMetaStoreCrashStateTests`
(`Orleans.Durable.Storage.Engine.MetaStore.Tests`) judge every state with `ConditionalRegisterSpec` (§3.1): the
register's read after recovery, at a version consistent with every earlier read, is one the spec allows after every
answer given by the crash point, with a call in flight at the point, one which threw, or one whose process crashed an
unknown outcome which may have applied once (MS-I3). They run a workload of creates, mismatched creates, updates, stale
updates, and reads, whose first write creates the cluster's directory, and that workload with the process crashing after
each of its operations in turn, followed by a restarted process's create, read, and update; a read whose value another
process's writer renamed just before it and has not flushed; and that workload with each of its operations failing in
turn, which stops the process. Removing any flush, flushing before the read, or answering after a failed flush fails one
of them (MS-I1). `LedgerStoreCrashStateTests` (`Orleans.Durable.Storage.Engine.Ledgers.Tests`) run twelve bounded
workloads, each from an empty node root and the store's cluster claim:

- replica creates and drops, including a drop of a ledger the store does not list, with three or more catalog
  checkpoints with drops between them, checkpoint-directory deletions, and a WAL release past drop records, across a
  restart, and reads which meet two of the drops while each drop record's fsync is held;
- checkpoints through compaction and the reclamation of a generation file, across a restart;
- a create record's write-through write held while later records complete above it, so crash states tear it beneath
  intact later records; a first write whose create record is held so; and creations waiting behind a held create
  record, which share the next one;
- a catalog checkpoint captured while a held write keeps the durable frontier below a drop record, cut past the drop
  record; and one captured at the frontier while a create record which begins in a durable page has its last page in
  flight, cut exactly where the create record begins;
- write-through WAL writes, as the portable storage writes them on Windows, completing out of order while a
  checkpoint follows each completion;
- a checkpoint whose write-through root write fails once its bytes reach the device, which stops the process, then a
  restarted process which loads the failed root when it is usable, and otherwise deletes its generation file, and
  checkpoints again (LS-I9);
- preallocated WAL files, as the store's default writes them, with the WAL preparing its next file and rolling into
  it;
- a store's creation, replica creates, writes, a checkpoint, and drops, with the process crashing after each operation
  in turn and a restarted process checkpointing every replica and then creating, writing, and checkpointing a new
  one, followed by every operating-system crash after the process crash;
- that workload with each of its operations failing in turn, as a device error fails it, which stops the process
  there, followed by every operating-system crash after the failure, at most 200 states for each (LS-I10).

Every workload's trace replays from its seed: each recording runs twice and fails unless both issue the same
operations, so a failure's seed reproduces the trace it reports. The store's maintenance and the WAL issue operations on
threads of their own, so the recorder orders them: after each step it waits until maintenance is idle, and within a step
it lets maintenance finish one piece of work, such as a drop's directory deletion, before handing it the next, such as a
requested catalog checkpoint, and lets the WAL issue a held write before maintenance writes.

A step during which the process crashed is unacknowledged. In every state the catalog recovery derives equals the newest
valid catalog checkpoint with the create and drop records the WAL holds at or above its cut applied, and at least two
valid catalog checkpoints exist before any WAL release: every create and drop answered by the point survives, a batch's
create record and a drop in progress survive whole or are absent, a torn create record followed by intact later records
included, and no slot record survives without its create (LS-I1); every acknowledged drop holds, with its tombstone,
across catalog checkpoints and WAL release, recovery with every newer valid catalog checkpoint damaged yields the
catalog with every acknowledged tombstone from each kept one, the first retained WAL file lies at or below the cut's
file of the older of the two newest valid catalog checkpoints, with every catalog checkpoint deleted the open refuses
exactly when the recovered WAL holds a record, holds damage, or has released file 1, so always once a create or drop is
durable or was answered, and never serves without a tombstone it recorded, a root without a node file is empty or an
interrupted creation, a dropped ledger is never created again, and every drop which an operation answered dropped
before the point holds (LS-I2, LS-I4, LS-I5); the node identity is the one
the store was created with (LS-I6); each checkpoint holds exactly the latest WAL record of each slot below its cut
(LS-I8); the root in force is at or above every acknowledged root, and every leaf it references reads (LS-I9); and every
acknowledged write reads back.

## 8. Snapshot testing

Snapshots pin human-readable renderings which reviewers approve: decoded ledger maps, manifests, batches, and run
objects (golden vectors stay the byte-level authority); exception messages, log event IDs and templates, and metric
names and tags; Accordant state graphs, so a behavior change shows as a reviewed graph change; and transcripts of
deterministic simulation scenarios with fixed seeds. Nondeterministic values (GUIDs, times, paths) are scrubbed at
their source, and a snapshot change is reviewed as a semantic change.

The tool is Verify, as the `Verify.XunitV3` package at exactly 32.0.0, which runs on the repository's xUnit v3 and
Microsoft.Testing.Platform versions ([build and packaging §2](build-packaging.md#2-source-and-dependency-policy)
states the pin). A test project takes part by referencing `Verify.XunitV3` and compiling
`test/Durable.Storage/shared/SnapshotSettings.cs`. Each test writes its rendering in the specification's terms:

| Snapshot | Renders |
| --- | --- |
| `WalVectorTests.TheWorkedExampleReadsAsTheFormatDescribesIt` | WAL's worked example as the log writes and scans it: header fields, each page's frames and the record bytes they carry, positions, and the frontier ([wal §7](components/wal.md#worked-example)) |
| `LedgerStoreVectorTests.CatalogVectorsDecodeAsTheFormatDescribesThem` | Each `CLSC` catalog vector decoded: its header and fields ([ledgerstore §7.2](components/ledgerstore.md#72-catalog-checkpoint-clsc-version-1)) |
| `LedgerStoreVectorTests.CatalogRejectionsReportTheirReasonAndMessage` | Each catalog rejection vector's reason and message |
| `BatchVectorTests.BatchVectorsDecodeAsTheFormatDescribesThem` | Each batch vector decoded: its length, its segment fields, its row count, and each row's index, kind, defining or referencing fields, and body ([ledgers §7](components/ledgers.md#7-formats)) |
| `BatchVectorTests.BatchRejectionsReportTheirReasonAndMessage` | Each batch rejection vector's reason and message |
| `ManifestVectorTests.ManifestVectorsDecodeAsTheFormatDescribesThem` | Each manifest vector decoded: its epoch, `DrainedBelow`, watermarks, and each section's position, partition, tags, and tail ([archive LSM §7](components/archive-lsm.md#7-formats)) |
| `ManifestVectorTests.ManifestRejectionsReportTheirReasonAndMessage` | Each manifest rejection vector's reason and message |

A snapshot lives beside its test class as `{Class}.{Method}.verified.txt`, with a theory's arguments in the name. It is
UTF-8 with a byte-order mark and LF line endings, as `.gitattributes` and `.editorconfig` state. A rendering is
deterministic at its source: fixed identities, `FakeTimeProvider`, and seeded randomness. Verify replaces the solution,
project, output, temporary, and user-profile directories with placeholders and scrubs the GUID and date members of the
objects it serializes; the shared settings also scrub dashed GUIDs and ISO 8601 round-trip times within text.

A new or changed rendering fails its test, which writes `{Class}.{Method}.{runtime}.received.txt` beside the
snapshot and shows both texts. Accepting renames the received files to their snapshots' names, after which the change
appears in the diff and is reviewed as a change to the behavior it renders:

```powershell
Get-ChildItem tests -Recurse -Filter *.received.* | ForEach-Object { Move-Item $_.FullName ($_.FullName -replace '(\.DotNet\d+_\d+)?\.received\.', '.verified.') -Force }
```

## 9. Mutation testing

Stryker.NET, pinned in `.config/dotnet-tools.json`, runs with the Microsoft.Testing.Platform runner over the unit,
snapshot, property, and conformance tests. Its unit is a component (spec README §2.5), so a score measures one
component's tests however many components share an assembly. Each component but one has a configuration beside the
`src` project which compiles it, `stryker-config.<Component>.json`, whose `mutate` patterns select exactly the
component's source files, which `Orleans.Durable.Storage.Architecture.Tests` checks, and which names the test projects
that test it, or for an abstractions component its layer's test project, each with the kits, oracles, and corpus replays
it links from `test/Durable.Storage/shared`. `Orleans.Durable.Storage.Architecture.Tests` takes no part, since it checks dependencies
rather than behavior, nor does `Orleans.Durable.Storage.Fuzz.Tests`, which checks the fuzzing harness's registry while
each layer's tests replay its corpora. Mutation runs neither build nor mutate the consensus and simulation families:
`Orleans.Durable.Storage.CasPaxos`, which builds on Fast CASPaxos, has no configuration, and the simulation
projects, which build on Clockwork, take no part (below). Stryker skips every file headed `<auto-generated>`, as source
generators head their output.

Stryker records which tests reach each mutant by running each test alone. By default (`perTest`) it reuses a test host
and collects each test's coverage through the host within a fixed ten-second window. A busy host can miss the window;
Stryker then marks the test dubious and runs it against every mutant, whose timeouts then measure that test. The ledger
replica's configuration captures coverage in isolation (`perTestInIsolation`), a fresh host per test which writes the
test's coverage when it exits, because its longest crash-state, conformance, and competing-process tests miss that
window. The script fails a run in which Stryker marks any test dubious. Stryker reruns a test for every mutant it
reaches, so mutation runs leave out the longest crash-state enumeration, a process crash followed by an operating-system
crash, whose writes the lighter enumerations reach too; the test skips itself when `DurableStorageMutationTesting` is set
(`TestDiagnostics.UnderMutationTesting`), and runs in every ordinary test run.

Stryker tests mutants in test hosts which it reuses from one mutant to the next, and it starts a new host only after a
timeout or a crash. A static initializer runs once in a host, under whichever mutant is active when its type is first
used there, and its values stay for every later mutant the host tests. A mutant of a static initializer therefore takes
effect only if it is active at that moment, and once it has, the host fails the tests it changes under every later
mutant, which Stryker reports killed: a ledger writer's instrument which one such mutant renamed failed the metrics
tests under every mutant tested after it in its host, unit and description mutants among them, which no measurement
reads. Each unit's configuration therefore leaves out the mutants of the instrument factories its code calls
from static initializers (`Meter.CreateCounter`, `CreateGauge`, `CreateHistogram`, and `CreateUpDownCounter`) with
`ignore-methods`, and lists only those it calls: `Orleans.Durable.Storage.Engine.Ledgers` (`LedgerWriterMetrics` and
`LedgerReadMetrics`), `Orleans.Durable.Storage.Engine.Archive.Lsm` (`ArchiveMetrics` and `SweepMetrics`),
`Orleans.Durable.Storage.Engine.Wal` (`WalMetrics`), and `Orleans.Durable.Storage.FileSystem`
(`FileSystemObjectStoreMetrics`). `LedgerWriterMetricsTests.TheLedgersMeterPublishesItsAppendAndReadInstruments` pins
each `Orleans.Durable.Storage.Engine.Ledgers` instrument's name, unit, and description; the other meters' instruments
have no such test, so no test pins their names, units, or descriptions. The ledger replica's static initializers,
`SlotLeafCodec`'s leaf length bounds, are calls which no `ignore-methods` pattern names apart from other calls, and
their three mutants survive (§13).

Stryker limits each run to one and a half times its tests' duration in the initial run, plus the configuration's
`additional-timeout`, five seconds by default, and counts a mutant whose run exceeds that limit as timed out, which the
score counts as detected. Load which slows a run past the limit thus reports a surviving mutant as detected. Under the
default, ledger replica's runs timed out mutants which compile to the original code or change only an exception's
message. The ledger replica's configuration adds 60 seconds, so that its timeouts are hangs (§9.1).

`test/Durable.Storage/eng/mutation.ps1` runs Stryker with the pinned SDK and a unit's configuration: on the core units, on the units
`-Project` names, or with `-Since <commit>` on what changed since that commit in the working tree. A unit's changed
source files are mutated alone, the whole unit is mutated when its test projects or the shared test sources changed,
and a unit with neither change is skipped; Stryker's own `--since` diffs through LibGit2Sharp, which resolves a git
worktree to its main checkout. `-Rescore` scores existing reports against the baseline. `just mutation-core`,
`just mutation <unit>...`, and `just mutation-since <ref>` wrap it. It writes each unit's Stryker log, HTML and
JSON reports, `survivors.txt`, and `equivalent.txt` under `artifacts/mutation/<Unit>`, removes any snapshot test's
`.received` file a killed mutant left (§8), and reports each unit's score, break threshold, counts, and wall time.
Stryker.NET 5.0.0 runs on .NET 10, reads each project's language version, `preview`, and compiles its mutants with its
own Roslyn 5.9, whose `preview` includes unions; Stryker.NET 5.1.0 takes the version each project reports rather than
resolving it against its own compiler (row 0.20 of the [implementation plan](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md)).

A mutation run sets `DurableStorageMutationTesting`, under which the build writes to `Artifacts/durable-storage-mutation` at the repository's root and the test
applications carry no `testconfig.json`. Stryker writes each mutated assembly into its test projects' output
directories, so the outputs which validation tests keep the build's assemblies. The crash and hang dump extensions
(§11) run the test host beneath a controller process which opens a second server connection, and Stryker's runner
serves one connection per test application; under mutation a failing or crashing test is a killed mutant and Stryker's
own timeout ends one which hangs, so the run also sets `DURABLE_STORAGE_TEST_FAILURE_DUMPS=0`.

The score is the share of killed and timed-out mutants among every mutant a test ran or could have run against (killed,
timed out, survived, or uncovered), less the survivors which `test/Durable.Storage/eng/mutation-baseline.json` records as equivalent. Each
equivalent entry names a mutator and optionally a file, the original and replacement text, and a `within` pattern which
must enclose the mutant, with a one-line reason; entries without a file describe classes of equivalent mutants. A full
run fails on an entry for a file which no survivor matches, so the baseline stays current. The core units are those
the baseline lists with a break threshold: `Orleans.Durable.Storage.Engine.Ledgers` and
`Orleans.Durable.Storage.Engine.Ledgers.Store`, then the LSM, archive logs
(`Orleans.Durable.Storage.Engine.Archive.Logs`), and Journals as each lands. Each breaks at 80% unless the baseline
holds it at a lower measured score while its gap closes (§9.1, §13), and its threshold rises as its suites mature. A
surviving mutant is either killed by a new test or recorded as equivalent. A run in which a core unit tests no mutant
fails, unless only changed files without a mutant were mutated. A full run of a core unit also compares its tested
mutants (killed, timed out, survived, uncovered, and equivalent) and its compile errors with the counts the baseline
records, within the baseline's `countTolerance` of 10%: a parse or compile gap removes mutants from the score and can
raise it, and code which adds or removes mutants moves the counts, which the baseline then records after review. A
change to a core unit runs `just mutation-since main`; milestones run `just mutation-core`.

Stryker removes every mutant of a method when one of them causes a compile error it cannot attribute to that mutant,
and reports those mutants as compile errors. Four shapes cause it: a local assigned in a `try` whose `catch` throws,
read after the `try` statement once a mutant removes the throw or empties either block (CS0165); pattern variables
declared in an `||` condition and read after it, which a mutant of the condition leaves unassigned (CS0165); an `async`
method returning a value which a mutant lets fall off its end (CS0161), since Stryker adds an ending `return` to
synchronous methods only; and a generic call whose type argument a mutated argument leaves uninferable (CS0411). The
script reports each project's compile errors; §9.1 lists the methods affected.

Simulation suites take no part: Stryker replaces a project's assembly in a test's output with its own compilation after
Clockwork has rewritten the build's (§7.1), so a simulation would run unrewritten code. Simulation oracles are tested by
an implementation-level counterpart of each Quint mutant (§5), a mutant of the guard the model's mutant disables, which
the simulation must catch (§13).

### 9.1 Baseline

Stryker.NET 5.0.0 measured the core projects on the reference machine (§12) at concurrency 8, while other work loaded
it, before row B.3 replaced positional SEAL entries, the tail check, and repair, so the methods it names below include
removed ones:

| Project | Score | Break | Killed | Timed out | Survived | No coverage | Equivalent | Compile errors | Wall time |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `Orleans.Durable.Storage.Engine.Ledgers` and `Orleans.Durable.Storage.Engine.Ledgers.Store`, one project until row B.8b split it | 78.50% | 78.5% | 1,640 | 58 | 362 | 103 | 224 | 501 | 2 h 53 min |
| The append path and its sequencer, their own project until row B.8c folded them into `Orleans.Durable.Storage.Engine.Ledgers` | 100% | 80% | 110 | 27 | 0 | 0 | 47 | 54 | 4 min 37 s |

`test/Durable.Storage/eng/mutation-baseline.json` records the two ledger projects' tested mutants and compile errors (§9) at their first full
runs after row B.8b's split and row B.8c's fold, which carried the append path's reviewed equivalent mutants into
`Orleans.Durable.Storage.Engine.Ledgers`; before them, the projects this table measured tested 2,387 and 184 mutants and
failed to compile 501 and 54. Until then each holds the combined project's 78.5% as a provisional floor, and the code
row B.8c folded into `Orleans.Durable.Storage.Engine.Ledgers` keeps the 80% floor it met as its own project, which the
review of that run checks, since a break threshold applies to a whole unit.

No mutant which compiles to the original code or changes only an exception's message timed out in either run, so the
timeouts do not measure the machine's load (§9). The ledger projects' remaining timeouts are hangs: a scan whose counter
runs away from its bound (`address--` in `InMemoryReplicaStorage`'s tail scan, `leaf--` in `LiveReplica`'s tail scan),
a loop whose counter no longer advances (`SlotCheckpoints`' tree height), a binary search whose bounds stop converging
(`SlotLeafCodec`'s exception lookup), and a quorum wait which counts answers down and so waits on replicas a test holds
(the quorum operations, which `Ledger` holds). The ledger replica's baseline holds its threshold at its measured score
while its gap closes (§13).

The compile errors include every mutant of the methods Stryker removes whole (§9): the append path's `AppendAsync`,
the quorum's `SealAsync` and `ResolveAddressAsync`; `LedgerStore.OpenAsync`, `CreateStoreAsync`,
`ApplyRecoveredStoreRecordAsync`, and `DisposeAsync`; `NodeFile.ReadAsync`; the `CreateRecord` constructor; and
`LiveReplica.ReadAsync`, `WriteCheckpointAsync`, and `ResolveHighestAsync`. The decisions those LedgerStore methods
sequence are mutated in their own methods, `DecideOpen` and `ApplyLocked`. The ledger replica's undetected mutants,
which the script lists in its `survivors.txt`, are the gap §13 records.

## 10. Integration, end-to-end, and soak

**Integration.** The conformance suites (§3.1, §4.1) run over real boundaries: Orleans grain calls, WAL files (and
io_uring on Linux), RocksDB, and cloud providers against the storage-service emulators of
`test/Durable.Storage/apphost/Orleans.Durable.Storage.AppHost`. They establish that each boundary behaves as its simulated double assumes.
Local boundaries run on every change; emulators run at milestones: `just emulators` runs
`Orleans.Durable.Storage.Cloud.Tests` and `Orleans.Durable.Storage.Interop.Tests` in Release and Debug with
`DURABLE_STORAGE_EMULATORS=1`, which starts the AppHost's emulators through Aspire's testing host, and fails if any test skips: an
emulator which does not start or answer fails its tests. Without the variable the emulator tests skip, naming it, while
the stores' stub tests run on every change: canned responses through a transport double pin each request's conditions
and checksum and each service status's outcome, so the every-change gate, and a mutation run over it, covers the mapping
without an emulator. Each run keeps its configuration's xUnit report under
`artifacts/validation/Emulators/{Release,Debug}`, and a passing run writes `artifacts/validation/Emulators/run.json`
beside them: the commit (`git rev-parse HEAD`, and whether the tree had uncommitted changes), each AppHost image's
digest ([build and packaging §2](build-packaging.md#2-source-and-dependency-policy)), and the reports' paths.

**Archive-log fidelity on a live account.** `AzureArchiveLogFidelityTests` (`Orleans.Durable.Storage.Cloud.Tests`) runs
each precondition of [archive logs §9.1](components/archive-logs.md#91-azure-append-blobs-azurearchivelogstore) on
Azurite and on a live storage account, each backend's result a test case of its own, at milestones. It runs with the
`DURABLE_STORAGE_AZURE_LIVE` environment variable set to the blob service endpoint of an account, such as
`https://account.blob.core.windows.net/`, and authenticates with Microsoft Entra ID through `DefaultAzureCredential`,
which takes the Azure CLI's sign-in on a workstation and a workload identity in CI; the identity holds Storage Blob Data
Owner on the account, the built-in role which may write blob index tags. No shared key or SAS reaches the tests, and no
file in the repository holds a credential. Without the variable each live case skips with a reason which names it;
`just emulators` scopes its skip allowlist per project (`Orleans.Durable.Storage.Interop.Tests` allows only
`AzureLiveAccount.SkipReason` while `DURABLE_STORAGE_AZURE_LIVE` is unset, and `Orleans.Durable.Storage.Cloud.Tests` allows each of
`AzureLiveAccount`, `S3LiveAccount`, `S3ExpressLiveAccount`, and `GcsLiveAccount`'s skip reasons verbatim while its
corresponding `DURABLE_STORAGE_*_LIVE` variable is unset, which `LiveAccountHarnessTests` pins), records the tests it allowed in
its run record, and allows no skip while each variable is set. Precondition 3b's tag condition, 7, and 7b, which Azurite
does not model, are established only by that run.

Live runs, each case recorded per backend:

| Date | Account | Commit | Result |
| --- | --- | --- | --- |
| 16 November 2026 | StorageV2, Standard_LRS, westus2, no hierarchical namespace, shared keys disabled; Entra ID through the Azure CLI | `8d9f0e4437481feba4d548b87af61dad106b1e33`, with the Entra ID authentication of `4802e5fb9f22fd3075858834b0d3504a625610f4` uncommitted | Azurite 12 of 12; live 10 of 12. Precondition 3 failed: an `Append Block` under `x-ms-if-tags` on an absent blob answered 404 `BlobNotFound`, and a `Put Blob` under it created the blob. The name case failed: a name of 255 segments was created, and one of 256 refused with 400 and no error code |
| 16 November 2026 | The same | The commit which adds this row: the opener's `If-Match: *` and the restated precondition 3 | Azurite 13 of 13; live 13 of 13: preconditions 1 to 8, 3b, and 7b, the checksum and checkpoint case, the name case, and the listing; `just emulators` with the variable set, Release and Debug, 148 of 148 each, no skip |
| 16 November 2026 | The same | The commit which adds the Orleans interop row below, the live helpers shared as `AzureLiveAccount` | Azurite 13 of 13; live 13 of 13; `just emulators` with the variable set, Release and Debug, `Orleans.Durable.Storage.Cloud.Tests` 148 of 148 each, no skip |

**Object-store ledger fidelity on live accounts.** `AzureAppendableObjectFidelityTests`, `S3ObjectLedgerFidelityTests`,
`S3ExpressObjectLedgerFidelityTests`, and `GcsObjectLedgerFidelityTests` (`Orleans.Durable.Storage.Cloud.Tests`) run
each precondition of [object-store ledgers §3.1](components/object-ledgers.md#31-from-the-object-store) and the
end-to-end `ObjectLedgerStorage` and `AppendObjectLedgerStorage` lifecycles on their emulators (or
`FakeS3DirectoryBucketService` for S3 Express) and on live accounts gated by `DURABLE_STORAGE_AZURE_LIVE`, `DURABLE_STORAGE_S3_LIVE`,
`DURABLE_STORAGE_S3_EXPRESS_LIVE`, and `DURABLE_STORAGE_GCS_LIVE`. `AzureLiveAccount` isolates each test in a per-test container with a
once-per-process stale-container sweep; `S3LiveAccount`, `S3ExpressLiveAccount`, and `GcsLiveAccount` isolate each test
under a timestamped prefix with a once-per-process stale-prefix sweep and bounded-parallel cleanup on disposal. No file
in the repository holds a credential. Each provider remains unqualified until its live run passes and is recorded below:

| Provider | Environment variable | Date | Account / bucket | Commit | Result |
| --- | --- | --- | --- | --- | --- |
| Azure Blob Storage (append & per-slot) | `DURABLE_STORAGE_AZURE_LIVE` | Pending | — | — | Not yet run; awaits live account credentials |
| Amazon S3 (per-slot) | `DURABLE_STORAGE_S3_LIVE` | Pending | — | — | Not yet run; awaits live account credentials |
| S3 Express One Zone (per-slot) | `DURABLE_STORAGE_S3_EXPRESS_LIVE` | Pending | — | — | Not yet run; awaits live account credentials |
| Google Cloud Storage (per-slot) | `DURABLE_STORAGE_GCS_LIVE` | Pending | — | — | Not yet run; awaits live account credentials |

**Orleans interop.** `Orleans.Durable.Storage.Interop.Tests` reads the composition's archive logs with Orleans' own
Azure journaling provider, `AzureBlobJournalStorage` from the repository's `Orleans.Durable.AzureStorage` project, with read-only access, against the fold of each journal's writes and a `JournaledStateManager`'s
recovered state ([archive logs §10](components/archive-logs.md#10-verification)); its cases are the evidence of [archive
logs §7.3](components/archive-logs.md#73-azure-layout)'s observations of plain Orleans. Each case runs on the fake Azure
transport on every change, on Azurite with the emulators, and on the live account under `DURABLE_STORAGE_AZURE_LIVE`, whose cases
skip with `AzureLiveAccount`'s reason, which `just emulators` allows, while it is unset; `just emulators` runs the
project beside `Orleans.Durable.Storage.Cloud.Tests`. The random-sequence property's milestone runs write their seed to
the test's output, which the run's xUnit report keeps, and a failing case names the seed which replays it on its
backend. These cases are evidence of [archive logs §7.3](components/archive-logs.md#73-azure-layout)'s layout and
observations, not of OR-I8: they write through `ArchiveLogWriter` and bypass `DurableJournalStorage`, whose payloads
OR-I8 governs.

| Date | Commit | Result |
| --- | --- | --- |
| 16 November 2026 | `2a45fd30defe024ceeb858e4a75270b66e4be992`, then in `Orleans.Durable.Storage.Cloud.Tests` | Fake transport 10 of 10 and Azurite 10 of 10, Release and Debug; live not run, `DURABLE_STORAGE_AZURE_LIVE` unset |
| 17 November 2026 | The commit which adds this row, in `Orleans.Durable.Storage.Interop.Tests` | `just emulators` with `DURABLE_STORAGE_AZURE_LIVE` set, Release and Debug, no skip: fake transport, Azurite, and live (the account of the fidelity runs) 26 of 26 each: every case, a journal whose identity holds the characters a URI escapes, a non-ASCII identity refused before admission, the state replay in both formats, the observations of [archive logs §7.3](components/archive-logs.md#73-azure-layout), and the random-sequence property, from the recorded seeds `45_QqVr6QYhYwn1W` (Azurite) and `13o4Wl9IMoqfHM01` (live) in Release and `4dJ1sRcChrP2oU7L` and `0Brqn1BnXpgW-uB-1` in Debug. The escaping case found Clog's one disagreement, a checkpoint's non-ASCII name, which no metadata header can carry, fixed by Azure's name rule `azure-blob-2`; no case found Clog and Orleans apart otherwise |

**End-to-end.** Multi-silo clusters through `Orleans.TestingHost` run over real LedgerStores and real grain calls, and
every journal written is checked against the fold of its history and for linearizability, and every grain-visible
outcome matches the history ([orleans §9](compositions/orleans.md#9-verification)). A smoke set runs with the default
tests: a hand-off whose old primary keeps appending, a kill and takeover, and calls forwarded during a transition. The
rest are explicit tests with the trait `Category=EndToEnd`, which `just e2e` (`test/Durable.Storage/eng/validate.ps1 -Check EndToEnd`) runs
with the smoke set at milestones; a scenario which is skipped or loses its trait fails the run. A partition between two
silos is injected by a grain-call filter, a loss membership does not see: every call between their nodes' grains fails
as a lost message's response timeout does, a forwarded journal write once its owner answered it and every other call
before it leaves its silo, while a ledger moves from one silo's node to the other's
(`OrleansControllerTests.AWorkloadStaysLinearizableWhileALedgerMovesAcrossAPartition`). A partition membership sees,
which declares silos dead, is covered by the kill and takeover scenarios.

**The sample.** `Orleans.Durable.Storage.Samples.Tests` runs `playground/DurableStorage/Orleans.Durable.Storage.Samples.Journaling`'s silo
configuration, its `appsettings.json` and its registration through the composition's public surface, in a three-silo
`Orleans.TestingHost` cluster over in-memory stores with the default tests: a grain's state written, its silo killed,
the state recovered on another silo, and the killed silo restarted over its node root. The sample itself, over Azurite
(`just sample`), is run by hand; its README describes what to watch.

**Soak.** Long multi-node runs with the oracles enabled establish steady throughput and latency and the absence of
leaks: memory, handles, disk, WAL retention, and object counts. Soak runs gate final validation of the finished
system only.

**Linux LazyFS crash soak.** `LazyFsCrashSoakTests` (`Orleans.Durable.Storage.Tests`, trait `Category=LazyFs`,
explicit, run via `just lazyfs` / `test/Durable.Storage/eng/lazyfs.ps1` with `DURABLE_STORAGE_LAZYFS=1`) runs a three-process Orleans + Clog cluster on
Linux with each silo's `LedgerStore` root (WAL + WAL-less `RocksDbJournalIndex`) mounted on its own
[LazyFS](https://github.com/dsrhaslab/lazyfs) FUSE filesystem. LazyFS holds unsynced writes in a user-space page cache
and commits only files and directories flushed via `fsync`/`fdatasync`. Under concurrent journal appends and reads
(including contended keys), the test injects `SIGSTOP`/`SIGCONT` pauses, rotating single-silo `SIGKILL` +
`lazyfs::clear-cache` faults, explicit RocksDB `FlushAsync` followed by unflushed memtable writes, and a simultaneous
three-silo `SIGKILL` + `lazyfs::clear-cache` power loss. After recovery on the same mounts, it asserts preserved node
identities, clean WAL recovery and WAL-less RocksDB suffix replay without `JournalIndexCorruptException` or fatal
storage errors, agreement with slow-path `ArchiveReader` folds, and linearizability of the complete client history
(with in-flight crashed calls modeled as ambiguous) under `LinearizabilityChecker`. Outside Linux or without
LazyFS/FUSE, the cases skip cleanly unless `DURABLE_STORAGE_LAZYFS=1` is set. Because FUSE without `direct_io` does not support
`O_DIRECT`, the soak test mounts WAL files through buffered `RandomAccess` + `fdatasync` (`BufferedPortableWalStorage`)
so LazyFS's simulated page cache intercepts every unsynced write.

**Deployment.** Deployment qualification under retained state and injected faults is
[deferred](deferred.md#reliability-labs-for-the-orleans-topology). An application or deployment oracle stays
outside the faults it injects. It records an intent before submission and its outcome after it. Every acknowledged
entry is read back intact at its version. An ambiguous attempt retains its identity and uncertainty until recovered
history establishes an outcome. Process readiness, protocol recovery, resumed progress, and history preservation
are separate observations.

## 11. Failure evidence and leak checks

Each test application captures evidence independently of its launcher
(`test/Durable.Storage/Directory.Build.props`, `test/Durable.Storage/testconfig.json`):

| Evidence | Trigger | Content |
| --- | --- | --- |
| Crash dump | Abnormal host exit | Heap dump and running-test sequence |
| Hang dump | Ten minutes without a test starting/finishing | Full dump and hang log, then host termination |
| Failure dump | Test failure, error, or timeout | WithHeap dump; default one per host, configured by `DURABLE_STORAGE_TEST_FAILURE_DUMPS` and `DURABLE_STORAGE_TEST_FAILURE_DUMP_TYPE` |
| Test trace | Each test | Logging through `TestOutputLoggerProvider` |
| Simulation history | Failing seeded simulation | Replay command, history, and (with Clockwork) replay artifact, and on request a minimized artifact and trace (§7.4) |
| Leak report | A leak check failing at teardown (§11.1) | Outstanding page count, with `DURABLE_STORAGE_TEST_LEAK_TRACES=1` the call site of each outstanding rental; Clockwork's pending-work snapshot; the exchanges still in flight; the files still open |
| Linearizability visualization | Failing `LinearizabilityAssert.Linearizable` | `<test>.linearizability.html`: Porcupine's page of the client history, each partition's longest partial linearizations with the model's states, and annotations (§3.4) |
| Debug assertion | A failed `Debug.Assert`, `Debug.Fail`, or `Trace.Assert` (§11.2) | The assertion's message and its site's stack in the failure of its test, its seeded simulation (with that run's evidence), or its test assembly |
| Fatal error | A report to `FatalErrorHandler` which its test did not take ([conventions §14](conventions.md#14-local-storage-failures-stop-the-process)) | The handler's message, naming the exit code and the site, and for a storage failure the path and operating-system error, in the failure of its test or its test assembly, and every intercepted report in the test's trace |
| Platform diagnostics | Validation-script runs | Platform decisions at Debug verbosity |

Results live under each test project's `bin/<configuration>/net11.0/TestResults` or, for validation runs, `artifacts/validation/<check>`. Dump capture preserves
the test result.
CI retains reports and histories on all outcomes and dumps on failure, with seven-day retention.

### 11.1 Leak checks

Leak checks establish at teardown that buffers return to their pool
([BUF-I1, BUF-I2](components/buffers.md#5-invariants)), handles close, and no background work outlives its owner:
once the owners are disposed, nothing they started remains to run, whether or not it would end. They apply where
they matter most: buffer-owning paths, lifecycle and crash tests, and simulations. The checks live in
`test/Durable.Storage/shared/LeakChecks`:

| Check | Establishes | Applied to |
| --- | --- | --- |
| `ArcBufferLeakCheck` | Every page rented from the pool, or wrapped around an array, in the test's execution context and the work it starts has returned. A test class holds the check in a field initialized by `Start` and disposes it after the components it owns; a test which has already failed reports that failure instead. `InScope` and `InScopeAsync` run one case of a property in a nested check, which counts its pages in its parent too, restore the parent, and then assert once the case has released what it owns, so a leaking case fails, is shrunk, and reports its seed. `Run(accounting, body)` carries a check into work which starts outside the test's context, such as a test host which serves each request on threads of its own. With `DURABLE_STORAGE_TEST_LEAK_TRACES=1`, the check records each rental's call site, deduplicated and capped, and a failure reports the file and line of each outstanding page's call site. | Every test class of `Orleans.Durable.Storage.Engine.Buffers.Tests`, `Orleans.Durable.Storage.Engine.Wal.Tests`, `Orleans.Durable.Storage.Engine.Ledgers.Tests`, `Orleans.Durable.Storage.Engine.MetaStore.Tests`, `Orleans.Durable.Storage.CasPaxos.Tests`, `Orleans.Durable.Storage.Model.Tests`, `Orleans.Durable.Storage.Engine.Archive.Lsm.Tests`, `Orleans.Durable.Storage.Cloud.Tests`, `Orleans.Durable.Storage.Interop.Tests`, and `Orleans.Durable.Storage.Engine.Journals.Tests` not marked `[NoPageLeakCheck(reason)]`, which `PageLeakCheckCoverageTests` enforces in each; the MetaStore's tests dispose every entry they read, whose value is an `ArcBuffer` ([metastore §4](components/metastore.md#4-data-types)); every CsCheck property which rents pages checks each case in its own scope; every simulation, as the teardown check `SeededSimulation` registers after quiescence (§7.4) |
| `FileHandleLeakCheck.AssertClosed`, `SimulatedWalStorage.OpenHandleCount` | Once its owner is disposed, every file under a directory opens exclusively, and a simulated storage has no directory or file handle open. On Windows the check sees every handle; on Unix it sees the handles .NET opens, each of which holds an advisory lock, and not native ones such as RocksDB's | WAL and LedgerStore lifecycle and crash tests, the RocksDB state storage, `FileSystemMetaStore` and its lock file, and the CASPaxos SQLite cluster |
| `SimulationLeakCheck` | Once the scenario's owners are disposed, the work due at that instant runs without advancing virtual time, and then the cluster holds no runnable, waiting, or blocked work, no armed timer, and no unfinished operation; a failure reports Clockwork's pending-work snapshot. `SimulatedLedgerReplicaTransport` checks that a replica is disposed only once no exchange with it is in flight, and names any which is | Every simulation, as the teardown check `SeededSimulation` registers first (§7.4); `AssertQuiescent` checks a cluster a test replicas directly |

Mutant tests show that each check detects what it claims: a kept page fails `ArcBufferLeakCheck` and its traced report
names the rental's call site, also from work a test host runs through `Run` (`ArcBufferLeakCheckTests`); the
portable and, on Linux, io_uring WAL
storages fail the handle check while a file is open and pass it once disposed, and an undisposed simulated handle
leaves `OpenHandleCount` at one (`HandleLeakCheckTests`); a one-shot or periodic timer armed after disposal fails
the quiescence check (`SimulationLeakCheckTests`, `SeededSimulationTests`); a page a simulated scenario keeps fails the
simulation page check (`SeededSimulationTests`); and a replica transport which keeps a request's copy of its payload,
or a read answer which never reaches its caller, fails it on every seed (`SimulatedLedgerReplicaTransportTests`).

The checks are not applied where they establish nothing:

- **Pages:** `Orleans.Durable.Storage.Architecture.Tests`, `Orleans.Durable.Storage.Testing.Tests`, and
  `Orleans.Durable.Storage.Engine.Threading.Tests` rent no pages. In the covered projects, `IdentityTests`,
  `SlotTests`, and `RegisterKeyVectorTests` test values, and `PageLeakCheckCoverageTests` reflects over types.
- **Handles:** in-memory components open none; native handles on Unix are invisible to the check.
- **Quiescence:** only a Clockwork simulation exposes its pending work. Outside one, disposal tests establish that a
  component ends its background work: `LedgerQuorumDisposalTests` disposes a `LedgerQuorum` with an operation in flight
  and with a straggler outstanding.

### 11.2 Debug assertions

Clog's code states internal invariants with `Debug.Assert`, which a Release build compiles away; the Debug leg (§12)
builds the code under test in Debug, so its assertions run. Each test application's builder hook installs a trace
listener (`test/Durable.Storage/shared/Diagnostics/DebugAssertions.cs`) ahead of every other, once. A failed `Debug.Assert`,
`Debug.Fail`, or `Trace.Assert` throws `DebugAssertionException` at its site, with the assertion's message, its site's
stack, and its site's key: the first frame's method and IL offset. The listener records each failure with a sequence
number and the test whose execution context raised it. Attribution follows the context of the code which started the
work: work a test starts, on the thread pool or on Clockwork's scheduler lanes, is that test's, and a worker which may
serve several callers keeps no caller's context of its own ([conventions
§12](conventions.md#12-shared-workers-and-execution-context)).

An owner takes the records its test raised since the owner started, and each record fails exactly one owner. Tests run
in parallel, so the count of assertions raised in the process covers every test's: it serves only as the mark an owner
takes its own records from, `TakeRaisedBy(test, since)` or `FirstRaisedBy(test, since)`, and a test which expects no
assertion asks for none of its own since its mark. `DebugAssertionMarkTests`
(`Orleans.Durable.Storage.Architecture.Tests`) requires each member of the test sources to read the count at most once,
so none compares it before and after. The owners:

- **A seeded simulation on Clockwork** (§7.4) checks for a record after every step, before its other checks, and fails
  at the step which raised it, with its seed, report, history, and replay artifact. An assertion raised before the
  run's failure was set takes precedence over that failure and carries it as its inner exception. The run takes its
  remaining records when it ends, a minimization candidate's among them. Simulations run one at a time (§7.1): one
  which starts while another runs fails.
- **`SeededSimulation.RunAsync`** fails a run whose scenario raised an assertion, with its seed, replay command, and
  history. The assertion takes precedence over the scenario's own exception, which it carries as its inner exception.
- **The test** fails once its body has run, through an assembly-level `BeforeAfterTestAttribute`, with
  `RecordedDebugAssertionsException`: "N debug assertions were recorded in this test, whether or not they propagated",
  with the records as its inner exceptions. An assertion which propagated and failed the test directly therefore
  appears twice, the second time as a record.
- **The test assembly** fails at its cleanup with every record left over: one raised outside every test's execution
  context, or in a test class's `Dispose`, after the test's check; the report names each assertion's test, where it
  has one. Microsoft.Testing.Platform reports an assembly-cleanup failure as a failure of every test the assembly ran,
  named `[Test Assembly Cleanup Failure (<test>)]`. The check also fails unless the hook is still the first trace
  listener, and an assertion which fails after the check ends the process with `Environment.FailFast`.

An assertion which fails in a `finally` block or a `Dispose` while another exception propagates replaces that
exception, as any exception thrown there does: the assertion takes precedence, and its record preserves it whether or
not it propagates further. An assertion on a thread where nothing catches its exception ends the test host as an
unhandled exception, whose message and crash dump are the evidence (§11).

The leg's projects run every test they hold, including two whose outcome Debug code generation could change:

- `OneSeedsTraceAndScheduleArePinned` stays in the leg because a seed must take the same schedule in Debug as in
  Release; the snapshot is approved from the Release tests, never from the Debug leg.
- `AsReadOnlySequence_SingleSegment_DoesNotAllocate` stays in the leg, so an assertion on an allocation-free path must
  not allocate: its message is a constant.

The hook's evidence:

- `DebugAssertionsTests` checks each owner's report and that it leaves nothing recorded.
- `DebugAssertionCanaryTests` runs canaries in a child test host, as xUnit runs them: a test which swallows an
  assertion in work it started fails alone, with its message, and an assertion raised outside every test fails the
  assembly at its cleanup.
- `SeededSimulationTests` checks that an assertion which rewritten code raises and catches on the scheduler lane fails
  the run at that step: the probe never continues past its waits, and an invariant the same step violates gives way.
- `LedgerQuorumDebugAssertionTests` and `SeededSimulationTests` put a replica double which fails an assertion on every
  write under a real three-replica `Ledger`. The `Ledger` acknowledges the write, and the record still fails the test
  and the simulation.
- `BuildConfigurationTests` and `QuorumSimulationTests` check that the assemblies whose assertions the tests
  evaluate, Clockwork's rewritten copies among them, build in the tests' configuration.

The self-tests' and the doubles' source files define `DEBUG`, so these tests also run in the Release tests.

## 12. Gates and measurements

| Gate | Contents |
| --- | --- |
| Every change | Build on `net11.0`; formatting; unit, snapshot, property, and conformance tests; the simulation seed corpus within its budget; the Debug leg, which evaluates the debug assertions (§11.2); the affected models, the ledger model's quick tier when its specification or runner changed; trace validation of the ledger, archive-log, ledger map, and LSM traces (§5.1); golden vectors and fuzz corpus replay; the documentation checks |
| Every change, nightly | An incremental mutation run for changed core units; the ledger model's default set, its quick and nightly tiers, for a change to its specification or runner; in the nightly window after the change lands |
| Milestones | Seed exploration (`just explore`) and schedule exploration (`just explore-schedules`, every simulated conformance class's windows at `DURABLE_STORAGE_SCHEDULE_BOUND=deep`, §4.1); full mutation runs; fuzzing campaigns; emulator integration; extended models; end-to-end tests (`just e2e`) |
| Final validation | Soak runs; long end-to-end runs |

Every change passes its gate locally while CI is unavailable. Expensive work runs after hours, off the critical path:
mutation runs, seed exploration, fuzzing campaigns, extended model runs, and benchmarks which need a quiet machine run
in a nightly window, and a pull request lands without waiting for them. Their findings, such as surviving mutants, are
fixed in follow-up pull requests. Until the implementation is complete, the implementation plan's [decision
16](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md#decisions) defers them. `test/Durable.Storage/eng/validate.ps1` uses the pinned SDK, runs each selected project
once, and validates its xUnit report. `just check` covers the Release build, formatting verification, default tests, the
Debug leg, the documentation checks, the ledger model's quick tier when `test/Durable.Storage/specs/ledger-protocol` or its runner changed
(`just models-changed`), and the ledger traces' validation (`just ledger-traces`, §5.1), which ties a code change to the
model, as the archive-log, ledger map, and LSM traces' validations do (`just archive-log-traces`,
`just ledger-map-traces`, `just archive-traces`).
The quick tier holds the positive cases of about a million states or fewer, every mutant, and every witness, and
runs in about ten minutes on eight workers; `just models` adds the nightly tier's larger positive cases. `just
check-full` adds the ledger model's extended bounds and the Quint models. At milestones, `just explore <seeds>` runs the
simulation corpora over a seed range, `just ledger-traces <seeds>` validates their ledger traces, `just fuzz` runs the
fuzzing campaigns, and `just e2e` runs the Orleans composition's multi-silo scenarios (§10). `just mutation-since` runs
the incremental mutation run for changed core units, and `just mutation-core` the full one (§9). `just
ledger-map-traces` has a budget of five minutes of wall time; over its ten default seeds it took 154 and 167 s in two
runs on one developer machine loaded by other work: 24 s for the twenty simulations, 80 to 110 s for their replays, a
committer trace's 13 to 28 steps in 2 to 4 s and a transition trace's 42 to 75 in 4 to 15 s, and about 30 s for the
trace mutants. `just archive-traces` over its twenty default seeds took about 90 s in one such run: about 20 s for the
build, 8 s for the twenty simulations, 50 s for their replays, an LSM trace's 60 to 90 steps in 2 to 4 s, and about 10 s
for the trace mutants.

The Debug leg, `just test-debug` (`test/Durable.Storage/eng/validate.ps1 -Check TestDebug`), is the only gate which runs debug assertions
(§11.2); `just check` and `just check-full` include it. It builds in Debug each test project which sets `DurableStorageDebugLeg`,
a simulation project in `SimulationDebug`, and runs it; `just test` runs a simulation project in `SimulationRelease`.
The leg's projects are:

- `Orleans.Durable.Storage.Engine.Buffers.Tests` and `Orleans.Durable.Storage.Engine.Ledgers.Tests`, the tests of the
  code which holds debug assertions;
- `Orleans.Durable.Storage.Engine.Wal.Tests`, whose storage writes through those buffers, and the tests of the WAL's
  stop assertion;
- `Orleans.Durable.Storage.Fuzz.Tests`, whose corpus replay reaches the slot checkpoints' assertions;
- `Orleans.Durable.Storage.Engine.Journals.Tests`, the tests of the Journals' fold, admission, and `RocksDbJournalIndex`
  stop assertions, which hold debug assertions;
- `Orleans.Durable.Storage.Engine.MetaStore.Tests` and `Orleans.Durable.Storage.CasPaxos.Tests`, whose providers must
  never read a write's value after the write completes ([metastore §10](components/metastore.md#10-verification));
- the seed corpus, `Orleans.Durable.Storage.Simulation.Tests`, which runs that code under faults;
- the simulated conformance classes, `Orleans.Durable.Storage.Simulation.Conformance.Tests`, which judge that code by
  the specs of §4.1 within simulations.

A test project whose tests check that no pages are read after their buffer's release joins the leg too, since only
Debug builds poison a page as it returns to its pool, and a released page keeps its bytes in Release until it is reused
([buffers §2](components/buffers.md#ownership-model)).

`Orleans.Durable.Storage.Architecture.Tests` checks that these reach every component whose code under `src` holds a
debug assertion: a test project named for a component reaches it and what it reaches, and any other every component of
the projects it references. The leg has a budget of eight minutes of wall time run on its own on the reference machine.
Measured on the `net10.0` build, it took about five there: 4 min 53 s with up-to-date Debug builds and its processes at
high priority, of which the seed corpus took 1 min 52 s, `Orleans.Durable.Storage.Engine.Ledgers.Tests` 1 min 14 s, and
`Orleans.Durable.Storage.Engine.Wal.Tests` 1 min 9 s. A Debug build from scratch adds about 40 s.

`test/Durable.Storage/eng/validate.ps1` ends a test session which runs past its project's `DurableStorageTestSessionTimeout`. The timeout is 12
minutes, and 30 for the seed corpus and the simulated conformance classes, which run their tests one at a time and slow
several-fold on a loaded machine. The hang dump and each simulation's step, virtual-time, and livelock budgets
(§7.4) end a hang sooner.

The every-change seed corpus, `Orleans.Durable.Storage.Simulation.Tests` with its tests run one at a time (§7.1), has a
budget of three minutes of wall time run on its own on the reference machine, a 32-thread Intel Xeon Platinum 8370C with
128 GiB under Windows, and longer beside the solution's other test projects. A simulation cost about half a millisecond
per step there, nearly all of it Clockwork's scheduling, and every execution logs its steps and wall time (§7.4), so a
change which would exceed the budget moves seeds to exploration with `DURABLE_STORAGE_SIM_SEEDS` rather than growing the corpus.
Every change runs one to four seeds of each scenario, the seeds its coverage checks and mutants need; a replay check
reuses a corpus run (`SeededSimulation.AssertReplays`) or runs a smaller workload, since it runs its scenario three
times; and `just explore` and `just ledger-traces` (seeds 1 to 15 by default) run more. Fault theories which need a long
setup shape it to the fault, as the WAL-file deletion's smaller files do. On the `net11.0` build the corpus took 2 min
58 s there, 176 s of it in tests, of which the crash, history, and transition scenarios took 31 s, 27 s, and 23 s. Row
3.3's compactors, expiry schedules, and expiry mutants were measured on a developer machine, not the reference, with the
project run alone in Release: the corpus took 3 min 25 s there, against 3 min 22 s before them, and
`ArchiveDrainSimulationTests` 16 s, against about 23 s before them, of which its one seed took 7 s, the two drain and
drop mutants, each on that seed without compactors, 7 s, and the five expiry mutants, each once on its fixed expiry
schedule, 2 s; their second seeds and the scenario's other seeds run under exploration. Row 3.3b's per-child pushes,
64-byte blocks, and scripted dropper and push failures were measured against row 3.3's head on one developer machine
loaded by other work, the class run alone in Release, two runs of each interleaved: the class took 28.5 and 30.9 s
against 27.8 and 29.8 s; its seed run 12,979 steps in 12.4 to 12.6 s against 12,388 steps in 9.1 to 9.5 s; the drain and
drop mutants, without compactors, 6,173 and 8,487 steps each, unchanged, in 4.5 to 9.4 s; the expiry mutants 875 to
1,140 steps against 845 to 1,076, each under 1.1 s; and `never-drop` 649 steps against 618, under 2.1 s. Row 1.7 moved
the corpus to seed 1, and a scripted drain past its budget joined the dropper and push failures, so the budget's stop no
longer depends on the seed either; on the same loaded machine the seed ran 13,758 steps in 10.5 to 23.7 s, and the class
took 21.8 to 37.9 s over three runs. Row 3.4's sweepers, its held-commit schedule, which runs after the first two
rounds, a scripted drain against backpressure, which forces that path on seed 1, and the `sweep-unreferenced` mutant,
with the sweeper's trailer reads and deletes run through `Parallel.ForEachAsync` under the Clockwork pin which makes it
deterministic, take the seed's run from 13,758 to 18,121 steps, 19.9 s on the reference machine under load from other
work, and add the mutant's 776; its quiet measurement on the reference machine is pending. Row 3.5b's writer checksums
shifted the seed's schedule so that its random rounds took neither a superseded drain after a restart nor a history read
restarted on a dropped ledger, and two scripted steps now take both on every seed. Row 4.3's group committer took the
corpus from 373,653 steps to 382,034, 2.2% more, both measured on one developer machine loaded by other work: the
transition scenario's paths by construction added 5,665, the manifest committer's three seeds 1,182, the ledger map
committer's 560, the drain scenario's 891, and the history scenario's scripted read across a partial drain 83. The drain
scenario's failed compaction, which its random rounds took on seed 1 before, is now the scripted push failure, run
through the scenario's own compaction path. Moving the Clockwork pin from `07b7944` to `d26d877`, under which every
completion rewritten code performs runs as on a thread-pool thread and context-captured continuations are posted, took
the corpus's recorded runs from 382,034 steps to 387,621, 1.5% more, both measured on one developer machine loaded by
other work: the archive scenario's runs took 2,708 more, the transition scenario's 1,934, the quorum scenario's 1,209,
and the crash scenario's 357, and the ledger proposal and ledger scenarios' 561 and 78 fewer. Under it the crash
scenario's random rounds on seed 113 fail no local storage operation, so once the faults end a scripted step fails a WAL
write on one node on every seed. The step shifted the seed's schedule, which took its run from 19,016 steps to 14,150,
and the corpus to 382,839, 0.2% more than before the move. A second scripted step of the crash scenario, which restarts
a node holding a slot it answered corrupt so every ledger trace holds the event the restart-damage trace mutant changes
(§5.1), took seed 113's run from 14,150 steps to 16,428 and the replay seed's from 9,215 to 9,627, 3,102 steps in all:
the corpus took 364,138 after row 4.1b, against 361,036 without the step, measured on the same machine. Row 4.3's
controllers add `ControllerSimulationTests`, one seed of 19,493 steps in about 8.5 s and seven mutants of 554, 1,089,
150, 121, 121, 1,951, and 3,483 steps in about 4.5 s together; the class runs in about 14 s, measured on one developer
machine loaded by other work. Against the 5,915 steps and about 4 s of the scenario before the controllers' review, the
corpus grows by 18,644 steps and about 11 s. Each scripted step forces a path a review required, so the steps are the
evidence's cost: two rounds of two controllers over four nodes under faults; then, with every fault off, primary balance
spread over at least three nodes; two controllers' takeovers of one dead primary's ledger from one stale map value while
a manifest replace of the first is held at the store, of which one applies and one is superseded and the replace answers
only after both; a stopping node's hand-off, whose departures apply while it is given no ledger and is no new ledger's
replica, after which it restarts at a newer generation; a retirement, which the second controller's registry learns
late, in which the retired primary's first hand-off is lost unanswered and expires by `TransitionTimeout`, and which
asserts the hand-off of the retired primary's ledger and the repair of a ledger naming the retired replica, and, once it
is done, that a transition whose executor is retired while it recovers, chosen at the retirement's revision so that the
floor does not supersede it first, answers `Refused` (OR-I3); the retirement's gate, row 4.4's (OR-I4), asked of the
production gate (`ClusterController.Retirement`) after each of the first controller's passes, the gate checker of §3.2
holding every later step to the epoch it first answered open: the closed ledgers naming the retired node dropped, as
their drains would complete them, while the floor raises are held, so only the floor keeps the gate closed; then the
raises go on, the gate answers open once the first controller's passes raised the map's floor to the retirement, and the
second controller, paused since before the retirement, sends a roll of an open ledger which places the retired node,
chosen at the revision before it, whose rule holds against the map but for the floor and which reads `Superseded`; then
a new activation of the second controller, whose registry read returned the value before the retirement, below the map's
floor, with a takeover pending as an open ledger's primary reports it unserved, sends nothing and asks for no further
pass over three passes, and sends the takeover once its view reaches the floor (LG-I11); and cold siblings merged, the
silos reporting every ledger idle. The stop precedes the retirement because a node with a hand-off pending is no
replica: with `N = 3` of four nodes, a retired node and a stopping one together leave too few to place either's ledgers,
and the separate stop took the seed from 16,971 steps to 16,496. The timeout is 200 ms, about twice the longest
execution on seed 1 (83 ms, under faults); the run flags any reply which arrives at or after it, and counts the
controllers' expiries by the timeout, from late replies and lagging views alike, which must be the lost send's alone.
The retirement chooses, from the spread primaries, the node whose retirement leaves the fewest ledgers to repair, at
least one. Each mutant runs from the shortest scenario which reaches its oracle: the retirement-blind one retires the
first range's primary and fails at the blind controller's split; the floor-blind gate, a mutant of the production gate,
serves the first range, retires one of its replicas, rolls it out, opens at the drops while the raises are held, and
fails at the gate checker once the paused roll applies; the pause mutant, whose duplicate below the floor sends
transitions all the same, runs the same scenario and fails at the pause step's LG-I11 check; and the reply, landing, and
map mutants at their first check. The gate's step added 2,093 steps to the seed: the drops, the passes which raise the
floor, and the paused roll; asking the production gate, holding the raises, and the duplicate below the floor added 904
more. The floor-blind mutant took 10,983 steps through the whole retirement before its own shorter path. The scenario
with the review's paths first took 24,702 steps; two rounds rather than four, the held replace folded into the race,
which removes a cut-off and its rebalancing and merges, and the narrow retirement brought it here, while the forced
expiry adds about 1,400 steps of waiting. Eight rounds took the duplicate takeovers only by the seed's schedule, in
11,458 steps; the scripted race takes them on every seed. A fifth node, which would let the second controller's
membership view differ on the successor, cost 12,313 steps, most of it the rebalancing onto the joined node, so the
second controller's first send is lost unexecuted instead, which marks that successor failed for the ledger.

A membership change which left a repository unavailable for good in Enroute's deterministic simulation
(enroute-sh/enroute#7) is a regression case of the controller simulation,
`ControllerSimulationTests.MembershipChangesLeaveEveryLedgerServedOnNLiveReplicas`: with `N = 3` and every range served
over four registered nodes and an unregistered spare, one replica's node dies with its silo, an operator retires a
second replica's node, the primary of a ledger which names the first, and the dead node returns over its disk at a newer
generation or the spare replaces it at its ordinal, retiring it. Five orders run, each step reached by construction: the
retirement while the node is dead, at once, so its ledgers are taken over and the retired primary's handed off onto
degraded successors (OR-I5), then the return or the replacement; the return or the replacement first; and the
replacement registered while membership still shows the node live. Within a bounded virtual time every open ledger is
served on `N` live, non-retired replicas, each retired node's gate opens (OR-I4), the controllers' passes then send
nothing, no range is taken over more than twice, and a write to each open ledger is held by each of its replicas; the
OR-I5 checker and the history oracle judge every step. Clog leaves the degraded state once `N` nodes may be given work:
a retired primary's ledger which names no dead replica waits for `N`, served by its retired primary, and the repairs
wait with it. The five orders take about 12 s together on seed 1, and pass on seeds 1 to 8; a controller which never
rolls out a retired replica, one whose retired primaries' hand-offs never degrade, and one which repairs without end
each fail.

Row 4.6's failure-triggered drains add `FailureDrainSimulationTests` (OR-I23). Its scenarios force their failures by
construction, so the death runs three seeds and each scripted failure one, and each mutant runs once on seed 1, since
each takes the same path on every seed. Measured on one developer machine loaded by other work, with the project in
SimulationRelease: the death took 21,005, 18,640, and 18,035 steps on seeds 1 to 3, in 1.2 to 2.4 virtual seconds; two
deaths and a return 19,435; a return and a second death 31,648; a false suspicion 26,566; and a stop during the drain
11,219; the replay check runs seed 1's death three times; and the mutants `NeverDue`, `BoundAtTheMarker`, and
`OneDrainAStart` fail after 10,310, 25,688, and 21,006 steps. The class runs in about 31 s there.

The conformance kit's simulated cases (§4.1) run in a simulation test project of their own,
`Orleans.Durable.Storage.Simulation.Conformance.Tests`, beside the seed corpus, under a budget of 150 s of its wall
time, raised from 90 s once the ledger replica's, composition's, and fold oracle's classes measured about 120 s
together with the stores'. Since the move, measured on one developer machine with each project run alone in Release,
the conformance project's 35 tests took 132 s and the seed corpus's 225 took 60 s. Before the move, measured on one
developer machine with the classes run alone in Release, each case's simulation cost about 25 ms sampled and about 8 ms
per explored schedule; the object store's and the Azure archive-log store's classes, with the explorer's qualification,
take 45 s together, and the whole project took 88 s. The object store's 41 Accordant windows run 916 schedules, every
schedule within one preemption, in 7.8 s; the Azure archive-log store's 166 windows, whose SDK pipeline gives each about
fifty choice points, run every eighth window of racing writes explored, 19 windows in 829 schedules, and the rest under
their seeded schedules, in 14.4 s, and all 166 explored, 7,601 schedules, in about a minute under
`DURABLE_STORAGE_SCHEDULE_BOUND=deep`, which runs at milestones and in the nightly window. The race mutants' detection, on the same
machine: explored, the object store's check-then-write mutant fails its witness on every run at the 12th schedule, by
one preemption, and the Azure append-condition mutant fails the 7th of Accordant's windows of racing writes at its 14th
schedule, by one preemption; under seeded schedules, one simulation per case, the first is caught on 32 of 40 seeds and
the second on 3 of 158 windows; on the thread pool, which the fake Azure transport also serves, the second was caught on
2, 0, 0, 2, and 0 of the 166 windows over five passes, and in none of five passes of 40 sampled concurrent sequences,
the shape of `ArchiveLogStoreConformanceTests.ConcurrentGroupsConform`. The memory replica role's 175 Accordant windows,
in `SimulationRelease`, run 1,674 schedules, every schedule within one preemption, in 16.2 s, which brings the simulated
conformance classes to about 62 s; the replica's `write-checks-seal-before-landing` mutant fails its witness at the 4th
schedule, by one preemption, and under seeded schedules on 5 of 40 seeds. A LedgerStore's WAL appends and disk
completions give each of its windows about forty choice points, 36 on average without a restart and 45 with one, at
about 20 ms per explored schedule. Its 228 Accordant windows, the restart's among them, run every sixteenth window which
changes the store explored, 14 windows in 894 schedules, and the other 214 under their seeded schedules, in 22.3 s; all
228 explored within one preemption take 12,428 schedules in about three and a half minutes, and
`DURABLE_STORAGE_SCHEDULE_BOUND=deep`, up to two preemptions and 2,000 schedules a window, is estimated from those counts at under
an hour, which runs in the nightly window. Its 120 sampled cases take 3.1 s, and the class takes 41 s, which brings the
simulated conformance classes to about 87 s; the whole project ran in 107 s. The LedgerStore mutant, a replica wrapper
which checks the seal a step before it writes, fails its witness at the 4th schedule, by one preemption; seeded
schedules catch it on 17 of 40 seeds, and the same witness raced on the thread pool against a LedgerStore over unheld
storage on 215, 314, and 246 of 2,000 runs in three passes.

The composition, a `Ledger` of quorum two over three yielding memory replicas, gives a window up to 177 choice points,
about 90 on average; its 92 Accordant windows run every eighth window which changes the ledger explored, 11 windows in
1,027 schedules, and the rest under their seeded schedules, in 13 s, and all 92 explored take 8,479 schedules in about
66 s. Its 120 sampled cases over faulty replicas take 1.5 s. The fold oracle's 24 windows of appends and reads, up to 72
choice points each, run every second window which appends explored, 9 windows in 967 schedules, and the rest seeded, in
15 s, and all 24 explored take 2,212 schedules in 22 s; its 120 sampled cases take 1 s. The two classes take about 33 s,
which brings the simulated conformance classes to about 120 s. Exploring and sampling the composition found a defect
of the spec, which a seeded schedule of three calls, beyond the gate's one preemption, reached: a recovery which
overlaps the ledger's removal seals, and so creates, the replicas of the nodes the removal has not yet reached, which an
overlapping listing names, while the spec took only writes and seals as creating a replica
(`LedgerReplicaConformanceTests.AListingMayNameALedgerAnOverlappingRecoveryCreatedAsTheLedgerIsRemoved`). The
fold oracle's skewed replicas, which answered every read below the tail without a batch but the leader's, assumed the
leader held every completed slot; exploration completed an append on the other two first, so they now answer honestly
until the leader holds the slot. The acknowledge-before-land mutant fails the composition's witness at its 2nd schedule
and the fold oracle's at its 6th, each by one preemption; seeded schedules catch them on 29 and 13 of 40 seeds, and the
composition's witness raced on the thread pool, against a `Ledger` over memory replicas, on 39, 52, and 4 of 2,000 runs
in three passes.

The committer's fixed set of 12 histories of up to 12 steps, drawn from one seed so that its answers cover every kind of
outcome on every run (#373), runs 194 schedules, up to 43 choice points in a round, in 3.5 s. Its check-then-write
register mutant fails the 4th of the histories a fixed seed
draws, at its 16th schedule, by one preemption; seeded schedules catch that history on 25 of 40 seeds, and the
thread pool's 300 histories of up to 40 steps, whose writers yield on the real thread pool, catch it in each of three
runs. The fence mutants depend on fault sequences: the thread pool's histories caught `NoFence` in each of three runs
and `AnswerWhileHeld` in none, and 40 explored histories of up to 12 steps caught neither. The memory archive-log store
gives each of Accordant's 166 windows up to 11 choice points; every second window of racing writes, 79 in 775
schedules, runs explored and the rest seeded, in 6.3 s, and all 166 explored take 1,614 schedules in 12 s; its 80
sampled cases with faults take 0.6 s, and the memory object stores' 160 sampled cases 0.7 s. The two classes take about
13 s. The file-system object store over the recorded file system, which yields at each read and write, gives each of its
26 Accordant windows up to 36 choice points; with each of its two ways to publish a file, all 26 explored within one
preemption take 391 schedules, its appendable windows (`IAppendableObjectStore`) explore every schedule within one
preemption, its `SkipsReplaceLockMutant` fails by one preemption while the default schedule passes, and its 80 sampled
cases take 0.5 s; the class takes about 32 s on a developer machine. The chunk archive-log store over
`SimulatedObjectStore` (`SimulatedChunkArchiveLogStoreConformanceTests`) explores every eighth window of Accordant's
racing writes at the gate (and all 166 under `DURABLE_STORAGE_SCHEDULE_BOUND=deep`) and catches
`SimulatedObjectStore.ChecksBeforeWritingMutant` by one preemption, taking about 34 s; and `SimulatedGroupCommitterTests`
explores concurrent enqueue and merging, leader handoff, cancellation and disposal, failure propagation, and
`GroupCommitter.RecordsSequenceAfterIoMutant` in about 8 s. `SimulatedJournalStoreConformanceTests` explores 28 curated
concurrent windows spanning empty, cross-key, warm and cold populated, limit-reached, deleted/expiry-scan, taken-over,
fenced, and crash-recovered states (up to 105 choice points in one window): at the gate, every third window (10 windows,
815 schedules) runs under `WindowScheduleExplorer` and the remaining 18 under seeded schedules in about 20 s, alongside
100 seeded 3-replica quorum cases (2.4 to 4.1 s) and the two admission race-mutant witnesses (`DecidesWhileLoading`,
caught at its 3rd schedule by one preemption over up to 55 choice points, and `RejectsWaiting`, caught at its 4th
schedule by one preemption over up to 59 choice points, both passing the default schedule and caught on 40 of 40 seeded
runs; about 1 s each), taking about 22 to 27 s in `SimulationRelease` and 35 s in `SimulationDebug`; all 28 explored
under `DURABLE_STORAGE_SCHEDULE_BOUND=deep` take about 1,900 schedules within one preemption and run at milestones and in the
nightly window. `just explore-schedules` (`test/Durable.Storage/eng/validate.ps1 -Check ExploreSchedules`) runs every simulated conformance
class, or those a class filter names, under `DURABLE_STORAGE_SCHEDULE_BOUND=deep`, at milestones and in the nightly window; the
file-system store's class alone explores 4,787 schedules in 71 s under it.

Row 2.3b D adds `JournalSimulationTests`, measured on one developer machine loaded by other work with the class run
alone in Release. Its corpus is one seed, of 31,667 steps in 13 to 19 s. Each of its ten mutants runs no random work,
only the scripted step which forces its path, under a two-second progress bound, so each fails within 474 to 4,014
steps, 21,851 steps in all; the four control runs, each such step alone without a mutant, take 10,313. The replay check
runs its smaller workload three times on a second seed, 16,502 steps each. The class runs 113,337 steps in 47 to 65 s,
and takes the corpus's recorded runs from 678,870 steps to 792,207, 16.7% more. Before the review's rework, three
corpus seeds of 27,184 to 32,031 steps each and mutants which ran the whole scenario, one of them waiting out a minute
of virtual time for its progress check, took 309,050 steps and 214 s. Every path the corpus's coverage check names,
`Moved` among them, is forced by construction on each seed, so further seeds run under exploration.

Row B.12 runs the fault plan (§7.3) in every workload of `JournalSimulationTests`, so each mutant meets the faults
before its scripted step. Measured on one developer machine with the class run alone in `SimulationRelease`, before and
after the change: the corpus seed takes 43,513 steps, recorded in about 2.7 s, where it took 30,031 in 2.1 s; the
replay check 31,347 steps per run, where it took 17,202; the ten mutants 114,056 steps and the four control runs 50,822,
where they took 22,085 and 10,416. The class runs 302,432 steps, where it ran 114,138, in about 28 s, where it took 16 s.
Every fault takes its path on each seed by construction, which seeds 1 to 40 confirm.

`test/Durable.Storage/eng/coverage.ps1` collects Cobertura through Coverlet, requiring a report from each project it measures, reports CRAP
scores at least 30, and renders ReportGenerator output; simulation projects collect no coverage, since Coverlet would
instrument the IL which Clockwork rewrites. Branch coverage, CRAP, and mutation scores identify risk; meaningful
assertions and explored state spaces establish correctness.

Permanent microbenchmarks live under `test/Durable.Storage/benchmarks/Orleans.Durable.Storage.Benchmarks`. A new benchmark first completes a
filtered `--job Dry` run; optimization claims compare explicit baselines and report throughput, latency distributions,
and allocations. Baselines measured before row 0.17 of the [implementation plan](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md) ran on
`net10.0`, so an optimization re-measures its baseline on `net11.0`. Measurements of a composition report configuration,
workload, physical growth, and failure outcomes separately.

The documentation checks, `just docs` (`test/Durable.Storage/eng/check-docs.ps1`), run three checks over every Markdown file under
`test/Durable.Storage` which the repository tracks or which is new and not ignored, and report the failures of all three.
markdownlint applies its rules as `.markdownlint.jsonc` configures them, with prose wrapped at 120 columns; the copied license
texts keep their upstream form and skip it. remark-validate-links checks that
every relative link names a file which exists and every fragment a heading of its target, under GitHub's heading slugs.
Every `mermaid` block parses with the Mermaid release GitHub renders diagrams with, which reports each syntax error
GitHub would show in place of the diagram ([build and packaging §2](build-packaging.md#2-source-and-dependency-policy)).
A documentation-only change passes the documentation checks and `git diff --check`, and its review checks decision and
format coverage; code builds and runtime tests qualify behavior changes.

## 13. Qualification boundaries

| Boundary | Current status and owner | Required evidence or decision |
| --- | --- | --- |
| Ledgers, the LSM, Journals over ledgers, Orleans over ledgers | In code ([overview §5](README.md#5-implementation-status)): Ledgers (rows 1.1–1.7), the LSM over the memory, file-system, and cloud object stores with its section metadata and block caches (rows 2.1, 3.1–3.5b, 5.3 A, and 5.3 B), Journals over ledgers (rows 2.3a, 2.3b, and 3.4b), and the Orleans composition (rows 4.1–4.6 and 4.9, and row 4.7 in part). *Not yet in code*: the warm-state and key-index fast path (row 5.2) | Each remaining row with the tests its owner's Verification section plans, mapped onto the models' actions (§5.1) |
| Target forms of implemented components | The primary claim's lifetime (LP-I15, with Ledgers) is *not yet in code* ([invariants §13](invariants.md#13-invariants-without-evidence)) | The planned tests in each owner's Verification section |
| Deterministic simulation on Clockwork | Clockwork is the repository's simulation family, and the kit (§7.1, §7.2) runs LedgerStore nodes with overlapping crashes, restarts, media damage, pauses, partitions, and lost and duplicated messages under `SeededSimulation`, with step checks, quiescence and page checks at teardown, client histories, ledger traces (§5.1), replay artifacts, minimization on request (§7.4), and a liveness bound on recoveries once faults heal (§7.1). Clockwork drops Polly's and Accordant's Authenticode signatures when it rewrites them (warning `CWR0104`). Clockwork's simulation-thread check fails work which reaches a simulation from a thread outside its scheduler carrying the simulation's context at run time; `SimulationClosureTests` keeps out statically the unrewritten libraries which start such work, and the work which carries no context, such as change-token callbacks, which the run-time check does not see, and `ClockworkCompatibilityTests` enforces the §7.1 compatibility and `runtime-async` rules across every rewritten assembly in the closure and every Clog build file (§7.1). The kit's object store, `SimulatedObjectStore`, runs the manifest committers' history oracle ([archive LSM §10](components/archive-lsm.md#10-verification)) and passes `ObjectStoreSpec` with unknown outcomes through the conformance kit (§4.1), while the in-process double `FaultInjectingObjectStore` (`test/Durable.Storage/shared/Archive/Lsm`), which fails calls, leaves outcomes unknown, lands held writes later, and answers retried writes, passes the object store conformance suite. The Journals simulation (`JournalSimulationTests`) runs §7.3's workloads beyond one ledger's quorum under a fault plan which, on every seed, crashes a journal process with its node and restarts both, partitions a node and pauses one, loses and duplicates messages and loses journal answers, damages a restarted node's media, leaves ledger map writes' outcomes unknown, races two committers on one transition, and interrupts an executor once it submits. *Not yet in code*: the failure-detection seams; clock jumps and duplicated controllers, which need the controllers of Ledgers; under the Journals, MetaStore outages, object-store outages and ambiguous outcomes, which need an LSM or archive-log store beneath the simulation in place of the memory store, and a partition of one journal process from one node, since the simulation's processes send their replica requests from one client node; and of §7.3's workloads, maximal bodies, compactions, and sweeps | The remaining seams, faults, and workloads with the components which use them |
| Explored concurrency | The conformance kit explores the concurrent windows of the `IObjectStore` spec over `SimulatedObjectStore`, of the `IConditionalRegister` spec over `SimulatedConditionalRegister` and over `FileSystemMetaStore` on the recorded file system, of the `IArchiveLogStore` spec over `AzureArchiveLogStore` on the fake transport and `ChunkArchiveLogStore` on `SimulatedObjectStore`, and of the `ILedgerReplica` spec's replica role over the memory provider's replicas and over a LedgerStore across restarts and its composition role over a `Ledger`, and of the `LedgerSpec` fold oracle, the memory archive-log store, the file-system object store (including `IAppendableObjectStore`) over the recorded file system, the ledger map committer's generated histories, the manifest committer's generated histories, `GroupCommitter` (`SimulatedGroupCommitterTests`), and the `JournalStoreSpec` over the simulated `JournalStore` composition, each case in a simulation of its own, with a race mutant per contract (§4.1); the gate explores a stride of the Azure and chunk archive-log stores', the LedgerStore's, the composition's, the fold oracle's, the memory archive-log store's, and the journal store's windows and runs the rest under their seeded schedules, and the memory object stores run under seeded schedules alone. Every other contract's concurrent groups run on the thread pool, where their interleaving neither replays nor shrinks: outside the rewritten closure, the Orleans replica transport, the node registry committer (which lives in `Orleans.Durable.Storage` over Orleans runtime types that `SimulationClosureTests` keeps out of the rewrite closure, while its shared `GroupCommitter` engine in `Orleans.Durable.Storage.Engine` is explored directly in `SimulatedGroupCommitterTests` and through both the ledger map and manifest committers, and its `IConditionalRegister` seam through `SimulatedConditionalRegisterConformanceTests`), the cloud stores on their emulators, and the file-system object store and file-system MetaStore on the operating system's file system, whose concurrency is the real platform's. The file-system MetaStore's race mutant is the simulated conditional register's, the same contract's, since it serializes conditional writes under a lock no mutant switch opens. The committer's fence mutants need fault sequences, not interleavings, which only the generator and a scripted schedule reach. The explorer chooses among runnable operations, at Clockwork's controlled scheduling points, not between memory accesses, and wakes a semaphore's or monitor's waiters in order, since Clockwork exposes a waiter's choice only to its own strategies ([Clockwork#78](https://github.com/ReubenBond/Clockwork/issues/78)); the gate bounds each window at one preemption | Fence-mutant histories drawn toward the fault sequences they need; a Clockwork strategy hook for waiter choice; race-exploration instrumentation where a contract's races lie between `await`s |
| Contract specs | The kit (§4.1) and the `ILedgerReplica` spec (`LedgerReplicaSpec`) over the memory and LedgerStore replicas and a `Ledger` are in code, with the recovery flag, seals, contiguous-tail reads, the `Ledger`'s recovery, the LedgerStore's restarts, and two ledgers per node with node drops, listings, and tombstones through each node's `ILedgerStore`; the ledger spec (`LedgerSpec`) checks a `Ledger` and its `LedgerWriter` (`LedgerConformanceTests`); the `IConditionalRegister` spec (`ConditionalRegisterSpec`) checks the in-memory conditional register (`MetaStoreModelTests`), `SimulatedConditionalRegister` with unknown outcomes and `FileSystemMetaStore` over the recorded file system in explored Clockwork simulations (`SimulatedConditionalRegisterConformanceTests`), and judges the file-system MetaStore's crash states (`FileSystemMetaStoreCrashStateTests`); the `IObjectStore` spec (`ObjectStoreSpec`) checks the memory and file-system object stores and the object-store double, and judges the file-system object store's crash states; the manifest committer's oracle is a history oracle (archive §10); the Journals spec (`JournalStoreSpec`) checks `JournalStore` over memory replicas and the memory archive store, with takeovers, a write fenced by one, and a crash which leaves a write `Ambiguous` (`JournalStoreConformanceTests`), and over the Clockwork-rewritten simulated composition with explored concurrent windows, seeded 3-replica quorum cases, and admission race-mutant witnesses (`SimulatedJournalStoreConformanceTests`), while its drains, compactions, and sweeps as step functions are *not yet in code*; the other specs of §3.1 are *not yet in code* | One spec per contract, each landing with its component |
| Trace validation | The ledger model's events are recorded in simulation, checked by C# oracles, and validated against the ledger model by TLC over the quorum, crash, and `Ledger` corpora, each trace with a recovery's close of the map and a `LedgerWriter`'s completions and stops mapped, and the archive-log drain simulation's traces are replayed against `ArchiveLogs.qnt` by `quint test` (§5.1); an archive-log trace follows one key, over the memory storage rather than the fake Azure transport, with failures only before a request lands; the ledger map committer and transition simulations' traces are replayed against `LedgerMap.qnt` by `quint test`, with eight trace mutants (§5.1), over simulations which drain, trim, and read nothing, so a drop's drain is the model's and trims and reads are untraced; the LSM simulation's traces are replayed against `Archive.qnt` by `quint test`, with five trace mutants (§5.1), over a scenario of its own which follows one key over two ledgers of one lineage, since `ArchiveDrainSimulationTests`'s four keys over lineages which split and replace again lie outside the model's two ledgers, and a compaction output of a partition without the key maps to no step. Nodes crash while a replica serves a recovery's seal and while a recovery runs in the quorum and crash simulations, and a record which became durable without a logged answer is delivered silently at the event which first shows it; a scripted run fires each such delivery, a write's, a copy's, and a seal's, at an answer its crashed process gave. Each node's media damage is logged, and a silent corruption is allowed only at a node a logged damage named | Archive-log traces of several keys, over the fake Azure transport, with requests which land unanswered; ledger map traces of a simulation which drains, trims, and reads; LSM traces of several keys over lineages which split and merge; trace validation over exploration's seed ranges |
| Mutation testing | Stryker.NET runs over every component but `Orleans.Durable.Storage.CasPaxos` (§9); the baselines were measured on the `net10.0` build, and no run has yet mutated the `net11.0` build. `Orleans.Durable.Storage.Engine.Ledgers` and `Orleans.Durable.Storage.Engine.Ledgers.Store` hold the 78.5% their combined project measured before row B.8b split it as provisional floors until their first full runs; the append path, which row B.8c folded into `Orleans.Durable.Storage.Engine.Ledgers`, met its 80% threshold as its own project and keeps that floor until the first full run (§9.1). The ledger projects' 488 undetected mutants lie mostly in `SlotTable`, `LiveReplica`, `SlotLeafCodec`, `SlotCheckpoints`, `LedgerStore`, and the quorum's tail-check and repair paths, much of which row B.3 replaced; the mutants of the methods Stryker removes whole are untested (§9.1). Stryker.NET 5.0.0 reuses a test host from one mutant to the next, so a static initializer keeps the values of the mutant active at its first use in the host: `Orleans.Durable.Storage.Engine.Ledgers`'s configuration leaves out `LedgerWriterMetrics`' instrument creation's mutants, and the ledger projects' three mutants of static initializers survive without a sound test (§9). Of the Quint models' simulation guard mutants (§9), ten of `Archive.qnt`'s eleven mutants are caught by `ArchiveDrainSimulationTests` on every seed (row B.16; the eleventh, `snapshot-before-list`, is bounded below); the other models' simulation oracles do not yet have a full guard-mutant set | Once the implementation is complete ([plan decision 16](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md#decisions)), a full run on the `net11.0` build, which re-measures the baselines; each survivor of the ledger projects in the code those rows keep killed by a test or recorded as equivalent, starting with the quorum's `>= QuorumSize` feasibility boundaries and the catalog's header-length boundary; the ledger projects' thresholds raised to 80% as they close, and every threshold as its suites mature; the removed methods mutated, once Stryker attributes definite-assignment and missing-return errors to their mutants; static initializers mutated, once Stryker tests each of their mutants in a fresh host; a mutant of the guard each remaining model's Quint mutant disables, built through the simulation project so that Clockwork rewrites it, which its simulation catches |
| `Archive.qnt` `snapshot-before-list` mutant | `Sweeper` enforces a two-sweep grace ([archive LSM §8.5](components/archive-lsm.md#85-garbage-collection)): an object is deleted only when a second sweep's own listing and manifest read again find it unreferenced and its condition false. Because the deleting sweep's listing strictly follows the marking sweep's listing, it also follows any commit that existed when the object was marked; reading the manifest before listing (`Sweeper.SnapshotBeforeListMutant`) can wrongly mark an in-flight compaction output whose inputs were committed during the listing, but cannot delete a referenced or pending object or violate LSM-I6 in production without also disabling the two-sweep grace. No single-guard mutant of `Sweeper` can therefore violate LSM-I6 via `snapshot-before-list` in `ArchiveDrainSimulationTests` | `SweeperTests.ASweepReadsTheManifestAfterItLists` pins the manifest-after-list ordering directly on the mark verdict; if the sweeper ever removes the two-sweep grace, add a `snapshot-before-list` simulation mutant to `ArchiveDrainSimulationTests` |
| Decoder fuzzing | Targets for WAL header pages, data pages, and its reader over a whole file, the `CLFM` register file, the `CLSC` catalog, the WAL record, `CLSI` leaves, roots, and checkpoint loading, the `CLLB` batch, the `CLAM` manifest, the store's record, and the `CLAR` run object, with corpora replayed by each layer's tests on every run (§6.1). Campaigns run on x64 Windows and Linux with the pinned drivers; SharpFuzz instruments the `net11.0` harness, on which a 90-second `archive-manifest` campaign and 60-second `archive-log-token` and `archive-log-metadata` campaigns have run; a process-wide allocation bound is checked in the harness's own process, and in tests the decoding thread's bound and the deadline apply | A campaign per target on the `net11.0` harness; a target with each decoder still to come: segments (`ledgers-segment`) |
| Crash-state enumeration | The recording file system, the persistence model, and enumeration of the cluster claim, the LedgerStore's directories, catalog, checkpoint, and WAL writes, the WAL's directory publication, the portable storage's files in both shapes, and the file-system MetaStore's writes and reads are in code (§7.5). Each enumerated state follows one crash of the operating system, preceded by at most one process crash, and a seeded sample of states adds a crash of their own recovery. The io_uring storage's file I/O lies outside `WalFileSystem`. The journal index creates its directories and files outside the recorded file system. Seal records are enumerated with the seal's own creation of a replica; a write, a recovery write, and a seal create through one path, whose create record held beneath later records is enumerated. The file-system object store's creates, replaces, deletes, reads, and listings are enumerated through both publish paths | The io_uring storage's file I/O recorded through `WalFileSystem`; a recording double beneath the journal index's RocksDB environment |
| Local storage failures | Every storage seam reports through `FatalErrorHandler` (conventions §14): `WalFileSystem` and its files, injected through the recording and intercepting file systems and the simulated storage; the io_uring storage's files and ring, injected through hooks beneath the engine (a failed completion, a failed submission, a failed truncation) and, on Linux, a real failed open; and `RocksDbJournalIndex`, whose seven sites are injected through a hook in its guard and whose classification of RocksDB's statuses is tested with real errors at open. Each defect site is injected through a hook of its component. `FatalErrorHandlerTests` run the handler in a child process, which exits at once with the site's code, with a working, failing, or stalled standard error, and writes nothing more | A RocksDB environment which fails a chosen write, sync, or read beneath the engine; device errors beneath the io_uring ring rather than above it |
| Golden vectors | Vectors pin the formats implemented as specified ([formats §6](formats.md#6-golden-vectors)); segments and the entry record without its version byte gain theirs with their implementation, as archive-log tokens, metadata, and store records have, and code which implements a superseded representation keeps its tests until the implementation replaces both | Vectors in both directions and every rejection, written with each codec |
| Model scope | `LedgerMap` uses one- and two-bit keys and lands each append atomically, an abstraction of the ledger protocol which the ledger model's `RefinesAtomicLedger` establishes; `Archive` models one key, so partitions and the LSM's geometry are absent, as is `DrainedBelow`; `LedgerDrop` models one address of one ledger; LG-I5's proposal ordering is unmodeled; the Quint ledger protocol model checks one ledger of at most three positions (five with `K = 2` for its bound's mutant, which a seeded simulation finds) and five replicas, with larger bounds and extended concurrency configurations in its extended tier ([sizes](../../specs/ledger-protocol/README.md#bounds)); `LedgerDrop` checks three replicas by default and five in its extended case ([scope](../../specs/ledgers/README.md#scope-of-the-result)) | Tests of the unmodeled mechanisms against the history oracle (§3.3); model extensions when a mechanism changes a commit, drop, or sweep rule |
| Contiguous-tail reads | Reads below the tail from one replica at a time, with the preferred replica and bounded hedges, are in code (row 4.1b), checked against a read of every replica by a property and by both conformance oracles, which reach the fallback past a hole and the hedge ([LP-I10](components/ledger-protocol.md#5-invariants)); read repair of the preferred replica, byte-range reads forwarded to the replicas, and the primary's explicit contiguous-tail writes while idle are in code (row 5.1), checked by scripted tests with a mutant per mechanism, an Orleans case in which a replica retains a tail across callers on two silos, the quorum simulation's traces, which TLC validates with the explicit writes as the model's `WriteTail`, and a scripted read repair's trace, which TLC validates with the repair as a fetcher's `ReadBelowTail` ([LP-I11](components/ledger-protocol.md#5-invariants)); the ledger model carries contiguous tails on writes, repeats, seals, recovery writes, and the primary's explicit contiguous-tail writes, and checks single-replica reads with fallback and read repair and drains below a learned tail at `K` of 1 and 2 (`BelowTailDecided`, with the `eager-tail` mutant, in which a write carries one past the highest quorum-acknowledged address, and `fetch-stored-tail`) | The randomized simulations read below the tail with no preferred replica, so only the scripted run's trace holds a repair; row 4.7's evidence measures hedges per read over the Orleans transport for 1 MiB and 32 MiB batches while writes queue to the same node, and row 5.4 the hedge rate and decided-read p99; a decided read over the memory transport copies its batch from one replica (`LedgerReaderTests.ADecidedReadCopiesItsBatchFromOneReplica`) |
| Loss of a ledger's quorum | More than `Q − 1` lost replicas can lose an acknowledged entry, and more than `N − Q` unavailable ones leave a ledger unwritable, or fewer than `max(Q, N − Q + 1)` available ones unrecoverable ([ledger-protocol §6.5](components/ledger-protocol.md#65-liveness), [system model](system-model.md#3-failures-tolerated)); recovery is [deferred](deferred.md#recovering-a-range-after-a-ledger-loses-its-quorum) | An explicit loss protocol which closes the ledger without a recovery coverage, identifies the lost slots, and records operator acceptance |
| Instance-local NVMe disks | The profile, its rules, and its procedures are specified ([orleans §4](compositions/orleans.md#4-restrictions), §8; row 4.8 A), and change nothing a silo does, so the composition's tests cover it as they cover persistent volumes; no cloud is qualified and no measurement made, so no deployment adopts it. A whole-cluster stop which releases the instances is outside the model until row 4.8 B, after row 7.5, rolls every range into object-store ledgers before it, with a multi-silo stop and restart over empty roots | For each cloud, before a deployment there adopts the profile: (1) the events the disk survives and those which release it, from the cloud's documentation and a live run which writes and fsyncs a known pattern through the WAL's storage, brings each event the cloud lets a tenant bring (a reboot in place, an operating-system shutdown without deallocation, a stop, a simulated maintenance or redeploy), and checks the bytes and the node identity after it, every event the run cannot bring, a host failure or power loss among them, taken from the documentation as releasing the disk unless the cloud states otherwise; (2) fsync durability: the cloud's statement that a flushed write survives the host's power loss, by power-loss protection or a cache the flush empties, since no tenant can cut a host's power; (3) the fault domains a deployment can spread instances across, their number at least `N`, and how a silo learns its own for `FailureDomain`; (4) a measurement against the cloud's persistent volume at a comparable cost: the WAL's fsync latency distribution and throughput and the journal write path's (row 5.4's benchmarks), takeover and recovery times, and the cost per node and per GiB, with the expected instance moves over a cluster's life against the node registry's identities ([orleans §8.1](compositions/orleans.md#81-resource-and-request-cost-model)). A cloud qualifies when (1) shows every planned stop of the profile keeping the disk, (2) holds, and (3) gives `N` domains; the profile is adopted there when (4) favors it |
| Object-store ledger durability | An [object-store ledger](components/object-ledgers.md) keeps one copy, so its acknowledged entries not yet drained have exactly the object store's durability: a zone's loss on a single-zone store (S3 Express One Zone, Azure LRS, Cloud Storage zonal buckets) loses them. The operator chooses the store; a silo records the redundancy the operator states, which nothing verifies. A corrupt or lost batch below a ledger's end blocks its recovery and so its close, as a lost quorum does ([deferred](deferred.md#recovering-a-range-after-a-ledger-loses-its-quorum)). *Not yet in code* | A decision per deployment, recorded with its store; the loss procedure of the lost-quorum row, covering a single copy |
| Azure seals against appends in flight | An appendable object's "no append lands after `Sealed`" rests on `x-ms-if-tags` on `Append Block` being evaluated atomically against a concurrent `Set Blob Tags`, and a seal's answer for a non-appendable object on `x-ms-if-tags` on `Set Blob Tags` itself ([object store §9.1](components/object-store.md#91-azure-appendable-objects)). Azurite runs the race and passes, but ignores the tag condition on `Set Blob Tags`, so the store reads the blob's type before it seals; neither precondition is established until a live account runs them | `AzureAppendableObjectFidelityTests.NoAppendLandsAfterASealAnswers` and `ASealStopsEveryLaterAppend` on a live account (row 7.6) |
| Object-store ledger preconditions | The object-store ledger rests on its store's preconditions ([object-store ledgers §3.1](components/object-ledgers.md#31-from-the-object-store)): atomic create-if-absent, empty objects, CRC-32C metadata, strongly consistent reads and ordered listings (including `S3ObjectStore` directory-bucket mode's hierarchical layout over unordered S3 Express directory buckets, [object store §9.2](components/object-store.md#92-s3-directory-buckets)), atomic offset- and tag-conditioned appends on Azure, an atomic tombstone replace (a `Put Blob` replacing an append blob whole), and no lifecycle rule on its prefix. Azurite models blob index tag conditions only in part (the Azure archive-log row); S3 Express listings have no qualified emulator and are exercised against `FakeS3DirectoryBucketService` until row 7.6's live run. Delayed writes recreate objects after a drop, which later sweeps delete. Per-slot and append ledger storage, S3 directory-bucket mode, per-precondition fidelity suites with live-account harnesses (`S3ObjectLedgerFidelityTests` on `DURABLE_STORAGE_S3_LIVE`, `S3ExpressObjectLedgerFidelityTests` on `DURABLE_STORAGE_S3_EXPRESS_LIVE`, `GcsObjectLedgerFidelityTests` on `DURABLE_STORAGE_GCS_LIVE`, and `AzureAppendableObjectFidelityTests` on `DURABLE_STORAGE_AZURE_LIVE`), four-way metamorphic equivalence (`ObjectLedgerMetamorphicTests`), and in-memory microbenchmarks (`ObjectLedger*Benchmarks`) are in code (rows 7.2–7.6); live cloud qualification runs and live per-provider latency measurements await cloud credentials | Run each fidelity suite (`DURABLE_STORAGE_AZURE_LIVE`, `DURABLE_STORAGE_S3_LIVE`, `DURABLE_STORAGE_S3_EXPRESS_LIVE`, `DURABLE_STORAGE_GCS_LIVE`) on its live provider account and record the results and per-provider latency distributions; each provider remains unqualified until its live run passes |
| Loss of every catalog checkpoint | A node's tombstones live in its catalog checkpoints once the WAL releases their drop records, and the store keeps at least two valid ones ([LS-I4](components/ledgerstore.md#5-invariants)). Losing or corrupting every one is a detected node loss, counted against its ledgers' `Q − 1` budget ([system model §3](system-model.md#3-failures-tolerated)): the store refuses the open by [LS-I5](components/ledgerstore.md#5-invariants)'s rule, and the disk returns as a new node. A node which reopened without its tombstones under its identity could recreate a dropped ledger's replica, as the ledger model's assumption case `assumption-tombstone-loss` shows (LG-I7), so detection, not the `Q − 1` budget, carries this case. | The tests of [ledgerstore §10](components/ledgerstore.md#10-verification) and the crash-state oracle (§7.5): with every catalog checkpoint deleted, no state reopens the node without a tombstone it recorded |
| Coordinated restore | Node catalogs, the ledger map's epoch and `NextLedgerId`, watermarks, archive-log positions and drain markers, and the objects the sweepers deleted are each monotonic, and each store relies on the others ([ledgers §8.5](components/ledgers.md#85-replica-lifecycle-on-a-node), [LSM §8.5](components/archive-lsm.md#85-garbage-collection)) | A procedure which restores node roots, the MetaStore, and the LSM prefix or archive-log container from one coordinated cut; restoring any one alone, or a distributed restore, stays unsupported until qualified |
| Fast CASPaxos durability | Voters and proposers persist their state in SQLite, and `CasPaxosSqliteTests`, `NativeConsensusTests`, and `OrleansNativeConsensusTests` reopen them, but no test restarts an acceptor which lost its unsynced state. Every start creates each database's directory under its operator-created parent and flushes the parent, and each database flushes its own directory. A failed directory flush, acceptor transaction, admission record, or ballot reservation is reported through `ConsensusStorageFailures` and stops the process with code 117 or 118 (`ConsensusStorageFailuresTests`, `SqliteVoterRegistryTests`); the real stop is qualified with the other sites ([metastore §8.1](components/metastore.md#81-fast-caspaxos-semantics)) | An acceptor restart which loses unsynced writes, judged by the Accordant `IConditionalRegister` spec ([MS-I1](components/metastore.md#5-invariants)) |
| Fixed CASPaxos membership, disk replacement, allocator rollback | [MetaStore](components/metastore.md#81-fast-caspaxos-semantics) states the retained-state boundary; a voter restarted on a fresh database under its old identity is refused by admission (`AVoterWhoseStateWasLostIsRefused`, `OrleansNativeConsensusTests`); membership change and voter replacement are [deferred](deferred.md#fast-caspaxos-membership-change) | A procedure preserving every acknowledged promise, value and version, and allocation; detection of a rolled-back proposer allocator |
| Blocked archive-log drains | A key whose archive-log write no retry can make succeed, such as one whose log is corrupt with no valid copy in the provider's own recovery or whose name the provider refuses, stops its ledger's drain, its drop, and its descendants' drains ([archive logs §6](components/archive-logs.md#6-faults-concurrency-and-cancellation), [orleans §5.9](compositions/orleans.md#59-resource-governance)); the operator procedure restores what the provider kept, and loses the key's drained rows otherwise | A procedure, with operator acceptance, which unblocks the ledger while losing only the named key's rows, and its tests against the history oracle |
| Archive content at rest | Archive-log content carries only the store's own transport checksum, computed from the verified batch pages, and each base the MD5 its generation records, which every read checks; nothing Clog-specific detects corruption of appends at rest, which reads return as stored, as settled at design sign-off ([JS-I5](components/journals.md#5-invariants), [archive logs §9.1](components/archive-logs.md#91-azure-append-blobs-azurearchivelogstore)) | A decision to add an end-to-end checksum at rest, or the store's own at-rest integrity qualified |
| Restoring an older archive-log version | An operator who restores a corrupt log from the provider's recovery to a version older than its last held write loses the writes of segments whose ledgers dropped ([LOG-I6](components/archive-logs.md#5-invariants), [archive logs §6](components/archive-logs.md#6-faults-concurrency-and-cancellation)) | A procedure with operator acceptance which names the lost commands, and its tests against the history oracle |
| Azure archive-log preconditions | The Azure storage rests on the preconditions of [archive logs §9.1](components/archive-logs.md#91-azure-append-blobs-azurearchivelogstore): `x-ms-if-tags` evaluated atomically with `Append Block` and `Put Blob`, `appendpos` honoured with it, 100 MiB blocks, one-version reads, and Orleans' formats concatenable. Azurite models blob index tags and their conditions only in part: on an absent blob it answers an `Append Block` conditioned on tags with 404 and lets such a `Put Blob` create the blob, and its `Get Blob`, racing an append, may report a committed block count other than its content's, so their Azurite cases pin what Azurite does, and the live run (§10) establishes them; beside `If-Match: *` Azurite ignores `x-ms-if-tags` (3b), whose tag condition the live run and the fake transport establish; hierarchical-namespace accounts are unsupported; and a write-ahead blob re-blocked or restored without its `durable-gen` tag is unsupported. Only the fake transport models the rest: requests which land after their own retry, server errors after landing, and a block count or append offset a misbehaving service misreports | `AzureArchiveLogFidelityTests`, one test per precondition, each failure's error code asserted, on Azurite where it models it; the live run, on `DURABLE_STORAGE_AZURE_LIVE` with Microsoft Entra ID, at milestones, with each run recorded (§10) |
| Plain-Orleans readers of archive logs | A plain Orleans reader of a Clog-written log which reads the write-ahead blob, then meets a checkpoint the sweeper deleted after a newer generation appeared, throws where Clog's reader answers `Stale` ([archive logs §8.4](components/archive-logs.md#84-history-reads-and-absence)); Orleans' layout constants are internal, so the interop test runs on every Orleans version bump | The interop tests of [archive logs §10](components/archive-logs.md#10-verification) on each Orleans version; a retry in Orleans' reader, or its acceptance |
| Physical erasure after expiry | An `Expired` journal's rows leave the LSM when compaction reaches them ([LSM-I5](components/archive-lsm.md#5-invariants)); no deadline is specified, and compaction scheduling is [deferred](deferred.md#compaction-scheduling-policy) | Measured, bounded compaction backlog and storage progress before any erasure deadline is stated |
| Application and deployment | End-to-end and soak (§10): *not yet in code*; reliability labs are [deferred](deferred.md#reliability-labs-for-the-orleans-topology) | Multi-silo tests with the history and linearizability oracles; soak runs; retained-state fault runs with an external oracle |
| Executables and Native AOT | No executable is specified; executables arrive with the Orleans composition's implementation ([build and packaging](build-packaging.md#3-executable-artifacts)) | Each executable's artifact contract and smoke evidence; for Native AOT, a clean target-native publish and provider-closure integration evidence ([build and packaging §4](build-packaging.md#4-native-aot-qualification)) |
| Orleans source-tree import | Clog builds `net11.0` alone with preview features; the language, API, and Fast CASPaxos boundaries an import into the Orleans source tree meets are measured in [build and packaging §6](build-packaging.md#6-orleans-source-tree-preparation) | Each boundary resolved for the frameworks and language version the import targets, in the import's own change |
| Cloud object stores | The Azure Blob, S3, and Cloud Storage object stores run the object store conformance suite, its contract examples, the integrity test, and the fault examples ([archive LSM §10](components/archive-lsm.md#10-verification)) on Azurite 3.37.0, LocalStack 4.14.0, fake-gcs-server 1.56.1, and Google's Cloud Storage testbench v0.67.0, pinned by the digests of [build and packaging §2](build-packaging.md#2-source-and-dependency-policy), at milestones (§10), and on no live account; each milestone run records its commit, the digests, and its reports in `artifacts/validation/Emulators` (§10). Azurite runs with `--skipApiVersionCheck`, so it serves the SDK's newer service version as the versions it models. fake-gcs-server answers a resumable session's status query unlike the service, so the Cloud Storage client's recovery of an interrupted upload runs on the testbench, which answers an upload whose bytes fail their checksum with 412, so the integrity test runs on fake-gcs-server. The service documents no answer to a status query of a session whose final request failed its condition, so the Cloud Storage store reads each upload's answer back and confirms each 412 by a read. The concurrent groups judge S3's 409 `ConditionalRequestConflict`, which the store throws, as an unknown outcome, so they run unchanged against a live bucket. LocalStack 4.14.0 refuses a replace whose `x-amz-checksum-crc32c` does not match its bytes, but then serves the object it replaced with its length and no bytes, where S3 is expected to keep the object as it was; on LocalStack the replace half of the CRC-32C case therefore pins that answer, so an upgrade which fixes it fails the test, and the shared contract example, which requires the object kept, runs on the other emulators | Each store's suite on a live account, recorded per backend, before production use, including that a create or replace refused for its CRC-32C leaves the object as it was |
| Provider-specific incomplete evidence | [Invariant evidence](invariants.md#13-invariants-without-evidence) identifies partial coverage: the Azure, DynamoDB, and Firestore MetaStore registers are tested over credential-free fakes, and no MetaStore backend is tested against its emulator or live; Azure Table's applied-write ETag equalling its read ETag waits for Azurite or a live account | Qualify each provider's actual boundary while preserving its layer's contract |
| Filesystem acknowledged durability | WAL creates directories one level beneath its caller's root and flushes only the root and what lies beneath it, on Linux, macOS, and Windows; the flush is qualified on Linux 6.1 or later on ext4 (`data=ordered` or `data=writeback`) and XFS, and on Windows on local NTFS volumes ([wal §9](components/wal.md#file-system-directories)), and so is its files' direct, write-through I/O, whose descriptor flags `WalDirectIoTests` check on Linux and which every Linux open confirms through `statx`; the cluster claim and the file-system MetaStore flush through it. CI runs Linux on ext4 and Windows on NTFS. macOS serves development and tests only. ReFS, FAT, and exFAT document their directory flush as a no-op that reports success (MS-FSA §2.1.5.7, note 80), and a remote volume reports its server's file system; the WAL refuses each with `PlatformNotSupportedException` | Qualification of each further file system the WAL is to support |
| Lock files | The seam's own `flock(2)` lock ([wal §3](components/wal.md#3-requirements-downward), WAL-I11) runs in CI on Linux only, on ext4, where `WalFileSystemLockTests` exercise it across processes with .NET's file locking disabled on either side, and its check that the path still names the locked file; the NFS refusal runs only where a test mounts NFS, which none does. macOS's `flock` path is untested: only its `errno` values are, through the mapping `AFailedExclusiveLockAnswersHeldOnlyWhenItWouldBlock` checks. Windows locks through the handle's sharing mode | A run of `WalFileSystemLockTests` on macOS; an NFS mount refused on Linux |
| Power-loss qualification | The crash-state enumeration checks where Clog places its file and directory flushes, `WalDirectorySyncTests` tie each flush to the operating system's call, and `LazyFsCrashSoakTests` (`just lazyfs`, §10) exercises three real silo processes over LazyFS FUSE mounts with `SIGKILL` + `lazyfs::clear-cache` unsynced page-cache drops across WAL and WAL-less RocksDB `atomic_flush` + `MANIFEST` recovery; neither shows that a flush's data reached a physical block device, since no in-process or FUSE signal reports hardware cache flushes. The persistence model takes POSIX `fsync` on Linux, and on Windows NTFS's flush as MS-FSA §2.1.5.7 specifies it, at their contracts. Write errors are injected through the simulated and recording storages, not a real device: that each direct write reports its own error and that reads return the device's bytes rest on the platforms' documented direct I/O, and that a restarted process's directory flush makes durable what a failed one, which stopped the process before it, did not rests on ext4 aborting its journal and XFS shutting down when the device fails a metadata write, and on NTFS behaving alike ([wal §1](components/wal.md#design-rationale)) | Power-loss runs of file and directory flushes on Linux and Windows: block-level write logging below the file system, or a virtual machine or virtual disk whose power is cut after acknowledged writes, with recovery checked against the acknowledged history; write errors injected below the file system (for example Linux `dm-flakey`), with a reopen in the same boot checked against the acknowledged history |

## 14. Design rationale

Invariant-based oracles detect behavior which line execution alone cannot establish. Branch coverage
and CRAP focus investigation without replacing assertions, reference models, and fault schedules; mutation scores
measure whether the assertions detect change. Deterministic scheduling makes a discovered failure reproducible.
Mutants demonstrate that a modeled invariant detects its intended violation instead of passing vacuously; Quint adds
type checking for record- and map-heavy protocols before state-space exploration.

The safety core is small and modeled before it is built, so its rules are checked over every interleaving within
the models' bounds before code depends on them; trace validation then ties the code to those models. Everything
materialized from the history is checked against the history's fold rather than against its own reference model:
one oracle covers every cache, index, and fast path, and a new optimization needs no new specification of
correctness.

One oracle per contract, separate from the generators, lets exhaustive small-depth exploration, long shrinking
random sequences, hand-written scenarios, and simulation share a single statement of correctness, so a contract is
specified once however its sequences are produced.

The conformance kit explores an implementation's interleavings as Stateright and Coyote do, so the models' exhaustive
check of a protocol has a counterpart over the code. Each case runs in a fresh simulation rather than one simulation
running many cases, so a case is a function of its seed and schedule alone and CsCheck's shrinking stays sound; a
strategy reset between cases would leave virtual time, operation identities, and the page pool to carry over. The
explorer re-executes each schedule rather than restoring a state, since Clockwork snapshots no state, and bounds
preemptions rather than enumerating every interleaving, as CHESS does: a window holds tens of choice points, races
which need one or two preemptions are the common case, and the bound makes each gate run's cost known. Clockwork's
seeded schedule sweep (`ScheduleExplorer`) samples interleavings and finds a race only by chance, which the measured
mutants show (§12); a probabilistic scheduler with priority changes (PCT) and partial-order reduction, which needs
Clockwork's race-exploration instrumentation to know which steps commute, remain options should windows grow.

Clockwork is built from the repository's source rather than referenced as a package, so a fix to the kernel and the
simulations which need it land together, and its build targets and task assembly come from the same commit.

The rewritten closure's references to the Microsoft.Extensions abstractions pass a reviewed allow-list, which fails
closed, because the shared framework's copies run unrewritten: a new reference, from Clog or from a package, is reviewed
before a simulation runs it. The static check finds the reference when it is written, and Clockwork's run-time check
fails the escape itself; work which escaped both would show only as a seed which does not replay, far from its cause.

The simulation kit is a library rather than test code so that the build rewrites it with the code under test: its
nodes, transport, and doubles then await and wait on the same controlled scheduler and virtual clock as Clog, and need
no hand-off between rewritten and ordinary code. The driver stays in the test assembly, outside the simulation, because
it owns the cluster and must run the step checks between operations. It runs each check itself and turns a check's
exception into that check's violation in its own ordinary IL, because Clockwork guards every broad or filtered catch in
rewritten code, the kit's included, with a check which requires the simulation's context, and the driver's thread
carries none. A failure is raised as a faulted operation on the cluster rather than by aborting the run, because
Clockwork's minimizer shrinks only runs whose failure identity it can reproduce. Teardown advances no virtual time, so
a timer which outlives its component is still armed when the checks run, rather than fired and forgotten.

The kit separates a node's lifecycle from what its store promises, so the BookKeeper alignment (Phase 0B of the
[implementation plan](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md)) changes only the latter. The node publishes its store and traces only
its replicas; `LedgerStoreInvariants` projects the catalog it checks, tombstones included, and
`SimulatedLedgerReplicaTransport` hands out each node's `ILedgerStore`, which lists and drops on that node's own
scheduler lane, from its store. Replicas are created by their first write, under loss, duplication, and crashes, as the
contract creates them; the kit creates none itself. `SimulatedLedgerReplica` and `SimulatedLedgerReplicaTransport` map
each `ILedgerReplica` member onto one request, so a contract change changes them and the checks in
`LedgerStoreInvariants`, while `LedgerStoreNode`, `SimulatedRpc`, the environment, and the driver stay as they are, and
the pinned snapshot, which records the schedule of the code under simulation, is re-approved.

Simulation projects run their tests one at a time because concurrent simulations share the process's static state:
a seed's steps then depend on what another simulation does on another thread, which a pinned trace detected as one
extra step. Running the corpus sequentially costs wall time, which the corpus budget (§12) accounts for.

The linearizability checker is the Porcupine algorithm ported to C#, so it runs inside every simulation and
end-to-end test with no Go toolchain; writing Porcupine's own visualization keeps its visualizer for diagnosis.
Porcupine searches partitions in parallel because one partition may take exponential time to refute while another is
refuted at once; turns of a fixed number of steps on one thread bound the work the same way and keep a check
deterministic, which a simulation requires. Verdicts on Porcupine's published histories, and an exact match with the
visualization data of Porcupine's own tests, tie the port to its source. A recorder's timestamps follow the order in
which the process observed its events, because a virtual clock which does not advance between one operation's response
and the next one's invocation would otherwise make sequential operations concurrent and weaken the oracle. The journal
model checks a rejection's reported `CurrentVersion` as a name of its linearized state, except for a rejection the
simulation flags deposed, whose ledger the map no longer listed open: a deposed owner which has not yet met the seal
rejects from its own applied state, which a successor may have passed, so its report is a hint the white-box oracle and
the version checker judge against that state.
The report is durable, since an owner reports its applied state's `CurrentVersion`, so no crash revokes it. Versions
are opaque and unique for a key's lifetime, so the model compares them for equality only, and takes a rebase as an
environment step allowed only once the key has no live rows. Confirming ownership with a seal check
before each rejection would make the report linearizable; it was rejected because it costs a quorum round trip per
rejection and serves no caller, since a rejection has no effect and its caller recovers by reading. The simulation's
white-box rejection oracle ([journals §10](components/journals.md#10-verification)) checks what the model
cannot: the fold through the position each rejection was decided from.

Crash-state enumeration records beneath the real directory implementation, portable files, and cluster claim, so the
write, sync, and publication sequences it checks are the ones which ship, and applies the POSIX fsync contract rather
than the file system the tests run on, so recovery from its states rests on what fsync promises. Its write-through rule
asks an fsync for a length a write extends, as Windows does, rather than relying on `O_DSYNC`'s stronger promise,
so one rule covers both platforms' write-through writes. A failed direct write is kept whole once a later fsync
completes, one outcome of the device's; `SimulatedWalStorage`'s torn write faults cover the others at the log's level.
Inodes make every subset of a trace a well-defined state.
Images are deduplicated within each crash point, because an image is judged against what its own point acknowledged;
sampling beyond a cap with a replayable seed keeps exhaustive small workloads within the every-change budget.

Page accounting follows the execution context rather than a process-wide count: test classes which run in parallel
each account for their own pages, and a page's return reaches its renter's accounting from whichever thread releases
it. The accounting in that context is the one source of which check counts a flow, so nested checks restore the one
they replaced. `Orleans.Durable.Storage.Engine.Buffers` keeps only the observer, which costs a static read when no
accounting exists; counting and tracing are test policy. Traces record call sites rather than full stacks, and resolve
files and lines only when a check fails, because capturing a stack with file information at every rental made a
million-entry test run for many minutes.

A simulation's quiescence check runs only the work due when the owners are disposed and does not advance virtual
time: work which a timer would run later outlives its owner even if it would end, so it fails the check. The scenario
first ends its pauses and heals its partitions, so the owners' disposal finishes the work they own.

A failed debug assertion is recorded as well as thrown because the code under test catches exceptions by design: the
quorum counts a replica's failure toward its accounting, a retry retries, and a background loop keeps its fault. A
thrown exception alone, which is all xUnit's own trace listener provides, fails a test only when it happens to
propagate. The listener still throws, so the code stops where its invariant broke rather than running on in a state its
assertion excludes. It runs ahead of every other listener, and the assembly check confirms it stayed there, because a
listener added ahead of it would take the failure first. Records are keyed by test and sequence rather than by time,
because Clockwork's lanes and the thread pool carry the test's execution context to the work it starts, so an owner
takes exactly the records its own run raised. Each record has one owner, the narrowest which can name its evidence: a
simulation reports its seed, step, and artifact, a test its name, the assembly whatever no test could claim. An
assertion takes precedence over the failure which follows it, which it carries, because a broken invariant is the
earliest evidence of a fault and a later failure is often its consequence. The test's check reports what it recorded
in a wrapper rather than deduplicating against the test's own failure, which xUnit aggregates before the check could
compare: the wrapper's message makes the repeat legible. A site's key joins its message in a failure's identity
because several sites assert the same condition in the same words. An assertion after the assembly check ends the
process, since no owner remains and a host which exits cleanly would hide it.

The Debug leg runs the test projects which reach code holding debug assertions, rather than the whole suite, which
keeps its cost within every change's gate; membership is declared in each project file, which the validation script
and an architecture test read, so a project whose code gains an assertion cannot go unreached unnoticed. It runs one
framework, since an assertion's outcome does not depend on it. Debug code generation costs the tests little: with the
leg's processes at high priority on the reference machine, the seed corpus took 2 min 41 s in Debug against 2 min 34 s
in Release, and `Orleans.Durable.Storage.Engine.Ledgers.Tests` 1 min 25 s against 1 min 15 s.

Snapshot testing uses Verify 32.0.0, its last release before its Open Source Maintenance Fee; golden vectors remain
the byte-level authority, and snapshots cover what reviewers read. A snapshot's oracle is a reviewer's approval of
text, which suits outputs whose correctness is a reading: whether a rendering of a format says what its specification
says, and whether a message tells its reader what happened. Each test writes its own rendering in the
specification's terms, rather than serializing objects, so that every line of a snapshot is deliberate and a change
reads as a change in behavior.

Mutation testing breaks at 80% on the core units, whose guards carry the safety properties; equivalent mutants
are recorded in a reviewed baseline rather than lowering the threshold. The script computes the score from Stryker's
report so that one reviewed file holds both the thresholds and the equivalent mutants, each with its reason, and a
class of equivalent mutants (a `ConfigureAwait` flip, a redundant `checked`, an exception's message text) is one entry
rather than one per occurrence. Matching on a mutant's text and enclosing pattern rather than its line keeps an entry
valid across unrelated edits, and a full run rejects a stale entry, since a mutant a test now kills was never
equivalent. Stryker's documented `ignore-methods` filter would also skip a removed awaited call whose expression ends in
`ConfigureAwait`, which is a real mutant, so equivalents are recorded rather than filtered; the filter leaves out only
mutants which Stryker cannot test soundly, those of `LedgerWriterMetrics`' static instrument creation (§9). Mutation
runs build into their own tree because Stryker rewrites assemblies in place, and they omit the dump extensions because a
killed mutant is the outcome they seek. A project captures coverage in a reused host unless its tests miss the host's
window, because a reused host captures coverage without starting a process per test; a test whose coverage is missed
runs against every mutant, so its own duration becomes every mutant's timeout, and ledger replica pays for a host per
test to capture exact coverage. A larger `additional-timeout` costs its margin only on the mutants which hang, where
lowering the concurrency which loads the machine would lengthen every run, and other work on the machine would still
load it.

Decoder fuzzing drives SharpFuzz through libFuzzer rather than AFL because libfuzzer-dotnet runs campaigns on Windows as
well as Linux, and libFuzzer minimizes and merges corpora itself. Each oracle states the format's rules over the bytes
on its own, because an oracle built from the code under test agrees with that code's mistakes: a round trip through the
writer checks layout, and a decoder which validates structurally can drop or misplace objects which its writer then
faithfully preserves, so a tree's decoder is checked against an independent walk of the tree. A decoder which validates
structurally is held to preserving its value rather than to canonical bytes it does not promise. Recomputing checksums
lets coverage guidance reach the rules behind them, which random bytes almost never pass. Allocation is bounded per
object decoded, after a warm-up, so the bound is tight enough to catch a length field which sizes an allocation. Seeds
are read from the golden vectors rather than copied, so they never drift from the formats; committed corpora carry only
what campaigns learned beyond the seeds, and replaying them in ordinary tests keeps every finding a regression without
libFuzzer on the machine. Each layer's targets build on that layer alone, so a layer's evidence names nothing above it
([overview §2.5](README.md#25-dependency-rules)) and its tests replay its own corpora.

Soak runs establish properties only long runs reveal (drift, leaks, retention growth), and they gate only the
finished system because their duration would slow every change without catching what faster layers already catch.

Upgrade and compatibility tests and public API approval tests do not apply while Clog is a prototype with one current
representation of each surface ([build and packaging §5](build-packaging.md#5-representation-and-compatibility)). The
other test kinds Clog has not adopted wait in [deferred work](deferred.md#7-verification), each with its condition to
revisit.

An external oracle survives the faults it injects; an oracle inside the target would lose its evidence with the
target, and a target's readiness says nothing about its recovered history.
