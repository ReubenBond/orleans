# Format registry

Every durable format and wire protocol Clog defines, with the section which specifies it and the golden vectors
which pin it, and the layouts of object storage, the MetaStore, and local storage. Integers are little-endian
unless a format says otherwise; every CRC is CRC-32C ([conventions](conventions.md#4-integrity)).

Golden vectors live under `test/Durable.Storage/shared-vectors/<area>/`. The **Vectors** column names the files which
pin a format. A format's owning section gives its byte layout, which may precede its code; its encoder, decoder, golden
vectors, and fuzz target land together, and until then the format is *Not yet in code*.

## 1. Durable formats

| Format | Magic, version | Location | Owner | Vectors |
| --- | --- | --- | --- | --- |
| WAL file: header page, data pages of frames, hole pages | `CLFL`, 1 (header page) | `{root}/wal/{file:D20}.clfl` | [wal §7](components/wal.md#7-formats) | `wal/headers.json`, `header-rejections.json`, `pages.json`, `page-rejections.json`, `example.json` |
| Cluster claim | — (32 lowercase hexadecimal digits, no terminator) | `{root}/cluster` of a node root; archive stores keep none, each lying under its cluster's store prefix | [ledgerstore §7.1](components/ledgerstore.md#71-node-root), [identity §1](identity.md#1-cluster) | None; behavioral tests (`FileNodeRootClaimTests`) |
| LedgerStore node file: the node identity | — (32 lowercase hexadecimal digits, no terminator) | `{root}/wal/node` | [ledgerstore §7.1](components/ledgerstore.md#71-node-root) | `ledgerstore/node-files.json`, `node-file-rejections.json` |
| LedgerStore catalog checkpoint: its cut, the next handle, each live replica's handle, first file, and ledger ID, and the tombstones | `CLSC`, 1 | `{root}/wal/{file:D20}-{offset:D20}.clsc`, named by its cut | [ledgerstore §7](components/ledgerstore.md#7-formats) | `ledgerstore/catalog.json`, `catalog-rejections.json` |
| LedgerStore WAL record (8-byte record key, then the body: a slot's non-empty DATA value; or, under the store handle, a create record of one or more ledger, handle, and window entries, or a seal or drop record) | — | Each WAL record of the node's WAL | [ledgerstore §7](components/ledgerstore.md#7-formats) | `ledgerstore/records.json`, `record-rejections.json` (create, seal, and drop records, and slot records) |
| Slot-index checkpoint generation file (leaves and interior nodes; leaf states DATA or Faulty) | — (referenced by `CLSI` roots) | `{root}/slots/{handle}/{generation:D20}.clsi` | [ledgerstore §7](components/ledgerstore.md#7-formats) | `ledgerstore/leaves.json`, `leaf-rejections.json`, `generations.json`, `tree-rejections.json` |
| Slot-index checkpoint root (two slots, with the replica's seal) | `CLSI`, 1 | `{root}/slots/{handle}/slots.root` | [ledgerstore §7](components/ledgerstore.md#7-formats) | `ledgerstore/roots.json`, `root-rejections.json`, the sealed flag among them |
| Ledger map: the epoch, `NextLedgerId`, the retirement floor, and every live ledger, with a trailing CRC-32C | `CLLM`, 1 | MetaStore register `ledger-map` | [ledgers §7](components/ledgers.md#7-formats) | `ledgers/maps.json`, `map-rejections.json` |
| Batch: the segment-start flag, the segment offset, the ordinal base, and the rows, each defining its key (the key, the version before it, and the key's token) or referencing it by ordinal, with its kind and inline body, and a trailing CRC-32C; a marker holds no rows | `CLLB`, 1 | The DATA value of each slot of a ledger | [ledgers §7](components/ledgers.md#7-formats) | `ledgers/batches.json`, `batch-rejections.json`; `segments.json`, `segment-rejections.json` (row 1.7) |
| Journals row body: an `Append`'s or a `Replace`'s payload; a `Delete` row's tombstone, the deletion time as UTC ticks (`i64`, little-endian, 0 to `DateTime.MaxValue`'s), or nothing for an `Expired` | — (the batch's) | Each Journals row's body in a batch, and the content and tombstone each archive store keeps | [journals §7](components/journals.md#7-formats) | `journals/entries.json`, each record's `rowKind`, `versionBefore`, and `rowBody` |
| Run object: sections of CRC-32C-checked blocks; then each section's block index, with a continuation flag per block, and Bloom filter, recording its hash count; then a CRC-32C-checked trailer listing each section's blocks, row count, tag span, the positions of its block index and filter, with the reference condition and its own length; each row's body inline; at most 2<sup>31</sup> − 1 bytes | `CLAR`, 1 (trailer) | `d/{S}/{a}/{id}` and `c/{partition}/{id}` below the LSM prefix | [archive LSM §7](components/archive-lsm.md#7-formats) | `archive/run-objects.json`, `run-object-rejections.json`; the `archive-run-object` fuzz target |
| Manifest: the epoch, `DrainedBelow`, each ledger's watermark and completion, and the committed sections, with a CRC-32C | `CLAM`, 1 | `manifest` below the LSM prefix | [archive LSM §7](components/archive-lsm.md#7-formats) | `archive/manifest.json`, `manifest-rejections.json`; the `archive-manifest` fuzz target |
| Store record: UTF-8 `key=value` lines, the store's ID and layout, then its own fields (its `Plan` parameters), then the caller's opaque parameters under `caller.`, which `StoreRecordAsync(parameters)` records and answers verbatim (the Orleans composition supplies `format` and `max-body-bytes`), each group compared as a map, apart: a process whose layout or fields differ is refused by the store, and one whose parameters differ by its own check, each with `ArchiveStoreMismatchException` naming the field | — | `store` below an LSM prefix; `durable-storage/store` in an archive-log store; created once | [archive §7](components/archive.md#7-formats), [archive LSM §4](components/archive-lsm.md#4-data-types), [archive logs §7.5](components/archive-logs.md#75-clogs-objects) | `archive/store-records.json`, `store-record-rejections.json`; the `archive-store-record` fuzz target |
| Archive log on Azure: Orleans Journaling's layout, a write-ahead append blob of the current generation's appends and a checkpoint block blob of each generation's base, with Orleans' metadata (`format`, `checkpoint`, `checkpoint_offset`, `wal_generation`), Clog's metadata (`durable_key`, the key's xxHash64; `durable_kind`, the kind of the opener which created the generation; `durable_tomb`, a `Delete` opener's tombstone; `durable_base_md5`, the MD5 of the generation's base), and the index tag `durable-gen` | — (Orleans' journal format key, `format`) | `wal/{id}` and `checkpoints/{id}/{g:D20}` in the archive-log container | [archive logs §7.3](components/archive-logs.md#73-azure-layout) | `archive-logs/metadata.json`, `metadata-rejections.json` |
| Archive log as chunk objects: a base per generation, a `CLAB` header and its content, and a chunk per append run, its bytes and their CRC-32C, created once | `CLAB` 1 | `{name}/{g:D20}/base-{h:x16}` and `{name}/{g:D20}/{P₀:D20}` | [archive logs §7.4](components/archive-logs.md#74-chunk-layout) | `archive-logs/chunk-bases.json`, `chunks.json` |
| LSM token and version base: the token is empty, and a version base is the tag of the prefix's last row, its ledger (`u64`), slot (`u64`), and index (`u32`), 20 bytes, or empty for a prefix with no rows | — | Each prefix's `Token` and `VersionBase` | [archive LSM §2.8](components/archive-lsm.md#28-prefixes-and-tokens) | `archive/version-bases.json` |
| Archive-log token: private to `LogArchiveStore`; `Origin` empty, otherwise a version byte and the position `(g, P, b)` as varints | 1 (version byte) | Each defining row's token and each prefix's `Token` and `VersionBase` | [archive logs §7.2](components/archive-logs.md#72-tokens) | `archive-logs/tokens.json` |
| Archive-log drain marker: its segment's end slot, 20 decimal digits | — | `durable-storage/d/{S:D20}/{start:D20}` in the archive-log store, created once | [archive logs §7.5](components/archive-logs.md#75-clogs-objects) | With the implementation |
| Node registry | `CLNR`, 1 (header and `orleans.durable.storage.v1.NodeRegistry` body, one `NodeEntry` per node) | MetaStore register `node-registry` | [orleans §7.3](compositions/orleans.md#73-node-registry-clnr-v1) | `orleans/registries.json`, `registry-rejections.json`; the `orleans-registry` fuzz target |
| MetaStore register keys and version tokens | — | Each backend, below its root | [metastore §7](components/metastore.md#7-formats) | `metastore/register-keys.json` |
| File-system MetaStore register file | `CLFM`, 1 | `{root}/{cluster:N}/{stem}~clfm` | [metastore §7](components/metastore.md#7-formats) | `metastore/register-file-rejections.json`; behavioral tests (`FileSystemMetaStoreTests`) and the `metastore-register` fuzz target |
| Journal index: WAL-less atomic-flush RocksDB (`key_index`, `tombstones`, and `default` watermarks column families) | — (no format version; derived) | `{root}/index/` | [journals §8.3](components/journals.md#83-warm-state) | `RocksDbJournalIndexTests` |
| Block cache region files: the LSM blocks, each with a CRC-32C | — (derived) | A directory on the host's volatile disk | [archive LSM §8.7](components/archive-lsm.md#87-caches) | With the implementation |
| Object-store ledger store record: the store ID and the layout, as two `key=value` lines in the archive store record's form | — | `ledgers/store` below the cluster's store prefix | [object-store ledgers §7](components/object-ledgers.md#7-formats) | `test/Durable.Storage/shared-vectors/ledgers/object-store-records.json` and `object-store-record-rejections.json`; fuzz target `ledgers-object-store-record` |
| Object-store ledger slot object: a batch with its recorded CRC-32C, or empty, a SEAL; slots reverse-keyed, as the per-slot object profile removed in `047dc2c94` was; the `sealed` and `dropped` markers | — | `ledgers/l/{id:D20}/s/{rev}`, `ledgers/l/{id:D20}/sealed`, and `ledgers/l/{id:D20}/dropped` below the cluster's store prefix | [object-store ledgers §7](components/object-ledgers.md#7-formats) | `test/Durable.Storage/shared-vectors/ledgers/object-names.json` and `object-slots.json`, the profile's revived |
| Object-store ledger extent: DATA, `ROLL`, and `SEAL` frames, each append ending with a trailer; or its tombstone, one `SEAL` frame in a block blob | `CLOE`, 1 | `ledgers/l/{id:D20}/x/{rev}` below the cluster's store prefix | [object-store ledgers §7](components/object-ledgers.md#7-formats) | `test/Durable.Storage/shared-vectors/ledgers/object-extents.json` and `object-extent-rejections.json`; fuzz target `ledgers-object-extent` |
| Object-store ledger cache: batches with their CRC-32C | — (derived) | Memory, and optionally a directory on the host's volatile disk | [object-store ledgers §8.4](components/object-ledgers.md#84-reads-and-the-cache) | Behavioral and metamorphic tests (`ObjectLedgerCacheTests`) |

Derived formats are rebuilt from authoritative sources when they fail validation: an unusable slot-index or catalog
checkpoint is recomputed from the WAL; a corrupt or unreadable journal index is discarded, and its key indexes and
tombstones are recomputed from the node's live ledgers
([JS-I6](components/journals.md#5-invariants)); a cached block which fails its CRC-32C is read again from its
object. The journal index has no format version: a format change discards and rebuilds it.

## 2. Wire protocols

Generated serializers stay in the composition which owns their protocol (Orleans' for grain messages); domain contracts
never expose them. `NodeRegistryCodec` writes and checks the canonical encoding of `orleans.durable.storage.v1` itself,
with no Protocol Buffers runtime ([orleans §7.3](compositions/orleans.md#73-node-registry-clnr-v1)).

| Package or protocol | Service or messages | Project | Carrier | Owner |
| --- | --- | --- | --- | --- |
| `orleans.durable.storage.v1` | `NodeRegistry`, `NodeEntry` (durable, not an RPC) | `Orleans.Durable.Storage` | MetaStore register value | [orleans](compositions/orleans.md) |
| Fast CASPaxos register service | The consensus family's `IRegisterService` (`src/Consensus/`) | Consensus family | In-process; Orleans RPC for remote voters | [metastore §8](components/metastore.md#8-mechanism-and-policy) |
| Orleans grain interfaces | Replica calls, forwarded entries and reads (data plane); transitions, commit submissions, and the controller grain's other calls (control plane); transient, Orleans serialization | `Orleans.Durable.Storage` | Orleans silo-to-silo messaging | [orleans §7.2](compositions/orleans.md#72-grain-interfaces), [§5.12](compositions/orleans.md#512-replica-transport) |
| Cloud service APIs | MetaStore registers, LSM objects, and Azure archive logs, through each service's SDK | `Orleans.Durable.Storage.AzureStorage`, `Orleans.Durable.Storage.S3`, `Orleans.Durable.Storage.GoogleCloud` | HTTPS | [metastore §9](components/metastore.md#9-providers-and-implementations), [archive LSM §9](components/archive-lsm.md#9-providers-and-implementations), [archive logs §9](components/archive-logs.md#9-providers-and-implementations) |
| Journal entry record | One entry: a CRC-32C over the row key and the record, the kind, the expected version, a `Delete`'s deletion time, and the body; never stored, so it carries no version byte | `Orleans.Durable.Storage.Engine.Journals` | Carried verbatim from the writing process to the key's primary, in process or in the composition's forwarding calls | [journals §7](components/journals.md#7-formats) |

A forwarded entry carries the writing process's encoded entry record verbatim, with its CRC-32C
([JS-I5](components/journals.md#5-invariants)); the primary verifies it and writes the entry's row from it. Its golden
vectors are `journals/entries.json`, `entry-rejections.json`, and `row-keys.json`, and its fuzz target
`journals-entry`. A journal version's text form, the grain's ETag, has `journals/versions.json` and
`version-rejections.json`, and its fuzz target `journals-version`.

### 2.1 Protobuf framing

Protobuf messages are tagged fields, with key `(field_number << 3) | wire_type`. Integers, booleans, and enums use
varints (wire type 0); strings, bytes, embedded messages, and packed repeated scalars use length-delimited fields
(wire type 2). Fixed-width fields, when declared, use protobuf's fixed32/fixed64 little-endian encodings. Fields
have numbers and semantics rather than fixed byte offsets. The owning schema tables identify optional presence,
repeated fields, and discriminated `oneof` values.

The transport and provider limits apply at their own boundaries ([conventions](conventions.md#7-payload-size)).
CRC-bearing durable envelopes retain their own coverage when their bodies use protobuf; RPCs rely on their
transport and the end-to-end stored-value integrity checks. Generated descriptors and exact-byte golden vectors
qualify different contracts.

## 3. Object storage layout

Each cluster's LSM objects lie below one prefix: a provider's container or bucket, its configured base prefix, and the
cluster's store prefix, `{prefix}/`, which several clusters on one bucket keep apart
([identity §11](identity.md#11-lsm-object-names), [orleans §5.13](compositions/orleans.md#513-archive-log-store)).

```text
{prefix}/
  store                       the store record (archive §2.4): key=value lines, created once
  manifest                    the manifest, replaced only conditionally
  d/{S}/{a}/{id}              drain outputs of ledger S from watermark a
  c/{partition}/{id}          compaction outputs
```

Every object but `manifest` is created by a create which never overwrites and is immutable; names are never reused
([LSM-I7](components/archive-lsm.md#5-invariants)), and only the sweeper deletes objects, once no commit can reference
them ([LSM-I6](components/archive-lsm.md#5-invariants)). The file-system provider maps the layout onto one directory,
writing each `/` of a name as `~` ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations)).

A cluster whose archive store is archive logs keeps no LSM objects. Its archive-log storage, an Azure container, holds
under the cluster's store prefix each key's log in Orleans Journaling's layout, under the journal identity `{id}` which
`ArchiveLogNames` gives the key, and Clog's own objects under `{prefix}/durable-storage/` ([archive logs
§7](components/archive-logs.md#7-formats)):

```text
{prefix}/wal/{id}                   the write-ahead append blob of key k's current generation, tagged durable-gen
{prefix}/checkpoints/{id}/{g:D20}   the base of generation g, created once
{prefix}/durable-storage/store                 the store record (archive §2.4): key=value lines, created once
{prefix}/durable-storage/d/{S:D20}/{start:D20} the drain marker of ledger S's segment at start, created once
```

The chunk layout, over any object store with create-once writes, holds each log under its prefix `{name}/` as
`{name}/{g:D20}/base-{h:x16}` and `{name}/{g:D20}/{P₀:D20}`, with the same objects under `durable-storage/`. Only
drains write a log's objects, by conditional creates and appends at their token's position, and a log is never deleted;
only the sweeper deletes, a generation's base once the log's current generation is above it, and a dropped ledger's
markers ([LOG-I2, LOG-I6](components/archive-logs.md#5-invariants)).

A cluster which places ledgers in an object store keeps them under `ledgers/` below the cluster's store prefix,
`{prefix}/ledgers/`, the prefix its LSM objects live under, which the node registry records and the operator keeps
unique within its bucket ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations), *One cluster per
store prefix*; [object-store ledgers §7](components/object-ledgers.md#7-formats));
`{rev}` is a slot's reverse key, `(2^63 − 2) − slot` as 20 decimal digits, so a listing returns the newest slots first:

```text
{prefix}
  ledgers/store                       the store record (key=value lines: store ID, layout), created once
  ledgers/l/{id:D20}/sealed           created once by a recovery; never deleted
  ledgers/l/{id:D20}/dropped          created once by the sweeper; never deleted
  ledgers/l/{id:D20}/s/{rev}          per-slot: a batch with durablecrc32c metadata, or empty, a SEAL kept for the store's life
  ledgers/l/{id:D20}/x/{rev}          append: an extent (CLOE v1 append blob) from slot rev, or its block-blob tombstone
```

Data objects are placed only by creates which never overwrite and appends at the writer's offset; only
`ObjectLedgerSweeper` deletes, and replaces a fence extent with its tombstone, and it keeps every SEAL, tombstone, and
marker ([OL-I8, OL-I9](components/object-ledgers.md#5-invariants)).

## 4. MetaStore layout

| Register | Key | Value | Owner |
| --- | --- | --- | --- |
| `ledger-map` | `{cluster:N}/ledger-map` | `CLLM` | [ledgers §7](components/ledgers.md#7-formats) |
| `node-registry` | `{cluster:N}/node-registry` | `CLNR` | [orleans](compositions/orleans.md) |

Each backend places the key below its configured root and stores the value bytes verbatim; Azure Table row keys
and Firestore document identifiers write each `/` as `:` ([metastore §7](components/metastore.md#7-formats)). The
file-system provider stores each register as the `CLFM` file `{root}/{cluster:N}/{stem}~clfm`, whose stem writes each
`/` of the name as `+` and each `.` as `=`, with a `{stem}~lock` sibling which serializes writers. Validation probes
use names below `clog-probe/`, outside every cluster's namespace.

## 5. Local storage layout

Each node root holds one directory per kind of file. On Windows a dedicated volume is mounted at the node root, not at
`wal/` or `slots/`; elsewhere each subdirectory can be its own mount point
([ledgerstore §7.1](components/ledgerstore.md#71-node-root)):

```text
{root}/
  cluster                     the cluster claim: 32 lowercase hexadecimal digits
  wal/                        authoritative: the node identity and every record, creates, seals, and drops included
    lock                      WriteAheadLog's exclusive-ownership lock file
    node                      the node identity: 32 lowercase hexadecimal digits
    {file:D20}.clfl           WriteAheadLog files
    {file:D20}-{offset:D20}.clsc  CLSC catalog checkpoints, derived from the records
  slots/                      derived from wal/
    {handle}/                 one live ledger replica's checkpoints
      slots.root              the two-slot CLSI root
      {generation:D20}.clsi   immutable checkpoint generation files
  index/                      derived from the history: the node's journal index (RocksDB)
    CURRENT, MANIFEST-*, ...  RocksDB's files holding warm state and key indexes
```

A host's volatile disk holds the LSM block cache in its own configured directory; nothing in it needs to
survive a restart ([archive LSM §8.7](components/archive-lsm.md#87-caches)).

The node identity lives in the node file with the WAL, so the two always move together. Each Orleans silo owns one
node root. Each temporary file has an owning component and its publication and recovery procedure: the cluster
claim's owner atomically publishes a complete same-directory temporary file and arbitrates on the destination
([Ledger replicas §7.1](components/ledgerstore.md#71-node-root)); checkpoint and catalog owners
each specify the lifetime of their temporary files. A generic directory-wide cleanup cannot establish their ownership.

## 6. Golden vectors

| Area | Exists | With the implementation |
| --- | --- | --- |
| `wal/` | `headers.json`, `header-rejections.json`, `pages.json`, `page-rejections.json`, `example.json` | — |
| `ledgerstore/` | `node-files.json`, `catalog.json`, `records.json`, `leaves.json`, `generations.json`, `roots.json`, and `node-file-rejections.json`, `catalog-rejections.json`, `record-rejections.json`, `leaf-rejections.json`, `root-rejections.json`, `tree-rejections.json` | Create records of one and several pairs, and seal and drop records, in `records.json` and `record-rejections.json`; catalog checkpoints replacing the catalog file's vectors in `catalog.json` and `catalog-rejections.json` |
| `metastore/` | `register-keys.json`, `register-file-rejections.json` | — |
| `ledgers/` | `maps.json`, `map-rejections.json`, `batches.json`, `batch-rejections.json`, `segments.json`, `segment-rejections.json` | — |
| `journals/` | `entries.json`, `entry-rejections.json`, `versions.json`, `version-rejections.json`, `row-keys.json` | — |
| `archive-logs/` | `tokens.json` (both layouts' encodings, canonical forms, and malformed tokens by reason), `metadata.json` (`durable_kind`, `durable_tomb`, `durable_base_md5`, `durable_key`, Orleans' keys, and each write kind's full set) and `metadata-rejections.json` (each by the first rule it breaks, duplicate and case-variant names among them), written by `generate_vectors.py` | — |
| `archive/` | `manifest.json`, `manifest-rejections.json`, `run-objects.json`, `run-object-rejections.json`, `store-records.json`, `store-record-rejections.json`, `version-bases.json` | Object names |
| `orleans/` | `registries.json`, `registry-rejections.json` (each by the first rule it breaks, rule boundaries and pairs of adjacent rules among them), written by `generate_registry.py`, which serializes each body with the Protocol Buffers library | — |

Each area's vectors are read by the owning component's tests in both directions where the format has an encoder:
decoding the bytes yields the structured value, and encoding the value yields exactly the bytes. Rejection vectors name
the one rule each breaks, with the reason code the decoder reports in `Data["orleans.durable.storage.decode.reason"]`
([conventions §15](conventions.md#15-outcomes-unions-and-errors)), and the fuzz oracles and the tests of each caller's
classification load the codes from them. The record vectors' classification reasons (`record-truncated`,
`empty-slot-record`, `handle-not-allocated`) name the damage `LedgerRecords.Classify` finds, which reports a record
class rather than a rejection.

The vectors also seed a fuzz target for each implemented decoder
([verification §6.1](verification.md#61-decoder-fuzzing)), whose committed corpus the owning layer's tests replay on
every run:

| Format | Fuzz target | Seeds |
| --- | --- | --- |
| WAL header page | `wal-header` | `wal/headers.json`, `header-rejections.json`, the header of `example.json` |
| WAL data and hole pages | `wal-page` | `wal/pages.json`, `page-rejections.json`, the pages of `example.json` |
| WAL file, opened and read | `wal-scan` | The header and pages of `example.json`; each header of `headers.json` followed by the page vectors of its page size; each header of `header-rejections.json` |
| File-system MetaStore register file | `metastore-register` | Register files the encoder writes, and `metastore/register-file-rejections.json` |
| LedgerStore catalog checkpoint | `ledgerstore-catalog` | `ledgerstore/catalog.json`, `catalog-rejections.json` |
| LedgerStore WAL record | `ledgerstore-record` | `ledgerstore/records.json`, `record-rejections.json` |
| Slot-index leaf | `ledgerstore-leaf` | `ledgerstore/leaves.json`, `leaf-rejections.json` |
| Slot-index root slot | `ledgerstore-root` | `ledgerstore/roots.json`, `root-rejections.json` |
| Slot-index generation files, loaded through their root | `ledgerstore-generation` | `ledgerstore/generations.json`, alone and with a copy of its file as a second generation, `roots.json` without their files, and each tree of `tree-rejections.json` with its generation file |
| Ledger map | `ledgers-map` | `ledgers/maps.json`, `map-rejections.json` |
| Batch | `ledgers-batch` | `ledgers/batches.json`, `batch-rejections.json` |
| Segment: a batch count (`u16`, at most 1,024) and that many batches, each framed by its `u32` length, at slots 0 to `n − 1`, decoded by [ledgers](components/ledgers.md#7-formats) §7's batch and then segment rules | `ledgers-segment` | `ledgers/segments.json`, `segment-rejections.json` |
| Manifest | `archive-manifest` | `archive/manifest.json`, `manifest-rejections.json` |
| Store record | `archive-store-record` | `archive/store-records.json`, `store-record-rejections.json` |
| Run object, read whole | `archive-run-object` | `archive/run-objects.json`, `run-object-rejections.json` |
| Journal entry record | `journals-entry` | `journals/entries.json`, `entry-rejections.json` |
| Journal version text | `journals-version` | `journals/versions.json`, `version-rejections.json` |
| Node registry | `orleans-registry` | `orleans/registries.json`, `registry-rejections.json` |
| Journal node grain's answers, each as its surrogate's wire | `orleans-journal-write`, `orleans-journal-read`, `orleans-journal-page` | None: each case's encoding and refused surrogates, which the targets build |
| Replica node grain's answers, each as its surrogate's wire | `orleans-replica-write`, `orleans-replica-read`, `orleans-replica-tail` | None: each case's encoding and refused surrogates, which the targets build |

The `archive-log-token`, `archive-log-chunk-base`, `archive-log-chunk`, and `archive-log-metadata` targets, in
`test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.Archive.Logs`, are seeded from `archive-logs/tokens.json`, `chunk-bases.json`,
`chunks.json`, `metadata.json`, and `metadata-rejections.json`.

## 7. Rules for format changes

- Each format has exactly one current version. A change replaces the format in place, together with its encoder,
  decoder, golden vectors, fuzz target, and this registry ([conventions](conventions.md#10-one-current-representation)).
- Decoders accept only the current version of each format.
- A change to a format's rules updates, together, the table of reason codes in its owning section, the decoder, the
  rejection vectors, and the fuzz oracle.
- A format's first encoder, decoder, golden vectors, and fuzz target land together, with its byte layout in its owning
  section, which may give the layout earlier, marked *Not yet in code*.
- Canonical formats (`CLNR`, the ledger map, batches, the manifest, the LedgerStore catalog checkpoint, archive-log
  tokens, the store record) require their canonical encoding. Run objects have one writer encoding, which the vectors
  pin, and a decoder which accepts any layout meeting archive §7's rules: block splits and filter sizes are the
  writer's. Slot-index leaves have a canonical encoder and a structurally validating decoder; the decoder accepts some
  alternative width/predictor encodings. Exact encoder vectors establish encoder bytes rather than canonical-only
  decoder rejection.
- A derived format (slot-index and catalog checkpoints, the journal index, the block
  cache) may change by discarding and rebuilding its files; an authoritative format (WAL files, the LedgerStore's
  WAL records and node file, the ledger map, node registrations, batches, run objects, the manifest, archive logs and
  their tokens, the store record, drain markers) changes together with an explicit decision and rationale in its owning
  spec section. The entry record, never stored, changes with its encoder, decoder, and vectors.
