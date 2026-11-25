# Message Drain (`MessageDrain.qnt`)

Formal Quint model of unified single-pass `DrainScan` message draining and idempotent per-segment durable sink commits
across both LSM (`DrainWorker`) and Archive Logs (`ArchiveLogDrainWorker`), verifying `LG-I15`, `LG-I16`, `JS-I11`, and
`JS-I12`.

## What the model checks

- **Safety invariants**:
  - `TypeOK`: well-formed ledger, drainer, watermark/marker, and durable-sink state.
  - `MessagesPreserved`: every durably accepted message is preserved by the live primary's volatile session, the
    undropped ledger, or the durable sink.
  - `NoDropBeforeMessagesCommitted`: a closed ledger is never dropped until every written segment with surviving
    messages has committed to the durable sink.
  - `EmptyFoldWritesNothing`: any segment whose message fold is empty writes nothing to the durable sink.
  - `IdempotentCommit`: idempotent per-segment commits guarantee racing drainers and crash-retries write at most once
    per segment (`sinkWrites == size(durableSink)`).
- **Liveness** (under weak fairness):
  - `EventuallyDrainedAndDropped`: every ledger eventually finishes draining and drops.
  - `EventuallyMessagesDelivered`: every accepted message is eventually consumed, retracted, or delivered via the
    durable sink.
- **Non-vacuity witnesses**:
  - Crash recovery (`NeverSurvivingCrashMessage`), same-segment retract with zero writes (`NeverClearedInSegment`),
    message-only segment with 0 archive rows (`NeverMessageOnlySegment`), cross-segment emit-then-retract
    (`NeverCrossSegmentEmitThenRetract`), and racing drainers hitting idempotent commits (`NeverRacingDrainers`).
- **Mutants**:
  - `drop-before-messages-committed`, `skip-unknown-channel`, `non-idempotent-commit`, `write-on-empty-fold`, and
    `volatile-answer`.

## Running

```powershell
just quint-models -Model MessageDrain
just message-drain-traces
```
