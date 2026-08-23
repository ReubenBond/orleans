# Deadlock-detection test implementation plan

## Phase 1 - Deterministic local regressions

Create `test/Transactions/Orleans.Transactions.Tests/DeadlockDetectionRegressionTests.cs` plus only the test-only fakes/helpers required by that file.

1. Add `LockExpiryIsProcessedWhenDeadlockDetectionTimeoutExceedsLockTimeout`.
   - Configure `LockTimeout` earlier than `ITransactionalLockObserver.DetectionTimeout`.
   - Arm an exact `LockExpired` diagnostic observer before acquiring the lock.
   - Assert expiry occurs within a bound shorter than deadlock timeout, the lock validates as broken, the event identifies the held transaction/resource and deadline ordering, and deadlock analysis was not invoked.
2. Add `CycleFormedAfterInitialAcyclicScanIsDetected`.
   - Drive `DeadlockDetector.CheckForDeadlocks` through an initiating acyclic snapshot and a later snapshot which adds the closing wait edge.
   - Assert no notification after the initial scan and exactly one notification with the exact four cycle edges after the update.
3. Add `CoherentPerSiloSnapshotReplacesStaleEdgesAfterLockHandoff`.
   - Use two silo addresses and a captured batch ID.
   - Complete an initial acyclic distributed round, then submit a coherent handoff snapshot where transaction T1 waits for resource R1 now held by T2.
   - Assert no deadlock is reported and the batch can accept the peer response. This is expected to fail while the detector retains the old `R1 -> T1` edge.
4. Add `DuplicateDelayedDeadlockBreakDoesNotAbortSubsequentlyPromotedGroup`.
   - Acquire the current write group and queue a second write group.
   - Deliver the first break and use successful promotion as a phase barrier.
   - Deliver the duplicate break and assert the promoted transaction remains valid and no unlock notification is emitted for it. This is expected to fail until delivery is correlated with the group it intended to break.

Manifest change: add the centrally managed `NSubstitute` package reference to `Orleans.Transactions.Tests.csproj` if compilation confirms it is not already directly available.

Run the narrow build and focused regression class immediately. Fix test compilation/assertion mistakes without changing production.

## Phase 2 - Strengthen canonical existing tests

1. Update `Memory/DeadlockTest.cs`.
   - Give the fixture a nonzero unique collector grain key used by both the listener and test.
   - Clear collector state before triggering the deadlock.
   - Record a correlation timestamp and only accept events whose `StartTime` belongs to this invocation.
   - Assert the correlated deadlock event has locks and useful concrete metadata.
2. Update `WaitForGraphTests.cs`.
   - Construct `DoesNotDetectNonCycles` with `Lock(transaction, resource)` arguments in the correct order.
   - Make the graph a single connected acyclic chain.
   - Assert graph round-trip edge identity, connected-subgraph identity, false cycle result, and an empty cycle collection.

Run the narrow build and each exact existing test.

## Phase 3 - Final validation and quality gates

1. Run all focused deadlock tests and classify:
   - passing strengthened tests,
   - expected production regression failures,
   - compile/tooling blockers,
   - unrelated/pre-existing failures.
2. Run the full workspace non-incremental build.
3. Run full workspace net10.0 tests where feasible.
4. Invoke `test-gap-analysis` against the tested production/test files, remediate test gaps which do not require production changes, and record explicit out-of-scope production seams.
5. Invoke `assertion-quality`, replace weak/trivial assertions, and ensure multi-observable behavior is asserted.
6. Re-run affected narrow validation after quality fixes.
7. Create `.testagent/status.md` with the checklist-to-test mapping, commands/results, quality findings/fixes, expected failures, blockers, and full-validation status.

## Acceptance mapping

| Acceptance item | Planned exact test evidence |
|---|---|
| `DeadlockDetectionTimeout > LockTimeout processes lock expiry without CPU spin/suppression.` | `DeadlockDetectionRegressionTests.LockExpiryIsProcessedWhenDeadlockDetectionTimeoutExceedsLockTimeout` |
| `A cycle formed after an initial acyclic deadlock scan is still detected.` | `DeadlockDetectionRegressionTests.CycleFormedAfterInitialAcyclicScanIsDetected` |
| `Coherent distributed per-silo snapshots do not retain stale edges across lock handoff/update.` | `DeadlockDetectionRegressionTests.CoherentPerSiloSnapshotReplacesStaleEdgesAfterLockHandoff` |
| `Duplicate/delayed deadlock-break delivery is idempotent and cannot abort a subsequently promoted group.` | `DeadlockDetectionRegressionTests.DuplicateDelayedDeadlockBreakDoesNotAbortSubsequentlyPromotedGroup` |
| `DeadlocksAreReported uses isolated/correlated collector state.` | `DeadlockTest.DeadlocksAreReported` |
| `DoesNotDetectNonCycles constructs a connected acyclic graph with correct Lock(transaction, resource) argument order.` | `WaitForGraphTests.DoesNotDetectNonCycles` |
