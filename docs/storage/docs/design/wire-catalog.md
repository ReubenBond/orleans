# Wire catalog

Every message and durable format in Roux, with behavioral notes. The layer design documents are authoritative:

| Layer | Document | Scope |
| --- | --- | --- |
| L1 Messaging | [Messaging](messaging.md), [Connectivity](connectivity.md) | `roux.wire.v1` frames, handshake, `Service \| Object` targets |
| L2 Storage (Clog + `roux-storage`) | [Storage](storage.md) | `roux.storage.v1` targets 96–100, WAL (`CLFL`), LSM keys, protobuf metadata records (`CLLM`, `CLNR`, `CLSC`) |
| L3 Membership | [Membership](membership.md) | `roux.membership.v1` targets 16–17, `StoredView`, Rapid alerts and consensus |
| L4 Runtime | [Runtime](runtime.md) | `roux.runtime.v1` `GrainId`, `GrainRecord`, durable state commands/snapshots |

Layer names follow the 5-layer Roux-on-Clog stack ([Architecture](architecture.md), [Layers](layers.md)).

<a id="part-4-proposal-sum-types-on-the-wire"></a>

## Conventions

These rules apply to every message and durable record ([decisions](decisions.md): Sum types on the wire):

1. **Sum types.** Alternatives that carry data are Rust enums, encoded as a message with one `oneof`. Closed sets with no data are C-like enums, where 0 is invalid.
2. **No dependent fields.** No field's meaning depends on another field's value, and nothing derivable is stored.
3. **Required fields.** A field that may be absent is an `Option`. Every other field is required, and its absence is a decode error. No zero means "none" or "any".
4. **Unknown fields & variants:**
   - **Unknown protobuf fields** are dropped everywhere by `prost` (`Z7`), except membership `StoredView`/`Entry` records where `roux-membership`'s `reject_unknown_wire_tags` enforces fail-stop (`M7`) and canonical `GrainKey` where `roux_id::decode_canonical` checks exact round-trip bytes (`Z24`).
   - **Unknown variants / enum 0** fail `TryFrom` domain conversion (`Z22`). Protocol variants are gated by negotiated `protocol_version`; an unknown method is answered `NotStarted::Incompatible`.
5. **Methods travel in the header.** A target's request type is a Rust enum whose discriminant is the method ID. On the wire, that ID is `Target`'s `method`, and the variant's message is the frame's `Call.args` payload. Method IDs are any `u32` from 1.
6. **Wrappers.** Nested enums and structs keep their own messages, at about 2 bytes per level. No type is flattened.
7. **Memory.** An enum is as large as its largest variant. Rare, large variants are boxed, and detail text is `Box<str>`.
8. **Encoding.** Standard Protocol Buffers via `prost` + `protox` + `prost-build` directly (pure Rust, no `protoc`, no `roux-codec` wrapper crate), with strict Rust domain types via `TryFrom`. Durations are `_us` integers; 128-bit IDs are `Id128`, exactly 16 bytes.

## Part 1. Messaging

### Frame

**Decided (user): one message per frame.** A connection is a standard length-delimited Protocol Buffers stream:

```text
varint  length   1 ..= 32 MiB + 64 KiB
bytes   Frame    one roux.wire.v1.Frame (the handshake's Hello and HelloResult come first, in their own types)
```

- **Limits (N16).** The node-wide payload limit is 32 MiB (frames at most 32 MiB + 64 KiB, matching Clog's 32 MiB replica batch defaults). Every field except the payload counts against a 64 KiB header budget during decoding (Z11). A sender fails an oversized message with `NotSent`.
- **No checksum.** TLS (`rustls`) authenticates encrypted connections, and TCP checksums plaintext ones.
- **Buffers (`bytes::Bytes`).** `roux-net` receives frames into standard `bytes::Bytes` buffers (via `tokio::net` TCP + `tokio-rustls` or in-memory/`turmoil` test streams) and passes `Bytes` directly across layer boundaries without wrapper crates. `io_uring` provided-buffer rings, incremental consumption, `SEND_ZC`, `kTLS`, UDS, and head-first streaming admission (`N29`) are deferred past v1.
### Handshake

Optional TLS (`rustls`) comes first, then one round trip. Each side first sends a preamble: `"ROUX"`, `min_version u16 LE`, `max_version u16 LE`.

```rust
/// Dialer → acceptor: the first message on a connection.
struct Hello {
    cluster_id: ClusterId,
    peer: Peer,
    /// The instance the dialer expects; absent when dialing a configured address.
    target: Option<InstanceId>,
}
enum Peer {
    /// A Roux process; its InstanceId is chosen at process start.
    Instance(InstanceId),
    // Tag 3 reserved for deferred external ClientHello (N21).
}

/// Acceptor → dialer.
enum HelloResult {
    Accepted { protocol_version: u16, acceptor: InstanceId },
    /// This peer is read on another thread.
    WrongThread { redirect: String },
    Rejected(HelloRejection),
}
enum HelloRejection { WrongTarget = 1, WrongCluster = 2, Incompatible = 3, Unauthorized = 4 }
```

**Behavior:**
- **Acceptor checks.** The acceptor checks the cluster, the protocol version, `target`, and the peer's TLS certificate (it must chain to the cluster's roots). It then answers at once; membership plays no part.
- **Deadline.** A 5-second deadline covers the whole handshake, and messages flow only after `Accepted`.
- **Connection roles.** The dialer sends requests and one-way messages. The acceptor sends only responses, on the same connection.
- **Connection count.** Each process dials each peer process once, so a node pair has two connections.
- **Connection identity.** Connections are keyed by the peer's `InstanceId` and dialed by address. When a peer opens a second connection, the newest wins.
- **Threads.** An acceptor reads all of a peer's connections on thread `instance(peer) mod T`. A dialer that knows the peer's `NodeEndpoints` dials that thread directly. Dialing a configured address may cost one `WrongThread` redirect.
- **Writer.** One owner thread writes each outbound connection. The reading thread forwards each request to its `dest_thread`.

### Frame contents

```rust
/// Every message after the handshake.
enum Frame {
    Request(Request),
    OneWay(Call),
    Response(Response),
}

struct Request {
    id: u64,                       // tag 1; per-connection counter, varint (about 3 bytes)
    deadline: Option<Duration>,    // tag 2; remaining time (precedes call so payload is last)
    call: Call,                    // tag 3
}

struct Call {
    dest_thread: ThreadId,
    target: Target,
    context: PropagatedContext,
    args: Payload,                 // last field; method's argument message
}

/// One wire format; in memory, received `bytes::Bytes` view or a value still to encode.
enum Payload { Encoded(Bytes), Value(Box<dyn EncodePayload>) }

/// Decided in [Identities](identities.md): messaging never interprets keys or hints.
enum Target {
    /// The handler on the process at the other end of the connection.
    Service { interface: InterfaceId, method: MethodId },
    /// An object at whichever process owns its key. For grains, `key` is the canonical
    /// `GrainId`, mapped via a pluggable key router to `RowKey = RoutingKey || JournalKey`
    /// (default `xxhash32(key, 0) || key`) and routed through Clog `LedgerMapView`.
    Object { key: Bytes, hint: Option<u64>, interface: InterfaceId, method: MethodId },
}

struct Response { id: u64, outcome: Outcome }

enum Outcome {
    Ok(Payload),                   // the turn ran; T
    Err(Payload),                  // the turn ran; E
    NotStarted(NotStarted),
    Unknown(UnknownOutcome),
}

enum NotStarted {
    Overloaded { retry_after: Duration },
    /// `version` is opaque to messaging; for grains it carries the Clog `LedgerMap` version.
    NotHere { location: Option<Location>, version: u64 },   // Location { member, hint }
    Unavailable,
    Incompatible,                  // unknown interface or method, or not callable by this peer
    ActivationFailed { detail: Box<str> },
}

enum UnknownOutcome { DeadlineExceeded, Cancelled, Aborted { detail: Box<str> } }

struct PropagatedContext { trace: Option<TraceContext>, principal: Option<String> }   // ≤ 4 KiB
struct TraceContext { trace_id: Id128, parent_span_id: u64, flags: u32, trace_state: String }  // W3C
```

**Behavior:**
- **Retries.** `CallError::ran()` maps directly from `Outcome`: `Ok` and `Err` mean yes, `NotStarted` no, `Unknown` unknown.
- **Resending.** The caller resends only after `NotHere`, `Unavailable`, or a local `NotSent`, within its deadline. After `NotHere` it resends to `location`, or once its `LedgerMapView` reaches `version`, within its own resend cap. Nodes never forward a grain request.
- **Order.** Messages from one sender process to one destination thread, on one lane, are delivered in order and at most once, though some may be lost.
- **Lanes.** High carries membership traffic, ahead of everything else and exempt from the byte bound. Normal carries everything else (grains and Clog storage 96–100).
- **Limits.** A 32 MiB payload limit and 32 MiB queue bound per lane per connection (N16). Waiting for room in a full send queue ends with `NotSent` at the call's deadline.
- **Closing.** `close(instance)` fails a connection's pending calls with an unknown outcome. The runtime calls it when membership reports that a peer is defunct.
- **Request context.** Each handler receives the peer's `InstanceId` and TLS key hash.

### Identities and endpoints

```rust
// roux.id.v1. NodeId: Id128 (Clog UUID in wal/node) · ProcessId u64 (its Join's version)
// ThreadId u32 · MembershipVersion u64 · ClusterId, InstanceId: Id128 · LedgerId u64 (>= 1)
struct MemberId { node: NodeId, process: ProcessId }
struct GrainId { grain_type: u32, key: Bytes }     // canonical encoding; ≤ 1 KiB
struct GrainAddress { grain: GrainId, member: MemberId, activation: ActivationId, thread: ThreadId }

// roux.wire.v1, stored by membership in Join and LiveMember
struct NodeEndpoints { transports: Vec<TransportEndpoints> }
struct TransportEndpoints { transport: String, roles: u32, base: String, threads: Vec<String> }
// roles: bit 1 = node-to-node (bit 2 reserved for deferred gateway role). threads[i] completes base for thread i;
// threads.len() is the node's thread count.
```

A pluggable `KeyRouter::route(&GrainId) -> RoutedKey { routing_key, journal_key }` maps each grain's canonical `GrainId` to `RowKey = routing_key || journal_key` (default `HashRouter`: `xxhash32(key, 0) || key`) and routes it through `LedgerMapView.find(row_key)` to its open ledger's primary `NodeId`; the target thread on the primary is `mix(xxhash32(row_key, 0)) mod threads` (hashing the full `RowKey` so prefix-sharing custom routers still distribute across threads).

## Part 2. System targets

| ID | Target | Package | Owner | Lane | Clients | Schemas |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | — | | Messaging | | | Invalid |
| 1–15 | Connection control | `roux.wire.v1` | Messaging | | | None assigned |
| 16 | `Membership` | `roux.membership.v1` | Membership | High | No | Specified ([Membership](membership.md)) |
| 17 | `Join` | `roux.membership.v1` | Membership | High | No | Specified ([Membership](membership.md)) |
| 18 | `Gateway` *(reserved/deferred)* | `roux.membership.v1` | Membership | Normal | Deferred | Deferred past v1 |
| 32–63 | Reserved | — | — | — | — | Retired (`Balancer` 32, `Journals` 48 replaced by Clog) |
| 64 | `CallControl` *(reserved/deferred)* | `roux.runtime.v1` | Runtime | Normal | — | Deferred past v1 (remote cancel) |
| 96 | `MetaStoreCasPaxos` | `roux.storage.v1` | Clog (`roux-storage`) | Normal | No | Specified ([Storage](storage.md)) |
| 97 | `LedgerReplica` | `roux.storage.v1` | Clog (`roux-storage`) | Normal (replica budget) | No | Specified ([Storage](storage.md)) |
| 98 | `JournalForward` | `roux.storage.v1` | Clog (`roux-storage`) | Normal (forward read/write budgets) | No | Specified ([Storage](storage.md)) |
| 99 | `ClusterControl` | `roux.storage.v1` | Clog (`roux-storage`) | Normal | No | Specified ([Storage](storage.md)) |
| 100 | `NodeControl` | `roux.storage.v1` | Clog (`roux-storage`) | Normal | No | Specified ([Storage](storage.md)) |
| ≥ 1,024 | Grain interfaces | application | Runtime | Normal | No | Generated `contracts.proto` |

Each layer's unused IDs in its range are reserved: Membership 18–31, Runtime 64–95, Clog Storage 101–127.

### Membership (16–17)

Wire schemas are specified as Protocol Buffers (`roux.membership.v1`) in [Membership](membership.md):
- **Membership (16):** `Probe`, `Alerts`, `Unresponsive`, `ForwardJoin`, `FastVote`, `Phase1a`/`1b`/`2a`/`2b`, and `Durable`.
- **Join (17):** `Join(JoinRequest { instance, node, endpoints, zone?, format_version, tls_key_sha256 }) -> Joined { member } | JoinRefused { Incompatible | ClusterFull }`. Accepts any authenticated process. (`ViewTransfer` is removed; catch-up reads `{cluster:N}/membership` directly.)
- **Gateway (18):** Reserved and deferred past v1 alongside external clients.

**Decided behavior:**
- Every Rapid message carries its configuration version.
- Members count alerts and votes only from senders their view resolves to members of that configuration.

**Timing:** probes every 1 s with a 500 ms timeout, failure after 3 missed probes, K = 10 observers, H = 9, L = 3.

### Runtime (64, reserved/deferred)

Remote request cancellation (`CallControl::Cancel`, Target 64) is deferred past v1; callers rely on RPC deadlines (`R23`). The inbox delivery interface is designed with the inbox and outbox.

### Clog Storage (96–100)

`roux-storage` implements Clog's inter-node RPCs as `Service` targets over Roux Messaging (`roux-net`), sharing `bytes::Bytes` payloads and enforcing per-lane FIFO `CallBudget` admission (`storage.md §6`):

- **`MetaStoreCasPaxos` (96):** Fast CASPaxos acceptor RPCs (`clog-caspaxos`) for disk-native MetaStore deployments (`ledger-map`, `node-registry`, membership views).
- **`LedgerReplica` (97):** `LedgerReplicaTransport` calls (`Write` returning piggybacked `lease_ms`, `Read`, `ReadSlice` for single-slot byte ranges, `ReadRange` for batched consecutive multi-slot reads up to 128 slots / 1 MiB packed as `[u32 LE len][bytes]...`, `ReadSlots` for batched scattered-slot indexed recovery waves up to 1 MiB packed as `[u64 LE slot][u32 LE len][bytes]...`, `ReadTail` including `seal` returning `lease_remaining_ms`, and batched idle `Heartbeat`) targeting `(NodeId, LedgerId)` on a node's local `LedgerStore` in `clog-wal`, gated by `MaxReplicaCallBytes` / `MaxNodeCallBytes` across three per-node replica lanes (`Write`, `Read`, and `Control` for `ReadTail` and `Heartbeat`, `ST8`).
- **`JournalForward` (98):** Forwarded journal writes and reads from non-primary nodes to the primary of the key's open ledger, with separate FIFO budget lanes (`MaxForwardCallBytes` for writes; `MaxForwardedReadBytes` / `MaxForwardReadCalls` for reads).
- **`ClusterControl` (99):** Node registration (`RegisterNode`), `ledger-map` / `node-registry` committer submissions, and node reports to the `ClusterController` singleton service.
- **`NodeControl` (100):** Transition execution, cooperative `PrepareRelease` handoff drain, compaction, and balance commands from `ClusterController` to per-node `NodeController`s.

Concrete `roux.storage.v1` Protocol Buffers schemas live in [Storage](storage.md).

## Part 3. Durable formats

### Clog storage formats (L2)

Clog (`clog-*`) owns all durable storage formats under `{cluster:N}/` in `clog-metastore`, local NVMe (`clog-wal` `CLFL`, `CLSC`/`CLSI`, and `clog-journals`'s embedded WAL-less `lsm-tree` warm index), and the concrete SlateDB cold archive (`clog-archive`):
- **MetaStore registers (`clog-metastore`):** `{cluster:N}/ledger-map` (`CLLM` v1, carrying scalar `archive_watermark: u64` for `HI-I2`; zero `CLLM` changes for atomic messages) and `{cluster:N}/node-registry` (`CLNR`, recording `(NodeId, ProcessId)` generation; endpoints live in Rapid `View`). SlateDB manages its own object-store manifest under the cluster's archive prefix and records `message-channels`, `max-messages`, and `max-message-bytes` in `0x00meta/store` (`ArchiveStoreRecord`).
- **Ledgers & unversioned atomic messages (`clog-ledgers`, `ST12`):** `CLLB` v1 batches carry `RowKind` (`Append = 0`, `Replace = 1`, `Delete = 2`, **`Message = 3`**) in the existing 2-bit `KindMask` (zero `CLLB` header changes). `RowKind::Message` rows use a self-describing unversioned row layout: `flags (0x03, DefiningFlag = 0) || channel_len (u8, 1..=64) || channel_bytes (ASCII [a-z0-9._-]) || body_len (varint) || body_bytes`, consuming zero `LedgerSegmentDictionary` or `lsm-tree` index entries and excluded from SlateDB SSTs and `ArchiveReader` folds (`LG-I15`).
- **Journals (`clog-journals`, `JS-I5`, `JS-I11`):** `RowKey = routing_key || journal_key` (pluggable via `KeyRouter`; default `HashRouter` = `xxhash32(key, 0) || key`, opaque to Clog), `JournalEntryRecord` (`Append`, `Replace`, `Delete`; bit 7 `0x80` = `HasMessagesFlag` on the kind/flags byte, rejected on `Expired`, carrying head-before-payload `message_count: u8 [1..=255]` and `(channel_len: u8 [1..=64], channel_bytes, payload_len: varint [0..=65_536], payload_bytes)` entries under the same writer-computed CRC-32C), opaque `JournalVersion(Base, Count)`.
- **Normative specification:** [Storage](storage.md) and Clog specs in [`docs/design/clog/`](clog/README.md).

### Membership records (`clog-metastore`, L3)

Membership persists committed Rapid views in a single `clog-metastore` register, `{cluster:N}/membership` (`M36`). Its value is a `StoredView`: the full newest view plus the entry that produced it. Each view transition is one conditional write naming view `V − 1`'s register version:
- A process one view behind applies the stored entry; one further behind resyncs by loading `StoredView` (running the `C6` self-death check) and deriving net status changes.
- Membership retains 0 defunct records in `StoredView`; `ClusterConfig.max_members` bounds view size at genesis (joins beyond it return `ClusterFull`).
- Membership extensions (`C8`, old Ext 1 range map and old Ext 2 log registry), per-extension format versions (`M38`), and process manifests (cut from v1) are omitted; Clog owns `ledger-map` and `node-registry`.

```proto
message StoredView {
  uint32 format_version = 1; uint64 version = 2; ClusterConfig config = 3;
  repeated LiveMember live = 4; Entry entry = 5;
}
message LiveMember {
  roux.id.v1.MemberId id = 1; LiveStatus status = 2; uint64 since = 3; roux.id.v1.Id128 instance = 4;
  roux.wire.v1.NodeEndpoints endpoints = 5; optional string zone = 6; uint32 format_version = 7;
  bytes tls_key_sha256 = 8; optional uint64 last_batch = 9;
}
message ClusterConfig {
  uint32 format_version = 1; uint32 observers = 2; uint32 high_watermark = 3; uint32 low_watermark = 4;
  uint32 probe_interval_us = 5; uint32 probe_timeout_us = 6; uint32 failed_probes = 7;
  uint32 max_join_time_us = 9; uint32 max_members = 11; // tags 8, 10 reserved
}
// LiveStatus: JOINING = 1, ACTIVE = 2, SHUTTING_DOWN = 3
```

Full schemas for `Entry`, `Batch`, and targets 16–17 live in [Membership](membership.md#formats).

- **Commit point.** A view is published only once durable in `{cluster:N}/membership`.
- **Unknown fields** are rejected on membership records via `roux-membership`'s `reject_unknown_wire_tags` before `prost::Message::decode` (`M7`), so readers stop rather than misapply a newer format.
- **Format versions.** Membership gates its own record format via `ClusterConfig.format_version`; other layers use N−1/N+1 protobuf compatibility and protocol version negotiation.

### Grain entries and snapshots (Runtime, L4, inside Clog journal bodies)

```rust
struct GrainEntry { commands: Vec<StateCommand> }              // one flush, in apply order
struct StateCommand { state: StateId, command: Bytes }         // the state machine's own command enum
struct GrainSnapshot { states: Vec<StateSnapshot> }
struct StateSnapshot { state: StateId, snapshot: Bytes }
```

- **Recovery.** Fails on an unknown `StateId` or command variant, because skipping one would lose a state change. Unknown snapshot sections also fail recovery (rolling upgrades deploy readers before writers, `R28`).

### Atomic reminder messages (`roux-jobs`, L4, payload of `"roux.reminder"` `RowKind::Message` rows, `R37`)

- **`ReminderMessageCodec` / `ReminderRecord` (`roux-jobs`):** Canonical v1 little-endian payload carried by L2's general-purpose `RowKind::Message = 3` rows on channel `"roux.reminder"`: `version: u8 = 1`, `flags: u8` (`0x01 = HasDueTime`, bits `1..=7 = 0`), varint-prefixed canonical `TargetGrainId` (`1..=1024`), varint-prefixed non-blank UTF-8 `JobName` (`1..=256`), and optional `DueTimeUtcTicks: i64` LE (`0..=MAX_UTC_TICKS`, present iff `HasDueTime`).
- **Deterministic sealed `JobShard` ID:** Surviving reminders folded per closed segment by `ReminderMessageProcessor` publish one deterministic sealed `JobShard` per time bucket named `drain-{ledger_id:020}-{segment_start:020}-{bucket_utc_ticks:019}` via `CreateIfNotExists` ([Runtime](runtime.md#atomic-journal-reminders-and-grain-local-durable-jobs-r37)).

## Part 4. Sum types: costs

- **Wire.** Protobuf never writes an absent field, so a sum type saves bytes where its tag replaces a discriminant, such as `kind` or `code`, and costs about 2 bytes per wrapper level.
- **Memory.** An enum is as large as its largest variant plus a tag (`Header` ~160 bytes vs ~300 bytes flat; `Outcome` with boxed location < 80 bytes).
- **Codec.** Decoding reads into per-field locals and constructs the value at the end, so a missing required field is a decode error rather than a default.
- **Other languages.** proto3 cannot mark a field required, so encoders in other languages must set every required field; Roux rejects frames that omit one.

## Findings

Status after the Roux-on-Clog alignment. Open items first.

| # | Finding | Status | Proposal |
| --- | --- | --- | --- |
| 1 | Membership (16–17) and `roux-storage` Clog RPCs (96–100) protobuf schemas. | Resolved | Specified in [Membership](membership.md) and [Storage](storage.md). |
| 10 | `TransportEndpoints.threads` lists a port per thread, so each thread can accept the peers it reads. | Open, low | Keep it; revisit with Messaging's benchmarks. |
| 11 | The `Cancel` path (`R23`, Target 64). | Deferred | Deferred past v1; callers rely on deadlines. |
| 7 | Sentinel zeros. | Partly resolved | `activation` is an `Option`. Membership's `Batch.seq = 0` goes when Membership applies the planned `Batch` enum. |
| 2 | Storage RPC transport. | Resolved | `roux-storage` exposes Clog RPCs as `Service` targets 96–100 over shared L1 connections. |
| 3 | Old L5 48-byte journal frame overlap. | Superseded | Retired by Clog's own storage formats (`CLFL`, `CLSC`, `clog-journals`). |
| 4 | `message_id` was a 64-bit per-thread random XOR counter (N10). | Resolved (user) | A per-connection varint counter, about 3 bytes. |
| 5 | `hops` and `resends`. | Resolved (user) | Dropped. |
| 6 | `dest_thread` applies only to requests. | Resolved | It lives in `Call`. |
| 8 | Time units in old L5 `wake_at`. | Superseded | `_us` for durations and `_unix_us` for absolute times everywhere; `wake_at` removed with Durable Jobs above Clog. |
| 9 | Old `LoadReport` field order and `float`. | Superseded | `Balancer` (32) retired in favor of Clog `ClusterController`. |
