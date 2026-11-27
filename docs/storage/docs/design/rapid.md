# Rapid Consensus and Failure Detection (`roux-membership`)

Status: **Decided** (stand-alone specification for Rapid inside `roux-membership`).

Roux's L3 Membership ([Membership](membership.md)) commits every membership view durably through `clog-metastore`'s conditional `membership` register (`StoredView`), and uses **Rapid** (the USENIX ATC '18 protocol as implemented in RapidCluster, ported to pure sans-I/O Rust) for scalable failure detection, multi-process cut aggregation, and leaderless 1-RTT view-change agreement.

This document specifies Rapid's internal algorithms—expander monitoring, cut detection, and single-decree Fast/Classic Paxos per configuration—and how they compose with Roux's durable `membership` register. Formal models live in [`specs/rapid/`](../../specs/rapid/README.md) (Rapid cut detection and consensus) and [`specs/membership/`](../../specs/membership/README.md) (durable view log and self-fencing).

## 1. Role of Rapid inside L3 Membership

In Roux, Rapid proposes candidate next membership entries (`JoinBatch`, `MemberBatch`, `CutBatch`) for a given configuration version `v`:

- **Safety authority:** `clog-metastore`'s conditional register (`StoredView` at version `v`) is the ultimate linearizable arbitrator. Even if a partitioned minority or stale configuration somehow ran consensus, its conditional write at version `v` fails with a version mismatch.
- **Liveness and stability authority:** Rapid ensures that in steady state, healthy members aggregate concurrent joins and failures into the *same* multi-process cut and agree in **1 RTT** via Fast Paxos without a single bottleneck leader, falling back to Classic Paxos only on fast-round conflicts or > `f` failures.

## 2. Monitoring overlay (K-ring expander graph)

For a cluster view of size `N` (`N >= 2`):

- **Effective degree:** `K_eff = min(K, N - 1)` where default `K = 10`.
- **Deterministic ring construction:** For each ring `r in 0..K_eff`, all members are sorted by `hash(r, member_id)` (deterministic across every process holding view `v`).
- **Observer / subject edges:** On ring `r`, a member's predecessor is its **observer** and its successor is its **subject**. Each member therefore monitors `K_eff` subjects and is monitored by `K_eff` observers, forming a pseudo-random `K_eff`-regular expander graph with `O(K)` probing overhead per node regardless of cluster size.
- **Edge probing:** Observers probe subjects via L1 `roux.membership.v1` (`ProbeMessage` / `ProbeResponse`, `Lane::High`, carrying sender's `view_version` for bidirectional stale-view detection). Defaults: `probe_interval = 1s`, `probe_timeout = 500ms`, `failure_threshold = 3` consecutive misses.
- **External liveness hints (`M39`):** `roux-storage` may call `report_unresponsive(member_id)` when a handoff stalls; Rapid feeds this into the local edge detector as an accelerated suspicion signal (liveness hint only; never bypasses cut detection or consensus).
- **Alert emission:** When observer `src` detects subject `dst` down on ring(s) `R`, it broadcasts `AlertMessage { edge_src: src, edge_dst: dst, status: Down, view_version: v, rings: R }` (batched in `BatchedAlertMessage`). Joiners similarly trigger `status: Up` alerts from their prospective observers for view `v`.

## 3. Multi-process cut detector

Each member feeds validated `AlertMessage`s for the current view `v` into a deterministic sans-I/O `CutDetector`:

### Effective thresholds

Given configured `(K, H, L)` (defaults `K = 10, H = 9, L = 3`) and current view size `N`:

- If `N <= 1`: monitoring disabled (`H_eff = L_eff = 0`).
- `K_eff = min(K, N - 1)`.
- **Simple mode (`K_eff < 3`):** `H_eff = L_eff = K_eff`. Any subject reaching `K_eff` distinct ring reports triggers immediately (no unstable deferral window).
- **Watermark mode (`K_eff >= 3`):**
  - If `K_eff == K`: `H_eff = H`, `L_eff = L`.
  - Otherwise scale proportionally: `H_eff = min(K_eff - 1, max(1, ceil(K_eff * H / K)))`, `L_eff = min(H_eff, max(1, floor(K_eff * L / K)))`.
  - Invariant: `K_eff > H_eff >= L_eff >= 1`.

### Aggregation and stability rule

For each subject `dst`, the detector deduplicates alerts by `(dst, ring_number)` (at most one report per ring, ignoring duplicate alerts):

1. When `reports[dst].len()` reaches `L_eff`: `dst` enters the **unstable (`pre_proposal`)** set and `updates_in_progress` increments.
2. When `reports[dst].len()` reaches `H_eff`: `dst` moves from `pre_proposal` to the **stable (`proposal`)** set and `updates_in_progress` decrements.
3. **Cut emission:** A proposal is emitted **only when `updates_in_progress == 0`** and `proposal` is non-empty. All subjects in `proposal` are emitted together as one multi-process cut batch and `proposal` is cleared.

This watermark barrier prevents partial, conflicting cuts during correlated failures (e.g., rack switch outage) and causes almost all honest nodes to propose the exact same cut to Fast Paxos.

### Implicit edge invalidation & unstable timeout

- **Correlated observer failure (`invalidate_failing_edges`):** If any `Down` alerts have been seen and a subject `s` in `pre_proposal` has an observer `o` on ring `r` that is itself in `proposal` or `pre_proposal`, `o` may have crashed before reporting `s`. The detector synthesizes an implicit alert `(o, s, ring r)` so `s` is not stranded between `L_eff` and `H_eff`.
- **Unstable-mode timeout (`force_promote_unstable`):** If `updates_in_progress > 0` persists past the unstable timeout (e.g., gray failure where some observers never report), remaining `pre_proposal` nodes are promoted into `proposal` and emitted so liveness is never stalled.
- **View update (`update_view`):** On installing view `v + 1`, completed joins and departed members are purged; if `K_eff` changed, all ring reports are cleared because ring assignments changed.

## 4. Single-decree Fast + Classic Paxos per configuration

For each view version `v` (configuration of size `N`), members run one single-decree consensus instance (`ConsensusCoordinator`) to agree on the next entry for `v + 1`.

### Ranks and thresholds

- **Rank:** `(round: u32, node_index: u32)` ordered lexicographically.
  - **Fast round:** `Rank { round: 1, node_index: 0 }` (shared by all members).
  - **Classic rounds:** `Rank { round >= 2, node_index: member.paxos_index }` where `paxos_index` is a unique monotonic ID assigned at join (`MemberId` sequence).
- **Thresholds** for view size `N`:
  - `f = floor((N - 1) / 4)`
  - **Fast quorum:** `Q_fast = N - f`
  - **Classic quorum (majority):** `Q_classic = floor(N / 2) + 1`
  - Note that `Q_fast + Q_classic - N = Q_classic - f > f` for all `N >= 1`, so any classic quorum intersects any fast quorum in strictly more than `f` acceptors.

### Fast round (Round 1 — 1 RTT)

1. When `CutDetector` outputs a cut (or a member proposes a `JoinBatch` / `MemberBatch`), the member constructs a `Proposal` for `v + 1`, records its own local fast vote in `PaxosAcceptor` (`promised = accepted = (1, 0)`, `value = proposal`, only if `promised.round <= 1`), and broadcasts `Phase2b(v, (1, 0), proposal)` to all members of view `v`.
2. Each coordinator counts distinct senders per proposal in round 1:
   - **Fast decision:** If any proposal reaches `Q_fast = N - f` distinct votes, decide immediately in 1 RTT!
   - **Early fallback:** Track `remaining = N - distinct_voters - delivery_failures`. If no observed proposal can still reach `Q_fast` (`max_votes + remaining < Q_fast`), immediately abandon round 1 and start classic round 2 without waiting for timeout.
   - **Timeout fallback:** If the randomized round-1 timer fires before deciding, start classic round 2.

### Classic Paxos fallback (Rounds 2, 3, ...)

1. **Phase 1a (`Prepare`):** Coordinator chooses `rank = (max_seen_round + 1, my_node_index)` (starting at `round = 2`), resets round accumulators, broadcasts `Phase1a(v, rank)`, and processes it locally on its own `PaxosAcceptor`.
2. **Acceptor (`Phase1a -> Phase1b`):** If `rank > promised`, update `promised = rank`. Always reply `Phase1b(v, promised, accepted_rank, accepted_value)` so lower proposers learn `promised` and advance.
3. **Value selection (`choose_value`):** Once `Q_classic` matching `Phase1b` replies (`reply.promised == rank`) arrive:
   - Let `max_vrnd = max(reply.accepted_rank)` across the quorum.
   - Collect non-empty proposals with `reply.accepted_rank == max_vrnd`.
   - If all collected proposals are identical (always true when `max_vrnd.round >= 2` or unanimous round 1): pick that proposal.
   - If round-1 conflict (`max_vrnd == (1, 0)` with multiple distinct values): pick the proposal with **maximum frequency** at `max_vrnd` (equivalently, any proposal with `count > f` / `count > N / 4` — uniquely guaranteed to be the fast-round winner if one existed; deterministic tie-break by canonical proposal ordering when no value could have won round 1).
   - If no acceptor accepted any value (`max_vrnd == (0, 0)`): propose this coordinator's own cut proposal.
4. **Phase 2a (`Accept`) & Acceptor (`Phase2a -> Phase2b`):**
   - Coordinator broadcasts `Phase2a(v, rank, chosen_value)`.
   - Acceptor accepts if `rank >= promised` (idempotent if `rank == accepted_rank`): sets `promised = accepted_rank = rank`, `accepted_value = chosen_value`, and broadcasts `Phase2b(v, rank, chosen_value)`. If `rank < promised`, replies directly to sender with a NACK `Phase2b(v, promised, None)`.
5. **Learner & NACK handling:**
   - `PaxosLearner` ignores round-1 messages; for any classic `rank` (`round >= 2`), once `Q_classic` distinct senders vote `Phase2b(v, rank, value)` with non-empty `value`, it decides `value`.
   - If a coordinator receives enough NACKs or delivery failures that `N - negative_voters < Q_classic`, it schedules a jittered classic restart at `max_seen_round + 1`.

## 5. Commit to `clog-metastore` and view sync

- Once `ConsensusCoordinator` decides `entry` for view `v + 1`, the deciding member validates it against view `v` and performs a conditional write of `StoredView(v + 1)` to `clog-metastore`'s `membership` register (expected version `v`).
- All members either observe the write commit (CAS success or version-mismatch followed by reading `StoredView(v + 1)`), broadcast the committed view notification, or catch up via `MembershipViewRequest` / `MembershipViewResponse` (triggered when any `ProbeResponse` or L1 frame header reports a higher `view_version`).
- A process **never publishes** view `v + 1` locally until it is confirmed durable in `clog-metastore` (`C2`, checked in `specs/membership/membership.qnt`).
- When `clog-metastore` is backed by `clog-caspaxos`, seed voters (`{cluster:N}/caspaxos-config`, see [Fast CASPaxos](fast-caspaxos.md#2-voter-reconfiguration)) are distinct from Rapid cluster members. During a seed-voter reconfiguration's brief Fence→Activate pause, `{cluster:N}/membership` register commits wait and retry until Activate, while already-published views and L2 ledger-replica leases (`ST8`) continue uninterrupted.

## 6. Wire messages (`roux.membership.v1`, Targets `16`–`17`)

All Rapid messages live in `roux.membership.v1` (`prost` + `protox`) over L1 Messaging (`Lane::High`):
- `Probe` / `ProbeResponse` (edge liveness + bidirectional `view_version` check)
- `BatchedAlert` (`Alert { edge_src, edge_dst, status, view_version, rings }`)
- `Phase1a`, `Phase1b`, `Phase2a`, `Phase2b` (single-decree Fast/Classic Paxos scoped to `view_version`)
- `PreJoin` / `Join` / `JoinResponse` (two-phase join targeting `view_version`)
- `Leave` (graceful departure trigger)
- `ViewSyncRequest` / `ViewSyncResponse` (catch-up of committed `StoredView`)

## 7. Verification

- **Formal model:** [`specs/rapid/rapidConsensus.qnt`](../../specs/rapid/rapidConsensus.qnt) checks `CutDetector` watermark aggregation, implicit edge invalidation, and Fast + Classic Paxos single-decree agreement (`agreement`, `validity`, `fastRecoverySafe`, `cutStability`) plus mutants that must violate each invariant.
- **Durable membership composition:** [`specs/membership/membership.qnt`](../../specs/membership/membership.qnt) verifies `StoredView` durability, no resurrection, and self-fencing (`C1`–`C3`, `C6`). Per-ledger ownership leases live in L2 Clog and are verified in [`specs/range-handoff/rangeHandoff.qnt`](../../specs/range-handoff/rangeHandoff.qnt).
- **Production verification:** `stateright` BFS + `Bug` mutants over the sans-I/O `roux-membership` state machine and `turmoil` multi-node partition/flapping/join/leave simulations.
