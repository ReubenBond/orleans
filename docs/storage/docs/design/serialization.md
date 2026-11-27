# Serialization

Roux encodes RPC messages, message headers, grain journal entries, snapshots, and membership records as [Protocol Buffers](https://protobuf.dev/) using [`prost`](https://github.com/tokio-rs/prost) and [`protox`](https://github.com/andrewhickman/protox) (pure Rust, no `protoc` binary) over standard [`bytes::Bytes`](https://docs.rs/bytes). There is no separate `roux-codec` crate: each crate uses `prost` directly, L1 framing helpers (`Payload`, `Limits`) live in `roux-net` (`wire` module, [Messaging](messaging.md#n6-internal-layering)), canonical key checks live in `roux-id`, and membership's wire-tag verifier lives in `roux-membership`. Every service that talks over messaging uses it, including Clog's storage RPCs through `roux-storage`, and Rapid; durable values are Protocol Buffers messages that Clog stores as opaque bodies in its journals and MetaStore, inside Clog's own formats.

Status: every decision below is a working choice, revised for [Roux on Clog](architecture.md) and idiomatic Rust (`prost` + `protox` directly with no `roux-codec` crate, standard `bytes::Bytes`, dropped unknown fields). Each record states the decision, the problem, the alternatives, why it was chosen, what would change it, its complexity, and what undoing it takes. Statuses are **Decided**, **Proposed**, **Deferred**, and **Withdrawn**. [Review notes](#review-notes) lists what remains worth simplifying.

## Summary

**Contract.** Each Roux crate uses `prost` directly to translate between Rust domain values and the standard Protocol Buffers binary encoding. Values round-trip. Domain conversion boundaries (`TryFrom`) ensure every required field is present, every `oneof` is populated, and every closed enum discriminant is valid, so decoded domain values are always fully formed. Bytes written under one schema version decode under any compatible version. Unknown fields are dropped by `prost` everywhere; membership's stored view and consensus entries additionally run a small wire-tag verifier inside `roux-membership` before decoding so older nodes fail stop on unknown tags ([M7](membership.md#m7-formats)). Encodings of types without hash maps are deterministic. Decoding untrusted bytes returns a value or an error within configured resource limits, and never panics. See [Contract](#contract).

**Main decisions.**

| ID | Decision | Status |
| --- | --- | --- |
| [Z1](#z1-format-family) | Standard Protocol Buffers binary encoding | Decided |
| [Z15](#z15-schema-source) | Internal Roux schemas are `.proto` files compiled with `protox` + `prost-build` (no `protoc`); user grain types use `#[derive(prost::Message)]` with grain macros emitting `contracts.proto` | Decided |
| [Z17](#z17-implementation) | Use `prost` + `protox` directly in safe Rust; no `roux-codec` crate, no custom protobuf runtime, and no `protoc` binary | Decided |
| [Z7](#z7-unknown-fields), [Z18](#z18-unknown-field-storage) | Drop unknown fields everywhere via `prost` (`Z18` withdrawn); rolling journal upgrades deploy readers before writers (`S4`), and membership protects its stored register with `ClusterConfig.format_version` + a wire-tag check (`M7`) | Decided |
| [Z20](#z20-protobuf-names) | Every internal message has a `.proto` full name emitted into `prost::Name` | Decided |
| [N5](#n5-polymorphism), [Z16](#z16-generics) | Closed polymorphism through enums (`oneof`); no open `Any` in v1; no generic message types | Decided |
| [Z22](#z22-sum-types-and-required-fields) | Domain types use Rust enums and required fields; thin `TryFrom<Wire> for Domain` boundaries validate `prost`'s generated wire structs | Decided |
| [Z8](#z8-codec-traits), [Z9](#z9-encoding-strategy), [Z19](#z19-type-mapping) | `prost::Message` over `bytes::Bytes` / `bytes::BytesMut` (zero-copy slicing via `#[prost(bytes = "bytes")]`) with standard two-pass `prost` encoding | Decided |
| [Z11](#z11-decoding-limits) | Frame/record size limits, `prost` recursion depth limit, and a 64 KiB header budget | Decided |
| [Z23](#z23-payload) | A `Payload` is an encoded message kept as a `bytes::Bytes` view until its handler decodes it | Decided |
| [Z24](#z24-canonical-encodings) | Types used as identities have exactly one encoding, and `decode_canonical` rejects any other | Decided |
| [Z12](#z12-schema-checks) | `.proto` schemas (`schema/` and `contracts.proto`) are committed and checked with `buf lint` and `buf breaking --against` (`WIRE`) | Decided |
| [Z21](#z21-storage-envelope) | No L1 storage envelope; durable values live inside Clog's formats | Withdrawn |

**Complexity worth watching.** Maintaining thin `TryFrom` domain-to-wire conversions on Roux's own internal protocol structs ([Z22](#z22-sum-types-and-required-fields)) and keeping the membership wire-tag allowlist in sync with `roux.membership.v1` ([M7](membership.md#m7-formats)).

## Contract

- **Round trip.** `decode(encode(x)) == x` for every value of every message type; unknown fields from newer schemas are dropped on decode.
- **Fully formed domain values.** Every internal wire message validates at its `TryFrom<WireType> for DomainType` boundary: required fields are present, every `oneof` holds a known variant, and every closed enum discriminant is valid and non-zero.
- **Standard encoding.** Any conforming Protocol Buffers implementation decodes Roux's encodings, using the committed `.proto` files.
- **Version tolerance.** Under the [compatibility rules](#compatibility-rules), bytes written by one schema version decode under the other (`prost` skips unknown fields; membership's `StoredView` and `Entry` fail-stop on unexpected wire tags before decoding).
- **Determinism.** Encoding a value twice yields the same bytes, and `encode(decode(b)) == b` for bytes Roux encoded, for types without `HashMap` fields.
- **Total decoding.** Decoding returns a value or a `DecodeError` within its [limits](#z11-decoding-limits); it never panics or reads out of bounds.

## Wire format

The standard Protocol Buffers binary encoding via `prost`, unmodified ([Z1](#z1-format-family)):

| Aspect | Encoding |
| --- | --- |
| Field key | Varint of `(field_number << 3) \| wire_type` |
| Wire types | `0` varint, `1` fixed 64-bit, `2` length-delimited, `5` fixed 32-bit. Groups (`3`, `4`) are never written. |
| Varints | Little-endian base-128 (LEB128), at most 10 bytes |
| Strings | UTF-8, length-delimited, validated on decode |
| Nested messages | Length-delimited |
| Repeated scalars | Packed |
| Maps | Repeated entries of a key (field 1) and a value (field 2) |
| Enums with data | A message with one `oneof`; domain `TryFrom` rejects `None` |
| C-like enums | Protobuf `enum` with `<ENUM>_UNSPECIFIED = 0`, rejected at `TryFrom` |
| Presence | Explicit `optional` in `.proto` for required scalars whose zero value is distinct from absence, validated at `TryFrom` |
| Field order | `prost` writes fields in tag order. Keeping a frame's payload at the highest tag preserves forward compatibility if head-first streaming admission (`N29`) is added later |

## Compatibility rules

Standard Protocol Buffers `WIRE` compatibility rules, enforced in CI ([Z12](#z12-schema-checks)):

- Field numbers are never reused; a removed field's number and name are reserved in `.proto` (`reserved`).
- A field's type changes only between types Protocol Buffers lists as wire-compatible.
- A field added to a type that has data or peers in production is optional (`Option`, `Vec`, or map) or uses a proto3 default, so older bytes still decode.
- A required domain field is removed in two releases: readers accept it as optional first, and writers stop emitting it only after every reader has upgraded.
- Enum values and `oneof` variants are added, never renumbered. Protocol types gate new variants by the negotiated `protocol_version` ([N2](messaging.md#n2-handshake)); durable grain records deploy readers before writers (`S4`); membership gates new format versions via `ClusterConfig.format_version` ([M7](membership.md#m7-formats)).

## Schema authoring examples

Internal Roux schemas (`roux.id.v1`, `roux.wire.v1`, `roux.membership.v1`, `roux.storage.v1`, `roux.runtime.v1`) are `.proto` files compiled by `protox` + `prost-build` in `build.rs` with zero external binary dependencies, wrapped by thin domain `TryFrom` conversions. User grain types derive `prost::Message` directly:

```rust
// User RPC argument or durable grain command: standard prost::Message.
#[derive(Clone, PartialEq, prost::Message)]
pub struct Order {
    #[prost(uint64, tag = "1")]
    pub id: u64,
    #[prost(message, repeated, tag = "2")]
    pub lines: Vec<Line>,
    /// Zero-copy slice of the receive page via prost's bytes::Bytes support.
    #[prost(bytes = "bytes", tag = "3")]
    pub payload: bytes::Bytes,
}

// Canonical grain key: enforces deterministic encoding and rejects HashMap/floats.
#[derive(Clone, PartialEq, Eq, Hash, prost::Message, roux::GrainKey)]
pub struct CartKey {
    #[prost(string, tag = "1")]
    pub tenant: String,
    #[prost(uint64, tag = "2")]
    pub cart_id: u64,
}
```

## Decisions

### Z1. Format family

- **Status.** Decided.
- **Decision.** Standard Protocol Buffers binary encoding: LEB128 varints, explicit field numbers, `oneof` for enums with data, UTF-8 strings validated on decode, packed repeated scalars, and protobuf maps.
- **Problem.** One format for RPC, headers, and durable state that evolves safely, decodes untrusted input safely, and stays fast.
- **Alternatives.** A custom Orleans-style tagged format (no ecosystem tooling); MessagePack through serde (field-name evolution, no schema tooling); FlatBuffers (soundness history); Cap'n Proto (less widely used).
- **Why.** Explicit evolution rules, mature tooling (`buf`), implementations in every major language, and idiomatic Rust support via `prost` + `bytes::Bytes`.
- **Would change it.** Profiles showing decoding as a bottleneck that a zero-copy format would remove.
- **Complexity.** Values are trees: shared references and cycles are not representable.
- **To undo.** Replacing the format means migrating every durable format.

### Z15. Schema source

- **Status.** Decided; revised to drop custom `roux-codec` codegen and `protoc`.
- **Decision.** Two clean surfaces, both standard Protocol Buffers and zero `protoc`:
  - **Internal Roux schemas** (`roux.id.v1`, `roux.wire.v1`, `roux.membership.v1`, `roux.storage.v1`, `roux.runtime.v1`) are authoritative `.proto` files committed under `schema/`, compiled in `build.rs` using **`protox` + `prost-build`** (pure Rust, no external `protoc` binary). `prost` generates wire structs with `bytes::Bytes` fields (`Config::bytes(["."])`), and each layer converts wire structs to validated domain types via `TryFrom`.
  - **User grain types and interfaces** are idiomatic Rust structs/enums deriving `#[derive(prost::Message)]` (with `prost::Oneof` / `prost::Enumeration` as needed). `#[roux::grain_interface]` generates one `prost::Message` argument struct per method, and `cargo xtask schema` inspects registered interfaces to emit `contracts.proto` for `buf breaking` CI checks.
- **Problem.** Avoid maintaining a custom protobuf compiler/derive while keeping zero external `protoc` toolchain friction and ergonomic Rust grain APIs.
- **Alternatives.** Owning a custom Rust-first `#[derive(roux::Message)]` runtime and `.proto` emitter (the previous decision: 3,000–5,000 lines of custom codec + conformance maintenance); invoking external `protoc` in `build.rs` (platform/version drift in dev and CI).
- **Why.** `protox` parses `.proto` files in pure Rust and feeds `FileDescriptorSet` directly into `prost-build`, so `cargo build` works out of the box on every platform without installing `protoc`, while user grains stay pure Rust with `#[derive(prost::Message)]`.
- **Would change it.** Cross-language teams wanting `.proto`-first codegen for user grain interfaces as well (trivially supported since the wire format is standard protobuf).
- **Complexity.** One small `build.rs` invoking `protox::compile` + `prost_build::Config::compile_fds`, plus thin `TryFrom` conversions for internal domain types.
- **To undo.** Switching schema sources keeps the identical binary wire encoding.

### Z17. Implementation

- **Status.** Decided; revised from an owned `roux-codec` crate to using `prost` + `protox` directly.
- **Decision.** Use **`prost`** (safe Rust, mature, fast, native `bytes::Buf` and `bytes::Bytes` support) with **`protox` + `prost-build`** (pure Rust, no `protoc`), with **no separate `roux-codec` crate**. The few Roux-specific helpers live directly in the crate that owns them:
  - `Payload` and `Limits` live in `roux-net`'s `wire` module ([N6](messaging.md#n6-internal-layering)).
  - `decode_canonical` lives in `roux-id` alongside `GrainKey` ([Z24](#z24-canonical-encodings)).
  - Membership's ~40-line protobuf wire-tag verifier (`reject_unknown_wire_tags`) lives in `roux-membership` alongside `StoredView` and `ClusterConfig.format_version` ([M7](membership.md#m7-formats)).
- **Problem.** Implementing a custom protobuf wire engine—or maintaining an extra wrapper crate around `prost`—adds workspace indirection without creating an independent abstraction boundary.
- **Alternatives.** Owning a custom `roux-codec` engine (the previous decision, motivated by unknown-field preservation and rejecting unknown fields in-codec); keeping a thin `roux-codec` helper crate; Google's `protobuf` v4 (built on the upb C kernel).
- **Why.** Dropping unknown-field preservation (`Z7`) and handling membership's fail-stop check with a tiny wire-tag scanner eliminates the only reasons to fork or reimplement protobuf. `prost` already decodes directly from `bytes::Bytes` and slices `bytes::Bytes` zero-copy via `Buf::copy_to_bytes()`.
- **Would change it.** `prost` becoming unmaintained without a drop-in successor (pure-Rust protobuf wire decoding is standard, so swapping crates breaks no wire or stored data).
- **Complexity.** Minimal: thin helpers on top of standard ecosystem crates.
- **To undo.** Standard protobuf bytes; swapping implementations changes no stored or wire data.

### Z7. Unknown fields

- **Status.** Decided (user); revised to drop unknown fields everywhere.
- **Decision.** Unknown fields are **dropped** everywhere by `prost` during decoding.
  - **RPC messages:** dropped on decode. Forwarded requests pass through undecoded as `Payload` (`bytes::Bytes`), so gateways and proxies never drop unknown fields on transit.
  - **Grain journals:** journal records are append-only commands, and rolling upgrades that introduce a new record variant, `StateId`, or command field follow deploy-readers-before-writers (`S4`), so an older node never decodes and re-encodes a newer node's durable state.
  - **Membership (`M7`):** `{cluster:N}/membership` `StoredView` and `Entry` keep fail-stop protection against newer unknown fields through `ClusterConfig.format_version` plus `roux-membership`'s `reject_unknown_wire_tags` run over raw bytes before `prost::Message::decode`.
- **Problem.** Preserving unknown fields in every durable struct forced a custom protobuf implementation (`Z17`) and an explicit `unknown: UnknownFields` field on every user durable type (`Z18`).
- **Alternatives.** Preserving unknown fields in durable types via a custom codec (the previous decision).
- **Why.** Standard `prost` drops unknown fields cleanly, eliminates thousands of lines of custom codec code, keeps user structs idiomatic, and membership's fail-stop invariant is satisfied with a 40-line wire-tag verifier.
- **Would change it.** A concrete requirement to support out-of-order writer-before-reader rolling upgrades on durable state.
- **Complexity.** None in the codec; one 40-line wire-tag checker for membership.
- **To undo.** Additive if a future crate ever needs field preservation.

### Z18. Unknown-field storage

- **Status.** Withdrawn (superseded by [Z7](#z7-unknown-fields) dropping unknown fields via `prost`).

### Z20. Protobuf names

- **Status.** Decided.
- **Decision.** Every internal message has its explicit full name in its `.proto` package (`roux.id.v1.*`, `roux.wire.v1.*`, `roux.membership.v1.*`, `roux.storage.v1.*`, `roux.runtime.v1.*`), emitted into `prost::Name` by `prost-build` (`enable_type_names()`).
- **Problem.** Schema checks and diagnostics need names that survive Rust refactoring.
- **Alternatives.** Deriving names from `std::any::type_name` (breaks on module moves or renames).
- **Why.** Matches `prost`'s built-in `prost::Name` trait and Roux's stable identity rules.
- **Would change it.** Nothing foreseeable.
- **Complexity.** Built into `prost-build`.
- **To undo.** The wire encoding never carries names, so only schemas and diagnostics are affected.

### N5. Polymorphism

- **Status.** Decided.
- **Decision.** Polymorphism is closed: Rust enums (`prost::Oneof` on the wire) evolve under the normal rules. Grain methods return `Result<T, E>` with a concrete `E`. Open `prost_types::Any` polymorphism is omitted from v1; applications that need open payload envelopes can add `prost-types` directly in user code.
- **Problem.** Avoid speculative open-type machinery and deserialization type confusion when every internal Roux and Clog protocol uses closed sum types.
- **Alternatives.** Trait objects resolved through a link-time registry of aliases, as Orleans does; shipping `prost_types::Any` in Roux's v1 core API.
- **Why.** Closed enums are exhaustive, zero-overhead, and leave no untyped deserialization surface.
- **Would change it.** A concrete internal Roux requirement for open polymorphism.
- **Complexity.** None.
- **To undo.** Additive if ever needed.

### Z16. Generics

- **Status.** Decided.
- **Decision.** Message types are concrete: every wire and durable message has one concrete protobuf schema. The built-in containers (`Option`, `Vec`, maps, and `Result` in generated grain responses) are generic.
- **Problem.** `.proto` schemas have no generics.
- **Alternatives.** One descriptor per instantiation (`Page_Order`).
- **Why.** One concrete schema per type, directly supported by `prost`.
- **Would change it.** Many near-duplicate types in applications.
- **Complexity.** Some duplication in applications.
- **To undo.** Additive.

### Z8. Codec traits

- **Status.** Decided; simplified around `prost::Message` and `TryFrom` domain boundaries (no `roux-codec` crate).
- **Decision.** Roux uses `prost::Message` directly rather than defining parallel `Message` and `Field` traits or a `roux-codec` wrapper crate. Each crate owns only the tiny helpers specific to its boundary:

  - **`roux-net::wire`** provides `Payload`, `Limits`, and `decode_with_limits<M: prost::Message + Default>(bytes::Bytes, &Limits) -> Result<M, DecodeError>`.
  - **`roux-id`** provides `decode_canonical<M: prost::Message + Default>(&[u8]) -> Result<M, CanonicalDecodeError>` for canonical identity decoding ([Z24](#z24-canonical-encodings)).
  - **`roux-membership`** provides `reject_unknown_wire_tags(&[u8], &TagSchema, u32) -> Result<(), DecodeError>` for fail-stop decoding of `{cluster:N}/membership` ([M7](membership.md#m7-formats)).

  - **Zero-copy slicing:** `prost::Message::decode(buf)` decodes directly from `bytes::Bytes`. When `prost` decodes a `#[prost(bytes = "bytes")]` field, it calls `Buf::copy_to_bytes(len)`, returning a zero-copy `Bytes` slice of the frame buffer.
  - **Domain validation:** Internal Roux types convert `WireType -> DomainType` via `TryFrom`, checking required `Option` fields, non-zero closed enums, and `oneof` presence (`Missing` / `UnknownVariant`).
- **Problem.** Keep decoding fast, zero-copy, idiomatic Rust, and free of a custom protobuf runtime or unnecessary wrapper crate while still ensuring invalid internal protocol states cannot be constructed.
- **Alternatives.** Custom `Message`/`Field` traits and proc-macro (3,000–5,000 lines to maintain); a thin `roux-codec` helper crate (unnecessary indirection once `prost` is used directly).
- **Why.** `prost::Message` + `bytes::Bytes` already provides zero-copy slicing, varint decoding, and recursion-depth guards; `TryFrom` enforces Roux's strict domain invariants cleanly at each crate's wire boundary.
- **Would change it.** Profiles showing `TryFrom` conversion overhead on hot RPC headers (can be specialized for `roux-net::wire`'s `Frame` header without replacing `prost`).
- **Complexity.** Minimal; `TryFrom` impls for internal types and ~50 lines of localized helpers across `roux-net`, `roux-id`, and `roux-membership`.
- **To undo.** Internal to those crates.

### Z9. Encoding strategy

- **Status.** Decided; uses `prost`'s standard two-pass encoding into `bytes::BytesMut` / `bytes::Bytes`.
- **Decision.** `prost::Message::encoded_len()` computes a message's exact size, then `prost::Message::encode(&mut buf)` writes directly into a `bytes::BytesMut` sized to `encoded_len()`, which freezes into `bytes::Bytes` ([N14](messaging.md#n14-receive-and-send-buffers)).
  - Frames carrying a pre-encoded `Payload` (such as Clog replica batches or forwarded bodies) write the frame header `Bytes` and payload `Bytes` sequentially via vectored writes when `>= 4 KiB`, avoiding copying large payloads.
- **Problem.** Protobuf length-prefixes nested messages, and large payloads (forwarded bodies, Clog replica batches) should not be copied unnecessarily during framing.
- **Alternatives.** Custom per-thread page pools and `ChunkedBuf` chains (deferred past v1 until profiles show allocator cost); reverse single-pass encoding.
- **Why.** Uses `prost` and `bytes::BytesMut` unmodified with minimal code while keeping vectored sends for large payloads.
- **Would change it.** Profiles showing `BytesMut` allocation overhead at high message rates (per-thread pooled `Bytes::from_owner` pages can be added later without changing any API).
- **Complexity.** Minimal.
- **To undo.** Internal: no bytes change.

### Z19. Type mapping

- **Status.** Decided; aligns with standard `prost` + `protox` mappings.
- **Decision.** Standard Protocol Buffers types mapped through `prost` (with `Config::bytes(["."])` so every protobuf `bytes` field compiles to `bytes::Bytes`):

  | Rust | Protocol Buffers | Notes |
  | --- | --- | --- |
  | `bool` | `bool` | |
  | `u32`, `u64` | `uint32`, `uint64` | `fixed32` / `fixed64` where chosen in `.proto` or `#[prost(fixed64)]`. |
  | `i32`, `i64` | `sint32`, `sint64` (or `int32`/`int64`) | Prefer `sint32`/`sint64` for signed values that may be negative. |
  | `f32`, `f64` | `float`, `double` | Forbidden in canonical identity types ([Z24](#z24-canonical-encodings)). |
  | `String` | `string` | UTF-8 validated by `prost`. |
  | `bytes::Bytes` | `bytes` | Zero-copy slice of the receive buffer via `#[prost(bytes = "bytes")]` (`Config::bytes(["."])`). |
  | `Payload` | `bytes` | Wraps `bytes::Bytes` for frame bodies ([Z23](#z23-payload)). |
  | `Id128` (`[u8; 16]`) | `bytes` | Validated to exactly 16 bytes at `TryFrom`. |
  | `Duration` | `uint64` (`*_us`) | Microseconds, saturating. |
  | `SystemTime` | `sint64` (`*_unix_us`) | Microseconds since Unix epoch. |
  | `Option<T>` | `optional T` | Explicit presence; required internal fields are unwrapped in `TryFrom`. |
  | `Vec<T>` | `repeated T` | Packed for scalars. |
  | `BTreeMap<K, V>` | `map<K, V>` | Deterministic encoding (`prost` `Config::btree_map(["."])` for internal schemas). |
  | `HashMap<K, V>` | `map<K, V>` | Allowed on user RPC types; forbidden on canonical identity types. |
  | Enum with data | `message` with `oneof` | `prost` generates `Option<OneofEnum>`; internal `TryFrom` rejects `None`. |
  | C-like enum | `enum` | `<ENUM>_UNSPECIFIED = 0` rejected in `TryFrom`; `prost::Enumeration` maps `i32` via `try_from`. |

  **In-memory size.** Internal domain enums box rare large variants (`NotHere(Box<NotHere>)`) and use `Box<str>` for rarely extended error details. Boxing changes nothing on the wire.
- **Problem.** Standardize one idiomatic Rust-to-protobuf mapping compatible with `prost`, `bytes::Bytes`, and `buf lint`.
- **Alternatives.** Custom type-mapping table in an owned derive macro.
- **Why.** Matches standard `prost` conventions so Rust developers already know every attribute and generated type.
- **Would change it.** Nothing foreseeable.
- **Complexity.** Standard `prost` configuration (`bytes(["."])`, `btree_map(["."])`, `enable_type_names()`).
- **To undo.** A field's wire encoding is fixed once data exists.

### Z11. Decoding limits

- **Status.** Decided; adapted to `prost` and `bytes::Bytes`.
- **Decision.** Each decode takes a `Limits`:

  ```rust
  pub struct Limits {
      /// Maximum nesting of messages (matches prost's recursion limit). Default 100.
      pub max_depth: u32,
      /// Maximum total input bytes accepted before decoding.
      pub max_input_bytes: usize,
      /// Maximum bytes of a frame outside its `Payload` field. 64 KiB for frames.
      pub header_budget: usize,
  }
  ```

  - **Pre-decode size bounds:** Every frame length (`<= 32 MiB + 64 KiB`), header budget (`<= 64 KiB`), and Clog record size (`<= MaxBodyBytes`) is enforced before invoking `prost::Message::decode`.
  - **Recursion depth:** `prost` enforces its built-in nesting limit (default 100) and never panics on malformed varints, invalid UTF-8, or truncated inputs.
- **Problem.** Untrusted or malformed input must not cause unbounded memory growth.
- **Alternatives.** Per-element allocation counter inside a custom protobuf runtime (required owning the codec).
- **Why.** Pre-decode frame/header size limits + `prost` recursion depth keep memory bounded with zero custom codec maintenance; property tests check amplification on every internal message type.
- **Would change it.** Property tests showing problematic amplification on a specific message type (add explicit `Vec`/map element-count bounds in its `TryFrom`).
- **Complexity.** Minimal.
- **To undo.** Internal.

### Z23. Payload

- **Status.** Decided.
- **Decision.** A `Payload` is an encoded message carried inside a frame as its final length-delimited field, and decoded only by its handler:

  ```rust
  /// An encoded message kept as a zero-copy `bytes::Bytes` view
  /// until its handler decodes it.
  #[derive(Clone)]
  pub struct Payload(bytes::Bytes);

  impl Payload {
      pub fn from_bytes(bytes: bytes::Bytes) -> Self;
      pub fn decode<T: prost::Message + Default>(&self, limits: &Limits) -> Result<T, DecodeError>;
      pub fn as_bytes(&self) -> &bytes::Bytes;
      pub fn len(&self) -> usize;
  }
  ```

  On send, generated proxies encode typed arguments into `bytes::Bytes`. A retry or `roux-storage` passing a Clog batch passes an existing `Bytes`, avoiding re-encoding. Payloads do not count against a frame's 64 KiB header budget ([Z11](#z11-decoding-limits)).
- **Problem.** A frame's body must stay encoded until its handler, possibly on another thread or process, decodes it as the type its method names, and must appear only in the frame variants that carry one.
- **Alternatives.** A separate body after the header; decoding arguments in the frame decoder.
- **Why.** Messaging stays independent of application types, forwarding never re-encodes, and `Payload` speaks `bytes::Bytes` directly with zero conversions to `prost`, `slatedb`, or `object_store`.
- **Would change it.** Nothing foreseeable.
- **Complexity.** One thin wrapper type.
- **To undo.** Not applicable.

### Z12. Schema checks

- **Status.** Decided.
- **Decision.** Internal `.proto` files live under `schema/` and are compiled directly by `protox` + `prost-build` (`Z15`). For application grain interfaces, `cargo xtask schema` writes `contracts.proto` ([N23](messaging.md#n23-interface-and-method-ids)), and CI regenerates it and fails on diffs. CI runs `buf lint` and `buf breaking --against` (`WIRE`) on both `schema/` and `contracts.proto` against the last release.
- **Problem.** Breaking schema changes and inconsistent schemas must fail before release.
- **Alternatives.** A custom schema compatibility checker.
- **Why.** `buf` is mature and its `WIRE` rules match binary compatibility directly.
- **Would change it.** Nothing foreseeable.
- **Complexity.** A `buf` check in CI.
- **To undo.** Not applicable.

### Z21. Storage envelope

- **Status.** Withdrawn in review 2; the envelope moved to the durable layer, which is now Clog.
- **Decision.** L1 defines no storage envelope. Clog owns every durable format: its WAL records, ledger batches (with their CRC-32C), journal entries, and MetaStore values ([Storage](storage.md), [Clog spec](clog/README.md)). Grain journal entries, snapshots, and membership views are Protocol Buffers messages that Clog stores as opaque bodies, so they inherit Clog's checks.
- **Problem.** Review 2 asked to unify L3's and L5's 40-byte envelopes into one `roux-frame`.
- **Alternatives.** A shared 56-byte `roux-frame` in `roux-codec`.
- **Why.** The durable layer frames its own records, and the codec stays free of storage formats.
- **Would change it.** A durable record kind written outside Clog.
- **Complexity.** None in L1.
- **To undo.** Reintroduce a framing wrapper.

### Z22. Sum types and required fields

- **Status.** Decided.
- **Decision.** Every internal Roux domain type follows four rules:
  1. Alternatives that carry data are Rust enums, backed on the wire by a `.proto` `oneof`. Closed sets with no data are Rust enums backed by a protobuf `enum` whose `0` (`*_UNSPECIFIED`) value is rejected.
  2. No field's meaning depends on another field's value. When a field applies only in some cases, those cases become enum variants.
  3. A field that may be absent is an `Option`; every other field is required and validated in `TryFrom<WireType> for DomainType`.
  4. Unknown variants follow the type's policy: protocol types reject them (senders gate new variants by negotiated `protocol_version`, [N2](messaging.md#n2-handshake)), unknown interfaces/methods return `NotStarted::Incompatible` ([N23](messaging.md#n23-interface-and-method-ids)), and membership's stored register rejects unknown wire tags before decoding ([M7](membership.md#m7-formats)).

  Every layer applies these rules to its own messages.
- **Problem.** Make invalid state unrepresentable in Rust domain code without forcing a custom protobuf runtime.
- **Alternatives.** Using `prost`'s generated structs with `Option<Oneof>` and raw `i32` enums throughout domain logic (lets invalid states propagate); building a custom protobuf engine to generate domain enums directly (the previous decision).
- **Why.** Thin `TryFrom` conversions at the crate boundary give Roux clean, exhaustive Rust domain types while keeping standard `prost` + `protox` on the wire.
- **Would change it.** Nothing foreseeable.
- **Complexity.** One `TryFrom` impl per internal message family (a small, fixed set of types in `roux-net`, `roux-membership`, `roux-storage`, `roux-runtime`).
- **To undo.** Internal to each crate.

### Z24. Canonical encodings

- **Status.** Decided with typed grain keys (identities review).
- **Decision.** A type used as an identity, such as a grain key or `GrainId`, is canonical: every value has exactly one encoding, and decoding an identity accepts only that encoding. `#[derive(roux::GrainKey)]` (alongside `#[derive(prost::Message)]`) enforces it:
  - At compile time, the macro rejects `HashMap`, `f32`, and `f64` fields (and nested non-canonical types), ensuring `prost::Message::encode` is deterministic.
  - At decode time, `roux_id::decode_canonical` decodes the bytes with `prost::Message::decode`, re-encodes the value, and fails with `NotCanonical` unless the re-encoded bytes equal the input byte-for-byte. This rejects unknown fields, out-of-order fields, duplicate singular fields, non-minimal varints, and non-canonical repeated scalar encodings in one check.

  Messaging carries an object's key as uninterpreted bytes ([N22](messaging.md#n22-frame-schema)), so the handler's layer calls `decode_canonical` before it uses the key as an identity: the runtime does so before it looks up or creates a grain's activation, and before it derives the grain's Clog `JournalKey` and `RowKey` from it ([Identities](identities.md)).
- **Problem.** An object's key names its activation, its journal, and its place in Clog's key ranges. Protocol Buffers allows several encodings of one value, so two callers could send different bytes for the same grain and reach two activations or two journals, possibly on two nodes.
- **Alternatives.** Comparing decoded values instead of bytes; trusting senders to encode canonically.
- **Why.** Byte equality then means identity equality everywhere, and re-encoding a small key (`<= 1 KiB`) catches unknown fields, duplicates, and non-canonical varints without needing a custom protobuf decoder.
- **Would change it.** Profiles showing the re-encode on the hot path; caching decoded keys per connection would then remove it.
- **Complexity.** One small derive macro (`GrainKey`) and a 10-line `decode_canonical` helper.
- **To undo.** Internal, but identities would lose their single encoding.

### Z14. Verification plan

- **Status.** Decided.
- **Decision.**
  - **Round trips.** `proptest` checks `Domain::try_from(Wire::decode(encode(x))) == Ok(x)` for every internal message and representative user `prost::Message` types.
  - **Determinism & canonical encodings.** For every canonical identity type, `decode_canonical(encode(x)) == Ok(x)`, and injected unknown fields, permuted fields, duplicate singular tags, or non-minimal varints fail with `NotCanonical`.
  - **Domain validation (`Z22`).** For every internal message, bytes missing any required field or carrying a zero/unknown closed enum fail `TryFrom` with `Missing` or `UnknownVariant`.
  - **Membership wire-tag verifier (`M7`).** Property tests verify `reject_unknown_wire_tags` accepts every valid `StoredView`/`Entry` encoding and rejects any injected unknown tag, wrong wire type, or group at any nesting depth.
  - **Cross-version.** Pairs of `.proto` schema versions (optional fields added, variants added) are checked for forward/backward compatibility alongside `buf breaking` in CI.
  - **Limits & untrusted input.** `proptest` strategies feed arbitrary byte sequences through `roux-net::wire` and `TryFrom` boundaries, verifying no panics and bounded allocations.
  - **Derive errors.** `trybuild` compile-fail tests cover `#[derive(roux::GrainKey)]` rejecting `HashMap` and floats.
  - **Golden vectors.** `insta` snapshots pin the encodings of the header, handshake, identity, storage, and membership types.
  - **Benchmarks.** `criterion` benchmarks measure encode and decode latency for frames, headers, and typical RPC payloads over `Bytes`.
- **Problem.** Prove wire compatibility, domain-boundary validation, membership fail-stop safety, and malformed-input resilience.
- **Alternatives.** Round trips only.
- **Why.** `prost` already passes standard protobuf conformance tests; Roux's tests focus on Roux's own `TryFrom` invariants, canonical keys, framing limits, and `M7` wire-tag rejection.
- **Would change it.** Nothing foreseeable.
- **Complexity.** Small; only `TryFrom` boundaries and localized helpers need testing beyond `prost` itself.
- **To undo.** Not applicable.

### Durable formats

- **Status.** Decided.
- **Decision.** Grain journal entries, snapshots, and membership views are standard Protocol Buffers messages encoded by `prost`, stored by Clog as opaque bodies inside its own formats ([Z21](#z21-storage-envelope)), and every durable format carries a version.
- **Problem.** Durable data outlives the code that wrote it.
- **Alternatives.** A separate storage format.
- **Why.** One codec, one set of evolution rules, one set of tests.
- **Would change it.** A storage layout protobuf cannot express efficiently, such as a columnar index.
- **Complexity.** None beyond `prost`.
- **To undo.** Not applicable.

## Review notes

1. **Direct `roux-net::wire` header decoding** if benchmarks ever show `TryFrom<WireFrame> for Frame` allocation or copy overhead on tiny empty-call RPCs.
2. **Element-count limits in `TryFrom`** for any repeated/map field if property testing reveals excessive amplification under the 64 KiB header or 32 MiB payload cap.
