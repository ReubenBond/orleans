# Archive

## 1. Purpose and scope

The **Archive** is the layer which keeps every key's **history**, `History(k)`: its decided commands in order, across
the live ledgers ([ledgers](ledgers.md)) and the **archive store**, the store the live ledgers drain into. It has two
implementations, and a cluster uses one archive store for its life:

- **The LSM** ([archive LSM](archive-lsm.md)): an LSM tree of immutable objects in cloud object storage, with a manifest
  of sections and per-ledger watermarks, compaction, and preloaded Bloom filters.
- **Archive logs** ([archive logs](archive-logs.md)): one log per key, in Orleans' own journal layout on Azure append
  blobs, which plain Orleans can read, or as chunk objects on any store with create-once writes, with the live ledgers
  as its write-ahead log.

The layer owns:

- the definition of `History(k)` and its reads: the history read, and the first-touch read of the store, which answers
  absence;
- the archive store contract, `IArchiveStore`, which both stores implement: the read of a key's **prefix**, the part of
  its history the store holds, and the provider's pure members over **archive tokens**, which plan, admit, and judge
  each key's writes;
- what a store **holds**, which `Holds` decides, and the merge of a prefix with the live rows it does not hold;
- the coverage condition under which a closed ledger leaves the ledger map (HI-I2), which each store meets with its own
  evidence;
- the token chain's check (HI-I4), whose producer is Journals ([JS-I10](journals.md#5-invariants));
- `ArchiveCorruptException`.

Each store owns its drains, formats, token encoding, and garbage collection. [Ledgers](ledgers.md) owns rows, tags,
segments, versions, the ledger map, and drops, and stores each defining row's token and version base verbatim;
[Journals](journals.md) owns the meaning of rows, writes them, chooses each defining row's token with `Plan`, and reads
journals through this layer.

Code: `Orleans.Durable.Storage.Engine.Archive`, data plane in `Orleans.Durable.Storage.Engine.Archive`
(`ArchiveReader` and its types, and `IArchiveStore`, the layer's data-plane interface implemented by both stores),
control plane in `Orleans.Durable.Storage.Engine.Archive.Control` (`DrainScan`, `ByteBudget`, and
`ILedgerLocalDrainBarrier` / `LocalDrainBarrierOutcome` for closed-ledger local-stream draining, *barrier not yet in
code*, J2); the memory store, `InMemoryArchiveStore`, for tests and simulation, in
`Orleans.Durable.Storage.Engine.Archive.Memory` (§9).

### Design rationale

**One history, two stores.** The LSM and archive logs make opposite trades. The LSM's object-store requests scale
with ledgers and drain windows, and it answers absence from preloaded Bloom filters with no request; it needs an object
store, a manifest committer, compaction, and a sweeper. Archive logs keep each key's drained commands together in a
store an application's journals may already use, such as Azure append blobs in Orleans' own layout: requests scale with
the keys a drain touches, and a cold read and an absence answer each cost one read of the key's log, whose bytes grow
with the log; they need no manifest or compaction ([archive logs](archive-logs.md)). The choice is a deployment's, so
the layers above read one history through one contract.

**One layer named the Archive.** The layer is the Archive, and its two implementations are the LSM and archive logs:
`Orleans.Durable.Storage.Engine.Archive` holds the contract and the reader, `Orleans.Durable.Storage.Engine.Archive.Lsm`
and `Orleans.Durable.Storage.Engine.Archive.Logs` the stores, and every type carries its layer's noun (`IArchiveStore`,
`ArchiveReader`, `LsmArchiveStore`, `LogArchiveStore`). `History(k)` is the name of what the layer keeps, and HI the
prefix of the invariants which guard it; the LSM's invariants are LSM-I1 to LSM-I7 (LSM-I8 is retired) and archive logs'
LOG-I1 to LOG-I6 (LOG-I4 is retired). A retired prefix or number is never reused, so a stale citation names no invariant
rather than a different one, and the architecture tests reject the retired names; AL, the natural prefix for archive
logs, is a retired prefix of the ledger protocol's IDs. `IObjectStore` lives in its own component,
`Orleans.Durable.Storage.Engine.ObjectStore.Abstractions`, since the memory, file-system, simulated, and cloud object
stores implement it, the file-system provider references the contract without the LSM, and archive logs' chunk layout
uses it too. A History layer beside an LSM
named the Archive was rejected: the LSM is one of two implementations, and naming it after the layer made the second
implementation read as an exception to it.

**The Archive as its own layer.** Journals reads history and plans its writes without naming a store; the LSM and
archive logs implement one interface and know nothing of each other or of journals, so the dependency rules hold the
boundary. Three alternatives were rejected. Archive logs as a second representation inside the LSM would leave the
manifest, sections, and compaction as dead weight in one mode and make the layer mean two things. Journals defining the
store interface would make the keyed layers depend on Journals. `IArchiveStore` inside Ledgers would make the layer of
replicated ledgers own history reads and the archive's token semantics.

**Opaque tokens.** Each store tracks a key's coverage in its own terms: the LSM by per-ledger watermarks, an Azure log
by its generation, length, and block count, a chunk log by its chunk chain. A **token**, at most 32 opaque bytes
(`RowToken`, [ledgers §4](ledgers.md#4-data-types)), carries that state for one key, and only the provider which mints
it decodes it: Ledgers stores and lends it verbatim, Journals keeps and passes it, and this layer's callers compare
tokens only for equality, which each provider's canonical encoding makes byte equality (HI-I3). Ordering, coverage, and
predecessor checks stay inside the provider, behind `Holds`. A typed position in the contract, a union of every
provider's coverage, was rejected: every layer above would learn every provider's representation, and a new provider
(the chunk layout, a native append elsewhere) would change all of them.

**One token per key per segment.** A drain writes each key once per closed segment ([ledgers
§2.2](ledgers.md#22-rows-batches-and-tags)), so a key's token changes at most once per segment, and the key's defining
row in the segment stores the token the segment starts from. A token per command was rejected: one archive write per
command, and a token in every row. A token per ledger was rejected: a ledger lives for days, and its one write per key
would land only after the ledger closed, holding every row live until then.

**Pure members, so admission never waits.** `Plan`, `Admits`, `Holds`, `IsReplaceRequested`, `MaxAppendBytes`, and
`AdmitsKey` do no I/O, and the store fields they depend on are fixed for the cluster's life (HI-I3). Admission
therefore plans each defining row's token, and refuses a command the store could not hold, from state it holds,
without a request to the
store on the write path; and a drain, replaying `Plan` over the same rows, reaches the same token, which the chain check
compares (HI-I4). `Plan` is an incremental fold, `ArchivePlan`, which defines both `Plan` and `Admits`: a provider
supplies only its `Begin`, so no provider can answer either otherwise than its fold. Admission keeps one fold per key's
write and asks `ArchivePlan.Admits` before each command, O(1) per command, and never rescans a segment; `Plan(write)`
and `Admits(write, command)` over a whole write (`ArchiveStorePlans`) replay it in O(write), for tests and the reference
path. Default interface members were rejected: a provider could override them and drift from its fold, and a caller
replaying a write per command would make admission quadratic. Asking the store at admission was rejected: it puts a
store request on every write, and the answer could change between the question and the drain. Configuring the parameters
per process was rejected: two processes would plan different chains for one key, which the chain check would report as
corruption.

**`VersionBase` beside `Token`.** A rebase needs a value which advances with every command the store applies and never
repeats while the store holds the key ([journals §8.3](journals.md#83-warm-state)); planning needs the token the key's
next segment starts from. An archive log's token advances with every write, so the two coincide; the LSM plans nothing,
so its token is empty, while its version base, the tag of the last row of its prefix, advances with every row. One
member for both was rejected: the LSM would have to mint ordered tokens which only its rebase uses, in every defining
row it never reads. A store whose coverage is the read's map value computes the version base over the prefix's rows,
so its freshness is a property of the reads a rebase takes, whose map lists no ledger holding a row of the key, and
every version base names one state of the key; computing it over every row the store holds, regardless of the map, was
rejected: the base would then name rows the prefix does not hold.

**Merge by `Holds`, not by tag.** Archive logs store a key's native content in Orleans' layout, which holds bytes, not
rows or tags, so the reader cannot tell which live rows a log already holds by their tags. Each segment's write is
deterministic from the decided rows ([ledgers §2.2](ledgers.md#22-rows-batches-and-tags)), so `Holds(prefix, write, i)`
decides from the prefix's coverage whether the store holds the write's first `i` commands, and the reader keeps exactly
the rows not held. A prefix holds exactly the writes its `Holds` holds. Archive logs' prefix holds the writes its
position reaches ([archive logs §8.3](archive-logs.md#83-deciding-a-failed-condition)). The LSM's prefix holds the rows
of the ledgers its read's map value no longer lists, `LedgerMapValue.NoLongerLists`, a
ledger below the value's `NextLedgerId` which it does not list, and one predicate decides both the prefix's rows and
`Holds` (`LedgerMapCoverage`, [archive LSM §2.8](archive-lsm.md#28-prefixes-and-tokens)): a ledger created after the
map was read, at or past its `NextLedgerId`, is neither, since a later map may list it and a reader then scans it. Two
predicates were rejected: a prefix holding rows `Holds` does not hold folds them twice, which a first touch, with no
seal check, would answer. A merge by tag, which needs a tag per stored command, was rejected: it would add a
per-command record to every archive log, which Orleans' layout has no place for.

**The chain a merge checks.** A prefix holds each write's commands in none, part, or all. The earliest write with none
held starts from the prefix's `Token`, and each later write's stored token equals `Plan` of the write before it (HI-I4).
A write the prefix holds in part, as a drain which crashed inside an append run leaves on archive logs, lies at `Plan`
of its held part, which is the prefix's `Token`, and leaves the next write to start from `Plan` of the whole write; a
write held after one not held whole contradicts the held commands' prefix, and is corruption. The merge finds each
write's held commands by halving, since `Holds` is monotone in `i`, which a debug assertion checks. A prefix whose
`Token` lies past `Plan` of the last write the scans met, which the prefix holds whole, reaches past the end the read
reached, which the seal check after the store read rules out; the read starts again rather than answer content newer
than its rows, and treating it as corruption was rejected, since a restart costs one rare read. Partial holds stay
possible, for archive logs' append runs: asserting none or all held was rejected.

**A broken chain on a stale map is a restart.** A reader whose map lists a ledger which has since dropped scans it
and the ledgers after it; the primary may meanwhile have rebased the key at the store's prefix, so its next row's
version starts a new base, which does not follow the dropped ledger's rows. The merge answers the break as a value; the
reader reads the map again, starts again when a ledger it scanned is no longer listed, and throws
`ArchiveCorruptException` only when the map still lists every ledger it scanned. Throwing at once was rejected: a
legal rebase would fail a lagging reader.

**A read copies its rows' bodies.** A scan resolves a key's rows in batches which hold other keys' rows, so a row
retaining its batch's pages would keep a whole batch alive per row; on the LSM and archive logs no live row is held,
so a read would pin up to its live ledgers' every batch holding the key. A read copies each body into one buffer it
owns as it collects the row, so it holds about its key's own bytes. Retaining the pages was rejected for that cost.

**One record format, one identity.** Every store's record, the LSM's `store`, archive logs' `durable-storage/store`, and
the memory store's, is one `key=value` format with one codec, `ArchiveStoreRecord` (§7), so a mismatch is decided, and
named, the same way on every store. The caller's parameters live in a namespace of their own, `caller.{key}`, and the
record's store fields and caller parameters are compared as two maps: a store field a later row adds can neither take a
persisted parameter's name nor shift another field. One flat namespace compared by position was rejected for both. A
composition records a store by its identity, `ArchiveStoreIdentity`: its kind, the prefix it lies under in its bucket or
container (`ArchiveStorePrefix`), and its ID. The kinds' values are the Archive's own, pinned and persisted: the LSM 1,
archive logs 2, and the memory store 3, which only tests and simulations use; a composition which records a kind records
these values rather than a mapping of its own ([orleans §7.3](../compositions/orleans.md#73-node-registry-clnr-v1)).

**The record's identity apart from its agreement.** `StoreRecordAsync` answers the record: its ID, with both halves of
its agreement. The store checks its own half, its store fields, which fix what its pure members mean (HI-I3): a record
whose store fields differ from the store's is not this store's, and the store refuses it, whoever calls. The caller
checks its own half, its parameters, which the store records verbatim and never compares, since they are the caller's
contract and what a difference stops is the caller's decision. The caller always learns the store's identity from the
answer, even while its parameters differ. One call which answers the ID only when both halves match, `StoreIdAsync`
alone, was rejected: a caller whose parameters differ could not learn the identity, and could tell the halves apart
only by the exception's field.
**Prefixes carry content, not rows.** A store answers a key's prefix: its token, its version base, whether a `Replace`
opened its base, its tombstone, and its content, the bytes a fold of the held commands leaves. Archive logs hold only
that content, and Journals reads a journal as bytes ([journals §2.3](journals.md#23-reads)), so neither needs the held
rows. Returning rows was rejected: an archive log would have to keep per-command framing, which plain Orleans could not
read.

**Bodies are inline.** Every row's body is inline, bounded by its writer's admission policy ([journals
§8.1](journals.md#81-admission)), so a command's bytes have one copy in the live ledgers and one in the store, and no
object holds a single command with a lifecycle of its own: no upload before the row, no sweep after the drop, and no
reader which meets a reference to an object a sweep removed. A write therefore carries only its commands' lengths.

**One read for a first touch.** A journal's first touch in a primary claim reads the store once, and the store answers
`Absent` or its prefix: the LSM skips the sections whose Bloom filters exclude the key, so an absent key costs no object
request once its range's metadata is preloaded, and archive logs read the key's log once. A separate absence answer
followed by a read was rejected: on archive logs it downloads every existing journal's log twice. Preloading section
metadata is the LSM's alone, so `PreloadAsync` is `LsmArchiveStore`'s, which the composition constructs directly, and not
part of the store contract.

**Linearizable where the writer is served, and said so.** A read finds the open ledger's tail with
`LedgerReader.ReadTailAsync`, a seal check of `Q` replicas begun after the call. A ledger has one `Ledger` per process,
which the writer registry's writer shares ([ledgers §2.5](ledgers.md#25-reads-ledgerreader)), so in the process which
serves the ledger's writer that tail covers every proposal the writer answered `Durable` (LG-I8), and the seal check
reports it (`LedgerTail.ServedHere`); in another process it is a contiguous tail, which may lag the primary's answers.
The condition is visible rather than a convention: `ArchiveRead.Linearizable` is true only when the open ledger's tail
was served here, the read names that ledger (`OpenLedger`), so a caller checks the read was of the ledger whose writer
it serves rather than inferring it from the ledgers the read scanned, and Journals answers a read which is not its
writer's `Moved` or forwards it to the primary ([journals §8.2](journals.md#82-reads-on-the-owner)). `ArchiveReader`
takes the process's one `LedgerReader`, which its writer registry shares, and its map view, rather than a view of its
own, so the reader and the registry see one `Ledger` per ledger. Taking the registry and calling `ConfirmOpenAsync` was
rejected: it runs the same seal check through the same `Ledger`, and the reader would choose between two paths by
whether its process serves the ledger.

**A reader named `ArchiveReader`.** The type is `ArchiveReader`, since a type `Archive` in namespace
`Orleans.Durable.Storage.Engine.Archive` resolves to the namespace from every other namespace of the storage.

**Restarts only for stale caches; backpressure resumes.** A `Stale` answer, a dropped ledger, or a sealed tail shows
that a cache the read relied on is out of date, so the read starts again from the map: such a restart is rare, and a
read which starts again from one map value needs no rule for joining scans made under two. Backpressure shows only that
a replica is saturated: the read waits with backoff and resumes the same ledger's scan from the slot it reached, keeping
the rows it collected, the map value, and the store's answer. A restart whose map advanced begins at once, since the
newer value is what the read lacked, and does not count toward the backoff; one whose map did not, as for a sealed
ledger the map does not yet list closed, waits.

**Liveness of a sealed open ledger.** A read which finds the open ledger sealed waits until the map lists it closed or
drops it. That rests on failure handling ([ledgers §8.6](ledgers.md#86-takeover)): a ledger is sealed only
by a recovery, whose executor closes it in the map; an executor which is interrupted after its seal leaves the ledger
`Unserved`, and the controller's `Replace` recovers it again and records it closed. Reads wait under their caller's
cancellation meanwhile.

**Every live row from slot 0, and the seal check after the store read.** A load takes a key's version and token from
its newest listed defining row, whether or not the store holds it ([journals §8.3](journals.md#83-warm-state)), so the
slow path scans every ledger the map lists from slot 0 on both stores, and `Holds` marks the rows the prefix already
holds, which come back with their lengths only. The scans need nothing from the store, so they start at once and run
together, joined in ledger order, with the open ledger's `K` reads in flight in all: a key with several live ledgers
costs no more round trips than one with one, and no more load on the replicas than one scan. The open ledger's seal
check begins once the store has answered, so its tail covers every segment a drain could have written into that prefix,
and the prefix never holds a command beyond the end the read reached; a sealed answer restarts the read. A seal check
beside the store read, which saves a round trip, was rejected: a drain which landed between the two would leave content
in the prefix newer than the rows the read returns, and archive logs keep no per-ledger bound by which the reader could
detect it.

**A first touch pairs the map value.** `ReadStoreAsync` answers the map value it read before the store, not only its
epoch: its caller finds the rest of the key's history in the live ledgers that value lists, which an epoch does not
name. The answer merges the caller's live rows into a history read with the reader's own merge, so there is one merge.

**The slow path is the oracle, not the fast path.** Without a key index, `ReadAsync` scans every live ledger containing
the key from slot 0 (or from an `Absent` answer's per-ledger bound), reading every batch of those ledgers to find the
key's rows. It is the reference implementation and the test oracle. With an `ArchiveKeyIndex` (row 5.2 part D of the
[implementation
plan](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md)),
`ArchiveReader` locates the key's slots per live ledger through the node's caught-up key indexes ([journals
§8.3](journals.md#83-warm-state)) and point-reads only the defining and row batches, while `Absent` carries per-ledger
bounds (`ArchiveStoreRead.Absent.Bounds`) so an unindexed first touch scans only above them.

**One store per cluster.** The stores' durable states differ, so a cluster changing stores would need a migration
([deferred](../deferred.md#changing-the-archive-store-of-a-live-cluster)); the composition records the store the
cluster uses, by its kind and by an identity the store keeps, with the store's fields, and keeps a process whose
store is of another kind, another store of the same kind, or holds other store fields from draining into or reading
it, and one whose caller parameters differ from serving what depends on them
([orleans §5.3](../compositions/orleans.md#53-node-registry)). A token's meaning, which the store fields fix, therefore
never changes under a key.

## 2. Contract upward

### 2.1 History of a key

A key's commands are its decided archive rows ([ledgers §2.2](ledgers.md#22-rows-batches-and-tags),
`RowKinds.IsArchiveKind`): `Append`, which applies on top of the earlier commands; `Replace`, which supersedes them and
becomes the base; and `Delete`, which supersedes them and leaves its tombstone, its body, when it has one.
`RowKind.LocalAppend` rows are ledger-local stream records excluded from `History(k)`, `ArchiveRow.Live`, and all three
`ArchiveReader` scan and point-read paths (`Collect`, `FetchSingleSlotIndexedAsync`, and `ProcessSlot`), where each
skipped `LocalAppend` key is validated against `ILedgerLocalDrainBarrier.ValidateLocalAppendKey` when a barrier is
configured (or rejected with `ArchiveCorruptException` if malformed or unregistered; [LG-I15](ledgers.md#5-invariants)).

A key's **write** for a segment is the value a drain writes for the key, a pure function of the key's rows in that
segment. It holds commands, not their bytes:

| Write | When | Commands |
| --- | --- | --- |
| Opener | The segment holds a `Replace` or `Delete` of the key | The last such row, with its kind, its tombstone (a `Delete`'s body), and its content length (a `Replace`'s body length, 0 for a `Delete`); then each later `Append` of the key in the segment, by its length |
| Append run | Otherwise | Each of the key's `Append`s in the segment, in tag order, by its length |

A write carries its segment's identity, `(ledger, start slot)`, and its **token**: the token its key's defining row in
the segment stores, which the key's previous write leaves (JS-I10), and from which `Plan(write)` starts. An opener's
write also carries `Superseded`, the count of the key's rows before the opener in the segment, which it supersedes and
whose commands it drops; `ArchiveWriteBuilder` builds a key's write from its rows, a row at a time, for the history
read's merge and for every drain, so the two agree.

A decided row of `k` is **held** by a store's prefix `p` when `Holds(p, w, i)`, for `w` the key's write in the row's
segment and `i` the row's position among `w`'s commands, counting from 1; a row before `w`'s opener in its segment,
which the opener supersedes, is held when the opener is (`Holds(p, w, 1)`).

`History(k)`: the store's prefix of `k`, its content, its tombstone, and its version base (§4), followed, in tag order,
by `k`'s decided rows in the live ledgers which the prefix does not hold. A ledger absent from the ledger map holds no
row the prefix does not hold (HI-I2). A key's **content** is the fold of its history: the prefix's content, then each
live row not held applied in tag order. A **live row** of `k` is a decided row of `k` in a ledger the map lists, from
slot 0, whether or not the prefix holds it. A key is **deleted** when the last command which supersedes its earlier ones
is a `Delete` with a tombstone and no `Append` follows it.

### 2.2 History reads

`ArchiveReader`, one per process, over the process's one `LedgerReader`, which the process's writer registry shares,
the reader's map view, and the archive store:

| Member | Outcomes |
| --- | --- |
| `ReadAsync(key)` / `ReadAsync(key, includeBodies)` | `ArchiveRead(Prefix, Rows, Through, OpenLedger, Linearizable, NextToken)`: `k`'s history as of a cut which includes every row of a ledger closed before the call began and, when `Linearizable`, every row answered `Durable` before the call began (HI-I1). `Prefix`, the store's prefix, or null when the store answered `Absent`. `Rows`, every live row of `k` (§2.1), scanned from slot 0 of every listed ledger on every store, in tag order, each with its tag, kind, version, segment identity, its defining row's token, and whether the prefix holds it (`IsHeld`); a row not held carries its body when `includeBodies` is true (default), or its body's length only when `includeBodies` is false (used by metadata-only callers such as Journals' warm-state load so non-`Delete` payloads are not copied), and a held row always carries only its body's length, since the prefix's content holds its bytes. The read checks the stored tokens and versions of the unheld rows it scanned (HI-I4) before it answers. `Linearizable` is true only when the process serves the writer of `k`'s open ledger, whose seal check here covered every proposal the writer answered `Durable` (LG-I8); otherwise the open ledger was read below a contiguous tail, and a caller answers the read as linearizable only by forwarding it to the primary. `Through` names, per live ledger of the map value containing `k`, the decided end the read reached: a closed ledger's `T`, or the open ledger's tail from a seal check begun after the call. When `Linearizable`, the tail is the primary's own and the content is exactly `k`'s history through `Through`, which Journals' loads rely on. Otherwise `Through` is a lower bound: the content is a real prefix of `k`'s history through at least `Through`, and possibly past it, when the store already holds a drain of a later cut of the open ledger than the read's tail. `OpenLedger` names the open ledger over `k` in the map value the read took, whose seal check gave its end in `Through` and whose writer made the read `Linearizable` when this process served it; null when the value lists no open ledger over `k`, as before bootstrap. `NextToken` is the token `k`'s next write starts from after every write the read met: `Plan` of `k`'s write in the last segment the scans met, held or not, computed from the scanned rows before a held row drops its body, or with no live row the prefix's `Token`, `Origin` on `Absent`; a write the prefix holds whole lies at the prefix's `Token`. A load plans `k`'s next token from it when a held row, whose tombstone it may no longer carry, lies in the newest segment ([journals §8.3](journals.md#83-warm-state)). The caller disposes the read. |
| `ReadStoreAsync(key)` | `ArchiveStoreAnswer(Map, Prefix?)`: the store's answer for `k` from one read of the store with `Map`, a `LedgerMapView.Current` value read before it; it reads both again on `Stale`. `Prefix`, the store's prefix, means `k`'s history is that prefix followed by the rows of the live ledgers `Map` lists which the prefix does not hold, which the caller finds by scanning each such ledger from slot 0; null, the store's `Absent`, that it is those ledgers' rows (HI-I1). The answer holds only while `Map`'s open ledger for `k` is unsealed: its caller, a journal's first touch ([journals §8.3](journals.md#83-warm-state)), confirms that with the seal check in its process before it relies on the answer. `Merge(live, through, openLedger, linearizable)` merges the caller's scanned rows, each ledger's from slot 0 to the end `through` names, as `ReadAsync` merges them, and answers an `ArchiveMergeOutcome` (§4): the `ArchiveRead`; `PastEnd`, after which the caller reads the map and the store again; or the `ArchiveCorruptException` naming a break of the chain (HI-I4), which the caller throws once a map read after it still lists every ledger it scanned, and otherwise reads again. The merge borrows its inputs and retains what it returns. Its caller disposes the answer. |

A read uses cached maps and restarts when a cache proves stale (§8.1); a caller sees only the outcome, or
`ArchiveCorruptException` when the store contradicts the map or itself, or the scanned rows break the token chain.

### 2.3 Coverage and drops

A closed ledger `S` leaves the ledger map by the change `Drop(S)` ([ledgers §2.6](ledgers.md#26-transitions)) only once
the archive store holds every decided row of `S` below its recovered tail `T` (HI-I2) and, on a cluster with an
`ILedgerLocalDrainBarrier`, once `EnsureLocalStreamsDrainedAsync(S, T)` has returned
`LocalDrainBarrierOutcome.Drained` for `T > 0` ([LG-I16](ledgers.md#5-invariants)). Each store states the evidence its
drop submitter holds: for the LSM, `ManifestValue.IsComplete(S)` in a manifest value read or applied ([archive LSM
§2.6](archive-lsm.md#26-dropping-ledgers)), gated up front by the barrier in `DrainWorker` before committing
`complete: true`; for archive logs, every segment of `S` complete, each key's write held, as a write the drain saw land
or a properties read shows ([archive logs](archive-logs.md#2-contract-upward)), gated by the barrier in
`ArchiveLogDrainWorker` before marking `Complete` and submitting `Drop(S)`. Drops follow each key's lineage
([LG-I10](ledgers.md#5-invariants)), and each replica node drops its replica once its view revokes it.

### 2.4 Selecting the store

The composition constructs `ArchiveReader` over one `IArchiveStore` and runs that store's drains and garbage collection
([orleans §5.6](../compositions/orleans.md#56-controllers)). A cluster uses one store for its life. Each store keeps a
**store ID**, a random 128-bit value its first user creates once (`StoreRecordAsync`), so a composition tells one
container, bucket, or table from another of the same kind and records the one its cluster uses: the store's identity,
`ArchiveStoreIdentity`, its `Kind`, its `Prefix`, and its ID.

The ID lives in the **store's record** (§7), one object per store (`durable-storage/store` on archive logs, the root
object `store` on the LSM), which the first user creates once and every later user reads. It holds the store's own
fields, its ID, its layout, and the other store fields its pure members read (§3), and an opaque set of the caller's
parameters, `key=value` pairs in a namespace of their own which the store records verbatim and never interprets; the
composition supplies its own there ([orleans §5.3](../compositions/orleans.md#53-node-registry)). Every field is fixed
for the cluster's life, and the record's two halves are compared apart. The store compares its own fields with the
record's, and refuses a record whose store fields differ in any field with `ArchiveStoreMismatchException` (§4), as a
composition refuses another kind ([OR-I20](../compositions/orleans.md#51-invariants)). It answers the record whatever
the caller's parameters, and the caller compares those, `ArchiveStoreRecord.CheckParametersMatch`, which throws the same
exception, naming a caller parameter, when they differ; what a difference stops is the caller's decision
([OR-I8](../compositions/orleans.md#51-invariants)). A caller therefore learns the store's identity even while its
parameters differ. The record's ID checks nothing by itself: a composition which recorded a store's identity compares
the store's `Kind` and the record's ID with it before it uses the store
([OR-I20](../compositions/orleans.md#51-invariants)).

## 3. Requirements downward

**Archive store** (`IArchiveStore`), from each implementation. Its reads:

| Member | Outcomes |
| --- | --- |
| `ReadAsync(key, map)` | `ArchiveStoreRead.Absent` when the key's prefix would hold no command: the store holds none of `k`, or, on a store whose coverage is `map`, none of a ledger `map` no longer lists. `Absent` may carry per-ledger bounds (`Bounds`: `LedgerId → Slot`, each a segment start at or below the store's watermark for an incomplete listed ledger whose range contains `k`) so an unindexed scan starts at `bound` instead of slot 0; a store never emits a bound for a complete ledger. Otherwise `ArchiveStoreRead.Read(ArchivePrefix)`, which its caller owns and disposes: the key's prefix (§4), which holds exactly the writes its `Holds` holds, and whose `Holds` covers every decided row of `k` in a ledger `map` no longer lists, below its `T`; a reader scans each ledger `map` lists whose range contains `k` from slot 0 (or point-reads its indexed slots when using an `ArchiveKeyIndex`), so it sees every live row, held or not. Either answers from the store's state as of a read begun after the call, or from cached state which covers every ledger `map` no longer lists, where `map` is a ledger map value the caller read before the call. `ArchiveStoreRead.Stale` when the store's own snapshot proved out of date, such as an object a cached manifest names which a compaction removed, or when `map` proved older than the snapshot, which shows complete a ledger `map` lists open: the caller reads the map and the store again. `ArchiveCorruptException` when the store contradicts itself. |
| `StoreRecordAsync(parameters)` | The store's record (§2.4, §7), `ArchiveStoreRecord`: its ID, its layout and other store fields, and the caller parameters it holds. The first call which finds none creates it with the store's own fields and `parameters`, the caller's opaque `key=value` parameters, and every later call answers that record, whatever its own `parameters`; a first call which loses the creation to another process's answers the winner's record, checked as a later call's. A record whose store fields differ from the store's own, compared verbatim as a map with the layout first, throws `ArchiveStoreMismatchException` (§4) naming the first field which differs, never a caller parameter, and the record's value of it and the store's. A `parameters` which is not a set of valid fields throws `ArgumentException`; a record which breaks its format, `ArchiveCorruptException`. The caller compares the answered record's parameters with its own, `CheckParametersMatch(parameters)`, which throws `ArchiveStoreMismatchException` naming the first caller parameter which differs. |
| `Kind` | The store's `ArchiveStoreKind`, which a composition records with its ID (§2.4). |

Its **pure members**, which do no I/O, are deterministic, and depend only on their arguments and the store fields of its
record (HI-I3); these and the reads are the only operations callers use:

| Member | Contract |
| --- | --- |
| `Plan(write)` | The key's token after `write`, from the token `write` starts from; an empty write's token unchanged. Total over tokens the provider minted; a token it could not have minted throws `ArchiveCorruptException`. Defined by an incremental fold, `Begin(token)` returning an `ArchivePlan`, `ArchivePlan.Add(command)` per command in order, and `ArchivePlan.End()` returning the token, whose state is O(1) in the write's length; `ArchivePlan.Supersede(rows)`, before the opener, takes the rows it supersedes. Admission and drains share it, so admission's planned token is the drain's. Every state of a key's chain has its own token: two writes from one token which fold the key's rows to different states, an opener alone and the opener with the rows after it among them, plan different tokens, so a reader whose cut lies inside a held write tells the log's write from its own. `ArchiveStorePlans.Plan(store, write)` replays the fold over a whole write, O(write). |
| `Admits(write, command)` | Whether `write` may grow by `command` within the provider's limits on what one write or one generation holds; defined by the fold as `ArchivePlan.Admits(command)`, which admission asks before each command, and replayed over a whole write by `ArchiveStorePlans.Admits`. |
| `Holds(prefix, write, i)` | Whether `prefix` holds the first `i` commands of `write`, applied or superseded: `true` for `i = 0`, and, for each `write`, monotone in `i` and in the prefix's coverage, which only grows. |
| `IsReplaceRequested(token)` | Whether the provider asks for a `Replace` of the key soon: `true` from every token at which a fold refuses an `Append` within `MaxAppendBytes` (HI-I5), and, in a provider which keeps headroom, from a threshold before it. |
| `MaxAppendBytes` | The largest `Append` body it accepts, or unbounded. |
| `AdmitsKey(key)` | Whether the provider can store `key` at all. |

**Split writes.** Admission judges an `Append` against the key's write as though every entry of the key in flight joins
one segment, while the batches which hold them may start further segments, so the key's writes across those segments
may hold more units than one unsplit write: each segment start among a key's in-flight entries adds at most one unit,
the earlier write's partly filled last one. Admission lets a write split so only below the provider's replace request:
an `Append` which arrives while its key has entries in flight and its admitted write has reached `IsReplaceRequested`
waits for them, and is then judged exactly ([journals §8.1](journals.md#81-admission)). A provider therefore keeps,
between the units at which `IsReplaceRequested` answers and those `Admits` allows, headroom for the segment starts
which can fall among one key's in-flight entries, a handful: the memory store counts appends per generation, which a
split leaves unchanged; the LSM has no limit; archive logs on Azure keep 9,900 blocks between their request at 40,000
and their refusal at 49,900, itself 100 below the append blob's 50,000; and the chunk layout keeps 16 chunks between
its request at 32 and its refusal at 48, one per segment start ([archive logs §7.4](archive-logs.md#74-chunk-layout)).

The parameters these depend on (`MaxAppendBytes`, a provider's block or chunk limits and thresholds) are the store's,
recorded in the store's record (§2.4). Each store states its encoding of tokens, coverage, and version bases, and its
pure members, in its own specification: the LSM's token is empty, `Plan` returns it, `Admits` and `AdmitsKey` admit, and
`Holds` holds the writes of the ledgers the prefix's map value no longer lists ([archive LSM
§2.8](archive-lsm.md#28-prefixes-and-tokens)); archive logs' tokens encode a log's position ([archive
logs](archive-logs.md#4-data-types)). `Plan` and `Admits` are defined by the fold, `ArchivePlan`, which each store
provides through `Begin(token)`; a key's content is at most `int.MaxValue` bytes, and a read whose content would pass it
throws `NotSupportedException` ([deferred](../deferred.md#archive-content-above-2-gib)).

Every answer reflects durable state ([conventions §13](../conventions.md#13-durable-answers)). A store holds only
decided commands. A drain writes each key's write of a closed segment once, in segment order, and checks, by Debug
assertion, that each unheld write's stored token equals `Plan` replayed from the key's previous write, the earliest
unheld write's anchored at the prefix's `Token` (HI-I4).

**Ledgers** ([ledgers](ledgers.md)): the process's one `LedgerReader`, which its writer registry shares, with
`ScanAsync`, which rewinds to a segment start and lends resolved rows with their versions and tokens, `ReadTailAsync`
and its `LedgerTail.ServedHere`, and its map view, `Map`; and LG-I1 to LG-I13.

**Callers.** A `Drop(S)` is submitted only with the store's evidence of coverage (§2.3). Journals stores, in each
defining row, the token `Plan` gives and the version the fold of the key's rows gives (JS-I10); proposes no empty
`Append`, no `Append` body above `MaxAppendBytes`, and no row of a key `AdmitsKey` refuses; writes a `Delete` body that
is empty or 8 bytes; and answers an `Append` `Admits` refuses `ReplaceRequired` without proposing it
([journals §8.1](journals.md#81-admission)).

## 4. Data types

| Type | Definition |
| --- | --- |
| `RowToken` | Ledgers' opaque value of at most 32 bytes ([ledgers §4](ledgers.md#4-data-types)), which this layer uses for tokens and version bases. The empty token is `Origin`, the token of a key with no history. Each provider's encodings are canonical, so byte equality is equality. |
| `ArchivePrefix` | `(Token, VersionBase, HasBase, Tombstone?, Content, coverage)`, a key's prefix in the store, which owns its content; its caller disposes it. `Token`: the token the key's next write starts from. `VersionBase`: a non-empty token, the base a rebase takes ([journals §8.3](journals.md#83-warm-state)), which names one state of the key and, among the prefixes answered with a map value which lists no ledger holding a row of the key, advances with every write the provider applies and never repeats while the provider holds the key (HI-I4). `HasBase`: whether a `Replace` opened the prefix's base, so the base exists even when its content is empty; Journals tells an emptied journal, which exists, from an expired one, which does not, by it. `Tombstone`: the tombstone of the `Delete` which opened the prefix's base, present only when the key is deleted now: no `Append` follows that `Delete`, so a prefix with a tombstone has no `HasBase` and no content, which its constructor checks with `ArgumentException`, as it does an empty `VersionBase` or tombstone. `Content`: an `ArchiveContent`. Coverage: the provider's private state which `Holds` reads. The prefix retains its own lease on the content it is given. |
| `ArchiveContent` | The key's content: the base's body, then each later held `Append`'s body, concatenated, as `ArcBuffer` pages the prefix owns; page boundaries are not command boundaries. |
| `ArchiveWrite` | A key's write for a segment (§2.1): its segment's identity `(ledger, start slot)`, its starting `Token`, its commands, an optional opener and then `Append`s, and `Superseded`, the rows its opener superseded, 0 without one. `ArchiveWriteBuilder` builds it from the key's rows. |
| `ArchiveCommand` | One command of a write: `(Kind, ContentLength, Tombstone?)`. `ContentLength` is the body's length for an `Append` or a `Replace` and 0 for every `Delete`; `Tombstone` is a `Delete`'s body, separate from its content. |
| `ArchivePlan` | The incremental fold of `Plan`, with `Add(command)`, `Admits(command)`, and `End()`, whose representation is the provider's, within `ArchivePlan.MaxStateBytes` (128): `Begin` allocates at most that, and no `Append` grows it, which every provider's contract tests check, so admission counts a warm key's folds by it; `ArchivePlan.Empty()` is the fold of a store whose only token is empty. It defines `Plan` and `Admits`, which `ArchiveStorePlans` replays over a whole write. |
| `ArchiveMergeOutcome` | A union: the `ArchiveRead`; `PastEnd`, the prefix holds a write past the last one the scans met; or the `ArchiveCorruptException` naming a break of the key's chain (§8.1 step 4). |
| `LedgerMapCoverage` | The members of a store whose coverage is the read's map value and whose only token is empty, the LSM: `Holds` and the prefix's rows by one predicate, `LedgerMapValue.NoLongerLists`; the empty fold; and the 20-byte version base of a tag. |
| `ArchiveRead` | `(Prefix?, Rows: ArchiveRow[], Through: LedgerId → Slot, OpenLedger: LedgerId?, Linearizable, NextToken)`, which owns its prefix and its rows' bodies; its caller disposes it. |
| `ArchiveRow` | A scanned live row of one key: its tag, kind, `Version`, Ledgers' `RowVersion` `(base, count)` ([ledgers §4](ledgers.md#4-data-types)), segment identity, its defining row's token, its body's length, `IsHeld`, and, when not held, its body. `ArchiveRow.Of` makes one of a row a segment view lends, copying its body into a buffer the read owns, so a read holds its key's bytes and no batch's pages; `ToCommand()` gives its command. |
| `TaggedRow` | An owned `Row` with its tag: how the LSM's sections hold a key's rows, and how a fold takes them. |
| `ArchiveStoreAnswer` | A sealed class: `Map: LedgerMapValue`, `Prefix: ArchivePrefix?`, null for `Absent`, `AbsentBounds: ImmutableArray<LedgerAbsentBound>` (the per-ledger scan starts an `Absent` answer carried, empty otherwise), and `Merge(live, through, openLedger, linearizable)`, which answers an `ArchiveMergeOutcome`; a first touch's answer, which its caller disposes. |
| `ArchiveStoreRead` | `Absent` (with optional per-ledger `Bounds: ImmutableArray<LedgerAbsentBound>`, constructed through `Absent.WithBounds`); `Read(ArchivePrefix)`, which owns its prefix; or `Stale`. |
| `ArchiveKeyIndex` | Optional per-node key index seam supplied to `ArchiveReader`: `ReadAsync(ledger, below, key)` waits until the ledger's index has caught up through `below` (JS-I9) and returns a `KeyIndexRead` union: `Indexed(ImmutableArray<KeyIndexEntry>)` (each `(Tag, DefiningSlot, Ordinal)`), `Unindexed` (fall back to a scan), or `Dropped` (ledger revoked; restart the history read). |
| `ArchiveStoreId` | A store's random non-zero 128-bit ID, stored as 32 lowercase hexadecimal digits (§2.4), which `TryParse` reads back, refusing any other form. |
| `ArchiveStoreKind` | `Lsm` (1), `Logs` (2), or `Memory` (3), the memory store of tests and simulations; the values are the Archive's own, pinned and persisted, and a composition which records a kind records them, as the Orleans node registry does ([orleans §7.3](../compositions/orleans.md#73-node-registry-clnr-v1)). |
| `ArchiveStoreIdentity` | A readonly record struct `(Kind: ArchiveStoreKind, Prefix: ArchiveStorePrefix, Id: ArchiveStoreId)`, a store's identity, which a composition records for its cluster's life (§2.4); its constructor refuses an undefined kind, the default prefix, and the default ID, and the default identity names no store. `CheckKind(kind)` refuses a store of another kind and `CheckPrefix(prefix)` a store under another prefix, both needing no request, and `Check(kind, recordId)` a store of another kind or whose record is absent or holds another ID, each mismatch naming `ArchiveStoreRecord.KindField`, `PrefixField` (`prefix`), or `IdField`, so every store's check of a recorded identity is one rule. |
| `ArchiveStorePrefix` | A readonly record struct over the short prefix a cluster's store lies under in a bucket or container shared by clusters: 1 to 16 characters of `[a-z0-9_-]`, one object-name component; `Of(seed)` derives 10 lowercase base32 characters, 50 bits, of the SHA-256 of a seed's UTF-8, which the Orleans composition takes from its service ID ([orleans §5.13](../compositions/orleans.md#513-archive-log-store)). |
| `ArchiveStoreRecord` | The store's record (§2.4, §7): its `ID`, its `Layout`, its `StoreFields`, and the caller's `Parameters`, each group a map; `Decode` answers it or the `InvalidDataException` naming the first rule broken (`bad-utf8`, `bad-line`, `duplicate-field`, `bad-id`, `bad-layout`), and `FirstDifference` names the first field two records differ in, an `ArchiveStoreRecordField(Name, IsParameter)`. Each half has its check, which names its first field which differs with `ArchiveStoreMismatchException`: `CheckStoreFieldsMatch(layout, storeFields)`, the store's, and `CheckParametersMatch(parameters)`, the caller's. |
| `IArchiveStore` | The archive store contract (§3). |
| `ILedgerLocalDrainBarrier` | Control plane (`Orleans.Durable.Storage.Engine.Archive.Control`). Named barrier interface invoked by archive drain workers before completing or dropping a closed ledger ([LG-I16](ledgers.md#5-invariants)) and during segment scans to validate skipped `RowKind.LocalAppend` keys fail-closed: `ValidateLocalAppendKey(LedgerMetadata, ReadOnlySpan<byte>)` (throws `ArchiveCorruptException` if invalid or unregistered) and `EnsureLocalStreamsDrainedAsync(LedgerMetadata, Slot, CancellationToken)` returning `ValueTask<LocalDrainBarrierOutcome>` (idempotent across retries and racing drainers). Implemented by `LocalStreamDrainCoordinator` (`Orleans.Durable.Storage.Engine.Cluster.Control`). *Not yet in code* (J2). |
| `LocalDrainBarrierOutcome` | Control plane union (`Orleans.Durable.Storage.Engine.Archive.Control`): `Drained` (every registered local stream of closed ledger `L` through `T` is durably drained), `Dropped` (`L` left the ledger map), or `Backpressure` (replica backpressure prevented completion; asserts nothing). *Not yet in code* (J2). |
| `ArchiveCorruptException` | The history contradicts itself: a CRC-32C mismatch, an object or command the store names and does not hold, a store's record which breaks its format (the LSM's manifest or a section, an archive log), a token no provider could have minted, a stored token or version which breaks the chain (HI-I4), or a store which contradicts the ledger map. Both stores raise it. |
| `ArchiveStoreMismatchException` | The store's record names other store fields or caller parameters than the process's, naming the first which differs, `Field`, whether it is a caller parameter, `IsParameter`, and the record's value of it and the process's, `StoredValue` and `CallerValue`, each null when absent (§2.4); `StoreRecordAsync` throws it on every store for a store field, and `CheckParametersMatch` for a caller parameter. |

## 5. Invariants

| ID | Invariant |
| --- | --- |
| HI-I1 | A history read takes a ledger map value, then reads the store after it, and includes every row of a ledger closed before it began and, in the process which serves the writer of the key's open ledger, every row answered `Durable` before it began: the store's prefix, and, for each live ledger of that map value containing the key, its decided rows which the prefix does not hold, found by a scan from slot 0, a closed ledger's below the map's `T`, an open ledger's below the tail a seal check begun after the store answered finds, which there covers every proposal the writer answered `Durable` ([LG-I8](ledgers.md#5-invariants)); it counts each command once, held or live, and reports whether it was served there (`Linearizable`). A linearizable read answers exactly its cut; a read which is not may answer a longer real prefix of the key's history, when the store holds a drain from beyond its tail. It restarts when the store answers `Stale`, when a slot or ledger it needs is dropped, or when an open ledger it read is sealed. An absence answer pairs a map value with a store answer read after it, from state which covers every ledger that value no longer lists. |
| HI-I2 | A ledger leaves the ledger map only once the archive store holds every row it decided below its `T`; the store holds only decided rows. |
| HI-I3 | A provider's `Plan`, `Admits`, `Holds`, `IsReplaceRequested`, `MaxAppendBytes`, and `AdmitsKey` are pure and deterministic, and the store fields they depend on are recorded in the store's record (§2.4) for the cluster's life. Every token and version base a provider mints encodes canonically. `Plan` of an empty write returns its token. Where a provider's `Holds` reads tokens, as archive logs' does, tokens strictly order each key's chain: `Plan(w)` lies above `w`'s token in the provider's order for every non-empty write `w` which `Admits` grew. |
| HI-I4 | Among the unheld rows a drain or a load scans, every stored token equals the replay of `Plan` from the key's previous write, the earliest unheld write's anchored at the prefix's `Token`, a write the prefix holds in part lying at `Plan` of its held part and no write held after one not held whole, and every stored version continues the fold of the key's live rows before it (the check of [JS-I10](journals.md#5-invariants)); a break is corruption once a map read after it still lists every ledger the read scanned. On a store whose only token is empty, the LSM, the token clause holds trivially and the versions are the cross-check. A provider's `VersionBase` names one state of the key and, among the prefixes answered with a map value which lists no ledger holding a row of the key, advances with every write it applies and never repeats while the provider holds the key. |
| HI-I5 | A provider asks for a `Replace` from every token at which a fold refuses an `Append` within `MaxAppendBytes`: for every plan `p` begun from any token and grown by commands it admitted, if `p.Admits(Append)` is `false`, `IsReplaceRequested(p.End())` is `true`, a fresh write's plan, from `Begin(t)`, and a joined write's, as admission's candidates, among them. A writer refused learns of the request from its next read, which carries `IsReplaceRequested` of the key's token, so a writer which recovers from `ReplaceRequired` through a new instance writes a `Replace` next. |

HI-I1 makes reads in the primary's process linearizable: the map is read before the store, a ledger leaves the map only
once the store covers it (HI-I2), and each store's coverage only grows, so a store read after the map covers every
ledger the map no longer lists. Each store establishes HI-I2 with its own invariants: the LSM with LSM-I1 to LSM-I3,
archive logs with LOG-I1 to LOG-I3. HI-I3 makes admission's planned tokens, the drains' replays, and every process's
judgment of what a prefix holds agree. HI-I4 checks the chain JS-I10 produces, and its second sentence is what makes a
rebase fresh (§8.3).

## 6. Faults, concurrency, and cancellation

- **Stale caches.** A read whose map names a dropped ledger, or whose store answers `Stale`, reads both again (HI-I1).
- **Store unavailable.** A read whose store read fails propagates the store's exception to its caller, which retries it
  or waits; the live ledgers keep accepting writes. The reader retries only what asserts nothing: backpressure and a
  stale cache.
- **Corruption.** `ArchiveCorruptException`, raised wherever the contradiction is found: a store's own record, a
  malformed token, a broken chain (HI-I4) while the map still lists every ledger the read scanned, or a prefix which
  contradicts the map.
- **Cancellation.** Cancelling a read stops it and releases what it collected.

## 7. Formats

Each store owns its other formats and its token encoding. This layer owns one, the **store's record** (§2.4), which
every store keeps in one object (`durable-storage/store` on archive logs, the root object `store` on the LSM) and one
codec, `ArchiveStoreRecord`, reads and writes: strict UTF-8 lines separated by a line feed, with no terminator, each
`{field}={value}`.

| Line | Field | Value |
| --- | --- | --- |
| First | `id` | The store ID, 32 lowercase hexadecimal digits, not all zero |
| Second | `layout` | The store's layout, non-empty: the LSM's `lsm`, archive logs' own ([archive logs §7.5](archive-logs.md#75-clogs-objects)), or the memory store's `memory-store` |
| Then | `{key}` | Each of the store's other fields, its `Plan` parameters, a key of `[a-z0-9-]` other than `id` and `layout`, in ordinal order of their keys |
| Then | `caller.{key}` | Each of the caller's parameters, a key of `[a-z0-9-]` after the prefix `caller.`, in ordinal order of their keys |

A value holds no line feed. A reader checks, in order, that the bytes are strict UTF-8 (`bad-utf8`); that each line,
in order, is a key of `[a-z0-9-]`, or `caller.` and one, an equals sign, and a value (`bad-line`), and repeats no
earlier line's key (`duplicate-field`); that the first line is `id` with a store ID (`bad-id`); and that the second is
`layout` with a non-empty value (`bad-layout`); a rejection carries its reason. It reads the store fields and the
caller's parameters as two maps, in any order, and a writer writes the order above. Two records match when their
layouts, their store fields, and their caller parameters are equal, each group compared as a map; the first difference
is named by the layout, then the store fields, then the caller parameters, each in ordinal order of its keys. Golden
vectors: `test/Durable.Storage/shared-vectors/archive/store-records.json` and `store-record-rejections.json`; the
`archive-store-record` fuzz target.

## 8. Mechanism and policy

### 8.1 History reads

1. Take a ledger map value: the reader's cached `LedgerMapView.Current`. When it lists no ledger containing `k`, as
   before the bootstrap, read the map and start again if it advanced: no ledger is answered only from a value read
   after the call.
2. Read the store with that value (`IArchiveStore.ReadAsync`), never with a value read after the store, and at once
   start scanning the decided rows of `k` of each live ledger of the map value containing `k`, the closed ones in ID
   order and then the open one, whose ID is the greatest, from slot 0 on every store (`LedgerReader.ScanAsync`), copying
   each row's body into a buffer of its scan's own: together, with the open ledger's `K` reads in flight in all, each
   scan taking a place in a window of `K` places they share for each read it issues ([ledgers
   §2.5](ledgers.md#25-reads-ledgerreader)), joined in ledger and slot order. The open ledger's scan reads at once to its
   known contiguous tail in this process, which needs no seal check (LP-I11), so a read waits for the slowest of its
   scans and the store rather than for their sum. On `Stale`, read the map again and start again; `Absent` counts as an
   empty prefix which holds no row.
3. Find each live ledger's decided end as LG-I8 requires: the map's `T` for a closed ledger, and, for the open one, the
   tail of a seal check (`LedgerReader.ReadTailAsync`) which begins once the store has answered, and which in the
   process serving its writer covers every proposal the writer answered `Durable`; the open ledger's scan continues to
   that tail from the slot it reached. Keep each ledger's rows below its end. A sealed or dropped answer, which made the
   reader read the map again, starts the read again; backpressure on a read or the seal check delays that read with
   backoff, and the scan resumes from its slot with the rows it collected.
4. For each segment the scan reached, compute `k`'s write from its rows there and mark each row held or not by `Holds`
   (§2.1), releasing a held row's body. Check the chain (HI-I4), each write held in none, part, or all of its
   commands: the earliest write with none held starts from the prefix's `Token`, and each later write's stored token
   equals `Plan` of the write before it; a write held in part lies at `Plan` of its held part, the prefix's `Token`, and
   follows only writes held whole, as does a write held whole; each defining row of a write held in none stores the
   version after the key's previous scanned row's, and the earliest one, with no scanned predecessor, starts the check.
   When the last write the scans met is held whole and `Plan` of it is not the prefix's `Token`, the prefix reaches past
   the read's end: start again. A mismatch makes the reader read the map again: when a ledger it scanned is no longer
   listed, a rebase after that ledger's drop may explain it, and the read starts again; otherwise it is
   `ArchiveCorruptException`, which Journals maps to its own outcome ([journals §8.3](journals.md#83-warm-state)).
5. Answer the prefix and the scanned rows in tag order, with each ledger's end and whether the seal check was served
   here (`Linearizable`).

A restart begins at once when the map advanced past the value the read took, reading it once more when the reader's
read did not, and does not count toward the backoff; otherwise it waits with bounded, jittered, exponential backoff,
with unbounded attempts ([conventions §6](../conventions.md#6-retries-and-idempotency)): each wait lies between its
step, 20 ms doubling to at most 1 s, and twice the step, and never exceeds 1 s, so no restart begins without waiting
at least 20 ms. Backpressure, or a sealed ledger the map does not yet list closed, delays a read and never decides it.
Every other exception, corruption or an unavailable store or replica quorum, propagates to the caller.

The map is read before the store: a ledger leaves the map only once the store holds every row it decided (HI-I2), and
coverage only grows, so a store read after the map holds every row of every ledger the map no longer lists. A drain
copies a segment only once it is decided, closed, and below a contiguous tail, and the open ledger's seal check begins
after the store answered, so the prefix holds no command beyond the end the read reached in any live ledger, and the
prefix with the rows it does not hold is a prefix of the key's history.

**Key-indexed live reads and `Absent` bounds.** When `ArchiveReader` holds an `ArchiveKeyIndex` (provided by
`JournalStateDatabase`, [journals §8.3](journals.md#83-warm-state)), it reads each live ledger through the index rather
than scanning the whole ledger: after the ledger's end is known (`T` for a closed ledger, the seal check's tail for the
open one), `ArchiveKeyIndex.ReadAsync(ledger, below, key)` waits for the index's catch-up through `below` (JS-I9) and
returns the key's `KeyIndexEntry(Tag, DefiningSlot, Ordinal)` records in tag order. The reader streams point reads of
the distinct defining and row slots in ascending slot order through `LedgerReader.ReadAsync` (bounded to at most `K` in
flight and sharing the read's `K`-place window, releasing the window across backpressure backoff), looks up each
defining row by ordinal through `BatchView.FindDefining` (rejecting an out-of-range ordinal or mismatched row with
`InvalidDataException` carrying reason `unknown-ordinal`), verifies the defining row's full key bytes against `key` so a
hash collision is skipped silently, materializes each matching `ArchiveRow`, and releases each batch's buffer at once. A
`KeyIndexRead.Dropped` answer cancels sibling reads and restarts the history read with a fresh map; a
`KeyIndexRead.Unindexed` answer waits for the in-flight store read (restarting if cancelled by a sibling drop or a `Stale`
answer) and falls back to `LedgerReader.ScanAsync`, starting at the store's `Absent` bound for that ledger (or slot 0
when none was given). Without an `ArchiveKeyIndex` (the reference implementation and test oracle), `ReadAsync` runs
live scans in parallel with the store read, applying any `Absent` bound when the store read has already completed before
a scan starts and on the open ledger's post-answer continuation (step 3); otherwise it starts from slot 0. `JournalStore`
uses `ArchiveReader.ReadStoreAsync` and `JournalStateDatabase` for first-touch loads, owner reads, and absence answers
([Journals §8.2, §8.3](journals.md#82-reads-on-the-owner)).

### 8.2 First touch and absence

`ReadStoreAsync` takes `LedgerMapView.Current` and then reads the store with it (`IArchiveStore.ReadAsync`), which
answers from state which covers every ledger that map value no longer lists: the LSM from its view, a manifest value
which shows complete (`IsComplete`) every such ledger, skipping the sections whose Bloom filters exclude `k` ([archive
LSM §8.3](archive-lsm.md#83-history-reads)), and archive logs from one read of the key's log
([archive logs](archive-logs.md#8-mechanism-and-policy)). On `Stale` it takes the map again and reads again. Its caller
finds the rest of `k`'s history in the live ledgers of that map value, scanning each from slot 0, and merges them with
`ArchiveStoreAnswer.Merge`, which marks held rows and checks the chain as §8.1 does. The answer holds while that value's
open ledger for `k` is unsealed, which the caller confirms with the seal check in its process.

### 8.3 Version bases and rebases

Journals owns the rebase ([journals §8.3](journals.md#83-warm-state)): a primary which loads a key whose map value, read
before the store, lists no ledger holding a row of it, held or not, sets the key's version to `(prefix.VersionBase, 0)`,
`(Origin, 0)` on `Absent`, and the token of its next write to `prefix.Token`, `Origin` on `Absent`. This layer supplies
the property that makes the rebased version fresh, HI-I4's second sentence:

- Every ledger which held the key's rows dropped before the map read, and a drop requires the store to hold every row
  (HI-I2), so the prefix reflects every command of the key ever decided. The freshness the rebase relies on is that of
  such reads; a version base answered with another map value names a state of the key too, but may be older.
- `VersionBase` advances with every decided command the provider applies. Journals refuses an empty `Append`; on
  archive logs every write moves the log's position and every `Delete`, an empty one included, opens a generation; and
  every row the LSM stores has a tag above every earlier row of the key ([LG-I6](ledgers.md#5-invariants)).
- Version bases come only from rebases and `Origin`, and `Origin` only on `Absent`. An earlier version `(b, n)` equals
  the rebased `(prefix.VersionBase, 0)` only if `b` is the same version base and `n = 0`, so no command of the key was
  decided since that earlier rebase: the state is the same, and the earlier version is correct for it.

**The ABA caveat.** Archive logs never reuse a version base: logs are never deleted, and a `Delete` opens a new
generation. The LSM's version base is the tag of its last stored row, so once compaction drops a key entirely, an empty
`Delete` with everything older ([LSM-I5](archive-lsm.md#5-invariants)), the key reads `Absent` and rebases to
`(Origin, 0)`, which an earlier version may equal. That caveat is limited to keys the LSM has fully forgotten.

## 9. Providers and implementations

`ArchiveReader` has one implementation, over `IArchiveStore`. The stores: the LSM's `LsmArchiveStore`
([archive LSM §2.5](archive-lsm.md#25-archive-store)) and archive logs' `LogArchiveStore`
([archive logs](archive-logs.md#2-contract-upward)).

Tests and simulation use `InMemoryArchiveStore` (`Orleans.Durable.Storage.Engine.Archive.Memory`), a third provider with
its own token encoding: a version byte (1), then the key's generation and the appends that generation holds, as varints
in their shortest form, with `Origin`, generation 0 with no appends, the empty token, which no other encoding names.
`Plan` opens the next generation for an opener, holding the appends after it, and adds an append run's appends to the
generation, generation 0 for a key's first append runs; positions order each key's chain by generation, then appends,
and a token at the last generation or append count, which no chain reaches, is corruption once planned past. Its
coverage is the key's token, and `Holds(prefix, write, i)` holds the first `i` commands when the prefix's position is at
or past the position after them in the key's chain. Its `VersionBase` is its token, and its `HasBase` whether a
`Replace` opened the key's current generation. Its parameters, an optional `MaxAppendBytes`, a limit on the appends an
append run may bring one generation to, and the threshold at which `IsReplaceRequested` answers, strictly below the
limit so a writer asked has headroom before `Admits` refuses, let tests exercise `ReplaceRequired` at unit limits; an
opener's appends land in its base and always admit, and it admits every key. Its record (§7) holds its ID, the layout
`memory-store`, distinct from archive logs' `memory`, and its parameters (`max-append-bytes`, `max-appends`,
`replace-request-appends`), with the caller's. `Drain(key, rows)` takes one key's rows of a closed segment as owned
`ArchiveRow`s a scan resolved, as a drain would, in segment order per ledger and in lineage order; it computes the key's
write, checks its token against the key's chain (HI-I4) and its commands against the store's limits, refuses a write it
holds in part, and applies the write unless it holds it whole. A read answers the key's prefix or `Absent`, and never
`Stale`. Its IDs come from a source its constructor takes.

A key's first append runs stay in generation 0, from the empty token, where archive logs' first append run opens
generation 1 ([archive logs §4](archive-logs.md#4-data-types)). The memory store is the reference provider of the
contract, not a model of archive logs' layouts: its chain needs only to be strictly ordered and canonical (HI-I3),
which generation 0 is, and archive logs' generation skip is their layout's, which their own token tests and
conformance cover. Aligning them was rejected: it would give the memory store a rule which only one other layout needs.

The
simulation kit's `SimulatedArchiveStore` wraps it with latency, random `Stale` answers, a seeded ID source, and a hook
after a read's answer.

## 10. Verification

- **Oracles.** Each is written once and reused over every store ([verification §3](../verification.md#3-oracles)):
  - *The history fold.* The fold of a key's decided rows (versions, tokens, and content) is compared with each prefix
    followed by the live rows not held, over the memory store, the LSM, and every archive-log store, in deterministic
    simulation with drains, drops, compactions where the store has them, and stale caches
    ([verification §3.3](../verification.md#33-the-history-fold)). `ArchiveOracle`
    (`test/Durable.Storage/shared/Archive`) is the one reading of §2.1 every test shares.
  - *The chain checker* (HI-I4, JS-I10), `TokenChainChecker` in `test/Durable.Storage/shared/Archive`. A global checker
    after every simulation step replays `Plan` per key across segments and ledgers, through splits and merges, and
    checks every stored token, anchored as HI-I4 states, a write held in part at `Plan` of its held part and none held
    after one not held whole; `VersionChecker`, beside it, checks every stored version against the fold, a count reset
    among them, that a key's versions never repeat but in the LSM's caveat (§8.3), and that every version base a read
    answers names one state of its key; Debug assertions pair it where admission writes a defining row and where drains
    and loads read one. Mutants which store a token from the wrong parent, plan a token from a fenced write, reset a
    version, or skip the reader's check fail.
  - *Laws of the pure members* (CsCheck, over every provider): a write is not held before it lands and is held after;
    with a map value generated per read, one still listing a suffix of the landed ledgers or older than the store,
    `Holds` holds exactly the writes of the ledgers it no longer lists, or every landed write where tokens order the
    chain, and the prefix followed by the writes not held folds to the key's history (the two-way property); `Holds` is
    monotone in `i` and in coverage; `Plan` stays within the provider's limits for every write `Admits` grew, over
    generated limits on the memory store; `Plan(w)` lies strictly above `w`'s token in the provider's order for every
    non-empty write `Admits` grew, and `Plan` of an empty write returns its token (HI-I3); distinct states of a key's
    chain, over generated openers and appends, plan distinct tokens; the incremental fold equals the batch `Plan`; every
    token and version base re-encodes to its own bytes, the 20-byte tag version base pinned by
    `test/Durable.Storage/shared-vectors/archive/version-bases.json`; a token no provider minted throws.
- **History reads.** The reader is checked against a reference history in `ArchiveSimulationTests`, over the memory
  store, on three seeds (HI-I1), and over the LSM in `ArchiveDrainSimulationTests`, both with the chain and version
  checkers after every step; `ArchiveReaderContract` reads over the LSM across a dropped
  ledger, a drained listed one, and the open one, and across a drop its map predates: each read's prefix and live rows
  not held fold to exactly the reference's content over its `Through`, or, for archive logs' reads which are not
  linearizable, over a longer prefix (`ArchiveLogDrainSimulationTests`), which holds no row proposed after the read,
  every row of a ledger closed before it, and, when the read reports itself linearizable, every row answered `Durable`
  before it; each first touch composes with the reference rows of its map value's ledgers into the reference's history
  below the value's next ID. The schedules have two processes serving primaries, transitions which move them, a takeover
  during a read, drains of whole segments, drops whose stale maps readers meet, `Stale` answers, and replica
  backpressure, and the corpus shows a restart of each cause, a resumption, rows the store holds in a listed ledger, and
  a fenced proposal, whose row no chain joins; the rows the store and the live ledgers both hold arise by construction,
  once every fault is off: a drain which writes every row of a closed ledger but reports the bound zero, and a read
  whose map still lists the ledger.
- **Merge.** A CsCheck property over random segments of several keys, some rows in one slot, with writes the store holds
  or not, run over the memory store, the LSM, and archive logs on the memory store (`ArchiveMergeProperty`), checks that
  the merge marks held exactly the rows `Holds` covers, that the fold of the prefix and the rows not held equals the
  fold of the key's decided rows, each command once, and that the chain check passes; mutants which keep a held row,
  drop a row not held, or judge a row before its write's opener by its own position fail with the fold's comparison. A
  test double, `PrefixCutStore`, holds part of a write: the merge accepts a prefix at `Plan` of the held part and
  refuses a write held after one not held whole or a prefix off that point, which the mutants
  `HoldsAfterAPartlyHeldWrite` and `SkipsThePartlyHeldAnchor` accept; each break of the chain, a token, an anchor, a
  version's count or base, a count reset, two tokens in a segment, or a write held after one not held, is corruption,
  and a prefix past the scans' last write is `PastEnd`. A read's `NextToken` is `Plan` of the last write it met, also
  when the prefix holds that write in part and its held opener is a tombstoned `Delete`, and the prefix's token when it
  holds it whole or no row is live.
- **Read order.** A drain, a drop, and a refresh of the reader's view after the store answers make the reader meet the
  drop and read again, finding every row; a mutant which takes the map after the store misses them, in `ArchiveTests`
  and on every seed of the simulation (HI-I1). The Quint models' `manifest-before-map` mutant covers the LSM.
- **Restarts.** Tests show a restart on each cause, the resumption after backpressure without one, the backoff on a
  fake clock and its absence when the map advanced, a store failure which propagates, cancellation of a read which
  meets endless `Stale` answers, the release of rows collected before a restart, and a read before the bootstrap. A
  drain which lands between the store read and the open ledger's seal check leaves the read consistent, its rows
  reaching every command its prefix holds; a mutant which begins the seal check beside the store read returns content
  newer than its rows. On the LSM, a key whose rows the LSM holds in a listed ledger reads them back not held, with
  their bodies, and the rows of a ledger the map dropped held in the prefix. A reader on a map from before a ledger's
  drop, which meets a version from a legal rebase after it, starts again and answers the history, and a mutant which
  throws at once fails; a prefix past the read's end starts the read again. A read holds one page for sixteen bytes
  of its key in sixteen batches once the live ledgers release them, which a reader retaining its batches' pages fails.
- **Scans together.** `ArchiveTests.TheLiveLedgersScansRunTogetherAndTheOpenOneReadsPastItsKnownTailOnlyOnceTheStoreAnswered`
  holds the store read and makes the closed ledger's reads wait for the open ledger's first, so the read completes
  only when the scans run at once, and shows the open ledger read below its known contiguous tail before the store
  answered and past it only afterwards; `TheLiveLedgersScansKeepTheOpenLedgersKReadsInFlightInAll` holds every replica
  read and finds exactly `K` in flight at the peak across a closed and an open ledger; and
  `LedgerReaderTests.AScanWhichEndsWithReadsInFlightFreesTheirPlacesInItsWindow` shows a scan which backpressure ends,
  or its caller abandons, frees every place it took. Mutants which run the scans one after another, give each its own
  `K`, start the open ledger's scan only after the seal check, leave it at its known tail, or keep a consumed or drained
  read's place each fail one of them.
- **Key-indexed reads and `Absent` bounds.** `ArchiveKeyIndexTests` verifies that `ArchiveReader` with an
  `ArchiveKeyIndex` matches the unindexed slow-path `ArchiveReader` row-for-row (including a CsCheck property over
  randomized multi-row batches, multi-slot segments, openers, and multi-ledger histories) while point-reading only the
  key's defining and row batches, rejects a stale entry whose ordinal is out of range with `InvalidDataException`
  (`unknown-ordinal`), skips hash collisions (both defining and referencing rows) by checking full key bytes, restarts
  cleanly when `KeyIndexRead.Dropped` cancels an in-flight async store read, and starts unindexed scans at
  `ArchiveStoreRead.Absent.Bounds` rather than slot 0. `LsmAbsentBoundsTests` verifies `LsmArchiveStore` emits
  watermarks only for incomplete listed ledgers when the store holds no rows of the key (including closed incomplete
  ledgers and excluding keys present in a partly drained listed ledger) and catches `BoundCompleteLedgerMutant`
  end-to-end with `ArchiveReader` over compacted/expired rows.
- **Rebase freshness.** A property over random histories of one key with drops and reloads, over the memory store and
  each store in simulation: every `VersionBase` a read answers differs from every earlier one while the store holds the
  key, so rebased versions never repeat, except, on the LSM, after compaction drops the key entirely, the recorded
  caveat (§8.3).
- **Contract.** One `IArchiveStore` conformance suite (`ArchiveStoreContract`, `test/Durable.Storage/shared/Archive`)
  runs over every implementation: the memory store, the LSM, and every archive-log store. It checks `Absent` only when
  the key's prefix would hold no command, also when another key's commands are held; a prefix's content as the fold of
  the held commands, across openers, append runs, a tombstoned `Delete`, an empty `Delete`, and an `Append` after a
  `Delete`; `HasBase` true exactly when a `Replace` opened the base, an empty one included; `Tombstone` present only
  while the key is deleted; `Holds` of every write a drain applied; a read whose map value is older than the store,
  holding no write of a ledger past its `NextLedgerId`; a straggler drain which changes nothing, through the LSM's
  committer; `VersionBase` advancing with every write; the pure members' laws on the provider's parameters; a fold's
  state within `ArchivePlan.MaxStateBytes`, measured at `Begin` and at an `Append`; a store of empty tokens admitting
  every key and command; a stable, non-zero store ID which reads back; a store's record created once, holding the store
  fields the spec gives each store, and answered whole to every later call and to another instance over the same
  objects, whatever its caller parameters; a first call which loses the record's creation to a winner with other caller
  parameters answered the winner's stored bytes, which its caller's check refuses, refused by the store
  when the winner's store fields differ too, which a store field names, a create which landed but answered that the
  record exists answered its own record, and a record which exists but reads absent corrupt; a refused call leaving the
  stored record's bytes as they were; a record whose store
  fields differ in any one, its layout, a field
  added, changed, or missing, refused by `StoreRecordAsync` itself, naming the field and both values, whatever the
  caller's parameters; a record whose caller parameters differ answered, and refused by the caller's
  `CheckParametersMatch`, naming the parameter and both values; every single-field difference
  refused by exactly its own half's check (`ArchiveStoreRecordTests`), with a mutant ignoring one field failing; a
  caller parameter named as a store field kept apart; the record's golden vectors and its fuzz target; and each kind's
  pinned value, which the Orleans node registry's vectors pin on the wire ([orleans
  §7.3](../compositions/orleans.md#73-node-registry-clnr-v1)).
