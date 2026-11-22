# Identity and addressing

Every identity and address type in Clog, with its domain, ordering, equality, encodings, and owning component,
and the explicit mappings between the address spaces of the layers. §13 summarizes them in one table.

## 1. Cluster

A cluster is a non-empty `Guid` chosen by the deployment and configured equally in every process (in the Orleans
composition, `DurableStorageOptions.Cluster`). A host serves one cluster. Each node root and MetaStore namespace records
the cluster identity and refuses another, so clusters sharing a disk or a MetaStore backend never affect each other, and
ledger IDs, unique within a cluster (§3), never collide. A cluster's archive store lies under a store prefix of its own
in its bucket or container, which the Orleans composition derives from its service ID or takes from its configuration
and records in its node registry ([orleans §5.13](compositions/orleans.md#513-archive-log-store)); that each cluster's
prefix is unique within its bucket is the operator's precondition, which nothing checks ([archive LSM
§9](components/archive-lsm.md#9-providers-and-implementations), [archive logs
§9](components/archive-logs.md#9-providers-and-implementations)): two clusters under one prefix share its objects.

| Storage root | Records the cluster | Refuses another |
| --- | --- | --- |
| Node root (LedgerStore) | The cluster claim `{root}/cluster`, created by the root's first opener and never overwritten ([LS-I12](components/ledgerstore.md#5-invariants)) | Opening fails with `LedgerStoreClusterMismatchException` |
| MetaStore namespace | Every register key begins `{cluster:N}/` (§10, [MS-I4](components/metastore.md#5-invariants)) | A process reads and writes only its own cluster's registers |

| Surface | Encoding |
| --- | --- |
| Text: MetaStore keys, node roots' cluster claims, diagnostics | 32 lowercase hexadecimal digits (`Guid` format `N`), written `{cluster:N}` |
| Binary encodings | 16 bytes in RFC 4122 (big-endian) order, so their hexadecimal form equals `{cluster:N}` |
| Configuration (input) | Any GUID string the .NET parser accepts; renderings use `{cluster:N}` |

Equality is by value. A cluster has no order.

## 2. Node and silo

| Identity | Definition | Encoding | Owner |
| --- | --- | --- | --- |
| Node identity (`NodeId`) | A random, non-empty UUID created with a LedgerStore and fixed for its lifetime; stored in its node file `wal/node`, beside the WAL. A wiped or replaced disk starts a new node ([LS-I6](components/ledgerstore.md#5-invariants)). | `{node:N}` in the node file; 16 bytes big-endian in `CLNR` and in the ledger map's replica lists and primary claims; a `Guid` in Orleans grain keys and replica calls; `{node:N}` as text | [LedgerStore](components/ledgerstore.md#2-contract-upward) |
| Silo | One Orleans process incarnation of a node: `SiloAddress`, an IP endpoint with a positive `int32` generation which increases on every restart | `CLNR` `silo_endpoint` (`{ip}:{port}`) and `generation` | [Orleans](compositions/orleans.md) |

The ledger map names each ledger's replicas and primary by node identity, never by endpoint or handle, and a
replica is addressed by `(NodeId, LedgerId)` ([Ledger replicas
§2.2](components/ledger-replicas.md#22-iledgerreplicatransport)). The node registry maps a node to its current silo;
every replica call names the node it expects, and a silo hosting another node refuses it
([OR-I14](compositions/orleans.md#51-invariants)).

## 3. Ledger identity

A ledger is identified within its cluster by its `LedgerId`, a non-zero `ulong`.

- **Assignment.** The ledger map's write which installs a ledger assigns its ID from `NextLedgerId` and
  advances it; 0 is never assigned. IDs are consecutive in installation order and never reused within a cluster
  ([LG-I3](components/ledgers.md#5-invariants)).
- **Creation.** A ledger's replicas come into being on their nodes with the first write, recovery write, or seal
  which reaches each, unless the node dropped the ledger ([LR-I7](components/ledger-replicas.md#5-invariants)). Only a
  sender whose map lists the ledger with the node sends one ([LG-I7](components/ledgers.md#5-invariants)), and only
  the map write installing the ledger lists it, so every replica's ID is already assigned
  ([ledgers §8.1](components/ledgers.md#81-installing-ledgers)). Node reconciliation drops each replica its view
  revokes ([ledgers §8.5](components/ledgers.md#85-replica-lifecycle-on-a-node)).
- **What it names.** Each process's `Ledger` of the ledger and its primary's `LedgerWriter`; its replica on each node;
  the first component of its rows' tags and its segments' identities (§6); its watermark in the manifest; the LSM
  names of its drain outputs (§11); and its archive-log drain markers (§12).
- **Drops by tombstone.** A node drops a ledger only once its map view revokes it, recording a tombstone: the
  ledger is sealed and dropped on the node for its life, so the ledger's replica is never
  created again ([LG-I7](components/ledgers.md#5-invariants), [LR-I7](components/ledger-replicas.md#5-invariants),
  [LS-I2](components/ledgerstore.md#5-invariants)). A ledger's recovery sealed `N − Q + 1` nodes, each of which
  keeps its replica sealed until it drops the ledger, so a write delayed past the drop reaches no write quorum
  ([LP-I8](components/ledger-protocol.md#5-invariants)).
  The store relies on no order of ledger IDs, and ledgers are dropped in any order; Ledgers' revocation relies on
  their installation order (LG-I3).
- **Replica handle.** On one node, the LedgerStore's create record for each live replica assigns its `LedgerId` a
  handle, a `u32` below 2<sup>24</sup> − 1, ascending in WAL order and never reused on that node
  ([ledgerstore](components/ledgerstore.md#4-data-types)). Handles are local to a node and never leave it.

Equality is by value; the order is numeric. A `LedgerId` is a `u64` in binary encodings and decimal digits in
LSM object names. `LedgerId` and `NodeId` (§2) are defined here for every layer which names them. Their code
types are readonly structs in `Orleans.Durable.Storage.Engine.Ledgers.Abstractions` (namespace
`Orleans.Durable.Storage.Engine.Ledgers`), the lowest layer which uses them: `LedgerId` holds the non-zero `ulong` and
`NodeId` the non-empty `Guid`, each as `Value`. Only the constructors, which reject a value outside the domain, make an
identity: reading `Value` of a default instance throws `InvalidOperationException`, so every encoding and comparison
reads a validated identity. Their text forms are the decimal digits and `{node:N}`.

## 4. Keys and dyadic ranges

| Type | Definition | Ordering and equality | Owner |
| --- | --- | --- | --- |
| Key | An opaque byte string of 1 to 65,535 bytes | Lexicographic by unsigned bytes; bytewise equality | [Ledgers](components/ledgers.md#21-keys-and-ranges) |
| `KeyRange` | `(Prefix, Depth)`, `0 ≤ Depth ≤ 524,280`: the keys whose first `Depth` bits equal `Prefix`. `Prefix` is stored in `⌈Depth / 8⌉` bytes with the unused low bits 0 | Equality by value; ranges are related by containment, not ordered | Ledgers |

Bit `i` of a key is bit `7 − (i mod 8)` of byte `i / 8`; bits past the key's end are 0 for containment. `(0, 0)` is
the whole keyspace; the children of `(p, d)` are `(2p, d + 1)` and `(2p + 1, d + 1)`, which are siblings, and a range at
depth 524,280, the bits of the longest key, has none. Two ranges are disjoint or one contains the other, and the keys of
each range form an interval of the key order. A ledger's range and an LSM partition are both `KeyRange`s. A range's
text form is its prefix's bits, `/`, and its depth (`10/2`; `/0` is the whole keyspace).

A cluster has one keyspace; applications separate namespaces by key prefixes. Placement treats keys as uniformly
spread; nothing relies on the spread for safety.

## 5. Addresses

One scalar type names a slot: `Slot`, a readonly struct over a non-negative `long`, ordered and equatable by value,
with checked arithmetic. The ledger replica contract defines it, and a ledger writer's sequencer grants its values as
the slots of appends.

| Type | Domain | Meaning | Owner |
| --- | --- | --- | --- |
| `Slot` | `0 ..= 2^63 − 2`; granting `2^63 − 1` throws `OverflowException` in the sequencer; in a LedgerStore replica `0 ≤ slot < 2^40` | The position of one batch in its ledger, and the register which holds it in each of the ledger's replicas | [Ledger replicas](components/ledger-replicas.md#4-data-types), [ledger protocol](components/ledger-protocol.md#4-data-types), [Ledgers](components/ledgers.md#22-rows-batches-and-tags), [LedgerStore](components/ledgerstore.md#4-data-types) |

### 5.1 Mappings

| From | To | Mapping | Owner |
| --- | --- | --- | --- |
| Batch proposed to ledger `S` | Slot | The `Slot` its append acquired, whose register the append writes ([LP-I1](components/ledger-protocol.md#5-invariants)) | Ledgers, ledger protocol |
| `Slot a` in a `Ledger` | Each replica's slot | `a` on every replica | `Ledger` |
| `Slot a` in a LedgerStore replica with handle `h` | Record key | `a` below 2<sup>40</sup>; record key `(h << 40) \| a` | [LedgerStore](components/ledgerstore.md#7-formats) |
| Slot's latest record | WAL page | `WalPage(File, Index)`, data page `Index` of file `File`, at byte `P × (1 + Index)` | LedgerStore, [WAL](components/wal.md#4-data-types) |
| WAL record | `WalPosition(File, Offset)` | A file number and the byte offset of the record's first frame; ordered by file, then offset | WAL |
| Row `i` of the batch at slot `s` of ledger `S` | Tag | `(S, s, i)` (§6) | Ledgers |

## 6. Tags

A row's **tag** is `(LedgerId, Slot, Index)`: its ledger, its batch's slot (a `long`), and its index in the batch
(an `int` from 0). Tags order lexicographically and compare by value. For every key, the tag order of its decided rows
is their acceptance order ([LG-I6](components/ledgers.md#5-invariants)), so tags order each key's history across
the live ledgers and the LSM. The LSM stores each row with its tag; sections record their tag span (`MinTag`,
`MaxTag`); journal state records the tags of a journal's base and later entries. Byte encodings arrive with the
implementation ([archive LSM §7](components/archive-lsm.md#7-formats)). Owner:
[Ledgers](components/ledgers.md#22-rows-batches-and-tags).

A **segment** is identified by `LedgerSegmentId`, `(LedgerId, Slot)`: its ledger and its first slot, which carries the
segment-start flag; a segment never spans ledgers. Within a segment, each key defined there has an **ordinal**, the
number of keys the segment defined before it, from 0, below `MaxSegmentKeys`; a referencing row names its key by it,
and a key index entry names a row by its tag, its defining row's slot, and its ordinal. Ordinals are local to their
segment and compare numerically. Owner: [Ledgers](components/ledgers.md#22-rows-batches-and-tags).

## 7. Journal identities and routing

| Type | Domain | Ordering and equality | Encoding | Owner |
| --- | --- | --- | --- | --- |
| `JournalKey` | A non-empty string | Ordinal, case-sensitive | Strict UTF-8 of 1 to 65,531 bytes | [Journals](components/journals.md#41-keys) |
| Row key | `xxhash32(UTF-8 key, seed 0)` as 4 big-endian bytes, then the UTF-8 key: 5 to 65,535 bytes | As keys (§4) | The row's key in Ledgers and the LSM | Journals |
| `JournalVersion` | A journal's version `(Base, Count)`: a readonly record struct of `Orleans.Durable.Storage.Engine.Journals`, converted from the row version Ledgers computes (`RowVersion`, §9.1); `(Origin, 0)` for a journal with no history | Equality only; no order | The expected version in the entry record; in text, base64url without padding of the base's length, its bytes, and the count as a shortest varint, canonical, the grain's ETag | [Journals](components/journals.md#43-versions) |

**Routing.** A journal's row key is a stable function of its `JournalKey`. Its owner is the primary of the open ledger
whose range contains the row key (`LedgerMapView.Find`), and its history is the row key's ([archive
§2.1](components/archive.md#21-history-of-a-key)). The hash spreads journals evenly, so midpoint splits halve load. A
process which is not the owner forwards the encoded entry, or answers `Moved` naming the primary its cached map names
([journals §8.5](components/journals.md#85-forwarding)). The Orleans composition maps each grain to its `JournalKey`
([orleans](compositions/orleans.md)). Row-key vectors arrive with the implementation.

## 8. Sequencer identities

| Identity | Definition | Encoding | Owner |
| --- | --- | --- | --- |
| `SequencerInstanceId` | A non-empty `Guid` naming one sequencer lifetime, drawn when a `Ledger` creates it with the ledger's one `LedgerWriter` (`CreateWriter`), never reused: the executor of a transition creates a writer for each ledger it opens ([LP-I15](components/ledger-protocol.md#5-invariants)). Each `SlotGrant` carries its `Lifetime`, the ID of the lifetime which granted it, and a lifetime completes only its own slots | 16 bytes big-endian in the ledger map's primary claims; 32 lowercase hexadecimal digits as text | [ledger protocol](components/ledger-protocol.md#4-data-types) |
| Primary claim | `(Node, Sequencer)`: the node whose process holds the ledger's sequencer lifetime, and that lifetime's `SequencerInstanceId` | In the ledger's map entry | [Ledgers](components/ledgers.md#4-data-types) |

A lifetime lives in the `LedgerWriter` which created it, in that writer's process, so only that process proposes to the
ledger ([LG-I5](components/ledgers.md#5-invariants), [LP-I16](components/ledger-protocol.md#5-invariants)). A restarted
process holds none of its predecessor's lifetimes; its ledgers are taken over ([ledgers
§8.6](components/ledgers.md#86-takeover)).

## 9. Versions

Versions are opaque condition tokens. Callers compare them for equality only, never parse or order them, and use
each only with the register or object from which they read it.

| Version | Domain | Per-backend form | Owner |
| --- | --- | --- | --- |
| `MetaStoreVersion` | A non-blank string; equality is ordinal; every applied write produces one the register never had ([MS-I5](components/metastore.md#5-invariants)) | Memory: decimal count of applied writes; Fast CASPaxos: canonical Base64 of the protocol's version bytes; Azure Blob and Azure Table: ETag; DynamoDB: a fresh 32-hex GUID per write; Firestore: Base64 of the update time (seconds `int64` big-endian, nanoseconds `int32` big-endian); file system: a fresh random `Guid` | [MetaStore](components/metastore.md#4-data-types) |
| Object version | The token `ReadVersionedAsync` returns and `ReplaceAsync` names; used only for the manifest. It may be a digest of the content, so an object which holds earlier content again holds its earlier version again, and a replace never writes content the object held before its current content ([LSM §3](components/archive-lsm.md#3-requirements-downward)) | Azure Blob and Amazon S3: ETag; Google Cloud Storage: generation; memory: the decimal count of the store's applied writes; file system: the lowercase hexadecimal SHA-256 of the content | [LSM](components/archive-lsm.md#3-requirements-downward) |
| Slot-index checkpoint generation | `u64` in [1, 2<sup>63</sup>) per ledger replica; never taken while a usable root slot or generation file names it ([LS-I9](components/ledgerstore.md#5-invariants)); 0 is null | `{generation:D20}.clsi` and the root slot | [LedgerStore](components/ledgerstore.md#7-formats) |
| WAL file number | `u64` from 1, ascending, never reused within a directory | `{file:D20}.clfl` | [WAL](components/wal.md#7-formats) |

The manifest's **epoch**, which every commit increments, is a counter, not a condition token: views and commit
outcomes compare it ([archive LSM §2.2](components/archive-lsm.md#22-manifest-view)). So is the node registry's
**revision**, which every registry write increments, and which installs and the ledger map's retirement floor compare
([ledgers §2.6](components/ledgers.md#26-transitions), [orleans §5.3](compositions/orleans.md#53-node-registry)).
Format versions are not identities: each format has exactly one current version ([formats](formats.md)).

### 9.1 Row versions and archive tokens

| Value | Domain | Equality | Encoding | Owner |
| --- | --- | --- | --- | --- |
| `RowToken` | 0 to 32 opaque bytes; the empty token is `Origin` | Bytewise; each provider's encoding is canonical, so byte equality is equality | A `u8` length and the bytes in a defining row of a batch | [Ledgers](components/ledgers.md#4-data-types), minted by the archive provider |
| Archive token | A key's `RowToken` for one segment, stored in its defining row: `Origin` for a key's first segment, otherwise `Plan` of the key's previous token and write, or a prefix's `Token` after a rebase ([JS-I10](components/journals.md#5-invariants)) | Bytewise | The provider's private encoding: empty on the LSM; on archive logs a version byte and the position `(g, P, b)` as varints ([archive logs §7.2](components/archive-logs.md#72-tokens)) | [Archive](components/archive.md#4-data-types) |
| Version base | A `RowToken` the provider minted, which advances with every write it applies to a key and never repeats while it holds the key ([HI-I4](components/archive.md#5-invariants)): on archive logs the log's token, on the LSM the 20-byte tag of the key's last stored row | Bytewise | As a token | [Archive](components/archive.md#4-data-types) |
| Row version (`RowVersion`) | `(Base: RowToken, Count: u64)`: the key's version after a row; every row adds 1 to the count and keeps the base | Value; no order | The version before a key's defining row, as its base and a varint count, in that row; implied for later rows of the segment | [Ledgers](components/ledgers.md#4-data-types) |

Ledgers stores and lends tokens and version bases verbatim, Journals keeps and passes them, and only the provider which
minted a token decodes it. A key's versions are unique for its lifetime but not continuous across a **rebase**, which
takes `(VersionBase, 0)` from the archive's prefix once the key has no live rows
([journals §8.3](components/journals.md#83-warm-state)). The cluster's archive store and the store fields its tokens
depend on are fixed for the cluster's life (§12).

## 10. MetaStore register names

A register is keyed by `(cluster, name)`. A name is one or more components separated by `/`, each
`[a-z0-9._-]{1,128}`, compared case-sensitively.

| Name | One per | Value | Owner |
| --- | --- | --- | --- |
| `ledger-map` | Cluster | The ledger map: `NextLedgerId`, the retirement floor, and every live ledger | [Ledgers](components/ledgers.md#7-formats) |
| `node-registry` | Cluster | `CLNR`: every node's registration, written by conditional read-modify-write which accepts only newer silo generations | [Orleans](compositions/orleans.md) |

Backends key a register as `{cluster:N}/{name}` below their configured root. Azure Table row keys and Firestore
document identifiers write each `/` as `:`, which no name contains, so the mapping stays injective
([metastore](components/metastore.md#7-formats)). Names outside every cluster's namespace (`clog-probe/...`) are
used only by validation probes.

## 11. LSM object names

Every object lies below the cluster's LSM prefix: a provider's container or bucket, its configured base prefix, and the
cluster's store prefix, `{prefix}/` ([orleans §5.13](compositions/orleans.md#513-archive-log-store)). A name is 1 to 200
ASCII characters, `/`-separated components of `[a-z0-9_-]`, none empty, so no name holds a `.`, which a provider may use
for its own files ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations)). The bound is the LSM's
own: every name it makes is at most 75 characters (below), and the manifest's format carries a name's length in one
byte, so 200 leaves room for new kinds of object. A provider whose names are shorter checks its own limit where it maps
an object name. Owner: [LSM](components/archive-lsm.md#4-data-types).

| Name | Object | Lifetime |
| --- | --- | --- |
| `manifest` | The manifest | Replaced only conditionally on its object version |
| `store` | The LSM's store record ([archive §2.4](components/archive.md#24-selecting-the-store)): `key=value` lines of the store's ID and layout, `lsm`, and the caller's parameters | Created once, never replaced |
| `d/{S}/{a}/{id}` | A drain output of ledger `S` from watermark `a` | Immutable; deleted by the sweeper |
| `c/{partition}/{id}` | A compaction output, scoped by its first output partition | Immutable; deleted by the sweeper |

`{id}` is a random 128-bit value, so names are never reused ([LSM-I7](components/archive-lsm.md#5-invariants)). A drain
output's name carries the parameters of its reference condition; a compaction output records its inputs in its trailer
([archive LSM §8.5](components/archive-lsm.md#85-garbage-collection)). `{S}` and `{a}` are decimal, without leading
zeros, and `{id}` is 32 lowercase hexadecimal digits. `{partition}` is a bounded digest of the partition: its depth in
decimal, then, when the depth is above 0, `-` and the lowercase hexadecimal of its prefix's first ⌈min(depth, 64) / 8⌉
bytes, at most 23 characters. It scopes the name for listings and people, while the section's `Partition` carries the
partition whole and `{id}` keeps the name unique.

## 12. Names derived from identities

| Name | Derivation | Owner |
| --- | --- | --- |
| Cluster claim | `{root}/cluster` in a node root; content `{cluster:N}` with no terminator | [Ledger replicas](components/ledgerstore.md#71-node-root) |
| LedgerStore checkpoint directory | `{root}/slots/{handle}/` (decimal, no leading zeros) | [LedgerStore](components/ledgerstore.md#7-formats) |
| Journal index | `{root}/index/`: the node's derived per-ledger key indexes and tombstones | [Journals §8.3](components/journals.md#83-warm-state) |
| Node replica | `(node identity, LedgerId)`: the node which holds it and the ledger | [Ledger replicas](components/ledger-replicas.md#22-iledgerreplicatransport) |
| MetaStore register key | `{cluster:N}/{name}` (§10) | [MetaStore](components/metastore.md#7-formats) |
| LSM objects | §11, under the cluster's store prefix | [LSM](components/archive-lsm.md#4-data-types) |
| Archive log on Azure | `{prefix}/wal/{id}`, the write-ahead append blob, and `{prefix}/checkpoints/{id}/{g:D20}`, generation `g`'s checkpoint, under the cluster's store prefix, with `{id}` the journal identity `ArchiveLogNames` gives the key, as Orleans Journaling's own Azure provider names it; the `ArchiveLogName` carries the key's xxHash64, which every write records as `durable_key` and every read checks; a key whose longer name exceeds Azure's 1,024 characters is refused at admission (`AdmitsKey`) | [Archive logs](components/archive-logs.md#71-names) |
| Archive log as chunks | The prefix `{name}/`, `{name}` the key in lowercase hexadecimal under the composition's prefix: `{name}/{g:D20}/base` and `{name}/{g:D20}/{P₀:D20}` | [Archive logs](components/archive-logs.md#74-chunk-layout) |
| Archive-log store record | `{prefix}/durable-storage/store`, created once: the store's record ([archive §2.4](components/archive.md#24-selecting-the-store)), `key=value` lines of the store's own fields (its ID, its layout, and its `Plan` parameters: `MaxAppendBytes`, `MaxUnits`, `ReplaceRequestUnits`) and the caller's opaque `key=value` parameters, which `StoreRecordAsync(parameters)` records and answers verbatim (the Orleans composition supplies `format` and `max-body-bytes`); a process whose store fields differ is refused by the store, and one whose parameters differ by its own check, each with `ArchiveStoreMismatchException` ([archive §4](components/archive.md#4-data-types)) | [Archive logs](components/archive-logs.md#75-clogs-objects) |
| Archive-log drain marker | `{prefix}/durable-storage/d/{S:D20}/{start:D20}`, `{S}` the ledger and `{start}` the segment's first slot; its content is the segment's end slot, in 20 decimal digits | [Archive logs](components/archive-logs.md#75-clogs-objects) |
| Store prefix | 1 to 16 characters of `[a-z0-9_-]`: by default the first 50 bits of the SHA-256 of Orleans' service ID in 10 lowercase base32 characters, or configured; recorded in the node registry with the archive store's kind and ID | [Orleans](compositions/orleans.md#513-archive-log-store), [Archive](components/archive.md#4-data-types) |
| Block cache entry | `(object name, block)`; names are never reused, so entries need no invalidation | [LSM](components/archive-lsm.md#87-caches) |

No archive log is named under `durable-storage/`, and names are injective and prefix-free
([LOG-I5](components/archive-logs.md#5-invariants)).

## 13. Summary

| Type | Domain | Ordering | Equality | Encoding | Owner |
| --- | --- | --- | --- | --- | --- |
| Cluster | Non-empty `Guid` | None | Value | `{cluster:N}`; 16 bytes big-endian | Deployment; every storage root (§1) |
| Node identity (`NodeId`) | Non-empty UUID | None | Value | 16 bytes big-endian; `{node:N}` | [LedgerStore](components/ledgerstore.md) |
| Silo | `(IP endpoint, generation > 0)` | Generation | Value | `CLNR` | [Orleans](compositions/orleans.md) |
| `LedgerId` | `u64 ≥ 1` | Numeric (installation order) | Value | `u64`; decimal in object names | [Ledgers](components/ledgers.md) |
| Replica handle | `u32 < 2^24 − 1` | Numeric | Value | High 24 bits of a WAL record key; `u32` in create records and catalog checkpoints | LedgerStore |
| Key | 1–65,535 bytes | Lexicographic | Bytewise | Bytes | Ledgers |
| `KeyRange` | `(Prefix, 0 ≤ Depth ≤ 524,280)` | Containment | Value | `⌈Depth / 8⌉` prefix bytes and the depth | Ledgers |
| `Slot` | `0 ..= 2^63 − 2`; `< 2^40` in a LedgerStore | Numeric | Value | `int64`; low 40 bits of a WAL record key | [Ledger replicas](components/ledger-replicas.md), [ledger protocol](components/ledger-protocol.md), Ledgers |
| Tag | `(LedgerId, Slot, Index)` | Lexicographic | Value | `u64` ledger, `u64` slot, `u32` index (little-endian) in the manifest | Ledgers |
| `LedgerSegmentId` | `(LedgerId, Slot)`: a segment's ledger and first slot | Lexicographic | Value | The start slot in a drain marker's name | Ledgers |
| Ordinal | `0 ≤ ordinal < MaxSegmentKeys`, within one segment | Numeric | Value | Varint in a referencing row; implied in a defining row | Ledgers |
| `RowToken` (archive token, version base) | 0–32 opaque bytes; empty is `Origin` | None | Bytewise | `u8` length and bytes in a defining row | Ledgers; minted by the archive provider |
| `JournalKey` | Non-empty string, 1–65,531 UTF-8 bytes | Ordinal | Ordinal | Strict UTF-8 | [Journals](components/journals.md) |
| Row key | `[xxhash32][UTF-8 key]`, 5–65,535 bytes | Lexicographic | Bytewise | Bytes | Journals |
| `JournalVersion` | `(RowToken, u64)`, converted from `RowVersion` | None | Value | Base and varint count; canonical base64url text | Journals |
| `RowVersion` | `(RowToken, u64)` | None | Value | Base and varint count in a defining row | Ledgers |
| `SequencerInstanceId` | Non-empty `Guid` | None | Value | 16 bytes; 32 hex | Ledger protocol |
| Primary claim | `(Node, SequencerInstanceId)` | None | Value | In the ledger map | Ledgers |
| MetaStore register name | `/`-separated `[a-z0-9._-]{1,128}` components | Ordinal | Ordinal | `{cluster:N}/{name}` | [MetaStore](components/metastore.md) |
| `MetaStoreVersion` | Non-blank string | None | Ordinal | Backend-native token | MetaStore |
| Object version | Opaque token | None | Value | Backend-native token | [LSM](components/archive-lsm.md) |
| LSM object name | §11 | Ordinal | Ordinal | ASCII | LSM |
| Archive-log name | §12 | Ordinal | Ordinal | Azure blob names; object names | [Archive logs](components/archive-logs.md) |
| Checkpoint generation | `u64` in [1, 2<sup>63</sup>); never taken while a usable root slot or generation file names it (LS-I9) | Numeric | Value | `{generation:D20}` | LedgerStore |
| WAL file number | `u64 ≥ 1` | Numeric | Value | `{file:D20}` | [WAL](components/wal.md) |
| `WalPosition` | `(file, offset)` | Lexicographic | Value | — (in memory and checkpoints) | WAL |
| `WalPage` | `(file, index)` | Lexicographic | Value | File `u64`, index `u32` | WAL |
