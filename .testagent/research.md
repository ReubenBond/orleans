# Deadlock-detection test research

## Scope and constraints

- Authoritative workspace: `C:\dev\copilot-worktrees\orleans\deadlock-detection`.
- Broad Research -> Plan -> Implement run, bounded to transaction deadlock detection and its canonical transaction tests.
- Test-only changes are permitted. Production source changes, version-control mutation, commits, and pushes are prohibited.
- `code-testing-extensions` was unavailable to the caller and was not retried. Guidance below comes from repository files and representative tests.

## Acceptance checklist

1. `DeadlockDetectionTimeout > LockTimeout processes lock expiry without CPU spin/suppression.`
2. `A cycle formed after an initial acyclic deadlock scan is still detected.`
3. `Coherent distributed per-silo snapshots do not retain stale edges across lock handoff/update.`
4. `Duplicate/delayed deadlock-break delivery is idempotent and cannot abort a subsequently promoted group.`
5. `DeadlocksAreReported uses isolated/correlated collector state.`
6. `DoesNotDetectNonCycles constructs a connected acyclic graph with correct Lock(transaction, resource) argument order.`

## Bounded target inventory

| Production target | Relevant behavior | Canonical test target |
|---|---|---|
| `src/Orleans.Transactions/State/ReaderWriterLock.cs` | `LockWork` chooses between lock expiry and deadlock-analysis deadlines; current branch ordering can repeatedly reschedule an already-expired lock deadline while a later deadlock deadline remains set. | New focused tests in `test/Transactions/Orleans.Transactions.Tests/DeadlockDetectionRegressionTests.cs` |
| `src/Orleans.Transactions/DeadlockDetection/DeadlockDetector.cs` | Batches begin from an initiating snapshot, request later snapshots, detect cycles after graph changes, and currently merge snapshots additively rather than replacing each silo's prior contribution. | New focused detector tests in `DeadlockDetectionRegressionTests.cs` |
| `src/Orleans.Transactions/State/TransactionalResource.cs` and `DeadlockDetection/IDeadlockBreakableTransactionalResource.cs` | `BreakLocks()` has no batch/generation/transaction identity and aborts whichever group is current when delivery occurs. | New duplicate/delayed-delivery regression in `DeadlockDetectionRegressionTests.cs` |
| `src/Orleans.Transactions/DeadlockDetection/LockTracker.cs` and `DeadlockDetectionLockObserver.cs` | Silo snapshots use versions but detector-side ownership of prior per-silo edges is not represented. | Covered through public `DeadlockDetector.CheckForDeadlocks` orchestration rather than an adjacent graph-only test |
| `src/Orleans.Transactions/DeadlockDetection/WaitForGraph.cs` | Converts lock/wait edges, extracts connected subgraphs, merges edges, and detects cycles. | Existing canonical `test/Transactions/Orleans.Transactions.Tests/WaitForGraphTests.cs` |
| `test/Transactions/Orleans.Transactions.Tests/Memory/DeadlockTest.cs` and `src/Orleans.Transactions.TestKit.Base/Grains/DeadlockEventCollector.cs` | Cluster listener reports to collector key `0`; existing assertion can observe stale unrelated events. | Strengthen existing `DeadlocksAreReported` in its canonical file |

## Repository test conventions

- SDK-style .NET solution: `Orleans.slnx`; transaction test project targets `$(TestTargetFrameworks)` (`net8.0;net10.0` by default).
- xUnit v3 packages are centrally pinned (`xunit.v3.mtp-v2` 3.2.2) and Microsoft Testing Platform v2 is enabled by `test/Directory.Build.props`.
- Test projects are executable MTP applications. Repository filtering syntax uses `--filter-class` and `--filter-method`, not VSTest `--filter`.
- Existing transaction unit tests use `[Fact]`, `Assert.Equal`, `Assert.Single`, `Assert.All`, `WaitAsync(..., TestContext.Current.CancellationToken)`, exact deterministic values, and test-only fakes.
- Transaction tests carry `[TestSuite("BVT")]`, `[TestProvider("None")]`, `[TestArea("Transactions")]`, and `[TestCategory("BVT"), TestCategory("Transactions")]`.
- `src/Orleans.Transactions/Orleans.Transactions.csproj` grants `InternalsVisibleTo` to `Orleans.Transactions.Tests`.
- `test/AGENTS.md` requires isolated mutable state, explicit phase barriers instead of sleeps where possible, a single time driver, exact assertions, and contextual timeout failures.

## Dependencies and test seams

- `Orleans.Transactions.Tests.csproj` already references the production transactions assembly transitively and has internal access.
- Detector construction needs `ISiloStatusOracle`, `IInternalGrainFactory`, `IServiceProvider`, listeners, options, and `TimeProvider`. A test-only mocking dependency (`NSubstitute`, centrally versioned) is the minimal practical seam for the two wide Orleans runtime interfaces.
- Deadlock breaking needs a test `GrainReference` whose `IGrainReferenceRuntime.Cast` returns a completed substitute extension, so cycle detection can complete without a cluster.
- Lock-expiry tests can use the existing `TransactionDiagnosticEvents.LockExpired` observable as a deterministic phase barrier and can assert that deadlock analysis was not invoked.
- Queue tests can use a minimal `TransactionQueue<TState>` with null storage/timer dependencies because the exercised `CommitRole.NotYetDetermined` path does not touch them.
- Current production has no deadlock-break delivery identity. The duplicate-delivery regression can compile but is expected to fail until break requests carry enough transaction/batch/generation identity to reject stale delivery.
- Current detector has no per-silo edge replacement model. The coherent-snapshot regression can compile but is expected to fail until each accepted silo snapshot replaces that silo's previous graph contribution for the batch.
- Current `ReaderWriterLock.LockWork` prioritizes the presence of `DeadlockDeadline` over an earlier expired `Deadline`. The expiry regression can compile but is expected to fail until the earliest due deadline is processed without immediate expired-time rescheduling.

## Planned source-to-test pairs

1. `ReaderWriterLock.cs` -> `DeadlockDetectionRegressionTests.LockExpiryIsProcessedWhenDeadlockDetectionTimeoutExceedsLockTimeout`.
2. `DeadlockDetector.cs` -> `DeadlockDetectionRegressionTests.CycleFormedAfterInitialAcyclicScanIsDetected`.
3. `DeadlockDetector.cs` -> `DeadlockDetectionRegressionTests.CoherentPerSiloSnapshotReplacesStaleEdgesAfterLockHandoff`.
4. `TransactionalResource.cs` -> `DeadlockDetectionRegressionTests.DuplicateDelayedDeadlockBreakDoesNotAbortSubsequentlyPromotedGroup`.
5. `DeadlockTest.cs` -> strengthened `DeadlockTest.DeadlocksAreReported`.
6. `WaitForGraphTests.cs` -> strengthened `WaitForGraphTests.DoesNotDetectNonCycles`.

## Validation commands

Narrow build:

```powershell
dotnet build test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --no-incremental
```

Narrow generated regression tests:

```powershell
dotnet test --project test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --filter-class "*DeadlockDetectionRegressionTests*" --minimum-expected-tests 4
```

Narrow strengthened existing tests:

```powershell
dotnet test --project test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --filter-method "*DoesNotDetectNonCycles*" --minimum-expected-tests 1
dotnet test --project test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --filter-method "*DeadlocksAreReported*" --minimum-expected-tests 1
```

Full non-incremental workspace build:

```powershell
dotnet build Orleans.slnx --no-incremental
```

Full workspace tests (feasibility/time permitting):

```powershell
dotnet test --solution Orleans.slnx --framework net10.0 --minimum-expected-tests 1 --max-parallel-test-modules 1
```
