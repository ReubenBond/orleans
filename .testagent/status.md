# Deadlock-detection test-generation status

## Outcome

- Strategy: broad single-pass Research -> Plan -> Implement.
- Production files modified: none.
- Test project compiles on `net10.0`, and the full multi-target workspace build succeeds.
- Six acceptance items map to exact tests.
- Three tests pass against current production and three intentionally expose the not-yet-fixed production regressions.

## Acceptance checklist and exact evidence

| Requirement | Exact test | Result against current production |
|---|---|---|
| `DeadlockDetectionTimeout > LockTimeout processes lock expiry without CPU spin/suppression.` | `Orleans.Transactions.Tests.DeadlockDetectionRegressionTests.LockExpiryIsProcessedWhenDeadlockDetectionTimeoutExceedsLockTimeout` | **Expected regression failure.** A held lock with 50ms `LockTimeout` did not expire within 750ms while deadlock detection was 5s. The final test also counts scheduled work and rejects 100+ work items as a busy notification loop once expiry proceeds. |
| `A cycle formed after an initial acyclic deadlock scan is still detected.` | `Orleans.Transactions.Tests.DeadlockDetectionRegressionTests.CycleFormedAfterInitialAcyclicScanIsDetected` | **Passed.** Initial three-edge graph produced no event; a later closing edge produced exactly one four-edge cycle. |
| `Coherent distributed per-silo snapshots do not retain stale edges across lock handoff/update.` | `Orleans.Transactions.Tests.DeadlockDetectionRegressionTests.CoherentPerSiloSnapshotReplacesStaleEdgesAfterLockHandoff` | **Expected regression failure.** The coherent replacement snapshot is independently proven acyclic, but detector state retained the prior `resource -> original owner` edge and reported a two-edge false cycle. |
| `Duplicate/delayed deadlock-break delivery is idempotent and cannot abort a subsequently promoted group.` | `Orleans.Transactions.Tests.DeadlockDetectionRegressionTests.DuplicateDelayedDeadlockBreakDoesNotAbortSubsequentlyPromotedGroup` | **Expected regression failure.** After the next group was promoted and validated `Ok`, duplicate delivery changed it to `BrokenLock`. |
| `DeadlocksAreReported uses isolated/correlated collector state.` | `Orleans.Transactions.Tests.Memory.DeadlockTest.DeadlocksAreReported` | **Passed.** Uses a nonzero fixture-unique collector grain key, clears it before execution, filters on this invocation's start timestamp, and validates correlated cycle structure. |
| `DoesNotDetectNonCycles constructs a connected acyclic graph with correct Lock(transaction, resource) argument order.` | `Orleans.Transactions.Tests.WaitForGraphTests.DoesNotDetectNonCycles` | **Passed.** Exact formatted edges prove the transaction/resource order and the connected subgraph contains the complete five-edge acyclic chain. |

## Files

- Added `test/Transactions/Orleans.Transactions.Tests/DeadlockDetectionRegressionTests.cs`.
- Updated `test/Transactions/Orleans.Transactions.Tests/Memory/DeadlockTest.cs`.
- Updated `test/Transactions/Orleans.Transactions.Tests/WaitForGraphTests.cs`.
- Updated `test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj` with the centrally versioned test-only `NSubstitute` reference.
- Added `.testagent/research.md`, `.testagent/plan.md`, and this status file.

## Commands and results

### Narrow build

```text
dotnet build test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --no-incremental
Build succeeded. 0 warnings, 0 errors.
```

The final post-quality-gate run succeeded in 28.38s.

### Narrow tests

```text
dotnet test --project test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --filter-class "*DeadlockDetectionRegressionTests*" --minimum-expected-tests 4
total 4; succeeded 1; failed 3; skipped 0
```

The three failures are the intended production regressions listed above. `CycleFormedAfterInitialAcyclicScanIsDetected` passed independently and in the class run.

```text
dotnet test --project test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --filter-method "*DoesNotDetectNonCycles*" --minimum-expected-tests 1
total 1; succeeded 1; failed 0
```

```text
dotnet test --project test/Transactions/Orleans.Transactions.Tests/Orleans.Transactions.Tests.csproj --framework net10.0 --filter-method "*DeadlocksAreReported*" --minimum-expected-tests 1
total 1; succeeded 1; failed 0
```

### Full workspace build

```text
dotnet build Orleans.slnx --no-incremental
exit 0
```

The final full build was rerun after the final assertion strengthening and succeeded.

### Full workspace tests

```text
dotnet test --solution Orleans.slnx --framework net10.0 --minimum-expected-tests 1 --max-parallel-test-modules 1
total 10,524; succeeded 8,153; failed 65; skipped 2,306; duration 41m 24s
exit 2
```

- 3 failures are the expected new deadlock regressions.
- 62 unrelated/environmental failures require unavailable external services:
  - Firestore persistence: 14
  - Azure Queue streaming: 20
  - Cassandra clustering: 12
  - ADO.NET MySQL/PostgreSQL/SQL Server: 9
  - Event Hubs batched streaming: 7
- The final test-only spin-count strengthening was followed by a clean full workspace build and an exact targeted rerun; the full 41-minute workspace run was not repeated a second time.

## Quality gates

### `test-gap-analysis`

- Invoked before completion and rerun after the final test changes.
- `test-analysis-extensions` was unavailable as a skill; its .NET base extension was loaded directly from the installed plugin and applied with the repository's xUnit v3/MTP v2 conventions.
- No production mutation was injected because the request expressly prohibits production source modification. Mutation conclusions are therefore static/unverified; no mutation score is claimed.
- Acceptance-scope pseudo-mutations are pinned:
  - choosing the later deadlock deadline over expired lock deadline fails the expiry/status/no-detection assertions;
  - a busy expired-deadline reschedule loop fails the scheduler-count assertion;
  - removing the post-update cycle scan fails the exact four-edge cycle assertion;
  - retaining a silo's old edge fails the independently acyclic handoff assertion;
  - applying a stale break to the current group fails promoted-state and unlock-side-effect assertions;
  - reversing `Lock(transaction, resource)` arguments fails exact formatted-edge assertions.
- No feasible acceptance gap remains in test code. Production fixes are intentionally out of scope.

### `assertion-quality`

- Invoked before completion and rerun after the final test changes.
- All six acceptance tests contain concrete behavioral assertions; none is assertion-free or trivial-only.
- Assertions cover equality, collections/deep edge sets, negative outcomes, state transitions, side effects, null/content checks, comparison bounds, and exact request correlation.
- Fixes made during the gate:
  - replaced the non-cycle test's input/round-trip-only evidence with exact semantic edge strings;
  - proved the handoff snapshot is independently acyclic before submitting it;
  - added promoted-lock observer side effects and duplicate-unlock exclusions;
  - strengthened collector events with wait/lock edge presence and nondefault transaction/resource identities;
  - added an explicit scheduled-work bound to detect CPU-spin behavior.

### Static source-to-test pairing

- Ran the Roslyn `find-untested-sources` analyzer once against the workspace after implementation.
- It paired `DeadlockDetectionRegressionTests.cs` with `DeadlockDetector.cs`, `TransactionalResource.cs`, and `WaitForGraph.cs`.
- It did not statically pair `ReaderWriterLock.cs` or `LockTracker.cs` because the regressions reach those types indirectly through `TransactionQueue.RWLock` and detector orchestration. This is a known limitation of symbol-name pairing and is not evidence of missing line or branch coverage.

## Production seams required for the expected regressions

1. **Deadline arbitration:** `ReadWriteLock.LockWork` must process whichever of lock expiry and deadlock-analysis deadline is earliest/due, without immediately rescheduling an already-expired timestamp. A production time abstraction would further improve determinism, but was not introduced here.
2. **Snapshot ownership:** `DeadlockDetector.Batch` needs per-silo graph contributions so a newer accepted snapshot replaces, rather than only adds to, that silo's prior edges while preserving a coherent batch view.
3. **Break identity/idempotency:** the deadlock-break contract and delivery path need batch/group/transaction generation identity. `TransactionalResource.BreakLocks()` must reject duplicate or stale delivery instead of aborting the group current at delivery time.

## Constraints and blockers

- `code-testing-extensions` was reported unavailable by the caller and was not retried.
- `test-analysis-extensions` was unavailable as a skill; the installed .NET base extension was read directly.
- No source fix was attempted; therefore three regression tests correctly remain red.
- No coverage threshold was requested, so no coverage artifact was collected.
- No version-control restore/reset/clean/stash/revert/delete, commit, or push was performed.
