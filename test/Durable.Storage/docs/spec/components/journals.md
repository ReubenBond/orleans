# Journals

## 1. Purpose and scope

Journals stores independent keyed journals for applications co-hosted with Clog. A journal is a sequence of
**entries** (`Append`, `Replace`, `Delete`, `Expired`), each checked against the journal's opaque **version**, and its
state is one pure fold of them. Journals maps each entry to one row of the journal's key in [Ledgers](ledgers.md), whose
body is the entry's native content, decides admission on the key's primary, and reads each journal as the fold of its
[history](archive.md#2-contract-upward) across the live ledgers and the archive they drain into.

Journals owns:

- journal semantics: entry kinds, versions and their rebase, the reducer, and its mapping onto row kinds;
- the key encoding, which spreads journals over the keyspace;
- admission on the primary, the outcome of every write, and the archive token and version each defining row carries
  (JS-I10);
- the entry record, which carries an entry from its writing process to the primary, and its writer-computed CRC-32C;
- the body limit as journals meet it (`MaxBodyBytes`), and its refusals;
- tombstone expiry and replace requests;
- warm state, which lives for one primary claim, and the per-ledger key index: caches of the fold which make
  admission, absence answers, and reads local (JS-I6, JS-I9).

Ledgers own ordering, ranges, ownership, and the per-key counter which gives each row its version
([LG-I13](ledgers.md#5-invariants)); the Archive owns the history, its store contract, and the pure token functions
([archive §3](archive.md#3-requirements-downward)), and the archive store (the LSM or archive logs) owns drains,
formats, compaction, and garbage collection. Admission is the only rule Journals adds to the core; everything else it
materializes is a cache of the history (§8.3).

Code: `Orleans.Durable.Storage.Engine.Journals` (reducer, entry codec, admission, reads, warm state, index, expiry),
concrete classes which the composition uses directly. Every type is data plane, in namespace
`Orleans.Durable.Storage.Engine.Journals`, and uses only the data-plane types of Ledgers and the Archive ([README
§2.1](../README.md#21-control-plane-and-data-plane)). *In code* (rows 2.3a and 2.3b A to D of the [implementation
plan](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md)),
with every body inline and within `MaxBodyBytes`: the row key (`JournalRowKey`); versions (`JournalVersion`) with their
text form; the entry record of §7 (`JournalEntry`, `JournalEntryRecord`); the fold of a history read with the rebase
(`JournalState`); a key's planning state (`JournalPlanning`), which its load gives with its state (`JournalLoad`);
admission on the primary (`LedgerAdmission`), which decides by version, judges an `Expired` by version, tombstone, and
retention, refuses a key `AdmitsKey` refuses, answers `ReplaceRequired` and the archive store's replace request, plans
each row's token, writes each entry's row of native content, and proposes through `LedgerWriter`'s `LedgerBatch`; and
`JournalStore`, which encodes entries, serves each writer the registry serves through one admission, loads keys and
answers owner reads from the Archive's slow-path read with each read's replace request, and runs the startup checks of
§8.4 as it is constructed; and the Journals simulation over LedgerStore nodes, with the oracles and the fault plan of
§10; and tombstone expiry's scan (row 3.4b), which `JournalStore.ExpireTombstonesAsync` runs over its admissions' warm
state and the Orleans composition's node control loop starts each `ExpiryInterval` (§8.6); `JournalStateDatabase` (row
5.2 part D), which maintains each node's derived per-ledger key indexes and tombstones over `IJournalIndex`
(`RocksDbJournalIndex` in `Orleans.Durable.Storage.RocksDb` or `InMemoryJournalIndex` in
`Orleans.Durable.Storage.Engine.Journals.Memory`), wired as `LedgerApplyLoop`'s `LedgerApplier` with JS-I9 catch-up and
one shared scan for non-replicated closed predecessors, feeding `ArchiveReader`'s key-indexed live reads through
`ArchiveKeyIndex`; and warm state (row 5.2 part E): `JournalWarmState` per primary claim (discarded when the claim ends
or the process restarts), `ArchiveReader.ReadStoreAsync` first-touch loads and owner reads (with zero-object-store
absence answers once section metadata is preloaded), cold tombstone expiry over the journal index's `tombstones` column
family, and warm state's `BytesSinceBase` snapshot request trigger (§8.7). Admission's pump creates batches
under its disposal's token, and a creation its disposal
cancels fences the entries it took; and its in-flight batches are bounded by Ledgers' `MaxProposalBytes`.

### Design rationale

**Journal state is a function of the decided history.** Admission is the only decision; every other value Journals keeps
(`CurrentVersion`, the planning token, warm state, key indexes, Bloom filters) is derived and can be discarded. The slow
path, the fold of `History(k)` read through [the Archive](archive.md#2-contract-upward), is the reference implementation
and the test oracle for every optimization.

**Versions are unique, not durable.** A version exists to fence stale writers: a write applies only to the state its
writer read. That needs each version to name one state of the key for the key's lifetime, not a count which survives
every drop. So a version is Ledgers' per-key counter, an opaque base and a count which every row advances by one, and
nothing stores it once the key's rows leave the ledgers. A durable per-key number, kept through a drop in the archive
(a position record beside each archive log, or a field per stored row), was rejected: it costs a second archive request
per key per segment, makes drop evidence wait on the record, and adds an invariant, the record's monotonicity, for a
continuity no caller needs.

**A key with no live rows rebases.** Once every row of a key has left the ledgers, a loading primary takes the version
`(VersionBase, 0)` from the archive's prefix (§8.3). The base is fresh: the archive's version base advances with every
command it applies and never repeats while it holds the key ([HI-I4](archive.md#5-invariants)), and every command ever
decided is held once its ledger dropped. A version equal to a rebased one therefore names the same state, so the only
cost is one spurious `Rejected` for a writer whose version predates the drop, which then writes at the reported
version or re-reads. Using the archive token itself as the version was rejected: an LSM token is empty, and a token
names a segment's write, not a row, so the rows of one segment would share it.

**A state may have two names.** A primary which holds a key warm from rows of a predecessor keeps the version it loaded
after the predecessor drops, while a read, which folds a history read, names the same state by the rebased version.
Each names that state alone (JS-I10). Admission accepts an entry at the warm version until a rejection loads the key
again and rebases it, which it does before rejecting an entry of a key with no recorded segment in its own ledger
(§8.3), and at the rebased one from then on. Without that reload a writer which re-reads after `Rejected` would be
rejected again, at the version every read gives, until another write lands. Keeping the warm version as an alias of the
rebased one, so that both stay accepted, was rejected: each version names one state as the primary holds it, and the
one spurious `Rejected` of a writer at the older name is the rebase's accepted cost. Having reads report the warm
version, for one name per state, was rejected too: reads fold the slow-path read, and reading warm state needs the warm
read path of row 5.2. A caller whose condition is the content rather than its name bridges the two names itself: the
Orleans composition's storage, after a `Rejected` write, reads once and resends at the version read when the content is
the one it holds ([orleans §5.5](../compositions/orleans.md#55-forwarding-and-journal-outcomes)). One durable name per
state is deferred ([deferred work](../deferred.md#durable-journal-versions)).

**Equality, not arithmetic.** Admission compares an entry's expected version with a state's version for equality only,
and nothing above Journals reads a version's parts: versions are opaque, so whether an `Ambiguous` write applied is
learned from a read, never inferred from versions, and the version's encoding stays the provider's and Ledgers'.

**Admission after in-flight entries, rejection against every possible state.** Admitting an entry after its key's
in-flight entries is safe because a ledger decides its batches as a prefix ([LG-I5](ledgers.md#5-invariants)): the
entry lands only if every earlier entry of its ledger lands. A rejection must hold whichever prefix of the in-flight
entries the ledger decides, since a takeover's `T` chooses it. An entry is therefore `Rejected` only when it applies to
none of the states those prefixes leave, and reports the applied `CurrentVersion`, which is durable. While entries are
in flight the key's base is fixed and each entry adds one to the count, so an expected version names at most one
possible state and the decision is one comparison. An entry which applies to an earlier possible state waits: only a
seal can exclude an in-flight entry, and the seal fences the waiter, so the wait ends `Rejected` or `Fenced` within the
latency of the batches in flight (§8.1). `Ambiguous` asserts nothing, since an owner's read is not ordered after an
entry still in flight; the version check orders it with its writer's next write instead (§2.2).

**Row kinds named as the entries.** An `Append` is an `Append` row, a `Replace` a `Replace` row, and a `Delete` or an
`Expired` a `Delete` row, whose 8-byte body is the tombstone or which is empty. Each archive store drains and compacts
journals by row kind alone, without knowing their payloads, and the drop rule is decided once, below Journals.

**Rows carry native content.** A row's body is exactly what the archive stores and Orleans reads: an `Append`'s or
`Replace`'s payload, or a `Delete`'s tombstone. So a drain copies bodies without decoding them, an archive log holds the
bytes plain Orleans reads, and no row repeats a record head. The entry record exists only between the writing process
and the primary, which verifies it and writes the row (JS-I5).

**The record has no version byte.** A record is never stored: it lives from its writing process's encoding to the
primary's verification, within one cluster, and Clog keeps one current representation of every format
([conventions §10](../conventions.md#10-one-current-representation)). The forwarding message which carries it is the
composition's, and its message type versions the record with the rest of the message
([Orleans §5.5](../compositions/orleans.md#55-forwarding-and-journal-outcomes)); a version field of the record's own
would name a compatibility obligation no reader has.

**Bodies are inline.** Every entry's body travels in its record and its row, at most `MaxBodyBytes` (16 MiB by default),
and no layer has blob references or blob objects (§8.4). The record holds the writer's pages and the primary copies the
body once, into its batch; a body has no lifecycle apart from its row, and so needs no upload, no scoping to a ledger,
no re-upload after `Moved`, and no sweeper which could race a reader. A large-entry path, which uploaded a body above an
inline limit as a blob scoped to the key's open ledger and proposed a reference to it, was rejected: it added an object
per large entry with its own garbage collection and races, a second read path, and an outcome (`Moved` for a blob of a
ledger no longer open), for snapshots which a 16 MiB bound covers. The bound is a contract on writers: a grain whose
snapshot exceeds it cannot write it.

**Tokens are planned at admission.** A key's archive write for a segment is a pure function of its rows there, so the
key's token for its next segment is `Plan` of that write, which starts from the current token, and admission, which
writes every row of the key, computes it incrementally in constant state per key and stores it in the key's defining row
(JS-I10). Drains and loads then check the stored token of each row the archive does not hold against the replay
([HI-I4](archive.md#5-invariants)), and no archive request is needed to plan a write. The token rolls lazily, when the
key's next row joins a batch of another segment.

**`ReplaceRequired` is a backstop.** A provider limits what one generation of a key's log holds, such as Azure's
committed blocks per append blob. The provider asks for a `Replace` well before the limit (`IsReplaceRequested`), and a
grain's snapshot answers it; only an `Append` which the limit would still refuse is answered `ReplaceRequired`, with no
effect, decided against the applied state so that the answer is durable. Splitting a write across generations, or
refusing at the drain, was rejected: a drain cannot refuse a decided row.

**An `Expired` names its state by version.** An `Expired` carries the version of the deleted state it ends, and applies
only to a deleted state of that version whose tombstone's retention has passed (JS-I1). A version names one state, a
rebased one included, so the `Expired` cannot end a state written after the one it was issued for, and it needs no tag
of the `Delete`, which an in-flight row lacks and a drained one loses. An `Expired` is judged against the warm applied
state alone, and is `Rejected` at once while its key holds no warm state or any entry of its key is in flight or
waiting. Judging it against every possible
state, as other entries are, was rejected: it adds a case to admission for nothing, since an in-flight `Delete`'s
retention cannot have passed and any other in-flight entry changes the state the `Expired` names; its only writer, the
expiry scan, treats the rejection as a hint and proposes again later.

**Existence follows the latest opener.** A journal exists when its latest opener is a `Replace`, even of an empty
snapshot, or it has `Append`s since; it is deleted when that opener is a tombstoned `Delete` with no `Append` since; and
an empty `Delete` (an `Expired`), like no history, leaves it absent. Every archive tells these apart: a prefix exposes
whether its generation has a base (`ArchivePrefix.HasBase`), its tombstone, and its content, so a key's existence is
the same before and after its rows drain. Deriving existence from non-empty content was rejected: it would make an
emptied journal absent.

**Warm state for one claim, key indexes per ledger.** A primary computes a key's state when the key is first touched,
from the history where it lies, and keeps it for its claim only. Takeovers, splits, and merges therefore move no
journal state, and no claim finds warm state an earlier claim left, which a range handed away and back would make
stale. Each node's key index of a ledger catches up once per ledger to the ledger's decided end, so absence answers and
loads read no object on the hot path once the LSM has preloaded the range's section metadata (§8.3). Keeping warm state
across claims and restarts, refreshed from a recorded position, was rejected: a refresh needs every row above the
position, which a compaction may have dropped, and it costs about as much as the load it replaces.

**Reads only where they are linearizable.** A journal read answers from the history only where it includes every entry
acknowledged before it began: in the process which serves the key's open ledger's writer, whose seal check covers that
writer's `Durable` proposals (LG-I8). The Archive reports per read whether the open ledger's tail was served here, so
the condition is checked rather than assumed, and a read elsewhere, or one during which a takeover moved the primary
away, is answered `Moved` or forwarded. Serving a read from a contiguous tail was rejected: it may lag the primary's
answers, and a client which wrote and then read would miss its own write.

**Reads stream bytes.** The archive returns a key's content concatenated, a base followed by appends, in pages which are
not entry boundaries, and Orleans' `IJournalStorageConsumer` consumes bytes. So `ReadPayloadsAsync` yields the content
in chunks unrelated to entries, and a read never re-splits what the archive joined.

**Hashed keys.** A journal's key starts with a hash of its identity, so the midpoint splits and sibling merges of
dyadic ranges divide load evenly. The hash is Journals's convention; the layers below treat keys as bytes.

**Every per-entry check before the batch.** Admission checks each entry's key against the ledger's range and the
archive's `AdmitsKey`, its body against `MaxBodyBytes`, and an `Append` against emptiness, when the write arrives, and
the replica's payload limit holds the largest batch admission builds, which a check at startup confirms (§8.4), so no
row of a planned batch is refused. A batch which then fails to build or propose is a fault of admission's own: it
fences, fails that batch's entries with the fault, which asserts nothing, and abandons the writer, so failure handling
replaces the ledger ([ledgers §8.6](ledgers.md#86-takeover)). Proposing a later batch after a skipped one would break
the key's chain of versions and tokens, which every later read rejects (HI-I4); answering the batch `Fenced` would claim
it never lands, which the fault does not establish. Only the writer's disposal before the proposal took a slot, which
appends nothing, is `Fenced`.

**A fold which warm state left stops the process.** Warm state applies only entries it admitted against itself, so a
`Durable` entry which does not apply means warm state no longer folds the ledger's decided rows. That is a defect of
the same kind as a component's memory out of step with its log, so admission reports it to the fatal-error handler
and answers nothing more ([conventions §14](../conventions.md#14-local-storage-failures-stop-the-process)). Discarding
the key's warm state and loading it again was rejected: it would hide the defect and answer later writes from a state
nothing explains.

**Only the primary writes `Expired`.** `AdmitAsync` refuses an `Expired`, and the expiry scan admits one through
`ExpireAsync`, internal to Journals, so JS-I7 holds by construction rather than by every writer's discipline. The scan
is a type of its own, `JournalExpiryScan`, so the one caller of each step toward an `Expired` is checkable in the
compiled code: only admission builds the record, only `JournalStore` calls admission's `ExpireAsync`, and only the scan
calls the store's.

**The scan runs over warm state, paced by the node.** The candidates are the keys whose applied state admission
already holds, read under each admission's lock with no history read, and an `Expired` never loads its key, nor
reloads it: one whose key is no longer warm when it arrives, evicted since the scan read its candidates, is `Rejected`,
and one which meets a warm state other than its candidate's is `Rejected` from it. So a scan costs memory in
proportion to its candidates and archive requests for none of them, whatever warm state does meanwhile; when a
`JournalStateDatabase` is attached (row 5.2 part E), the scan also reads cold deleted keys whose retention has passed
from the state database's `tombstones` table and proposes their `Expired` entries through `ExpireColdAsync`, which
loads and validates each candidate against the durable fold before proposing. An `Expired` is no use of its key either:
once decided, the key returns to the least recently used end of warm state, so a scan never keeps tombstones warm at
live journals' expense.
Each candidate is a hint which admission decides afresh (JS-I1), so the scan needs no lock across its proposals, and a
candidate stale by the time its `Expired` arrives costs one `Rejected`. Letting an `Expired` load a cold key was
rejected: until key indexes, each load is a history read of the archive, so a scan whose candidates warm state evicted
would cost archive requests in proportion to them, and the `Expired` of a key no admission holds warm is one the scan
had no warm evidence for. The scan proposes in rounds of at most
`MaxBatchEntries`, each ended before the next, so it holds at most one batch's entries in admission and never crowds a
writer out of its ledger's queue, and its `Expired`s still share batches. The cadence belongs to the composition, the
node's control loop (orleans §5.6), which starts it beside the loop's other periodic work, at most one at a time, only
while the node's Journals serve, and awaits it at its stop with the loop; a failed scan waits for the next interval,
since retention (7 days) dwarfs the interval (1 hour) and a sooner retry would buy nothing. A timer inside
`JournalStore` was rejected: it would write `Expired` in every composition and test which builds a store, so the
simulation's oracles, which count each journal's `Expired` rows against the writes they recorded, would meet writes no
client made, and the stop order, which the composition owns, would gain a second owner. Running it on the node
controller's scheduler was rejected too: that layer names no Journals type, and the scan needs nothing the controller
holds.

**The writing silo stamps the deletion time; the primary's clock judges the retention.** A `Delete` carries the
tombstone its writing silo stamped as it encoded the record (`EncodeDelete`, and the Orleans storage adapter's own
encoding), and the primary which expires it compares that stamp with its own clock, so a tombstone keeps at least
`TombstoneRetention` less the largest skew between two silos' clocks (JS-I7), which the [system
model](../system-model.md#4-assumptions) assumes far below the retention. Having the primary stamp the deletion time
at admission was rejected. It would remove only the skew between the writer and the admitting primary: the retention is
judged by whichever silo is primary at expiry, which after a hand-off or takeover may not be the silo which admitted the
`Delete`, so skew between silos remains either way. And it would rewrite a forwarded record's bytes at admission, its
body and its CRC-32C, which the writing process computed and the primary verifies (JS-I5), adding complexity for no
real gain.

**The record's CRC-32C covers its row key, and ends at the primary.** The writing process computes it over the row key
and then the record, so a record verifies only under the key it was written for, and a forwarded record misrouted to
another key is corruption. The primary verifies it and writes the row's native content, which its batch's CRC-32C then
protects on every replica and for every reader (JS-I5). Carrying the record's CRC into every row was rejected: it would
store a record head per row, which the archive's native content cannot hold. Two spans stay unchecked: the primary's
memory between verifying a record and encoding its batch, and archive content at rest, which the store's own checksums
protect in transit only.

**Load and evict only with nothing in flight.** A key's state is loaded, or evicted, only while the primary has no
admitted entry of the key in flight or waiting on in-flight outcomes, which an assertion checks. A load may rebase, so a
load beside an in-flight entry could split one key's chain between that entry's row and the rebased state.

**Admission is warm state's one applier.** The primary's admission applies its own decided batches to warm state before
it answers them, and exposes the slot below which it has applied, which an owner read waits on (§8.2) and the store
answers per ledger (`JournalStore.AppliedBelow`) for its node's observation ([orleans
§5.9](../compositions/orleans.md#59-resource-governance)). A second applier, such as the node's apply loop, would race
admission over the same state; so the state a load returns becomes the admission's, and a caller sees a state only
through its values.

**Admission is disposed before its writer.** Disposal fences admission and waits for its outstanding proposals'
outcomes, which only the writer decides; disposing the writer first ends those proposals `Ambiguous` or `Fenced` instead
of letting them complete.

**Memory bounded by budgets.** Entries accepted and not yet proposed are bounded in bytes, so a burst past the batches
in flight waits asynchronously rather than queuing without limit, and warm state is bounded by a byte budget past which
the least recently used keys with nothing waiting or in flight are evicted; a later touch loads them again (§8.3).
A key's warm state is a fixed number of values whatever its journal's length. It keeps no row tags: reads fold the
history read, and the key indexes (§8.3) are each ledger's one authority for its rows' tags, so a copy in warm state
would cost memory in proportion to the journal and be a second source of truth.

## 2. Contract upward

Outcome taxonomy: [outcomes](../outcomes.md).

### 2.1 Journal semantics

- A journal is identified by a `JournalKey`. Its version, `CurrentVersion`, is an opaque `JournalVersion` (§4.3): an
  absent journal's is `(Origin, 0)`, and every accepted entry, of any kind, keeps the base and adds 1 to the count. A
  load of a key with no live rows rebases it to `(VersionBase, 0)`, the archive store's version base at that load
  (§8.3). A key's versions are unique for its lifetime (JS-I10), not continuous across a rebase, and a caller compares
  them only for equality.
- A version **names** a state of the journal, and a state may have two names: once every row of the journal has
  dropped, a read names its state by the rebased version, while its primary may hold the state warm under the version
  it had before the drop (§8.3). Once the LSM has forgotten an expired key entirely, the key starts a new lifetime at
  `(Origin, 0)`, and every `Origin`-based version of that lifetime may name a state which the same version named in an
  earlier lifetime. This hazard needs a writer stale across the key's `TombstoneRetention` and the compaction which
  forgot it, which bound it. Within a lifetime no version names two states of a key (JS-I10).
- Every entry names an **expected version**. An `Append`, `Replace`, or `Delete` is **accepted** only when its expected
  version names the journal's current state; once the primary has rebased the key, an older name of the state is
  rejected with the rebased one (§8.3). A rejection's `CurrentVersion` is a hint from the owner's applied state (§2.2):
  a name of the current state while the owner still serves the key, and otherwise of a state the journal held, which an
  owner deposed before it decided may report after its successor moved the journal on.
- `Append` adds a non-empty payload. `Replace` supersedes every earlier entry with one snapshot payload. `Delete` ends
  the content and leaves a **tombstone**, the writing process's deletion time.
- `Expired` ends a deleted journal's tombstone; only the owner writes it (§8.6). It is accepted only when no entry of
  the key is in flight or waiting on the owner and the journal is deleted, the `Expired`'s expected version names its
  state, and the tombstone's deletion time plus `TombstoneRetention` is at or before the primary's clock. The journal
  then reads as absent, at its advanced version.
- A journal's **opener** is its last `Replace`, `Delete`, or `Expired`. Its **content** is its opener's payload when the
  opener is a `Replace`, or nothing, followed by the payload of every later `Append`. A journal **exists** when its
  opener is a `Replace`, an empty snapshot included, or an `Append` follows its opener; it is **deleted** (`DeletedAt`)
  when its opener is a `Delete` and no `Append` follows it; otherwise, after an `Expired` or with no history, it is
  absent.

### 2.2 Writes

The writing process encodes each entry once, through `JournalStore.EncodeAppend(key, expectedVersion, payload)`,
`EncodeReplace(key, expectedVersion, snapshot)`, or `EncodeDelete(key, expectedVersion)`, each over the journal's
`JournalRowKey` and returning a `JournalEntryRecord` its caller owns, and writes it with `WriteAsync(record)`, which
returns a `JournalWriteOutcome`. `WriteAsync` also takes a record forwarded to the primary, which the composition
verified where its bytes entered the process (§7, JS-I5).

Preconditions, each refused with `ArgumentException` by the writing process as it encodes the entry, and again by
`WriteAsync` and by admission as the entry arrives (§8.1), through one check of the record (`CheckRecord`): the key's
strict UTF-8 encoding is 1 to 65,531 bytes and the archive store admits it (`AdmitsKey`: archive logs on Azure refuse a
key whose Orleans-style blob name would exceed 1,024 characters, as Orleans' own provider would fail it); and the
payload is an `ArcBuffer`, the default value when it is empty ([conventions
§1](../conventions.md#1-buffers-and-ownership)), of at most `MaxBodyBytes` (§8.4). An `Append`'s payload is non-empty,
as Orleans' `AppendAsync` requires: the writing process's encoding refuses an empty one with `ArgumentException`, so no
record holds one (§7). `WriteAsync` refuses the default record and an `Expired`, which only the owner's expiry scan
writes (§8.6). An entry which applies only to a state no row can advance, whose count is 2<sup>64</sup> − 1, fails with
`ArgumentException` and has no effect (§8.1). The record retains its own lease on the payload, and the store its own on
the record while it needs the bytes; each caller keeps and disposes its own. `EncodeDelete` stamps the deletion time
from the store's `TimeProvider`, and the encoding computes the record's CRC-32C (JS-I5); the composition carries the
encoded entry verbatim to the key's primary, and a resend after `Moved` or `Fenced` sends the same record.

| Outcome | Meaning | Effect |
| --- | --- | --- |
| `Accepted(Version, ReplaceRequested)` | Durable, and applied by the owner; `Version` is the journal's version after the entry, and `ReplaceRequested` the archive store's replace request at the key's admitted token (§2.4) | In the history |
| `Rejected(CurrentVersion)` | The entry applies to no state the key's in-flight entries can leave on the owner, or is an `Expired` whose tombstone's retention has not passed, whose key has an entry in flight or waiting, or whose key the owner holds no warm state of (JS-I1); `CurrentVersion` reports a name of the owner's applied state (§8.3), a version the journal held, or, for an `Expired` of a key with no warm state, the `Expired`'s own expected version, a hint which names no state the owner read | None |
| `ReplaceRequired` | An `Append` which the archive store's per-generation limits refuse (`Admits`), decided against the owner's applied state; the writer's next write is a `Replace` | None |
| `JournalMoved(Owner?)` | This process does not serve the key's open ledger's writer; `Owner` names the primary in its map, absent when the map lists no open ledger for the key, as before bootstrap, or when the map names this process but its registry serves no writer for the ledger (§8.5) | None |
| `Fenced` | The entry is not in the ledger and never will be | None |
| `Ambiguous` | Nothing: the entry may still be decided, at most once | Unknown |

A sender may resend `Moved` and `Fenced` entries; a resend of an accepted entry is `Rejected` with the advanced
`CurrentVersion`. Cancellation stops the caller waiting and asserts nothing, at any stage: an entry whose caller
cancelled may still be proposed and decided, and a proposal already made is decided as usual. A disposed store's
`ObjectDisposedException` asserts no effect: the store throws it only before any admission takes the entry, or for an
entry an admission holds waiting, before it is proposed; an entry admitted before the disposal is answered as its
ledger decides it, `Ambiguous` when its writer's disposal ends its proposal first, or `Fenced` (§9). A write whose key's
state loads while the key's ledger cannot reach its quorum fails with `JournalStoreUnavailableException` (§6), which
asserts no effect: the entry waited for the load, so it was never proposed. Resource governance
belongs to the composition, which refuses a write before it reaches the Journals ([Orleans
§5.9](../compositions/orleans.md#59-resource-governance)).

**`Ambiguous`.** A writer whose outcome is `Ambiguous` continues from a read; a read need not reflect the entry, which
may still be in flight on the primary and land afterwards. The version check orders the writer's next write at that
expected version with the entry: whichever reaches admission second waits for the other's outcome if it is in flight
(§8.1), and at most one of them is accepted. Versions are opaque, so nothing infers from versions whether an `Ambiguous`
entry applied; only a read tells.

### 2.3 Reads

| Member | Outcome |
| --- | --- |
| `ReadAsync(key)` | A `JournalReadOutcome`: a `JournalReadResult`, which the caller disposes, or `JournalMoved` as for a write (§2.2). The result holds `CurrentVersion`, `Exists`, `ContentLength`, `ReplaceRequested` (§2.4), and `ReadPayloadsAsync`, an `IAsyncEnumerable<ArcBuffer>` which yields the journal's content in order, `ContentLength` bytes in chunks unrelated to entry boundaries, each then owned and disposed by the caller; disposing the result releases the chunks it has not yielded, and enumerating it concurrently with its disposal is a caller's error. Reflects every entry acknowledged before the call, and only decided entries (JS-I3). |

A read throws `JournalStoreCorruptException` when the history contradicts itself, and
`JournalStoreUnavailableException` when its history read cannot reach the quorum of the key's ledger (§6). A read
assembles the content before it returns. Its `CurrentVersion` names the state its content is: the fold's version, the
rebased one once every row of the journal has dropped (§8.3).

### 2.4 Snapshot requests

Journals asks a journal's writer for a `Replace` with a **replace request**: because reads assemble far more bytes
than one snapshot would hold (§8.7), or because the archive store asks for one for the key's current token
(`IsReplaceRequested`), well before its per-generation limits would refuse an `Append`. An `Accepted` answer carries
the store's request at the key's admitted token, and a read's result carries the request its read gives, either
trigger; the writer latches the request until a `Replace` is accepted, or learns it again from its next read.

## 3. Requirements downward

- **Ledgers** ([ledgers](ledgers.md)): the cached map and its `Find(key)` and `FindOpen(key)` (`LedgerMapView`);
  `LedgerWriter.ProposeAsync` and `ConfirmOpenAsync` on the key's open ledger, through the primary
  `LedgerWriterRegistry.TryGet` serves while the map view lists this process's claim, which answers a ledger no writer,
  then one writer, then none forever, so the store retires an admission only once its writer is gone for good;
  `LedgerWriter.CreateBatchAsync`, across which admission holds no lock of its own; `LedgerBatch.BeginRow(key, kind,
  versionBefore, token, bodyLength, bodyHead)` and `WriteBody` ([ledgers §4](ledgers.md#4-data-types)), which store the
  version and token when the row defines its key and return the row's `RowVersion`, and the batch's segment identity
  (`LedgerSegmentId`); a replica payload limit which holds the largest batch admission builds (§8.4), which
  `LedgerAdmission` checks when it is created; decided batches of every ledger the node replicates delivered in slot
  order to the node's apply loop as resolved rows, each with its version and its defining row's token, through each
  closed ledger's `T` ([ledgers §8.5](ledgers.md#85-replica-lifecycle-on-a-node)); `LedgerReader.ScanAsync` for a key
  index's catch-up and a load's live rows; each live ledger's decided end ([ledgers
  §2.5](ledgers.md#25-reads-ledgerreader)); LG-I1 to LG-I13.
- **Archive** ([archive](archive.md)): `ArchiveReader.ReadAsync` and `ArchiveReader.ReadStoreAsync` with its `Merge`
  ([archive §2.2](archive.md#22-history-reads)), over the process's one `LedgerReader`, which its `LedgerWriterRegistry`
  shares: the key's archive prefix (`Token`, `VersionBase`, `HasBase`, `Tombstone`, `Content`), read after a ledger-map
  value, and the live rows of the key in the ledgers that value lists, each with its version, its defining row's token,
  and whether the prefix holds it; the archive store's pure functions, with parameters fixed for the cluster:
  `Plan(write)` and `Admits(write, command)`, both defined by the incremental fold `ArchivePlan` (`Begin`, `Add`,
  `Admits`, `End`), which is admission's only path: it keeps one fold per key's write and asks `ArchivePlan.Admits`
  before each command, never replaying a write; `IsReplaceRequested`, `AdmitsKey`, and `MaxAppendBytes`, which a startup
  check holds at or above `MaxBodyBytes` ([archive §3](archive.md#3-requirements-downward)); HI-I1 to HI-I4, which the
  LSM ([LSM](archive-lsm.md)) or archive logs ([archive logs](archive-logs.md)) establish. A read answers a journal only
  when it is linearizable (`ArchiveRead.Linearizable`) and the ledger whose seal check made it so
  (`ArchiveRead.OpenLedger`) is the admission's: one read outside the process serving the key's open ledger's writer is
  answered `Moved` or forwarded to the primary (§8.2, §8.5).
- **Journal index (`IJournalIndex`)**: per-node derived index storage for per-ledger `key_index`, `tombstones`, and
  durable watermarks (`InMemoryJournalIndex` in `Orleans.Durable.Storage.Engine.Journals.Memory` for tests and Clockwork
  simulations, and WAL-less, atomic-flush `RocksDbJournalIndex` in `Orleans.Durable.Storage.RocksDb`, §8.3).
- **Composition**: one Journals per process, routing each key to its primary, forwarding encoded entries, and
  governing resources before writes reach the Journals.

## 4. Data types

### 4.1 Keys

| Type | Definition |
| --- | --- |
| `JournalKey` | A non-empty string with ordinal identity; strict UTF-8 of 1 to 65,531 bytes. |
| Row key | `xxhash32(UTF-8 key, seed 0)` as 4 big-endian bytes, then the UTF-8 key. `JournalRowKey.Encode` forms it, and `TryDecode`, its inverse, returns the key of exactly the bytes `Encode` forms, and null for any others. |

### 4.2 Entries and rows

| Entry | Row kind | Row body |
| --- | --- | --- |
| `Append(expected, payload)` | `Append` | The payload |
| `Replace(expected, snapshot)` | `Replace` | The snapshot |
| `Delete(expected, time)` | `Delete` | The 8-byte tombstone: the deletion time (§7) |
| `Expired(expected)` | `Delete` | Empty |

A row's version is the key's version after it, which `LedgerSegmentView` computes ([LG-I13](ledgers.md#5-invariants)).
The fold applies each row by its kind and body:

| Row | Content after it | `Exists` after it | `DeletedAt` after it |
| --- | --- | --- | --- |
| `Append` | The content, then the body | Yes | None |
| `Replace` | The body | Yes | None |
| `Delete`, 8-byte body | Empty | No | The body's deletion time |
| `Delete`, empty body | Empty | No | None |

A prefix gives the state the fold starts from: `Exists` when it has a base (`ArchivePrefix.HasBase`) or non-empty
content, `DeletedAt` its `Tombstone`, and its `Content`.

| Type | Definition |
| --- | --- |
| `JournalState` | The fold: `CurrentVersion`; `Exists`; `DeletedAt?`; `ContentLength`; `BaseLength` and `BytesSinceBase` (the bytes applied since the base, which §8.7's warm-state snapshot trigger reads). Absent when the key has no history, whose version is `(Origin, 0)`. |
| `JournalPlanning` | A key's planning state on its primary (§8.1): its recorded segment identity and its token there, once, and two `ArchivePlan` folds of its write there, the written one through the rows batches hold and the admitted one through every admitted entry, each within `ArchivePlan.MaxStateBytes`. With no recorded segment, the token is the one the key's next write starts from, and both folds are empty writes from it. A load gives it with the key's state (`JournalLoad`, §8.3), and admission then owns it. |
| `JournalWriteOutcome` | `Accepted(Version, ReplaceRequested)`, `Rejected(CurrentVersion)`, `ReplaceRequired`, `JournalMoved(Owner?)`, `Fenced`, or `Ambiguous`. |
| `JournalReadOutcome` | A `JournalReadResult` or `JournalMoved(Owner?)`. |
| `JournalMoved` | `Moved` of a write, a read, and a load: `Owner`, the primary the map names, or none (§8.5). |
| `JournalStoreLimitException` | An `ArgumentException` from `JournalStore`'s constructor naming the startup bound which fails (`JournalStoreLimit`: the payload limit, `MaxAppendBytes`, `MaxQueuedBytes`, or `MaxProposalBytes`, §8.4). |

### 4.3 Versions

| Type | Definition |
| --- | --- |
| `JournalVersion` | An opaque readonly record struct of `Orleans.Durable.Storage.Engine.Journals`, compared for equality only. A caller sees `Origin`, equality, and the text form (§7), the grain's ETag, which `ToString` formats and `Parse` and `TryParse` read; the text is canonical, so string equality is version equality. An absent journal's version is `Origin`, and its ETag is `Origin`'s text, `AAA`. Within Journals a version is `(Base, Count)`, converted to and from Ledgers' `RowVersion` ([ledgers §4](ledgers.md#4-data-types)): `Base` a `RowToken`, opaque bytes of at most 32 which the archive provider minted (`Origin`'s empty token, or a prefix's `VersionBase`), and `Count` a `u64`. |

## 5. Invariants

| ID | Invariant |
| --- | --- |
| JS-I1 | An `Append`, `Replace`, or `Delete` is accepted only when its expected version names the state the fold of its key's history before it gives (§2.1); an `Expired` only when that fold is deleted, the `Expired`'s expected version names its state, and its tombstone's deletion time plus `TombstoneRetention` is at or before the primary's clock. Each accepted entry is one row, which advances the key's version by one. An entry is `Rejected` only when it applies to none of the states the primary's applied state and each prefix of its in-flight entries of the key give, after the primary loads again a key with no recorded segment in its ledger, with nothing of it in flight or waiting, unless the key's latest load started after the entry arrived (§8.3), and it reports a name of the applied state. An `Expired` is judged against the warm applied state alone and never loads its key: while its key holds no loaded warm state, or any entry of its key is in flight or waiting, it is `Rejected` at once, a hint to its only writer, the expiry scan; with no warm state it reports its own expected version. |
| JS-I2 | A write is `Accepted` only once its batch is `Durable` and the owner's warm state has applied it. |
| JS-I3 | A read which begins after an acknowledgement reflects that entry, until a later accepted entry supersedes it; every entry a read reflects is decided. |
| JS-I4 | Every write resolves to exactly one outcome; `Rejected`, `ReplaceRequired`, `Moved`, and `Fenced` have no effect. |
| JS-I5 | Each entry's CRC-32C, over its row key and its record, is computed by the writing process, carried verbatim by forwarding, and verified where the record enters the primary, which writes the entry's row from the verified record; from there the batch's CRC-32C protects the row on every replica and for every reader, and each drain computes its store's checksums from the verified batch pages. |
| JS-I6 | Warm state lives for one primary claim; it and key indexes equal what the fold and the history give at their applied position. A key's warm state is loaded or evicted only while the primary has no admitted entry of the key in flight or waiting on in-flight outcomes. |
| JS-I7 | `Expired` is written only by the key's primary, through its expiry scan, and is accepted only by JS-I1's rule: against an applied deleted state of its expected version whose tombstone's retention has passed, with no entry of the key in flight or waiting. The writing silo stamps the tombstone's deletion time and the primary's clock judges the retention, so a deleted journal keeps its tombstone for at least `TombstoneRetention` less the largest clock skew between any two silos, which the system model assumes far below the retention. |
| JS-I8 | Retired: entries carry no blobs. The number is not reused. |
| JS-I9 | An answer (an absence, a read, or a load for admission) counts a node's key index of a live ledger only once the index has caught up to the ledger's decided end, and counts the archive only through `ArchiveReader`'s reads ([archive §2](archive.md#2-contract-upward)). For the primary's own open ledger, a read counts its index through the tail its open check confirmed, the ledger's decided end; an admission counts it through the primary's applied position, with the key's in-flight entries decided apart (§8.1). |
| JS-I10 | Admission stores, in each defining row, the key's token for that segment and its version before the row. The token is `Origin` for a key's first segment, `prefix.Token` after a rebase, and otherwise `Plan` of the key's write in its previous segment (in any ledger of its lineage), which starts from that segment's token. The version continues the fold of the rows its primary loaded (its warm version), which may since have dropped, or is `(prefix.VersionBase, 0)` after a rebase, which happens only when no ledger the map value read before the archive lists holds a row of the key. A key's versions are unique for its lifetime; a key the LSM has forgotten entirely starts a new lifetime at `(Origin, 0)` (§8.3). Each version names one state, the fold's version or, once every row of the key has dropped, the rebased version, which a primary holding the key warm from earlier rows holds beside it. |

## 6. Faults, concurrency, and cancellation

- **Primary failure.** Entries in flight are `Durable` below the takeover's recovered tail and absent above it. An
  `Ambiguous` entry is one of them; its writer continues from a read (§2.2).
- **Fencing on the primary.** After a `Fenced` or `Ambiguous` proposal outcome, the primary discards its in-flight
  state, answers every entry admitted against it `Fenced` or `Ambiguous` as the ledger decides, answers every entry
  waiting on them `Fenced`, since it proposes nothing more, and admits again only on a new open ledger.
- **Lost warm state.** A claim's end or a restart discards warm state; the next touch of each key recomputes it, and
  key indexes catch up from their durable watermarks (§8.3).
- **Concurrency.** Admission of one key is serialized on the primary; keys are independent.
- **Quorum out of reach.** A history read, a read's or a key's load, which meets a `Ledger`'s `AggregateException`
  because no decision reached its quorum while a repetition may succeed ([ledger-protocol
  §6.4](ledger-protocol.md#64-failures)) fails with `JournalStoreUnavailableException`, whose inner exception is the
  aggregate: unavailability, which asserts nothing about the journal ([outcomes §8](../outcomes.md#8-error-taxonomy)).
  It has no effect: a read changes nothing, and a write which fails with it waited for its key's load and was never
  proposed, so its caller may send it again at the same expected version, as the Orleans composition does
  ([Orleans §5.5](../compositions/orleans.md#55-forwarding-and-journal-outcomes)). A write already proposed meets no such
  failure, since an append never throws it ([ledger-protocol §2.4](ledger-protocol.md#24-append-outcomes)); its ledger
  decides it (§2.2). An aggregate of failures no repetition changes, the corruption of more replicas than the quorum
  tolerates among them, propagates as the ledger's.
- **Corruption.** `JournalStoreCorruptException`: a record which §7's decoding rejects where it enters the primary; a
  `Delete` row whose body is neither empty nor 8 bytes, or whose deletion time is out of range; and the archive's
  corruption, which Journals reports as `JournalStoreCorruptException`: an `ArchiveCorruptException`, a stored token
  or defining version which breaks the key's chain among them, which `ArchiveReader` checks
  ([HI-I4](archive.md#5-invariants)).

## 7. Formats

**Entry record.** The encoded entry which the writing process computes once and the composition carries
verbatim to the key's primary (JS-I5). It is never stored: the primary verifies it and writes the entry's row (§4.2), so
the record carries no format version, and its layout is the cluster's one current layout; the composition's forwarding
message type, which carries it between processes, versions it with the rest of the message
([Orleans §5.5](../compositions/orleans.md#55-forwarding-and-journal-outcomes)). The row's key carries the
journal key (§4.1), so the record does not repeat it, but its CRC-32C covers it: the CRC is over the row key and then
the record's bytes after the CRC, so a record verifies only under the row key it was written for. Integers are
little-endian, and the varint is unsigned LEB128 in its shortest form, at most 10 bytes. In layout order:

| Field | Bytes |
| --- | --- |
| CRC-32C of the row key, then every byte of the record after this field (`u32`) | 4 |
| Kind (`u8`): 0 `Append`, 1 `Replace`, 2 `Delete`, 3 `Expired` | 1 |
| Expected version: the base's length (`u8`, at most 32), the base's bytes, then the count (varint) | 2 to 43 |
| Deletion time, a `Delete`'s only: UTC ticks (`i64`), 0 to 3,155,378,975,999,999,999 (`DateTime.MaxValue`) | 8 |
| Body: an `Append`'s payload, non-empty, or a `Replace`'s snapshot, possibly empty; a `Delete`'s and an `Expired`'s is empty | The rest |

Each value has one encoding. The CRC leads the record, so a writer which holds the payload in pages computes it over the
row key, the head's fields, and then the payload, and the record is its head followed by the payload's pages, which the
primary copies once, into its batch ([conventions §1](../conventions.md#1-buffers-and-ownership)). A body's bound,
`MaxBodyBytes`, is a precondition the writer and admission check (§8.4), not a decoding rule.

Decoding takes the row key and the record, and accepts only this layout. It checks these rules in this order, and a
rejection names the first rule the record breaks by its reason, which the primary reports as
`JournalStoreCorruptException` ([conventions §15](../conventions.md#15-outcomes-unions-and-errors)):

1. `truncated`: the record holds its CRC, its kind, and its base's length, 6 bytes.
2. `crc-mismatch`: the CRC-32C matches the row key and the record's bytes after the CRC.
3. `unknown-kind`: the kind is 0 to 3.
4. The expected version, in layout order: `base-too-long`, a base length above 32; `field-truncated`, a base which runs
   past the record's end; `bad-varint`, a count which is overlong, longer than 10 bytes, above 2<sup>64</sup> − 1, or
   unterminated.
5. A `Delete`'s deletion time: `field-truncated`, fewer than 8 bytes left; `bad-deletion-time`, a value outside 0 to
   `DateTime.MaxValue`'s ticks.
6. The body: `empty-append`, an `Append` with an empty body; `trailing-bytes`, a `Delete` or `Expired` with bytes after
   its last field.

**Row bodies.** An `Append`'s and a `Replace`'s row body is the entry's payload. A `Delete` row's is the tombstone, the
deletion time as UTC ticks (`i64`, little-endian, in the record's range), or, for an `Expired`, empty. The fold rejects
any other `Delete` body (§6).

**Version text form.** A `JournalVersion`'s text, the grain's ETag, is base64url without padding
([RFC 4648 §5](https://www.rfc-editor.org/rfc/rfc4648#section-5)) of the base's length (`u8`), the base's bytes, and the
count as a shortest varint: 3 to 58 characters. Parsing decodes it, checks the length and the varint as §7's record
does, re-encodes the value, and accepts only text equal to the re-encoding, so each version has one text and string
equality is version equality.

**Row key.** `xxhash32` of the journal key's strict UTF-8, seed 0, as 4 big-endian bytes, then the UTF-8 (§4.1).

Golden vectors: `test/Durable.Storage/shared-vectors/journals/entries.json`, `entry-rejections.json`, `versions.json`,
`version-rejections.json`, and `row-keys.json` ([formats §6](../formats.md#6-golden-vectors)), each record with the row
key it was written for, the row it is written as (its row kind, its version before, and its body), and rejections which
break two rules for the earlier; the `journals-entry` fuzz target, whose input frames a row key and a record, and the
`journals-version` target, whose input is a version's text, which arrives from Orleans as an ETag
([verification §6.1](../verification.md#61-decoder-fuzzing)).

## 8. Mechanism and policy

### 8.1 Admission

On the primary of the key's open ledger, per key, serialized:

1. Check the entry, which needs no state, so admission checks it first, as the write arrives: its key lies in the
   ledger's range (else `Moved` without an owner); the archive store admits its key (`AdmitsKey`) and its body is at
   most `MaxBodyBytes` (else `ArgumentException`); and its CRC-32C was verified where the record entered the primary
   (JS-I5). An `Append`'s body is non-empty in every record: the writing process's encoding refuses an empty one with
   `ArgumentException`, and §7's decoding rejects one as `empty-append`.
2. Ensure the key's warm state (§8.3). An `Expired` never loads its key: one whose key holds no loaded warm state is
   `Rejected` at once, with no effect and no history read, reporting its own expected version, a hint which names no
   state the primary read (§8.6).
3. Decide the entry's version against the key's possible states: the applied state, and the state after each prefix of
   the key's in-flight entries, any of which the ledger may decide ([LG-I5](ledgers.md#5-invariants)). While entries
   are in flight the key's base is fixed and each in-flight entry adds 1 to the count, so the possible states' versions
   are the applied state's and one more count per in-flight entry, and the entry's expected version names at most one of
   them. An `Append`, `Replace`, or `Delete` applies to the state it names. An `Expired` is judged against the warm
   applied state alone, and never reloads its key: while any entry of its key is in flight or waiting it is `Rejected`
   at once, and otherwise it applies only when the applied state is the one it names, deleted, and its tombstone's
   deletion time plus `TombstoneRetention` is at or before admission's clock (`TimeProvider`).
   - An entry which applies to the state after every in-flight entry goes on to step 4. One which applies to a state no
     row can advance, whose count is the `u64` maximum, fails with `ArgumentException`, as `BeginRow` would, and leaves
     the admission and the key's other entries as they were.
   - An entry which applies to none of the possible states is answered `Rejected` at once, with the warm state's
     `CurrentVersion`, which is applied and therefore durable.
   - Any other entry waits for the in-flight entries' outcomes, holding the key's later entries behind it. It ends
     `Rejected`, decided again once every in-flight entry has landed, or `Fenced`, since excluding an in-flight entry
     takes a seal, which fences this primary (§6).
4. For an `Append`, check the archive store's per-generation limits. Admission does not know yet whether the entry's
   batch continues the key's recorded segment or starts another, so it asks `Admits` for both, on the key's admitted
   fold (**Tokens** below): the key's write in its recorded segment through its admitted entries, grown by the
   `Append`, and an empty write from that write's `End()`, grown by the `Append`, on a fold begun from it, which is the
   same write while the admitted write is empty. When both admit, the entry goes on to step 5. When either refuses and
   the key has no entry in flight, the answer is `ReplaceRequired`, decided against the applied state and with no effect
   (JS-I4); with entries in flight, the entry waits for their outcomes and is decided again from step 3, since they
   decide the write it would join. While entries are in flight and the admitted write has reached the store's replace
   request (`IsReplaceRequested` of its `End()`), an `Append` waits for them before it is judged, so a write which
   batches split across segments passes what the candidates judged by at most the headroom the provider keeps between
   its replace request and its refusal ([archive §3](archive.md#3-requirements-downward)). Judging both candidates is
   conservative: a key loaded from a closed segment records it, and its next batch starts another, so the joined
   candidate may refuse an `Append` the new segment admits, and the key answers at most one `ReplaceRequired` early,
   after which the writer's `Replace` opens a generation.
5. Add the entry to the in-flight set and to the next batch. Once the batch's creation gives its segment identity,
   admission plans its rows' tokens in row order, under its lock. When the batch's segment identity differs from the
   key's recorded one, admission first rolls the key's token: `End()` of the key's written fold for the recorded
   segment becomes the token, both folds begin again from it (`Begin`), with the batch's identity recorded, and the
   admitted fold takes again the key's admitted entries no batch holds yet, this one first. A row rolls exactly when it
   is the key's defining row in its segment, which a debug assertion checks against the batch's dictionary
   ([ledgers §7](ledgers.md#7-formats)); others check that the written fold admits each `Append`, as step 4
   judged, and that the command the folds take is the archive command of the row the entry writes. It writes the row
   through `BeginRow(key, kind, versionBefore, token, bodyLength, bodyHead)` and `WriteBody`
   ([ledgers §4](ledgers.md#4-data-types)), adds the entry's command to the written fold (`Add`), and adopts the
   version `BeginRow` returns, which a debug assertion checks equals its own expectation. A `BeginRow` which throws
   leaves the writer's dictionary unchanged. Each row reserves a token of 32 bytes until its batch plans it. Batches go
   to `ProposeAsync` in order; a batch which took no slot names no segment, plans no token, and is `Fenced`.
6. On `Durable`, admission, warm state's one applier, applies the batch, removes its entries from the in-flight set,
   and completes them `Accepted` with their versions.

`LedgerAdmission` admits the entries of one open ledger for one primary claim, and holds the warm state of the keys it
admitted. Its mechanism:

- **Records.** An entry reaches admission as a `JournalEntryRecord`, which carries its row key and exists only as the
  writing process's encoding or a verified decoding of the bytes forwarded to the primary, so the CRC-32C of step 1 is
  verified once, where the bytes enter the primary (JS-I5). Admission retains its own lease on the body until the batch
  holds a copy of its bytes. Only the expiry scan admits an `Expired`, through `ExpireAsync` (JS-I7).
- **First touch.** A key's first entry starts its one load (§8.3); every entry of the key waits for it, and a load
  which fails fails them with its exception, and the key's next entry loads again.
- **Possible states.** Admission keeps the applied state's version and the in-flight entries in order, so step 3 is one
  comparison of counts; an `Expired`, judged only with nothing in flight or waiting, reads the applied state's
  tombstone.
- **Tokens.** Admission keeps one planning state per key (`JournalPlanning`), constant in size, which the key's load
  gives (§8.3): the recorded segment identity and the token there, and two incremental `Plan` folds of the key's write
  there. The written fold takes the rows batches hold, and plans each row's token (step 5). The admitted fold takes
  every admitted entry as though it joins the recorded segment, which step 4's two candidates read; a roll founds it
  again from the entries admitted against the old segment which no batch holds yet. Once nothing of the key is in
  flight, both folds hold the same write, which a debug assertion checks where each batch lands.
- **Preconditions.** An entry whose row steps 1 to 4 refuse throws or is answered before it is admitted, so every row of
  a batch is within the batch's checks and no batch refuses a row.
- **Batches.** The admitted entries form batches in admission order, each at most `MaxBatchEntries` entries and, past
  its first entry, `MaxBatchBytes` of rows and the replica's payload limit, written into a `LedgerBatch` without
  building a `Row` per entry ([ledgers §2.4](ledgers.md#24-primary-ledgerwriter)); an entry whose body exceeds
  `MaxBatchBytes` rides in a batch of its own. At most `K`, the ledger's window, are outstanding, holding at most the
  writer's `MaxProposalBytes`, so entries which arrive while the window or that budget is full share the next batch.
- **Backpressure.** The rows of entries accepted and not yet in a batch are at most `MaxQueuedBytes`; a write past it
  waits, neither loaded nor decided, until a batch takes entries, and then is accepted in arrival order.
- **Fencing.** The first proposal which is not `Durable` ends admission (§6): its entries take its outcome, every entry
  not yet proposed or waiting is `Fenced`, and so is every later write. A proposal already made answers its entries as
  the ledger decides it, a `Durable` one after it has been applied. A proposal the writer's disposal refused before it
  took a slot is `Fenced`, and one its disposal ended afterwards is `Ambiguous`.
- **Faults.** A fault of admission's own while it builds, plans, or proposes a batch, or in a proposal's outcome,
  fails that batch's entries with the fault, and one in the store's folds while it decides an entry fails that entry;
  the fault asserts nothing, ends admission as a fence does, and abandons the writer (`LedgerWriter.Abandon`), so
  failure handling replaces the ledger; admission proposes nothing after it. A `Durable`
  entry which does not apply to warm state stops the process
  ([conventions §14](../conventions.md#14-local-storage-failures-stop-the-process)).
- **Answers.** Admission changes its state under its lock and completes writes, releases payloads, and starts loads and
  the pump after leaving it. It never holds its lock across `CreateBatchAsync`, which may wait for admission's own
  earlier proposals to be decided.
- **Disposal.** Disposal ends admission as a fence does and waits for the outstanding proposals' outcomes, which the
  writer decides. Admission tolerates its writer's disposal, before or after its own: the writer then decides each
  proposal from its view, or ends it with `ObjectDisposedException`, which admission answers `Ambiguous`, and a batch it
  refused to create or propose leaves its entries `Fenced`. `JournalStore` is disposed before its
  `LedgerWriterRegistry`, so the writers decide from the view what the registry's disposal would leave `Ambiguous`.
- **Cancellation.** A caller's cancellation ends only its own wait and asserts nothing: its entry may still be proposed
  and decided (§2.2).

`ReplaceRequired` is a backstop: the archive store's replace request (§2.4), which each `Accepted` answer carries,
answers well before its limits refuse, so a grain snapshots first. The composition carries the answer back through
forwarding, and the writer's storage instance latches the replace request until a `Replace` of the key is accepted. A
writer which recovers after `ReplaceRequired` does so through a new instance, whose read carries the request: a store
asks for a `Replace` from every token from which it refuses an `Append` ([HI-I5](archive.md#5-invariants)), so the
grain's next write is a `Replace` ([Orleans §5.5](../compositions/orleans.md#55-forwarding-and-journal-outcomes)).

### 8.2 Reads on the owner

The primary answers from one cut: one read of the archive through `ArchiveReader`, with the key's live rows through the
tail its seal check of the open ledger confirmed, which is the read's open check. The read is its admission's only when
it is linearizable, its open ledger (`ArchiveRead.OpenLedger`) is the admission's, and the registry still serves the
admission's writer; otherwise it answers `Moved` (§8.5). It then waits until its admission has applied every one of its
own `Durable` batches below that tail (`LedgerAdmission.WaitAppliedAsync`): until it has applied through a slot at or
above the tail, or has answered every batch it took before the wait, since a batch taken later gets a slot at or above
the tail, and a slot below it which is not the admission's, such as a segment marker, needs no wait. The wait is why a
write at the version a read returns is never rejected for a `Durable` batch the read saw and admission had not yet
applied, such as an `Expired` of the `Delete` the read saw. An admission which has fenced answers the wait at once,
and the read `Moved`. The content, `Exists`, `ContentLength`, and `CurrentVersion` are all the fold of those same rows,
by §8.3's rules, so the version a read returns is the version of the content it returns, never warm state's newer one.
A read answers only from the process which serves the key's open ledger's writer, where the open check covers every
proposal answered `Durable` (LG-I8): a history read which is not linearizable, as in a process the primary moved away
from during it, is answered `Moved` or forwarded to the primary (§8.5), never served from a contiguous tail. An absent
journal answers from the caught-up key indexes and the archive's `Absent` answer: on the LSM with no object-store
request once the range's section metadata is preloaded, and on archive logs with one read of the key's log
([LSM](archive-lsm.md#8-mechanism-and-policy), [archive logs](archive-logs.md#8-mechanism-and-policy)) (JS-I9).

### 8.3 Warm state

Each node keeps, in its state database:

- **Key indexes (`IJournalIndex` and `RocksDbJournalIndex`)**: for each live ledger the node tracks, the keys its
  decided batches touch, each entry with its row's tag, its defining row's slot, and its ordinal, applied in slot order;
  each ledger's watermark in the journal index is the index's next slot to apply. An index persists across restarts and
  catches up from its durable watermark. `JournalStateDatabase` maintains them through the narrow `IJournalIndex`
  contract (`OpenAsync`, `ApplyBatchAsync`, `GetKeyEntriesAsync`, `HasKeyEntryAsync`, `ScanTombstonesAsync`,
  `DropLedgerAsync`, `FlushAsync`, `ResetAsync`, `CheckAsync`), with `InMemoryJournalIndex` for tests and simulation and
  `RocksDbJournalIndex` (`Orleans.Durable.Storage.RocksDb`) on disk at `{root}/index/`:
  - **Column families and fixed-width encodings.** `key_index` maps a 52-byte key (`LedgerId` 8B BE || `SHA-256(rowKey)`
    32B || `Slot` 8B BE || `Index` 4B BE) to a 16-byte value (`DefiningSlot` 8B LE || `Ordinal` 8B LE); `tombstones` maps
    a 40-byte key (`LedgerId` 8B BE || `SHA-256(rowKey)` 32B) to `(DeletionTicks 8B LE || DeleteSlot 8B LE || DeleteIndex
    4B LE || RowKey)` (written on `Delete` rows, deleted on same-ledger `Expired` or same-batch superseding live rows,
    with cross-batch live resurrections filtered via `key_index` during expiry scans); the default column family holds
    one 8-byte key (`LedgerId` 8B BE) per ledger mapped to its 8-byte LE durable watermark (`NextSlot`). Point reads
    verify the defining row's full key bytes so a hash collision is skipped safely.
  - **WAL-less writes and atomic multi-CF flush.** `RocksDbJournalIndex` writes every batch directly into RocksDB's
    native memtables with `disableWAL = true` (eliminating RocksDB WAL I/O and fsyncs) and opens RocksDB with
    `atomic_flush = true`. Each batch's `key_index` writes, `tombstones` writes/deletes, and watermark advance commit
    together in one `WriteBatch`, so lookups see every batch's changes atomically. Periodic/size-bounded and clean
    shutdown flushes (`FlushAsync`) issue an atomic multi-CF flush (`rocksdb_flush_cfs` with `force_atomic_flush`) so
    `key_index`, `tombstones`, and `watermarks` advance together durably in SSTs + `MANIFEST`; `db_write_buffer_size`
    bounds total memtable memory so crash replay from `LedgerStore` starting at each ledger's durable watermark stays
    short.
  - **Domain-tuned options and fast ledger drops.** `RocksDbJournalIndex` disables memory-mapped I/O explicitly
    (`allow_mmap_reads = false`, `allow_mmap_writes = false`), configures prefix bloom filters (`FixedPrefix(40)` on
    `key_index` and `FixedPrefix(8)` on `tombstones`), runs all blocking RocksDB calls on dedicated threads (never
    thread-pool threads), and drops a ledger via whole-ledger `WriteBatch.DeleteRange` on `[ledgerId_be, (ledgerId+1)_be)`
    (with an inclusive `0xFF`-padded end key for `ulong.MaxValue`) plus watermark deletion, followed after flush by
    `DeleteFilesInRange` on `key_index` and `tombstones`. All prefix scans bound their upper end in managed code (keeping
    `ReadOptions.SetIterateUpperBound` banned).
  - **Fail-stop vs corruption boundary.** RocksDB I/O failures report through `FatalErrorHandler` with distinct
    `FatalErrorSite` values (`JournalIndexOpen`, `JournalIndexRead`, `JournalIndexWrite`, `JournalIndexFlush`,
    `JournalIndexDrop`, `JournalIndexReset`, `JournalIndexCheck`) and stop the process; an already-held DB lock throws
    `JournalIndexLockHeldException`; RocksDB `Corruption` statuses and malformed stored rows throw
    `JournalIndexCorruptException`, on which `JournalStateDatabase.OpenAsync` resets and rebuilds the derived index from
    the live ledgers.
  - **Wiring.** `JournalStateDatabase.Applier` implements `LedgerApplier`, `JournalStateDatabase.KeyIndex` implements
    `ArchiveKeyIndex` (wired into `ArchiveReader` when `JournalStore` is constructed with a `JournalStateDatabase`), and
    `Attach(LedgerApplyLoop)` lets catch-up waits wake a replicated ledger's delivery (`LedgerApplyLoop.Wake`) and observe
    loop completion or faults. A background map-watcher drops indexes for non-replicated predecessors once the map drops
    them.
- **Warm state**: on a primary, `JournalWarmState` holds `JournalState` and `JournalPlanning` per touched key, owned
  exclusively by `LedgerAdmission` for one primary claim and discarded when the claim ends or the process restarts
  (JS-I6).

**Catch-up.** A node counts its index of a ledger only once the index has caught up to the ledger's decided end
(JS-I9), and catch-up runs once per ledger:

- for a ledger the node replicates, its apply loop does it, since Ledgers delivers every decided batch through a
  closed ledger's `T` ([ledgers §8.5](ledgers.md#85-replica-lifecycle-on-a-node));
- for a live predecessor the node does not replicate, as after a takeover onto a node outside its replicas, one
  `LedgerReader.ScanAsync` builds the index from slot 0 to its `T`, so the index answers for every live row, below an
  LSM watermark too. Every answer which needs it shares that scan, and the node keeps the index until the ledger
  drops.

An answer which needs an index waits for its catch-up. Because `RocksDbJournalIndex` runs WAL-less with atomic multi-CF
flushes, a crash leaves durable watermarks at the last atomic flush, behind applied memtable watermarks, and a new
primary's indexes of its predecessors reach only as far as its apply loop has, so replay and catch-up are ordinary after
a restart or a takeover.

**First touch.** A primary loads a key's state at its first touch in the claim from one read of the archive's prefix,
`ArchiveReader.ReadStoreAsync(key)`, with the ledger-map value read before it (HI-I1, HI-I2), and the key's live rows in
the ledgers that value lists, which the caught-up key indexes locate, merged by the answer's `Merge`
([archive §8.2](archive.md#82-first-touch-and-absence)). The answer holds while that value's open ledger for the key is
unsealed, which the primary's open check confirms before the load counts.

A key's **live rows** are its rows in every ledger the map value lists, from slot 0, whether or not the archive holds
them.

- **Live rows exist.** A listed ledger holds a row of the key, so the version and the planning state come from those
  rows:
  - The newest listed defining row gives the version base and count before it, and the fold through the key's live rows
    after it gives `CurrentVersion`. Every store's reader scans each listed ledger from slot 0, `Holds` marking the
    rows the archive holds, so the slow path and the key indexes see every live row of the key, that one included.
  - The stored versions and tokens of the live rows the archive does not hold continue the chain JS-I10 states, from
    the prefix's `Token` for the earliest such write, which `ArchiveReader` checks ([HI-I4](archive.md#5-invariants));
    Journals reports a break as `JournalStoreCorruptException` (§6).
  - The planning state is the newest live segment's when the archive holds no row of it: its identity, its stored
    token, and the fold of the key's write there, whether the segment is still open or closed, since a batch of another
    segment rolls it. A held row lies in a closed segment, which a drain copied, and may have lost the tombstone its
    command needs, so then the key has no recorded segment, and its next write starts from the token the read gives
    after its last write (`ArchiveRead.NextToken`, [archive §2.2](archive.md#22-history-reads)); its next batch's row
    defines it in a new segment.
  - The content, `Exists`, and `DeletedAt` are the archive prefix's, followed by the key's live rows the prefix does not
    hold, by `Holds`, which is also the only check of the prefix against the chain.
- **No live rows exist.** The primary **rebases**: the version becomes `(prefix.VersionBase, 0)`, and the key's token
  for its next segment is `prefix.Token`, with no recorded segment until its next batch; on `Absent`, `(Origin, 0)` and
  `Origin`. The content, `Exists`, and `DeletedAt` are the prefix's (`Content`, `HasBase` or non-empty content,
  `Tombstone`).

"No live rows" is judged over the map value read before the archive: no listed ledger containing the key holds a row of
it, from slot 0, which the caught-up key indexes answer on either store. A listed ledger is never trimmed below a slot
the prefix does not hold, and a reader which finds a ledger's trim floor above what the prefix holds answers `Stale`
([HI-I1, HI-I2](archive.md#5-invariants)).

**A rebase is fresh.** Every ledger which held the key's rows dropped before the map read, and a drop requires the
archive to hold every row, so the prefix reflects every command of the key ever decided. The archive's `VersionBase`
advances with every command it applies and never repeats while it holds the key
([HI-I4](archive.md#5-invariants)), so an earlier version `(b, k)` equals `(prefix.VersionBase, 0)` only when it was
taken at an earlier rebase with no command decided since: the state is then the same, and a stale writer's version is
correct for it. Archive logs never reuse a token, since logs are never deleted and every `Delete` opens a new
generation, so an expiry repeats no version there. The LSM's base is its last stored row's tag; once compaction has
dropped an expired key entirely ([LSM-I5](archive-lsm.md#5-invariants)), the key reads `Absent` and rebases to
`(Origin, 0)`, starting a new lifetime whose `Origin`-based versions may repeat an earlier lifetime's: the one case in
which a key's version may repeat, an ABA hazard bounded by the key's `TombstoneRetention` and the compaction which
forgot it (JS-I10; [archive §8.3](archive.md#83-version-bases-and-rebases)).

**A warm key keeps its version as a name.** A primary which loaded a key from a predecessor's rows, as a split's child
or a successor does, holds the key's version after the predecessor drains and drops, while a read then folds no live
row and names the same state by the rebased version: the state has two names, each its alone. Before rejecting an
entry of a warm key at a version other than its warm one, admission loads the key again when the key has no recorded
segment in admission's own ledger, when nothing of it is in flight or waiting on in-flight outcomes (JS-I6), and
unless the key's latest load started after the entry arrived. A key with no recorded segment here holds a version
from rows which may since have dropped, or from the archive alone, whose LSM may since have forgotten an expired key
entirely and read it `Absent` at `(Origin, 0)` (JS-I10). The last condition bounds the cost: a burst of stale writes
loads the key at most twice, once for the first rejection and once for those which arrived during that load. The load
either confirms the warm version or rebases, and admission decides the entry again against the loaded state, so a
writer at the name the primary now holds is accepted, and a stale writer is rejected with it. With nothing in flight no
command intervenes, so the loaded state is the warm one but for its version, which a debug assertion checks. Each such
load counts in `orleans.durable.storage.journals.reloads` (`{load}`, meter `Orleans.Durable.Storage.Engine.Journals`). A
key with a recorded segment in admission's ledger needs none: its row there is live while the ledger is, and its version
is the state's only name. An `Expired` never loads its key again, nor loads a cold one (§8.6): its expected version came
from the key's warm state, so one which meets another is stale, and is rejected from the warm state.

A load's obligations, which `LedgerAdmission` states for the loader it is given: the state is the fold of every row of
the key decided before the load (JS-I9), this ledger's included; it holds none of the key's rows admission has yet to
apply, since a key loads only with no admitted entry in flight or waiting on in-flight outcomes, which an assertion
checks (JS-I6): a Debug-only count, per row key, of the admitted entries not yet landed, kept apart from the key's
state so that it outlives an eviction or a failed load, must be zero when a load starts, when a load fails, and at
eviction; the state becomes
admission's, and the loader keeps no reference to it; the load's cancellation means admission's disposal; and a failure
fails the writes waiting for it. A loader gives the key's load from its history read, `JournalLoad.Of(read, store)`:
its state, `JournalState.Fold(read)`, and its planning state, `JournalPlanning.Load(store, read)`, so the prefix, the
live rows, and the next token come from one read. A load whose read is not its admission's to answer (§8.2), as after a
split or a handoff during the read, answers `Moved` (`JournalLoadOutcome`): the admission answers the key's waiting
entries `Moved`, naming the primary the map lists, proposes nothing, and leaves the key unloaded, so its next touch
loads again. Its admission, warm state's one applier, keeps the state current by applying its own decided batches.

Warm state is bounded by a byte budget (§8.8): past it, admission evicts the least recently used keys with no admitted
entry in flight or waiting on in-flight outcomes, which an assertion checks too, and a later touch loads them again. An
evicted key's own decided rows are in the ledger the load reads, so the later load is complete and does not rebase.

### 8.4 Bodies and memory

Every body is inline, in its record and its row: no entry travels as a blob. `MaxBodyBytes` is Journals' admission
policy, a cluster parameter which the composition records among the caller parameters of the store's record
(`max-body-bytes`, [archive §2.4](archive.md#24-selecting-the-store)), 16 MiB by default and configurable up to the
replica's payload limit less a maximal row's overhead, a 65,535-byte key, a 32-byte version base and token, and headers,
about 64 KiB; `JournalStore.StoreParameters` gives the composition the parameter from the store's own copy of its
options, which it enforces. An `Append`'s or a `Replace`'s body above it is refused with `ArgumentException` by the
writing process before it forwards the entry, and again by the owner (§2.2, §8.1). Ledgers bounds a body only by its
batch's length.

Memory in flight is bounded by bytes, not by counts of entries or slots: on a primary, per ledger, admission holds at
most `MaxQueuedBytes` of entries not yet in a batch, and its `LedgerWriter` at most `MaxProposalBytes` of batches
created and not yet decided ([ledgers §2.4](ledgers.md#24-primary-ledgerwriter)), 128 MiB together by default; a
forwarded write waits on the composition's per-node forwarding byte budget
([Orleans §5.5](../compositions/orleans.md#55-forwarding-and-journal-outcomes)). `JournalStore`'s constructor, the
process's start check, copies its options and runs three checks on the copy, after each setting's range, and throws
`JournalStoreLimitException` naming the bound (`JournalStoreLimit`) which fails, so the process refuses to start:

- the largest batch admission builds (`LedgerAdmission.LargestBatch`), the larger of a batch of `MaxBatchEntries`
  rows within `MaxBatchBytes` and a batch of one row with a 65,535-byte key and a `MaxBodyBytes` body, or a tombstone,
  with the batch's and the row's headers, a 32-byte version base and token among them, is within the replica's payload
  limit ([ledgers §7](ledgers.md#7-formats));
- `MaxBodyBytes` is at most the archive store's `MaxAppendBytes`, so the archive never refuses an `Append` for its size;
- `MaxQueuedBytes` and the writer's `MaxProposalBytes` are each at least that largest batch, so a maximal entry always
  fits.

| Limit | Default | Relation to a maximal body |
| --- | --- | --- |
| `MaxBodyBytes` | 16 MiB | A `Replace`, so a grain's snapshot, is bounded by it. |
| Replica payload limit | 32 MiB | Holds the largest batch admission builds, checked at startup. |
| `MaxBatchBytes` | 1 MiB | A larger body rides in a batch of its own. |
| `MaxQueuedBytes` | 64 MiB | Holds four maximal bodies waiting for batches; at least the largest batch, checked at startup. |
| `LedgerWriter.MaxProposalBytes` | 64 MiB | Holds four maximal batches in flight; at least the largest batch, checked at startup. |
| Archive `MaxAppendBytes` | 100 MiB on Azure | At least `MaxBodyBytes`, checked at startup. |

A writer's payload crosses as an `ArcBuffer`, which the store retains while it needs the bytes; the writing process
encodes the record from its pages, and the primary copies them once, into its batch.

### 8.5 Forwarding

A process which is not the key's primary forwards the encoded entry to the primary named by its cached map, or answers
`Moved` to its caller; the composition chooses
([Orleans](../compositions/orleans.md#55-forwarding-and-journal-outcomes)). `JournalStore` answers `Moved` from the
process which does not serve the key's open ledger's writer, naming the primary its map view lists. When that names
this process, its registry has yet to adopt the writer or has stopped serving it: the store waits until the registry
has reconciled with the map view as it stands and routes the write or read once more, and when the process still serves
no writer for the key it answers `Moved` naming no owner, so the caller reads the map again. A `Moved` has no effect
(JS-I4), so the same record may be sent again. A forwarded write waits for the per-node forwarding byte budget, apart
from the replica calls' own, and one whose wait reaches its deadline fails with no effect. A forwarded read is served
from one snapshot the owner pins, one archive read and the live rows through its cut, in pages of at most 1 MiB, each
of which waits on that forwarding budget.

### 8.6 Tombstone expiry

Every `ExpiryInterval`, each primary scans its warm and indexed state for journals whose tombstone's deletion time plus
`TombstoneRetention` is at or before its clock and proposes, for each, an `Expired` whose expected version is the
deleted state's, through admission. The scan covers only the keys its warm state and key indexes hold. The scan's
candidates are hints: admission decides each `Expired` against the key's warm state (JS-I1), and never loads the key
for it, so a journal written or deleted again since the scan keeps its state, and a retention which has not passed, a
key with an entry in flight, or a key no longer warm, evicted since the scan read it, is `Rejected`, with no history
read. Retention far exceeds the drain age, so the `Delete` has usually left the ledgers, and the `Expired` applies to
the state warm state holds by the version it holds: the deleted state's warm name, or its rebased one, by the prefix's
`Tombstone`, once the key loaded after its rows dropped. An `Expired` is no use of its key: once decided, its key
returns to the least recently used end of warm state, whatever its position before.

`JournalStore.ExpireTombstonesAsync` runs one scan. Its warm candidates are the keys each admission's `JournalWarmState`
holds loaded, with nothing waiting or in flight, deleted with the retention passed on admission's clock, and a count
below the `u64` maximum (`LedgerAdmission.AddExpiryCandidates`), each read under its admission's lock after the store's
is released. When `JournalStateDatabase` is attached, `JournalStore.ExpireTombstonesAsync` also collects cold tombstone
candidates from `JournalStateDatabase.CollectExpiredTombstonesAsync` across each active primary's open ledger (waiting
through its contiguous tail) and each overlapping closed predecessor (scanned once per pass, skipping keys already warm
or superseded by a newer row in `key_index`), and `JournalExpiryScan.RunColdAsync` proposes them with bounded cold-load
concurrency (`MaxColdExpiryConcurrency`) through `JournalStore.ExpireColdAsync` into `LedgerAdmission.ExpireColdAsync`,
which loads the key through the unified single-flight load path without evicting live warm keys and applies JS-I1's
version, tombstone, retention, and idle checks before proposing `Expired`. Tombstones whose ledger has already drained
and dropped before `TombstoneRetention` elapses are archived and not tracked in the per-ledger `tombstones` table.
`JournalExpiryScan` proposes warm candidates in rounds of at most `MaxBatchEntries`, each awaited before the
next, so one scan holds at most one batch's entries in admission, and its `Expired`s share batches through admission's
own batching. It answers a `JournalExpiryScanResult`, the count of each outcome: `Rejected`, `Moved`, `Fenced`, and
`Ambiguous` are expected, a candidate being a hint, and the next scan proposes again whatever warm state still holds
deleted. A proposal which fails, as a fault of its admission's own fails it, counts as `Failed`, asserts nothing, and
ends nothing but itself: the scan goes on with the rest. Only a cancellation, which asserts nothing of what the scan
proposed, or the store's disposal (`ObjectDisposedException`) ends a scan early, once the round it met them in has
ended. In the Orleans composition, the node's control loop starts a scan on the thread pool each `ExpiryInterval` while
the node's Journals serve, at most one at a time, so collecting its candidates never holds the loop; it logs a scan
with failed proposals as a warning, and its stop awaits the scan ([orleans
§5.6](../compositions/orleans.md#56-controllers)).

The `Expired`'s row, an empty `Delete`, empties the journal in every reader's fold at once. The LSM drops it, and every
older row of the key, once nothing older remains outside a compaction ([LSM-I5](archive-lsm.md#5-invariants)), while it
keeps a tombstoned `Delete` as the key's base; an archive log opens an empty generation for either `Delete`, which plain
Orleans reads as an empty journal ([archive logs](archive-logs.md#5-invariants)).

### 8.7 Snapshot requests

A read which assembles more than `(1 + α) × Sz` bytes of a journal's content, where `Sz` is its base's length, the
content its last opener or the archive's prefix began, requests a snapshot (`α = 1`), and so does a primary whose warm
`JournalState.BytesSinceBase` exceeds `α × BaseLength` (`ContentLength > (1 + α) × BaseLength`), so a journal which is
written and rarely read stays bounded too. The archive store's `IsReplaceRequested`, over the key's current token,
requests one too, well before its per-generation limits refuse an `Append` (§8.1): admission reports the union of
`JournalState.IsReplaceRequested` and the store's `IsReplaceRequested` at the key's admitted token on each `Accepted`
answer, and a read reports the union of the assembled-byte ratio and the store's `IsReplaceRequested` at its next
token. "Replace request" names both triggers, which a read's and an acceptance's `ReplaceRequested` answer. The
snapshot arrives as an ordinary `Replace`.

### 8.8 Policy

| Setting | Default |
| --- | --- |
| `MaxBodyBytes`: an entry's body, recorded per cluster in the store's record, at most the payload limit less a maximal row's overhead (about 64 KiB) | 16 MiB |
| Batch: entries, bytes, linger (`MaxBatchEntries`, `MaxBatchBytes`) | 256, 1 MiB, 0 (batch what is queued) |
| Rows accepted and not yet proposed, per ledger's admission (`MaxQueuedBytes`); at least the largest batch | 64 MiB |
| Warm state, per ledger's admission (`WarmStateBytes`), counting each key's state and its planning state, whose folds `ArchivePlan.MaxStateBytes` bounds | 64 MiB; least recently used keys with nothing waiting or in flight are evicted |
| `ExpiryInterval`: the least time between the starts of a primary's expiry scans, which never overlap; above zero | 1 hour |
| `TombstoneRetention` | 7 days |
| History reads the store runs at once, its loads and its owner reads together (`MaxConcurrentHistoryReads`); a read past it waits, in arrival order, and holds its slot only while it reads the archive and the replicas | 4 |

`LedgerAdmission` copies the settings it uses when it is created. `MaxConcurrentHistoryReads` bounds concurrent archive
store reads and assembled history memory while key indexes and preloaded LSM metadata avoid slow-path ledger scans and
object-store reads for absent keys. A read holds no slot while it waits for anything else, its key's apply
through the read's cut among them, so no read waits for a slot behind one which waits for it. The slots are a FIFO
budget of calls alone (`CallBudget.ForCalls`, `Orleans.Durable.Storage.Engine.Threading`), and the store's disposal
withdraws a read waiting for one, which throws `ObjectDisposedException`, without effect (§2.2).

## 9. Providers and implementations

`JournalStore`, over `LedgerWriterRegistry` and its reader's `LedgerMapView`, an `ArchiveReader` it creates over that
reader, so a seal check in its reads covers the writers the registry serves, the archive store's pure functions, and
optionally `JournalStateDatabase` (whose `ArchiveKeyIndex` feeds `ArchiveReader`'s key-indexed point reads), admits
each ledger's entries through that ledger's `LedgerAdmission`, which proposes
through the ledger's `LedgerWriter`. Under its lock it reads the registry, creates a writer's admission at the writer's
first use, at most one per writer, and retires one once the registry no longer serves its writer, registering the
retirement's disposal in the same section. It notices a retired writer at each use and, once the registry has
reconciled with each newer map value or stopped serving a writer, in a sweep which follows the registry's. The
follower's baseline, the view's epoch and the unserved ledgers, is taken as the store is constructed, before any
admission exists, so every change which unserves an admission's writer wakes it, however late its task starts. It
encodes entries (`EncodeAppend`, `EncodeReplace`, `EncodeDelete`), writes an encoded or forwarded record
(`WriteAsync`), and runs the expiry scan (`ExpireTombstonesAsync`, §8.6), which the composition schedules. Its disposal
retires every admission and waits for their proposals, and the composition disposes it
before its registry (§8.1); a call which meets the disposed store fails with `ObjectDisposedException`, without effect
(§2.2). It has one implementation; the composition forwards entries to it on the key's primary.

## 10. Verification

- **Model.** [`LedgerMap.qnt`](../../../specs/ledgers/LedgerMap.qnt) admits an entry against the history of every
  ledger up to the primary's own: `HistoryOrdered` checks JS-I1 (mutant `own-ledger-only`), and `AckedInHistory` and
  `ReadsSeeAcked` check JS-I2 and JS-I3 with the ledger and LSM invariants. It carries the token chain across ledgers
  through split, merge, and takeover (JS-I10): the mutants `token-from-wrong-parent` and
  `token-includes-fenced` break `TokensReplayPlan`, and `withdrawn-defining-batch` breaks `ReferencesResolve`.
  `ArchiveLogs.qnt` models a loader (its map read, archive read, and rebase) beside drains and drops, and checks the
  rebase by `VersionsUnique`.
- **Oracle.** Every optimized answer (warm state, key indexes, absence through the archive's `Absent`, content reads) is
  checked against the fold of `ArchiveReader`'s history read in deterministic simulation with takeovers, splits, merges,
  drains, compactions, and restarts (JS-I6). For JS-I6 and JS-I9 the schedules include a primary restarted with its
  durable watermarks behind its applied ones, a handoff target whose index of its predecessor lags the predecessor's
  `T`, a takeover onto a node outside a predecessor's replicas, and a range handed away and back while its keys gained
  entries, were deleted, expired, and compacted away in the LSM on the other primary: each answers absence, admits the
  version `(Origin, 0)` or the rebased one, and proposes `Expired` only as the fold does. Mutants which count an index
  before its catch-up, or keep warm state across claims, fail. The simulation in code (rows 2.3b D and B.12) runs
  takeovers, splits, merges, drains, and drops over `InMemoryArchiveStore` under a fault plan ([verification
  §7.3](../verification.md#73-faults-and-workloads)): among its faults, a primary's process crashes with its node, and
  the process which starts in its place loads each key again from the ledgers and the store, with no warm state across
  the crash. `JournalStateDatabaseTests` (row 5.2 part D) verifies JS-I6 and JS-I9 for per-node durable key indexes
  against the unindexed slow-path oracle across restarts with lagging durable watermarks, corrupt DB auto-reset on open,
  open-ledger catch-up waking `LedgerApplyLoop`, takeovers and handoffs whose predecessor indexes lag `T`, ranges handed
  away and back while keys are written/deleted/expired/drained and ledgers dropped on `InMemoryArchiveStore`, one shared
  scan for a non-replicated closed predecessor (including mid-scan drop and restart), `ApplyLoop` stop/fault waking
  waiters, key/value codec ordering and corruption checks, hash-collision filtering, and stale-entry rejection
  (`unknown-ordinal`), catching `SkipCatchUpMutant` and `StaleOrdinalMutant` deterministically; `ArchiveKeyIndexTests`
  and `LsmAbsentBoundsTests` verify point-read slot counts, row-for-row CsCheck parity, async `Absent.Bounds`
  cancellation on `Dropped`, `unknown-ordinal`, and `Absent` per-ledger bounds including LSM compaction of empty
  expiries (`BoundCompleteLedgerMutant`). `JournalWarmStateTests`, `SectionMetadataCacheTests`, and
  `JournalWarmStateSimulationTests` (row 5.2 part E) verify per-claim `JournalWarmState` lifecycle and eviction,
  `ArchiveReader.ReadStoreAsync` first touch and owner reads, zero object-store calls for preloaded absent keys on the
  LSM, warm-state `BytesSinceBase` snapshot requests (§8.7), cold tombstone expiry from `JournalStateDatabase`'s
  `tombstones` table, and JS-I6/JS-I9 against the unindexed slow-path oracle in Clockwork simulation across restarts
  with lagging durable watermarks, takeovers and handoffs whose predecessor indexes lag `T`, ranges handed away and
  back across LSM compactions and key forgetting, and evictions, catching the mutants `SkipCatchUp`,
  `KeepWarmStateAcrossClaims`, `UncoveredDrop`, `IgnoreRetention`, and `ApplyOnAdmit`; `ExpiredWriterTests` verifies
  single-writer discipline over both warm and cold expiry paths, and
  `RocksDbJournalIndexTests` verifies WAL-less atomic multi-CF flush durability, zero-mmap configuration
  (`allow_mmap_reads = false`, `allow_mmap_writes = false`), prefix bloom filter lookups, whole-ledger `DeleteRange` +
  `DeleteFilesInRange` drops (including `ulong.MaxValue`), SST corruption detection and reset, malformed-row corruption,
  lock-held vs fatal I/O reporting, and DI wiring (`AddRocksDbJournalIndex` / `AddRocksDbJournalIndexForNode`);
  `LocalJournalsTests.NodeRuntimeWiresOptionalJournalIndexIntoJournalStateDatabase` and silo DI tests for
  `AddDurableJournalIndex` / `AddRocksDbJournalIndex` verify Orleans DI wiring. A handoff,
  in which a primary hands its own ledger to another node, is not expressible in that simulation: a transition's
  executor becomes the primary of the ledgers it opens, so a takeover by the other process stands for both, and
  `JournalStoreTests` holds a handoff to another node by construction.
- **Chain checker.** A global checker after every simulation step replays `Plan` per key across segments and ledgers
  and checks every stored token and defining version against it (JS-I10), with debug assertions where admission writes a
  defining row and where loads read one. Admission's two chain mutants, a token not rolled at a segment change and a
  defining version which does not continue the fold, are in code (row 2.3b B): the memory store's drain catches the
  first and a check of each key's row versions the second, and in simulation the chain checker catches the first and
  warm state's apply, which refuses a decided row whose version does not follow the key's, the second.
- **Versions.** A global checker asserts that a key's versions never repeat, except along the `Origin`-based versions of
  a new lifetime of a key the LSM has forgotten. It identifies each state of a key by its newest row in the reference
  history, and records every name the answers give it, each read's, acceptance's, and rejection's, so it finds a version
  naming two states. A rebased version a read or a rejection reports must come after the drop of every earlier ledger
  holding a row of the key, as the register applied it, and take the store's `VersionBase` at that load as its base: a
  read's is checked as it answers and again over the whole reference once the run ends, which holds the rows of ledgers
  closed but not yet drained when it answered. A defining row's rebase is judged by its base alone, the token `Plan`
  replays over the key's drained rows, which no rebase over rows not yet drained gives.
  Rebase cases: a key whose every row has dropped reloads as `(prefix.VersionBase, 0)`; an `Expired` after its `Delete`
  has dropped applies to the rebased state by the prefix's `Tombstone` and the version; a load while an entry of the
  key is in flight fails the per-row-key count's assertion where the load starts or the key is evicted (mutant
  `evict-with-inflight`); a writer holding a pre-drop version gets one `Rejected`, then succeeds after a read; a key
  warm in a split's child from its parent's rows accepts, once the parent has dropped, a write at its warm version and
  one at the rebased version a read gives, the latter after one reload, and stale writes reload a warm key only with no
  recorded segment in admission's ledger and nothing of it in flight, at most twice per burst (mutant
  `rejects-without-reload`, which rejects the write at the rebased version); a reload with no command since the last
  rebase gives the same version and
  accepts the stale writer, correctly; an expiry on archive logs never repeats a version; an LSM key which compaction
  forgot rebases to `(Origin, 0)`. The rebase mutants `rebase-with-live-rows`, `rebase-from-map-after-archive`, and
  `rebase-on-stale-token` are implementation mutants of row 2.3b: the version checker fails the last two, and the
  history read's version-continuity check (HI-I4) refuses the first before it. In simulation `rebase-on-stale-token`
  fails the version checker at its first rebased read, and a read which rebases while rows are live
  (`read-rebase-with-live-rows`) fails the read bounds.
- **Cold primary.** A metamorphic property restarts the primary at every cut while a live segment is partly drained:
  while the key has live rows, the cold state equals the warm primary's `JournalState`; after its last row drops, the
  content, `Exists`, `DeletedAt`, and the planning token are equal, and the version is `(VersionBase, 0)`. Mutants:
  replay without `Holds`, and no lazy roll.
- **Rejections.** Each `Rejected` in simulation records the applied position it was decided from, the key's in-flight
  entries, and the reload condition's inputs; a white-box check confirms the fold through that position reports its
  `CurrentVersion`, that the entry applies to no state any prefix of those entries leaves, and that a key the condition
  required loading again was loaded after the entry arrived. A progress check at quiescence finds no entry waiting once
  faults stop and the in-flight batches are decided; a mutant which never decides a waiting entry again fails it, a
  mutant which rejects an entry some prefix state admits fails the white-box check, and one which rejects without the
  reload (`rejects-without-reload`) fails the reload condition. `JournalModel` requires a rejection's `CurrentVersion`
  to name the current state, relaxed only for an owner the simulation records as deposed, whose ledger the map no longer
  lists open when it decides (§2.1): its version is a hint, a version the journal held or a rebase no answer named,
  which binds nothing. The scripted conflicting writes' rejections, from an owner the map lists open, must be recorded
  as not deposed, which an oracle mutant recording every rejection deposed (`deposed-always`) fails.
- **`ReplaceRequired`.** In `JournalModel`, an answer without effect at a name of the current state, or of a state the
  journal held for a deposed owner, and decided only from the applied state; the latch until a `Replace` is accepted is
  the writer's storage instance's, which the Orleans adapter holds (row 4.2), and a recovering instance's read carries
  the request again (HI-I5). A white-box oracle records, for each `ReplaceRequired`, that the key had no entry in flight
  and that `Admits` refused one of the two candidate writes; the mutants which answer it with entries in flight, or when
  both candidate writes are admitted, fail it, in the property test and in simulation, where scripted appends reach the
  store's limit. Boundary tests run over `InMemoryArchiveStore` with unit limits (row 2.3b), and the Azure layout's
  block boundaries (39,999 and 40,000 for the replace request, 49,899 and 49,900 for the refusal) run over
  `LogArchiveStore`'s Azure layout (`ArchiveLogPlanTests`).
- **`Expired`.** A stale `Expired` whose state was written again, one whose retention has not passed, and one whose key
  has any entry in flight, its `Delete` included, each `Rejected` at once; the mutant which admits an `Expired` without
  the retention check fails.
- **Read bounds.** Each owner read records its cut, the tail its open check confirmed, and the simulation compares its
  answer with the fold through that cut, which checks JS-I3's upper bound directly: each cut lies within what was
  decided when the read answered, and covers every write acknowledged before it began. The version a read returns is
  the fold's through the same cut. Warm state's reads come with row 5.2, and with them its mutants: `apply-on-admit`,
  which applies entries to warm state at admission, one which answers warm state's newer version, and the kit's handoff
  case, in which the old primary answers a read after the seal.
- **Linearizability.** Each client history of a simulation or end-to-end run is checked per journal against
  `JournalModel`, the semantics of §2 as a sequential specification with opaque versions and `ReplaceRequired`
  ([verification §3.4](../verification.md#34-linearizability)), with ambiguous writes recorded with unbounded
  responses.
- **Conformance.** The Journals spec (`JournalStoreSpec`, [verification §3.1](../verification.md#31-contract-specs)),
  whose state is each journal's `JournalModelState`, judges `JournalStore` over live ledgers on memory replicas and
  `InMemoryArchiveStore` with unit limits through the conformance kit (`JournalStoreConformanceTests`): CsCheck's
  sequences and concurrent groups of writes at the newest and at stale versions, reads, and expiry scans, and every
  sequence of three of nine calls. Every answer goes through `JournalModel`; the spec adds that a serving process
  answers a steady write `Accepted`, `Rejected`, or `ReplaceRequired`, one serving none `Moved`, that the store's limits
  refuse an `Append` exactly at the generation's limit, and the replace requests of §2.4. Its environment's steps are a
  takeover, a write held at its load across one (`Fenced`, `Moved` while the registry does not yet serve the
  successor's writer, or answered by the successor), and a crash once a write's batch reaches its replica
  (`Ambiguous`, a pending landing), after which the next process answers `Moved` until a takeover. No ledger drops, so
  the spec holds every answer to a name of the state, which catches a version naming no state that `JournalModel`,
  taking it as a rebase, allows. A
  mutant per clause fails the spec: an `Append` accepted at a stale version; `ReplaceRequired` below the limit and an
  `Append` accepted past it; `Moved` and `Fenced` entries which land when sent again; an `Ambiguous` `Append` counted
  twice; an `Accepted` and a `Rejected` version naming no state; a read missing an acknowledged entry or reflecting an
  undecided one; a `Delete` and a `Replace` folded wrongly. `SimulatedJournalStoreConformanceTests`
  (`Orleans.Durable.Storage.Simulation.Conformance.Tests`) runs the same spec in Clockwork simulations over
  `SimulatedConditionalRegister`, yielding memory replicas, and `SimulatedArchiveStore`: 28 curated concurrent windows
  covering empty, cross-key, warm and cold populated, limit-reached, deleted/expiry-scan, taken-over, fenced, and
  crash-recovered states explored by `WindowScheduleExplorer` (every third window explored at the gate and the rest
  seeded; all 28 explored under `DURABLE_STORAGE_SCHEDULE_BOUND=deep`), 100 seeded 3-replica quorum cases with latency
  and archive stale-snapshot restarts, and two admission race-mutant witnesses
  (`LedgerAdmission.RejectionMutant.DecidesWhileLoading` and `RejectsWaiting`), each passing the default schedule and
  caught by one preemption. Drains, compactions, and sweeps as spec step functions are not yet in code.
- **Tests.** Entry codec vectors, the version's text form and its non-canonical rejections; admission under concurrent
  writers and fencing, including a primary whose ledger a handoff seals mid-batch, whose entries resolve `Durable` below
  `T` and `Fenced` above it; a conflicting entry which waits while its key has entries in flight and ends `Rejected`
  once they land, or `Fenced` when a seal excludes one, including an in-flight `Expired` which the seal fences; an entry
  rejected at once while entries are in flight because it applies to no prefix state; the early refusals at and past
  their bounds (an empty `Append`, a body of `MaxBodyBytes` and one byte more, a key `AdmitsKey` refuses, a count at
  `u64` maximum); a body above `MaxBatchBytes` in a batch of its own; the token roll at a segment change; a `Delete`,
  then an `Append` in a later segment, then a reload, after which the key exists; an empty `Replace`, after which it
  exists, and an `Expired`, after which it is absent, each before and after its rows drain; an `Expired`, then an
  `Append`, with the count still advancing; CRC-32C verification at the primary; and page leak checks.
- **In code** (row 2.3b A to C). `JournalVectorTests` read the entry, version-text, and row-key vectors in both
  directions, compare every field with `EntryRules`, an independent reading of §7, and check the rejection vectors'
  reasons against §7's rules in order; `JournalEntryCodecTests` decode generated entries under generated keys, judge
  mutated encodings by the `journals-entry` fuzz oracle, reject by its CRC-32C a record under another row key (JS-I5),
  check that `JournalRowKey.TryDecode` decodes every well-formed key's row key and that any bytes it decodes are
  exactly that key's row key, and check that every version's text parses back and is its only text; each entry vector
  also names its row, the row
  kind, the version before, and the body, which the record gives. `JournalsCorpusReplayTests` replay the seeds and
  corpora of the `journals-entry` target, re-merged from the record's seeds, and of the `journals-version` target, whose
  oracle `VersionRules` reads the text form apart from the parser. `JournalFoldTests` check the fold of a prefix and the
  live rows it does not hold, wherever the prefix ends and however many live rows it holds, against an independent model
  of the whole history, with the rebase when no row is live; a read's content folded from the same prefix and rows into
  non-empty chunks, against the model's bytes and its base's length; warm state applying later entries against the fold;
  existence after an empty `Replace` and an `Expired`; the planning state of the newest unheld segment, and of a partly
  held newest write whose held opener is a tombstoned `Delete`, from the read's next token; a state no row can advance;
  warm state's per-key estimates against a folded state's and a planning state's measured allocations; and each
  corruption. `LedgerAdmissionTests`, over a scripted ledger: acceptance only once `Durable` and applied (JS-I2), with
  the row's version; rejection and waiting against possible states by version, a version of another base among them; an
  `Expired` admitted only once its retention has passed, a stale one, and one with an entry of its key in flight or
  waiting, each rejected at once; entries behind an in-flight `Expired`, fenced or decided by the version it leaves; a
  count at its maximum, refused for its entry alone, an `Expired` among them; a retention of at least zero; rows of
  native content; the per-entry checks, the startup check's boundaries, batching, backpressure, eviction and reload, a
  rebased load, loads, fencing, faults, among them the store's folds throwing as an entry is decided and as a batch is
  planned, disposal, and cancellation. `LedgerAdmissionPropertyTests` drive writes to two keys, the expiry scan,
  advances of the clock across a two-day retention, and segment ends against proposals decided in order, under a budget
  which keeps or evicts each idle key, checking progress, JS-I2, and warm state against the fold, and JS-I1 against an
  oracle of the accepted writes alone: each key's accepted entries chain from `Origin`, and an accepted `Expired`
  follows an accepted `Delete` whose retention passed, with nothing of its key in flight. Its memory store holds one
  append per generation: a white-box oracle checks each `ReplaceRequired`, with nothing of its key in flight and one
  candidate write refused, which the mutants `AnswersWithEntriesInFlight` and `AnswersWhenBothAdmit` fail, each by its
  own check; each key's rows follow one another; and every `Durable` row drains into the store, which checks the token
  chain and its limits. `LedgerAdmissionPlanningTests`: the lazy roll at a segment change, from the segment's last
  opener; a roll with entries admitted against the old segment; a load continuing its open segment, and a reloaded key
  going on in it; `ReplaceRequired` at the store's unit limits, after an opener, and after entries in flight land; an
  `Append` waiting once the replace is requested; queued bytes released by answers before a batch; a store limiting one
  write's appended bytes, under which the joined candidate alone refuses and a roll founds the admitted fold again; the
  chain mutants `TokenNotRolled` and `VersionOffTheFold`, which the drain's chain check and the version check catch; and
  a key `AdmitsKey` refuses. The scripted ledger checks each proposal against the largest batch the options allow.
  `LedgerAdmissionWriterTests` run admission over a real `LedgerWriter` on in-memory replicas. `JournalStoreTests`, over
  live ledgers and the memory store, which checks each drained key's token chain, hold each rare path by a gated archive
  read, replica opening, write answer, seal, or sweep: writes accepted only at the journal's version and reads folding
  the history, with `ContentLength`; the archive store's replace request on `Accepted` and on reads, and a read's own
  past twice its base; `ReplaceRequired` past the store's limits; `Moved` naming no owner before bootstrap; the refused
  preconditions, encoded or received; a version continuing across ledgers and the rebase once every ledger holding a
  key's rows dropped, each load counted; existence read and reloaded before a drain, between it and the drop, and after;
  an `Expired` applying to a rebased state; a read disposed partway; a load whose admission the follower retires
  mid-read, `Fenced`, and one the map moved past with the sweep held, `Moved` and routed again to the child; a read
  routed again after a split; a write held while its ledger is replaced, `Fenced`; a writer whose lifetime stops
  mid-read, with its recovery's seal held, `Moved` naming no owner; a ledger handed to another node, `Moved` naming it;
  a read waiting for a decided batch admission has not applied, after which a write at its version is accepted; routes
  racing the registry's adoption, with one admission per writer; an admission the follower retires once its writer
  stops, with no later call; a follower whose task starts only after a split still retiring the parent's admission,
  from the baseline the store took as it was constructed; the store's disposal waiting for its writes and then
  refusing every call; the registry
  disposed first, `Ambiguous`; the archive's corruption as the journal's; a quorum out of reach, with every replica
  partitioned, as the store's unavailability for a read and an unproposed write, after which the journal reads as it was
  and a write at its version is accepted; the largest batch pinned by hand; the startup
  checks at their boundaries, each refusal naming its bound; the store's parameters; and the history-read bound, at most
  `MaxConcurrentHistoryReads` reads at the archive, a slot held only while its read runs, returned after corruption and
  cancellation, and a read waiting for one withdrawn by the store's disposal; and the expiry scan (row 3.4b), on a fake
  clock: a warm journal's `Expired` proposed once its tombstone's retention has passed and not a tick before, after
  which it reads absent at the version after its `Delete`; nothing for a journal written since its `Delete`, or deleted
  again until the later tombstone's retention passes, or with an entry in flight; and, in rounds of one, a candidate
  written while the first round's `Expired` was in flight `Rejected`, which mutants scanning without the retention, at
  the instant strictly, beside an entry in flight, or in one unbounded round each fail.
  A candidate evicted before its round is `Rejected`, reading no history.
  A cancellation during the first round of one, or between rounds, leaves the other candidate deleted; a scan racing
  the store's disposal ends with `ObjectDisposedException`; and a proposal which faults counts `Failed` while the next
  round's expires.
  After a split, the scan takes candidates from both children's admissions.
  `LedgerAdmissionTests` add an `Expired` of a key never loaded or evicted, rejected with its own version and no load;
  an `Expired`, accepted or rejected, leaving its key at the least recently used end, so a third key evicts it before a
  live one; and no candidate from a key waiting for its reload or from a fenced admission. `ExpiredWriterTests`
  (`Orleans.Durable.Storage.Architecture.Tests`) pin each step toward an `Expired` to its one caller under src (JS-I7),
  the scan's own run among them, which a second caller of the store's `ExpireAsync`, or of the record's `Expired`,
  fails. `LedgerAdmissionTests` refuse each setting out of its range, naming the setting
  (`JournalStoreOptions.Validate`). Every test class holds a page leak check.
- **In code** (row 2.3b D). `JournalSimulationTests` run three ledger-store nodes, two primary-capable processes, each
  with a registry, a transition executor, and a `JournalStore`, and a drainer over `InMemoryArchiveStore`, with
  concurrent writers, readers, each process's expiry scan (`ExpireTombstonesAsync`), whose every proposal the history
  records as a write, and random replaces, splits, and merges. Scripted steps force
  each rare path on every seed: a takeover during a read, after which a write sent to the deposed process is `Moved`;
  a write across a segment's end; a `Replace`, which opens a generation, and then three writes at once which
  meet an entry in flight; appends to the store's limit; two rebases, each a stale write warming the successor, then a
  write at the rebased version a read gives; a delete and its expiry; a split and a merge; and a read while every
  replica is partitioned, which must fail with `JournalStoreUnavailableException` carrying the quorum's aggregate of
  missed deadlines (§6), and a read once they reconnect. After every step the
  chain and version checkers judge each drained ledger. At the end the final ledgers join the reference history, every
  journal's final read must be its whole reference's fold, and the white-box oracles (rejections, `ReplaceRequired`,
  read bounds, acceptances against the decided history, each journal's `Delete` and `Expired` rows counted against its
  accepted and ambiguous writes of each, and names) run before each client history is checked against `JournalModel`.
  Each oracle fails a mutant on the step which forces its path: the chain mutants, `RejectsWaiting`,
  `NeverDecidesWaitingAgain`, `rebase-on-stale-token`, `read-rebase-with-live-rows`, `rejects-without-reload`,
  `AnswersWithEntriesInFlight`, `AnswersWhenBothAdmit`, and the oracle mutant `deposed-always`; each step a mutant runs
  alone also passes without it, a control run. Row B.12 adds the fault plan of [verification
  §7.3](../verification.md#73-faults-and-workloads) to every run, the mutants' among them, healed before the scripted
  steps: a journal process crashed with its node while a write is in flight on it, which ends unanswered, and restarted
  in its place, which replaces the ledgers still named under the crashed process's claims; a partition; a pause; lost
  and duplicated messages and lost answers; a restarted node's media damaged; unknown ledger map outcomes; two committers
  racing one transition; and an executor interrupted once it submits. A controller replaces each ledger a process lists
  unserved, and once the faults heal every open range is served and every call answered. A restarted process's expiry
  scan records its proposals as its predecessor's did; a crash stops a process's scan, and a proposal the crash cut, or
  which failed while the faults ran, is an unknown write, while one which fails once they heal fails the run.
  `TheCorpusFiresEveryFault`
  requires each fault's evidence on each seed. `JournalRebaseTests` run the rebase mutants against the
  version checker and the history read, `JournalColdPrimaryTests` the cold-primary property, `VersionCheckerTests` the
  checker's own cases, and `JournalModelTests` the model's. The simulation's cost is in
  [verification §12](../verification.md#12-gates-and-measurements).
