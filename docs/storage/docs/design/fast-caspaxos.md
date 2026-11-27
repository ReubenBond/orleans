# Fast CASPaxos (`clog-caspaxos`)

Status: **Decided** (stand-alone specification for `clog-caspaxos`, including voter reconfiguration).

`clog-caspaxos` implements a linearizable, replicated, rewritable register (`MetaStore`, `MS-I1`–`MS-I5`) for object-store-free, disk-native Roux clusters over L1 Messaging Target `96` (`MetaStoreCasPaxos`). Its safety properties — including operator-driven voter reconfiguration — are formally checked in [`specs/fast-caspaxos/`](../../specs/fast-caspaxos/README.md).

## 1. Protocol overview

Fast CASPaxos blends **CASPaxos** (piggybacked prepare on accept for 1-RTT steady-state updates with compact per-register metadata) with **Fast Paxos** (shared fast ballots allowing any proposer to commit uncontended updates in 1 RTT without acquiring leadership, falling back to classic ballots on contention or fast-quorum loss) and **Vertical Paxos I voter reconfiguration** (configuration-qualified ballots, an acceptor-wide monotonic floor, and a configuration register).

### Ballots and quorums

- **Configuration-qualified ballot:** lexicographically ordered triple `(configuration: u64, round: u64, proposer_id: u32)`:
  - `(0, 0, 0)` is the **zero ballot** (unaccepted state or absent piggybacked `next_ballot`), strictly less than every non-zero ballot.
  - `(c, 0, 0)` (`c ≥ 1`) is a **floor ballot**, fencing all configurations `< c` atomically across every key. Floor ballots never carry an accepted value.
  - `(c, r, 0)` (`c ≥ 1, r ≥ 1`) is a **shared fast ballot** in configuration `c` that any proposer may propose into.
  - `(c, r, o)` (`c ≥ 1, r ≥ 1, o ≥ 1`) is a **classic ballot** exclusively owned by proposer `o` in configuration `c`.
- **Initial implicit promise:**
  - In bootstrap configuration `c = 1`, acceptors initialize every absent key with implicit `floor = (1, 0, 0)`, `promised = (1, 1, 0)`, `accepted = (0, 0, 0)`, `value = None`. Register creation at genesis commits in 1 RTT via a direct fast-round `Accept` at `(1, 1, 0)` with no `Prepare`.
  - In every configuration `c > 1`, `(c, 1, 0)` is **not** implicitly prepared. A key's first ballot in `c > 1` is always a classic ballot `(c, r ≥ 1, o ≥ 1)` — either the hand-off ballot during **Install** or a standard classic `Prepare` + `Accept` for a key first created after **Activate**. That classic accept may piggyback `(c, r + 1, 0)` to establish 1-RTT fast writes for subsequent operations.
- **Per-configuration voter sets and quorums:** Each configuration `c ≥ 1` defines a non-empty finite set of distinct voter identities `V(c)` of size `N(c) = |V(c)| ≥ 1`:
  - `classic_quorum(c) = floor(N(c) / 2) + 1`
  - `fast_quorum(c) = ceil(3 * N(c) / 4)` (satisfies `2 * fast_quorum(c) > N(c)` and `2 * fast_quorum(c) + classic_quorum(c) > 2 * N(c)`, ensuring every classic quorum of `V(c)` sees any fast-committed value strictly more often than any competing value).
  - `quorum(b) = fast_quorum(b.configuration)` if `b.proposer_id == 0`, else `classic_quorum(b.configuration)`.
  - Votes and piggybacked promise certificates for ballot `b` count **strictly** from voters in `V(b.configuration)`.

| Seed voters `N(c)` | Fast quorum `ceil(3N/4)` | Classic quorum `floor(N/2)+1` | Fault tolerance |
| --- | ---: | ---: | --- |
| 3 | 3 | 2 | 0 fast / 1 classic |
| 4 | 3 | 3 | 1 fast / 1 classic |
| 5 | 4 | 3 | 1 fast / 2 classic |

### Acceptor state machine

Acceptors track neither configuration membership nor configuration completion; they enforce only monotonic ballots and a single acceptor-wide durable floor. Each acceptor durably stores in SQLite (`caspaxos/acceptor.db`):

- **Acceptor floor:** singleton `floor = (c_floor, 0, 0)` with `c_floor ≥ 1` (initialized to `(1, 0, 0)` for bootstrap voters and to `(c_join, 0, 0)` for a voter first introduced in `c_join > 1`).
- **Per-key state:** `(promised[key], accepted[key], value[key])`, defaulting to `((0, 0, 0), (0, 0, 0), None)` for absent keys (with implicit `promised[key] = (1, 1, 0)` at bootstrap `c = 1`).
- **Effective promise:** `eff_promised(key) = max(promised[key], floor)` uniformly for every key, present or absent.

Acceptor operations:

1. **`SetFloor(target_floor)`:**
   - Validate `target_floor = (c, 0, 0)` with `c ≥ 1`.
   - Atomically persist and sync `floor = max(floor, target_floor)` and return the durable `floor`. Monotonic, idempotent, and uniform across all present and absent keys.
2. **`Prepare(key, ballot)`:**
   - Validate `ballot` is a classic ballot `(c ≥ 1, r ≥ 1, o ≥ 1)`.
   - Compute `gate = max(eff_promised(key), accepted[key])`.
   - If `ballot < gate`, reject (`Nack`) returning `conflicting_ballot = gate`, `accepted[key]`, and `value[key]`. When `floor` causes the refusal, `gate` carries `floor` (`configuration ≥ c_floor`), signaling stale proposers to refresh their configuration.
   - Otherwise persist and sync `promised[key] = ballot` and return `Promise(accepted[key], value[key])`.
3. **`Accept(key, ballot, value, next_ballot)`:**
   - Validate `ballot` is a fast or classic ballot (`c ≥ 1, r ≥ 1`), and `next_ballot` is either `(0, 0, 0)` or a valid ballot `(c, r + 1, o')` in the same configuration `c` (`o' = 0` when `o = 0`, or `o' ∈ {0, o}` when `o > 0`).
   - **Exact-duplicate replay (read-only):** If `ballot == accepted[key] && value == value[key]`, reply `Ack(promised = eff_promised(key))` without mutating durable state. Because this adds no new vote and echoes `eff_promised(key) ≥ floor`, it preserves idempotence and never certifies a piggybacked promise below `floor`.
   - **Fencing:** Compute `gate = max(eff_promised(key), accepted[key])`. Reject (`Nack(conflicting_ballot = gate)`) if `ballot < gate` or `ballot == accepted[key]` (first-write-wins for conflicting values on the same fast ballot). Checking both `eff_promised` and `accepted` fences acceptors that received an `Accept` without a prior `Prepare`.
   - **Commit locally:** Atomically persist and sync `accepted[key] = ballot`, `value[key] = value`, and `promised[key] = max(promised[key], next_ballot)`, then reply `Ack(promised = max(promised[key], floor))`.
4. **`Read(key)` and `ListKeys(prefix, after_key, limit)`:**
   - Both operations are **ballot-free** and remain available when `floor` is raised.
   - `Read(key)` returns a consistent committed local snapshot of `(eff_promised(key), accepted[key], value[key])` (or absence).
   - `ListKeys` enumerates all keys with `accepted[key] != (0, 0, 0)` in deterministic ordinal key order, each with its committed `(eff_promised(key), accepted[key], value[key])`.

### Proposer state machine

Each proposer tracks its active configuration `(c, V(c))` and issues ordinary register operations (`read`, `create`, `compare_and_swap`) only when `K_cfg` for `c` is `active` (or at bootstrap `c = 1`). Proposers send `Accept(b, ...)` strictly to `V(b.configuration)` (`onlyVotersAccept`) and count acknowledgments and piggybacked promise certificates strictly from `V(b.configuration)`.

Every conditional `MetaStore` operation is a pure state-transition function `f(Option<VersionedValue>) -> TransitionResult`:

1. **Fast/prepared attempt:** If the proposer holds a prepared ballot `b` in active configuration `c` (initially `(1, 1, 0)` in `c = 1`, or learned from a prior piggybacked `next_ballot` quorum certificate in `V(c)`) and knows base state `v`, compute `v' = f(v)` and broadcast `Accept(b, v', next_b)` to `V(c)`.
   - If `quorum(b)` voters in `V(c)` `Ack`: return the committed `v'`, caching the `next_b` certificate when `quorum(next_b)` voters in `V(c)` echoed `promised == next_b`.
   - Otherwise (conflict, `Nack`, or timeout): if any refusal carries `conflicting_ballot.configuration > c` (or a duplicate ack echoes `promised.configuration > c`), run stale-configuration recovery below; otherwise advance to classic recovery at `(c, max_seen_round + 1, proposer_id)`.
2. **Classic recovery (`Prepare` + `Accept`):**
   - Allocate monotonic `(c, round)` from durable `caspaxos/proposer.db` before sending `Prepare` (or any classic `Accept`), forming `b = (c, round, proposer_id)`.
   - Broadcast `Prepare(key, b)` to `V(c)` and collect `classic_quorum(c)` `Promise` replies (`q ⊆ V(c)`). (During Vertical Paxos I hand-off for a pending `c + 1`, Phase 1 collects from `V(c) ∪ V(c + 1)` per [§2.2](#22-vertical-paxos-i-hand-off-and-completion).)
   - **Configuration-scoped value recovery (`recover_value`):** Let `min_relevant_cfg = b.configuration` when `b.configuration` is `active` (complete), and `b.configuration - 1` during hand-off. Find `highest = max(r.accepted)` across `q`:
     - If `highest == (0, 0, 0)`: recovered state is `None`.
     - If `highest.configuration < min_relevant_cfg`: because `min_relevant_cfg` is already complete and `q` contains a classic quorum of `V(min_relevant_cfg)`, any value chosen in `< min_relevant_cfg` was already transferred to a quorum intersecting `q`. Thus `highest` is an unchosen remnant with zero viable fast winners (`HasConflict = false`); deterministically pick any value at `highest` (or treat it as unaccepted) with no historical `V(highest.configuration)` lookup.
     - Otherwise (`hCfg = highest.configuration ≥ min_relevant_cfg`), filter `top = { r in replies(b, q) : r.accepted == highest && r.acceptor in V(hCfg) }`:
       - If `highest.proposer_id > 0` (classic): all replies in `top` agree; recover that value.
       - If `highest.proposer_id == 0` (fast): tally frequencies over `top` (members of `V(hCfg)` only) and compute viability strictly over `V(hCfg)` (`seen_in_hcfg = |{ r in replies(b, q) : r.acceptor in V(hCfg) }|`, `remaining_in_hcfg = |V(hCfg)| - seen_in_hcfg`, candidate `v` viable iff `count(v) + remaining_in_hcfg >= fast_quorum(hCfg)`). Select a maximum-frequency value in `top`.
   - Apply `v' = f(recovered)` (for a linearizable read or failed precondition, `v' = recovered` — an identity update — is driven through `Accept` to establish a durable read barrier) and broadcast `Accept(b, v', next_b)` to `V(b.configuration)`.
3. **Adaptive mode:** Proposers default to `fast` mode (`next_ballot = (c, round + 1, 0)`), and may switch to `classic` mode (`next_ballot = (c, round + 1, proposer_id)`) under sustained contention, returning to `fast` once contention subsides.
4. **Stale-configuration recovery:** When a proposer operating in configuration `c` receives a refusal carrying `conflicting_ballot.configuration > c` (or a duplicate ack echoing `promised.configuration > c`):
   - Clear cached prepared ballots and in-flight phase state for `c`.
   - Issue ballot-free `Read({cluster:N}/caspaxos-config)` across reachable voters of `V(c)` (waiting for `classic_quorum(c)` responses). Treat returned `ConfigurationRecord`s with `number > c` as discovery hints ordered from highest `number` downward (falling back to `c + 1` if a higher candidate is unreachable).
   - Query ballot-free `Read({cluster:N}/caspaxos-config)` on the candidate voter set `V(c_cand)` and follow the chain until reaching the highest reachable configuration `c_cur` (falling back to operator-updated discovery endpoints if all `V(c)` voters were decommissioned).
   - If `K_cfg` in `V(c_cur)` is `pending`, reconfiguration is in progress: wait/retry or return transient unavailability without issuing ordinary operations in `c_cur`.
   - Adopt `(c_cur, V(c_cur))` only after confirming `(c_cur, V(c_cur), active)` across a `classic_quorum(c_cur)` of `V(c_cur)`, then retry or reconcile according to normal `Ambiguous`/retry semantics.

### Mapping to `MetaStore` (`MS-I1`–`MS-I5`)

- Each `MetaStore` register key (`{cluster:N}/caspaxos-config`, `{cluster:N}/ledger-map`, `{cluster:N}/node-registry`, `{cluster:N}/membership`) maps to one Fast CASPaxos key holding `(etag: u64, payload: Bytes)`.
- **Create-only:** `f(None) -> Some((1, payload))`; `f(Some(_)) -> Conflict` (identity commit or precondition failure).
- **Conditional write (`expected_etag`):** `f(Some((e, _))) if e == expected_etag -> Some((e + 1, payload))`; otherwise `Conflict`. Every applied write strictly increments `etag` (`MS-I5`).
- **Ambiguous outcomes (`MS-I3`):** A timeout or transport drop after `Accept` dispatch leaves the outcome `Ambiguous`; the caller reconciles by reading the register.

## 2. Voter reconfiguration

`clog-caspaxos` supports operator-driven reconfiguration of its seed voter set (replacing a lost voter, adding a voter, removing a voter, or migrating to a disjoint voter set) through the configuration register, the acceptor floor, and sequential Vertical Paxos I hand-off.

### 2.1 Configuration register (`K_cfg`)

The reserved register key `K_cfg = {cluster:N}/caspaxos-config` stores:

```text
ConfigurationRecord = (number: u64, voters: Set<VoterDescriptor>, state: pending | active)
```

- `number` starts at `1` (bootstrap) and increments by exactly `1` on each reconfiguration (`c → c + 1`), providing a total order over configurations.
- `voters` is the canonical set of voter descriptors for `number`, each naming a unique durable `AcceptorId` (`VoterId`) and its L1 endpoint metadata.
- `state` is `pending` from **Choose** through **Install** and transitions to `active` upon **Activate**.
- At genesis (`c = 1`), an absent `K_cfg` in `V(1)` with no voter reporting `floor > (1, 0, 0)` represents `(1, V(1), active)` by bootstrap agreement. All mutations to `K_cfg` (**Choose** and **Activate**) use classic ballots (`Prepare` + `Accept`, never fast ballots).

### 2.2 Vertical Paxos I hand-off and completion

Reconfigurations advance sequentially: configuration `c + 1` begins only after configuration `c` is `active` (complete for every key). While `c + 1` is `pending`, hand-off recovery for any key `k` uses a classic ballot `b = (c + 1, r ≥ 1, proposer_id > 0)`:

- **Phase 1 (`Prepare`):** Broadcast `Prepare(k, b)` to `V(c) ∪ V(c + 1)` and wait for promises from `q ⊆ V(c) ∪ V(c + 1)` satisfying **both** `|q ∩ V(c)| ≥ classic_quorum(c)` and `|q ∩ V(c + 1)| ≥ classic_quorum(c + 1)`. Voters in `V(c) ∩ V(c + 1)` answer a single `Prepare` RPC that counts toward both quorums. Intersecting both quorums in Phase 1 serializes racing hand-off installs with zero extra round-trips and handles 1-step voter replacement, add-one, remove-one, and full disjoint migrations uniformly.
- **Recovery:** Compute `base = recover_value(b, q)` with `min_relevant_cfg = c` per [§1](#proposer-state-machine).
- **Phase 2 (`Accept`):** Broadcast the identity transition (`value = base`) to `V(c + 1)` only and wait for `classic_quorum(c + 1)` acknowledgments from `V(c + 1)`.
- **Dual per-key completion:** Once `c` is complete, key `k` is complete in `c + 1` when either (a) a ballot of `c + 1` is chosen for `k` on a quorum of `V(c + 1)` (**transferred**), or (b) a classic quorum `F ⊆ V(c)` holds durable `floor ≥ (c + 1, 0, 0)` with `accepted[k] == (0, 0, 0)` (**vacuously complete**). Configuration `c + 1` is complete once every key is complete in `c + 1`.

### 2.3 Four-step idempotent operator procedure

All four steps are idempotent, resumable after any operator, voter, or network crash, and safe under concurrent operators:

1. **Choose:** With `c` active, fresh never-previously-used `AcceptorId`s for new voters, and non-empty `V(c + 1)`, run classic `compare_and_swap` on `K_cfg` in `c`: `(c, V(c), active)` → `(c + 1, V(c + 1), pending)` (`expected_etag = None` if `c = 1` and `K_cfg` is absent). Linearizability of `K_cfg` picks at most one `V(c + 1)`; a racing loser sees `Conflict` and observes the winner. On `Ambiguous` after crash, read `K_cfg` in `c` (or follow stale-configuration recovery if a concurrent operator already ran Step 2).
2. **Fence `c` (and prepare new voters):** Initialize every new voter `a ∈ V(c + 1) \ V(c)` with durable `floor = (c + 1, 0, 0)`, identity binding, and majority admission before it serves protocol RPCs. Send `SetFloor((c + 1, 0, 0))` to all reachable voters of `V(c)` until a classic quorum `F ⊆ V(c)` durably acknowledges `floor ≥ (c + 1, 0, 0)`. Every quorum of `V(c)` intersects `F`, fencing all new prepares, new accepts, and piggybacked certificates of `c`, while any in-flight ballot of `c` that could still form a quorum is already durably present on at least one member of `F`.
3. **Install:** Run `ListKeys` to exhaustion on every voter `a ∈ F`, forming `Keys = ⋃_{a ∈ F} ListKeys(a)` (`K_cfg ∈ Keys` because `F` intersects Step 1's commit quorum). For every `k ∈ Keys` (transferring `K_cfg` last as operational hygiene), run Vertical Paxos I identity hand-off at a classic ballot `(c + 1, r ≥ 1, proposer_id > 0)` preserving the key's logical `etag`. Every key not in `Keys` is vacuously absent across `F`.
4. **Activate:** In configuration `c + 1` (using `V(c + 1)` quorums only), run classic `compare_and_swap` on `K_cfg`: `(c + 1, V(c + 1), pending)` → `(c + 1, V(c + 1), active)`. Stale proposers discovering `c + 1` now confirm `active` and resume normal 1-RTT / classic operations. Retired voters (`V(c) \ V(c + 1)`) may now be safely decommissioned.
   - **Crash/resume rule:** On any crash or resume where `K_cfg` is still `pending` (whether observed in `V(c)` or `V(c + 1)`), the operator resumes from **Step 2 (Fence `c`) → Step 3 (Install all `Keys`) → Step 4 (Activate)** so a crash after transferring `K_cfg` never activates `c + 1` with untransferred keys.

### 2.4 Availability, fence-quorum retention, and fault tolerance

- **Pause scope:** `MetaStore` mutations and linearizable reads pause between **Fence `c`** (Step 2) and **Activate** (Step 4) — bounded by `ListKeys` plus one classic RTT per MetaStore key (typically a handful of keys: `caspaxos-config`, `ledger-map`, `node-registry`, `membership`). Data-plane operations (ledger appends, hot reads, in-flight grain turns, already-published Rapid views, and L2 ledger-replica leases `ST8`) continue uninterrupted; Rapid `{cluster:N}/membership` view commits wait and retry until Activate.
- **Fence-quorum retention:** A classic quorum `F ⊆ V(c)` with durable `floor ≥ (c + 1, 0, 0)` must remain available and retain its durable state until **Activate** commits in `c + 1`. Do not wipe or decommission any voter of `V(c)` until Step 4 commits.
- **Fault tolerance during reconfiguration:** Let `f_lost` be the number of voters in `V(c)` already lost when reconfiguration begins and `f_max(c) = N(c) - classic_quorum(c) = floor((N(c) - 1) / 2)`:
  - In `V(c)`: tolerates at most `f_max(c) - f_lost` additional voter losses before **Activate** (e.g., `N = 3, f_lost = 1` tolerates `0` additional losses; `N = 5, f_lost = 1` tolerates `1` additional loss by re-running Step 2 to fence the remaining survivor into a new classic quorum `F'` and re-running Step 3 over `F'`).
  - In `V(c + 1)`: requires at least `classic_quorum(c + 1)` voters available to accept **Install** and **Activate**.

### 2.5 Normative invariants (`VR-I1`–`VR-I12`)

- **`VR-I1`–`VR-I6`:** Per-configuration quorum intersection, first-write-wins & classic ownership, single decision per ballot counted strictly over `V(b.configuration)`, cross-configuration chosen chain (`b1 ≤ b2 ⇒ ancestor(v1, v2)`), accepted extends chosen (`b < a.ballot ⇒ ancestor(v, a.value)`), and durable history & acknowledgment provenance.
- **`VR-I7` (Uniform monotonic floor):** `floor` is monotonically non-decreasing; once `floor ≥ (c + 1, 0, 0)`, the acceptor grants no new promise and records no new acceptance for any ballot `< (c + 1, 0, 0)` on any key (present or absent), while exact-duplicate replays remain read-only and echo `eff_promised ≥ floor`.
- **`VR-I8` (Single configuration choice):** By induction from agreed bootstrap `(1, V(1))` and `VR-I4` on `K_cfg`, every configuration number `c` maps to at most one voter set `V(c)`.
- **`VR-I9`–`VR-I10` (Sequential hand-off & dual completion):** Configuration `c + 1` begins only after `c` completes, uses Vertical Paxos I classic hand-off with `V(hCfg)`-scoped recovery and `onlyVotersAccept`, and completes per key via transfer or vacuous absence across a fenced classic quorum of `V(c)`.
- **`VR-I11`–`VR-I12` (Pre-join refusal & identity non-reuse):** New voters in `c_join > 1` persist `floor = (c_join, 0, 0)` before answering and refuse all ballots `< c_join`; retired `AcceptorId`s are permanently retired and never reused.

## 3. Wire contract (L1 Target `96`)

`clog-caspaxos` defines a sans-I/O `CasPaxosTransport` trait wired by `roux-storage` onto L1 Messaging Target `96` (`MetaStoreCasPaxos`, `Lane::Normal`) using `prost` + `protox` messages in `roux.storage.v1`:

- `Ballot { uint64 configuration = 1; uint64 round = 2; uint32 proposer_id = 3; }`
- `PrepareRequest { bytes key, Ballot ballot }` → `PrepareResponse { bool promised_ok, Ballot conflicting_ballot, Ballot accepted, optional bytes value }` (`conflicting_ballot` carries `max(eff_promised, accepted)`, including `floor` when fenced).
- `AcceptRequest { bytes key, Ballot ballot, bytes value, Ballot next_ballot }` → `AcceptResponse { bool ok, Ballot promised }` (`promised` carries `eff_promised` on success/duplicate replay and `conflicting_ballot` = `max(eff_promised, accepted)` on refusal).
- `SetFloorRequest { Ballot floor }` → `SetFloorResponse { Ballot floor }` (monotonic durable floor advance).
- `ReadRequest { bytes key }` → `ReadResponse { bool found, Ballot promised, Ballot accepted, optional bytes value }` (ballot-free local snapshot; remains available when fenced).
- `ListKeysRequest { bytes prefix, bytes after_key, uint32 limit }` → `ListKeysResponse { repeated KeyEntry entries, bool has_more }` (ballot-free deterministic ordinal listing of keys with `accepted != (0, 0, 0)`; remains available when fenced).
- `RepairDigestRequest / RepairSyncRequest` for background anti-entropy and graceful departure drain.
- Payload limits obey L1 `N16` (default 32 MiB; cluster views and ledger maps are typically < 1 MiB).

## 4. SQLite durability & seed operations

Each seed node maintains two distinct local SQLite databases under `{root}/caspaxos/` (`WAL` mode, `synchronous = FULL`):

| File | Owner | Required invariants |
| --- | --- | --- |
| `acceptor.db` (+ WAL) | Acceptor (`AcceptorId`) | Singleton `acceptor_floor(singleton INTEGER PRIMARY KEY CHECK (singleton = 1), config INTEGER NOT NULL)` synced before `SetFloor` returns and before a new voter serves any protocol RPC; per-key `(promised, accepted, value)` committed and synced before `Promise` or `Ack` leaves the process; voter identity binding and admission synced before serving. |
| `proposer.db` (+ WAL) | Proposer (`ProposerId`) | Unique positive `ProposerId` per seed; monotonic `(configuration, round)` reservation committed and synced before sending `Prepare` or `Accept` for any classic ballot `(configuration, round, proposer_id)` (including piggybacked classic accepts). |

- **Directory durability:** On every startup, `clog-caspaxos` creates `{root}/caspaxos/` if absent, `fsync`s `{root}` and `{root}/caspaxos/`, and fails startup (exit non-zero before serving Target `96`) if directory sync fails or if the parent directory is missing. On Windows, data directories must reside on local NTFS.
- **Fail-stop on local I/O error:** Any SQLite commit or fsync failure reports to the fatal-error handler and terminates the seed process immediately without running graceful drain or departure barriers against the failed store.
- **Bootstrap validation:** Static seed configuration defines only bootstrap `(1, V(1))`; after the first reconfiguration (`c ≥ 2`), `K_cfg` is the sole authority for the active voter set. Before serving in `c = 1`, bootstrap voters exchange and validate a canonical digest of the sorted `V(1)` voter set during admission/handshake, failing startup on mismatch. A node restarting with older seed endpoints reads `K_cfg` (or receives a floor refusal) and follows `1 → ... → c`.
- **Permanent retirement and new-voter initialization:** Never restart a lost voter on an empty disk under an existing `AcceptorId`. A lost or removed `AcceptorId` is permanently retired by omission from `V(c + 1)` and never reused; surviving voters retain its old `(AcceptorId, incarnation)` admission record so accidental empty-disk restarts are refused. A replacement voter allocates a brand-new `(AcceptorId, fresh_incarnation)`, persists `floor = (c + 1, 0, 0)`, serves admission RPCs (only protocol RPCs wait for `admitted = 1`, preventing mutual-admission deadlock on multi-voter or disjoint migrations), obtains majority admission from `V(c + 1) \ {self}` (self-admitting when `N = 1`), persists `admitted = 1`, and then answers protocol RPCs.
- **Shutdown modes:**
  - `Restart`: stop admitting new mutations, drain in-flight operations, keep local SQLite files intact.
  - `Departure`: for planned graceful seed retirement, drain in-flight mutations and run a repair barrier replicating every local key until `target_replication_factor` (default `classic_quorum(c)`) *other* seeds hold a durable copy, then reconfigure `V(c) → V(c + 1)` to permanently retire the departed `AcceptorId`.
- **Periodic repair:** Background bounded digest scans (`repair_interval = 60s`, `repair_page_size = 128`) propagate committed values to lagging acceptors, skipping keys in configurations below `floor` and targeting only `V(c)`.

## 5. Verification

- **Formal model:** [`specs/fast-caspaxos/FastCASPaxos.qnt`](../../specs/fast-caspaxos/FastCASPaxos.qnt) (shared byte-identically with `ReubenBond/fast-caspaxos` `quint/`) checks all 11 safety invariants (`quorumIntersection`, `stateWellFormed`, `firstWriteWins`, `classicOwnership`, `oneDecisionPerBallot`, `chosenChain`, `acceptedExtendsChosen`, `durableHistory`, `acknowledgmentProvenance`, `floorMonotoneAndEffective`, `noPreJoinAcceptance`) across single-configuration and multi-configuration topologies (replace, 4-voter, double-replace `1 → 2 → 3`, add/remove, disjoint migration), directed/negative/boundary scenarios, 7 guard-disabling mutants via `specs/check-mutants.sh`, and exhaustive TLC (`FastCASPaxosExhaustive`, `FastCASPaxosPipeline`, `FastCASPaxosReconfigExhaustive`).
- **Production verification:** `stateright` BFS + `Bug` mutants over the sans-I/O proposer/acceptor core, `turmoil` fault-injection simulation with linearizability checking (`LinearizabilityTester`), per-key trace replay against `FastCASPaxos.qnt`, and real SQLite crash tests.
