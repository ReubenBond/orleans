# Generic L2 Atomic Message Drain (`messageDrain.qnt`)

Formal Quint model of Roux's L2 general-purpose atomic journal message system (`RowKind::Message = 3`) and single-pass `DrainScan` segment folding with deterministic idempotent durable sink commits (`ST12`, `LG-I15`, `LG-I16`, `JS-I11`, `JS-I12`).

This model stays strictly within Layer 2 (`clog-ledgers`, `clog-journals`, `clog-archive`) and is independent of any upper-layer consumer; L4 channels (such as `"roux.reminder"` in `roux-jobs`, `R37`) instantiate its `MessageHandler`/`MessageProcessor` contract.

Authoritative design docs:
- [docs/design/storage.md](../../docs/design/storage.md) (§3a `ST12`: unversioned `RowKind::Message = 3`, `MessageChannel`, `MessageHandler`/`MessageSession`, `MessageProcessor`/`MessageFold`, `SegmentMessageFolds` in `DrainScan`)
- [docs/design/clog/invariants.md](../../docs/design/clog/invariants.md) (`LG-I15`, `LG-I16`, `JS-I11`, `JS-I12`)

## Protocol behavior modeled

- An open ledger `L` writing journal-only rows (`"K"`), atomic journal + `RowKind::Message` foldable channel operations (`Emit` / `Retract`) across two sink partitions (`"E0"`, `"R0"`, `"E1"`, `"R1"`), and message-only segments with zero archive rows (`"M0"`).
- Primary volatile `MessageSession` consumption while open (`primaryConsume`, zero sink writes) vs primary crash losing volatile session state and closing `L` at its decided tail (`primaryCrash`), plus graceful roll/close (`closeLedger`).
- Single-pass `DrainScan` over each closed segment (`scanSegment`), folding archive rows and `RowKind::Message` rows together under `DrainScan`'s shared `DrainMemoryBytes` `ByteBudget` (including `overBudgetReset` clearing folds and rescanning alone).
- `MessageProcessor::commit()` (`commitBatches`) performing zero durable sink writes when a segment fold clears to empty and committing one deterministic idempotent durable sink batch per surviving `(seg, partition)`, strictly before `SlateDbDrainWorker` commits the atomic SlateDB `WriteBatch` advancing `0x00wm/{L}` (`commitSegment`), plus `slateMarkClosedComplete` when `watermark == T` and `dropLedger` once complete.
- Concurrent racing drainers (`Drainers = 2`), cross-segment `Emit`-then-`Retract`, multi-partition segments, and drainer crashes at every step (`drainerCrash`).

## Invariants, liveness, and witnesses

| Property | Kind | Guarantee |
| --- | --- | --- |
| `TypeOK` | Safety | Every segment cursor, watermark, sink-write count, and drainer phase stays within bounds. |
| `MessagesPreserved` | Safety | Every durably accepted undelivered message is preserved by the live open primary's `MessageSession`, an undropped ledger, or a durable sink batch matching its partition. |
| `NoDropBeforeMessagesCommitted` | Safety | `LG-I16`: SlateDB's watermark never advances past a segment, and a closed ledger never drops, before every surviving partition of that segment has committed its durable sink batch. |
| `EmptyFoldWritesNothing` | Safety | Any segment whose message fold clears to empty for a partition writes zero durable sink batches for it. |
| `IdempotentCommit` | Safety | Deterministic sink batch keys `(seg, partition)` guarantee racing drainers and crash retries create at most one durable sink batch per `(seg, partition)` (`sinkWrites == committedBatches.size()`). |
| `EventuallyDrainedAndDropped` | Liveness | Under weak fairness, every ledger eventually finishes draining all segments and drops. |
| `EventuallyMessagesDelivered` | Liveness | Under weak fairness, every durably emitted message is eventually consumed, retracted, or delivered from its durable sink. |
| `NeverSurvivingCrashMessage` | Witness | A primary crashes with a surviving emitted message; `DrainScan` commits a sink batch, `L` drops, and the sink delivers it. |
| `NeverRetractedInSegment` | Witness | A message emitted then retracted within one segment drains and drops with zero sink writes. |
| `NeverMessageOnlySegment` | Witness | A message-only segment (`0` archive rows) still commits its sink batch and advances the watermark. |
| `NeverCrossSegmentEmitThenRetract` | Witness | Cross-segment `Emit`-then-`Retract` commits segment 0's batch while segment 1 writes zero batches. |
| `NeverMultiPartitionSegment` | Witness | One segment with surviving messages in two distinct partitions commits two deterministic sink batches. |
| `NeverRacingDrainers` | Witness | Two racing drainers commit the same deterministic batch key with zero duplicate creations. |

## Mutants (verified by `../check-mutants.sh`)

| Mutant | Guard disabled | Breaks |
| --- | --- | --- |
| `mutantCommitBeforeMessagesCommitted` | `SlateDbDrainWorker` advances `0x00wm/{L}` before committing `SegmentMessageFolds` | `NoDropBeforeMessagesCommitted` |
| `mutantSkipUnknownChannel` | `DrainScan` drops unmatched message channels instead of failing closed | `MessagesPreserved` |
| `mutantNonDeterministicBatchKey` | Non-deterministic batch keys create duplicates under racing drainers | `IdempotentCommit` |
| `mutantWriteOnEmptyFold` | Processor writes a sink batch even when a segment's fold cleared to empty | `EmptyFoldWritesNothing` |
| `mutantVolatileSinkCommit` | `Commit` answers complete while the sink write is still volatile | `MessagesPreserved` |
| `mutantSkipMessageOnlySegment` | `SlateDbDrainWorker` skips 0-archive-row message-only segments | `NoDropBeforeMessagesCommitted` |

## Running locally

```bash
cd specs/message-drain
quint typecheck messageDrain.qnt
quint test messageDrain.qnt --main=messageDrainSim --match=Test
quint run messageDrain.qnt --main=messageDrainSim --backend=rust --invariant=safety --max-steps=40 --max-samples=20000 --seed=7
bash ../check-mutants.sh messageDrain.qnt messageDrainSim safety --backend=rust --max-steps=40 --max-samples=20000 --seed=7
quint verify messageDrain.qnt --main=messageDrainTlc --backend=tlc --invariant=safety
quint verify messageDrain.qnt --main=messageDrainRetractedTlc --backend=tlc --invariant=safety
quint verify messageDrain.qnt --main=messageDrainCrossSegTlc --backend=tlc --invariant=safety
quint verify messageDrain.qnt --main=messageDrainMultiPartitionTlc --backend=tlc --invariant=safety
quint verify messageDrain.qnt --main=messageDrainTlc --backend=tlc --temporal=EventuallyDrainedAndDropped
quint verify messageDrain.qnt --main=messageDrainTlc --backend=tlc --temporal=EventuallyMessagesDelivered
```
