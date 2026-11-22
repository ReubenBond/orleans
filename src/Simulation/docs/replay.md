# Replay and schedule exploration

Clockwork persists controlled executions as canonical UTF-8 JSON. The format name is
`clockwork.replay`; the current schema version is `2`.

## Artifact contract

An artifact records:

- simulation and schedule seeds, scheduling strategy, strategy options, and hard execution bounds;
- the simulated processor count (`scheduler.options.processorCount`), which the program observes as
  `Environment.ProcessorCount` and which sizes `Parallel` loops and range partitioners;
- Clockwork/runtime compatibility plus optional instrumentation manifest, rule-set, and assembly hashes;
- ordered scheduler choices, resource-winner and wait-resolution decisions, virtual deadlines, and the most recent race
  scheduling points (`SimulationScheduler.RaceTraceCapacity`, 4,096 by default; a scenario which raises it before
  registering work recovers earlier points on replay);
- completion, fault, cancellation, race, deadlock, bound, or aborted outcome with a stable failure identity;
- terminal operation/resource wait graph, pending timer queue, race pair, and deadlock cycles.

JSON property order and number/string formatting are stable. Decision sequences are contiguous and
ordered. Unknown optional properties are ignored within schema version 2. Incompatible format/schema
versions require a reader update and fail before execution.

Schema version 2 retains the historical JSON property name `rootSeed`; current APIs and diagnostics
call this value `SimulationSeed`. This naming change does not alter existing artifact bytes or replay.

Artifacts are limited to 16 MiB, 250,000 decisions, 250,000 race points, 4,096 assembly identities,
64 scheduler options, and 8,192 UTF-16 characters per string. Corrupt, oversized, truncated, and
incompatible artifacts are rejected.

Process arguments, environment variables, stack traces, arbitrary object values, caller work
descriptions, and source paths are not retained by default. API callers can explicitly retain bounded
diagnostic messages and source paths.

## Scenario harness

The CLI executes only a public `IReplayScenario` type in an explicitly supplied assembly:

```csharp
public sealed class TransferScenario : IReplayScenario
{
    public void Configure(SimulationScheduler scheduler)
    {
        scheduler.Schedule("sender", scheduler.Yield);
        scheduler.Schedule("receiver", () => { });
    }
}
```

Loading this harness executes application code. Clockwork does not scan for scenarios, infer a command,
or launch an arbitrary child process.

The runtime also supports real cluster scenarios without requiring a separate harness type:

```csharp
ReplayExecutionResult recorded = ReplayRunner.RecordCluster(
    new ReplayRecordingOptions
    {
        SimulationSeed = 123,
        SchedulingPolicy = ReplaySchedulingPolicy.SeededRandom,
        ScheduleSeed = 7,
    },
    cluster =>
    {
        SimulationNode sender = cluster.AddNode("sender");
        SimulationNode receiver = cluster.AddNode("receiver");
        sender.Context.SchedulerLane.Enqueue(cluster.Scheduler.Yield);
        receiver.Context.SchedulerLane.Enqueue(static () => { });
    },
    cancellationToken);

ReplayExecutionResult replayed = ReplayRunner.ReplayCluster(
    recorded.Artifact,
    ReplayCompatibilityRequirements.Current(),
    cluster => ConfigureClusterScenario(cluster),
    maxSteps: 1_000_000,
    cancellationToken);
```

A recording runs with `ReplayRecordingOptions.ProcessorCount` (4 by default), and replay runs with the count the
artifact recorded, so a seed replays on any machine. An artifact without a valid recorded count fails before scenario
code runs with a `ReplayCompatibilityException`, as does one whose count differs from
`ReplayCompatibilityRequirements.ProcessorCount` when the caller sets it (`ReplayTestConfiguration.ProcessorCount`
sets it for environment-selected replays).

`RecordCluster` and `ReplayCluster` create a fresh cluster at `DateTimeOffset.UnixEpoch` in UTC,
drive it to a terminal state, capture diagnostics before cleanup, detach replay recording, and then
dispose it. Cluster disposal therefore cannot append decisions or consume the recorded replay stream.
Artifacts identify whether they require the scheduler or cluster execution host and reject the wrong
replay entry point.

A cluster scenario may instead return the run's entry task, through separately named methods:

```csharp
ReplayExecutionResult recorded = ReplayRunner.RecordClusterEntry(
    options,
    async cluster =>
    {
        SimulationNode node = cluster.AddNode("node");
        await RunWorkloadAsync(node);
    },
    cancellationToken);

ReplayExecutionResult replayed = ReplayRunner.ReplayClusterEntry(
    recorded.Artifact,
    ReplayCompatibilityRequirements.Current(),
    async cluster => await RunScenarioAsync(cluster),
    maxSteps: 1_000_000,
    cancellationToken);
```

The runner starts an entry-task scenario on the cluster's task scheduler, as
`SimulationCluster.RunToCompletion` does, so its continuations stay on the controlled scheduler.
`ScheduleExplorer.ExploreClusterEntry` explores the same form. The names are distinct so that an
`async` lambda or a `Task`-returning call passed to `RecordCluster`, `ReplayCluster`, or
`ExploreCluster` still binds to the synchronous `Action` form: it registers work, and the runner drives
that work. An entry-task artifact records `entry: task` in `scheduler.options`, alongside
`executionHost`; an artifact without it has a synchronous callback entry. Replaying an artifact in the
other form fails before scenario code runs with a `ReplayCompatibilityException` naming both forms.

## Outcome classification

The entry is the scenario. An entry-task scenario's returned task (`RecordClusterEntry`) is the entry
task, and the run ends when it completes, as a process ends when `Main` returns. A synchronous callback
(`Record`, `RecordCluster`, `IReplayScenario.Configure`) is an entry which ran to completion when it
returned; the run's work is the controlled work it registered, which the runner drives until it
quiesces. An exception from a synchronous callback aborts the recording.

The runner drives one step at a time and stops at the first of:

- a detected race, or an operation fault (the first exception that escapes a controlled operation's
  body; a lane callback's escaping exception also stops the run as an unhandled callback failure);
- a controlled API called off the simulation's logical thread, which aborts the run;
- completion of the entry task;
- quiescence: nothing is runnable and no timer is pending;
- the step bound.

A synchronous entry has already completed, so its run continues until quiescence or the bound.

The outcome is the first rule that applies. Recording and replay use the same rules; replay compares
only the kind and failure identity.

| Outcome | Rule | Failure identity |
|---|---|---|
| `Aborted` | The synchronous callback, or the runner outside any controlled operation, threw before a terminal boundary, or a controlled API ran off the simulation's logical thread (`SimulationThreadAffinityException`, see [thread affinity](compatibility.md#thread-affinity)). Not replayable. | exception type |
| `RaceDetected` | Race exploration found a data race. | `race:<hash>` |
| `Faulted` | An exception, including an `OperationCanceledException`, escaped a controlled operation's body. | concrete exception type |
| `Deadlocked` | The terminal wait-for graph has a cycle, and the entry is a synchronous callback or an incomplete entry task. | `deadlock:<hash>` |
| `BoundExceeded` | The step bound was reached while the entry task was incomplete or, for a synchronous entry, operations were still pending. | `step-bound` |
| `Completed` | Otherwise: the entry task ran to completion. | none |
| `Canceled` | Otherwise: the entry task ended canceled. | `entry-task-canceled` |
| `Faulted` | Otherwise: the entry task faulted. | type of its first inner exception |
| `Deadlocked` | Otherwise: the entry task had not completed at quiescence. | `entry-task-incomplete` |

An exception escaping an operation body is unhandled, as it is in .NET, where it crashes the process:
a `Thread` body, a thread-pool work item, a `System.Threading.Timer` callback, a scheduler-lane or
synchronization-context callback, or a raw `SimulationScheduler.Schedule` body. This includes an
`OperationCanceledException`; the failure identity is its concrete type, such as
`System.OperationCanceledException` or `System.Threading.Tasks.TaskCanceledException`. Task-backed
operations (`Task.Run`, `TaskFactory.StartNew`, continuations, and async methods) capture their
delegate's exception, including cancellation, in their task, so their operations complete and a fault
or cancellation the program observes through the task does not make the run `Faulted`.
`System.Timers.Timer` suppresses exceptions from `Elapsed` handlers, as .NET does. A `Join` on a thread
whose body threw returns, observing the terminated thread, because `Join` never surfaces a thread's
exception; the run itself has already faulted.

A canceled operation never decides the outcome. In .NET, cancellation is an ordinary result, and the
scheduler also cancels operations as bookkeeping: for example, the continuation a `Task.WaitAsync`
withdraws when its timeout or token wins, or a lane callback whose timer is reset before it runs.
An operation is canceled only by the scheduler (`SimulationScheduler.Cancel`, such withdrawals, node
detachment, or teardown), never by an exception from its own body. Every operation's terminal state is
recorded in the artifact's operation diagnostics; `ReplayDiagnosticSnapshot.CanceledOperations` lists
the canceled ones in id order, and `trace show` prints their count and ids.

After an entry task completes, the runner does not drive leftover work, and that work never changes
the outcome: the diagnostics report its operations, liveness, virtual time, and any wait-for cycle
(`DeadlockCycles`) at that boundary, whether or not unrelated work is still pending. A wait-for cycle
found while the entry task is incomplete, or at the quiescence that ends a synchronous entry's run, is
`Deadlocked`; other work still pending at that quiescence is reported but does not change the outcome.
Diagnostics are captured before teardown, so scheduler teardown and the cluster disposal that follows
never appear in the artifact. A node detached during the run, such as one whose attachment failed,
cancels its pending operations; they appear as canceled operations.

## Commands

```powershell
dotnet orleans-simulation record `
  --assembly .\tests.dll `
  --scenario-type Tests.TransferScenario `
  --artifact .\artifacts\transfer.cwr.json `
  --simulation-seed 123 --schedule-seed 7 --strategy seeded-random

dotnet orleans-simulation replay .\artifacts\transfer.cwr.json `
  --assembly .\tests.dll `
  --scenario-type Tests.TransferScenario

dotnet orleans-simulation explore `
  --assembly .\tests.dll `
  --scenario-type Tests.TransferScenario `
  --output .\artifacts `
  --simulation-seed 123 --schedule-seed 1 --count 100 --max-failures 1

dotnet orleans-simulation minimize .\artifacts\transfer.cwr.json `
  --assembly .\tests.dll `
  --scenario-type Tests.TransferScenario `
  --output .\artifacts\transfer.min.cwr.json

dotnet orleans-simulation trace show .\artifacts\transfer.min.cwr.json
dotnet orleans-simulation trace show .\artifacts\transfer.min.cwr.json --json
```

Supply `--manifest <closure-manifest>` when the scenario uses an instrumented closure. Replay compares
the manifest (including its typed copied-asset paths and hashes), rule-set, mode, assembly hashes,
Clockwork runtime version, and .NET compatibility before executing the scenario. Closure manifests use
strict schema version 3; the previous string-array `copiedAssets` shape and schema version 2 are not
accepted.

Exit code `6` reports a reproduced scenario failure, `7` reports artifact/compatibility/divergence
failure, and `8` reports minimization failure. Existing usage, configuration, closure,
instrumentation, and I/O codes remain unchanged.

## Exploration and minimization

Exploration is serial. The simulation seed remains fixed while schedule seeds increase deterministically from
`FirstScheduleSeed`. Stop controls include iteration count, failure count, per-iteration
step bound, cancellation, and a between-iteration wall-clock safety bound. The result contains stable
iteration ids and outcome counts and retains the smallest artifact for each failure identity.
`ScheduleExplorer.ExploreCluster` applies the same campaign policy while constructing and disposing a
fresh `SimulationCluster` for every iteration.
When `--time-limit` is supplied without `--count`, the CLI runs until the time or failure limit.

The minimizer removes decision subsequences and tries discrete scheduling/resource alternatives. Each
candidate runs through exact replay. Compatibility rejection, first decision divergence, surplus or
truncated streams, and a changed failure identity reject the candidate. Progress is ordered and bounded
by attempt count and optional wall-clock time.

## Test integration

`Orleans.Testing.Simulation.Testing.ReplayTestFixture` derives its default simulation seed with
`SimulationSeed.FromStrings(testClass, testMethod)`. Failed runs write an artifact and expose its path
through `ReplayTestResult.ArtifactPath`, `ToFailureMessage()`, and `GetReplayCommand(...)`.

Use `Explore` to execute the same fresh scenario under a deterministic sequence of schedules:

```csharp
ReplayTestCampaignResult result = fixture.Explore(
    scheduler =>
    {
        scheduler.Schedule("sender", scheduler.Yield);
        scheduler.Schedule("receiver", () => { });
    },
    new ReplayTestCampaignOptions
    {
        MaxIterations = 1_000,
        TimeLimit = TimeSpan.FromMinutes(5),
        MaxFailures = 1,
    },
    cancellationToken);

result.ThrowIfFailed();
```

The callback is invoked once per fresh scheduler and must construct fresh scenario state. Explicit
campaign options override environment values. With only a time limit, exploration continues until that
limit; without either bound, campaigns default to 100 iterations. The time limit is checked between
complete iterations, so it never truncates an artifact. Failed campaigns retain the smallest artifact
for each stable failure identity. `ORLEANS_SIMULATION_REPLAY_ARTIFACT` always performs exactly one replay and
cannot be combined with campaign limits.

Environment variables:

| Variable | Meaning |
|---|---|
| `ORLEANS_SIMULATION_REPLAY_ARTIFACT` | Replay this complete artifact instead of recording. |
| `ORLEANS_SIMULATION_SEED` | Override the simulation seed. |
| `ORLEANS_SIMULATION_SCHEDULE_SEED` | Override the schedule seed. |
| `ORLEANS_SIMULATION_ARTIFACT_DIRECTORY` | Directory for failed-test artifacts. |
| `ORLEANS_SIMULATION_EXPLORATION_ITERATIONS` | Override the campaign iteration limit. |
| `ORLEANS_SIMULATION_EXPLORATION_TIME_LIMIT` | Set a positive invariant `TimeSpan` wall-clock safety limit. |
| `ORLEANS_SIMULATION_EXPLORATION_MAX_FAILURES` | Stop after this many non-success outcomes. |

The helper is framework-neutral and works with xUnit/Microsoft.Testing.Platform without coupling the
runtime to either framework.

## Limits

- CLI execution requires an explicit `IReplayScenario`; generic production-process launch is not provided.
- Random and identity values derived solely from stable seed domains do not need individual records.
  A custom nondeterministic choice must be routed through the decision log to become replayable.
- The minimizer preserves failures that exact replay can validate; it does not mutate application state
  or synthesize unrecorded inputs.
- Hosting, transport models, profiler/ReJIT interception, and fault-injection expansion are outside this capability.
