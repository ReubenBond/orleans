# Testing and simulation

Roux is tested primarily by deterministic simulation (`turmoil` + `slatedb-dst`) and Rust-native protocol model checking (`stateright` with mandatory `Bug` mutants and `LinearizabilityTester`): whole clusters (Roux actors, membership, messaging, and Clog storage) run in one thread on virtual time, driven by one seed, with injected faults. Any failure replays exactly from its seed. Property-based tests, crash-state enumeration, Miri, workspace lints, and real-process LazyFS + `SIGKILL` + Elle soak tests cover the layers beneath and around the simulator.

## Principles

- **Deterministic environment.** Roux code reaches threads, task spawning, cross-thread queues, time, randomness, and the network through `roux-net` and Tokio seams; Clog injects its clock, RNG, transport, WAL, and `MetaStore` traits (with `clog-journals` driving WAL-less `lsm-tree` synchronously without background threads in simulation); and SlateDB (`clog-archive`) injects `SystemClock` (`MockSystemClock`), `DbRand`, and `ObjectStore` (`slatedb-dst`). Production binds Roux to per-core Tokio (`current_thread` + `LocalSet`), Clog to `clog-wal` (Linux `io-uring` / portable `std::fs`) and WAL-less `lsm-tree`, and SlateDB to `object_store`. Simulation binds all three to a single-threaded [`turmoil`](https://github.com/tokio-rs/turmoil) simulation (`LocalSet` per simulated thread, `turmoil-net`, `turmoil-fs`, `turmoil-io-uring`, and seeded Tokio `RngSeed`), sharing production's exact Tokio `current_thread` + `LocalSet` executor semantics.
- **Sans-I/O protocol cores.** Membership, fault detection, activation lifecycle, durable jobs, and inbox/outbox — and Clog's ledger protocol, ledger-replica leases, ledger map, and CASPaxos — are pure Rust state machines driven directly by Stateright models, property tests, and the simulator.
- **Single oracle per contract.** Each core contract (`JournalModel`, `MembershipModel`, `GrainRuntimeModel`) has one pure-Rust reference model shared across `proptest-state-machine`, `stateright`, `turmoil`, and `LazyFS`.
- **Same code in production and simulation.** The simulator replaces the environment and I/O ports, preserving product logic verbatim.
- **Fault points.** Product code marks rare branches with `buggify!`-style points and names interesting moments for the simulator to target. Both compile to no-ops in production.
- **Properties, not examples.** Tests state invariants and let generators, crash enumerators, Stateright, and the simulator find counterexamples.

## Verification stack (consolidated)

Roux standardizes on a lean, Rust-native verification stack:

| Layer | Purpose | Libraries |
| --- | --- | --- |
| Property-based & model-based | Codecs (`prost` + `decode_canonical` + `reject_unknown_wire_tags`), `KeyRange` math, durable state machines, WAL-less `lsm-tree` replay against shared reference models | [`proptest`](https://docs.rs/proptest) + [`proptest-state-machine`](https://docs.rs/proptest-state-machine) |
| Protocol model checking & linearizability | Exhaustive BFS and randomized walks directly over *production* sans-I/O Rust protocol cores (`clog-ledgers`, `clog-caspaxos`, `roux-membership`, `roux-net`, `roux-runtime`) with mandatory `Bug` mutants and client linearizability checks | [`stateright`](https://docs.rs/stateright) (`LinearizabilityTester`) |
| Crash-state enumeration | Exhaustive single-file crash prefixes on WAL (`CLFL`), multiplexed `LedgerStore` (`CLSC`/`CLSI`), and WAL-less `lsm-tree` warm index recovery | Fault-injecting FS (`turmoil-fs` / `turmoil-io-uring`) |
| Deterministic simulation | Whole Roux + Clog + SlateDB clusters under partitions, stalls, crashes, disk errors, and object-store faults, guarded by PR determinism self-tests | [`turmoil`](https://github.com/tokio-rs/turmoil) (`turmoil-net`, `turmoil-fs`, `turmoil-io-uring`) + `slatedb-dst` |
| Kernel & runtime fault tests | Real `clog-wal` Linux `io-uring` (`O_DIRECT` alignment/short I/O, teardown, loopback `ENOSPC`, `LazyFS` `EIO`) and `tokio::net` + `tokio-rustls` reset/half-close on Linux CI | `io-uring` + `tokio` + `tokio-rustls` + `LazyFS` |
| UB detection | `unsafe` blocks (`clog-wal` aligned/direct-I/O buffers, cross-thread queues) | [Miri](https://github.com/rust-lang/miri) |
| Real-process crash & linearizability soak | Multi-process `SIGKILL` over unsynced page-cache drops, partitions, `SIGSTOP` stalls, and overlapping membership/disk-loss faults | [LazyFS](https://github.com/dsrhaslab/lazyfs) + `SIGKILL` harness + [Elle](https://github.com/jepsen-io/elle) / `LinearizabilityTester` |
| Architecture, schema & workspace lints | Cross-crate DAG (`clog-*` ↛ `roux-*`, L0–L4 layering), cross-crate dead `pub` items, workspace dependency inheritance, committed `contracts.proto` schema checks, strict Clippy restrictions | `cargo xtask lint` + `cargo xtask schema` + `[workspace.lints]` |
| Golden formats | Wire and storage format stability | [`insta`](https://insta.rs) + Clog golden vectors |
| Runner | Parallel tests, seed sweeps | [`cargo-nextest`](https://nexte.st) |

### Protocol model checking (Stateright + `Bug` mutants)

Roux runs **`stateright`** directly against the *production* Rust sans-I/O protocol state machines (`clog-ledgers`, `clog-caspaxos`, `roux-membership`, `roux-net`, `roux-runtime`), eliminating spec-to-code drift:

- **Mandatory `Bug` mutant enum.** Every Stateright model includes an explicit `Bug` enum injecting realistic protocol defects (e.g., `NoFence`, `SealOne`, `SyncEarly`, `ActivateEarly`, `IgnoreRemoved`, `DropBeforeDrain`, `AckBeforeDurable`). Tests assert that every mutant is caught in both exhaustive BFS and randomized walks (`UniformChooser`).
- **State-space pruning & non-vacuous properties.** Models designate a single `doomed` replica to explore separate `Sync`/`Crash` interleavings while healthy peers auto-settle, prune dead in-flight replies (`forget()`), and pair every `Property::always` safety invariant with `Property::sometimes` reachability checks so invariants cannot pass vacuously.
- **Fast dev-build Stateright execution.** The root `Cargo.toml` sets `opt-level = 3` on `stateright` and the sans-I/O protocol crates under `[profile.dev.package.*]` (`stateright`, `clog-ledgers`, `clog-caspaxos`, `roux-membership`, `roux-net`, `roux-runtime`) so million-state BFS checks complete in seconds during regular `cargo nextest run`.
- **Type-enforced durability barriers.** Protocol state machines only emit persistence-dependent acknowledgments from a dedicated `synced(durable_pos)` transition invoked after `fdatasync` / `MetaStore` commit completes, preventing the I/O driver from acknowledging un-synced writes structurally (and enabling `SyncEarly` mutants to prove the invariant).

### Property-based & model-based testing

- **Serialization (`prost` + `protox`).** Round trips for every `prost::Message` / domain `TryFrom` pair, canonical re-encoding invariants (`roux_id::decode_canonical`, Z24), `roux-membership`'s `reject_unknown_wire_tags` (`M7`), and schema evolution across version pairs `n-1`, `n`, `n+1`.
- **Clog storage & journaling.** Dyadic `KeyRange` coverage and split/merge invariants; unversioned `RowKind::Message = 3` batch framing (zero segment-dictionary and zero `lsm-tree` index entries); `JournalVersion` CAS and CRC-32C integrity (including `HasMessagesFlag = 0x80` atomic messages and `roux-jobs` `ReminderMessageCodec` canonical round-trips and procedural rejection order); applying grain entries then `Replace` snapshot equals applying the snapshot.
- **Model-based testing.** `proptest-state-machine` checks durable state machines (`DurableValue`, `DurableMap`, `DurableList`, `DurableQueue`, `DurableSet`, `Inbox`, `Outbox`, `DurableJobQueue` including monotonic `execution_generation` cancel guards and dirty-state failure fencing), `clog-journals` (including atomic message admission `JS-I11`, slot-ordered `MessageSession` oracle `JS-I12`, and WAL-less `lsm-tree` warm index replay (size-based, `30 s` periodic, and clean-shutdown flushes skipping `!is_archive_kind` rows) vs a `BTreeMap` reference model), `clog-archive` (including single-pass `DrainScan` `SegmentMessageFolds`, `OverBudget` -> `Alone` rescan, deterministic idempotent sink commits, and fail-closed unknown-channel checks), `roux-jobs` (`ReminderMessageProcessor` deterministic sealed `JobShard` idempotency), membership replay, and the activation table against simple reference models.

### Crash-state enumeration & Miri

- **Crash-state enumeration.** `clog-wal` (`CLFL` + multiplexed `LedgerStore` `CLSC`/`CLSI`) and `clog-journals`'s WAL-less `lsm-tree` recovery run exhaustive crash-state enumeration: simulating power loss after every prefix of writes/fsyncs (including torn tails, un-fsynced renames, and lost `lsm-tree` memtables replayed from `clog-wal`) and verifying exact recovery invariants.
- **Miri.** Thread-per-core execution with deferred work stealing (`R24`) and standard `bytes::Bytes` keeps almost all of Roux in safe, single-threaded Rust; Miri verifies the narrow `unsafe` code (`clog-wal` `AlignedBuf` direct-I/O buffers and cross-thread queues).

### Runtime-level tests

Code below the transport and storage traits (`tokio::net` TCP + `tokio-rustls`, Linux `io-uring` and portable `std::fs` `clog-wal`, `lsm-tree`, `object_store`, SlateDB) runs on real Linux and Windows/macOS runtimes with targeted fault suites:

- **Linux `clog-wal` `io-uring` & socket fault tests (Linux CI):** `O_DIRECT` alignment and short-read/write handling, clean ring teardown with in-flight SQEs (including `MSG_NOSIGNAL` on peer-kill sends and `IORING_OP_ASYNC_CANCEL` completion handling with zero leaked slab slots when `io_uring` networking is added; see [Storage performance & resilience learnings §13](storage-performance-learnings.md#13-linux-io_uring-networking--wal-implementation-cautions-v8)), small `tmpfs`/loopback `ENOSPC` fail-stop behavior, `LazyFS` `EIO` injection, and `tokio::net` + `tokio-rustls` reset/half-close handling.
- **Resource & priority checks:** real-runtime integration tests verify open file descriptors, WAL segment reclamation, and `High`-lane latency under sustained `Normal`-lane load.

## Deterministic simulator

The simulator runs a whole Roux-on-Clog-on-SlateDB cluster, simulated NVMe disks, and simulated object storage on one thread via **`turmoil`** (`turmoil-net`, `turmoil-fs`, `turmoil-io-uring`) and **SlateDB DST** (`slatedb-dst`):

- **Executor.** `turmoil` drives multiple simulated hosts on a seeded single-threaded Tokio runtime (`tokio_unstable` `RngSeed`). Each simulated Roux thread runs inside a `LocalSet`, matching production's exact per-core `current_thread` + `LocalSet` Tokio executor semantics, alongside Clog's `RPITIT + Send` storage tasks and SlateDB's `Db`/`DbReader` background tasks.
- **Time.** Virtual time advances deterministically to the next timer when all tasks are idle (`turmoil` + SlateDB's `MockSystemClock`). Node wall clocks receive arbitrary skews and jumps; monotonic clocks drift within configurable bounds (`ρ`).
- **Network.** `turmoil-net` simulates connections with configurable latency, reordering across connections, symmetric/asymmetric partitions, resets, slow peers, and backpressure on Clog's replica/forward budget lanes.
- **Disk & object storage (`turmoil-fs` / `turmoil-io-uring` + `slatedb-dst`).** Simulated NVMe uses `turmoil-io-uring` / `turmoil-fs` (portable across Linux, Windows, and macOS) for configurable fsync latency, torn writes, and crash-on-power-loss, paired with `InMemoryMetaStore` / fault-injecting `object_store` (conditional writes, throttling, timeouts, ambiguous failures) and `clog-caspaxos`. Real `clog-wal`, `clog-ledgers`, `clog-journals` (driving WAL-less `lsm-tree` synchronously without background threads on per-node temp directories, wiped/replayed from `clog-wal` on simulated crashes), and `clog-archive` (SlateDB) run directly on top.
- **Processes.** Nodes crash, restart with their persisted `NodeId` (or after disk loss), and pause (simulating VM stalls).
- **Swarm testing.** Each seed randomizes fault rates, thread counts, ledger split/merge thresholds, timeouts, and `MetaStore` backend (`ObjectStoreMetaStore` vs `clog-caspaxos`).
- **Determinism self-test.** PR CI runs fixed `turmoil` + `slatedb-dst` seeds twice and asserts identical execution-trace and final-state hashes so non-determinism (random `HashMap` seeds, leaked background threads) fails immediately.

### Invariants

The simulator checks invariants continuously and at quiescence. Durable invariants hold under arbitrary clock skew and pauses; non-durable single-activation invariants hold under the L2 ledger-replica lease assumptions (`ST8`: `ρ`, `δ`), and runs exceeding those assumptions must still preserve every durable invariant:

- **Membership.** Views are dense and monotonic; a dead `MemberId` never returns; a removed process stops on observing its own death (`C6`). Per-ledger replica leases (`ST8`) guarantee no successor ledger commits in `LedgerMap` while a closing ledger's lease remains valid.
- **Clog keyspace & durability.** Open ledgers in `ledger-map` cover the full `RowKey` space without overlap; every acknowledged `Journal` write survives crashes, handoffs, takeovers, splits, merges, rolls, and SlateDB archive draining/compaction; `JournalVersion` CAS is strictly linearizable.
- **Single activation.** At most one activation commits journal entries for any grain version; under lease bounds, at most one activation produces non-durable effects at a time.
- **Nil-externality.** No peer observes a response or outgoing message whose dependent journal write is lost across a crash.
- **Jobs.** Every durable job due before quiescence executes at least once.
- **Inbox/outbox.** Every committed outgoing message is processed once per deduplication window.
- **Resource bounds (at quiescence).** Open file descriptors, WAL segments, `lsm-tree` trees, and live activations return to expected bounds after churn, deactivations, and closed-ledger archival.
- **Priority isolation.** `High`-lane membership probes and Clog's replica control lane (`ReadTail` seals, `Heartbeat` lease renewals) stay well within `D = 5 s` while `Normal` Clog replication lanes are saturated.
- **Liveness.** After faults stop, the cluster converges: views agree, every `KeyRange` has a healthy open ledger, drains complete, and grain calls succeed.
- **Coverage.** `assert_sometimes!` checks that rare paths (handoff during crash, split during takeover, ambiguous MetaStore CAS, SlateDB compaction during read) actually execute.

Operation histories are checked against the shared pure-Rust reference models (`JournalModel`, `MembershipModel`, `GrainRuntimeModel`) using Stateright's `LinearizabilityTester`—modeling indeterminate-outcome (`Unknown`/crashed) operations explicitly so an unobserved invocation may linearize at most once after invoke or not at all—and can be exported to [Elle](https://github.com/jepsen-io/elle).

## Continuous testing and workspace lints

- **Every pull request** runs `cargo xtask lint` (enforcing cross-crate layering `clog-*` ↛ `roux-*` and L0–L4 DAG, cross-crate dead `pub` items, workspace dependency inheritance, and strict `[workspace.lints]` Clippy restrictions: `unsafe_code`, `unwrap_used`, `expect_used`, `panic`, `indexing_slicing`, `as_conversions`, `large_futures`, `let_underscore_future`, `unchecked_time_subtraction`), `cargo xtask schema` (checking committed `contracts.proto` for ID reuse or breaking changes), `cargo nextest run` (unit, `proptest`, shared-oracle model tests, crash-enumeration, Stateright BFS + `Bug` mutant checks, fixed-seed `turmoil` + `slatedb-dst` simulations + determinism self-test, and targeted Linux `clog-wal` `io-uring` and `tokio-rustls` fault tests), and Miri on `unsafe` blocks.
- **Scheduled nightly runs** sweep fresh `turmoil` + `slatedb-dst` seeds; failing seeds become regression tests.
- **Scheduled weekly soak runs** gate on `planted-bugs` *first* (every Stateright `Bug` mutant must be caught within 5 minutes); only after that passes do parallel 30-minute randomized-walk shards run with reproducible CI-run-derived seeds alongside the **LazyFS + `SIGKILL` multi-process crash suite** (injecting unsynced page-cache drops, network partitions, `SIGSTOP` stalls, and overlapping membership/disk-loss faults while checking operation histories with Elle / `LinearizabilityTester` against the shared reference models).
- **Scheduled cloud integration tests** run against real S3, Azure Blob Storage, and GCS.
- **Workspace profiles (`Cargo.toml`):** `[profile.dev.package.*] opt-level = 3` for `stateright` and sans-I/O protocol crates, `[profile.lab]` (thin LTO + incremental) for fast benchmark iteration, `[profile.profiling]` (release + full debuginfo), and fat-LTO `[profile.release]`.

### Milestone-gated additions

Added as the corresponding subsystems land rather than built upfront:

- **Decoder fuzzing (`cargo-fuzz`, weekly CI):** add ~5 small fuzz targets once wire and disk codecs (`CLFL`, `CLSC`/`CLSI`, `CLLM`, `roux.wire.v1`, `decode_canonical`) exist, using a CRC-recompute wrapper for disk frames.
- **Mixed-version and persisted-fixture upgrade tests:** add when approaching v1 release and the first format version bump to verify `N−1`/`N`/`N+1` rolling upgrades and persisted-file replay.
- **Hot-path and layer-decomposition benchmarks (`criterion` + harness under `[profile.lab]`):** Added once the end-to-end skeleton runs (on-demand/nightly) to track layer overhead, validate the 1M RPS target, and evaluate deferred optimizations (`kTLS`, `SEND_ZC`, pooled pages):
  - **L0–L4 cumulative layer decomposition:** Benchmark matched payloads (`128 B`, `4 KiB`, `64 KiB`, `1 MiB`) cumulatively across **L0** (`clog-wal`), **L1** (`LedgerStore`), **L2** (`LedgerQuorum` `N=3, Q=2`, in-memory and WAL-backed), **L3** (`JournalStore`, in-memory and WAL-backed, single hot journal and many journals), and **L4** (one-node-per-OS-process loopback TCP cluster), across both **1-ledger** and **16/64-ledger fan-out** (multi-ledger fan-out catches cross-ledger serialization bottlenecks such as lock striping).
  - **Closed-loop + coordinated-omission-free open-loop sweeps:** Pair fixed-concurrency closed-loop runs (`c = 1, 16, 64`) with coordinated-omission-free open-loop Poisson arrival sweeps (`10%`, `50%`, `75%`, `90%` of closed-loop saturation), recording both total latency (`t_done - t_scheduled`) and service time (`t_done - t_dispatch`), and explicitly flagging `OVERLOADED` whenever the bounded in-flight cap drops requests.
  - **Multi-process cluster saturation checks:** Run real one-node-per-OS-process loopback TCP scenarios including `forwarded-journals` and `probe-saturation` (asserting Rapid `High`-lane probe p99 RTT stays far below failure-detector timeouts with zero false suspicions under sustained `1 MiB` `Normal`-lane storage saturation).

## Golden vectors

Roux's `clog-*` crates are algorithmically aligned with Clog and own their own Rust/protobuf golden vectors (`tests/shared-vectors/`), keeping binary/protobuf formats, Stateright models, and implementations updated in the same PRs.

## Formal Quint specifications

Algorithm-level protocol models for every layer live in [`specs/`](../../specs/README.md) (`messaging`, `fast-caspaxos`, `ledger-protocol`, `ledgers`, `archive`, `message-drain`, `rapid`, `membership`, `grain-runtime`, `range-handoff`, `jobs`) and run automatically in GitHub Actions CI (`specs-*.yml`) with typechecking, directed scenarios, randomized simulation, guard-disabling mutants, and TLC model checking.
