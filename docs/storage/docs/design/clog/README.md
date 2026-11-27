# Clog Storage & Journaling Specification (Roux Adaptation)

This directory contains the stand-alone Clog system model, invariant registry, and outcome/error semantics imported for Roux's storage stack (`clog-*` crates).

## Files in this directory

| Document | Contents |
| --- | --- |
| [`system-model.md`](system-model.md) | System and fault model, clocks, tolerated failures, local/object storage guarantees, and safety/liveness boundaries. |
| [`invariants.md`](invariants.md) | Normative invariant registry (`BUF-I*`, `WL-I*`, `LR-I*`, `LS-I*`, `LP-I*`, `MS-I*`, `LG-I*`, `HI-I*`, `JS-I*`). |
| [`outcomes.md`](outcomes.md) | Operation outcomes, ambiguity/fencing rules, read semantics, error taxonomy, and cancellation contracts. |

## Roux-specific mapping and exclusions

Roux aligns **algorithmically** with Clog (`ST11` in [`../storage.md`](../storage.md)), implemented natively in pure Rust with protobuf/`prost` formats and the following Roux-specific architecture decisions (see [`../storage.md`](../storage.md) and [`../decisions.md`](../decisions.md)):

| Clog spec concept | Roux concrete realization |
| --- | --- |
| Buffers (`BUF-I*`) | `bytes::Bytes` / `bytes::BytesMut` (no custom `ArcBuffer` or C# `Memory<byte>` pool). |
| WAL (`WL-I*`) | `clog-wal` using direct Linux `io-uring` (plus `turmoil-io-uring` in simulation and `pread`/`pwrite` fallback on non-Linux dev hosts). |
| Ledger replicas & LedgerStore (`LR-I*`, `LS-I*`) | `clog-ledgerstore` backed directly by WAL-less `lsm-tree` v3 (not RocksDB, no pluggable `IStateDatabase` abstraction). Replayed from `clog-wal` on startup. |
| Ledger protocol (`LP-I*`) | `clog-ledger-protocol` over L1 Messaging Targets `64`–`79`. Formally checked in [`../../../specs/ledger-protocol/`](../../../specs/ledger-protocol/README.md). |
| MetaStore (`MS-I*`) | `clog-metastore`: `ObjectStoreMetaStore` (via Apache `object_store` conditional writes) and `clog-caspaxos` ([`../fast-caspaxos.md`](../fast-caspaxos.md), formally checked in [`../../../specs/fast-caspaxos/`](../../../specs/fast-caspaxos/README.md)). |
| Ledgers (`LG-I*`) | `clog-ledgers` keyspace partitioning, splits/merges/takeovers, unversioned `RowKind::Message = 3` + `MessageChannel` (`LG-I15`), segment-ordered message drain (`LG-I16`), and drops. Formally checked in [`../../../specs/ledgers/`](../../../specs/ledgers/README.md) and [`../../../specs/message-drain/`](../../../specs/message-drain/README.md). |
| Archive (`HI-I*`) | `clog-archive` backed by **SlateDB** (`slatedb::Db` single-writer fenced drain + single-pass `DrainScan` `SegmentMessageFolds` + `slatedb::DbReader` with `subscribe()`/reopen refresh; `ST2`, `ST3`, `ST12`). Formally checked in [`../../../specs/archive/`](../../../specs/archive/README.md) and [`../../../specs/message-drain/`](../../../specs/message-drain/README.md). |
| Journals (`JS-I*`) | `clog-journals` (`JournalEntryRecord` with `HasMessagesFlag = 0x80`, atomic messages `JS-I11`, slot-ordered `MessageSession` `JS-I12`, WAL-less `lsm-tree` index with `64 MiB` memtable / `30 s` periodic / clean-shutdown flushes skipping `!is_archive_kind`) + `roux-runtime` (`DurableValue`, `DurableMap`, `DurableList`, `DurableQueue`, `DurableSet`, `Inbox`, `Outbox`, `DurableJobQueue`). |

**Upstream Clog sections NOT used in Roux** (present in the imported reference files for cross-reference continuity only):
- **ObjectLedger** (`ObjectLedgerStore` / diskless object-only ledger mode) — deferred (`S1`).
- **Custom Archive LSM (`LSM-I*`) and Archive Logs (`AL-I*`)** — replaced by SlateDB (`C1`, `ST2`, `ST3`).
- **Generic State Database (`SD-I*` / RocksDB)** — replaced by dedicated WAL-less `lsm-tree` v3 inside `clog-ledgerstore`.
- **Orleans C# journaling (`OR-I*`)** — replaced by Roux's Rust L4 `roux-grain-runtime` (`docs/design/grain-runtime.md`, `docs/design/state.md`).
