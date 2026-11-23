# Compatibility and capability contract

This document describes Clockwork's supported deterministic execution modes and
platform/deployment contract. It is a durable product document and should be updated,
not duplicated, as supported scope changes.

> **Authoritative summary:** The current capability matrix is
> [README — Current capability contract](../README.md#current-capability-contract). The kernel,
> build/tool rewriting pipeline, analyzers, and built-in BCL/task/synchronization
> rules described there are implemented. This document describes only current behavior and
> durable limitations.

## Intended execution modes

Clockwork supports four modes of running application code against
the deterministic kernel. They trade off fidelity, overhead, and how much of the
application's own concurrency they can observe or control.

### Cooperative mode

The application opts in explicitly: it uses `TimeProvider`, the simulation
`TaskScheduler`/`SynchronizationContext`, `SimulationRandom`, and the simulated
network surfaces directly, as described in the README's "Determinism requirements"
section. This remains available without instrumentation. The application author explicitly routes
nondeterminism through Clockwork APIs.

### Controlled mode

Clockwork verifies usage with shipped Roslyn diagnostics and redirects the exact built-in rule
inventory through opt-in IL rewriting. Controlled async/task/thread/thread-pool/Parallel and modern
synchronization surfaces run on logical strands. Controlled code never falls through to an inactive
or unregistered runtime service: it fails explicitly instead. (Completing a `TaskCompletionSource`
is the one exception: it passes through to the BCL outside a simulation.) Controlled code also runs only
on the simulation's logical thread (see [Thread affinity](#thread-affinity)). Timers, delays, asynchronous timeouts,
and timer-driven cancellation use the shared virtual-time deadline scheduler.

#### Thread affinity

A simulation's logical thread is, at any moment, the thread running one of its scheduler's operations
or the one thread holding `SimulationScheduler.EnterControlScope()`
(`SimulationScheduler.IsSimulationThread`). The cluster's drive methods (`RunUntil`, `RunUntilIdle`,
`RunFor`, `RunToCompletion`), its disposal, and `ReplayRunner` hold that scope on the calling thread. A
second thread entering it while it is held throws `SimulationSchedulerException`. Every controlled entry
point called with a simulation's ambient context requires the logical thread, and on any other thread
throws `SimulationThreadAffinityException` naming the API, the thread, and the runtime and seed, before
it does anything else. The exception-hardening guard injected into `catch` blocks and filters reads no
simulation state and runs on any thread, so escaped work's own handlers run as written.

Simulation affinity is logical rather than physical. `SimulationScheduler.IsSimulationThread` is the
authoritative check. A synchronous callback which `ReplayTestFixture.Run`/`ReplayRunner.Record` invokes
directly under the control scope runs on the caller thread. More generally, the first synchronous
segment of code entered directly under that scope can remain on the caller until it queues or yields.
`SimulationCluster.RunToCompletion` schedules its root task as scheduler-owned work; root-task
segments, captured continuations, lane callbacks, and other queued operations execute on threads named
`SimulationOperationWorker-*`. Continuations and later operations can use a different worker from an
earlier segment while retaining the same `SimulationSynchronizationContext`, runtime identity, and
logical affinity. `Environment.CurrentManagedThreadId` equality is not a simulation guarantee.

The ambient context flows with the `ExecutionContext`, so work which uninstrumented code moves onto a
real thread carries it there: the real `Task.Run` with which a host's call to `BackgroundService.StartAsync`
starts a rewritten `ExecuteAsync` (see the [.NET 11 note](#supported-today)), a timer, thread-pool item, or
continuation queued by an uninstrumented library, or a `CancellationToken.Register` callback which a
thread outside the simulation runs by cancelling the token. That work races the coordinator, so it
fails at its first controlled call. Start it from instrumented code instead, so it runs as a controlled
operation; for a hosted service, implement `IHostedService` in the system under test rather than
deriving from the shared framework's `BackgroundService`, whose `StartAsync` rewritten code cannot call: the call
site is rejected (`BackgroundWork` family).

The scheduler records the first violation, with the escaped thread's stack, and rethrows it from its
next step on the driving thread whether or not an operation runs, so the run fails even when the escaped
work swallows the exception, as a faulted `ExecuteTask` does. Cluster disposal collects a violation not
yet rethrown among its failures, and `ReplayRunner` ends the run as `Aborted`, since the escaped work ran
outside the recorded schedule. Detection follows the escaped work's own timing: a violation which
happens after disposal reaches only the escaped thread, and an exception which escapes a real timer or
thread-pool callback ends the process, as in .NET.

Two kinds of call legitimately run off an operation thread. A `TaskCompletionSource` completion with
no ambient context (from a finalizer, say) passes through to the BCL. Host code which runs simulation
callbacks inline while no step is in progress - cancelling a token which the system under test
registered callbacks on, between drives - runs them on the logical thread inside `EnterControlScope()`.
The cluster cancels `TeardownCancellationToken` that way, at disposal or, once the token passed to its
constructor is cancelled wherever that happens, from the next step of a cluster drive method. Pass the system under
test a token which the scenario owns and cancels from simulation work, rather than a token another
thread cancels, such as a test framework's timeout token.

#### Scheduling and resource model

Controlled mode and cooperative mode share one `SimulationScheduler`. It owns operation selection,
continuations, node lanes, virtual time, and timers, ensuring that at most one logical operation executes
system-under-test code at a time. Logical execution ids are independent of physical thread ids and flow
through `SimulationExecutionContext` into decisions and diagnostics. `SimulationSchedulerLane` is only a
node-scoped scheduling and diagnostic facade; it does not own a separate queue or execution pump.

An operation keeps one physical worker while its stack is live. A controlled synchronous wait or pause
therefore retains the stack and worker until the operation resumes or is canceled. Once an operation
completes, faults, or cooperatively unwinds after cancellation, its worker returns to the scheduler's
bounded idle pool and can execute a later operation. Scheduler disposal unwinds parked operations,
stops idle workers, and joins every worker before returning.

`SimulationCluster.DisposeAsync` is a draining teardown boundary. It cancels
`TeardownCancellationToken`, drives pending lane callbacks and node-disposal work to quiescence, and
advances virtual time to scheduled deadlines when required, subject to the configured execution
bounds. Pending work is completed rather than discarded; callback and disposal failures are collected
after cleanup.

`SimulationNodeContext.DiscardPendingWork()` is the explicit escape hatch for a member which the
consumer has already stopped or crashed. The scheduler's node identity is the ownership boundary: the
call cancels that node's ready and delayed lane callbacks, callback timers and virtual deadlines,
controlled operations (including readiness continuations), resource waits and their timeout/cancellation
registrations, plus context-owned shared callbacks such as the cluster-lane resume registered by
`SuspendFor`. Cancellation unwinds parked controlled-operation stacks cooperatively and releases their
workers; it does not invoke the discarded callbacks. Work owned by other nodes or by the cluster is
unchanged, discarded nodes are excluded from `SimulationCluster.ActiveNodes`, discarded work disappears
from pending-work diagnostics, and its deadlines cannot advance virtual time during later cluster
disposal.

Discard is a permanent lifecycle transition to `SimulationNodeState.Discarded`. It is idempotent while
the context remains attached, and every later node-owned enqueue is rejected with
`ObjectDisposedException`, including task-scheduler, synchronization-context, timer, continuation, and
direct scheduler paths. A node cannot discard itself from one of its currently running callbacks:
that call throws `InvalidOperationException` before changing state. Suspended nodes may be discarded.
After cluster disposal detaches the context, discard throws `ObjectDisposedException`. The consumer
must complete any shutdown or disposal logic which needs to schedule node work before discarding it;
the cluster still invokes the node object's disposer, but node-scoped scheduling from that disposer is
rejected. Nodes which are not explicitly discarded retain the draining teardown behavior.

The reusable resource model provides stable resource identities, ownership and capacity metadata,
deterministic waiter queues, atomic pause/wakeup, virtual-time deadlines, synchronous cancellation,
wait-for graphs, deadlock/liveness diagnostics, and FIFO, round-robin, seeded-random, priority, and exact
replay scheduling strategies. Release, timeout, and cancellation races resolve exactly once; equal virtual
deadlines fire by registration order. Fairness is deliberately limited to deterministic, replayable waiter
selection and does not claim BCL fairness.
Built-in strategies are created through `SimulationSchedulingStrategies`; custom
`ISimulationSchedulingStrategy` implementations remain supported.

The built-in task and synchronization shims use this model for monitors, semaphores, wait handles,
synchronous task waits, timers, and modern synchronization primitives. Controlled instrumentation remains
opt-in; uninstrumented applications can continue to use cooperative mode directly.

### Race exploration mode

`RaceExploration` is an explicit instrumentation mode (`--mode RaceExploration`,
`<OrleansSimulationInstrumentationMode>RaceExploration</OrleansSimulationInstrumentationMode>`, or JSON
`"mode": "RaceExploration"`). `Controlled` remains the default and runs none of the fine-grained
passes, so normal controlled binaries incur no memory/control-flow instrumentation calls.

The Cecil pass injects:

- tracked reads/writes for reference-type instance fields (`ldfld`/`stfld`), static fields
  (`ldsfld`/`stsfld`), and one-dimensional vector array elements (`ldelem.*`/`stelem.*`);
- schedule-only points for volatile fields, field-address and indirect/object accesses
  (`ldflda`/`ldsflda`, `ldind.*`/`stind.*`, `ldobj`/`stobj`, `cpobj`, `initobj`, and `ldelema`) where
  the evaluation stack does not retain enough information for a stable object-plus-offset identity;
- control-flow points at `brtrue`/`brfalse`, matching Coyote's memory-access rewriting coverage;
- direct concrete calls on `List<T>`, `Dictionary<TKey,TValue>`, and `HashSet<T>` as tracked
  collection accesses, plus scheduling points for `ConcurrentBag<T>`,
  `ConcurrentDictionary<TKey,TValue>`, `ConcurrentQueue<T>`, and `ConcurrentStack<T>`.

Injected calls carry the original containing method, IL offset, portable-PDB document, and source
line. Branch targets, volatile prefixes, exception-handler boundaries, and return values remain
valid. Constructors and property accessors follow Coyote's documented exclusions. Compiler-generated
`MoveNext` methods are intentionally visited: generated value-type state fields get schedule-only
points, while accesses reached through a hoisted `this` or closure object retain object-field identity.

The runtime assigns weak, monotonically ordered identities through `ConditionalWeakTable`; reports and
location tables retain only numeric identities. Two accesses to one tracked location race when at
least one is a write and neither happens before the other. Happens-before orders each operation's
events, each operation after the operation that registered it, and each controlled release before
the acquires that observe it: Monitor/C# `lock`, `System.Threading.Lock`, the write and upgradeable
holds of `ReaderWriterLockSlim`, read-lock exits before later writers, `SemaphoreSlim`, controlled
task completion and waits, and scheduler resources and events. Every recorded lock is exclusive, so
accesses under a common lock are ordered by its release-to-acquire edges; the locks an access holds
label its report. Two operations which hold a lock at once as one logical strand, which happens only
where an operation runs as another's strand, are not ordered by it, and their accesses race. The
first race is selected deterministically and returned as a distinct `RaceDetected` structured outcome
rather than thrown through user code. No verdict, report, or scheduling decision depends on
garbage-collection timing.

The detector retains the running operations and, for each live location, its last write and the
reads since then which are mutually unordered. A static field's location lives for the run; a field
or element's lives as long as its object, and the detector releases a collected object's locations
at a sweep, due once the tracked objects' locations have doubled since the last. Lock and signal
state is freed with its synchronization object. Vector clocks are bounded by the running operations
and the clock slots of those retained accesses, so a continuation chain or loop does not grow them;
a program which keeps many mutually unordered reads of a live location retains each of them, since a
later write must be checked against each.

Each run retains the most recent `SimulationScheduler.RaceTraceCapacity` race scheduling points
(4,096 by default, at most 250,000, the replay artifact limit) for `CaptureRaceSchedulingPoints`,
race reports, and replay artifacts; each point's sequence number counts every point the run reached.
Replaying the run with a larger capacity, set by the scenario before it registers work, recovers the
earlier points.

Race reports differ from earlier builds in five respects: a write racing several reads names the
read which ran first; a write racing the last write names that write; `sync#N` labels number locks
alone, in the order they are first used; accesses of two operations holding a lock at once as one
logical strand are reported rather than suppressed by the lock; and a lock which another operation of
its holder's strand exits, or whose holder ends while holding it, orders that operation's accesses
before the lock's next holder.

**Exact limits:** multidimensional/non-vector arrays lowered to helper calls, interface-typed collection
calls, reflection/dynamic invocation, `Span<T>`/native pointers, unmanaged memory, and offsets derived
from arbitrary managed pointers are not assigned tracked locations. They remain schedule-only where an
IL opcode is identifiable. Concurrent collections are interleaving points but are not themselves
reported as data races. Collection coverage is direct-member call coverage, not whole-type substitution;
members reached only through `ICollection<T>`/`IDictionary<TKey,TValue>` are outside the inventory.
Tail-prefixed collection calls are left intact because the CLI requires `tail.` adjacency and a direct
call-to-return flow; injecting after such a call would invalidate the method.
Replay artifacts, bounded schedule exploration, and trace minimization operate over these scheduling
points. This capability does not add profiler/native detours or runtime hosting/transport interception.

### Replay and schedule exploration

Replay artifacts use canonical UTF-8 JSON with format identity `clockwork.replay` and schema version 2.
They record the simulation seed, explicit schedule seed, strategy/options (including the
[simulated processor count](#simulated-processor-count), which replay runs with), Clockwork/runtime compatibility,
optional closure-manifest and assembly hashes, ordered scheduler/resource/timer decisions, race
scheduling points, terminal outcome, and structured operation/resource/timer/race/deadlock diagnostics.
Readers ignore unknown optional properties within a supported schema version. A schema version,
runtime version, rule-set, manifest, or assembly hash mismatch fails before scenario code executes.

Recording and replay are exposed through `ReplayRunner`. Replay validates every decision at the first
divergence, including expected/actual source, candidate metadata, and selected result, then verifies
that the complete decision stream was consumed at the reproduced terminal boundary. An `Aborted`
outcome identifies an interrupted decision prefix and is rejected for exact replay.

The terminal outcome is `Aborted`, `RaceDetected`, `Faulted` for an exception that escapes an operation
body, `Deadlocked` for a wait-for cycle, or `BoundExceeded` when one applies, and otherwise the entry
task's own terminal state. For an asynchronous entry (`RecordClusterEntry`, `ReplayClusterEntry`,
`ExploreClusterEntry`), the entry task is the task the scenario returns, and the runner stops when it
completes, as a process ends when `Main` returns: work left at that point, including a wait-for cycle,
is reported in the diagnostics and does not change the outcome. A synchronous scenario callback
(`Record`, `RecordCluster`, `ExploreCluster`) is an entry which completed when it returned; its run
continues until the work it registered quiesces or reaches a bound, and a wait-for cycle at that
quiescence is `Deadlocked`. Artifacts record the entry form, and replaying one in the other form is
rejected. An exception, including an `OperationCanceledException`, that escapes a `Thread` body,
thread-pool item, timer callback, scheduler-lane or synchronization-context callback, or raw `Schedule`
body is unhandled, as in .NET, and faults the run with its concrete type; task-backed operations such as
`Task.Run` capture it in their task instead. Canceled operations, including the continuation a
`Task.WaitAsync` withdraws when its timeout or token wins, are diagnostics and never make a run
`Canceled`. See [`replay.md`](replay.md#outcome-classification) for the exact rules and failure
identities.

`ScheduleExplorer` varies only the schedule seed while keeping the simulation seed fixed.
Iterations run serially; iteration, failure, step, cancellation, and optional wall-clock safety bounds
are explicit. Iteration ids are deterministic, aggregate outcomes are stable, and the smallest artifact
per failure identity is retained. Parallel exploration is rejected until runtime/instrumentation
isolation can be guaranteed.

`ReplayTraceMinimizer` uses bounded delta debugging plus discrete scheduling/resource alternative
selection. A candidate is accepted only if exact replay passes compatibility and first-divergence
validation and reproduces the original failure category and identity.

Artifacts exclude process arguments, environment variables, arbitrary user metadata, stack traces,
caller work descriptions, and source paths by default. Caller descriptions/source paths can be
explicitly retained through the API. Per-field, decision, race-point, assembly, and total-byte limits
are enforced before use.

### Build-time instrumentation boundary

Clockwork's deepest supported instrumentation is opt-in, out-of-place caller rewriting performed
by `Microsoft.Orleans.Testing.Simulation.Instrumentation.Build` or
`Microsoft.Orleans.Testing.Simulation.Tool` before execution. It can rewrite
application and selected dependency assemblies, including third-party managed callers, but does
not rewrite framework assemblies, mixed-mode/native code, or calls originating inside excluded
vendor binaries. Those boundaries require an explicit Clockwork model, rejection, or consumer
adapter when they affect simulation behavior.

Clockwork does not provide a CLR profiler/ReJIT component, startup-hook or
`AssemblyLoadContext` load-time rewriting, or native detours. Runtime interception is explicitly
out of scope: it would compete with coverage and APM profilers, add platform-specific native
deployment and security burden, and still not cover NativeAOT or unmanaged behavior. The current
consumer evidence does not show a determinism blocker that would justify those costs.

The build package and CLI use Mono.Cecil; see
[Third-party notices](../THIRD-PARTY-NOTICES.md).

#### Rewrite engine

The **generic IL rewrite engine** in `Orleans.Testing.Simulation.Instrumentation` uses Mono.Cecil 0.11.6. It is a deterministic,
rule-driven Cecil transformation pipeline plus an extensive golden test corpus, and
does not perform runtime interception. The engine (`RewriteEngine.Rewrite`) takes a caller-supplied, versioned
`RewriteRuleSet` and a set of replacement ("shim") assemblies, applies an ordered set
of passes to the input assembly, validates the result by reading it back, and emits a
deterministic `InstrumentationManifest`. The rule model integrates the simulation API
policy classification (`Controlled`/`Rejected`) without the engine referencing any
concrete shim by identity. APIs remain unchanged only when callers omit their rule or
exclude the containing assembly from instrumentation.

**Supported IL transformations (verified by the golden corpus):**

- static and instance `call`/`callvirt` redirection to a static replacement method
  (the instance receiver becomes the replacement's first argument);
- `newobj` redirection to a static factory method;
- generic-instance methods (type arguments carried onto the replacement) and calls
  embedded in generic types;
- type-reference substitution in method bodies (`newarr`, `castclass`, `isinst`,
  `box`, `unbox(.any)`, `ldtoken`, `initobj`, `sizeof`, `constrained.`), including operands composed
  from a substituted type such as the `constrained. List<Barrier>.Enumerator` prefix of a `foreach`;
- substitution in signatures composed from a substituted type: fields, locals, parameters and return
  types, base types and interfaces (`List<Barrier>`, `IEnumerable<CountdownEvent>`), explicit interface
  implementations, and generic parameter constraints (`where T : IEnumerable<Barrier>`). A type which
  derives directly from a substituted type fails the rewrite (`CWR0001`) rather than invoking the
  replacement's members on an object of a different type;
- optional post-call wrapping (interception after a matched call);
- deterministic rejection injection before an unsupported/forbidden invocation;
- correct behavior when the redirected site sits inside by-ref/array/constrained-
  generic/delegate/async-state-machine/iterator/nested-type shapes and inside
  `try`/`catch`/filter/`finally` regions, with exception-handler and branch
  boundaries repaired when instructions are inserted or replaced;
- portable and embedded PDB preservation with per-site source mapping; one-for-one
  replacements retain Cecil's offset-based sequence points. Member substitutions record
  the exact operand instruction offset and nearest source point when a call site exists;
  structural type/member edits without a call site use offset `-1` rather than a fabricated
  `IL_0000`. Absent or unsupported symbols are reported (`CWR0004`/`CWR0005`), never silently dropped;
- assembly/rule-set/options-level idempotence markers: re-running with the same rule set and
  semantic rewrite options is a verified no-op, and an incompatible rule set or options
  fingerprint fails clearly (`CWR0008`) rather than double-rewriting;
- type exclusion — a type marked `[DoNotRewrite]` or named in `ExcludedTypeFullNames` is not rewritten,
  and neither are the compiler-generated types nested in it (marked `[CompilerGenerated]`, or nested in
  such a type: async and iterator state machines, lambda and local-function closures, and an async
  lambda's state machine in its closure), transitively, so the type and its helpers stay consistent and
  its code runs with BCL behaviour, uncontrolled. An explicitly declared nested type keeps its own
  annotation. Because its lambdas and local functions are excluded with it, a callback an excluded type hands
  to simulated code runs uncontrolled too: its body uses real time, real timers, and the thread pool, so
  excluding a type removes its callbacks from the simulation. Each excluded type is recorded in the manifest
  (`CWR0006`). Rewritten code cannot cross the boundary with a substituted type: a reference into an excluded
  type whose signature mentions one, or an implementation or override across it whose signature does, fails
  (`CWR0001`), as below; nor can excluded code use a rewritten type whose base type, interfaces, or
  constraints the rewrite substitutes, nor rewritten code an excluded type that keeps such a part;
- strict resolution — a targeted member whose replacement cannot be resolved is a hard
  failure (`CWR0001`); a targeted call is never silently skipped. A member reference whose signature a
  type substitution rewrites, but whose declaring type it keeps, binds only where that type is rewritten
  too, so the rewrite fails (`CWR0001`) for a member of an assembly the closure does not rewrite (the
  framework, a replacement assembly, or an excluded assembly) or of a type excluded from rewriting, and for
  a field of an excluded type whose type is substituted. Code excluded from rewriting keeps the original
  signatures, so an excluded type's reference to a member of its module whose signature the rewrite changed
  fails too. An interface implementation or override is matched to its member by signature when the type
  loads, so one whose signature the rewrite changes on one side only (between a rewritten type and an
  excluded type or a type of an assembly the closure does not rewrite, including where one of them names the
  implemented interface or base type with a substituted generic argument, which the rewrite substitutes in a
  rewritten type only) fails (`CWR0001`) rather than throwing `TypeLoadException`. An explicit
  implementation's declaration is retargeted with the member it implements, and fails (`CWR0001`) where that
  member does not bind after rewriting: the substitute lacks it, or its declaring type keeps the original
  signature. The rewrite also substitutes a rewritten type's base type, interfaces, and generic parameter
  constraints, and a method's constraints, while code excluded from rewriting keeps the original ones, so an
  excluded type that mentions a type or method of its module so reshaped, or a type deriving from one, fails
  (`CWR0001`): it could use the type through a base type or interface it no longer has (adding an element of
  the original type to what is now a list of the substitute throws `ArrayTypeMismatchException`), or
  instantiate it with a type that satisfies only the original constraint. Conversely, rewritten code sees
  such a part as composed from the substitute, so rewritten code that mentions a type which keeps a base type,
  interface, or constraint composed from a substituted type (an excluded type, a type of an assembly the
  closure does not rewrite, or a type deriving from one, such as an excluded `Old : List<Barrier>` that
  rewritten code would use as a `List<ControlledBarrier>`), or a generic method of such a type that keeps
  such a constraint, fails (`CWR0001`) too. Any mention counts, because IL does not spell out a conversion
  to a base type or interface. The closure runner tells the engine which assemblies it
  rewrites; a direct engine caller that does not is taken to rewrite none, so every such reference into
  another assembly fails closed. The set is part of the options fingerprint, so an already-rewritten input
  reused in a closure that rewrites different assemblies fails (`CWR0008`). Once the closure is staged, the
  runner resolves every reference into a rewritten assembly that mentions a substituted or substitute type,
  from a copied assembly or an excluded type, checks every implementation and override across assemblies the
  same way, and fails the run (`CWR0001`) if one no longer binds, if a copied assembly or an excluded type
  mentions a reshaped type or method of a rewritten assembly, including a type of its own assembly deriving
  from one, or if rewritten code mentions a type that another rewritten assembly excludes, or that derives
  from one, which keeps a part composed from a substituted type. It resolves the staged closure as the
  runtime binds it: a framework assembly is the runtime's own copy, and any other assembly is the one in the
  staging directory, whose subdirectories (another target framework's build, say) are not searched. A cycle
  of type forwarders met while resolving fails the rewrite or the run (`CWR0003`) rather than overflowing the
  stack. Read-back validation resolves every such reference whose declaring assembly the closure does not
  change (this assembly, the framework, a replacement assembly, or an excluded one) and reports one that
  does not bind (`CWR0009`).

**Engine boundary:** load-time `AssemblyLoadContext` hooks, profiler/native detours, mixed-mode
rewriting, Authenticode re-signing, application hosting, and transport interception are not
provided. Mixed-mode assemblies are rejected (`CWR0011`). The engine supplies IL transformation
mechanics; build and CLI orchestration remain separate opt-in entry points.

#### Build and tool integration

The build and command-line entry points are generic, strictly opt-in plumbing that fails explicitly rather than
silently degrading. It ships two packages:
`Microsoft.Orleans.Testing.Simulation.Instrumentation.Build` (an MSBuild task with
`build/` props and targets, a development dependency) and
`Microsoft.Orleans.Testing.Simulation.Tool` (the `orleans-simulation` CLI).

**Opt-in only.** An ordinary build never instruments. The `OrleansSimulationInstrument` target
runs `AfterTargets="Build"` only when the consumer sets
`OrleansSimulationInstrumentationEnabled=true` and supplies at least one `@(OrleansSimulationRuleSet)`
document. It discovers the full resolved managed closure (honoring `.deps.json`, runtimeconfig,
satellite/resource/native assets, and include/exclude globs). Framework and reference assemblies
are always copied rather than rewritten, and an explicit include cannot override that safety
boundary. It rewrites **only managed IL** out-of-place under
`obj/<Config>/<Tfm>/orleans-simulation/instrumented/`, copies the non-managed assets needed to run
the staged app unchanged, and emits a manifest under
`obj/<Config>/<Tfm>/orleans-simulation/orleans-simulation.manifest.json`. Source and `bin` outputs are never
mutated. The work is incremental, keyed by every input asset's hash, the rule-set signature,
engine version, manifest schema, configuration, and reference set. Each copied native library,
dependency/runtime configuration file, symbol file, replacement assembly, and race-runtime asset is
recorded as a typed manifest entry containing its closure-relative path and lower-case SHA-256.
Incremental hits verify the staged hash of every rewritten assembly and copied asset; a missing or
modified output invalidates the hit and rebuilds the full staged closure. Closure manifests use strict
schema version 3; `copiedAssets` entries are objects containing `relativePath` and `sha256`. Version 2
is rejected rather than interpreted through a compatibility shape. Manifests are limited to 16 MiB,
4,096 assembly entries, 65,536 copied assets, and 8,192 UTF-16 characters per string.

**Instrumented test projects.** Executable test projects can set
`OrleansSimulationInstrumentedTestProject=true`. After an ordinary project build, the package snapshots the
complete test output under `obj` and automatically selects the test assembly plus every
eligible managed assembly in its resolved closure. It rewrites that complete simulation closure out
of place and validates every manifest assembly and rewrite signature before deploying the result to
that simulation test project's `bin` directory. Strong-name identities are stripped automatically
from rewritten assemblies, together with their intra-closure reference tokens and
`InternalsVisibleTo` public-key qualifiers. Test-host implementation assemblies are automatically
excluded because discovery and runner startup execute before a simulation exists. The test entry
assembly and Clockwork kernel are also copied unchanged so async test methods can create the
simulation before controlled code runs; the complete application/dependency closure remains eligible
for rewriting. The next build restores the pristine snapshot first, so
incremental compilation and instrumentation-mode changes never consume a previously rewritten input.
Because the rewritten test copy occupies the project's normal module path, `dotnet build` followed
by `dotnet test --no-build` uses it without runner-specific dispatch hooks. Production project
outputs and non-opted-in test projects remain ordinary IL. Instrumentation must be selected per
project, never using a solution-wide `OrleansSimulationInstrumentationEnabled` global property.

**Task package requires the .NET 11 SDK.** The task and its Cecil-based engine target
`net11.0` and load only under `dotnet build` / `dotnet msbuild`; .NET Framework MSBuild
(classic `msbuild.exe`) cannot host them. The `Microsoft.Orleans.Testing.Simulation.Tool` CLI exposes `rewrite`
(with `--dry-run`) and `inspect` (text or `--json`), with nonzero exit codes classified
by failure kind. The tool also exposes explicit `IReplayScenario` harness commands for
`run`/`replay`/`explore`/`minimize`/`trace show`; it never discovers a process or scenario type
implicitly.

**Target runtime.** Rules are classified against a .NET runtime's surface, so every rewrite evaluates them
against a target runtime, a feature version such as `11.0`. The configuration file's `targetRuntime`, the
`OrleansSimulationTargetRuntime` MSBuild property, and the CLI's `--target-runtime` set it; when more than one is set, they
must agree. Without any of them, it is the runtime the entry assembly's `*.runtimeconfig.json` names:
`Microsoft.NETCore.App`, or the ASP.NET Core or Windows Desktop framework, which ship in lockstep with it. That file
is what the host reads to choose a runtime, which the target framework stops naming once an app rolls forward. A
closure whose runtime cannot be determined, such as a library's build output without a runtimeconfig.json, fails
until one is set; the engine API requires one. The build log and CLI output name the target each run used and where
it came from.

Before anything is staged, the selected rules must support the target: each rule's `supportedRuntimes` includes it,
or rules with the same target that do between them rewrite every kind of site (call, `newobj`, type reference) it
rewrites. A rule set can so carry one rule per runtime for an API. A rule for an API the target runtime lacks
should include that runtime, since no call site can name the API there. Otherwise the run fails once with
`CWR0010`, naming the rule set, the runtimes its rules support, and example rules. The built-in rules support target
runtime `11.0`.

**Runtime check in every simulation.** Each rewritten assembly records the runtimes the selected rules support (the
intersection of the `supportedRuntimes` of the rules that support the target) as
`[assembly: AssemblyMetadata("Clockwork.SupportedRuntimes", "[11.0,11.0]")]`. A `SimulationScheduler` checks every
loaded assembly against the feature version of `Environment.Version` when it is constructed, so every
`SimulationCluster`, replay, and exploration does too, and again after any scheduling step during which an assembly
loaded, since code under test often loads behind a delegate after the simulation starts. A mismatch fails with
`UnsupportedRuntimeException` naming the assembly, the runtimes it supports, and the one the process runs, and fails
again at every later check. This catches what a build cannot see: a `rollForward` policy in runtimeconfig.json or
`DOTNET_ROLL_FORWARD` that runs the app on a newer runtime than the rules were classified against, whose surface
and internals (such as the `ValueTask` field `ControlledValueTask` reads) the shims depend on. Each assembly's
metadata is read once per process, and a step after which nothing loaded checks nothing.
**Configuration is data, not code.** Configuration and rule sets are JSON documents,
validated strictly for schema, types, and signatures; **no arbitrary code is executed
from configuration**. Multiple rule sets merge deterministically by a defined precedence shared
by built-in, application, and third-party rules. Instrumentation configuration files must declare
`"schemaVersion": 2`. Its exact optional fields are `ruleSets`, `mode`, `builtInRuleSets`,
`builtInIncludeFamilies`, `builtInExcludeFamilies`, `include`, `exclude`, and `targetRuntime`;
unknown fields are rejected. Version 1 is rejected, and there are no migration
or compatibility aliases.

**Strong naming (build/tool scope).** Signed, public-signed, and delay-signed inputs are
detected. Clockwork automatically strips strong-name identities from every rewritten assembly and
removes matching public-key tokens from references within the rewritten closure. Friend-assembly
public-key qualifiers are removed at the same time, so the transformed closure remains internally
consistent without signing keys. This is safe for isolated simulation/test artifacts; instrumented
assemblies are not production replacements. **Authenticode** signatures are detected
and reported as unsupported (CWR0104) - they are never re-applied, and a rewritten assembly does not
retain its Authenticode signature; re-sign such outputs with your own toolchain after
instrumentation. An instrumented test project's staged closure runs only in its own test output, so
the build targets report CWR0104 there as a message rather than a warning.

**ReadyToRun (build/tool scope).** R2R/native sections are detected and always stripped by
round-tripping the usable managed IL through Cecil before rewriting, producing IL-only staged
output with no stale native code. Mixed-mode images and images without usable managed IL are
rejected. Instrument before single-file bundling and Native AOT; those published forms cannot be
recovered to a rewriteable managed closure.

#### Deterministic BCL rule set

The built-in simulation rule set `clockwork.bcl.deterministic` (version `3.0.0`),
redirects the direct **static** time / identity / random BCL surface to Cecil-free runtime
shims in the `Orleans.Testing.Simulation` assembly, under namespaces matching
`Orleans.Testing.Simulation.Shims.<framework namespace>`. The complete, exhaustive
list of controlled and rejected signatures is generated into
[`rule-inventory.md`](rule-inventory.md) and verified against the shipped rules by a test, so
the published inventory can never drift from the code.

**Surface classification.** Every public and protected member of each type the built-in rules
target is controlled or rejected by a rule, or is listed in the inventory's *Members left as BCL
calls* section with the reason a call to it stays a BCL call: `Value` for `DateTime.AddDays`,
`HostState` for `Environment.MachineName`, `Blocks` for `SynchronizationContext.WaitHelper`, and so on. A type is
targeted when a rule names or substitutes it, or when a member's signature mentions a class,
struct, or interface of `System.Diagnostics.Process.dll`, so a process API is classified wherever
the framework adds it.
Tests classify each targeted type against the .NET 11 reference assemblies with the rewriter's own
rule matcher and substitution mapper, and fail on an unclassified member or on a listed member that
the reference assemblies lack or a rule matches. A constructor counts as rewritten only if rules
rewrite both its `newobj` and a `call` to it, which a derived type's constructor or a value type
constructed in place makes. Snapshots of the reference pack's
assembly list and of the public types in the namespaces of the targeted types fail when a runtime
adds or removes one, prompting a review of it. A member that starts, signals, waits on, reschedules,
or terminates a process is rejected; every other member no rule rewrites is listed with its
behavior, and the reasons the inventory marks as documented holes (`CultureOrTimeZone`,
`HostState`, `Process`, `RealClock`, `ProcessWideId`, `CurrentThread`, `IdentityHandle`,
`ConstructorCall`, `Blocks`, and `Schedules`) are real BCL calls under simulation.

Instrumented closure binaries are simulation/test artifacts, not production replacements. Every
Controlled entry point requires an active Clockwork simulation; without one it throws
`SimulationNotActiveException` before any real BCL operation runs. It also requires the simulation's
logical thread, and elsewhere throws `SimulationThreadAffinityException` (see
[Thread affinity](#thread-affinity)). With an active simulation:

- **With a registered runtime environment** it dispatches to the node's
  simulated clock and the correct independent seed domain (Application/Identity only - never
  the scheduler, network, or Buggify domains).
- **Runtime completeness** installs the deterministic environment and task coordinator atomically
  before a runtime can become ambient. There is no active-but-unconfigured state and no process-wide
  service registry.

Uninstrumented production binaries retain ordinary BCL behavior. The runtime inventory names are
`ControlledDateTime`, `ControlledDateTimeOffset`, `ControlledStopwatch`, `ControlledEnvironment`,
`ControlledGuid`, `ControlledRandom`, `ControlledRandomNumberGenerator`,
`SimulationRandomNumberGenerator`, and `SimulationStableHash`.

Semantics: local-time clocks (`DateTime.Now`/`Today`, `DateTimeOffset.Now`) honour the
configured simulation time zone; `Environment.TickCount`/`TickCount64` wrap with correct
`int`/`long` behaviour; `Stopwatch.GetTimestamp`/`GetElapsedTime(long)` are machine-independent.
`Guid.NewGuid` draws deterministic bytes while preserving RFC 4122 variant and version 4;
`Guid.CreateVersion7` encodes the simulated UTC millisecond timestamp in the first 48 bits with
version 7 (no monotonicity guarantee beyond the BCL contract). `Random.Shared` and unseeded
`new Random()` become per-node deterministic streams; explicitly seeded `new Random(int)`
preserves the caller's seed exactly. Cryptographic randomness (`RandomNumberGenerator` static
APIs and controlled factory instances) draws deterministic, per-node non-cryptographic bytes from a
stream isolated from application randomness, identity generation, scheduling, networking, and fault
injection.

**Opt-in.** No JSON authoring is required. MSBuild consumers set
`<OrleansSimulationUseBuiltInRules>true</OrleansSimulationUseBuiltInRules>`; CLI consumers pass
`--builtin clockwork.bcl.deterministic` (or `--builtin all`) with optional
`--builtin-include`/`--builtin-exclude` family filters. The selected families are versioned and folded into the rule-set signature,
so incremental rebuilds stay correct.

#### Controlled task and async rule set

The second built-in simulation rule set, `clockwork.tasks.controlled` (version `2.0.0`),
controls the compiler-generated async machinery and the direct `Task` surface that ordinary
application code uses, so `async`/`await` runs on the simulation's single logical thread instead
of the physical thread pool. It is selected independently of the BCL rule set (CLI
`--builtin clockwork.tasks.controlled`, or `--builtin all` for both). The exhaustive controlled
and rejected signature list is generated into [`rule-inventory.md`](rule-inventory.md) and
verified against the shipped rules by a test.

The rule set has two halves. A **member-aware type substitution** pass retargets the
compiler-generated builder and awaiter types of an `async` state machine onto controlled
value-type equivalents in `Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices`
(`AsyncTaskMethodBuilder`(`<T>`) → `ControlledAsyncTaskMethodBuilder`(`<T>`); `TaskAwaiter`(`<T>`),
`ConfiguredTaskAwaitable`(`<T>`)`/ConfiguredTaskAwaiter`, and `YieldAwaitable`/`YieldAwaiter` →
their `Controlled…` counterparts), rewriting field, local, method- and field-reference, and
type-operand metadata (including closed generic instances such as `TaskAwaiter<int>`) so a Debug
or Release state machine is fully controlled. The same pass also retargets the
`async ValueTask`/`async ValueTask<T>` machinery — `AsyncValueTaskMethodBuilder`(`<T>`),
`ValueTaskAwaiter`(`<T>`), and `ConfiguredValueTaskAwaitable`(`<T>`)`/ConfiguredValueTaskAwaiter` →
their `Controlled…` counterparts (the `ValueTaskMachinery` family) — so value-task `async`/`await`
and `ValueTask.ConfigureAwait(false)` are controlled identically. The same family substitutes the
configured async disposable and enumerable that `await using` and `await foreach` consume
(`ConfiguredAsyncDisposable`, `ConfiguredCancelableAsyncEnumerable<T>` and its `Enumerator` →
`ControlledConfiguredAsyncDisposable`, `ControlledConfiguredCancelableAsyncEnumerable<T>`), whose BCL
members return configured value-task awaitables, and redirects the `TaskAsyncEnumerableExtensions.ConfigureAwait`
and `WithCancellation` extensions that produce them. As in the BCL, the configured enumerable passes its token to
`GetAsyncEnumerator(CancellationToken)`, where an async iterator combines it with its `[EnumeratorCancellation]`
parameter, and each `MoveNextAsync`/`DisposeAsync` value task is configured with the requested
`continueOnCapturedContext`, which the controlled awaitable treats as `ValueTask.ConfigureAwait(bool)` does. The controlled awaiter hands
every continuation to the simulation coordinator rather than the awaited task's completion
callback, which is exactly why **`ConfigureAwait(false)` stays controlled**. A method compiled with runtime
async (`MethodImplOptions.Async`) has no state machine whose builder and awaiters could be substituted, and
the runtime resumes it through its own continuation machinery, so rewriting one with a rule set that
substitutes the async method builders fails with `CWR0013` naming the method and its assembly; compile it
without runtime async (remove the `runtime-async=on` compiler feature), or exclude an assembly that cannot be
recompiled from rewriting, which leaves it uncontrolled. The runtime-async
`AsyncHelpers.Await` intrinsics that take a configured task or value-task awaitable resume their caller
through the runtime's own continuation machinery instead, so they are rejected by name
(`SimulationApiException`); compile such methods without runtime async. `AsyncHelpers.HandleAsyncEntryPoint`,
through which an async `Main` returns (`task.GetAwaiter().GetResult()`), waits through the controlled awaiter.
`Task.ConfigureAwait(ConfigureAwaitOptions)` and `Task<T>.ConfigureAwait(ConfigureAwaitOptions)` build
the same controlled awaitables: `SuppressThrowing` completes the await normally for a faulted or
canceled `Task`, `ForceYielding` always yields through the coordinator, and invalid options (including
`SuppressThrowing` on `Task<T>`) throw the BCL's `ArgumentOutOfRangeException` from the BCL's
`ConfigureAwait` frame, as a call the JIT does not inline does. As in the BCL,
`GetAwaiter()` or `ConfigureAwait` on a null task throws `NullReferenceException` and the controlled
awaiters keep their frames out of a rethrown exception's stack trace. A synchronous
`GetAwaiter().GetResult()` on an incomplete task or value task pumps the coordinator until it completes
instead of blocking. Uninstrumented
production binaries retain normal BCL async semantics. A
**call-site redirect** half routes both the
non-generic `Task.WhenAll` / `Task.WhenAny` (array, span, pair, enumerable) combinators **and their
generic `Task<T>` overloads** (array, span, enumerable, and the `WhenAny<T>` pair), the synchronous
`Task.Wait()` / `Task.WaitAll` / `Task.WaitAny(Task[])` waits, the blocking generic
`Task<T>.Result` accessor, the `TaskExtensions.Unwrap` extension methods, all 6 `Task.WhenEach` overloads, all 40
`Task.ContinueWith`/`Task<T>.ContinueWith` overloads, all 48 `TaskFactory`/`TaskFactory<T>`
`ContinueWhenAll`/`ContinueWhenAny` overloads, the `ValueTask`/`ValueTask<T>` `AsTask` and `Preserve`
conversions, and the completion members of `TaskCompletionSource`/`TaskCompletionSource<T>` to shims in
`Orleans.Testing.Simulation.Shims.System.Threading.Tasks`, and rejects every `TaskFactory`/`TaskFactory<T>` `FromAsync` overload at
the call site. Synchronous waits **pump the
coordinator loop until completion instead of blocking a physical thread**, then delegate to the
real API to reproduce its exact `AggregateException` semantics, so a synchronous wait or a blocking
`Task<T>.Result` read on incomplete controlled work never deadlocks the scheduler.
`TaskAsyncEnumerableExtensions.ToBlockingEnumerable` redirects to
`ControlledTaskAsyncEnumerableExtensions`, whose enumerable pumps the coordinator until each
`MoveNextAsync` and the final `DisposeAsync` complete; as in the BCL, enumeration is deferred, the
token reaches `GetAsyncEnumerator`, the source enumerator is disposed on completion, early exit, and
fault, and exceptions propagate unwrapped.
`Task.Run` and `TaskFactory.StartNew` queue controlled operations instead of escaping, while
`Task.Delay` and `Task.WaitAsync` register virtual deadlines.

**Clockwork registers no BCL continuation on a task.** A continuation the BCL registers inherits the
task's dispatch: completing a `TaskCompletionSource` created with `RunContinuationsAsynchronously`, a
channel's `Completion`, a controlled timer's `DisposeAsync`, or a value-task source which runs continuations
asynchronously (a channel's pending `ReadAsync`/`WaitToReadAsync`, a `ManualResetValueTaskSourceCore<T>` with
`RunContinuationsAsynchronously`) would run it on the real thread pool, so the result it completes would become
ready at a nondeterministic scheduling step. Instead:

- **Combinators.** `WhenAll`, `WhenAny`, and `Unwrap` return a result the BCL computes synchronously when their
  antecedents have already completed (the BCL's own result, including `Task.CompletedTask` for no tasks, the
  task itself for one task to the non-generic `WhenAll`, and the inner task for an outer task which has already
  completed successfully). Otherwise the coordinator polls the antecedents' `IsCompleted` through a readiness wait,
  as the controlled awaiters do, and a controlled operation completes a promise with the BCL's outcome, including
  its exception dispatch information, the cancellation token and exception it picks, and the generic results in
  argument order. Argument validation throws the BCL's exceptions, parameter names, and messages. `WhenAny` returns
  the first antecedent, in argument order, among those the coordinator observes complete. The non-generic `WhenAll`
  aggregates faults, and picks the canceled task, in the order the coordinator observes its antecedents complete
  (as the BCL uses completion order); antecedents observed in the same scheduling step are ordered by argument
  position. The generic `WhenAll` uses argument order, as the BCL does.
- **`ContinueWith`.** The returned task is a genuine BCL continuation of a private gate, so its status, creation
  options, state, cancellation (including `LazyCancellation`), `NotOn*`/`OnlyOn*` filtering, and exception and
  `OperationCanceledException` handling are the BCL's own. The coordinator observes the antecedent complete and a
  controlled operation completes the gate with the antecedent's status (never its exceptions, so a fault the
  continuation ignores stays unobserved and raises `TaskScheduler.UnobservedTaskException`, as in the BCL), which
  runs the continuation inline on that operation, on a fresh logical strand in the execution context captured by
  `ContinueWith`. `ExecuteSynchronously` is a hint, as in
  the BCL: the continuation never runs inside the call which completes the antecedent or inside `ContinueWith`.
  `LongRunning` (a dedicated thread) and `AttachedToParent` (controlled tasks have no parent) cannot be preserved
  and throw `SimulationApiException`. The overloads without a scheduler use `TaskScheduler.Default` rather than
  `TaskScheduler.Current`, as the controlled `TaskFactory.StartNew` treats a factory without one: inside a simulation
  `TaskScheduler.Current` is `TaskScheduler.Default` or the simulation's own `SimulationTaskScheduler` (in the root
  task of `SimulationCluster.RunToCompletion` before its first await, for example), and the coordinator runs the
  continuation either way. An explicit `TaskScheduler.Default` or `SimulationTaskScheduler` is accepted on the same
  terms, so `TaskScheduler.Current` inside the continuation is `TaskScheduler.Default`; any other explicit scheduler
  throws `SimulationApiException`.
- **`Task.WhenEach`.** The BCL registers a completion action on each task when `WhenEach` is called, and yields
  the tasks in the order those actions run through an async iterator which resumes on the thread pool. The
  controlled sequence has the coordinator observe the tasks complete from the call on (tasks complete at the call
  in argument order) until each has completed or the enumeration which claimed them has ended, and records the
  order it observes. `MoveNextAsync` returns a value task which a controlled operation completes once a task is
  available, and which a controlled await observes through the coordinator. Argument validation, the single
  enumeration (the first enumerator to start yields every task; any other yields none), cancellation through the
  enumerator's token (`OperationCanceledException` when a task is available, `TaskCanceledException` while waiting
  for one), `Current`, and disposal are the BCL's. The enumerator acquires each yielded task's race-detection edge.
- **`TaskFactory.ContinueWhenAll`/`ContinueWhenAny`.** As for `ContinueWith`, the returned task is a genuine BCL
  continuation of a private gate, which the coordinator completes once it observes every antecedent complete, or
  one, so its status, creation options, state (the continuation delegate, as in the BCL), and cancellation are the
  BCL's. Argument validation follows the BCL's order and messages, and a token already canceled without
  `LazyCancellation` yields a task canceled at creation, with no state, as in the BCL. `ContinueWhenAll` passes the
  continuation a defensive copy of the antecedents; `ContinueWhenAny` passes the first antecedent, in argument order,
  among those observed complete. The overloads without a scheduler use the factory's, or `TaskScheduler.Default`
  rather than `TaskScheduler.Current`; `LongRunning`, `AttachedToParent`, and schedulers other than
  `TaskScheduler.Default` and `SimulationTaskScheduler` throw `SimulationApiException`, as for `ContinueWith`.
- **`TaskFactory.FromAsync`.** Every overload of both factories is rejected at the call site with
  `SimulationApiException`: the task completes when an `IAsyncResult` completes, on a thread of its
  implementation's choosing, outside Clockwork's control. Await a task-returning API instead.
- **`ValueTask.AsTask`/`Preserve`.** A value task backed by a pending `IValueTaskSource` is polled through the
  source's `GetStatus` by the coordinator, with no completion callback registered on the source, and a controlled
  operation consumes the completed source once through the BCL's own conversion (one `GetStatus`, one `GetResult`).
  Any other value task is converted by the BCL synchronously, so `AsTask` of a value task wrapping a task returns
  that task.
- **`TaskCompletionSource`.** Rewritten code keeps the real `TaskCompletionSource` and `TaskCompletionSource<T>`
  types, with their real `CreationOptions`, so their identity is unchanged at the boundary with uninstrumented
  code. Their completion members (`SetResult`, `SetException`, `SetCanceled`, `SetFromTask`, and the `TrySet*`
  forms) redirect to shims which complete the source through the same BCL member and publish the race-detection
  release edge on its task; an await, synchronous wait, combinator, or continuation which observes the task
  acquires it, and the combinators carry their antecedents' edges to their results. The constructors which take
  `TaskCreationOptions` redirect to factories which reject `AttachedToParent` inside a simulation with
  `SimulationApiException`, as `new Task`, `StartNew`, and `ContinueWith` do: a promise which attaches to the task
  running it makes that parent fault when it faults under the BCL, and Clockwork's controlled tasks do not model
  parent and child tasks. The factories otherwise construct the source with the BCL's options, state, and validation. Inside a simulation each completion shim
  completes the source where the BCL inlines continuations (see
  [Completions from rewritten code](#completions-from-rewritten-code)).

The redirect obeys the same simulation-only invariant as the BCL rule set: instrumented Controlled
builders, awaiters, and shims require an active Clockwork simulation. Continuations and waits route
through the coordinator carried by the complete ambient runtime and cannot silently escape to the
thread pool. The `TaskCompletionSource` completion members are the one exception: a finalizer, or a
callback from uninstrumented code, may complete a source outside any simulation, so outside one their
shims pass through to the BCL member, and they record the race-detection edge only inside a controlled
operation.

A blocking `ValueTask<T>.Result` read pumps the coordinator until the value task completes, as `Task<T>.Result` does,
then returns the BCL's result or throws its exception.
Other APIs absent from the generated rule inventory are outside the support claim. The task surfaces which can
still run a continuation outside the coordinator are:

- **Continuations registered by uninstrumented code** (an assembly excluded from rewriting, or the framework itself)
  on a task or value-task source follow the BCL's dispatch. A `RunContinuationsAsynchronously` completion source
  completed in rewritten code still runs them on the thread pool; Clockwork's own continuations of the same task
  stay controlled. Instrument the assemblies which await simulation work. The framework's own async loops over
  simulation work which Clockwork knows of are controlled: `Parallel.ForEachAsync` and `Parallel.ForAsync`
  ([Parallel loops](#parallel-loops)), `ChannelReader<T>.ReadAllAsync` and the inherited base
  `ReadAsync`/`WriteAsync` ([Channels](#channels)), and the base asynchronous members of `Stream`, `TextReader`, and
  `TextWriter` ([Stream, TextReader, and TextWriter asynchronous members](#stream-textreader-and-textwriter-asynchronous-members)).
  Inside a task which uninstrumented code constructs and instrumented code starts, `TaskScheduler.Current` is the
  coordinator's scheduler: uninstrumented code which continues on it after a completion on another thread is reported
  as a thread-affinity violation rather than run there.
- **Race-detection release edges** are published by rewritten completions (completion sources, async methods
  returning a `Task` or a `ValueTask`,
  `Task.Run`, `TaskFactory.StartNew`, a controlled timer's `DisposeAsync`, and the controlled combinators and
  continuations). A task completed by
  uninstrumented code, including a channel's internal operations, publishes none, so the detector does not order
  accesses through it. Nor does `CancellationTokenSource.Cancel` publish one: a `ContinueWith`, `ContinueWhenAll`, or
  `ContinueWhenAny` canceled through its token before its antecedents complete is completed by the canceller, so a
  write before `Cancel` and a read after awaiting the canceled continuation may be reported as a race that is not
  one, until `Cancel` publishes a release edge.

**Remaining deviations from the BCL.** The coordinator observes completions between scheduling steps, so
`WhenAny`, the non-generic `WhenAll`, `WhenEach`, and `ContinueWhenAny` order antecedents completed within one step
by argument position rather than by their exact completion order; a combinator, continuation, value-task conversion,
or `WhenEach` enumerator whose antecedent is still pending completes one controlled operation after the antecedent
rather than inside the completing call; and a combinator or continuation whose antecedent never completes remains
pending simulation work, as an await of that antecedent does (for `WhenEach`, until its enumeration ends, or until
every task has completed if nothing enumerates it). `ControlledTimer.DisposeAsync` completes its task without
`RunContinuationsAsynchronously`, so its `CreationOptions` report `None`. The task `SemaphoreSlim.WaitAsync` returns is
still incomplete when the `Release`, `Cancel`, or `Dispose` which resolves it returns, and completes in a controlled
operation of its own, where the BCL completes it inside that call and runs only its continuations asynchronously
([Completions from rewritten code](#completions-from-rewritten-code)). Because Clockwork's own continuations never
run inside the completing call, Clockwork cannot surface bugs where such a continuation runs inline under the
completer's lock (a deadlock or reentrancy the BCL's synchronous continuations can cause). A continuation which
uninstrumented code registered on the current synchronization context, whose task rewritten code completes while that
context is still current, is posted to it, one controlled operation later, where the BCL runs it inline; and code which
runs inside a completion sees no synchronization context and the default scheduler, where the BCL runs that code under
the completer's context and scheduler ([Completions from rewritten code](#completions-from-rewritten-code)).
`DocumentedDeviationConformanceTests` pins each of these deviations, with the BCL's value beside it.

##### Completions from rewritten code

The BCL runs a continuation registered as a bare state machine (an await with `ConfigureAwait(false)`, or one
registered where no synchronization context or task scheduler was current) inline in the call which completes its task
only where no synchronization context other than the base one is current, `TaskScheduler.Current` is
`TaskScheduler.Default`, and the stack has room, as on a thread-pool thread. Anywhere else it queues the continuation to
the real thread pool.

Simulation code completes tasks in such other places. A scheduler lane's work runs with the lane's
`SimulationSynchronizationContext` current; the root task of `SimulationCluster.RunToCompletion`, and any other task on
a `SimulationTaskScheduler`, run visibly on that scheduler; and a task which uninstrumented code constructs and rewritten
code starts runs visibly on the coordinator's scheduler. Those contexts and schedulers stay current for the rest of that
work, so uninstrumented code there continues and schedules work on the simulation, as
[cooperative mode](#cooperative-mode) relies on. Every completion which Clockwork makes for simulation code therefore
runs where the BCL inlines continuations: with no synchronization context, and, where another scheduler is current,
inside a task which hides it, run inline on the logical thread. That covers the `TaskCompletionSource` and
`ManualResetValueTaskSourceCore<T>` completion shims, `CancellationTokenSource.Cancel` and the cancellations its
virtual timer and `CancelAsync` make, the controlled synchronization primitives' waiters, rendezvous, and timers, and the
promises of the controlled combinators and continuations. A bare continuation of the task then runs inline, on the
logical thread, as the BCL runs it on a thread-pool thread. Inside the `Orleans.Testing.Simulation` assembly, analyzer rule `CW1004` rejects
any call to these completion members outside that one path.

Around the completion the BCL's behavior is kept where it differs:

- **Asynchronous completions stay asynchronous.** The BCL completes the task `SemaphoreSlim.WaitAsync` returns with
  `RunContinuationsAsynchronously`, so no continuation runs inside the `Release`, `Cancel`, or `Dispose` which resolves
  the waiter. Clockwork fixes the waiter's outcome in that call and completes its task in a controlled operation of its
  own, on the logical thread, so the task itself is not yet complete when that call returns (a remaining deviation). The
  synchronous waits of the other primitives complete inside the call, as in the BCL.
- **`Send` runs inline on the owning lane.** `SimulationSynchronizationContext.Send` runs its callback inline when the
  calling thread is executing that context's lane's work, whichever context is current, so a callback registered with
  `CancellationToken.Register(..., useSynchronizationContext: true)` runs inside `Cancel`, as in the BCL. A `Send` made
  from another lane's work or from a controlled operation cannot pump the lane: it throws
  `SimulationSchedulerException` and fails the run.
- **A nearly exhausted stack fails the run.** The BCL checks the stack several frames inside a completion and queues
  the continuations it would inline to the thread pool where too little remains. A completion therefore requires 32 KB
  beyond the BCL's own margin, and otherwise throws `SimulationApiException` and fails the run before it has any effect,
  even if simulation code catches the exception. That covers the continuations which the completion itself runs inline;
  a chain of continuations which complete further tasks inline goes deeper, where the BCL checks only its own margin.

Code which runs inside a completion, such as a callback which `Cancel` runs or a continuation which the BCL runs inline,
sees no synchronization context and the default scheduler. Uninstrumented code there which yields, starts a task
without naming a scheduler, or captures the context reaches the real thread pool, so start such work from rewritten
code. Where a scheduler is hidden, `Task.CurrentId` there is the hiding task's. A continuation which captured the
current context is posted to it rather than run inline (see the remaining deviations above).

Two completions still follow the BCL's dispatch to the thread pool: a task which uninstrumented code completes in a
lane's work, or anywhere else a context or scheduler of the simulation is current; and a continuation of a task which
ran visibly on a Clockwork scheduler, which the BCL runs while that task is current. Hiding the scheduler there would
break cooperative mode. A built-in channel's wait canceled by its token is one such completion (issue #59). A task
which uninstrumented code completes also publishes no race-detection edge, so a framework await between a rewritten
completion and a rewritten continuation leaves their accesses unordered for the race detector.

Continuation placement and the delivery of `SemaphoreSlim.WaitAsync` changed with this contract, so schedules and
replay artifacts recorded before it may replay differently.

`TaskSchedulingConformanceTests` pins each place with a `System.Linq.AsyncEnumerable` pipeline over a task-backed
source, against the same probe run uninstrumented on a thread-pool thread (and, as a negative control, under a
synchronization context), and pins Clog's pattern of rewritten work started fire-and-forget from the cluster's lane.
`SimulationCompletionTests` covers every `TaskCompletionSource` completion shim, `Cancel` with a callback registered on
the context, `Send` from another lane, the `SemaphoreSlim.WaitAsync` waiter's outcomes, the BCL's exceptions on the
hidden-scheduler path, and the exhausted stack. `CompletionEscapeConformanceTests` pins the two completions which still
escape. `TaskCompletionRaceEdgeTests` pins the unordered race-detection case. In the conformance tests, an
`EventListener` on the runtime's thread-pool events fails any test which queues real thread-pool work from the
simulation's logical thread, unless the test pins such an escape.

#### Threads, thread pool, Parallel, and task parity

`clockwork.tasks.controlled` ensures that every unit of concurrent work an application
spawns — a `Thread`, a `Task.Run`/`TaskFactory.StartNew` body, a `ThreadPool.QueueUserWorkItem`
callback, or a `Parallel` branch — is modelled as a **controlled operation scheduled on the same
single logical thread** the async machinery already uses, instead of escaping onto a physical OS
thread or the real thread pool. The exhaustive controlled/rejected signature list is regenerated
into [`rule-inventory.md`](rule-inventory.md); the Coyote parity matrix is
[`coyote-parity.md`](coyote-parity.md).

Controlled surfaces include the full `Task.Run` and `TaskFactory`/
`TaskFactory<T>.StartNew` families; `Task.Start` and `Task.RunSynchronously` for a task constructed with
`new Task(...)`; every `Task.ContinueWith` and `Task<T>.ContinueWith` overload; every `Task.Wait`, `Task.WaitAll`,
and `Task.WaitAny` overload, including the timed and cancellable ones, whose timeouts elapse in virtual time;
`Thread` construction/`Start`/`UnsafeStart`/`Join`/`Sleep`/`Yield`/`SpinWait`; `ThreadPool.QueueUserWorkItem` and
`UnsafeQueueUserWorkItem` (including the generic `Action<TState>` and `IThreadPoolWorkItem` forms);
`Parallel.ForEachAsync`, `Parallel.ForAsync`, `Parallel.Invoke`, `Parallel.For`, and `Parallel.ForEach` (see
[Parallel loops](#parallel-loops)); and the `ChannelReader<T>`/`ChannelWriter<T>` members whose BCL implementations
await inside the framework (see [Channels](#channels)).

##### Task schedulers

Every controlled API which takes a `TaskScheduler` (`Task.Start`, `Task.RunSynchronously`, `TaskFactory.StartNew`,
`ContinueWith`, `ContinueWhenAll`/`ContinueWhenAny`, and `ParallelOptions.TaskScheduler`) applies one policy.
`TaskScheduler.Default`, which the controlled APIs map to the coordinator, and the coordinator's own scheduler run the
work on the caller's node. A
`SimulationTaskScheduler` of the running simulation runs it on its lane's node, so the work suspends and crashes with
that node; `RunSynchronously` on it queues the task to the lane and waits for it, pumping the coordinator, since the
lane never runs a task inline. Any other scheduler throws `SimulationApiException`, because work queued to it would
run outside the coordinator.

The root task of `SimulationCluster.RunToCompletion` runs on the cluster's `SimulationTaskScheduler`, with the cluster's
`SimulationSynchronizationContext` current, so until its first await `TaskScheduler.Current` is that scheduler and
uninstrumented code there awaits, continues, and schedules work on the cluster's lane, as in any other work of a
scheduler lane. Rewritten code continues after an await as a controlled operation, where `TaskScheduler.Current` is
`TaskScheduler.Default`. A completion by rewritten code in the root runs where the BCL inlines continuations
([Completions from rewritten code](#completions-from-rewritten-code)), and its code sees `TaskScheduler.Default`.

##### Constructed tasks

`new Task(...)` and `new Task<TResult>(...)` are controlled. The BCL runs a task's delegate in the `ExecutionContext`
captured when it was constructed, which carries its creator's strand; the controlled constructors make the delegate
run as the strand which executes it instead, so a task started with `Task.Start` owns its own locks, as a task on its
own thread does, and a task run inline with `RunSynchronously` shares its caller's. `TaskCreationOptions.AttachedToParent`
is rejected: Clockwork's controlled tasks do not model parent and child tasks, and a parent waiting for an attached
child would block the simulation's logical thread. The constructed task denies child attachment, as `Task.Run`'s
tasks do, so no child attaches to it from uninstrumented code. It also hides its
scheduler, so inside it
`TaskScheduler.Current` is `TaskScheduler.Default`, as inside a task the BCL starts on the default scheduler; the BCL
runs a framework continuation which the task completes inline only there, and would otherwise queue it to the thread
pool. Validation, `AsyncState`, `CreationOptions` (which report the options the creator passed), and the outcome of a
task constructed with a cancelled token are the BCL's.

`Task.Start` and `Task.RunSynchronously` run the task on Clockwork's coordinator `TaskScheduler`: `Start` queues it
as a fresh controlled operation, as `Task.Run` queues its body, and `RunSynchronously` runs it on the calling logical
thread. The BCL keeps the task's state, so its status transitions (`WaitingToRun` after `Start`), its rejection of a
second start, and the execution context captured at construction are the BCL's. A task which uninstrumented code
constructs without `DenyChildAttach` may gain attached children while it runs; `RunSynchronously` runs its delegate
inline and then waits for those children with the controlled wait, pumping the coordinator, so the BCL's
`RunSynchronously` finds the task complete and never blocks the logical thread on them. Inside such a task
`TaskScheduler.Current` is the coordinator scheduler, which every controlled API that takes a scheduler accepts as
it accepts `TaskScheduler.Default`. A timed or cancellable `Wait`, `WaitAll`, or `WaitAny` pumps the coordinator
until the tasks complete, the token is cancelled, or its deadline elapses, then lets the BCL compute the result
and exceptions from the completed tasks. `Thread.UnsafeStart` starts the controlled thread without flowing the
caller's `ExecutionContext`, as the BCL does.

**Deliberate deviations from real BCL semantics:**

- **Cooperative, non-preemptive execution.** A controlled thread/threadpool/Parallel body runs as a
  single scheduling unit; it interleaves with other controlled work only at explicit yield points
  (`await`, `Task.Yield`, `Thread.Yield`, `Thread.Sleep`, `Join`, a blocking `Task` wait). This is
  faithful for the async-first concurrency Clockwork targets, but a purely synchronous CPU loop with
  no yield point does **not** interleave the way real preemptive threads would. `SimulationScheduler`
  is the live backend for both task continuations and controlled operations, but fully preemptive
  synchronous interleaving is not supported.
- **`Thread.Sleep` / `Thread.Join(timeout)` are virtual waits.** They yield the logical thread
  through the deterministic loop rather than consuming real wall-clock time. The same virtual-time
  scheduler now backs timers, delays, asynchronous timeouts, and timer-driven cancellation.
- **An unhandled exception faults the run instead of terminating the process.** An exception,
  including an `OperationCanceledException`, that escapes a `Thread` body, a thread-pool work item, or
  a timer callback crashes a real process. Here it faults that controlled operation, which a replayed
  run reports as `Faulted`. A `Join` on the thread returns, observing the terminated thread, because
  `Join` never surfaces a thread's exception.
- **Safe vs. unsafe `ExecutionContext` flow is modelled.** `QueueUserWorkItem` captures and flows
  the caller's `ExecutionContext`; `UnsafeQueueUserWorkItem` does not — matching the BCL contract —
  so `AsyncLocal` values observed by the callback differ between the two exactly as they do on the
  real thread pool.
- **OS-specific and un-modellable surfaces are rejected precisely, not silently ignored.** Thread
  `Priority`/apartment-state (including the obsolete `ApartmentState` setter)/`Interrupt`, the `Parallel`
  overloads whose body receives a `ParallelLoopState` (break/stop and thread-local `TLocal` state), and
  `ThreadPool.UnsafeQueueNativeOverlapped` all fail at the rewritten call
  site with a diagnostic that names the exact API. The registered-wait APIs
  (`RegisterWaitForSingleObject`/`UnsafeRegisterWaitForSingleObject`) are **controlled**
  (see below) — they depend on controlled wait handles, which now exist.
- **Uncontrolled process/termination APIs are rejected *unconditionally*.** `Process.Start`/`Run`/
  `RunAsync`/`RunAndCaptureText`/`RunAndCaptureTextAsync`/`StartAndForget`/`Kill`/`Signal`, the
  `WaitForExit`/`WaitForExitAsync`/`WaitForExitStatus`/`TryWaitForExitStatus`/`WaitForExitStatusAsync` waits,
  the `ReadAllBytes`/`ReadAllText`/`ReadAllLines` reads and their async forms, the `WaitForInputIdle`
  waits, the `Responding` getter, `CloseMainWindow`, the `EnableRaisingEvents` setter and `BeginOutputReadLine`/`BeginErrorReadLine`
  (whose events the BCL raises on threads of its choosing), the `Process` priority, processor-affinity,
  and working-set setters and the `ProcessThread` scheduling setters, the `SafeProcessHandle`
  `Start`/`Signal`/`Resume`/`Kill` and wait members, the `WindowsProcessStartArguments`/
  `UnixProcessStartArguments` `Start` launchers, and
  `Environment.Exit`/`FailFast` throw whether or not a simulation is active (a rewritten assembly must
  never launch, signal, wait on, reschedule, or tear down a real OS process). This unconditional guard is distinct
  from the simulation-only Controlled invariant, because there is no faithful in-simulation model of
  spawning or killing a process.
- **Cross-assembly uncontrolled-task detection is diagnosis, not wrapping.** With
  `DetectUncontrolledTasks` enabled, a call into an uncontrolled dependency assembly that returns a
  `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` or a custom awaitable is flagged with the `CWR0200`
  warning at the exact call site, so a task whose continuation could escape the coordinator is never
  silently accepted. Clockwork diagnoses rather than runtime-wraps the foreign task (honest about
  what it can prove); framework-hosted I/O remains outside the instrumentation boundary.
- **Exception-handler hardening is defence-in-depth.** With `HardenExceptionHandlers` enabled, a
  `dup; call SimulationExceptionGuard.ThrowIfControlSignal` is injected at the start of every broad
  `catch (Exception)`/`catch`/filter handler so an internal scheduler control signal cannot be
  swallowed by application `catch` blocks. Finally blocks, rethrow-only handlers, and async
  state-machine `SetException` handlers are skipped, and **normal application exception handling is
  unchanged** — the guard is a no-op for every object that is not the internal control signal. This
  layers on top of the runtime's preference for explicit gate/state transitions over control
  exceptions.

Every instrumented Controlled shim requires an active simulation: work routes through the
coordinator, and an active simulation with no registered coordinator throws rather than silently
escaping. The synchronization and virtual-timer capabilities delivered since then are recorded below.

Each mode is intended to be strictly additive: an application written for
cooperative mode should continue to work unmodified under controlled, race
exploration, or deep instrumentation mode.

#### Parallel loops

Every controlled `Parallel` loop runs on a deterministic pool of workers, each a fresh controlled operation on the
coordinator, following the BCL's `ForEachAsync` design: the loop queues one worker, and each worker queues the
next when it takes its first element, until the degree of parallelism is exhausted, so no more than that many bodies
are in flight. A worker takes an element, runs the body, awaits it, and yields to the scheduler before taking
the next, which is where another worker could take the source first on a real machine; the scheduling strategy
therefore chooses the interleaving of the workers' iterations, a seed replays it exactly, and different seeds
explore different interleavings. Workers share an `IEnumerable<T>`'s or `IAsyncEnumerable<T>`'s one enumerator,
taking one element at a time under an asynchronous FIFO lock; synchronous `ForEach` indexes an `IList<T>` (including an
array) over `[0, Count)`, where `ForEachAsync` shares its enumerator as for any sequence; each worker enumerates its
own dynamic partition of a `Partitioner<T>`; integer ranges are taken in ascending order.

- **`ForEachAsync` and `ForAsync`** return a task which the last worker completes with the BCL's outcome:
  cancelled if the caller's token is cancelled, otherwise faulted with every body fault in the order they occurred,
  otherwise successful. The first fault, or the caller's token, cancels the token passed to the bodies and stops
  every worker before its next element. A token cancelled at the call returns a cancelled task without running a
  body, and a source whose `GetEnumerator` throws returns a faulted task, as in the BCL. Without
  `ParallelOptions`, or with `MaxDegreeOfParallelism` of -1, the BCL runs `Environment.ProcessorCount` workers;
  the controlled loop runs the [simulated processor count](#simulated-processor-count) of workers, so a seed
  replays on any machine.
- **`For`, `ForEach`, and `Invoke`** block their caller until the loop completes, so the caller pumps the
  coordinator while the workers run, as every controlled synchronous wait does; a body which waits (a
  `Thread.Sleep`, a blocking `Task` wait) pumps in turn. They throw what the BCL throws: an `AggregateException` of
  the body faults, or a single `OperationCanceledException` when the options' token stopped the loop or every fault
  is a cancellation by that token. The first worker runs on the caller, as the caller's strand and in the context the
  loop captured, as the BCL's root replica does, with a scheduling point between its iterations, so a loop of degree 1
  runs entirely on the caller and may re-enter a lock the caller holds. A loop stops launching iterations after its
  first fault; with `MaxDegreeOfParallelism` of -1 it runs the simulated processor count of workers, standing in for
  the thread-pool threads which run the BCL's replicas. `Invoke` runs every action despite faults, on the same worker
  pool, which checks the token after each action, and aggregates their faults. A source is disposed once the loop
  completes, and a fault there is thrown in place of the loop's outcome, as in the BCL. The returned
  `ParallelLoopResult` reports completion.
- **Schedulers.** An unset `ParallelOptions.TaskScheduler` runs the loop on the caller's node whatever
  `TaskScheduler.Current` is; a set one follows the [scheduler policy](#task-schedulers). A node's
  `SimulationTaskScheduler` runs every worker on that node, so they suspend and crash with it, and a synchronous loop's
  caller only waits, as the BCL's caller waits for a root replica its scheduler declines to run inline.
- **Rejected overloads.** The overloads whose body receives a `ParallelLoopState` (break/stop, and the thread-local
  `TLocal` overloads) throw `SimulationApiException` at the call site: `ParallelLoopState` has no public
  constructor, so Clockwork cannot supply one to a body. Stop a loop through `ParallelOptions.CancellationToken`.
- **Race detection.** Every take of an element synchronizes with the takes before it, as the BCL's lock (for
  `ForEachAsync` and `ForAsync`), interlocked index (for `For`), or partitioner does, and every body happens before
  the loop completes, so accesses after `await Parallel.ForEachAsync(...)` or a synchronous loop are ordered after
  every body's.

Without these rules the BCL's `ForEachAsync` and `ForAsync` queue their workers to the thread pool and resume them
through BCL awaits, so the bodies run on thread-pool threads outside the schedule;
`ParallelLoopConformanceTests` pins that escape with a probe rewritten without them.

##### Simulated processor count

Each simulation has one processor count, which its runtime owns: `SimulationCluster`'s `processorCount` argument
(the `ProcessorCount` property), `ReplayRecordingOptions.ProcessorCount`, `ScheduleExplorationOptions.ProcessorCount`,
and `ReplayTestConfiguration.ProcessorCount` set it, and it defaults to 4. Four workers are enough for the scheduler
to explore interleavings among them, few enough to keep the schedule space and the cost of each run bounded, and the
same on every host, so a seed replays on any machine; set it to model a production machine's count. The replay
artifact records it (`scheduler.options.processorCount`) and replay runs with the recorded value.

- **`Environment.ProcessorCount`** returns it, so code that sizes its own work by the processor count (a worker
  pool, a `MaxDegreeOfParallelism = Environment.ProcessorCount`) behaves the same on every host.
- **`Parallel` loops** without a limit, or with `MaxDegreeOfParallelism` of -1, run that many workers.
- **`Partitioner.Create(fromInclusive, toExclusive)`** (`int` and `long`) divides the range into three times that
  many ranges, the BCL's range size for that processor count, so each worker takes the same ranges on every host.
- **The other `Partitioner.Create` overloads** stay BCL calls. The list, array, and explicit range-size factories
  never read the processor count. `Create(IEnumerable<T>)` reads it only to size a fill buffer which the BCL never
  fills: each partition reads the source only for the chunk it takes, so the source is read at the same points on
  every host. `ProcessorCountConformanceTests` pins that against the BCL.

The framework's own reads of the processor count, inside BCL code no rule reaches, see the host's count; they size
internal structures such as a `ConcurrentDictionary`'s default lock striping.

#### Channels

The built-in channels complete their operations on the calling thread: `TryWrite`, `TryRead`, and `TryComplete`
change the channel's state, and the value tasks which `ReadAsync`, `WaitToReadAsync`, `WriteAsync`, and
`WaitToWriteAsync` return complete when controlled code changes it. A controlled await observes them through the
coordinator without registering a continuation on them, so those members stay BCL calls. `ChannelReader<T>.ReadAllAsync`,
and the base `ChannelReader<T>.ReadAsync` and `ChannelWriter<T>.WriteAsync` which a custom reader or writer inherits,
are instead async methods compiled into the channels assembly, whose awaits resume on the thread pool. They are
controlled: the shim calls a reader's or writer's override as written, and otherwise runs the BCL's algorithm (the
same `WaitToReadAsync`/`TryRead` and `WaitToWriteAsync`/`TryWrite` calls, the same cancellation checks, and the
same `ChannelClosedException`) with controlled awaits. An override's base call (`base.ReadAsync(...)`), which IL
makes as a non-virtual `call`, runs that base algorithm, through the rule's base-call replacement. `ReadAllAsync` reads nothing until it is enumerated, and
combines a token passed to `GetAsyncEnumerator` with its own, as `[EnumeratorCancellation]` does. Its enumerator is the
value-task source of its own `MoveNextAsync`, as the compiler-generated iterator is: a waiting step completes when its
`WaitToReadAsync` does, and reads its element when the awaiter takes its result, so each wake is one controlled
operation that allocates nothing; a step whose element another reader took in between waits again, pumping the
coordinator. `DisposeAsync` during a pending step throws `NotSupportedException`, as the iterator's does, and needs no
active simulation. Analyzer rule `CW1001` reports these three members except a virtual call whose receiver's type
overrides the member.

#### BlockingCollection

`BlockingCollection<T>` blocks a taker on an empty collection, and an adder on a full bounded one, on semaphores and
wait handles inside the framework. Every member which adds, takes, or completes adding is controlled (`Add`, `TryAdd`,
`Take`, `TryTake`, `GetConsumingEnumerable`, `CompleteAdding`, and the static `AddToAny`, `TryAddToAny`,
`TakeFromAny`, and `TryTakeFromAny`), over the real collection. Each first makes the BCL's own attempt with a zero
timeout, which validates the arguments, checks disposal and the token, and adds or takes exactly as the BCL does, in
the BCL's order. When the BCL would have waited, the caller parks on the coordinator until the collection can satisfy
it, adding is completed, the token is cancelled, or the timeout elapses in virtual time, and then retries; a wait
which nothing can satisfy is reported as a deadlock. As in the BCL, `CompleteAdding` makes a waiting taker return
`false` (or `Take` throw), and a waiting adder throw `InvalidOperationException`; a cancelled token throws
`OperationCanceledException`; `GetConsumingEnumerable` takes nothing until it is enumerated. Which of several waiters
proceeds first is the scheduling strategy's choice, as it is the thread scheduler's in the BCL. Every add and
`CompleteAdding` publishes a race-detection edge which every take, and every observation of a completed collection,
acquires. `BlockingCollectionConformanceTests` compares each deterministic scenario with the same probe run
uninstrumented, and pins the escape: without these rules a timed take blocks the logical thread for its whole timeout.

#### Stream, TextReader, and TextWriter asynchronous members

The base asynchronous members of `Stream`, `TextReader`, and `TextWriter`, which a type that overrides only the
synchronous members inherits, run the synchronous member on a thread-pool thread (`Stream.ReadAsync`, `WriteAsync`,
`FlushAsync`; `TextReader.ReadLineAsync`, `ReadToEndAsync`, `ReadAsync`, `ReadBlockAsync`; `TextWriter.WriteAsync`,
`WriteLineAsync`, `FlushAsync`), block the caller on a semaphore or a task (`Stream.BeginRead`/`EndRead`,
`BeginWrite`/`EndWrite`), or await the other members through BCL awaiters (`Stream.CopyToAsync`, `ReadExactlyAsync`,
`ReadAtLeastAsync`, `TextWriter.WriteAsync(StringBuilder)`). They are controlled (`AsyncIO` family). A call to a virtual
member checks, once per receiver type, whether the receiver overrides it, and calls an override as written. Otherwise
the shim runs the BCL's base algorithm with the same calls on the receiver:

- A synchronous read, write, or flush which the BCL runs on the thread pool runs as a fresh controlled operation in the
  caller's execution context, concurrently with the caller, so the scheduling strategy chooses how it interleaves with
  the caller, as the thread pool does. Its task completes as the BCL's does, including cancellation by a token which is
  canceled before the operation starts.
- A stream's base reads and writes are serialized in FIFO order, as the BCL's per-stream semaphore serializes them:
  `ReadAsync` and `WriteAsync` wait for the stream asynchronously and release it when their operation ends;
  `BeginRead` and `BeginWrite` wait for it with the caller parked on the coordinator, and `EndRead` and `EndWrite`
  release it. `EndRead` and `EndWrite` pump the coordinator until the operation completes; the callback runs in its own
  controlled operation after the operation completes. A base `ReadAsync` or `WriteAsync` on a stream which overrides
  `BeginRead`/`EndRead` (or the write pair) completes through those overrides, as the BCL's does.
- Every await in a base algorithm, and in the non-virtual members, is a controlled await of the controlled virtual
  members, so a `CopyToAsync` or `ReadExactlyAsync` over a stream whose `ReadAsync` awaits simulation work resumes on
  the coordinator.

An override's base call (`base.ReadAsync(...)`), a non-virtual `call`, runs the base algorithm through the rule's
base-call replacement. Validation and its exceptions and messages are the BCL's. `DisposeAsync`, whose base calls
`Dispose`, and the synchronous members run on the calling thread and stay BCL calls. Framework code which calls a
receiver's asynchronous members (a `StreamReader` over a custom stream, `Stream.Synchronized`,
`TextWriter.CreateBroadcasting`) is not rewritten, so it reaches the receiver's base members as in the BCL.
`StreamConformanceTests` compares each scenario, including the shapes of Clog's `ArcBufferReadStream` and
`ArcBufferWriteStream`, with the same probe run uninstrumented, and pins the escape: without these rules the base
`ReadAsync` reads on a thread-pool thread.

#### AsyncEnumerable

The `System.Linq.AsyncEnumerable` operators are async methods and iterators in the framework, which await a source's
`MoveNextAsync` with BCL awaiters, some with `ConfigureAwait(false)`. They are not rewritten, and they stay on the
logical thread over sources in rewritten code: a controlled operation runs with no `SynchronizationContext` and the
default `TaskScheduler`, so an operator's await captures no context, and its continuation runs inline in the controlled
operation which completes the source's value task (a rewritten async iterator as it resumes on the coordinator, a
task-backed source as a controlled operation completes its task). A task constructed with `new Task(...)` hides its
scheduler ([Constructed tasks](#constructed-tasks)), so the same holds inside a task started with `Task.Start` or run
with `RunSynchronously`. `AsyncEnumerableConformanceTests` pins both. A task-backed source which rewritten code
completes in a scheduler lane's work, before the first await of the root task of `RunToCompletion`, or in a task on a
`SimulationTaskScheduler` or constructed by uninstrumented code also resumes the operator's bare continuations inline
on the logical thread, since Clockwork makes those completions where the BCL inlines continuations
([Completions from rewritten code](#completions-from-rewritten-code)). An operator's await which captured the lane's
`SimulationSynchronizationContext`, because the operator started in the lane's work, continues on that lane, one
controlled operation later.

#### Concurrency entry points: status

The status of every concurrency entry point is generated from the rule inventory into [Concurrency entry points:
status](rule-inventory.md#concurrency-entry-points-status): the rule families which control or reject them, the members
left as BCL calls with their reasons, and the result of an audit which scans every managed assembly of the shared
framework, on Windows, Linux, and macOS, for members that reach the thread pool, `TaskScheduler.Default`, a new
thread, a real timer, or an async method, classified by member or by assembly. The members classified by their
assembly are kept in a snapshot, so a runtime which adds one fails the audit until it is reviewed against the
assembly's reason. Anything it does not list
runs as a BCL call; work such a call starts on another thread fails the run at its first controlled call (see
[Thread affinity](#thread-affinity)), and work which only computes or changes shared state runs unchecked.

#### Monitors, locks, and semaphores

The controlled rule set puts the highest-value synchronization primitives on the same cooperative logical-thread
kernel: `System.Threading.Monitor` (and therefore every C# `lock (object)` statement),
`System.Threading.Lock` (and the C# `lock (Lock)` statement), and `System.Threading.SemaphoreSlim`.
The exhaustive controlled/rejected signature list is regenerated into
[`rule-inventory.md`](rule-inventory.md); the per-member Coyote parity ledger is
[`coyote-parity.md`](coyote-parity.md).

What is now **controlled**: the entire `Monitor` static surface — `Enter`/`Exit`/`IsEntered`, all six
`TryEnter` overloads, all five `Wait` overloads, `Pulse`/`PulseAll` — modelling per-object ownership, a
reentrancy count, and a condition-variable wait set on the logical thread. Because the C# compiler lowers
`lock (object)` to `Monitor.Enter(obj, ref bool)` + `finally Monitor.Exit(obj)`, redirecting `Monitor`
controls every `lock` automatically (verified against both Debug and Release lowering, nested/reentrant
locks, third-party assemblies, and lock release through exceptions/finally). `System.Threading.Lock` and
its nested `Scope` ref struct are controlled by **type substitution**, which covers the C# `lock (Lock)`
scope lowering (`EnterScope`/`Scope.Dispose`) as well as the explicit
`Enter`/`Exit`/`TryEnter`/`IsHeldByCurrentThread` members. `SemaphoreSlim` is controlled across its
constructors, `CurrentCount`, the synchronous `Wait` overloads, the asynchronous `WaitAsync` overloads,
`Release`, and `Dispose`, enforcing the maximum count and serving waiters in deterministic FIFO order.

**Finite positive timeouts are honoured exactly in virtual time.** `Monitor.TryEnter(obj, 250)`,
`Monitor.Wait(obj, 250)`, `Lock.TryEnter(250)`, `SemaphoreSlim.Wait(250)`, and `SemaphoreSlim.WaitAsync(250)`
wait until acquisition/signal or a **simulated** deadline, then return/set `false` on timeout — never
consuming wall-clock time. Zero timeouts stay faithful non-blocking tries and infinite timeouts stay
indefinite. The deadline is a `PausedUntilTime` state driven by the cluster clock, so it is *not* a
deadlock cycle edge; advancing the cluster clock fires it. Because modelled time only advances when
nothing else is runnable, any release, pulse, or cancellation possible at the current instant beats a
same-instant timeout (the scheduler's deterministic first-winner policy), and ties between two deadlines at
the same instant resolve by registration order. Cancellation is honoured faithfully — a
`CancellationToken` fires synchronously on the logical thread and throws `OperationCanceledException`.

**Deliberate deviations from real BCL semantics** (documented here, tested, and revisited when the
physical-gate backend lands):

- **A never-satisfiable acquire or *indefinite* wait surfaces as a deadlock diagnostic, not a hang.**
  A finite wait times out (above); an infinite one with no possible progress is reported. Instead of
  blocking a physical thread forever, an unsatisfiable contended `Enter`, `Monitor.Wait`, or
  `SemaphoreSlim.Wait` throws the loop-model `SimulationSynchronousWaitDeadlockException`. One
  consequence: a `Monitor.Wait` that deadlocks has already released the monitor to wait, so a
  compiler-generated `lock` `finally` that then runs `Monitor.Exit` would observe no ownership — the
  deadlock is a terminal diagnostic for the run, not a recoverable exception to catch inside a `lock`.
- **Ownership is by logical strand, not physical thread.** All controlled work shares one cooperative
  thread, so ownership/reentrancy is tracked by the ambient logical-strand id assigned at the single
  new-strand choke point. This is faithful for the async-first concurrency Clockwork targets; a purely
  synchronous CPU loop that never yields does not interleave the way real preemptive threads would.
- **Waiter selection is deterministic and replayable, but does not promise BCL fairness.** `Pulse`,
  `PulseAll`, and `SemaphoreSlim.Release` serve waiters in arrival (FIFO) order for reproducibility; the
  real BCL makes no such guarantee, so code that depends on a specific non-FIFO wakeup order is not a
  target.
- **`SemaphoreSlim.AvailableWaitHandle` is controlled.** It exposes a `WaitHandle`; the
  rewritten getter hands back a bridged controlled manual-reset handle whose signalled state tracks
  `CurrentCount > 0` and follows disposal, so it composes with the controlled `WaitOne`/`WaitAny`/`WaitAll`
  surface instead of leaking an uncontrolled OS handle.
- **Lock objects are never kept alive by the model.** Monitor/semaphore association state lives in a
  `ConditionalWeakTable` keyed weakly by the lock/semaphore object, so a controlled association never
  roots an otherwise-collectible object.

Every instrumented Controlled shim requires an active simulation. The operation routes
through the coordinator; an active simulation with no registered coordinator throws rather than
silently escaping. Uninstrumented production binaries continue to call the ordinary BCL primitives.
The controlled inventory includes wait handles / events (`WaitHandle`/`EventWaitHandle`/`AutoResetEvent`/`ManualResetEvent`,
including `WaitAny`/`WaitAll`/`SignalAndWait`), `Interlocked`, `Volatile`, `SpinWait`, the
`SemaphoreSlim.AvailableWaitHandle` bridge, and the `ThreadPool` registered-wait APIs under control.

#### Modern synchronization

The modern synchronization rules expand the exact, opt-in `clockwork.tasks.controlled` inventory; the generated
[`rule-inventory.md`](rule-inventory.md) is the complete controlled/rejected signature list. The
following behavior applies only to instrumented closure binaries under an active simulation.
Uninstrumented production binaries keep ordinary BCL behavior; an inactive simulation or a missing
runtime service is an explicit failure, never a pass-through to OS synchronization.

- **`ReaderWriterLockSlim`:** every public constructor, state/recursion/waiter property,
  enter/try-enter/exit overload, and `Dispose` is redirected. The real object is only an identity key;
  read, upgradeable-read, and write ownership/recursion are per logical strand. Contention pumps the
  scheduler and finite timeouts use virtual time; writer waiters hold back new readers/upgradeable
  readers to prevent writer starvation.
- **`ManualResetEventSlim`:** constructors, `IsSet`/`SpinCount`, `Set`/`Reset`, every wait overload,
  `WaitHandle`, and `Dispose` are redirected. Its configured spin count is observable metadata only:
  waits do not busy-spin. The bridge is a controlled manual-reset handle, and signal, timeout,
  cancellation, and disposal are modelled state.
- **Kernel `Mutex` and `Semaphore`:** unnamed constructors and release members are controlled through
  the wait-handle kernel; the BCL object is identity only. Mutex ownership and recursion are logical
  strand state. Owner exit without `ReleaseMutex` deliberately leaves it owned, so a later indefinite
  wait reports the controlled deadlock diagnostic instead of fabricating
  `AbandonedMutexException`. Non-null named constructors and all `OpenExisting`/`TryOpenExisting`
  forms are rejected because they represent cross-process kernel state; null-name constructor forms
  are treated as unnamed.
- **`SpinLock`:** whole-type substitution preserves value-type/copy semantics and optional
  owner tracking, but acquisition pumps controlled work rather than spinning CPUs; finite waits are
  virtual-time waits. **`Barrier` and `CountdownEvent`** are likewise whole-type substitutions:
  their participant/count state, callbacks, waits, cancellation, timeouts, disposal, and
  `CountdownEvent.WaitHandle` bridge remain in the simulation.
- **`ExecutionContext` and `SynchronizationContext`:** capture/run, flow suppression/restoration,
  copy/disposal, ambient context, and callback dispatch are controlled. `SynchronizationContext.Post`
  queues through the coordinator and `Send` executes on the current logical strand rather than calling
  custom dispatch. Legacy `ExecutionContext.GetObjectData` serialization and raw
  `SynchronizationContext.Wait(IntPtr[], ...)` are rejected before they can use uncontrolled behavior.
- **Wait-handle composition:** `WaitOne`, `WaitAny`, `WaitAll`, and `SignalAndWait` operate on
  controlled handles without OS blocking; bridges and registered waits compose with that state.
  `WaitAll` validates arrays and atomically consumes eligible auto-reset handles, but rejects an
  array containing a `Mutex` because atomic multi-mutex acquisition is not modelled. Raw
  `Handle`/`SafeWaitHandle` accessors and handles not created by Clockwork are rejected.

All synchronous waits use deterministic loop pumping: zero timeouts poll, finite timeouts use virtual
deadlines, and an unsatisfiable indefinite wait reports the controlled deadlock diagnostic. Disposal
marks controlled state disposed and faults/blocks subsequent use according to the controlled surface;
ownership is logical-strand rather than physical-thread ownership.

### Virtual timer and deadline contract

The controlled rule set classifies the exact .NET 11 timer surface:

- `System.Threading.Timer`: constructors `(TimerCallback)`, and
  `(TimerCallback, object?, int|long|uint|TimeSpan, int|long|uint|TimeSpan)`; all four matching
  `Change` overloads; `ActiveCount`; `Dispose()`, `Dispose(WaitHandle)`, and `DisposeAsync()`.
- `System.Timers.Timer`: constructors `()`, `(double)`, and `(TimeSpan)`; `AutoReset`, `Enabled`,
  `Interval`, `Elapsed`, `Start`, `Stop`, `Close`, and disposal. Non-null `SynchronizingObject` and
  designer `Site` integration are rejected because they can marshal work outside the scheduler.
- `PeriodicTimer`: constructors `(TimeSpan)` and `(TimeSpan, TimeProvider)`, mutable `Period`,
  `WaitForNextTickAsync(CancellationToken)`, and `Dispose()`.
- `Task.Delay`: all six `int`/`TimeSpan`, cancellation, and `TimeProvider` overloads.
- `Task.WaitAsync` and `Task<T>.WaitAsync`: all five overloads on each type (cancellation,
  `TimeSpan`, and `TimeProvider` combinations).
- `CancellationTokenSource`: timed constructors `(int)`, `(TimeSpan)`, and
  `(TimeSpan, TimeProvider)`; both `CancelAfter` overloads; cancellation, reset, and disposal paths
  which must invalidate a pending timer generation. `Cancel` runs the token's callbacks where the BCL inlines continuations
  ([Completions from rewritten code](#completions-from-rewritten-code)).
- `TimeProvider.System` and `TimeProvider.CreateTimer(TimerCallback, object?, TimeSpan, TimeSpan)`.
  The returned object implements the standard `ITimer`; interface `Change` and disposal dispatch to
  the controlled timer. Unrecognized custom providers reject instead of allocating an OS timer.

Finite deadlines advance only when no work is runnable. Work already queued at the current virtual
instant therefore wins before time advances. Equal deadlines fire in registration order; timer
callbacks are then appended to the controlled ready queue. Periodic schedules are based on successive
virtual due instants, callbacks never overlap physically, and `PeriodicTimer` coalesces unconsumed
ticks. Generation checks suppress stale firings after reentrant `Change`, stop, reset, or disposal.
Applicable timer callbacks restore the construction-time user `ExecutionContext` while executing on a
fresh logical strand. Pending deadlines appear in diagnostics as `PausedUntilTime`, and teardown
cancels all remaining registrations without invoking user callbacks.

#### Framework timeouts

Some framework members bound their work with a timer of their own. Where Clockwork can run that timer in virtual time
it does; where the timer lives inside the framework, the member is rejected for the configurations which arm it:

- **`HttpClient`.** Every member which sends a request (`GetAsync`, `GetStringAsync`, `GetByteArrayAsync`,
  `GetStreamAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`, `Send`, `SendAsync`), `HttpMessageInvoker.Send`
  and `SendAsync` when the receiver is an `HttpClient`, and every `HttpClientJsonExtensions` member throw
  `SimulationApiException` when the client's `Timeout` is finite, which it is by default (100 seconds): the BCL bounds
  the request, and the JSON extensions the reading of its response, with that timeout on a real timer. Set
  `Timeout = Timeout.InfiniteTimeSpan` and bound a request with a `CancellationToken` which a virtual delay cancels,
  such as one from `new CancellationTokenSource(timeout)`. With an infinite timeout each member is the BCL's. The
  virtual `Send` and `SendAsync` with a request and a token check the timeout only where they run `HttpClient`'s
  implementation: a client type's override is called as written, and its base call to `HttpClient`'s implementation is
  checked.
- **`WebSocketStream`.** A stream which owns its `WebSocket` closes it on disposal, bounding the closing handshake
  with a real timer. `WebSocketStream.Create(webSocket, messageType, ownsWebSocket: true)` (whose close timeout is 16
  seconds) and `Create(webSocket, messageType, closeTimeout)` with a finite, non-zero timeout throw
  `SimulationApiException`. A stream which does not own its socket, and an owning one whose close timeout is zero (an
  already-cancelled close) or `Timeout.InfiniteTimeSpan`, are the BCL's; close the socket yourself, with a token a
  virtual delay cancels, before disposing the stream.
- **Hosting.** Every `HostingAbstractionsHostExtensions` and `HostingAbstractionsHostBuilderExtensions` member is
  controlled. `StopAsync(IHost, TimeSpan)` stops the
  host with a token which a virtual timer cancels once the timeout elapses. `Start`, `Run`, `WaitForShutdown`, and
  `IHostBuilder.Start` block the caller on the host's work in the BCL; under simulation they wait with the controlled
  wait, which pumps the coordinator, so the host's work runs while the caller waits. `RunAsync`,
  `WaitForShutdownAsync`, and `IHostBuilder.StartAsync` await
  through the coordinator: the BCL's framework awaits would leave the logical thread where the simulation's
  synchronization context is current. Each runs the BCL's steps in the same order, with the BCL's outcome.

`FrameworkTimeoutConformanceTests` compares each controlled outcome with the BCL's, and stages each probe without its
rules to show the real timer racing virtual time.

## Platform and deployment contract

### Supported today

- **.NET 11** is the only targeted runtime (`net11.0`, see `Directory.Build.props`), built with the
  .NET 11 SDK's 11.0.1xx feature band. `global.json` rolls forward to the newest installed patch of that band
  (`latestPatch`), and CI's `setup-dotnet` reads the same file, so servicing fixes arrive without a commit.
  A new feature band (11.0.2xx) is a deliberate `global.json` bump: a band can ship a new C# compiler, and the
  conformance suite, which compiles its fixtures with the SDK's own compiler under Debug and Release, proves
  Clockwork still controls that compiler's async lowering. Consumers that compile code Clockwork rewrites
  should build with a band Clockwork has proven. The built-in rule sets are classified against the .NET 11
  reference assemblies and declare `supportedRuntimes` 11.0. .NET 11's shared framework includes the
  `Microsoft.Extensions` abstractions (`Logging.Abstractions`, `DependencyInjection.Abstractions`,
  `Options`, `Primitives`, and the other `*.Abstractions` assemblies), and NuGet prunes their packages
  from a `net11.0` project's dependencies, so they load from the shared framework and are not rewritten:
  `BackgroundService.StartAsync`, for example, starts `ExecuteAsync` through the real `Task.Run`, so a call to it
  from rewritten code is rejected. A rewritten `ExecuteAsync` which a host starts that way fails at its first
  controlled call with `SimulationThreadAffinityException`, and so does the run (see [Thread affinity](#thread-affinity)).
- **Windows, Linux, and macOS** are all supported for the existing kernel; it has no
  platform-specific dependencies (no P/Invoke, no OS-specific I/O).
- **JIT execution** (the default `dotnet run`/`dotnet test` path) is fully supported.
- **Deterministic BCL rule set** (`clockwork.bcl.deterministic`) covers the direct static
  time/identity/random surface enumerated in [`rule-inventory.md`](rule-inventory.md).
  Determinism is claimed **only** for those exact signatures.
- **Controlled task rule set** (`clockwork.tasks.controlled`) controls the compiler-generated
  `async Task`/`async ValueTask` machinery and the direct `Task`/`Task<T>` combinator (non-generic
  and generic `WhenAll`/`WhenAny`, `Unwrap`), synchronous-wait, blocking `Task<T>.Result`, continuation
  (every `Task.ContinueWith` and `Task<T>.ContinueWith` overload), `ValueTask` conversion (`AsTask`, `Preserve`),
  and `TaskCompletionSource` completion surface enumerated in [`rule-inventory.md`](rule-inventory.md),
  routing `async`/`await` and synchronous waits through the simulation coordinator. No controlled surface
  registers a BCL continuation on a task. The inventory
  also controls `Task.Run`, all 24 .NET 11 `TaskFactory`/`TaskFactory<T>.StartNew` signatures, `Thread`, `ThreadPool`
  (`QueueUserWorkItem`/`UnsafeQueueUserWorkItem`), `Parallel` (including `ForEachAsync` and `ForAsync`), and the
  channel members which await inside the framework under control (see the Coyote
  parity matrix, [`coyote-parity.md`](coyote-parity.md)). Control is claimed **only** for those
  exact signatures, including virtual timers, all `Task.Delay`/`Task.WaitAsync` overloads, and
  timer-driven cancellation; synchronous `ValueTask` blocking remains a documented hole.
  The inventory additionally includes `Monitor` (and the C# `lock (object)`
  statement), `System.Threading.Lock` (and the C# `lock (Lock)` statement), and `SemaphoreSlim` under
  control. The synchronization surface also includes `Interlocked`, `Volatile`, `SpinWait`,
  the wait-handle / event family (`WaitHandle`/`EventWaitHandle`/`AutoResetEvent`/`ManualResetEvent`
  with `WaitOne`/`WaitAny`/`WaitAll`/`SignalAndWait`), the `SemaphoreSlim.AvailableWaitHandle` bridge,
  and the `ThreadPool` registered-wait APIs
  (`RegisterWaitForSingleObject`/`UnsafeRegisterWaitForSingleObject`) are all now controlled; named /
  cross-process event APIs and raw handle accessors are rejected with tested diagnostics. Modern
  synchronization adds `ReaderWriterLockSlim`, `ManualResetEventSlim`, unnamed kernel
  `Mutex`/`Semaphore`, `SpinLock`,
  `ExecutionContext`, `SynchronizationContext`, `Barrier`, and `CountdownEvent`; their exact surfaces
  are described above and in the generated inventory. Their waits use virtual time and controlled loop
  pumping, never a busy spin or OS block.
- **ReadyToRun inputs** are detected by the build/tool path: the default policy rejects them, while
  `StripToIL` produces an IL-only staged output. Instrument before publishing R2R.

### Limitations

- **Single-file, trimming, and NativeAOT ordering.** Clockwork rewrites managed assemblies, not
  completed bundles or native images. Instrument the resolved IL closure before single-file
  bundling, trimming, crossgen/ReadyToRun, or NativeAOT. Rewriting an already bundled, trimmed,
  ReadyToRun, or NativeAOT output is unsupported.
- **Signed assemblies.** Rewritten strong-name identities and matching closure references are
  stripped automatically. Authenticode is detected but not re-applied; consumers must apply it
  after instrumentation if an instrumented artifact must be redistributed.
- **Nondeterministic BCL surface beyond the rule inventory.** Only the exact signatures in
  [`rule-inventory.md`](rule-inventory.md) are rewritten. Its *Members left as BCL calls* section
  lists every other member of a targeted type with its behavior; the documented holes among them
  include `Stopwatch` instance APIs (`RealClock`), `DateTime`/`DateTimeOffset` parse/format/convert
  helpers (`CultureOrTimeZone`), host and process reads (`HostState`, `Process`), a derived type's
  constructor chaining to a controlled one (`ConstructorCall`), `SynchronizationContext.WaitHelper` (`Blocks`), and
  `ThreadPool.BindHandle` (`Schedules`); see
  [Concurrency entry points: status](#concurrency-entry-points-status). A delegate created from a member (`ldftn`) calls the BCL member, since rules rewrite
  `call`, `callvirt`, and `newobj` sites. Timer limitations are unrecognized custom `TimeProvider`
  implementations, non-null `System.Timers.Timer.SynchronizingObject`/designer integration, and
  `Timer.Dispose(WaitHandle)` with a handle outside Clockwork's controlled event surface.
- **No runtime interception.** Clockwork intentionally provides no CLR profiler/ReJIT component,
  startup hook, `AssemblyLoadContext` rewriting, or native detours. Instrumentation is build-time
  and out-of-place, avoiding profiler conflicts with coverage and APM tools.

These limitations apply only to controlled-mode build-time redirection. Cooperative mode requires
no rewriting or hooking and has no additional deployment constraints.

## Project layout

The implemented package boundaries under `src/` map to the modes above:

| Project | Depends on | Current purpose |
|---|---|---|
| `Orleans.Testing.Simulation` | *(none)* | Simulation kernel, ambient context, policy, controlled task/thread/synchronization shims, logical strands, and unified scheduling/resource infrastructure. |
| `Orleans.Testing.Simulation.Instrumentation` | `Orleans.Testing.Simulation` | Cecil rewrite engine, manifests, built-in rules, closure orchestration, and diagnostics. |
| `Orleans.Testing.Simulation.Instrumentation.Build` | `Orleans.Testing.Simulation.Instrumentation` | Opt-in MSBuild task + targets that instrument the resolved output closure out-of-place. |
| `Orleans.Testing.Simulation.Tool` | `Orleans.Testing.Simulation.Instrumentation` | `dotnet orleans-simulation instrument` / `inspect` CLI over the shared orchestrator. |
| `Orleans.Testing.Simulation.Analyzers` | *(none)* | Roslyn diagnostics aligned with controlled/rejected direct BCL usage. |
| `Orleans.Testing.Simulation.Testing` | `Orleans.Testing.Simulation` | Reusable test helpers, scenario builders, and in-memory log capture for consumers. |
| `Orleans.Testing.Simulation.Testing.MTP` | `Orleans.Testing.Simulation.Testing`, Microsoft Testing Platform | Explicit MTP test-application hooks for simulation progress command-line options, output, and test-host environment forwarding. |

Source builds can import
[`eng/Microsoft.Orleans.Testing.Simulation.SourceBuild.props`](../eng/Microsoft.Orleans.Testing.Simulation.SourceBuild.props)
to enumerate the seven package IDs and their source projects, assembly names, target frameworks, and roles.
The map is declarative and adds no references or build hooks. Clog's future `eng/FastCasPaxos.props`
owns replacing CASPaxos package references with project references into its vendored Clockwork source.
That source build must redirect the complete mapped package set, which is preferred, or verify that any
restored Orleans simulation packages were built from the identical Clockwork revision. Restoring those
packages beside a different vendored simulation kernel is forbidden because it can load divergent package
and source-build identities into one process.

`Orleans.Testing.Simulation.Testing` is framework-neutral reusable support and retains the
`Orleans.Testing.Simulation.Testing` namespace boundary. MTP executables opt into
`Orleans.Testing.Simulation.Testing.MTP`; its build-transitive hook does not flow through the support
package into ordinary libraries. Application hosting and transport models are consumer-owned and outside
the Clockwork core; consumers compose them over `SimulationNetwork` and the generic
application-composition APIs. Exact shipped interception behavior is defined by
[`rule-inventory.md`](rule-inventory.md).
