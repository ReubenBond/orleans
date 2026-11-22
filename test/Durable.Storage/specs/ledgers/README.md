# Ledgers models

TLC exhaustively checks finite [Quint](https://quint-lang.org) models of the safety core of the
[specification](../../docs/spec/README.md): the ledger map and each key's history, and dropping a closed ledger's
replicas; the same runner checks the [LSM's model](../archive/README.md). Each model makes requests, deliveries,
responses, and decisions separate transitions, so their interleavings remain visible. Every invariant has at least one
mutant, a configuration which disables the guard which establishes it, and TLC must produce that invariant's
counterexample.

## Run

Java 21, Node.js 20 or later, and PowerShell 7 are required:

```powershell
.\eng\model-check-quint.ps1
.\eng\model-check-quint.ps1 -Extended
.\eng\model-check-quint.ps1 -Model LedgerDrop -Workers 8
```

The runner installs the Quint version pinned by `test/Durable.Storage/eng/quint/package-lock.json` under
`artifacts/tools/quint`, and downloads the Apalache 0.62.1 archive, verifying its SHA-256, under
`artifacts/tools/quint-home`. Quint's TLC backend translates each model through Apalache and runs
the TLC bundled in that archive. The runner starts the Apalache server itself because Node.js does
not launch Apalache's Windows batch file.

Each case binds the model's constants in a generated instance module. Results, instance modules,
complete checker logs, and counterexamples are written under `artifacts/model-checks/quint`. A
positive case succeeds only when TLC reports success and exhausts its queue. An expected
counterexample succeeds only with TLC's violation of the named property and a behavior trace.
`quint typecheck` rejects ill-typed models before any case runs.

## Models and implementation mapping

Each row names the spec sections a model checks and the types which implement them.

| Model | Behavior | Spec | Implementation |
| --- | --- | --- | --- |
| `LedgerMap.qnt` | Ledgers owning dyadic ranges of one- or two-bit keys; takeovers, splits, and sibling merges, each a seal and recovery of the ledgers it closes and one ledger-map write which closes every overlapped open ledger and takes new IDs from `next_ledger_id`; primaries of installed ledgers admitting the next JSN over the history of every ledger up to their own, and former primaries which keep appending; drains raising watermarks, trims at segment starts below them, and drops of drained closed ledgers which no listed ledger with a lower ID overlaps; readers snapshotting the ledger map, then the manifest, then the live ledgers; each ledger's segments, started by its first batch or a flagged one, with defining and referencing rows by the writer's dictionary, and the token each defining row carries, planned from the key's previous segment | [Ledgers](../../docs/spec/components/ledgers.md) §2, §5, §8; [LSM](../../docs/spec/components/archive-lsm.md) §2.5, §8.3, §8.4; [Journals](../../docs/spec/components/journals.md) §8.1 | `LedgerMapCommitter`, `LedgerMapValue`, `TransitionExecutor`, and `LedgerWriter`'s segments; the committer and transition simulations' traces are replayed against it ([trace validation](#trace-validation)) |
| `LedgerDrop.qnt` | One final-window address of a closed ledger on three replicas, created by their first write, recovery write, or seal unless the node holds the ledger's tombstone; the former primary's delayed writer writes, a recovery's seals and recovery writes, which also land late; drain, removal from the ledger map, and each node's reconciliation, which acts from its own view of the map, from before the split which installed both ledgers to after the removal, and drops each ledger its view revokes, recording the tombstone in the same catalog write; stray replicas which a request delayed past the drop creates on nodes which held none; a lower open ledger's first write after a node dropped the closed one; the writer's outcome from the ledger map | [Ledgers](../../docs/spec/components/ledgers.md) §2.4, §8.5; [Ledger replicas](../../docs/spec/components/ledger-replicas.md) LR-I3, LR-I7, LR-I8; [Ledger protocol](../../docs/spec/components/ledger-protocol.md) LP-I5, LP-I7; [LedgerStore](../../docs/spec/components/ledgerstore.md) §8.5, §8.6 | `LedgerStore` and `InMemoryLedgerReplicaTransport` creation, seals, and drops; `Ledger` recovery and outcome resolution |
| `LedgerLease.qnt` | L2 ledger-replica quorum read lease with bounded monotonic clock-rate drift (`VMin..VMax`, `F = VMax / VMin`): primary recording `t_send` on writes/heartbeats, extending `localLeaseUntil = max(prev, t_send + floor(D / F))` on `Q` unsealed grants, serving 0-RTT local reads while `now + δ < localLeaseUntil`, RAM-only replica `grantedLeaseUntil` and startup `bootLeaseHorizon` across crash/restart with durable `isSealed`, sealing `ReadTailAsync` returning `leaseRemaining`, sealer waiting `max(leaseRemaining)` before `LedgerMap` CAS (`Replace`, `Split`, `Merge`), and cooperative `PrepareRelease` permanently surrendering `localLeaseUntil` before `Released` to skip the sleep | [System model](../../docs/spec/system-model.md) §2; [Ledger replicas](../../docs/spec/components/ledger-replicas.md) LR-I10; [Ledger protocol](../../docs/spec/components/ledger-protocol.md) LP-I18; [Ledgers](../../docs/spec/components/ledgers.md) LG-I14; [Orleans](../../docs/spec/compositions/orleans.md) OR-I24 | `LedgerStore`, `LiveReplica`, `LedgerQuorum`, `Ledger`, `LedgerWriter`, `LedgerWriterRegistry`, `TransitionExecutor` |

Pausing an actor models arbitrary delay or loss of its response. A request may land after its
sender crashed, was fenced, or gave up.

## Invariants

| Model | Invariant | Guarantee |
| --- | --- | --- |
| `LedgerMap` | `OneOwner` | LG-I1, LG-I2: the open ledgers partition the keyspace: one open owner per key |
| | `IdsDense` | LG-I3: the installed ledgers, listed or dropped, carry the IDs 1, …, `next_ledger_id − 1`, one each |
| | `HistoryOrdered` | LG-I4, LG-I6, JS-I1: tags totally order each key's history, and its JSNs are 1, 2, … in that order |
| | `AckedInHistory` | LSM-I3, JS-I2: every acknowledged entry stays in its key's history |
| | `DropsInLineageOrder` | LG-I10: every listed ledger which overlaps a dropped one has a higher ID, so each key's ledgers leave the map oldest first |
| | `ReadsSeeAcked` | LG-I8, LSM-I4, JS-I3: a read returns every entry acknowledged before it began |
| | `ReferencesResolve` | LG-I12: every referencing row a listed ledger stores has its defining row stored before it in its segment |
| | `TrimsAtSegmentStarts` | LG-I12: each ledger is trimmed at a segment start, so it stores whole segments |
| | `TokensReplayPlan` | JS-I10, HI-I4: each row's token is the replay of `Plan` over its key's history, the key's last write in each earlier segment, across splits, merges, and takeovers |
| | `NeverReferencingRow` (witness) | LG-I12: a listed ledger stores a referencing row, which needs a key's second write, so `ReferencesResolve` is not vacuous with two entries per key |
| | `NeverMarker` (witness) | LG-I12: a listed ledger stores a marker, a batch of no rows which starts a segment |
| `LedgerDrop` | `DecisionsAgree` | A write quorum and recovery's end at the address are exclusive and irrevocable |
| | `DurableIsDecided` | LG-I5: a write reported `Durable` is decided DATA |
| | `FencedHasNoEffect` | JS-I4: a write reported `Fenced` is not decided DATA |
| | `EndAddressSealed` | LG-I4, LG-I7, LR-I3, LR-I7, LR-I8: once recovery ends the ledger at the address, fewer than `Q` nodes answered the writer `Written` there or could still do so, before and after the drop |
| | `NeverDropsListed` | LG-I7: no node holds the tombstone of a ledger the map lists with it, though its view may predate that ledger's installation |
| | `TombstoneExcludesReplica` | LR-I7, LS-I2: no node holds both a replica of a ledger and its tombstone |
| | `DroppedIsFinal` | LR-I3, LS-I2: a node which answered a request `Dropped` holds the tombstone and no replica from then on, across crashes |
| | `NeverLowerCreatedAfterDrop` (witness) | LG-I7: a lower ledger's first write succeeds on a node which has dropped a higher one, which a creation floor at the highest dropped ID would refuse; the counterexample is the witness |
| | `NeverStrayAfterRemoval` (witness) | LG-I7: a request delayed past the drop creates a stray replica on a node which held none while another node holds the tombstone; the counterexample is the witness |
| | `NeverStrayDroppedAgain` (witness) | LG-I7: that node's reconciliation drops the stray, recording its tombstone |
| | `NeverWriterAnsweredDropped` (witness) | LP-I5: a writer's write reaches a node which answers `Dropped` |
| `LedgerLease` | `NoStaleLocalRead` | LR-I10, LP-I18, LG-I14, OR-I24: no 0-RTT local read ever executes after a successor ledger commits in `LedgerMap`, and once committed the outgoing primary's read guard is false |
| | `SealedStaysSealed` | LR-I3, LR-I10: a sealed replica remains sealed across crashes and restarts |
| | `NeverLocalRead` (witness) | LP-I18: a primary acquires a quorum lease and serves a 0-RTT local read |
| | `NeverUnplannedWaitCommit` (witness) | LG-I14: an unplanned transition waits out `max(leaseRemaining)` and commits |
| | `NeverCooperativeZeroWaitCommit` (witness) | OR-I24: a cooperative `Replace` skips the wait after `PrepareRelease` and commits immediately |
| | `NeverRestartBeforeSeal` (witness) | LR-I10: a replica crashes and restarts (latching `bootLeaseHorizon`) before sealing and committing |

## Mutants

| Model | Mutant | Disabled guard | Counterexample |
| --- | --- | --- | --- |
| `LedgerMap` | `no-close-overlap` | A split closes the ledger it splits | `OneOwner` |
| | `skip-id` | A takeover gives its successor the ID `next_ledger_id` | `IdsDense` |
| | `reuse-id` | A successor takes a new ID from `next_ledger_id` | `HistoryOrdered` |
| | `own-ledger-only` | A primary admits over its predecessors' history, not only its own ledger's | `HistoryOrdered` |
| | `no-seal` | A transition seals the ledgers it closes | `HistoryOrdered` |
| | `drop-undrained` | A closed ledger is dropped only once its watermark reaches its recorded `T` | `AckedInHistory` |
| | `write-before-install` | A primary appends only once the map write which installs its ledger applied | `AckedInHistory`; `HistoryOrdered`, the hazard itself: the write lands once a transition installs the ledger, beside the former primary's admission of the same JSN |
| | `drop-out-of-order` | A closed ledger is dropped only once no listed ledger with a lower ID overlaps it | `DropsInLineageOrder` |
| | `manifest-before-map` | A reader snapshots the ledger map before the manifest | `ReadsSeeAcked` |
| | `ignore-trimmed` | A reader retries when a ledger it needs was dropped or trimmed past its snapshot | `ReadsSeeAcked` |
| | `withdrawn-defining-batch` | A writer whose created batch is disposed unproposed is abandoned, so no later batch is decided | `ReferencesResolve`: a later batch references a key only the withdrawn batch defined |
| | `trim-mid-segment` | A trim falls at the last segment start at or below the watermark | `TrimsAtSegmentStarts`; with two entries per key, `ReferencesResolve`: a stored referencing row loses its defining row |
| | `token-from-wrong-parent` | A defining row's token extends the key's latest row in every ledger up to the writer's own | `TokensReplayPlan`: a successor's first segment plans from its own ledger alone |
| | `token-includes-fenced` | A token plans only from the key's decided writes | `TokensReplayPlan`: a sealed ledger's fenced write enters the successor's token |
| `LedgerDrop` | `drop-keeps-replica` | A drop removes the replica from the node's catalog | `DurableIsDecided`, `EndAddressSealed` |
| | `no-tombstone` | A drop records the ledger's tombstone, which refuses its creation | `DurableIsDecided`, `EndAddressSealed` |
| | `non-atomic-drop` | A drop removes the replica, with its DATA and seal, in one catalog write | `DurableIsDecided`, `EndAddressSealed`; with crashes disabled it holds every property, so each counterexample passes through a crash |
| | `split-tombstone` | The catalog write which removes the replica records its tombstone | `DurableIsDecided`, `EndAddressSealed`; with crashes disabled it holds every property, so each counterexample passes through a crash |
| | `drop-volatile` | A request which meets a drop answers `Dropped` only once the drop's catalog write is durable | `DroppedIsFinal`: a crash before the write brings back the replica the node answered dropped. A drop follows the close, so the state the crash restores is the node's state before the drop, and the mutant holds every other property |
| | `revoke-unlisted` | Reconciliation revokes only a ledger whose ID lies below its view's `NextLedgerId` | `NeverDropsListed`: a view which predates Y's installation drops Y's replica |
| | `count-dropped` | Outcome resolution counts a `Dropped` answer as sealed, never as `Written` | `DurableIsDecided` |
| | `short-coverage` | Recovery ends the ledger on `N − Q + 1` absent answers from sealed replicas | `DecisionsAgree` |
| | `ambiguous-as-fenced` | A writer whose ledger left the ledger map reports `Ambiguous` | `FencedHasNoEffect` |
| `LedgerLease` | `no-boot-horizon` | A restarting replica latches `bootLeaseHorizon = bootClock + D` before opening RPCs | `NoStaleLocalRead` |
| | `no-drift-divisor` | The primary divides the granted duration by the clock-drift factor `F = VMax / VMin` | `NoStaleLocalRead` |
| | `no-seal-wait` | An unplanned transition (`Replace`, `Split`, `Merge`) waits `max(leaseRemaining)` before `LedgerMap` CAS | `NoStaleLocalRead` |
| | `no-surrender-before-release` | `PrepareRelease` permanently clears `localLeaseUntil` before returning `Released` | `NoStaleLocalRead` |
| | `extend-after-surrender` | Late grant replies cannot re-extend `localLeaseUntil` once surrendered | `NoStaleLocalRead` |
| | `short-lease-quorum` | Lease acquisition requires `Q` unsealed replica grants (not `Q - 1`) | `NoStaleLocalRead` |
| | `short-seal-coverage` | Seal completion requires `N - Q + 1` replica seals (not `N - Q`) | `NoStaleLocalRead` |

## Bounds

Default exhaustive cases:

- `LedgerMap`: one-bit keys and four ledgers (takeovers, splits, and merges) with one entry per key;
  three ledgers with two entries per key. Each write may start a segment, and one marker, a batch of no rows, may
  start one once the open segment holds rows, so a segment may hold only its marker; `MaxMarkers` bounds the
  markers a run writes, since each adds a slot to every interleaving after it.
- `LedgerDrop`: three replicas with quorum two, and the split mutants without crashes as controls.

Extended cases add four ledgers with two entries per key and two-bit keys with five ledgers to `LedgerMap`,
and, to `LedgerDrop`, the `drop-volatile` mutant over every property but
`DroppedIsFinal` as a control and five replicas with quorum three, whose quorum is always the majority Ledgers
derives from `N`. There are no depth cutoffs, sampling, or state constraints that discard reachable states.

## Trace validation

`just ledger-map-traces` (`test/Durable.Storage/eng/ledger-map-traces.ps1`) checks the implementation's runs against `LedgerMap.qnt`
([verification §5.1](../../docs/spec/verification.md#51-trace-validation)). With `DURABLE_STORAGE_LEDGER_MAP_TRACES` set, each
seed of the ledger map committer simulation (`LedgerMapCommitterSimulationTests`) and the transition simulation
(`TransitionSimulationTests`) records a `LedgerMapTrace` (`test/Durable.Storage/Orleans.Durable.Storage.Simulation`) and writes it,
rendered by `LedgerMapTraceExport`, as a Quint module, `LedgerMapTraceCommitter{seed}.qnt` or
`LedgerMapTraceTransition{seed}.qnt`, under `artifacts/ledger-map-traces`; `test/Durable.Storage/eng/model-check-quint.ps1 -Traces` replays
each with `quint test` on Quint's TypeScript simulator. The seeds are 1 to 10 by default. A module imports `LedgerMap`
with the constants its run fixes (`KeyBits` the simulation's deepest range, 4 for the committers and 3 for the
transitions; `MaxLedgers` its last ID; `MaxJsn` its most writes of one key; `MaxMarkers` its markers) and its
`traceReplay` run applies the model's actions in order, checking every invariant after each step, and the model's state
against the run's: `mapIs` after each ledger-map write, and `wrote` or `marked` after each batch. A trace fails at an
action the model disables, a broken invariant, or a state the run did not reach.

The trace records every value the ledger-map register applied, in order, interleaved with the proposals answered
durable, and each ledger's decided batches, which the transition simulation's scans read: each closed ledger's below its
recorded `T` as its LG-I5 check reads them, and, only when the trace is exported and after every check, each open
ledger's below its highest durable answer. Recording takes no step, so an untraced run's schedule is unchanged. The
mapping:

| Implementation | Model |
| --- | --- |
| The first value which lists a ledger, the bootstrap of ledger 1 over the root | `init` |
| An install which opens one ledger over the range of a ledger it closes (a replace, handoff, replica change, or roll) | `takeover(i)` |
| An install which opens the two children of a ledger it closes, the lower first | `split(i)` |
| An install which opens the parent of two siblings it closes | `merge(i, j)` |
| A ledger the value no longer lists | `drop(i)`, after the installs of its write |
| A group commit, one write of several changes | Its installs in the order of the IDs they open, the order the committer's fold applied them, then its drops in ID order |
| A decided batch of one row | `write(i, k, flagged)`, `flagged` when it starts a segment past slot 0 |
| A decided batch of no rows | `marker(i)` |
| A proposal answered durable at a slot no scan holds | `write(i, k, false)` where its answer arrived |

A ledger's batches apply in slot order, no later than the first durable answer at or above their slot, and every batch
below a ledger's recorded `T` before the step which closes it: an atomic append lands between its call and its answer,
before the seal. After a write's last step, `mapIs` requires the model's listed ledgers, their `(prefix, depth)`, their
status, and `next` to be the value's, and, in a transition trace, each closed ledger's tail to be its recorded `T`. After
a batch, `wrote` requires the model's next slot, key, and row form, defining or referencing by the writer's dictionary,
to be the batch's.

The abstractions:

- The model's ledger `i` is the implementation's ledger whose ID is `i`, and the bootstrap is `init`; values written
  before it list no ledger.
- Node identities, replicas, primaries, quorums, `ChosenAt`, and the retirement floor (LG-I11) are not modeled, so a
  write which only raises the floor is no step.
- A range of depth `d`, at most `KeyBits`, is the model's `(prefix, depth)` with the prefix's top `d` bits. A key is the
  model's key of its first byte's top `KeyBits` bits; each one-byte key the transition simulation writes is the smallest
  of its ledger's range, so those bits identify it.
- A row's body, version, JSN, and token are not compared: the model computes each key's JSNs and tokens from its own
  history.
- The simulations drain nothing: in a transition trace, a drop of a ledger with batches follows the model's
  `drain(i, T)`, the Archive's completion which precedes every drop (LSM-I3). The committers' ledgers take no writes,
  so their recorded `T` is no model tail, and their drops need no drain.
- Trims and reads are not traced.

`test/Durable.Storage/eng/model-check-quint.ps1 -Traces` then runs each trace mutant on the first validated trace which holds what it
changes. The mutated replay through the step before the change must pass, and through the changed step must fail, the
model refusing that step by its action guard or by its expectation:

| Trace mutant | Change | Invariant | Refused by |
| --- | --- | --- | --- |
| `two-owners` | A takeover's write lists its closed ledger open | `OneOwner` | `mapIs` |
| `skip-id` | An install's write records `NextLedgerId` one higher | `IdsDense` | `mapIs` |
| `reuse-id` | A takeover's successor carries its predecessor's ID | `HistoryOrdered` | `mapIs` |
| `write-before-install` | A ledger's write lands before the write which installs it | `AckedInHistory` | `write`'s guard |
| `write-after-seal` | A ledger's write lands after the write which closes it | `HistoryOrdered` | `write`'s guard |
| `drop-undrained` | A ledger with batches is dropped without its drain | `AckedInHistory` | `drop`'s guard |
| `drop-out-of-order` | A ledger's drop moves before that of ledger 1, the root, whose ID is lower | `DropsInLineageOrder` | `drop`'s guard |
| `referencing-as-defining` | A referencing row is read as defining its key | `ReferencesResolve` | `wrote` |

## Scope of the result

A successful run establishes the listed properties for every reachable state of each configured
finite model, subject to TLC's fingerprint collision probability. The models abstract the
seal and recovery of a ledger (checked by [`test/Durable.Storage/specs/ledger-protocol`](../ledger-protocol/README.md)) and byte payloads.

`LedgerMap` lands each append atomically, leaving sealing and recovery to `test/Durable.Storage/specs/ledger-protocol`, whose
`RefinesAtomicLedger` checks that the ledger protocol refines that atomic ledger, and reduces the LSM to drains.
`LedgerDrop` models one
closed ledger and one lower open ledger. Creation reads only the node's catalog, and a restart keeps every replica,
seal, and tombstone, so a node's view matters only to its reconciliation: a view from before the split which installed
both ledgers lists neither, and its `NextLedgerId` lies below both, and a later view lists X until its removal. A crash
between two catalog writes matters only in the mutants which split a drop into two; requests wait at the node's catalog
gate between the two, so only a crash loses the second. Y's first write reaches one node, which stands for any by
symmetry. The closed ledger's recovery reads the address once enough replicas are sealed: until it finds the DATA,
nothing adds it to a sealed replica, so reading the sealed replicas one at a time observes what one step reads.
