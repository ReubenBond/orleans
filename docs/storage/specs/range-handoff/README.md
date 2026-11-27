# Range Ownership & Ledger-Replica Lease Handoff Specification (`rangeHandoff.qnt`)

Formal Quint model of Roux's cross-layer dyadic range ownership handoff protocol with **ledger-replica quorum leases** spanning `clog-wal` (`LedgerReplica` Target `97`), `clog-ledgers` (`ClusterController`, `TransitionExecutor`, `LedgerMap`), `roux-storage`, and the `L4` grain runtime (`ST8`, `R33`).

Authoritative design docs:
- [docs/design/storage.md](../../docs/design/storage.md) (Ledger-replica leases, `PrepareRelease`, `ExecuteTransition`, `RangeEvent`)
- [docs/design/runtime.md](../../docs/design/runtime.md) (`R33` Range ownership and per-ledger `Lease::check`)

## Durable & volatile safety unified in the ledger ensemble

Each dyadic ledger `L` in `LedgerMap` has **one immutable primary (`MemberId`)**, a **static ensemble of `N` replicas** (`N = 3`, quorum `Q = 2`, `2Q > N`), and a **one-way lifecycle** (`Open -> Closed -> Dropped`), succeeded by `Replace` (1→1), `Split` (1→2), or `Merge` (2→1):

- **Active & idle lease renewal (zero WAL writes):**
  - Every successful `ReplicaWrite` on a quorum of `Q` unsealed replicas piggybacks a lease grant (`granted_lease_until = max(granted_lease_until, r_now + D)` in RAM).
  - When a ledger is idle, periodic batched `ReplicaHeartbeat` calls per peer replica node maintain the lease with zero WAL writes.
  - Once `Q` replicas ack a write/heartbeat started at primary monotonic time `t_send`, the primary extends its local lease expiry to `t_send + D / F` (`F = 1 + ρ`, bounded clock-rate drift factor; modeled with `F = 2`).
- **Replica crash/restart safety (zero WAL writes):**
  - On `LedgerStore` restart, RAM-only `granted_lease_until` is cleared and `boot_lease_horizon = boot_clock + D` is latched in RAM so any subsequent seal conservatively bounds pre-crash lease grants. Durable `sealed` flags (`LP-I8`) survive restart.
- **Revocation IS ledger sealing (`ReplicaReadTail { seal: true }`):**
  - On seal, replica `r` durably persists `sealed = true` (refusing all future writes and lease grants) and returns `lease_remaining = max(0, max(granted_lease_until, boot_lease_horizon) - r_now)`.
  - **Unplanned takeover (`TakenOver`):** `TransitionExecutor` waits `max_{r ∈ Q_seal} lease_remaining_r` on its monotonic clock *before* committing `LedgerMap` CAS (`L -> L'`). Because the primary already divided `D` by `F`, any worst-case clock drift (`v_max / v_min ≤ F`) satisfies `T_commit ≥ T_seal + (D - v_max·T_seal) / v_max = D / v_max ≥ T_P_exp` — no extra `* F` multiplier is needed on the sealer side, and a dead primary whose lease expired `> D` ago seals with `lease_remaining = 0` (0 ms extra wait).
  - **Cooperative handoff (`Cooperative`):** Outgoing primary completes `PrepareRelease(L)` (fences L4, drains activations & flushes/drops held responses, permanently surrenders its local lease on `L`, stops admitting on `L`, and replies `Released(L)`). `TransitionExecutor` still quorum-seals `L` and recovers its tail, skipping the `lease_remaining` sleep (0 ms planned handoff).
- **Zero dependency on Rapid `Defunct` & zero predecessor tracking:** Because `L`'s replica-quorum lease is already expired or surrendered *before* `L` becomes `Closed` in `LedgerMap`, chained takeovers (`A -> B -> C`) preserve single-activation safety by induction with plain `RangeEvent::Acquired { range, ledger, lease }`.

## Modeled invariants (`safety`)

- **`closedLedgerHasNoValidLease`** — Once a ledger `L` is `Closed` in `LedgerMap`, its primary never holds a valid local lease on `L`.
- **`singleActiveOwner`** — At most one node holding a valid lease on its active ledger holds an active grain activation at any instant.
- **`noPostReleaseZombie`** — Once a newer ledger is open, no node with a valid lease holds an active grain or un-flushed durability-gated response from an older ledger, and no effect is ever emitted from an older or already-closed ledger.
- **`fencedLedgerNeverServed`** — Once `L4` enters `Releasing` or `Lost` for ledger `L`, it never serves `L` (or older) again; it only re-activates upon a strictly newer `Acquired(L')`.

## Mutants (verified by `../check-mutants.sh`)

| Mutant | Guard disabled | Breaks |
| --- | --- | --- |
| `mutantRenewAfterSeal` | Replica grants a lease renewal after `L` is sealed (`LP-I8`) | `closedLedgerHasNoValidLease` |
| `mutantTakeoverSkipsLeaseWait` | Unplanned `TakenOver` commits immediately without waiting `max(lease_remaining)` | `closedLedgerHasNoValidLease` |
| `mutantPrimaryForgetsDriftFactor` | Primary computes local expiry `t_send + D` instead of `t_send + D / F` | `closedLedgerHasNoValidLease` |
| `mutantReplicaRestartForgetsBootHorizon` | `LedgerStore` restart clears RAM grants without setting `boot_lease_horizon = clock + D` | `closedLedgerHasNoValidLease` |
| `mutantCooperativeWithoutReleased` | Executor skips `lease_remaining` wait without receiving `Released` | `closedLedgerHasNoValidLease` |
| `mutantCooperativeForgetsSurrender` | `PrepareRelease` replies `Released` without surrendering its local lease | `closedLedgerHasNoValidLease` |
| `mutantEffectWithoutLeaseCheck` | L4 emits an effect without checking `ledger_lease.check(now)` | `noPostReleaseZombie` |

## Running locally

```bash
cd specs/range-handoff
quint typecheck rangeHandoff.qnt
quint test rangeHandoff.qnt --main=rangeHandoffSim
quint run rangeHandoff.qnt --main=rangeHandoffSim --invariant=safety --max-samples=20000 --max-steps=40 --backend=rust
bash ../check-mutants.sh rangeHandoff.qnt rangeHandoffSim safety --backend=rust --max-steps=45 --max-samples=30000 --seed=7
# Exhaustive TLC verification (`N=2, QUORUM=2, D=2, MAX_CLOCK=2, MAX_RENEWS=1, MAX_PENDING_TXS=1`)
quint verify rangeHandoff.qnt --main=rangeHandoffTlc --invariant=safety --backend=tlc
```

