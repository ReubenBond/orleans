# Storage Performance & Resilience Learnings (`v1 → v11`)

This reference consolidates the 13 empirical performance, zero-copy, and failover/rejoin lessons proven across an 11-round (`v1 → v11`) benchmark campaign on a 3-node Azure AKS cluster (`Standard_L8s_v3`, local NVMe, 3×-replicated Clog ledgers). Each lesson pairs the exact failure mode observed under load or node churn with the normative rule, default threshold, and idiomatic Rust implementation pattern for Roux's `clog-*`, `roux-storage`, and `roux-net` crates.

Normative specifications live in [`storage.md`](storage.md), [`messaging.md`](messaging.md), [`wire-catalog.md`](wire-catalog.md), and [`clog/invariants.md`](clog/invariants.md).

## Campaign Headline Outcomes

| Metric | Baseline (`v1`) | Final (`v11`) |
| --- | --- | --- |
| 3×-replicated durable grain writes (128 B) | `50.8k ops/s` | **`173,108 ops/s`** |
| Aggregate 3×-replicated physical WAL throughput | — | **`1,691 MiB/s` (~`1.65 GiB/s`) at ~60% CPU** |
| Per-node allocation rate under peak WAL load | `760–833 MB/s` | **`262 MB/s` (`−36%` to `−45%`)** |
| WAL file rollover latency | `3.5–13.9 s` | **`0.41–1.77 ms`** |
| Graceful node stop duration (`N=3, Q=2`) | `31 s` (missed `0 / 147` checkpoints) | **`< 1.5 s` (`147 / 147` checkpoints)** |
| `hedge_budget_exhausted` events across failover/rejoin | `453,853` | **`0`** |
| Rejoin recovery max latency (`silo-2`) | `13,875 ms` | **`659 ms`** |
| Timeouts, lost writes, or zero-op seconds (graceful stop + rejoin & hard crash + rejoin) | Frequent stalls / timeouts | **`0` / `0` / `0`** |

## Summary Table of Defaults & Thresholds

| Parameter | Normative Default | Owning Layer / Module | Lesson |
| --- | --- | --- | --- |
| WAL staged zero-fill sync cadence (`ZERO_FILL_SYNC_BYTES`) | `64 MiB` (4 MiB chunks + `POSIX_FADV_DONTNEED`) | `clog-wal` | [#1](#1-zero-stall-wal-file-preallocation-v1--v2) |
| WAL staging trigger / hurry threshold (`PREPARE_READY_FILL`) | `12.5%` (doubling) / `25%` (steady), hurry at `75%` | `clog-wal` | [#1](#1-zero-stall-wal-file-preallocation-v1--v2) |
| WAL bulk-mode EWMA hysteresis | Enter `>= 3 MiB`, leave `< 1 MiB` (weight `1/8`) | `clog-wal` | [#1](#1-zero-stall-wal-file-preallocation-v1--v2) |
| Graceful shutdown handoff quorum gate | `>= Q = (N + 1) / 2` other live active diskful nodes | `clog-ledgers` / `roux-storage` | [#2](#2-degraded-q-of-n-cooperative-graceful-shutdown-on-n-node-clusters-v1--v2) |
| Per-peer replica transport lanes | `3` independent FIFO lanes (`Write`, `Read`, `Control`), local & remote | `roux-storage` | [#5](#5-three-isolated-fifo-lanes-per-peer-write-read-control-v4--v5) |
| Adaptive `LedgerAdmission` coalescing threshold | `0` linger when idle; coalesce up to `min(64 KiB, max_batch_bytes)` / `256` entries | `clog-journals` | [#7](#7-adaptive-proposal-coalescing-in-ledgeradmission-v2--v3) |
| Snapshot compaction triggers (`REPLACE_REQUEST_*`) | `bytes_since_base > max(64 KiB, α * base_length)` (`α=1`) **OR** `appends_since_base >= 128` | `clog-journals` | [#8](#8-snapshot-compaction-floor--append-count-cap--descending-key-index-reads-v2--v3--v7--v9) |
| Concurrent history reads (`max_concurrent_history_reads`) | `64` per node | `clog-journals` | [#8](#8-snapshot-compaction-floor--append-count-cap--descending-key-index-reads-v2--v3--v7--v9) |
| Batched replica read RPC limits | `ReadRange`: `128` slots / `1 MiB`; `ReadSlots`: `1 MiB` (`16–32` wave entries) | `clog-ledgers` / `roux-storage` | [#9](#9-batched-multi-slot-readrange-and-scattered-slot-readslots-rpcs-v4--v6--v10) |
| `LedgerApplyLoop` scan permits | `32` background + `16` priority (`WaitForReplicatedCatchUp`) | `clog-ledgers` / `clog-journals` | [#10](#10-bounded--wait-prioritized-ledgerapplyloop--batched-index-commits--closed-ledger-read-repair-suppression-v4--v5--v6) |
| `JournalStateDatabase` apply batching | `1,024` rows or `1 MiB` per atomic `lsm-tree` `WriteBatch` | `clog-journals` | [#10](#10-bounded--wait-prioritized-ledgerapplyloop--batched-index-commits--closed-ledger-read-repair-suppression-v4--v5--v6) |
| Warm-index periodic flush & shutdown drain wait | `flush_interval = 30 s` (`64 MiB` memtable); drain unwind `<= min(transition_timeout, 5 s)`, flush `<= 5 s` | `clog-journals` / `roux-storage` | [#11](#11-fast-graceful-shutdown-cancelling-idempotent-drains--30-s-periodic-index-flush-v8--v9) |
| Drain & transition pacing | `max_concurrent_drains = 2` (`+1` exposed `OR-I23` = `3` max); `max_concurrent_transitions = 2` (`32` departures, `64` repairs) | `clog-ledgers` / `clog-archive` | [#11](#11-fast-graceful-shutdown-cancelling-idempotent-drains--30-s-periodic-index-flush-v8--v9) |
| `LedgerSegmentView` snapshot cadence | Every `15 s` when advancing + exact final watermark on graceful shutdown | `clog-journals` | [#12](#12-persisted-ledgersegmentview-snapshots--catchupready-balance-gate-v10--v11) |

---

## The 13 Lessons

### 1. Zero-stall WAL file preallocation (`v1 → v2`)

- **Failure mode avoided:** Synchronous `1 GiB` zero-fill on WAL file rollover stalled all writes on the node for `3.5–13.9 s`.
- **Normative rule ([`storage.md §2c`](storage.md#2c-zero-stall-staged-wal-preallocation-clog-wal-wal-i3-wal-i7)):** Decouple zero-fill staging from file-number publication:
  - Stage unpublished `prealloc-{id}.tmp` files in the background via `WalDirectory::prepare_file`, writing `4 MiB` chunks through the page cache, syncing every `ZERO_FILL_SYNC_BYTES = 64 MiB` (and at completion), and dropping synced pages on Linux with `POSIX_FADV_DONTNEED`. Start staging at `12.5%` full while file sizes are doubling (`4 MiB → file_size`) and `25%` at steady `file_size`.
  - Keep a **hurry token** separate from the **shutdown cancellation token**: pace remaining bytes toward `75%` (`PREPARE_READY_FILL`, capped at `1 s` per wait), and cancel the hurry token immediately at `75%` or on roll so background staging finishes at full speed without cancelling. Shutdown cancels the cancellation token immediately and unlinks the `.tmp` cleanly; open deletes any leftover `*.tmp`.
  - On rollover, publish a ready staged file via `publish_prepared_file` (1-page header write + sync + atomic rename to `{file:020}.clfl` + directory fsync) in `< 1 ms`. If staging is still in flight during a burst, hurry it and roll immediately into a `< 1 ms` `fallocate` (`create_allocated_file`) file, then switch early once the staged file completes — publishing zero skipped empty `.clfl` files.
  - Preserve `WAL-I7` across consecutive fast rolls with a transitive `previous_files_durable` check covering all earlier files in the current open.
- **Rust guidance:** Use two `tokio_util::sync::CancellationToken`s (`hurry` and `shutdown`) inside the `clog-wal` staging worker, and model `PreparedWalFile` as an affine/linear RAII handle whose `Drop` unlinks the temp path unless consumed by `publish_prepared_file`.
- **Measured impact:** WAL rollover tail latency dropped from `3.5–13.9 s` to `0.41–1.77 ms`.

### 2. Degraded `Q`-of-`N` cooperative graceful shutdown on `N`-node clusters (`v1 → v2`)

- **Failure mode avoided:** Requiring `>= N` other live nodes before handing off primaries stalled graceful shutdown on a 3-node cluster (`N = 3, Q = 2`) for `31 s` until membership declared the stopping node dead, turning planned stops into unplanned crash takeovers.
- **Normative rule ([`storage.md §8`](storage.md#graceful-shutdown-order), [`OR-I5`](clog/invariants.md)):**
  - When a primary departs gracefully (`Departing` / `HandingOff`) on a cluster with fewer than `N` but at least `Q = (N + 1) / 2` other live active non-diskless nodes, degraded `Departure` placement selects all available live non-leaving executors (`>= Q`, primary first) and fills the remaining slots from active non-retired, non-diskless `current` replicas of the closing ledger in order: declared `Dead` replicas first, then active gracefully leaving (`Departing` / `HandingOff`) replicas.
  - On clusters with `>= N` other live executors, exclude the stopping node before any degraded fallback runs. Never keep retired or diskless nodes (`OR-I4`).
  - Keep `NodeControl::PrepareRelease` open while the stopping node is in `HandingOff` so successors perform `0 ms` cooperative `PrepareRelease` + `Replace`, and lower the shutdown handoff wait gate (`quorum_target_exists`) to `>= Q` other active diskful nodes (exiting immediately during whole-cluster shutdown or when `< Q` remain).
- **Rust guidance:** Encode the placement rule as a pure function in `clog-ledgers` (`Placement::choose_departure`) asserted by `proptest` and `stateright` against `OR-I4`/`OR-I5`, and separate transition-admission closing from `PrepareRelease` serving in `NodeControl`.

### 3. Balanced `Departure` / `HandOff` primary placement (`v5 → v6`)

- **Failure mode avoided:** Selecting the successor primary from candidate current replicas in fixed replica-array order dumped all 21 departing ledgers onto a single surviving node, pegging it at `91%` CPU and throttling the other survivor's quorum acknowledgments even while that survivor sat at `40%` CPU.
- **Normative rule ([`storage.md §6`](storage.md#translating-rapid-viewupdate-to-clog-ledgers-controller-m39), [`§8`](storage.md#graceful-shutdown-order)):** Order candidate current replicas for every departing or takeover handoff by `(under_disk_pressure, primary_count, node_id)` (and `(primary_count, node_id)` for deferred object-store ledgers), accounting for both **same-pass** and **in-flight** primary assignments so departing ledgers split evenly across all surviving nodes.
- **Rust guidance:** Maintain a mutable `HashMap<NodeId, usize>` projection of `active_and_inflight_primaries` inside `ClusterController::run_pass`, incrementing the chosen executor's count immediately as each transition in the pass is planned.

### 4. Dead-node & stopping-incarnation transport fast-fail (`v3 → v4 → v5`)

- **Failure mode avoided:** Sending every quorum batch to a dead or stopping 3rd replica during degraded `Q`-of-`N` operation triggered `13,189` placement-refusal exceptions and synchronous error logs in `10 s`, causing a `−45%` healthy-node throughput dip and `2×` allocator/GC churn.
- **Normative rule ([`storage.md §2b`](storage.md#2b-hot-path-concurrency-allocation-and-zero-copy-discipline), [`§5`](storage.md#admission-and-budget-lanes)):**
  - Cache `(CLNR registry, Rapid membership)` liveness resolution by reference/epoch on each peer's transport state.
  - Record `(NodeId, HostIncarnation)` as stopping/unavailable whenever a peer replies `NodeStopping` or fails transport-unavailable during the brief membership-gossip gap.
  - Check liveness and stopping marks **before** acquiring caller FIFO call budgets, copying/framing payloads, or touching transport queues: fail fast locally as `Unavailable` with zero effect, trigger a single-flight rate-limited (`<= 1/s`) stale-cache registry re-read if needed, and automatically clear the stopping mark once `CLNR` advances to a newer active host incarnation.
- **Rust guidance:** Store `cached_resolution: ArcSwap<(u64, u64, PeerResolution)>` and `stopping_incarnation: AtomicU64` (storing the `ProcessId` / incarnation generation) per peer in `roux-storage::transport` so the fast-fail check is two lock-free atomic loads with zero allocations and zero error-level logging on expected degraded writes.

### 5. Three isolated FIFO lanes per peer: `Write`, `Read`, `Control` (`v4 → v5`)

- **Failure mode avoided:** Sharing one `Data` FIFO lane (`128` slots) between foreground quorum `Write`s and background `Read`s allowed failure-triggered drains (`OR-I23`) and `LedgerApplyLoop` catch-up scans to head-of-line block foreground quorum writes for `100–4,267 ms`.
- **Normative rule ([`storage.md §5`](storage.md#admission-and-budget-lanes)):** Split every node's replica transport budget — for **both** local and remote replicas — into three independent FIFO `CallBudget` lanes:
  1. **`Write` lane** (`MaxNodeCalls`, `MaxNodeCallBytes`): `LedgerReplica::Write` (including recovery writes).
  2. **`Read` lane** (`MaxNodeCalls`, `MaxNodeCallBytes`): `Read`, `ReadSlice`, `ReadRange`, `ReadSlots` (local reads count as calls and reserve `0` bytes).
  3. **`Control` lane** (`MaxNodeControlCalls = 8`): `ReadTail` (including seals) and batched `Heartbeat` lease renewals.
- **Rust guidance:** Embed the five per-peer `CallBudget` structs (`replica_write`, `replica_read`, `replica_control`, `forward_write`, `forward_read`) directly inside `Arc<PeerBudgets>` cached on each peer handle. Acquire uncontended leases synchronously with zero clock reads (`try_enter_lease()`) and release via a stack RAII `CallBudgetLease` `Drop` guard.

### 6. True zero-copy pooled wire framing & eliminating intermediate copies (`v3 → v4 → v5 → v7`)

- **Failure mode avoided:** Copying payloads at each boundary (`state manager → journal storage → LedgerBatch → wire copy into oversized 32 KiB pool buckets → replica receive buffer`) burned `760–833 MB/s` per node and leaked pooled buffers on failure/drop paths.
- **Normative rule ([`storage.md §2`](storage.md#2-buffers-bytesbytes-and-alignedbuf-st6-n14), [`§2b`](storage.md#2b-hot-path-concurrency-allocation-and-zero-copy-discipline)):**
  - Borrow caller slices (`&[u8]` / multi-segment slice) directly when writing into `LedgerBatch` without materializing an intermediate owned copy.
  - Frame outbound multi-replica payloads, single-slot `Read` replies, batched `ReadRange` replies, and scattered `ReadSlots` replies directly from shared immutable ref-counted `bytes::Bytes` (`Bytes::clone()` is an atomic refcount bump, zero copy) straight into transport send buffers (`write_vectored` / `io_uring`), and decode received payloads directly into exact-sized `Bytes` slices (`#[prost(bytes = "bytes")]`).
  - Never pad small payloads into fixed-size oversized pool buckets; enforce deterministic RAII release (`Drop`) on both send completion and drop-before-send.
- **Rust guidance:** Leverage `bytes::Bytes` end-to-end across `roux-net`, `prost`, `roux-storage`, `clog-ledgers`, `clog-wal`, `clog-archive`, and `clog-journals`, plus `BytesMut::with_capacity(exact_len)` for exact-sized framing and `SmallVec<[Bytes; 4]>` (`ChunkedBuf`) for vectored writes.
- **Measured impact:** Cut per-node allocations by `−36%` to `−45%`, sustaining `1,691 MiB/s` aggregate physical WAL at `262 MB/s` allocation rate.

### 7. Adaptive proposal coalescing in `LedgerAdmission` (`v2 → v3`)

- **Failure mode avoided:** Eagerly proposing a new 1-entry `LedgerBatch` while an earlier proposal was already in flight doubled replica RPCs and WAL records under moderate per-ledger concurrency.
- **Normative rule ([`storage.md §2b`](storage.md#2b-hot-path-concurrency-allocation-and-zero-copy-discipline)):**
  - When the ledger is idle (`outstanding == 0`), draining for cooperative `PrepareRelease`, or blocking writers waiting for queue room (`queued_bytes >= max_queued_bytes`), propose immediately with zero linger.
  - While `1 <= outstanding < sequencer_window`, coalesce arriving small writes into a single multi-row `LedgerBatch` until either an in-flight proposal completes or unbatched entries reach `max_batch_entries` (`256`) or `min(64 KiB, max_batch_bytes)` (default `max_batch_bytes = 1 MiB`), then pipeline immediately up to `sequencer_window`.
- **Rust guidance:** Track `unbatched_entries`, `unbatched_bytes`, `outstanding`, `draining`, and `queue_waiters` under `LedgerAdmission`'s lock, keeping the uncontended first-batch inline fast path on the caller task when `outstanding == 0`.

### 8. Snapshot compaction floor + append-count cap + descending key-index reads (`v2 → v3 → v7 → v9`)

- **Failure modes avoided:**
  1. A pure byte-ratio trigger (`bytes_since_base > α * base_length`) forced a full snapshot on nearly every 128 B write.
  2. Raising only a `64 KiB` floor without an append-count cap — combined with *ascending* key-index scans (`slot 0 → tail`) — let append chains grow to `250–300` entries and caused migrating `400` hot grains (each with `1,500–2,600` historical writes in the ledger) to issue `~600,000` single-slot reads (`1,610` canceled recoveries and a `22 s` stall).
  3. Too tight an append cap (`32`) increased steady-state compactions `11×` (`−9%` write throughput).
- **Normative rule ([`storage.md §2a`](storage.md#2a-specialized-local-warm-journal-index-st9-wal-less-lsm-tree), [`§2b`](storage.md#2b-hot-path-concurrency-allocation-and-zero-copy-discipline), [`JS-I9`](clog/invariants.md)):**
  - Trigger `ReplaceRequested` when `bytes_since_base > max(64 KiB, α * base_length)` (`α = 1`, `REPLACE_REQUEST_MIN_BYTES = 64 KiB`) **OR** `appends_since_base >= 128` (`REPLACE_REQUEST_MAX_APPENDS = 128`).
  - Walk `KeyIndexEntry` slots **descending (newest → oldest)** in bounded waves (`clamp(K, 16, 32)` entries per wave), resolving each entry's `defining_slot` when distinct for full-key collision verification, **stopping immediately** once a verified matching base row (`Replace`, `Delete`, or `Expired`) is decoded, and skipping all older ledgers and the SlateDB archive store once a newer ledger yields a verified base. Reverse the collected `[base .. newest]` slice into chronological order before returning.
  - Set `max_concurrent_history_reads = 64` per node so cold activations, bulk population, and post-handoff reloads do not serialize behind a 4-slot semaphore.
- **Rust guidance:** Use `lsm-tree`'s reverse prefix iterator (`.rev()`) over `(ledger_id, key_hash)` index entries so descending wave construction allocates only the current wave (`SmallVec<[KeyIndexEntry; 32]>`) and never scans pre-`Replace` history.

### 9. Batched multi-slot `ReadRange` AND scattered-slot `ReadSlots` RPCs (`v4 → v6 → v10`)

- **Failure mode avoided:** Fetching individual slots during catch-up (`787,996` RPCs) and simultaneous 400-grain migration (`400 × 32 = 12,800` interleaved single-slot reads into a 128-slot `Read` lane) caused `453,853` `hedge_budget_exhausted` events, hedge storms, and `12 s` FIFO recovery stalls.
- **Normative rule ([`storage.md §5`](storage.md#target-97-ledgerreplica), [`wire-catalog.md`](wire-catalog.md#clog-storage-96100)):**
  - **Contiguous `ReadRange` (Target `97`, Method `6`):** `ReadRange(from, below, contiguous_tail, max_slots = 128, max_bytes = 1 MiB)` for `LedgerApplyLoop` catch-up and archive drains, returning consecutive slots packed as `[u32 LE len][bytes]...` unpacked via zero-copy `Bytes::slice`.
  - **Scattered `ReadSlots` (Target `97`, Method `7`):** `ReadSlots(slots: Vec<u64>, contiguous_tail, max_bytes = 1 MiB)` for `ArchiveReader`'s descending key-index recovery waves (one RPC per wave for all distinct `(defining_slot, row_slot)` entries, `~32–64×` fewer RPCs), returning all present ascending slots packed as `[u64 LE slot][u32 LE len][bytes]...` while skipping missing, faulty, or oversized slots.
  - In `LedgerQuorum::read_slots`, query the preferred replica first and fall back to a surviving peer in **one** batched `ReadSlots` RPC for any remaining missing slots (no per-slot hedges; respect closed-ledger read-repair suppression; single-slot fallback only for oversized/backpressure edge cases). Never issue speculative remote hedges against healthy local NVMe.
- **Rust guidance:** Validate strictly ascending unique `slots` and frame bounds (`checked_add`, no panicking slice indexing) before slicing zero-copy `Bytes::slice(pos..end)` views from the single received `packed: Bytes` buffer.
- **Measured impact:** `hedge_budget_exhausted` dropped from `453,853` to `0` across all 3 nodes.

### 10. Bounded & wait-prioritized `LedgerApplyLoop` + batched index commits + closed-ledger read-repair suppression (`v4 → v5 → v6`)

- **Failure mode avoided:** 63 unconstrained catch-up deliveries issuing 1-slot index writes plus `521,340` read-repair WAL writes on already-closed ledgers pegged rejoined nodes at `90%` CPU.
- **Normative rule ([`storage.md §2a`](storage.md#2a-specialized-local-warm-journal-index-st9-wal-less-lsm-tree)):**
  - Bound node-wide `LedgerApplyLoop` scan reads with a shared semaphore (`32` background permits) plus a dedicated **priority scan window (`16` permits)** for ledgers woken by foreground catch-up waiters (`WaitForReplicatedCatchUp` / `LedgerApplyLoop::wake`, covering both open and closed replicated ledgers) ahead of background open-ledger indexing.
  - Add `LedgerApplier::flush()` called at the end of each `LedgerApplyLoop` scan pass; buffer consecutive decided slots per ledger in `JournalStateDatabase` up to `1,024` rows or `1 MiB` and commit them in one atomic `lsm-tree` `WriteBatch` (flushing immediately on either bound, on waiter satisfaction at arrival or during `apply`, or on `flush`/`remove`).
  - Suppress local read-repair recovery writes in `LedgerReader` once a ledger is `Closed(T)` in `LedgerMap` (every slot `< T` is already durable on `Q` replicas under `LP-I7` and draining to drop; open-ledger read repair stays active).
- **Rust guidance:** Use two `tokio::sync::Semaphore`s (`bg_permits = 32`, `priority_permits = 16`) in `LedgerApplyLoop`, trying `priority_permits` first for woken ledgers before falling back to `bg_permits`.

### 11. Fast graceful shutdown cancelling idempotent drains + `30 s` periodic index flush (`v8 → v9`)

- **Failure mode avoided:** Waiting up to `transition_timeout` (`30 s`) in Step 2 of graceful shutdown for large background archive drains to finish exhausted container termination grace periods, starving `JournalStateDatabase::flush()` and `LedgerStore` final checkpoints (`0 / 147` checkpoints written) and forcing gigabyte WAL re-indexing from slot 0 on restart.
- **Normative rule ([`storage.md §2a`](storage.md#2a-specialized-local-warm-journal-index-st9-wal-less-lsm-tree), [`§3`](storage.md#3-concrete-slatedb-archive-clog-archive-c1-st2-st3), [`§6`](storage.md#translating-rapid-viewupdate-to-clog-ledgers-controller-m39), [`§8`](storage.md#graceful-shutdown-order)):**
  - Immediately after Step 1 (`HandOff`) completes, cancel all in-flight background drains (drains are idempotent and will run on surviving replicas or after restart), close admission, and wait at most `min(transition_timeout, 5 s)` for already-admitted transitions and prompt drain cancellation unwind (`DrainsEnded`).
  - Follow with a bounded (`5 s`) `JournalStateDatabase::flush()` (draining buffered replicated slot batches before flushing durable `lsm-tree` SSTs) and final `LedgerStore` checkpoints.
  - Default the warm index's periodic `flush_interval = 30 s` (alongside the `64 MiB` memtable budget and clean-shutdown flush; optionally `None` to disable) so hard crashes (`SIGKILL` / VM power-off) bound unindexed WAL replay to at most `~30 s` with negligible steady-state cost.
  - Pace `max_concurrent_drains = 2` per node (`+1` reserved exposed `OR-I23` slot = `3` max) and `max_concurrent_transitions = 2` for routine balancing (`32` for departures, `64` for repairs).
- **Rust guidance:** Pass a dedicated `drain_cancel: CancellationToken` into `SlateDbDrainWorker` separate from the transition-execution token so shutdown Step 3 cancels drains instantaneously while preserving the budget for bounded `tokio::time::timeout(Duration::from_secs(5), ...)` index and checkpoint flushes.
- **Measured impact:** Graceful stop dropped from `31 s` (`0 / 147` checkpoints) to `< 1.5 s` (`147 / 147` checkpoints).

### 12. Persisted `LedgerSegmentView` snapshots + `CatchUpReady` balance gate (`v10 → v11`)

- **Failure mode avoided:** With `128 MiB` open segments (~`40k` slots each), restarting `LedgerApplyLoop` with an empty `LedgerSegmentView` forced `LedgerReader::scan` to rewind to slot `0` (~`8 GiB` across 63 ledgers = `10–13.8 s` stall) just to rebuild a `~50 KiB` key-ordinal dictionary, while `ClusterController::balance_primaries` immediately migrated `21` hot ledgers onto the warming node and blocked grains inside `WaitForReplicatedCatchUp`.
- **Normative rule ([`storage.md §2a`](storage.md#2a-specialized-local-warm-journal-index-st9-wal-less-lsm-tree), [`§5`](storage.md#target-99-clustercontrol--target-100-nodecontrol), [`§6`](storage.md#translating-rapid-viewupdate-to-clog-ledgers-controller-m39)):**
  - Persist optional, rebuildable CRC-32C-checked `LedgerSegmentView` snapshots (`segment_start`, `last_slot`, `(ordinal, defining_slot, key_bytes)`) atomically alongside each ledger's watermark in the local `lsm-tree` warm index (`load_segment_view` / `save_segment_view`), saved periodically every `15 s` when advancing and at the exact final watermark on graceful shutdown, and deleted on `drop_ledgers`. On restart, a restored snapshot at `watermark - 1` resumes at `watermark` with **zero rewind**; an earlier in-segment snapshot after a hard crash resumes at `last_slot + 1`; an absent, mismatched, or corrupt snapshot safely falls back to the `LG-I13` full segment rewind.
  - Track `LedgerApplyLoop::is_initial_catch_up_complete` across all ledgers on the node listed at startup until each completes its first catch-up pass (`Reached`/`Dropped`) or leaves the map, reporting `catch_up_ready` in `NodeReport`. Gate **only** routine `BalancePrimaries` on `catch_up_ready` (triggering a controller pass immediately when `catch_up_ready` flips `false → true`), while keeping urgent `Departure`, `Takeover`, `Split`, `Roll`, and `Repair` transitions immediately eligible (`OR-I5`).
- **Rust guidance:** Serialize `LedgerSegmentView` snapshots with a trailing `crc32c` and store them in the same `lsm-tree` `WriteBatch` as watermark flushes; keep `LedgerApplyLoop::is_initial_catch_up_complete` as an `AtomicBool` paired with a `Notify` that triggers an immediate `NodeReport` on transition to `true`.
- **Measured impact:** Rejoin recovery max latency dropped from `13,875 ms` to `659 ms`, with `0` timeouts and `0` zero-op seconds across both graceful stop+rejoin and hard crash+rejoin.

### 13. Linux `io_uring` networking + WAL implementation cautions (`v8`)

- **Failure modes avoided:**
  1. Issuing `io_uring` socket send SQEs without `MSG_NOSIGNAL` raises `SIGPIPE` on peer crashes/disconnects, terminating the process or forcing global signal-mask workarounds.
  2. Skipping completion callbacks for `IORING_OP_ASYNC_CANCEL` CQEs on non-multishot operations leaks operation slots when a multishot disconnect races an in-flight cancel, hanging connection teardown and graceful shutdown.
- **Normative rule ([`messaging.md §N14/N15`](messaging.md#review-notes), [`storage.md §1`](storage.md#runtime-and-fail-stop-discipline)):**
  - Whenever Linux `io_uring` networking is enabled in `roux-net::transport`, set **`MSG_NOSIGNAL`** on every socket send SQE (`IORING_OP_SEND` / `IORING_OP_SENDMSG` / `IORING_OP_SEND_ZC`).
  - Ensure CQE completion draining invokes cancel-operation completion handlers (`IORING_OP_ASYNC_CANCEL`) for non-multishot operations so disconnected multishot sockets never leak slab/operation slots or hang shutdown.
  - Pair end-to-end zero-copy `io_uring` networking with `O_DIRECT` `io_uring` WAL (`clog-wal`) using shared `bytes::Bytes` / `AlignedBuf`.
- **Rust guidance:** In any `io-uring`-based transport or WAL driver, enforce `MSG_NOSIGNAL` in the single SQE builder helper (tested by killing a connected peer mid-send in Linux CI) and track every submitted `user_data` slot in a slab asserted empty at ring teardown (`testing.md` Linux `io-uring` fault suite).
- **Measured impact:** End-to-end zero-copy `io_uring` networking + `O_DIRECT` `io_uring` WAL sustained **`173,108 durable ops/s`** (128 B) and **`1,691 MiB/s` (~`1.65 GiB/s`) aggregate 3×-replicated physical WAL** across 3 `Standard_L8s_v3` nodes at ~`60%` CPU.
