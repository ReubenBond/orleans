# Membership core specification

[`membership.qnt`](membership.qnt) is a [Quint](https://quint-lang.org) specification of how the membership core (layer L3) records and applies decided entries, as designed in [Membership](../../docs/design/membership.md). It checks C1 to C3, C6, the at-most-once commit of member commands, and that member lookups only move forward. Rapid's agreement is checked separately in [`specs/rapid`](../rapid), and per-ledger ownership leases live in L2 Clog ([`specs/range-handoff`](../range-handoff)).

## Model

- **Agreement.** `decide` picks the next entry from submitted batches, membership cuts, or genesis, validated against the stored view. `staleAppend` is a writer from a stale configuration that commits a different valid entry first, so the register arbitrates.
- **Register.** The `membership` register in `clog-metastore` (L2), abstracted as `log`, the history of its writes, one entry per view. Processes read only the head: the stored view and the entry that produced it. A view is published only once it is stored.
- **Processes.** One process per node at a time. Processes start with a fresh instance ID, load the stored view, apply the stored entry when one view behind (`readNext`), resync to the stored view when further behind (`resync`), submit batches, and crash. A restart is a new process that joins with the node's `NodeId` (Clog's UUID, modeled as an integer). Submitters keep resending batches until they see them committed or invalid, which models lost acknowledgements.
- **Faults.** Each mutant enables one fault flag rather than adding commands to the model.

## Properties

| Invariant | Contract | Meaning |
| --- | --- | --- |
| `logValid` | C1 | Every committed batch is valid against the state it applies to. |
| `batchesOnce`, `instancesOnce` | M30 | No batch commits twice; each joiner instance joins at most once. |
| `viewsArePrefixes` | C2 | Each process's view is exactly the committed view at its version, including after a resync. |
| `noResurrection`, `oneLivePerNode` | C3 | A defunct member never becomes live again; each node has at most one live process. |
| `selfFencing` | C6 | A running process never holds a view in which it is defunct. |
| `lookupMonotonic` | M34 | Each process's member lookups only move forward, including across pruning and resyncs. |

`safety` is their conjunction.

## Mutants

Checked by the shared gate, [`check-mutants.sh`](../check-mutants.sh):

| Mutant | Broken rule | Invariant |
| --- | --- | --- |
| `mutantNoReplace` | A restart's `Join` leaves the previous process live | `oneLivePerNode` |
| `mutantNoDedup` | Resubmitted batches are not deduplicated by sequence number | `batchesOnce` |
| `mutantPruneForgets` | Lookups answer `Unknown` for pruned members | `lookupMonotonic` |
| `mutantPublishDecided` | A view is published before its entry is durable | `viewsArePrefixes` |
| `mutantResyncUnfenced` | A process that resyncs to a view showing it defunct keeps running | `selfFencing` |

## Running

```sh
quint test membership.qnt --main=membershipSim --match=Test
quint run membership.qnt --main=membershipSim --backend=rust --invariant=safety --max-steps=40 --max-samples=5000
../check-mutants.sh membership.qnt membershipSim safety --backend=rust --max-steps=40 --max-samples=10000 --seed=7
quint verify membership.qnt --main=membershipTlc --backend=tlc --invariant=safety
```

## Results

- Directed scenarios: all pass.
- Random simulation: no violation in 3,000 traces of 40 steps.
- Mutants: all five violate their invariants in simulation within 10,000 traces; `noDedupTest` and `resyncUnfencedTest` also catch their mutants directly.
- Batches are a sum type (`GenesisBatch`, `JoinBatch`, `MemberBatch`, `CutBatch`), as in the design (M35).
- The specification found that a process reloading the stored view (now a resync) must also check for its own removal (C6).
- The specification found that a stale `Join` from a crashed joiner could commit again once its instance's record was pruned; joins now target a version, as in Rapid.
- TLC: not run locally for this revision; the weekly job runs it with a time limit and reports a timeout as inconclusive.
