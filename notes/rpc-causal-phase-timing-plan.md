# RPC causal phase timing plan

## Purpose

The next performance pass should measure where wall-clock time is spent in an
individual cross-silo call instead of inferring latency from sampled CPU stacks.
At one outstanding call, most elapsed time is off-CPU and small changes are
easily hidden by CPU frequency, thermal state, ThreadPool wake-up latency, and
background activity. CPU and hardware-counter profiles remain useful at
saturation, but they cannot identify which asynchronous handoff delayed a
single call.

This plan adds low-overhead, deterministically sampled point events for the
request/response path, extends pvanalyze to reconstruct calls and aggregate
phase durations, separates the two silos into independently controllable
processes, and defines a repeatable host-quiescence procedure. It also adds
active causal profiling based on Coz: randomized virtual-speedup experiments
and progress points which test whether optimizing a phase would improve
end-to-end latency or throughput.

The instrumentation must:

- be disabled by default with a nearly free `EventSource.IsEnabled` branch;
- preserve the message wire format;
- make the same sampling decision independently on both silos;
- correlate request and response events across processes;
- avoid allocating an `Activity`, string, or state object per call;
- report incomplete and ambiguous samples instead of silently repairing them;
- distinguish exact wall-clock phase timing from sampled CPU attribution; and
- distinguish validated virtual-speedup predictions from slowdown sensitivity;
  and
- include an unchanged control which quantifies instrumentation overhead.

## Why point events instead of per-call activities

Orleans already has `DiagnosticListener` message events in
`MessagingEvents`, but those calls are compiled out unless `MESSAGING_TRACE`
is defined. The existing EventSources in `EventSourceEvents.cs` emit events
without message identity, so they cannot reconstruct a call.

Creating and propagating an `Activity` for every sampled call would add more
state and lifecycle behavior to the path being measured. Propagating a new
activity identifier would also alter request context or the wire protocol.
Instead, a dedicated EventSource should emit fixed-schema point events keyed
by the existing 64-bit `Message.Id`. Requests and their responses already
carry the same correlation ID over the wire.

Use an explicit key consisting of:

```text
(origin silo identity, correlation ID)
```

For a request, the origin is `SendingSilo`; for a response, it is
`TargetSilo`. Include the local silo identity and process role in every event
so one-process and split-process traces can both be analyzed. A compact port
and generation pair is preferable to an allocated address string. pvanalyze
must warn if the selected identity is not unique in the trace.

Activity IDs can remain available for optional CPU/activity-stack attribution,
but they should not be the primary causal key.

## Sampling

### Deterministic decision

Sampling must be derived from the correlation ID rather than local mutable
state. Every process then makes the same decision without adding a sampled bit
to the message:

```text
sample = Mix64((ulong)correlationId) & (sampleRate - 1) == 0
```

`sampleRate` must be a power of two. Use a stable integer mixer so sequential
IDs remain uniformly distributed. The EventSource should parse a
`SampleRate` provider argument in `OnEventCommand` and publish the resulting
mask through a volatile field.

Do not rely on that argument until the collectors can demonstrably deliver it.
The current pvanalyze `CollectCommand.CreateProviders` accepts only
`Name:Keywords:Level`; extend it to parse provider arguments and pass an
argument dictionary to `EventPipeProvider`. For PerfView, validate the exact
EventSource provider-argument syntax with a capture test. Until both paths are
verified, set the same `ORLEANS_RPC_TRACE_SAMPLE_RATE` environment variable in
the driver and target before process startup and treat provider arguments only
as an override.

Publish a safe sample-none mask before configuration is complete. Include the
effective sample rate in every phase event. pvanalyze must reject a causal
report if participating processes disagree on the rate instead of quietly
producing mostly incomplete pairs.

Suggested starting rates:

| Workload | Calls/s | Rate | Expected sampled calls in 30 s |
| --- | ---: | ---: | ---: |
| Single-flight latency | 25K-35K | 1/64 | 12K-16K |
| Moderate concurrency | 100K-300K | 1/512 | 6K-18K |
| Saturated throughput | 750K-900K | 1/4096 | 5K-7K |

Run a rate sweep of disabled, 1/16384, 1/4096, 1/1024, and 1/64. A usable rate
must not move the unchanged throughput control or latency distribution beyond
its paired-run noise. Event loss must remain zero.

### Fast path

The call-site shape should be:

```csharp
if (RpcCallEventSource.Log.IsEnabled() && RpcCallEventSource.Log.IsSampled(message.Id))
{
    RpcCallEventSource.Log.Phase(message, RpcCallPhase.RequestTransportQueued);
}
```

`IsSampled` and the disabled check should inline. The event payload should use
primitive values only. Do not store a trace flag on `Message`: that increases
the hot object size even when tracing is disabled and creates reset/ownership
requirements for pooled messages.

## Event schema

Add an internal EventSource named `Microsoft-Orleans-RpcLatency`, preferably in
`src\Orleans.Core\Diagnostics\RpcCallEventSource.cs`.

Use one stable `Phase` event instead of one event ID per phase. Its payload
should contain:

| Field | Type | Purpose |
| --- | --- | --- |
| `correlationId` | `long` | Existing `Message.Id` |
| `originSiloPort` | `int` | Stable origin component |
| `originSiloGeneration` | `int` | Disambiguates restarts |
| `localSiloPort` | `int` | Identifies the emitting silo |
| `localSiloGeneration` | `int` | Disambiguates restarts |
| `direction` | `byte` | Request, response, or one-way |
| `phase` | `byte` | Stable `RpcCallPhase` value |
| `retryCount` | `int` | Detects resend attempts |
| `forwardCount` | `int` | Detects legitimate forwarded hops |
| `batchSize` | `int` | Shared flush/dispatch batch size |
| `batchIndex` | `int` | Per-message head-of-line position |
| `detail` | `int` | Phase-specific byte count, transport kind, or outcome |
| `durationTicks` | `long` | Optional local operation duration |
| `stopwatchFrequency` | `long` | Converts duration ticks without assumptions |
| `sampleRate` | `int` | Verifies consistent sampling across processes |

Process ID, thread ID, processor number, event timestamp, provider, and activity
IDs already come from ETW/EventPipe and should not be duplicated.

There is no typed `EventSource.WriteEvent` overload for this payload. A
`params object[]` call would allocate an array and box every primitive. The
implementation must use an unsafe `WriteEventCore` method with stack-allocated
`EventData` descriptors, following the runtime's allocation-free EventSource
patterns. Add an allocation test which repeatedly emits enabled sampled events
and verifies zero managed bytes after warmup.

Keep phase enum values append-only. Reserve values for failure, rejection,
timeout, cancellation, forwarding, and retry markers even if the first pass
only analyzes successful benchmark calls.

## Phase model

The first implementation should instrument these boundaries:

| Phase | Location | Meaning |
| --- | --- | --- |
| `RequestCreated` | `InsideRuntimeClient.SendRequest` after callback registration | Runtime owns a request and starts addressing |
| `RequestAddressingComplete` | `MessageCenter.SendMessage` immediately before `Connection.Send` | Async placement/addressing and connection lookup completed |
| `TransportQueued` | `Connection.Send` after `TryWrite` succeeds | Message entered the connection queue |
| `SerializeStart` | `Connection.ProcessOutgoing` before `MessageSerializer.Write` | Transport worker began serialization |
| `SerializeStop` | immediately after `MessageSerializer.Write` | Frame bytes are in the pipe writer |
| `FlushStart` | before `PipeWriter.FlushAsync` | Current batch submitted for flush |
| `FlushStop` | after the flush completes | Batch was accepted by the transport |
| `FrameDecoded` | `Connection.ProcessIncoming` after `MessageSerializer.TryRead` | Header/body decode completed |
| `DispatchQueued` | when the populated `MessageHandler` is queued | Decoded batch is waiting for ThreadPool dispatch |
| `DispatchStart` | `MessageHandler.Execute` immediately before each `OnReceivedMessage` | Per-message inbound handoff started |
| `RuntimeReceived` | `MessageCenter.ReceiveMessage` entry | Silo routing began |
| `ActivationQueued` | `ActivationData.ReceiveRequest` after `_waitingRequests.Add` | Request entered the activation queue |
| `InvocationStart` | `ActivationData.InvokeIncomingRequest` before `RuntimeClient.Invoke` | Grain invocation began |
| `InvocationStop` | `InsideRuntimeClient.Invoke` after the invokable completes | Grain result is available |
| `ResponseCreated` | `MessageCenter.SendResponse` after creating the response | Response entered the outbound runtime path |
| `CallbackStart` | `InsideRuntimeClient.ProcessResponseCallback` | Correlated callback was removed |
| `CallbackComplete` | `CallbackData.DoCallback` after `ResponseCallback` | Response source was resolved |

Transport phases apply to both request and response directions. For a batch,
emit `FlushStart` and `FlushStop` for every sampled message in `inflight`; all
messages in the batch legitimately share those timestamps. Include batch size
and index. Do the same when a `MessageHandler` is queued: all messages share
`DispatchQueued`, while each gets its own `DispatchStart`. The resulting delta
intentionally includes ThreadPool queue delay plus head-of-line time behind
earlier messages in the same inbound batch.

`FrameDecoded` alone combines socket arrival and deserialization. To separate
them without parsing the correlation ID twice, capture a timestamp before
`TryRead` only while the provider is enabled. After decoding reveals the ID,
emit `FrameDecoded` for sampled messages with decode duration in
`durationTicks`. This adds one timestamp read per frame only during a trace.
The analyzer can subtract decode time from the cross-process
`FlushStop -> FrameDecoded` interval. Measure the enabled-but-zero-sample
control to quantify that cost.

The initial terminal boundary is `CallbackComplete`, not the user continuation
after `await`. `ResponseCompletionSource` lives in the serialization assembly
and does not currently know the Orleans correlation ID. If phase totals leave
an unexplained gap versus benchmark-observed latency, add a second-stage design
which carries an optional primitive trace key into the completion source and
emits from `GetResult`; do not add that coupling preemptively.

## Derived durations

pvanalyze should calculate at least:

| Derived phase | Start | Stop |
| --- | --- | --- |
| Placement/addressing | `RequestCreated` | `RequestAddressingComplete` |
| Origin send routing | `RequestAddressingComplete` | request `TransportQueued` |
| Request connection queue | request `TransportQueued` | request `SerializeStart` |
| Request serialization | request `SerializeStart` | request `SerializeStop` |
| Request flush wait | request `SerializeStop` | request `FlushStop` |
| Request wire and receive | request `FlushStop` | request `FrameDecoded` minus decode duration |
| Request inbound dispatch queue | request `FrameDecoded` | request `DispatchStart` |
| Connection callback | request `DispatchStart` | request `RuntimeReceived` |
| Target routing/activation lookup | request `RuntimeReceived` | `ActivationQueued` |
| Activation queue | `ActivationQueued` | `InvocationStart` |
| Grain invocation | `InvocationStart` | `InvocationStop` |
| Response construction/routing | `InvocationStop` | response `TransportQueued` |
| Response connection queue | response `TransportQueued` | response `SerializeStart` |
| Response serialization | response `SerializeStart` | response `SerializeStop` |
| Response flush wait | response `SerializeStop` | response `FlushStop` |
| Response wire and receive | response `FlushStop` | response `FrameDecoded` minus decode duration |
| Response inbound dispatch queue | response `FrameDecoded` | response `DispatchStart` |
| Response connection callback | response `DispatchStart` | response `RuntimeReceived` |
| Response runtime routing | response `RuntimeReceived` | `CallbackStart` |
| Callback resolution | `CallbackStart` | `CallbackComplete` |
| Runtime end-to-end | `RequestCreated` | `CallbackComplete` |

For same-machine ETW, timestamps share the system QPC and cross-process deltas
are valid. A single EventPipe trace is valid for the current one-process
benchmark. Independent EventPipe traces from split processes must not be merged
unless pvanalyze implements explicit clock synchronization.

## Retries, forwarding, and incomplete calls

The benchmark expects one request hop and one response hop, but the format must
not assume that all Orleans calls are that simple.

- Use `retryCount` to group repeated transport attempts and `forwardCount` to
  identify legitimate hops.
- Retain repeated phase occurrences instead of overwriting them.
- Mark forwarded calls as multi-hop and report each hop separately.
- Exclude rejections, timeouts, and cancellations from the successful latency
  distribution by default, while reporting their counts.
- Report duplicate starts, stops without starts, starts without terminal
  events, negative durations, phase-order violations, and samples truncated by
  the selected time window.
- Print a completeness ratio. Performance conclusions require a high and
  comparable ratio on both sides.
- Classify local delivery explicitly. A local call correctly lacks
  serialize/flush/decode phases and must not be reported as window-truncated or
  corrupt.

## Benchmark changes

The current `AdaptivePingBenchmark` starts both silos in one process. Keep that
mode as the control, but add a split-process profile mode under
`test\Benchmarks\Ping`:

1. A target command starts the secondary silo, hosts `PingGrain`, prints its
   PID/role/endpoints, emits a ready marker, and waits for shutdown.
2. A driver command starts the primary silo without `PingGrain`, connects to
   the target, runs the fixed workload, and prints its PID/role/endpoints.
3. A PowerShell harness starts both prebuilt Release binaries, waits for
   readiness, sets process affinity and priority, collects the ETW process tree,
   and shuts both down by a cooperative cancellation signal.

Do not use fixed sleeps as readiness. Use a named pipe, loopback control socket,
or readiness file created atomically after cluster membership and a warmup call
succeed. Preserve the existing one-process `FixedPing` command so every
instrumented run has an unchanged control.

Add benchmark phase events for `Startup`, `WarmupStart`, `WarmupStop`,
`MeasurementStart`, `MeasurementStop`, and `Shutdown`. pvanalyze should accept
`--measurement-window` to select these markers instead of manually guessing
`--from` and `--to`.

The driver should record per-call latency in a fixed-size HDR-style histogram
or equivalent allocation-free recorder. Report count, mean, p50, p90, p99,
p99.9, and max. Throughput alone is only a reciprocal mean at concurrency one
and hides tails.

## pvanalyze changes

Add a `phases` command, with `latency` as an alias:

```powershell
pvanalyze phases trace.etl `
  --provider Microsoft-Orleans-RpcLatency `
  --process-role driver --measurement-window `
  --format text

pvanalyze phases trace.etl --format json --output phases.json
```

Command options:

- `--pid` and `--process` filters;
- `--process-role` and `--origin-silo` filters;
- `--from` and `--to`;
- `--measurement-window`;
- `--successful-only` (default true);
- `--include-incomplete`;
- `--min-completeness`;
- `--format text|json`; and
- optional `--with-cpu` to add sampled CPU attribution.

Implementation surfaces in `C:\dev\pvanalyze`:

- register the command in `Program.cs`;
- add `Commands\LatencyCommand.cs`;
- add response DTOs and JSON metadata in `Models.cs`;
- extend `TraceCapabilities.cs` to recognize the Orleans provider/schema;
- put reusable correlation and aggregation logic in `TraceAnalyzer.cs`; and
- extend `Commands\CollectCommand.cs` to preserve EventPipe provider arguments;
- document ETW/EventPipe examples in `README.md`.

The command must reconstruct correlation state before applying `--from`, just
as activity stack analysis currently preserves pre-window events. Filter
reported samples after reconstruction so a request which begins before the
window can be classified as truncated rather than mispaired.

Use one pass over phase events and dictionaries keyed by
`(originSilo, correlationId)`, then bucket occurrences by
`(direction, phase, retryCount, forwardCount, localSilo)`. Avoid payload string
conversion and stack construction unless requested. For each derived phase and
end-to-end duration, report:

- count and completeness;
- mean, p50, p90, p99, p99.9, and max;
- standard deviation and median absolute deviation;
- missing-start, missing-stop, duplicate, out-of-order, retry, forwarded, and
  cross-process counts; and
- sampled CPU milliseconds when `--with-cpu` is enabled.

JSON output must include the selected window, process identities, provider
arguments, trace loss count, sampled-call count, estimated source-call count,
sampling rate, warnings, and percentile method. Use a consistent percentile
definition and test boundary cases.

When `--with-cpu` is requested, reuse TraceEvent's Start/Stop activity
machinery only for CPU attribution. Wall-clock phase durations come from the
explicit phase markers. Label CPU-only results as sampled attribution, never
as exact elapsed time.

### pvanalyze tests

Add synthetic trace/event tests for:

- request/response reconstruction across two PIDs;
- deterministic sample identity;
- all normal phase deltas;
- shared flush timestamps for a batch;
- retries and forwarding;
- missing, duplicate, and out-of-order events;
- window truncation with a start before `--from`;
- PID, role, and silo filters;
- percentile and deviation calculations;
- JSON/text schema; and
- ETW versus EventPipe event projection.

## Causal profiling capability

The phase trace should support active causal experiments modeled on
[Coz](https://cacm.acm.org/research/coz/) in addition to passive attribution.
The key distinction is:

- `pvanalyze phases` answers **where elapsed time was observed**; and
- `pvanalyze causal` answers **whether making a selected phase relatively
  faster changes an end-to-end progress metric**.

Coz implements a virtual speedup by allowing the thread executing selected code
to advance while delaying all other threads by the corresponding amount. It
then measures throughput or latency progress points over randomized experiment
windows. A hot phase with a flat causal curve is not worth optimizing; a phase
with a steep positive curve is on the critical path; a negative curve can
indicate contention.

### Scope of valid causal claims

Classic Coz is thread-centric and samples on-CPU source lines. Orleans calls
move among ThreadPool threads, release threads at `await`, and cross processes.
The first implementation must not claim that every named wall-clock phase can
be virtually sped up with Coz's exact semantics.

Classify phases into three groups:

| Class | Examples | Supported experiment |
| --- | --- | --- |
| Synchronous local execution | serialization, deserialization, routing, callback lookup | Coz-style virtual speedup |
| Logical scheduler execution | activation turns and grain continuations | Not eligible until per-continuation on-CPU spans and wake-credit semantics exist |
| Waiting/external time | network transit, flush wait, activation queue wait, arbitrary grain awaits | Randomized slowdown sensitivity or observation only |

For waiting/external phases, pvanalyze must label a positive-delay experiment
as `slowdown-sensitivity`. It may estimate a local derivative near zero, but it
must not invert that result into a claimed speedup unless a synthetic benchmark
demonstrates symmetry and linearity over the reported range.

### Cooperative virtual time

Use Coz's global/local pause-counter model with Orleans logical participants
instead of enumerating activations on every phase hit.

Each benchmark process registers participants for:

- each connection input loop;
- each connection output loop;
- each activation scheduler/work group;
- hosted-client callback dispatch; and
- any other scheduler which can make progress independently during the
  selected experiment.

The experiment state contains a global virtual-time epoch. Each participant
stores the epoch it has already observed.

When participant `P` completes an eligible selected phase with duration `D`
and virtual speedup fraction `s`:

1. Compute `credit = s * D`.
2. Atomically advance the global virtual-time epoch by `credit`.
3. Advance `P`'s local epoch by the same credit, so `P` does not pause for its
   own selected work.
4. Other participants compare their local epoch with the global epoch at the
   next safe point, pause for the difference, and then catch up.

Coz treats suspension reasons differently. A participant blocked on external
I/O accumulates debt and applies it after resume; paying while no progress was
possible would have no effect. A participant waiting for another participant
through a lock, signal, queue, or task continuation can instead receive credit
from the participant which wakes it. Orleans must classify safe points
accordingly:

- connection socket/pipe waits use I/O debt semantics;
- activation work signals, channel wakeups, task continuations, and locks use
  synchronization-credit semantics; and
- producers reconcile their own debt before blocking or waking another
  participant, then transfer the appropriate observed epoch to the consumer.

Do not treat every `await` as I/O. A newly created participant inherits the
creating participant's observed epoch when there is a causal parent; otherwise
it starts at the current global epoch.

Use a shared, versioned memory-mapped control block so the epoch is common to
the driver and target processes on the same machine. The block must contain
cache-line-aligned atomics for:

- schema/version and session ID;
- experiment ID, selected phase, mode, and speedup basis points;
- absolute QPC start/end timestamps;
- global virtual-time epoch;
- throughput progress count;
- latency begin/end counts and in-flight-area accumulator;
- requested/applied/overshoot pause totals;
- skipped checkpoints, debt-cap hits, and participant count; and
- emergency-disable state.

An aligned cross-process `Interlocked` update must be covered by a native
integration test. Do not use this design across machines: independent QPC
domains and network coordination invalidate the shared-epoch model.

### Safe points and pause application

Participants check debt only where pausing cannot leave a lock held or corrupt
protocol state:

- before processing the next connection message/batch;
- before an activation dequeues the next runnable turn;
- before hosted-client callback dispatch; and
- before blocking, waking, or handing work to another participant when needed
  to preserve Coz's delay-credit semantics.

Never pause while holding an activation, callback-dictionary, connection, pipe,
or message-queue lock.

The first prototype should accumulate small credits into a configurable delay
quantum instead of issuing a timer operation for every phase hit. Use a
high-resolution waitable timer for the coarse portion and a short calibrated
spin only for the final sub-quantum tail. Record requested and actual pause
duration. Exclude an experiment when timer overshoot exceeds its configured
limit.

Blocking ThreadPool workers can cause hill-climbing to inject replacement
threads, defeating the experiment. Record worker count, pending work, and
starvation events. The physical-worker prototype is a feasibility probe, not a
trusted profiler: start with short windows and small credits, and exclude
windows where worker count changes materially. Trust throughput curves only
after scheduler-level delay can postpone logical dequeue without blocking or
spinning a physical ThreadPool worker.

Crediting the full `s * D` only after phase completion is also a lump-sum
approximation to Coz's continuous sampled pauses. Require known critical-path,
off-critical-path, and contention synthetic tests to match predictions before
using a real phase curve. If they do not, reduce the delay quantum or add
checkpoints within the phase.

The effective experiment duration is:

```text
effective duration = wall duration - change in global virtual-time epoch
```

It is not the sum of every participant's physical waits, since those waits are
conceptually concurrent copies of the same virtual-time debt. Record physical
wait totals only as a validity diagnostic.

### Randomized experiment controller

Add a benchmark-side controller rather than a production system grain for the
first implementation. It owns the shared control block and:

1. waits until warmup and JIT activity finish;
2. randomly selects an eligible phase;
3. chooses 0% speedup with 50% probability and otherwise chooses uniformly
   from configured non-zero levels;
4. publishes a future QPC start and end time, then waits for both processes to
   acknowledge the exact experiment tuple;
5. snapshots progress counters and runs the window;
6. records virtual time, physical pause diagnostics, GC/JIT/thread-pool state,
   and progress deltas;
7. runs a cool-off interval long enough for all participants to catch up; and
8. repeats without adapting phase selection based on earlier results.

Randomize both phase and speedup. Every phase requires its own 0% windows so
phase-specific instrumentation/checkpoint overhead is represented in the
baseline. Require a minimum number of progress points and completed windows per
speedup. Do not shorten or prioritize experiments based on promising early
results.

### Progress points

Use end-to-end progress, not grain execution alone:

- Throughput progress point: `CallbackComplete` on the calling silo.
- Latency begin: `RequestCreated`.
- Latency end: `CallbackComplete`.

The shared control block increments progress counters without emitting an event
per call. At experiment end, emit one summary snapshot.

For latency, pvanalyze should integrate the in-flight count over the experiment
window on the virtual timeline and apply Little's Law:

```text
virtual time   = wall time - global virtual-time epoch
virtual area   = integral of in-flight count over virtual time
mean in-flight = virtual area / effective duration
arrival rate   = latency begins / effective duration
mean latency   = mean in-flight / arrival rate
               = virtual area / latency begins
```

Exclude unstable windows where in-flight count trends upward or begin/end
imbalance does not settle during cool-off. Compare the estimate with directly
sampled `RequestCreated -> CallbackComplete` durations from the observational
trace. The first causal milestone is throughput-only; latency curves remain
disabled until virtual-time integration matches direct latency in synthetic
and end-to-end tests.

Causal curves require concurrent progress to delay. They describe the measured
concurrent workload, typically the saturated guard, and must not be presented
as predictors of single-flight latency. Single-flight changes still require
observational phase evidence and paired direct latency measurements.

### Causal experiment events

Add a companion allocation-free EventSource named
`Microsoft-Orleans-CausalProfiling`. It emits rare control/summary events:

| Event | Required data |
| --- | --- |
| `SessionStarted` | schema, session ID, QPC frequency, process/silo identity, control-block name |
| `ExperimentStarted` | experiment ID, phase, mode, speedup, start/end QPC, delay quantum |
| `ExperimentEnded` | progress deltas, virtual-time delta, requested/applied/overshoot pauses, validity counters |
| `ExperimentAborted` | reason and diagnostics |
| `ParticipantSummary` | participant kind/count, checkpoints, debt, skipped/capped pauses |

Use `WriteEventCore` with primitive payloads. Fine-grained phase hits remain in
`Microsoft-Orleans-RpcLatency`; do not emit a causal event for every progress
point or pause.

### Slowdown-sensitivity experiments

For phases which cannot satisfy virtual-speedup semantics, randomly add a small
positive delay to the selected phase while leaving other participants
unchanged. Compare treatment windows with phase-specific zero-delay windows.
Report:

- added delay distribution;
- throughput and latency response;
- local derivative and confidence interval; and
- non-linearity across delay levels.

This is a valid causal statement about the effect of slowing the phase under
the measured workload. It is not automatically a prediction of the benefit
from speeding the phase up.

### pvanalyze causal command

Add:

```powershell
pvanalyze causal trace.etl `
  --progress callback-complete `
  --format text

pvanalyze causal trace.etl --format json --output causal.json
```

Implementation surfaces:

- `Commands\CausalProfileCommand.cs` for CLI and output;
- `CausalProfileAnalyzer.cs` for experiment reconstruction and estimators;
- `Models.cs` for session, experiment, curve, confidence, and warning DTOs;
- `TraceCapabilities.cs` for provider/schema detection;
- `Program.cs` for command registration; and
- `CollectCommand.cs` for a minimal causal provider profile and provider
  arguments.

Group experiments by `(phase, mode, speedup)`. Pool progress visits and
effective duration only after validating each window. Compute:

```text
baseline rate = sum(progress visits at 0%) / sum(effective duration at 0%)
treatment rate = sum(progress visits at s) / sum(effective duration at s)
program speedup = treatment rate / baseline rate - 1
```

For latency, use the in-flight integral above. Report each curve point with
experiment count, progress count, confidence interval, excluded-window count,
and reasons. Rank virtual-speedup phases using the near-zero slope and show the
full curve; a single large-speedup point is insufficient. Negative slopes are
contention indicators, not automatic optimization prescriptions.

Use bootstrap confidence intervals over experiment windows and retain raw
window results in JSON. Randomization is the basis for causal attribution;
regression does not repair non-random experiment order.

### Causal validity checks

Exclude or flag windows with:

- mismatched experiment tuples or QPC boundaries across processes;
- process restart, control-block version mismatch, or missing acknowledgement;
- event loss or incomplete progress snapshots;
- JIT during the selected phase;
- Gen2 GC or excessive GC pause;
- non-stationary arrival/progress rate;
- unstable in-flight population;
- too few progress points;
- low participant concurrency for a virtual-speedup experiment;
- ThreadPool worker growth/starvation during pause injection;
- pause overshoot or capped debt above threshold;
- participant checkpoint coverage below threshold; or
- affinity, power policy, or collector configuration changes.

Causal output must state its scope. A validated cooperative virtual-speedup
curve supports a prediction for the named phase under the measured workload.
An observational phase, network wait, or positive-delay experiment does not.
Until the non-blocking scheduler implementation and synthetic prediction gates
pass, label output `experimental-feasibility` and suppress optimization
recommendations.

### Causal profiling tests

Add synthetic and end-to-end tests for:

- the global/local epoch algorithm with two and many participants;
- current-participant credit and suspended-participant catch-up;
- cross-process aligned atomics and shared QPC experiment boundaries;
- participant creation and emergency disable;
- no pauses while locks are held;
- delay quantum accumulation, overshoot, caps, and cool-off;
- exact effective-duration calculation from global virtual time;
- randomized phase/speedup selection with phase-specific 0% baselines;
- throughput and Little's Law latency estimators;
- virtual-time rather than wall-time in-flight integration;
- I/O debt accumulation and synchronization-wait credit transfer;
- exclusion of GC, JIT, starvation, unstable, and mismatched windows;
- a synthetic known critical path with a positive curve;
- an off-critical-path phase with a flat curve;
- a contention case with a negative curve;
- a known real optimization whose measured gain matches the prediction; and
- slowdown-sensitivity output which is never mislabeled as virtual speedup.

The first milestone should support one synchronous target phase (for example
request serialization) and one throughput progress point. Expand to scheduler
participants only after the synthetic prediction test passes.

Causal profiling references:

- [Coz: Finding Code that Counts with Causal Profiling](https://cacm.acm.org/research/coz/)
- [Coz SOSP paper](https://arxiv.org/abs/1608.03676)
- [Coz implementation](https://github.com/plasma-umass/coz)

## Windows system quiescence

### What can and cannot be controlled

Windows can request a performance range, disable core parking, and change boost
policy, but firmware, thermal, and electrical limits remain active. On an Intel
desktop, a fixed multiplier, disabled Turbo Boost, and restricted C-states may
be available in BIOS/UEFI or vendor tooling. Those settings are machine-specific
and must be recorded and restored. An OS power plan by itself is not proof of a
fixed frequency.

`Stopwatch`/QPC is the correct clock on modern Windows systems and is independent
of CPU frequency changes. Do not call `timeBeginPeriod` to improve measurement
accuracy: Microsoft documents that it does not improve QPC accuracy and can
prevent normal power management.

Use two separately labeled configurations:

1. **Representative mode:** AC power, stable thermal state, normal process
   affinity, normal security services, and the selected performance power
   policy. This is the result used for product claims.
2. **Diagnostic mode:** the same setup plus disjoint source/target process
   affinity and High process priority. This reduces migration and contention
   enough to identify causal phases, but it is not a production-representative
   throughput result.

### Reversible power-policy setup

Run from an elevated PowerShell window. Duplicate the active OEM scheme rather
than modifying it in place. The example requests 100% minimum/maximum processor
state, disables parking, and requests disabled boost for lower thermal drift.
Firmware may ignore unsupported settings, especially boost mode, so query the
scheme after setting it and record the output. If the Intel benchmark machine
supports a fixed-ratio BIOS profile, use that instead of claiming the power plan
locked frequency.

```powershell
#Requires -RunAsAdministrator
$active = powercfg /getactivescheme
$originalScheme = [regex]::Match(
    $active,
    '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}').Value
if (-not $originalScheme) { throw "Could not read the active power scheme" }

$duplicate = powercfg -duplicatescheme $originalScheme
$benchmarkScheme = [regex]::Match(
    $duplicate,
    '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}').Value
if (-not $benchmarkScheme) { throw "Could not duplicate the active power scheme" }

# Processor subgroup and settings.
$processor = '54533251-82be-4824-96c1-47b60b740d00'
$minimumState = '893dee8e-2bef-41e0-89c6-b55d0929964c'
$maximumState = 'bc5038f7-23e0-4960-96da-33abaf5935ec'
$boostMode = 'be337238-0d82-4146-a960-4f3749d470c7'
$minimumCores = '0cc5b647-c1df-4637-891a-dec35c318583'
$maximumCores = 'ea062031-0e34-4ff1-9b6d-eb1059334028'

powercfg /setacvalueindex $benchmarkScheme $processor $minimumState 100
powercfg /setacvalueindex $benchmarkScheme $processor $maximumState 100
powercfg /setacvalueindex $benchmarkScheme $processor $minimumCores 100
powercfg /setacvalueindex $benchmarkScheme $processor $maximumCores 100
powercfg /setacvalueindex $benchmarkScheme $processor $boostMode 0
powercfg /setactive $benchmarkScheme
powercfg /query $benchmarkScheme $processor

try
{
    # Launch and measure here.
}
finally
{
    powercfg /setactive $originalScheme
    powercfg /delete $benchmarkScheme
}
```

If disabling boost lowers frequency too far or the firmware ignores the setting,
compare two fully documented policies: boost disabled for stability and boost
enabled after a sustained thermal warmup. Never mix policies within a paired
comparison.

Windows can lower application QoS after user inactivity. Microsoft recommends
disabling user-presence QoS for unattended performance tests, especially on
battery. Prefer AC power and first determine whether the policy affects this
machine. If the registry override is required, capture the prior value, set it
before the benchmark session, and restore it afterward:

```powershell
#Requires -RunAsAdministrator
$path = 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling'
$old = (Get-ItemProperty $path -Name DisableUserPresenceQos -ErrorAction SilentlyContinue).
    DisableUserPresenceQos

New-Item $path -Force | Out-Null
Set-ItemProperty $path -Name DisableUserPresenceQos -Type DWord -Value 1

try
{
    # Benchmark session.
}
finally
{
    if ($null -eq $old)
    {
        Remove-ItemProperty $path -Name DisableUserPresenceQos -ErrorAction SilentlyContinue
    }
    else
    {
        Set-ItemProperty $path -Name DisableUserPresenceQos -Type DWord -Value $old
    }
}
```

### Affinity and priority

Enumerate processor groups, physical cores, NUMA nodes, and CPU-set efficiency
classes using
`GetSystemCpuSetInformation`/`GetLogicalProcessorInformationEx` in the
orchestration harness. On hybrid Intel CPUs, select cores from one documented
efficiency class and do not mix P-cores and E-cores in a diagnostic mask.

For single-flight diagnostic runs, an example on a machine with at least twelve
logical processors is:

```powershell
$driver = Start-Process dotnet -ArgumentList $driverArgs -PassThru
$target = Start-Process dotnet -ArgumentList $targetArgs -PassThru

# Example only: four cores per silo and four cores left for Windows/collectors.
$driver.ProcessorAffinity = [IntPtr]0x00F
$target.ProcessorAffinity = [IntPtr]0x0F0
$driver.PriorityClass = 'High'
$target.PriorityClass = 'High'
```

Swap the two masks in half of the balanced `B,O,O,B,B,O,O,B` runs so core
quality cannot favor one implementation. Use the same masks for baseline and
candidate. Do not use `Realtime` priority; it can starve kernel and desktop
work. Do not pin individual .NET ThreadPool threads in the first implementation:
process affinity is reversible and preserves ThreadPool scheduling semantics.

For saturated throughput, four-core affinity changes the capacity being
measured. Run the guard both with all benchmark cores available and, if useful,
with a fixed diagnostic mask. Only the all-core representative run supports a
general throughput claim.

### Thermal and run-order protocol

- Connect AC power and use the same charger, power policy, display state, and
  cooling configuration for all runs.
- Wait at least five minutes after boot, resume, or a power-policy change.
- Run the exact workload until two consecutive warmups differ by less than 3%.
- Record available `\Thermal Zone Information(*)\Temperature` and
  `\Processor Information(_Total)\% Processor Performance` counters. These are
  indicators, not proof of per-core frequency.
- Use balanced alternating order and swap affinity masks across pairs.
- Stop and cool down if throughput trends monotonically with run order.
- Preserve raw per-iteration results; do not compare a cold baseline with a
  warm candidate.

### Background activity

The safe default is to close interactive applications, pause sync clients using
their supported UI, prevent sleep/display power transitions, and wait for
Windows Update or antivirus scans to finish. Record all-process CPU in the ETW
trace so residual interference is visible.

Do not routinely stop Defender, disable the firewall, disable network adapters,
or add directory exclusions. Those actions change the production environment,
reduce security, and can make a remote machine inaccessible. If a clean system
is essential, use Microsoft's documented clean-boot procedure in an isolated
benchmark session and label those results separately. Any narrowly scoped
Defender process exclusion requires explicit approval, must be removed in
`finally`, and cannot be compared directly with normal-security results.

Loopback traffic does not require disabling the physical NIC. Windows Filtering
Platform and security filter drivers can still affect loopback, so record them
as part of the environment and investigate only if causal timing places the
delay in the wire/receive phase.

### Collector and counter hygiene

- Run PerfView/WPR elevated for kernel context switches and PMCs.
- Enumerate counters and intervals on the current CPU with
  `PerfView listCpuCounters`, `wpr -pmcsources`, or `xperf -pmcsources`.
- Check `wpr -pmcsessions` before capture; PMU resources can be session-exclusive.
- Collect one small related counter set at a time and require zero lost events.
- Measure EventSource-only, CPU, thread-time, allocation, and PMC captures
  separately. `/ThreadTime` and PMCs perturb timing.
- Run an untraced control beside every traced workload and quantify collector
  overhead.
- Record QPC frequency with `[System.Diagnostics.Stopwatch]::Frequency`.
- Do not call `timeBeginPeriod`; it does not improve QPC.

Pre-run checks:

```powershell
powercfg /getactivescheme
[System.Diagnostics.Stopwatch]::Frequency

Get-Counter '\Process(*)\% Processor Time' |
  Select-Object -ExpandProperty CounterSamples |
  Where-Object CookedValue -gt 2 |
  Sort-Object CookedValue -Descending |
  Select-Object -First 10

Get-Counter '\Processor Information(_Total)\% Processor Performance' `
  -SampleInterval 1 -MaxSamples 10

C:\tools\PerfView.exe /AcceptEula /NoGui listCpuCounters
wpr -pmcsessions
```

Counter names are localized and not uniformly available. Record an
unsupported counter as a limitation rather than replacing it silently.

### Claims and session record

Every result directory should include:

- commit, runtime, OS build, OEM/model, CPU SKU, firmware, AC/DC state;
- original and benchmark power-scheme GUIDs plus queried processor settings;
- process affinity, priority, role, PID, processor group, and NUMA node;
- sample rate, provider configuration, trace loss, and collector mode;
- warmup history, run order, temperature/performance-counter samples;
- background processes using more than 2% CPU; and
- whether the run used normal startup, clean boot, or any security exclusion.

Do not claim that frequency was fixed unless firmware settings and observed
frequency data support it. Do not claim that noise was eliminated or that PMCs
improved unless normalized counter rates were actually calculated. The correct
wording is that OS noise was minimized under a documented policy and remaining
variation was measured.

Relevant Microsoft references:

- [Quality of service and user-presence QoS](https://learn.microsoft.com/windows/win32/procthread/quality-of-service)
- [Processor power-management options](https://learn.microsoft.com/windows-hardware/customize/power-settings/configure-processor-power-management-options)
- [powercfg command-line options](https://learn.microsoft.com/windows-hardware/design/device-experiences/powercfg-command-line-options)
- [QueryPerformanceCounter guidance](https://learn.microsoft.com/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)
- [timeBeginPeriod behavior](https://learn.microsoft.com/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)
- [PMU event recording](https://devblogs.microsoft.com/performance-diagnostics/recording-hardware-performance-pmu-events-with-complete-examples/)
- [Realtime-priority risk](https://devblogs.microsoft.com/oldnewthing/20100610-00/?p=13753)
- [Defender exclusion risks](https://learn.microsoft.com/defender-endpoint/navigate-defender-endpoint-antivirus-exclusions)
- [Clean boot](https://support.microsoft.com/topic/how-to-perform-a-clean-boot-in-windows-83a7dd5a-8b6f-3ebb-97ee-aaece9419c21)

## Capture workflow

Build first and launch binaries directly. For a one-process EventPipe trace:

```powershell
$pv = "C:\dev\pvanalyze\bin\Release\net10.0\pvanalyze.dll"
$env:ORLEANS_RPC_TRACE_SAMPLE_RATE = "64"
$provider = "Microsoft-Orleans-RpcLatency:0x1:5"

dotnet $pv collect --profile none `
  --providers $provider --duration-seconds 45 `
  --output Artifacts\Benchmarks\Rpc\phases\single-flight.nettrace -- `
  dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  FixedPing silo-to-silo 1 5 30 1

dotnet $pv info Artifacts\Benchmarks\Rpc\phases\single-flight.nettrace
dotnet $pv phases Artifacts\Benchmarks\Rpc\phases\single-flight.nettrace `
  --measurement-window
```

For split processes and thread-time, use elevated PerfView ETW collection so
both silos, kernel context switches, and the provider share one clock:

```powershell
C:\tools\PerfView.exe /AcceptEULA /NoGui /ThreadTime `
  /Providers:*Microsoft-Orleans-RpcLatency `
  /DataFile:Artifacts\Benchmarks\Rpc\phases\split.etl `
  collect

dotnet $pv info Artifacts\Benchmarks\Rpc\phases\split.etl
dotnet $pv phases Artifacts\Benchmarks\Rpc\phases\split.etl `
  --measurement-window --format json `
  --output Artifacts\Benchmarks\Rpc\phases\split-phases.json
```

The proposed causal benchmark harness owns the shared control block and launches
the split processes. Capture both providers in one ETW clock domain:

```powershell
$env:ORLEANS_RPC_TRACE_SAMPLE_RATE = "4096"
$env:ORLEANS_CAUSAL_PROFILE = "1"

C:\tools\PerfView.exe /AcceptEULA /NoGui `
  /Providers:*Microsoft-Orleans-RpcLatency,*Microsoft-Orleans-CausalProfiling `
  /DataFile:Artifacts\Benchmarks\Rpc\causal\experiments.etl `
  run dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  CausalFixedPing --concurrency 225 --duration 300

dotnet $pv info Artifacts\Benchmarks\Rpc\causal\experiments.etl
dotnet $pv causal Artifacts\Benchmarks\Rpc\causal\experiments.etl `
  --progress callback-complete --format json `
  --output Artifacts\Benchmarks\Rpc\causal\profile.json
```

The exact command and provider syntax must be validated during implementation;
the current pvanalyze provider parser does not yet preserve provider arguments.

Collect CPU, thread-time, allocation, and PMC traces separately. Enabling all
providers in one capture changes the timing being measured.

## Validation and acceptance

Before using phase data for optimization:

1. Verify that disabled instrumentation does not move latency, throughput, or
   allocation.
2. Verify collector argument delivery and fail a test when two processes
   report different effective sample rates.
3. Assert allocation-free event emission after warmup.
4. Compare enabled-zero-sample and each sample rate against the unchanged
   control.
5. Require zero lost events and report phase completeness.
6. Confirm the sum of median derived phases is consistent with median runtime
   end-to-end duration; percentiles must not be summed across independent
   distributions.
7. Compare runtime end-to-end timing with the driver's latency histogram.
8. Confirm request/response correlation across separate PIDs.
9. Deliberately inject a delay at one phase and verify that only the expected
   derived duration moves.
10. Run local delivery, one-hop, forwarded, retried, rejected, and truncated
    samples through the analyzer.
11. Run both concurrency one and saturation guards.
12. Validate a causal curve against a synthetic phase with a known real
    optimization before trusting predictions on production phases.
13. Require virtual-speedup output to pass participant coverage, pause
    overshoot, worker-count, and cross-process synchronization checks.

Retain an optimization only when the targeted phase moves by more than the
paired control variation, end-to-end latency improves, and saturated throughput
does not regress materially.

## Implementation order

1. Add the EventSource, phase enum, deterministic sampler, and unit tests.
2. Instrument the one-process benchmark path and add measurement markers.
3. Implement `pvanalyze phases` with correlation, diagnostics, and JSON output.
4. Validate sampling overhead and injected observational delays.
5. Add the split-process benchmark, shared QPC orchestration, and quiescence
   harness.
6. Establish the quiesced observational baseline.
7. Implement the shared virtual-time control block and one synchronous
   connection participant.
8. Implement one eligible phase (request serialization) and the
   `CallbackComplete` throughput progress point.
9. Implement `pvanalyze causal` and validate its prediction against a synthetic
   known optimization.
10. Add activation-scheduler participants and only then expand the eligible
    virtual-speedup phase set.
11. Add separately labeled slowdown-sensitivity experiments for waiting phases.
12. Resume runtime optimization using phases whose causal curves clear the
    confidence and validity thresholds.
