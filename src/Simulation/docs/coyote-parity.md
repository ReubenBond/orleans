# Coyote parity matrix — controlled concurrency and modern synchronization

This document is the explicit parity ledger for Clockwork's controlled-concurrency
surface against **[Microsoft Coyote](https://github.com/microsoft/coyote)** (MIT-licensed prior
art). Coyote's controlled rewriting types live under
[`Source/Test/Rewriting/Types/Threading`](https://github.com/microsoft/coyote/tree/main/Source/Test/Rewriting/Types/Threading)
(and its `Tasks` subfolder). Every Coyote thread / task / thread-pool / Parallel / monitor / semaphore
surface is classified here as one of:

| Status | Meaning |
| --- | --- |
| ✅ **Controlled** | Clockwork rewrites the call site to a controlled shim. The exact rule id is cited; the full signature list is in [`rule-inventory.md`](rule-inventory.md). |
| ⛔ **Rejected (tested)** | Clockwork deliberately rejects the call at the rewritten site with a precise diagnostic, because the semantics cannot be modelled faithfully by the cooperative scheduler yet. A test asserts the rejection. |
| 🏛 **Controlled by architecture** | No dedicated rule is needed: Clockwork controls the *awaiter*, not the `Task` type, so the surface is already controlled whenever the produced task is awaited or waited on. |
| n/a | Not applicable on .NET 11 / not a concurrency-scheduling surface. |

The single largest **architectural difference**: Coyote substitutes the `Task`/`Task<T>` *types*
wholesale, so it must also wrap `TaskCompletionSource`, `TaskExtensions`, `ValueTask`, etc. to keep
every task a *controlled* task. Clockwork instead substitutes the compiler-generated **builder and
awaiter** types (see the `AsyncMachinery`/`ValueTaskMachinery` families) and redirects the direct
`Task` call sites (combinators, waits, `Result`, continuations, `Run`). A plain BCL `Task` produced
by any means is therefore already controlled the moment it is awaited or waited on — which is why
several Coyote wrapper types map to "controlled by architecture" rather than a dedicated rule. The
corollary is that no controlled surface may register a BCL continuation on a task: the continuation
would follow the task's own dispatch (the thread pool, for a `RunContinuationsAsynchronously` source), so the
combinators, `ContinueWith`, and `ValueTask.AsTask`/`Preserve` are redirected too.

Clockwork additionally goes **beyond** Coyote for `System.Threading.ThreadPool`, which Coyote does
not model at all.

This ledger describes instrumented closure binaries, which are simulation/test artifacts. Their
Controlled entry points require an active Clockwork simulation and fail before performing real BCL
work when none is active; the `TaskCompletionSource` completion members alone pass through to the BCL
outside a simulation, since finalizers and uninstrumented callbacks complete sources too. Production
binaries remain uninstrumented and retain ordinary BCL behavior.

---

## Race exploration instrumentation

Clockwork's optional `RaceExploration` mode uses Microsoft Coyote's
`MemoryAccessRewritingPass`, `SchedulingPoint`, collection interception types, and race tests as the
primary prior art. Clockwork preserves Coyote's constructor/property exclusion and `brtrue`/`brfalse`
coverage, and extends the pass with static fields, arrays, safely classified indirect accesses,
source/IL metadata, and manifest records.

| Surface | Clockwork coverage | Deviation / limit |
| --- | --- | --- |
| `ldfld` / `stfld` | Tracked by weak object identity + member | Value-type/state-machine infrastructure fields are schedule-only |
| `ldsfld` / `stsfld` | Tracked by member identity | Volatile-prefixed accesses are indivisible schedule-only points |
| `ldelem.*` / `stelem.*` | Tracked by weak array identity + index | Multidimensional helper calls are not recognized |
| field address / indirect / object IL | Schedule-only | Arbitrary managed/native pointer offsets have no safe logical identity |
| `brtrue` / `brfalse` | Control-flow scheduling point | Other branch opcodes are not injected, matching Coyote's pass |
| `List<T>` / `Dictionary<TKey,TValue>` / `HashSet<T>` | Direct concrete reads, writes, and enumeration are tracked | Interface/reflection/dynamic and `tail.`-prefixed calls are outside coverage |
| four `System.Collections.Concurrent` types | Direct concrete calls are scheduling points | Thread-safe collection access is not reported as a race |

Unlike Coyote's wrapper subclasses for generic collections, Clockwork spills and restores the receiver
and arguments around the original direct call. The runtime collection type, return value, exceptions,
and .NET 11 member semantics therefore remain those of the BCL. Normal `Controlled` mode runs no such
pass and does not change collection types.

The detector uses vector-clock happens-before, with exclusive locks ordered by their release-to-acquire
edges, rather than physical-thread overlap. Object and synchronization identities are weak-keyed, and a
location's state lives as long as its object. Reports include both operations,
read/write kinds, source/IL call sites, held synchronization, and the seeded schedule trace.

---

## `System.Threading.Thread` — Coyote `…Types.Threading.Thread`

Coyote's controlled `Thread` exposes exactly `Create` (×4), `Start` (×2), `Sleep` (×2), `SpinWait`,
`Yield`, and `Join` (×3). Clockwork mirrors all of them and, going further, **explicitly rejects**
the OS-specific surface Coyote simply leaves as an uncontrolled real call.

| Coyote surface | .NET 11 signature | Clockwork status | Rule / reason |
| --- | --- | --- | --- |
| `Create(ThreadStart)` | `new Thread(ThreadStart)` | ✅ Controlled | `clockwork.thread.ctor.threadstart` |
| `Create(ThreadStart, maxStackSize)` | `new Thread(ThreadStart, int)` | ✅ Controlled | `clockwork.thread.ctor.threadstart.stacksize` |
| `Create(ParameterizedThreadStart)` | `new Thread(ParameterizedThreadStart)` | ✅ Controlled | `clockwork.thread.ctor.parameterized` |
| `Create(ParameterizedThreadStart, maxStackSize)` | `new Thread(ParameterizedThreadStart, int)` | ✅ Controlled | `clockwork.thread.ctor.parameterized.stacksize` |
| `Start(instance)` | `Thread.Start()` | ✅ Controlled | `clockwork.thread.start` |
| `Start(instance, parameter)` | `Thread.Start(object)` | ✅ Controlled | `clockwork.thread.start.parameter` |
| *(not modelled by Coyote)* | `Thread.UnsafeStart()` / `UnsafeStart(object)` | ✅ Controlled (beyond Coyote) | `clockwork.thread.unsafestart[.parameter]` — starts the controlled thread without flowing the caller's `ExecutionContext` |
| `Join(instance)` | `Thread.Join()` | ✅ Controlled | `clockwork.thread.join` (virtual wait) |
| `Join(instance, ms)` | `Thread.Join(int)` | ✅ Controlled | `clockwork.thread.join.milliseconds` |
| `Join(instance, TimeSpan)` | `Thread.Join(TimeSpan)` | ✅ Controlled | `clockwork.thread.join.timespan` |
| `Sleep(ms)` | `Thread.Sleep(int)` | ✅ Controlled | `clockwork.thread.sleep.milliseconds` (virtual wait) |
| `Sleep(TimeSpan)` | `Thread.Sleep(TimeSpan)` | ✅ Controlled | `clockwork.thread.sleep.timespan` (virtual wait) |
| `SpinWait(iterations)` | `Thread.SpinWait(int)` | ✅ Controlled | `clockwork.thread.spinwait` |
| `Yield()` | `Thread.Yield()` | ✅ Controlled | `clockwork.thread.yield` |
| *(not modelled by Coyote — left uncontrolled)* | `Thread.set_Priority(ThreadPriority)` | ⛔ Rejected (tested) | `clockwork.thread.set_priority` — thread priority has no faithful meaning in a cooperative single-logical-thread scheduler |
| *(not modelled by Coyote)* | `Thread.Interrupt()` | ⛔ Rejected (tested) | `clockwork.thread.interrupt` — OS thread interruption cannot be modelled |
| *(not modelled by Coyote)* | `Thread.SetApartmentState(ApartmentState)` | ⛔ Rejected (tested) | `clockwork.thread.setapartmentstate` — COM apartments are an OS concept |
| *(not modelled by Coyote)* | `Thread.TrySetApartmentState(ApartmentState)` | ⛔ Rejected (tested) | `clockwork.thread.trysetapartmentstate` |
| *(not modelled by Coyote)* | `Thread.set_ApartmentState(ApartmentState)` (obsolete property) | ⛔ Rejected (tested) | `clockwork.thread.set_apartmentstate` — tries to set the apartment state, as `TrySetApartmentState` does |

**Deviation:** `Join`/`Sleep` are *virtual* waits (yield through the deterministic loop, no real
time) — identical in spirit to Coyote's `DelayCurrentOperation`/`PauseUntilThreadCompletes`.
`Thread.Name`/`ManagedThreadId` remain the real BCL members (logical identity is preserved by the
underlying real `Thread` object each controlled operation runs on), matching Coyote.

---

## `System.Threading.Tasks.Task` static surface

The direct `Task` call-site surface controls combinators, waits, `Result`, continuations,
async machinery, `Yield`, and `Run`.

| Coyote surface | Clockwork status | Rule / family |
| --- | --- | --- |
| `Task.Run` (8 overloads: `Action`/`Func<TResult>`/`Func<Task>`/`Func<Task<TResult>>`, ± `CancellationToken`) | ✅ Controlled | `clockwork.tasks.run.*` (TaskScheduling family) |
| `Task.WhenAll` / `Task.WhenAny` (array, span, pair, enumerable; non-generic + generic) | ✅ Controlled | `clockwork.tasks.whenall.*` / `whenany.*` (TaskCombinators); completed through the coordinator, registering nothing on the antecedents |
| `Task.WhenEach` (array, span, enumerable; non-generic + generic) | ✅ Controlled (beyond Coyote) | `clockwork.tasks.wheneach.*` (TaskCombinators); the coordinator observes the tasks complete and completes `MoveNextAsync` on a controlled operation |
| `Task.Wait` / `WaitAll` / `WaitAny`, every overload (timed, cancellable, `ReadOnlySpan<Task>`, `IEnumerable<Task>`) | ✅ Controlled | `clockwork.tasks.wait.*`, `clockwork.tasks.waitall.*`, `clockwork.tasks.waitany.*` (TaskSynchronization); timeouts elapse in virtual time |
| `Task.Start()` / `Start(TaskScheduler)` / `RunSynchronously()` / `RunSynchronously(TaskScheduler)` | ✅ Controlled | `clockwork.tasks.start[.scheduler]`, `clockwork.tasks.runsynchronously[.scheduler]` (TaskScheduling); the task runs on the coordinator's `TaskScheduler`; a custom scheduler is rejected |
| `Task<T>.Result` (blocking get) | ✅ Controlled | `clockwork.tasks.result.generic` |
| `Task.ContinueWith` (all 20 overloads: `Action`/`Func`, ± state, ± `CancellationToken`/`TaskContinuationOptions`/`TaskScheduler`) | ✅ Controlled | `clockwork.tasks.continuewith.{action,action.state,func,func.state}*` (TaskContinuations); `LongRunning`, `AttachedToParent`, and custom schedulers rejected |
| `Task<T>.ContinueWith` (all 20 overloads) | ✅ Controlled | `clockwork.tasks.continuewith.generic.*` |
| `Task.Yield()` | ✅ Controlled | `clockwork.tasks.yield.call` (AsyncMachinery) |
| `async` builder + awaiter types (`AsyncTaskMethodBuilder`, `TaskAwaiter`, `ConfiguredTaskAwaitable`, `YieldAwaitable`, generic + non-generic) | ✅ Controlled | AsyncMachinery family (type substitution) |
| `Task.Delay` (all 6 .NET 11 overloads: `int`/`TimeSpan`, cancellation, and `TimeProvider`) | ✅ Controlled | `clockwork.tasks.delay.*` — virtual deadlines, no wall clock |
| `Task.WaitAsync` / `Task<T>.WaitAsync` (all 5 overloads on each type) | ✅ Controlled | `clockwork.tasks[.generic].waitasync.*` — result/fault/cancel propagation with virtual timeout |
| `Task.FromResult` / `FromException` / `FromCanceled` / `CompletedTask` | 🏛 Controlled by architecture | already-completed tasks need no scheduling; awaiting one routes through the controlled awaiter |

---

## `System.Threading.Tasks.ValueTask`

| Coyote surface | Clockwork status | Rule / family |
| --- | --- | --- |
| `async ValueTask`/`ValueTask<T>` builder + awaiter types (`AsyncValueTaskMethodBuilder`, `ValueTaskAwaiter`, `ConfiguredValueTaskAwaitable`, generic + non-generic) | ✅ Controlled | ValueTaskMachinery family (type substitution) |
| `ValueTask.AsTask()` / `ValueTask<T>.AsTask()` / `Preserve()` | ✅ Controlled | `clockwork.tasks.valuetask[.generic].astask` / `.preserve` (ValueTaskConversions); a pending `IValueTaskSource` is polled through the coordinator, with no callback registered on the source |
| Blocking `ValueTask<T>.Result` read | ⛔ Not rewritten (documented hole) | `await`, or `GetAwaiter().GetResult()` through the controlled awaiter (which pumps until the value task completes), is the supported controlled path (see `rule-inventory.md` holes) |

---

## `System.Threading.Tasks.TaskFactory` / `TaskFactory<TResult>`

Coyote wraps the full `StartNew` overload set plus the read-only property getters. Clockwork
classifies and rewrites the same 24 .NET 11 `StartNew` signatures.

| Coyote surface | Clockwork status | Rule |
| --- | --- | --- |
| `TaskFactory.StartNew(Action)` (+ token / options / full scheduler) | ✅ Controlled | `clockwork.tasks.factory.startnew.action*` |
| `TaskFactory.StartNew(Action<object>, object)` (+ token / options / full scheduler) | ✅ Controlled | `clockwork.tasks.factory.startnew.action.state*` |
| `TaskFactory.StartNew<TResult>(Func<TResult>)` (+ token / options / full scheduler) | ✅ Controlled | `clockwork.tasks.factory.startnew.func*` |
| `TaskFactory.StartNew<TResult>(Func<object,TResult>, object)` (+ token / options / full scheduler) | ✅ Controlled | `clockwork.tasks.factory.startnew.func.state*` |
| `TaskFactory<TResult>.StartNew(Func<TResult>)` (+ token / options / full scheduler) | ✅ Controlled | `clockwork.tasks.factory.generic.startnew.func*` |
| `TaskFactory<TResult>.StartNew(Func<object,TResult>, object)` (+ token / options / full scheduler) | ✅ Controlled | `clockwork.tasks.factory.generic.startnew.func.state*` |
| Full-scheduler form with `TaskScheduler.Default` and `TaskCreationOptions.None` | ✅ Controlled | state, cancellation, result, and `Task.AsyncState` are preserved |
| Full-scheduler form with a custom scheduler; any non-`None` creation options | ⛔ Rejected (tested) | unsupported observable semantics are never silently ignored or allowed to escape the logical strand |
| `ContinueWhenAll` / `ContinueWhenAny` (all 48 overloads of both factories: `Action`/`Func`, `Task[]`/`Task<TAntecedentResult>[]`, ± token / options / full scheduler) | ✅ Controlled (beyond Coyote) | `clockwork.tasks.factory[.generic].continuewhenall.*` / `continuewhenany.*` (TaskFactoryContinuations); a genuine BCL continuation of a gate the coordinator completes; `LongRunning`, `AttachedToParent`, and custom schedulers rejected |
| `FromAsync` (every overload of both factories) | ⛔ Rejected (tested) | `clockwork.tasks.factory[.generic].fromasync` (TaskFactoryFromAsync); an `IAsyncResult` completes outside Clockwork's control |
| `get_ContinuationOptions` / `get_CancellationToken` / `get_CreationOptions` / `get_Scheduler` | n/a | pure read-only property getters — no scheduling, no interception needed |

---

## `System.Threading.Tasks.TaskCompletionSource` / `TaskCompletionSource<TResult>`

Coyote wraps TCS so the produced task is a controlled task. Clockwork keeps the real BCL
`TaskCompletionSource` and `TaskCompletionSource<TResult>`, with their real `TaskCreationOptions`: the controlled
awaiters, combinators, and continuations observe the produced task through the coordinator and register no BCL
continuation on it, so `RunContinuationsAsynchronously` sends none of them to the thread pool. Only the completion
members redirect, to `Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskCompletionSource`, which completes the
source through the same member and publishes the race-detection release edge.

| Coyote surface | Clockwork status | Notes |
| --- | --- | --- |
| `new TaskCompletionSource()` / `new TaskCompletionSource(TaskCreationOptions)` (and generic, ± state) | 🏛 Controlled by architecture | the real BCL type and options; not rewritten |
| `SetResult` / `SetException(Exception \| IEnumerable<Exception>)` / `SetCanceled(± CancellationToken)` / `SetFromTask` (and generic) | ✅ Controlled | `clockwork.tasks.completionsource[.generic].set*` (TaskCompletionSource family) |
| `TrySetResult` / `TrySetException` / `TrySetCanceled` / `TrySetFromTask` (and generic) | ✅ Controlled | `clockwork.tasks.completionsource[.generic].tryset*` |
| `get_Task` | 🏛 Controlled by architecture | the real task, observed through the coordinator |

---

## `System.Threading.Tasks.TaskExtensions`

| Coyote surface | .NET 11 signature | Clockwork status | Notes |
| --- | --- | --- | --- |
| `Unwrap(this Task<Task>)` | `Task Unwrap(this Task<Task>)` | ✅ Controlled | `clockwork.tasks.unwrap`; the proxy is completed through the coordinator, registering nothing on the outer or inner task |
| `Unwrap<TResult>(this Task<Task<TResult>>)` | ditto | ✅ Controlled | `clockwork.tasks.unwrap.generic` |

---

## `System.Threading.Tasks.Parallel` — Coyote `…Types.Threading.Tasks.Parallel`

Coyote wraps the full `Invoke`/`For`/`ForEach` matrix including thread-local (`TLocal`) and
`Partitioner`/`OrderablePartitioner` overloads. Clockwork controls every loop whose body takes only the element,
`ForEachAsync` and `ForAsync` included, and rejects the forms whose body receives a `ParallelLoopState`;
[Parallel loops](compatibility.md#parallel-loops) describes their behavior.

| Coyote surface | Clockwork status | Rule / reason |
| --- | --- | --- |
| `Invoke(Action[])` | ✅ Controlled | `clockwork.parallel.invoke` |
| `Invoke(ParallelOptions, Action[])` | ✅ Controlled | `clockwork.parallel.invoke.options` |
| `For(int, int, Action<int>)` (± `ParallelOptions`) | ✅ Controlled | `clockwork.parallel.for.int32[.options]` |
| `For(long, long, Action<long>)` (± `ParallelOptions`) | ✅ Controlled | `clockwork.parallel.for.int64[.options]` |
| `ForEach<T>(IEnumerable<T>, Action<T>)` (± `ParallelOptions`) | ✅ Controlled | `clockwork.parallel.foreach[.options]` |
| `ForEach<T>(Partitioner<T>, Action<T>)` (± `ParallelOptions`) | ✅ Controlled | `clockwork.parallel.foreach.partitioner[.options]` — each worker enumerates its own dynamic partition |
| *(not modelled by Coyote)* `ForEachAsync<T>(IEnumerable<T> \| IAsyncEnumerable<T>, …)` (± token / `ParallelOptions`) | ✅ Controlled (beyond Coyote) | `clockwork.parallel.foreachasync[.asyncenumerable][.cancellationtoken \| .options]` |
| *(not modelled by Coyote)* `ForAsync<T>(T, T, …)` (± token / `ParallelOptions`) | ✅ Controlled (beyond Coyote) | `clockwork.parallel.forasync[.cancellationtoken \| .options]` |
| `For(…, Action<int/long, ParallelLoopState>)` (break/stop) | ⛔ Rejected (tested) | `clockwork.parallel.for.int32/int64.loopstate[.options]` — `ParallelLoopState` has no public constructor, so Clockwork cannot supply one to a body |
| `ForEach(…, Action<T, ParallelLoopState>)` / `Action<T, ParallelLoopState, long>` (± `ParallelOptions`, sequence or partitioner) | ⛔ Rejected (tested) | `clockwork.parallel.foreach[.partitioner \| .orderablepartitioner].loopstate[.index][.options]` |
| `For<TLocal>(…)` / `ForEach<TSource,TLocal>(…)` (thread-local accumulation) | ⛔ Rejected (tested) | `clockwork.parallel.for.int32/int64.local[.options]`, `clockwork.parallel.foreach[.partitioner \| .orderablepartitioner].local[.index][.options]` — their bodies receive a `ParallelLoopState` |
| *(not modelled by Coyote)* `Environment.ProcessorCount`, `Partitioner.Create(int, int)` / `(long, long)` | ✅ Controlled (beyond Coyote) | `clockwork.environment.processorcount`, `clockwork.parallel.partitioner.create.int32/int64` — the simulation's processor count |

**Deviation:** execution is cooperative rather than physically parallel; see [Parallel loops](compatibility.md#parallel-loops)
and [Simulated processor count](compatibility.md#simulated-processor-count).

---

## `System.Threading.ThreadPool` — **beyond Coyote**

Coyote has **no** controlled `ThreadPool` type (thread-pool work is expected to arrive via the
controlled `Task`/`TaskFactory` types). Clockwork models `ThreadPool` directly so applications that
queue raw work items are deterministic.

| .NET 11 signature | Clockwork status | Rule / reason |
| --- | --- | --- |
| `QueueUserWorkItem(WaitCallback)` | ✅ Controlled (flows `ExecutionContext`) | `clockwork.threadpool.queue.waitcallback` |
| `QueueUserWorkItem(WaitCallback, object)` | ✅ Controlled (flows `ExecutionContext`) | `clockwork.threadpool.queue.waitcallback.state` |
| `QueueUserWorkItem<TState>(Action<TState>, TState, bool preferLocal)` | ✅ Controlled (flows `ExecutionContext`) | `clockwork.threadpool.queue.generic` |
| `UnsafeQueueUserWorkItem(WaitCallback, object)` | ✅ Controlled (does **not** flow `ExecutionContext`) | `clockwork.threadpool.unsafequeue.waitcallback.state` |
| `UnsafeQueueUserWorkItem(IThreadPoolWorkItem, bool)` | ✅ Controlled (does **not** flow `ExecutionContext`) | `clockwork.threadpool.unsafequeue.workitem` |
| `UnsafeQueueUserWorkItem<TState>(Action<TState>, TState, bool)` | ✅ Controlled (does **not** flow `ExecutionContext`) | `clockwork.threadpool.unsafequeue.generic` |
| `UnsafeQueueNativeOverlapped(NativeOverlapped*)` | ⛔ Rejected (tested) | `clockwork.threadpool.unsafequeuenativeoverlapped` — native overlapped I/O cannot be modelled |
| `RegisterWaitForSingleObject(…)` (uint32/int32/int64/TimeSpan) | ✅ Controlled (flows `ExecutionContext`) | `clockwork.threadpool.registerwait.*` — passive event-driven controlled wait; fires `timedOut:false` on signal, `timedOut:true` on the virtual-time deadline |
| `UnsafeRegisterWaitForSingleObject(…)` (uint32/int32/int64/TimeSpan) | ✅ Controlled (does **not** flow `ExecutionContext`) | `clockwork.threadpool.unsaferegisterwait.*` |
| `RegisteredWaitHandle` (returned token; `Unregister(WaitHandle)`) | ✅ Controlled (whole-type substitution → `ControlledRegisteredWaitHandle`) | `clockwork.threadpool.registeredwaithandle.type` — `Unregister` stops the wait and signals its completion event |

**ExecutionContext modelling:** the safe `QueueUserWorkItem` variants capture and flow the caller's
`ExecutionContext` (so `AsyncLocal` values are visible to the callback); the `Unsafe…` variants do
not — matching the BCL contract exactly, and covered by conformance tests.

---

## `System.Threading.Monitor` — Coyote `…Types.Threading.Monitor`

Coyote's controlled `Monitor` mirrors the synchronization surface (`Enter`, `Exit`, `IsEntered`,
`TryEnter`, `Wait`, `Pulse`, `PulseAll`) on its scheduler. Clockwork classifies the **entire** .NET 11
declared surface on the cooperative logical-thread kernel. Because the C# `lock (object)` statement lowers to
`Monitor.Enter(obj, ref bool)` + `finally Monitor.Exit(obj)`, redirecting these members controls **every**
`lock` automatically — no separate lock rule is needed (verified for both Debug and Release lowering).

| .NET 11 signature | Clockwork status | Rule / reason |
| --- | --- | --- |
| `Monitor.Enter(object)` | ✅ Controlled | `clockwork.monitor.enter` |
| `Monitor.Enter(object, ref bool)` | ✅ Controlled | `clockwork.monitor.enter.locktaken` (the `lock` lowering target) |
| `Monitor.Exit(object)` | ✅ Controlled | `clockwork.monitor.exit` |
| `Monitor.IsEntered(object)` | ✅ Controlled | `clockwork.monitor.isentered` |
| `Monitor.LockContentionCount` | ⛔ Rejected (tested) | `clockwork.monitor.get_lockcontentioncount` — process-wide physical contention has no per-simulation meaning |
| `Monitor.TryEnter(object)` | ✅ Controlled | `clockwork.monitor.tryenter` |
| `Monitor.TryEnter(object, ref bool)` | ✅ Controlled | `clockwork.monitor.tryenter.locktaken` |
| `Monitor.TryEnter(object, int)` | ✅ Controlled | `clockwork.monitor.tryenter.milliseconds` |
| `Monitor.TryEnter(object, int, ref bool)` | ✅ Controlled | `clockwork.monitor.tryenter.milliseconds.locktaken` |
| `Monitor.TryEnter(object, TimeSpan)` | ✅ Controlled | `clockwork.monitor.tryenter.timespan` |
| `Monitor.TryEnter(object, TimeSpan, ref bool)` | ✅ Controlled | `clockwork.monitor.tryenter.timespan.locktaken` |
| `Monitor.Wait(object)` | ✅ Controlled | `clockwork.monitor.wait` |
| `Monitor.Wait(object, int)` | ✅ Controlled | `clockwork.monitor.wait.milliseconds` |
| `Monitor.Wait(object, int, bool)` | ✅ Controlled | `clockwork.monitor.wait.milliseconds.exitcontext` |
| `Monitor.Wait(object, TimeSpan)` | ✅ Controlled | `clockwork.monitor.wait.timespan` |
| `Monitor.Wait(object, TimeSpan, bool)` | ✅ Controlled | `clockwork.monitor.wait.timespan.exitcontext` |
| `Monitor.Pulse(object)` | ✅ Controlled | `clockwork.monitor.pulse` |
| `Monitor.PulseAll(object)` | ✅ Controlled | `clockwork.monitor.pulseall` |

**Semantics:** ownership and reentrancy are tracked per monitored object by the acquiring logical strand
and a recursion count; `Enter`/`TryEnter` acquire (a contended acquire pumps the loop until the owner
releases); `Wait` atomically releases the **full** recursion count, parks in the object's wait set, and
re-acquires the same count after being pulsed; `Pulse` moves one waiter (and `PulseAll` all waiters) to
the ready set with arrival-ordered, replayable scheduling (no lost pulses). Ownership/argument/timeout
errors throw exactly as the BCL (`SynchronizationLockException`, `ArgumentNullException`,
`ArgumentOutOfRangeException`). **Timeouts:** zero timeouts are faithful non-blocking tries; a finite
positive timeout waits until acquisition/signal or a **simulated** deadline (driven by the cluster clock)
and then returns/sets `false` — a same-instant pulse or release beats the timeout, and the finite wait is
`PausedUntilTime`, never a deadlock edge; an infinite / never-satisfiable acquire or wait surfaces as the
loop-model `SimulationSynchronousWaitDeadlockException`. Monitor
associations are held in a `ConditionalWeakTable` (weak keys) so lock objects are never kept alive.

---

## `System.Threading.Lock` — **beyond Coyote**

`System.Threading.Lock` is the .NET 9+ dedicated lock type; it postdates Coyote's rewriter and has **no
Coyote equivalent**. Clockwork controls it by **type substitution**: the type and its nested `Scope` ref
struct are retargeted onto `ControlledLock`/`ControlledLock.Scope`, so `new Lock()`, every field/local/
parameter typed as `Lock` or `Lock.Scope`, and the C# `lock (Lock)` lowering
(`Lock.Scope scope = obj.EnterScope(); try { … } finally { scope.Dispose(); }`) are redirected wholesale
onto the controlled monitor kernel (verified for both Debug and Release lowering).

| .NET 11 surface | Clockwork status | Rule / reason |
| --- | --- | --- |
| `System.Threading.Lock` (type) | ✅ Controlled | `clockwork.lock.type` (type substitution → `ControlledLock`) |
| `System.Threading.Lock.Scope` (nested ref struct) | ✅ Controlled | `clockwork.lock.scope.type` (type substitution → `ControlledLock.Scope`) |
| `new Lock()`, `Enter()`, `Exit()`, `EnterScope()`, `TryEnter()`, `TryEnter(int)`, `TryEnter(TimeSpan)`, `IsHeldByCurrentThread`, `Scope.Dispose()` | ✅ Controlled | reached through the two type substitutions above |

**Semantics:** identical to the controlled `Monitor` (ownership, reentrancy, contended-acquire pumping,
finite virtual-time timeouts). No member of `System.Threading.Lock` needs a rejection — the whole
surface is safely representable by type substitution.

---

## `System.Threading.SemaphoreSlim` — Coyote `…Types.Threading.SemaphoreSlim`

Coyote wraps `SemaphoreSlim` to control its waits on the scheduler. Clockwork models the permit count and
waiter set on the cooperative logical thread. `SemaphoreSlim` is `sealed`, so the controlled handle **is**
a real `SemaphoreSlim` instance whose count/waiter state lives in a weak-keyed side table; the two
constructors redirect to `Create` factories and every instance member is a receiver-first shim.

| .NET 11 signature | Clockwork status | Rule / reason |
| --- | --- | --- |
| `new SemaphoreSlim(int)` | ✅ Controlled | `clockwork.semaphoreslim.ctor.initial` |
| `new SemaphoreSlim(int, int)` | ✅ Controlled | `clockwork.semaphoreslim.ctor.initial.max` |
| `SemaphoreSlim.CurrentCount` | ✅ Controlled | `clockwork.semaphoreslim.get_currentcount` |
| `Wait()` | ✅ Controlled | `clockwork.semaphoreslim.wait` |
| `Wait(CancellationToken)` | ✅ Controlled | `clockwork.semaphoreslim.wait.cancellationtoken` |
| `Wait(int)` | ✅ Controlled | `clockwork.semaphoreslim.wait.milliseconds` |
| `Wait(int, CancellationToken)` | ✅ Controlled | `clockwork.semaphoreslim.wait.milliseconds.cancellationtoken` |
| `Wait(TimeSpan)` | ✅ Controlled | `clockwork.semaphoreslim.wait.timespan` |
| `Wait(TimeSpan, CancellationToken)` | ✅ Controlled | `clockwork.semaphoreslim.wait.timespan.cancellationtoken` |
| `WaitAsync()` | ✅ Controlled | `clockwork.semaphoreslim.waitasync` |
| `WaitAsync(CancellationToken)` | ✅ Controlled | `clockwork.semaphoreslim.waitasync.cancellationtoken` |
| `WaitAsync(int)` | ✅ Controlled | `clockwork.semaphoreslim.waitasync.milliseconds` |
| `WaitAsync(int, CancellationToken)` | ✅ Controlled | `clockwork.semaphoreslim.waitasync.milliseconds.cancellationtoken` |
| `WaitAsync(TimeSpan)` | ✅ Controlled | `clockwork.semaphoreslim.waitasync.timespan` |
| `WaitAsync(TimeSpan, CancellationToken)` | ✅ Controlled | `clockwork.semaphoreslim.waitasync.timespan.cancellationtoken` |
| `Release()` | ✅ Controlled | `clockwork.semaphoreslim.release` |
| `Release(int)` | ✅ Controlled | `clockwork.semaphoreslim.release.count` |
| `Dispose()` | ✅ Controlled | `clockwork.semaphoreslim.dispose` |
| `SemaphoreSlim.AvailableWaitHandle` | ✅ Controlled | `clockwork.semaphoreslim.get_availablewaithandle` — bridged to a controlled manual-reset wait handle tracking count > 0 |

**Semantics:** a synchronous `Wait` with no permit pumps the loop until a permit is released; `WaitAsync`
returns a task completed when a permit is released (driven by the controlled awaiter when awaited);
`Release` enforces the maximum count (`SemaphoreFullException`) and serves waiters in a deterministic,
replayable FIFO order (matching arrival, not promising BCL fairness); cancellation is honoured
synchronously on the logical thread (`OperationCanceledException`). `AvailableWaitHandle` is bridged to a
controlled manual-reset wait handle — materialised once and cached — whose signalled state tracks whether
a permit is available (count > 0) across every `Wait`/`Release` transition; observing it never consumes a
permit and it composes with `WaitAny`/`WaitAll`, and it faults after the semaphore is disposed.
**Timeouts:** zero timeouts are
faithful non-blocking tries; a finite positive timeout (sync `Wait` or async `WaitAsync`) completes with
`false` on a **simulated** deadline driven by the cluster clock — a same-instant release or cancellation
wins over the timeout (the scheduler's first-winner), no wall-clock time is used; a never-satisfiable *infinite*
`Wait` surfaces as the loop-model deadlock diagnostic.

---

## `System.Threading.Interlocked` — Coyote `…Types.Threading.Interlocked`

Coyote controls the interlocked surface so that under its scheduler each atomic read-modify-write is
observed as an indivisible operation. Clockwork mirrors the **full .NET 11 `Interlocked` surface** by
redirecting every call site to a shim with the identical `ref`-first signature. Because Clockwork runs on
a **single cooperative logical thread** the operation can never be interleaved mid-flight, so each shim
delegates straight to the real primitive — preserving exact atomic return, overflow, and reference-write
semantics under the active simulation. The **documented exploration policy** injects **no**
mid-operation scheduling point (unlike Coyote, whose real preemptible threads require one); an atomic
operation is never split. The single delegation site is the race-exploration access-tracking attachment point.

| .NET 11 member | Posture | Rule id |
| --- | --- | --- |
| `Increment(ref int/long/uint/ulong)` | ✅ Controlled | `clockwork.interlocked.increment.*` |
| `Decrement(ref int/long/uint/ulong)` | ✅ Controlled | `clockwork.interlocked.decrement.*` |
| `Add(ref int/long/uint/ulong, …)` | ✅ Controlled | `clockwork.interlocked.add.*` |
| `And(ref int/uint/long/ulong, …)`, generic `And<T>(ref T, T) where T : struct` | ✅ Controlled | `clockwork.interlocked.and.*` |
| `Or(ref int/uint/long/ulong, …)`, generic `Or<T>(ref T, T) where T : struct` | ✅ Controlled | `clockwork.interlocked.or.*` |
| `Exchange(ref T, …)` — int, long, object, sbyte, short, byte, ushort, uint, ulong, float, double, IntPtr, UIntPtr, unconstrained generic `<T>` | ✅ Controlled | `clockwork.interlocked.exchange.*` |
| `CompareExchange(ref T, …, …)` — same 13 primitive/native/float/reference overloads + unconstrained generic `<T>` | ✅ Controlled | `clockwork.interlocked.compareexchange.*` |
| `Read(ref long/ulong)` | ✅ Controlled | `clockwork.interlocked.read.*` |
| `MemoryBarrier()`, `MemoryBarrierProcessWide()` | ✅ Controlled | `clockwork.interlocked.memorybarrier`, `…processwide` |

**Semantics:** every overload returns exactly what the BCL returns (the incremented/decremented value,
the sum, the *original* value for `And`/`Or`/`Exchange`/`CompareExchange`) and writes the same result to
the referenced location; `CompareExchange` swaps only when the comparand matches; the generic overloads
operate on references by identity and on primitives and enums by value. No overflow checking is added or
removed. Enumerated against the .NET 11 reference assemblies; no applicable `Interlocked` overload is left
uncontrolled.

---

## `System.Threading.Volatile` — Coyote `…Types.Threading.Volatile`

Coyote controls the volatile surface so a read/write and its fence are observed atomically under its
scheduler. Clockwork mirrors the **full .NET 11 `Volatile` surface** by redirecting every call site to a
shim with the identical `ref`-first signature. On the single cooperative logical thread a volatile access
is an indivisible step, so each shim delegates to the real primitive — preserving the exact value together
with the acquire (read) / release (write) fence intent. The single delegation site is the
race-exploration access-tracking attachment point.

| .NET 11 member | Posture | Rule id |
| --- | --- | --- |
| `Read(ref bool/sbyte/byte/short/ushort/int/uint/long/ulong/IntPtr/UIntPtr/float/double)` | ✅ Controlled | `clockwork.volatile.read.*` |
| `Read<T>(ref T) where T : class?` | ✅ Controlled | `clockwork.volatile.read.generic` |
| `Write(ref bool/sbyte/byte/short/ushort/int/uint/long/ulong/IntPtr/UIntPtr/float/double, …)` | ✅ Controlled | `clockwork.volatile.write.*` |
| `Write<T>(ref T, T) where T : class?` | ✅ Controlled | `clockwork.volatile.write.generic` |
| `ReadBarrier()`, `WriteBarrier()` | ✅ Controlled | `clockwork.volatile.readbarrier`, `…writebarrier` |

**Semantics:** `Read` returns exactly the value at the location; `Write` stores exactly the supplied
value; the generic overloads publish/acquire a reference by identity; the barriers are controlled
acquire/release fences. Enumerated against the .NET 11 reference assemblies (13 primitive/native/float
`Read` + 1 generic, the matching 14 `Write`, and both barriers); no applicable `Volatile` overload is
left uncontrolled.

---

## `System.Threading.SpinWait` — Coyote `…Types.Threading.SpinWait`

`SpinWait` is a **value type**, so — exactly like `System.Threading.Lock` — it is retargeted by
**whole-type substitution** rather than per-member call redirects. Every local/field/parameter typed
`SpinWait`, each `new SpinWait()` / `default`, the instance members and the static `SpinUntil` overloads
remap onto the controlled `ControlledSpinWait` struct. Coyote controls `SpinWait` so a spin yields to its
scheduler instead of burning CPU; Clockwork does the same. Inside a simulation a spin never consumes real
time: `SpinOnce` is a cooperative no-op that only advances the observable spin count, and `SpinUntil` pumps
the deterministic loop until its predicate holds (a never-satisfiable predicate surfaces as the loop-model
deadlock diagnostic). The finite `SpinUntil` overloads use a first-winner virtual-time deadline.
Invoking the substituted type without an active simulation fails before spinning.

| .NET 11 member | Posture | Rule id |
| --- | --- | --- |
| `SpinWait` type (value type) | ✅ Controlled (type substitution) | `clockwork.spinwait.type` |
| `Count` { get; } | ✅ Controlled | via `clockwork.spinwait.type` |
| `NextSpinWillYield` { get; } | ✅ Controlled | via `clockwork.spinwait.type` |
| `Reset()` | ✅ Controlled | via `clockwork.spinwait.type` |
| `SpinOnce()`, `SpinOnce(int)` | ✅ Controlled | via `clockwork.spinwait.type` |
| `SpinUntil(Func<bool>)` | ✅ Controlled | via `clockwork.spinwait.type` |
| `SpinUntil(Func<bool>, int)`, `SpinUntil(Func<bool>, TimeSpan)` | ✅ Controlled | via `clockwork.spinwait.type` |

**Semantics:** `Count` and `NextSpinWillYield` mirror the BCL's observable spin progress (the yield
threshold matches the documented value); `Reset` clears the count; `SpinOnce` never busy-spins; the
`SpinUntil` predicate is evaluated on the logical thread and the loop is pumped deterministically until it
holds or a virtual deadline elapses. Enumerated against the .NET 11 reference assemblies (2 properties, 4
instance methods, 3 static `SpinUntil` overloads); no applicable `SpinWait` member is left uncontrolled.

---

## `System.Threading.WaitHandle` / `EventWaitHandle` / `AutoResetEvent` / `ManualResetEvent` — Coyote `…Types.Threading` events

`AutoResetEvent`, `ManualResetEvent` and `EventWaitHandle` are **concrete sealed classes**, so — exactly
like the controlled `SemaphoreSlim` — the real object is retained as an **identity handle** while its
signalled state and a **deterministic FIFO waiter set** live in a weak-keyed side table. Each `new` is
redirected to a `Create` factory (`clockwork.autoresetevent.ctor`, `clockwork.manualresetevent.ctor`,
`clockwork.eventwaithandle.ctor.*`); every member inherited from `WaitHandle` (`WaitOne` ×5, `Dispose`,
`Close`) is a receiver-first shim on `ControlledWaitHandle`; `Set`/`Reset` are receiver-first shims on
`ControlledEventWaitHandle`. A `WaitOne` with no signal pumps the deterministic loop until `Set` (a
never-satisfiable wait surfaces as the loop-model deadlock diagnostic); an **auto-reset** `Set` wakes and
consumes exactly one eligible waiter (or leaves the event signalled until the next `WaitOne` consumes it),
while a **manual-reset** `Set` releases every waiter and stays signalled until `Reset`. Finite timeouts use
a first-winner virtual-time deadline (zero polls, infinite never times out). Named / cross-process APIs and
the raw native-handle accessors cannot be modelled in a single simulated process and are rejected precisely.
Coyote controls the same event surface on its cooperative scheduler; adapted from Microsoft Coyote (MIT).
Invoking these instrumented entry points without an active simulation fails before touching a real
BCL primitive.

| .NET 11 member | Posture | Rule id |
| --- | --- | --- |
| `new AutoResetEvent(bool)` | ✅ Controlled | `clockwork.autoresetevent.ctor` |
| `new ManualResetEvent(bool)` | ✅ Controlled | `clockwork.manualresetevent.ctor` |
| `new EventWaitHandle(bool, EventResetMode)` | ✅ Controlled | `clockwork.eventwaithandle.ctor.mode` |
| `new EventWaitHandle(bool, EventResetMode, string)` | ✅ Controlled (null name) / ⛔ Rejected (non-null name) | `clockwork.eventwaithandle.ctor.named` |
| `new EventWaitHandle(bool, EventResetMode, string, out bool)` | ✅ Controlled (null name) / ⛔ Rejected (non-null name) | `clockwork.eventwaithandle.ctor.named.creatednew` |
| `new EventWaitHandle(bool, EventResetMode, string, NamedWaitHandleOptions)` | ✅ Controlled (null name) / ⛔ Rejected (non-null name) | `clockwork.eventwaithandle.ctor.named.options` |
| `new EventWaitHandle(bool, EventResetMode, string, NamedWaitHandleOptions, out bool)` | ✅ Controlled (null name) / ⛔ Rejected (non-null name) | `clockwork.eventwaithandle.ctor.named.options.creatednew` |
| `WaitHandle.WaitOne()` | ✅ Controlled | `clockwork.waithandle.waitone` |
| `WaitHandle.WaitOne(int)` | ✅ Controlled | `clockwork.waithandle.waitone.milliseconds` |
| `WaitHandle.WaitOne(TimeSpan)` | ✅ Controlled | `clockwork.waithandle.waitone.timespan` |
| `WaitHandle.WaitOne(int, bool)` | ✅ Controlled | `clockwork.waithandle.waitone.milliseconds.exitcontext` |
| `WaitHandle.WaitOne(TimeSpan, bool)` | ✅ Controlled | `clockwork.waithandle.waitone.timespan.exitcontext` |
| `WaitHandle.WaitAny(WaitHandle[])` | ✅ Controlled | `clockwork.waithandle.waitany` |
| `WaitHandle.WaitAny(WaitHandle[], int)` | ✅ Controlled | `clockwork.waithandle.waitany.milliseconds` |
| `WaitHandle.WaitAny(WaitHandle[], TimeSpan)` | ✅ Controlled | `clockwork.waithandle.waitany.timespan` |
| `WaitHandle.WaitAny(WaitHandle[], int, bool)` | ✅ Controlled | `clockwork.waithandle.waitany.milliseconds.exitcontext` |
| `WaitHandle.WaitAny(WaitHandle[], TimeSpan, bool)` | ✅ Controlled | `clockwork.waithandle.waitany.timespan.exitcontext` |
| `WaitHandle.WaitAll(WaitHandle[])` | ✅ Controlled | `clockwork.waithandle.waitall` |
| `WaitHandle.WaitAll(WaitHandle[], int)` | ✅ Controlled | `clockwork.waithandle.waitall.milliseconds` |
| `WaitHandle.WaitAll(WaitHandle[], TimeSpan)` | ✅ Controlled | `clockwork.waithandle.waitall.timespan` |
| `WaitHandle.WaitAll(WaitHandle[], int, bool)` | ✅ Controlled | `clockwork.waithandle.waitall.milliseconds.exitcontext` |
| `WaitHandle.WaitAll(WaitHandle[], TimeSpan, bool)` | ✅ Controlled | `clockwork.waithandle.waitall.timespan.exitcontext` |
| `WaitHandle.SignalAndWait(WaitHandle, WaitHandle)` | ✅ Controlled | `clockwork.waithandle.signalandwait` |
| `WaitHandle.SignalAndWait(WaitHandle, WaitHandle, int, bool)` | ✅ Controlled | `clockwork.waithandle.signalandwait.milliseconds.exitcontext` |
| `WaitHandle.SignalAndWait(WaitHandle, WaitHandle, TimeSpan, bool)` | ✅ Controlled | `clockwork.waithandle.signalandwait.timespan.exitcontext` |
| `WaitHandle.Dispose()` | ✅ Controlled | `clockwork.waithandle.dispose` |
| `WaitHandle.Close()` | ✅ Controlled | `clockwork.waithandle.close` |
| `EventWaitHandle.Set()` | ✅ Controlled | `clockwork.eventwaithandle.set` |
| `EventWaitHandle.Reset()` | ✅ Controlled | `clockwork.eventwaithandle.reset` |
| `WaitHandle.Handle` { get; set; } | ⛔ Rejected (tested) | `clockwork.waithandle.get_handle` / `.set_handle` — exposes the OS handle |
| `WaitHandle.SafeWaitHandle` { get; set; } | ⛔ Rejected (tested) | `clockwork.waithandle.get_safewaithandle` / `.set_safewaithandle` — exposes the OS handle |
| `EventWaitHandle.OpenExisting(string)` / `(string, NamedWaitHandleOptions)` | ⛔ Rejected (tested) | `clockwork.eventwaithandle.openexisting[.options]` — cross-process |
| `EventWaitHandle.TryOpenExisting(string, out)` / `(string, NamedWaitHandleOptions, out)` | ⛔ Rejected (tested) | `clockwork.eventwaithandle.tryopenexisting[.options]` — cross-process |

**Semantics:** enumerated against the .NET 11 reference assemblies. The static multi-handle operations
`WaitHandle.WaitAny` (returns the lowest-index signalled handle), `WaitAll` (waits until every handle is
simultaneously signalled, then consumes them atomically so an auto-reset handle is never partially
consumed), and `SignalAndWait` (atomically signals the first handle then waits on the second) register
across all handles with no lost signals; they validate null / empty / over-64 arrays and — for `WaitAll`
— reject duplicate handles with `DuplicateWaitObjectException`. A non-null event
name and the `OpenExisting`/`TryOpenExisting` open-by-name APIs model a **system-wide kernel object** that
a single simulation process cannot faithfully represent, so they are rejected with a precise diagnostic; a
`null` name is a degenerate unnamed event and stays fully controlled. The raw `Handle`/`SafeWaitHandle`
accessors expose the underlying OS primitive (which would let code block a physical thread or signal a
kernel object outside the scheduler) and are likewise rejected.

---

## Modern synchronization — beyond this Coyote attribution ledger

Modern synchronization is part of Clockwork's implemented inventory, not a claim that these exact
implementations were adapted from Coyote. The runtime source comments retain explicit Coyote/MIT
attribution where it applies: the controlled wait-handle model says it is adapted from
Microsoft Coyote, and the earlier task/lock entries above retain their existing attribution. The new
Modern synchronization runtime type comments describe their own controlled models; they do not claim Coyote source
adaptation. The overlap is deliberate: unnamed `Mutex` and `Semaphore`, plus
`ManualResetEventSlim`/`CountdownEvent` bridges, use the existing controlled wait-handle kernel, so
they compose with the Coyote-attributed controlled event operations without exposing OS handles.

| .NET surface | Clockwork status | Exact posture |
| --- | --- | --- |
| `ReaderWriterLockSlim` | ✅ Controlled | All public constructors/properties/enter/try-enter/exit/`Dispose` members are receiver-first shims. Read, upgradeable-read, and write ownership/recursion are logical-strand state; contended entries pump the scheduler and finite timeouts use virtual time. |
| `ManualResetEventSlim` | ✅ Controlled | Constructors, properties, `Set`/`Reset`, all waits, `WaitHandle`, and `Dispose` are receiver-first shims. `SpinCount` remains observable metadata, but the model never busy-spins; the bridge is a controlled manual-reset handle. |
| Unnamed `Mutex` | ✅ Controlled | Constructors and `ReleaseMutex` use the controlled wait-handle kernel; `WaitOne`/`WaitAny` and `SignalAndWait` compose through it. Ownership/recursion are logical-strand state. Owner exit without release leaves it owned and leads to controlled deadlock detection rather than simulated abandonment. |
| Unnamed `Semaphore` | ✅ Controlled | Constructor and both `Release` overloads use the controlled wait-handle kernel; permit count, maximum count, and waiters are modelled rather than using kernel waits. |
| `SpinLock` | ✅ Controlled | Whole-type substitution to `ControlledSpinLock` preserves value-type copy and optional owner-tracking behavior while replacing CPU spinning with scheduler pumping and virtual deadlines. |
| `ExecutionContext` | ✅ Controlled / ⛔ Rejected | Capture/run/flow control/copy/dispose preserve the logical strand; legacy `GetObjectData` serialization is rejected. |
| `SynchronizationContext` | ✅ Controlled / ⛔ Rejected | Ambient state, `Post`, and `Send` stay in the simulation (`Post` queues, `Send` runs inline); raw native-handle `Wait` is rejected. |
| `Barrier` / `CountdownEvent` | ✅ Controlled | Whole-type substitutions keep participant/count state, callbacks, waits, cancellation, virtual deadlines, disposal, and the `CountdownEvent.WaitHandle` bridge under simulation. |
| Named mutex/semaphore forms and `OpenExisting`/`TryOpenExisting` | ⛔ Rejected | Non-null names are cross-process kernel state. Null-name constructor forms are the controlled unnamed case. |
| `WaitHandle.WaitAll` containing a `Mutex` | ⛔ Rejected | The shared kernel does not claim atomic multi-mutex acquisition. Other controlled-handle `WaitAll` cases validate arrays and consume auto-reset handles atomically. |

All modern synchronization controlled entry points are simulation-only: a missing simulation or runtime service fails
before a BCL operation can run, so there is no inactive pass-through. Waits pump deterministic work;
finite timeouts are virtual-time deadlines, not OS blocking or CPU spinning. Raw
`Handle`/`SafeWaitHandle` accessors, unmanaged/unregistered wait handles, and named/cross-process
event forms remain rejected as documented above.

## Virtual timers and timer-driven cancellation

`System.Threading.Timer`, `System.Timers.Timer`, and `PeriodicTimer` are whole-type substitutions
backed by the controlled deadline loop. `Task.Delay`, `Task.WaitAsync`, timed
`CancellationTokenSource` construction, `CancelAfter`, and `TimeProvider.System`/`CreateTimer` use
the same virtual-time ordering. Equal deadlines preserve registration order, reentrant changes
invalidate stale generations, periodic ticks coalesce where the BCL specifies, and callbacks never
escape to a physical thread pool. Custom providers and `System.Timers.Timer` designer/UI marshaling
are rejected precisely.

---

## Summary

- **Coyote `Thread`:** 13/13 controlled surfaces mirrored; Clockwork additionally **rejects** 4
  OS-specific members Coyote leaves uncontrolled.
- **Coyote `Task` static / async machinery:** all six `Task.Delay` overloads and all ten
  `Task.WaitAsync`/`Task<T>.WaitAsync` overloads use controlled virtual deadlines.
- **Coyote `TaskFactory`:** all 24 .NET 11 `StartNew` signatures classified and rewritten; default
  scheduler forms controlled, unsupported scheduler/option combinations rejected. Beyond Coyote, all 48
  `ContinueWhenAll`/`ContinueWhenAny` overloads are controlled and every `FromAsync` overload is rejected.
- **Coyote `TaskCompletionSource` / `TaskExtensions` / `ValueTask`:** the real completion sources are kept and their
  completion members redirected to publish race-detection edges; `Unwrap`, `AsTask`, and `Preserve` are controlled,
  and no controlled surface registers a BCL continuation on a task.
- **Coyote `Parallel`:** the element-only overloads controlled, plus `ForEachAsync` and `ForAsync` beyond Coyote;
  loop-state / thread-local overloads rejected (see [Parallel loops](compatibility.md#parallel-loops)).
- **`ThreadPool`:** modelled by Clockwork **beyond Coyote**; the registered-wait APIs
  (`RegisterWaitForSingleObject`/`UnsafeRegisterWaitForSingleObject`, all four timeout overloads) are
  controlled as passive event-driven waits and `UnsafeQueueNativeOverlapped` is rejected.
- **Coyote `Monitor`:** all 18 .NET 11 declared members classified: 17 synchronization methods
  controlled and process-wide `LockContentionCount` rejected; C# `lock (object)` is controlled in
  both Debug and Release lowering.
- **`System.Threading.Lock`:** controlled by type substitution **beyond Coyote**, covering the
  C# `lock (Lock)` scope lowering; nothing rejected.
- **Coyote `SemaphoreSlim`:** every constructor, `CurrentCount`, sync `Wait`, async `WaitAsync`,
  `Release`, and `Dispose` controlled; `AvailableWaitHandle` bridged to a controlled manual-reset
  wait handle tracking count > 0.
- **Coyote `Interlocked`:** full .NET 11 surface controlled — every `Increment`/`Decrement`/
  `Add`/`And`/`Or`/`Exchange`/`CompareExchange`/`Read` overload plus the memory barriers, each delegating
  to the real primitive since a cooperative logical thread makes the read-modify-write indivisible.
- **Coyote `Volatile`:** full .NET 11 surface controlled — every `Read`/`Write` overload plus
  the `ReadBarrier`/`WriteBarrier` fences, delegating to the real primitive with acquire/release intent
  preserved.
- **Coyote `SpinWait`:** the `SpinWait` value type controlled by type substitution — `Count`,
  `NextSpinWillYield`, `Reset`, both `SpinOnce` overloads and all three static `SpinUntil` overloads; a
  controlled spin yields to the deterministic loop instead of burning CPU, and finite `SpinUntil` uses a
  first-winner virtual-time deadline.
- **Coyote events (`AutoResetEvent`/`ManualResetEvent`/`EventWaitHandle`/`WaitHandle`):** controlled — ctors redirect to `Create` factories, the five `WaitOne` overloads plus `Dispose`/`Close`
  and `Set`/`Reset` are receiver-first shims over a modelled signalled state and deterministic FIFO waiter
  set (auto-reset wakes exactly one waiter, manual-reset releases all and stays signalled), finite timeouts
  use a virtual-time deadline; the static `WaitAny` (lowest-index signalled), `WaitAll` (all-signalled,
  atomic consume) and `SignalAndWait` (atomic signal-then-wait) multi-handle operations are controlled with
  full array validation; named/cross-process open APIs and the raw `Handle`/`SafeWaitHandle` accessors are
  rejected with tested diagnostics.
- **`ThreadPool` registered waits (`RegisterWaitForSingleObject`/`UnsafeRegisterWaitForSingleObject`):**
  controlled — a passive event-driven waiter on the target handle's modelled signalled state
  that never blocks the logical thread; fires `timedOut:false` on a signal (auto-reset consumes exactly
  one) or `timedOut:true` on the virtual-time deadline, honours `executeOnlyOnce`/re-arm, flows
  `ExecutionContext` for the safe family only, and substitutes the returned `RegisteredWaitHandle` so
  `Unregister` stops the wait and signals its completion event.
- **Modern synchronization additions beyond this Coyote attribution ledger:** `ReaderWriterLockSlim`,
  `ManualResetEventSlim`, unnamed `Mutex`/`Semaphore`, `SpinLock`, `ExecutionContext`,
  `SynchronizationContext`, `Barrier`, and `CountdownEvent` are controlled with logical-strand
  ownership, virtual time, and no OS blocking; named/cross-process forms and raw context waits are
  rejected. `WaitAll` containing a `Mutex` is rejected.
- **Virtual timer surface:** `System.Threading.Timer`, `System.Timers.Timer`, `PeriodicTimer`,
  `Task.Delay`, `Task.WaitAsync`, timed cancellation-source construction, `CancelAfter`, and
  `TimeProvider` timer creation are controlled; custom providers and designer/UI marshaling reject.

Every Coyote entry above is therefore **controlled** (with a cited rule id or by architecture) or
**deliberately rejected with a tested reason**; no listed concurrency surface is silently unhandled.
