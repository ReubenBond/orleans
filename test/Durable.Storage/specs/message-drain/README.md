# Message Drain (`MessageDrain.qnt`)

Formal Quint model of unified single-pass `DrainScan` message draining and deterministic sealed `JobShard` publishing
across both LSM (`DrainWorker`) and Archive Logs (`ArchiveLogDrainWorker`), verifying `LG-I15`, `LG-I16`, `JS-I11`, and
`JS-I12`.

## What the model checks

- **Safety invariants**:
  - `TypeOK`: well-formed ledger, drainer, watermark/marker, and sealed-shard state.
  - `WakeupPreserved`: every durably accepted wakeup is preserved by the live primary's volatile heap, the undropped
    ledger, or a durable sealed `JobShard`.
  - `NoDropBeforeMessagesCommitted`: a closed ledger is never dropped until every written segment with a surviving
    wakeup has published its sealed `JobShard`.
  - `ClearedInSegmentWritesNothing`: any segment whose messages fold to empty publishes zero sealed shards.
  - `IdempotentShardPublish`: deterministic sealed shard IDs guarantee racing drainers and crash-retries create at most
    one shard per segment (`createdShards == size(sealedShards)`).
- **Liveness** (under weak fairness):
  - `EventuallyDrainedAndDropped`: every ledger eventually finishes draining and drops.
  - `EventuallyWakeupDelivered`: every accepted wakeup is eventually delivered or cleared.
- **Non-vacuity witnesses**:
  - Crash recovery (`NeverSurvivingCrashWakeup`), same-segment clear with zero writes (`NeverClearedInSegment`),
    message-only segment with 0 archive rows (`NeverMessageOnlySegment`), cross-segment set-then-clear
    (`NeverCrossSegmentSetThenClear`), and racing drainers hitting idempotent deterministic shard creation
    (`NeverRacingDrainers`).
- **Mutants**:
  - `drop-before-messages-committed`, `skip-unknown-channel`, `non-deterministic-shard-id`, `write-on-empty-fold`, and
    `volatile-answer`.

## Running

```powershell
just quint-models -Model MessageDrain
just message-drain-traces
```
