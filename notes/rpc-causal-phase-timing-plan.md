# RPC causal chain timing plan

## Purpose

The next performance pass should measure where wall-clock time is spent in an
individual cross-silo call instead of inferring latency from sampled CPU stacks.
At one outstanding call, most elapsed time is off-CPU and small changes are
easily hidden by CPU frequency, thermal state, ThreadPool wake-up latency, and
background activity. CPU and hardware-counter profiles remain useful at
saturation, but they cannot identify which asynchronous handoff delayed a
single call.

This plan adds profiling-build support for both exact and sampled
request/response traces, extends pvanalyze to reconstruct calls and aggregate
phase/queue durations, separates the two silos into independently controllable
processes, and defines a repeatable host-quiescence procedure. Exact mode must
be able to follow one selected call through a saturated system while all other
calls continue generating normal load.

The instrumentation must:

- be absent from normal binaries behind `ORLEANS_PROFILING`;
- support an exact selected trace as well as deterministic population sampling;
- preserve the message wire format;
- make the same sampling decision independently on both silos;
- correlate request and response events across processes;
- avoid allocating an `Activity`, string, or state object per call;
- report incomplete and ambiguous samples instead of silently repairing them;
- distinguish exact wall-clock phase timing from sampled CPU attribution; and
- include an unchanged control which quantifies instrumentation overhead.

## Hybrid activity and point-event design

Orleans already has `DiagnosticListener` message events in
`MessagingEvents`, but those calls are compiled out unless `MESSAGING_TRACE`
is defined. The existing EventSources in `EventSourceEvents.cs` emit events
without message identity, so they cannot reconstruct a call.

Do not create an `Activity` for every sampled call. Use the existing W3C
activity propagation only for exact trace selection: the benchmark creates one
probe root activity, the existing outgoing/incoming activity filters propagate
that trace through request context, and profiling code recognizes its trace ID.
All detailed phase and queue boundaries remain fixed-schema EventSource point
events. This avoids creating activity objects for background load while still
reusing Orleans' established cross-silo diagnostic context.

Population sampling continues to use the existing 64-bit `Message.Id`.
Requests and their responses already carry the same correlation ID over the
wire.

Use an explicit key consisting of:

```text
(origin silo identity, correlation ID)
```

For a request, the origin is `SendingSilo`; for a response, it is
`TargetSilo`. Include the local silo identity and process role in every event
so one-process and split-process traces can both be analyzed. A compact port
and generation pair is preferable to an allocated address string. pvanalyze
must warn if the selected identity is not unique in the trace.

For exact mode, use `(activity trace ID, origin silo, correlation ID)` as the
key. For sampled mode, use `(origin silo, correlation ID)`. Existing activity IDs can attribute CPU/thread-time only for phases which
execute inside the client/server activity context. Transport, inbound dispatch,
and continuation phases run outside that ambient activity; correlate their
events and CPU intervals using the explicit trace-ID/correlation payload
instead of claiming full-chain ActivityId coverage.

## Trace selection modes

### Exact activity-selected trace

The benchmark should create a W3C root `Activity` for one probe call while the
normal fixed-concurrency workers continue running. Install a benchmark
`ActivityListener` which samples Orleans client/server activities only when
their parent trace ID matches that probe. Normal background calls therefore do
not allocate Orleans activities, but the opt-in filters still execute
`StartActivity`/propagator checks. The profiling-disabled control measures that
cost.

The profiling benchmark must call `AddActivityPropagation` on both silo
builders; this facility is opt-in today. Install the same selective listener in
the driver and target processes before either host starts.

The existing outgoing filter already injects traceparent when the selective
listener creates the probe child activity, so no filter behavior change should
be required initially. The incoming filter extracts the W3C parent and creates
a server activity when the target listener requests it.

At message creation, copy the selected `ActivityTraceId` into profiling-only,
non-serialized `Message` fields. Immediately after remote `TryRead`, inspect
the deserialized `Message.RequestContextData` dictionary for traceparent and the
exact marker, then cache the parsed ID in those fields before emitting
`FrameDecoded` or queue markers. Ambient `RequestContext` is not imported until
later invocation dispatch and is too late for transport phases. Perform this
lookup only in a profiling build while the provider is enabled, and quantify
its per-frame overhead. Copy the cached ID from request to response in
`MessageFactory.CreateResponseMessage`. This does not change the wire format:
traceparent already travels through Orleans request context.

Set a profiling-only request-context marker such as
`orleans.profiling.exact=true` around the probe. Require both that marker and a
valid W3C trace ID before enabling exact events. This prevents unrelated
application activities from being traced. Remove/restore the marker in a
`finally` block after issuing the probe.

Represent the 128-bit trace ID as two primitive `ulong` values in EventSource
payloads. Exact mode emits every phase for the selected trace regardless of
sample rate. pvanalyze must report an error if more than one unrelated root
matches an exact selector.

The benchmark prints the selected trace ID before issuing the probe and records
the background concurrency, throughput, queue depths, and probe latency. Probe
calls must use the same connection, target grain population, and runtime path
as background calls; a dedicated idle connection would hide the load effect.

### Deterministic population sampling

Sampling must be derived from the correlation ID rather than local mutable
state. Every process then makes the same decision without adding a sampled bit
to the message:

```text
sample = (Mix64((ulong)correlationId) & (sampleRate - 1)) == 0
```

`sampleRate` must be a power of two. Use a stable integer mixer so sequential
IDs remain uniformly distributed. The EventSource should parse a
`SampleRate` provider argument in `OnEventCommand` and publish the resulting
mask through a volatile field. Reserve `0` to disable population sampling while
still allowing exact activity-selected traces.

Do not rely on that argument until the collectors can demonstrably deliver it.
The current pvanalyze `CollectCommand.CreateProviders` accepts only
`Name:Keywords:Level`; extend it to parse provider arguments and pass an
argument dictionary to `EventPipeProvider`. For PerfView, validate the exact
EventSource provider-argument syntax with a capture test. Until both paths are
verified, set the same `ORLEANS_RPC_TRACE_SAMPLE_RATE` environment variable in
the driver and target before process startup and treat provider arguments only
as an override.

Publish a safe sample-none mask before configuration is complete. Include the
effective sample rate in every phase event. pvanalyze must reject a phase
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
#if ORLEANS_PROFILING
if (RpcCallEventSource.Log.IsEnabled() && RpcCallEventSource.Log.IsSampled(message.Id))
{
    RpcCallEventSource.Log.Phase(message, RpcCallPhase.RequestTransportQueued);
}
#endif
```

`IsSampled` and the disabled check should inline. The event payload should use
primitive values only. Do not store a trace flag on `Message`: that increases
the hot object size even when tracing is disabled and creates reset/ownership
requirements for pooled messages.

Exact selection is checked before deterministic sampling:

```csharp
#if ORLEANS_PROFILING
if (RpcCallEventSource.Log.ShouldTrace(message))
{
    RpcCallEventSource.Log.Phase(message, RpcCallPhase.RequestTransportQueued);
}
#endif
```

`ShouldTrace` returns true for the configured exact activity trace ID or the
correlation-ID sample predicate.

## Compile-time isolation

Use a dedicated symbol such as `ORLEANS_PROFILING`, not the standard `TRACE`
symbol which normal .NET builds commonly define. Add an opt-in MSBuild property
which applies the symbol consistently to Orleans source projects, generated
code, and benchmarks:

```xml
<PropertyGroup Condition="'$(OrleansProfiling)' == 'true'">
  <DefineConstants>$(DefineConstants);ORLEANS_PROFILING</DefineConstants>
</PropertyGroup>
```

Wrap all of the following in `#if ORLEANS_PROFILING`:

- the profiling EventSource and phase/resource enums;
- profiling-only `Message` trace-ID fields and reset/copy logic;
- trace context on response completion sources;
- continuation wrappers;
- timestamp reads, depth reads, and phase calls;
- exact-probe listener, request-context extraction, and message propagation; and
- benchmark commands which require tracing.

Normal builds must contain no profiling field, branch, timestamp read, queue
depth read, or EventSource call. A `[Conditional]` helper is useful for leaf
calls, but it is not sufficient for added object fields or argument
preparation; use preprocessor guards at those sites.

Produce separate output directories:

```powershell
dotnet build test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 `
  -p:OrleansProfiling=false --artifacts-path Artifacts\Build\normal

dotnet build test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 `
  -p:OrleansProfiling=true --artifacts-path Artifacts\Build\profiling
```

Use three controls:

1. normal binary;
2. profiling binary with provider disabled and no exact selector; and
3. profiling binary tracing one exact probe under load.

The normal binary is authoritative for final latency/throughput comparisons.
The second comparison measures compile-time profiling scaffolding cost; the
third measures active exact-trace cost. Do not compare an optimized normal
binary only against an instrumented baseline.

The existing `MESSAGING_TRACE` conditional DiagnosticListener support can be
enabled by the profiling build if useful, but it is not a replacement for the
allocation-free correlated EventSource schema.

## Event schema

Add the allocation-free primitive EventSource named
`Microsoft-Orleans-RpcLatency` to
`src\Orleans.Serialization.Abstractions\Diagnostics\RpcCallEventSource.cs`.
Both `Orleans.Serialization` completion sources and Orleans.Core need to emit
through the same provider, and Orleans.Serialization cannot reference
Orleans.Core. Put `RpcCallPhase`, resource enums, and a primitive `WritePhase`
API beside the provider under `#if ORLEANS_PROFILING`. If cross-assembly access
is required, add profiling-conditional `InternalsVisibleTo` entries for
Orleans.Serialization and Orleans.Core instead of expanding the public API.

Add `src\Orleans.Core\Diagnostics\RpcCallTrace.cs` as the facade which converts
`Message`, silo, connection, retry, forwarding, queue, and batch state into the
primitive emitter arguments. This keeps `Message` dependencies out of the
serialization layer and guarantees one EventSource instance per process.

Use one stable `Phase` event instead of one event ID per phase. Its payload
should contain:

| Field | Type | Purpose |
| --- | --- | --- |
| `traceIdHigh` / `traceIdLow` | `ulong` | Exact W3C probe identity, or zero for correlation-only samples |
| `correlationId` | `long` | Existing `Message.Id` |
| `originSiloPort` | `int` | Stable origin component |
| `originSiloGeneration` | `int` | Disambiguates restarts |
| `localSiloPort` | `int` | Identifies the emitting silo |
| `localSiloGeneration` | `int` | Disambiguates restarts |
| `direction` | `byte` | Request, response, or one-way |
| `phase` | `byte` | Stable `RpcCallPhase` value |
| `selectionMode` | `byte` | Exact activity trace or deterministic sample |
| `resourceKind` | `byte` | Connection send, inbound dispatch, activation, continuation, or other queue |
| `resourceId` | `long` | Stable per-process identity for the specific queue/resource |
| `queueDepth` | `int` | Queue depth observed at this boundary, or `-1` if unavailable |
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
| `DispatchBuffered` | `MessageHandler.TryAdd` after adding the message | Message entered an inbound batch |
| `DispatchQueued` | when the populated `MessageHandler` is queued | Decoded batch is waiting for ThreadPool dispatch |
| `DispatchBatchStart` | `MessageHandler.Execute` entry | ThreadPool began executing the inbound batch |
| `DispatchStart` | `MessageHandler.Execute` immediately before each `OnReceivedMessage` | Per-message inbound handoff started |
| `RuntimeReceived` | `MessageCenter.ReceiveMessage` entry | Silo routing began |
| `ActivationQueued` | `ActivationData.ReceiveRequest` after `_waitingRequests.Add` | Request entered the activation queue |
| `InvocationStart` | `ActivationData.InvokeIncomingRequest` before `RuntimeClient.Invoke` | Grain invocation began |
| `InvocationStop` | `InsideRuntimeClient.Invoke` after the invokable completes | Grain result is available |
| `ResponseCreated` | `MessageCenter.SendResponse` after creating the response | Response entered the outbound runtime path |
| `CallbackStart` | `InsideRuntimeClient.ProcessResponseCallback` | Correlated callback was removed |
| `CompletionSignaled` | response completion source immediately before `SetResult` | Continuation became runnable |
| `ContinuationStart` | wrapped `IValueTaskSource.OnCompleted` continuation | Await continuation began executing |
| `CallbackComplete` | `CallbackData.DoCallback` after `ResponseCallback` | Runtime callback processing completed |

Transport phases apply to both request and response directions. For a batch,
emit `FlushStart` and `FlushStop` for every sampled message in `inflight`; all
messages in the batch legitimately share those timestamps. Include batch size
and index. Do the same when a `MessageHandler` is queued: all messages share
`DispatchQueued` and `DispatchBatchStart`, while each gets its own
`DispatchStart`. This separates ThreadPool queue wait from head-of-line time
behind earlier messages in the same inbound batch.

Add profiling-only accessors for the private `MessageHandler` count/messages
needed to emit batch markers. Widen `Message.RetryCount` (`short`) and packed
`ForwardCount` (`byte`) to the event payload's `int` fields without changing
their runtime storage or accepted ranges.

`FrameDecoded` alone combines socket arrival and deserialization. To separate
them without parsing the correlation ID twice, capture a timestamp before
`TryRead` only while the provider is enabled. After decoding reveals the ID,
emit `FrameDecoded` for sampled messages with decode duration in
`durationTicks`. This adds one timestamp read per frame only during a trace.
The analyzer can subtract decode time from the cross-process
`FlushStop -> FrameDecoded` interval. Measure the enabled-but-zero-sample
control to quantify that cost.

To measure the final ThreadPool continuation queue, extend
`IResponseCompletionSource` with an optional primitive trace context which
`InsideRuntimeClient.SendRequest` sets after creating the message. For sampled
calls, both `ResponseCompletionSource` and `ResponseCompletionSource<TResult>`
store the original continuation and state in the pooled completion source and
register one static wrapper with `ManualResetValueTaskSourceCore`. Emit
`CompletionSignaled` immediately before setting the result and
`ContinuationStart` in the wrapper before invoking the original continuation,
using the primitive EventSource in Orleans.Serialization.Abstractions. Clear all
trace and continuation fields in both `Reset` implementations. Unsampled calls
retain the existing direct registration path and allocate nothing.

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
| Request inbound batch formation | request `DispatchBuffered` | request `DispatchQueued` |
| Request ThreadPool queue | request `DispatchQueued` | request `DispatchBatchStart` |
| Request batch head-of-line | request `DispatchBatchStart` | request `DispatchStart` |
| Connection callback | request `DispatchStart` | request `RuntimeReceived` |
| Target routing/activation lookup | request `RuntimeReceived` | `ActivationQueued` |
| Activation queue | `ActivationQueued` | `InvocationStart` |
| Grain invocation | `InvocationStart` | `InvocationStop` |
| Response construction/routing | `InvocationStop` | response `TransportQueued` |
| Response connection queue | response `TransportQueued` | response `SerializeStart` |
| Response serialization | response `SerializeStart` | response `SerializeStop` |
| Response flush wait | response `SerializeStop` | response `FlushStop` |
| Response wire and receive | response `FlushStop` | response `FrameDecoded` minus decode duration |
| Response inbound batch formation | response `DispatchBuffered` | response `DispatchQueued` |
| Response ThreadPool queue | response `DispatchQueued` | response `DispatchBatchStart` |
| Response batch head-of-line | response `DispatchBatchStart` | response `DispatchStart` |
| Response connection callback | response `DispatchStart` | response `RuntimeReceived` |
| Response runtime routing | response `RuntimeReceived` | `CallbackStart` |
| Callback resolution | `CallbackStart` | `CompletionSignaled` |
| Caller continuation queue | `CompletionSignaled` | `ContinuationStart` |
| Runtime end-to-end | `RequestCreated` | `ContinuationStart` |

For same-machine ETW, timestamps share the system QPC and cross-process deltas
are valid. A single EventPipe trace is valid for the current one-process
benchmark. Independent EventPipe traces from split processes must not be merged
unless pvanalyze implements explicit clock synchronization.

## Queue residency and cost model

Queue wait must be a first-class result. A queue marker pair should identify
the logical queue, the specific queue instance, observed depth, batch position,
and the same call correlation key as the surrounding phases.

Instrument these queues:

| Queue/resource | Enqueue | Dequeue/start | Depth source |
| --- | --- | --- | --- |
| Request connection send channel | request `TransportQueued` | request `SerializeStart` | `outgoingMessages.Reader.Count` when supported |
| Response connection send channel | response `TransportQueued` | response `SerializeStart` | same |
| Pipe/socket backpressure | `FlushStart` | `FlushStop` | in-flight batch size and pipe flush state |
| Inbound batch formation | `DispatchBuffered` | `DispatchQueued` | `MessageHandler.Count` |
| Inbound ThreadPool work item | `DispatchQueued` | `DispatchBatchStart` | global `ThreadPool.PendingWorkItemCount` as an approximation |
| Inbound batch head-of-line | `DispatchBatchStart` | per-message `DispatchStart` | batch index and size |
| Activation waiting requests | `ActivationQueued` | `InvocationStart` | `_waitingRequests.Count` under the existing lock |
| Caller continuation | `CompletionSignaled` | `ContinuationStart` | global pending work count as an approximation |

The connection send channel and activation queue have exact per-resource
residency. ThreadPool depth is process-global, so label it as contextual rather
than the depth of the Orleans work item queue. Queue wait is still exact because
the sampled work item's enqueue and execution timestamps are correlated.

`FlushStart -> FlushStop` is backpressure observed by Orleans, not proof of time
inside the kernel socket send queue. The residual
`FlushStop -> FrameDecoded` combines transport buffering, loopback/TCP, socket
receive, and decode. If that residual dominates, add targeted ETW TCP/socket
events and a transport batch ID before calling it a kernel/network queue cost.

For every queue, pvanalyze should report:

- arrivals and completed waits;
- mean, p50, p90, p99, p99.9, and max wait;
- mean and maximum observed depth;
- wait grouped by enqueue depth;
- batch-size and batch-index distributions;
- head-of-line time by batch index;
- fraction of end-to-end latency attributable to the queue per sampled call;
- correlation between queue wait and end-to-end latency; and
- missing enqueue/dequeue and queue-instance collision counts.

Also calculate the queueing identity `L = lambda * W` using sampled arrival rate
and mean wait, then compare the inferred mean queue length with observed depth.
Large disagreement indicates biased sampling, missing events, or a depth value
whose scope was misunderstood.

Do not sum independently computed p99 queue times. For each sampled call,
calculate its own queue-time sum and compare that distribution with its
end-to-end duration. Report medians and tails from those per-call totals.

The benchmark matrix should include concurrency 1, the throughput optimum, and
several intermediate values. Queueing costs which disappear at concurrency 1
but grow sharply with load are capacity effects, while a stable queue delay at
concurrency 1 is a handoff/scheduler cost.

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

Add a `--trace-probes` option to the fixed driver. During the measurement
window it should choose randomized offsets, create a new W3C root activity,
print its trace ID, and issue one probe call through the same grain/connection
set used by background workers. Only one exact probe is active at a time. Start
with one probe per process run; later runs can trace multiple probes with
distinct IDs if each chain remains unambiguous.

The load generator continues reporting aggregate background throughput and
latency while the exact probe records its own observed latency. This allows the
timeline to explain whether a slow call waited in the connection channel,
ThreadPool dispatch, activation queue, continuation queue, or transport while
the system was busy.

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
- `--trace-id <w3c-id>` for one exact probe;
- `--correlation-id <id>` for a known sampled call;
- `--timeline` for an ordered single-call chain;
- `--from` and `--to`;
- `--measurement-window`;
- `--successful-only` (default true);
- `--include-incomplete`;
- `--min-completeness`;
- `--queues` to include queue-residency and depth analysis;
- `--queue <kind>` to restrict queue output;
- `--format text|json`; and
- optional `--with-cpu` to add sampled CPU attribution.

Implementation surfaces in `C:\dev\pvanalyze`:

- register the command in `Program.cs`;
- add `Commands\LatencyCommand.cs`;
- add phase and `QueueProfileEntry` DTOs plus JSON metadata in `Models.cs`;
- extend `TraceCapabilities.cs` to recognize the Orleans provider/schema;
- put reusable correlation logic in `TraceAnalyzer.cs`;
- add queue residency/depth aggregation in `QueueAnalyzer.cs`;
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

Text output should include a dedicated queue table:

```text
Queue                    Count   Mean    P50    P90    P99    Max   MeanDepth
request-connection       12031   1.8us  1.2us  2.9us  8.1us  41us  0.4
request-threadpool       12028   3.7us  2.8us  6.4us   15us  83us  contextual
request-activation       12022   0.9us  0.2us  1.1us   12us  95us  0.2
response-connection      12020   1.5us  1.0us  2.5us  6.9us  38us  0.3
caller-continuation      12018   2.6us  1.9us  4.8us   14us  71us  contextual
```

For a selected queue, add wait-by-depth and wait-by-batch-index tables plus
caller/callee-style links to the preceding and following phases.

Exact timeline output should resemble:

```text
Trace 4bf92f3577b34da6a3ce929d0e0e4736  Correlation 7A1D...

 +0.000us  driver  T18  RequestCreated          depth=-
 +1.420us  driver  T18  TransportQueued         send-queue=3
 +4.870us  driver  T09  SerializeStart          waited=3.450us
 +8.210us  driver  T09  FlushStop               batch=8 index=5
+18.640us  target  T14  FrameDecoded            decode=1.170us
+22.100us  target  T14  DispatchQueued          batch=6 index=2
+27.920us  target  T21  DispatchBatchStart      tp-wait=5.820us
+29.310us  target  T21  DispatchStart           head-of-line=1.390us
+34.880us  target  T07  ActivationQueued        depth=11
+46.260us  target  T07  InvocationStart         waited=11.380us
...
+91.400us  driver  T16  CompletionSignaled
+97.750us  driver  T23  ContinuationStart       tp-wait=6.350us
```

Include process, thread, processor, queue/resource identity, depth, batch
position, delta from the prior event, cumulative time, and the derived queue or
execution interval. When thread-time data is present, annotate context-switch
state during a queue interval without replacing the explicit elapsed duration.

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
- connection, ThreadPool, activation, and continuation queue residency;
- queue depth, batch size/index, and head-of-line aggregation;
- per-call queue-time totals and `L = lambda * W` consistency;
- retries and forwarding;
- missing, duplicate, and out-of-order events;
- window truncation with a start before `--from`;
- PID, role, and silo filters;
- percentile and deviation calculations;
- JSON/text schema; and
- ETW versus EventPipe event projection.

## Deferred active causal profiling

The exact and sampled causal chain is the immediate implementation target. Active
Coz-style virtual-speedup experiments are explicitly deferred until phase
timing is accurate, low-overhead, and able to identify a stable candidate.

The phase schema should remain extensible enough to support future randomized
experiments and end-to-end progress points, but the first implementation does
not need shared virtual time, pause injection, a causal experiment controller,
or a `pvanalyze causal` command. If passive timing later proves insufficient,
revisit [Coz](https://cacm.acm.org/research/coz/) as a separate design with
synthetic validation for .NET async and cross-process semantics.

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
   enough to identify phase boundaries, but it is not a production-representative
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
as part of the environment and investigate only if chain timing places the
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

Build first and launch binaries directly. For one exact probe under saturated
background load:

```powershell
$pv = "C:\dev\pvanalyze\bin\Release\net10.0\pvanalyze.dll"

dotnet build test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 `
  -p:OrleansProfiling=true --artifacts-path Artifacts\Build\profiling

$env:ORLEANS_RPC_TRACE_SAMPLE_RATE = "0"
$profilingDll = "<profiling artifacts path>\Benchmarks.dll"

C:\tools\PerfView.exe /AcceptEULA /NoGui /ThreadTime `
  /Providers:*Microsoft-Orleans-RpcLatency `
  /DataFile:Artifacts\Benchmarks\Rpc\phases\exact-under-load.etl `
  run dotnet $profilingDll FixedPing silo-to-silo 225 5 30 1 --trace-probes 1

dotnet $pv info Artifacts\Benchmarks\Rpc\phases\exact-under-load.etl
dotnet $pv phases Artifacts\Benchmarks\Rpc\phases\exact-under-load.etl `
  --trace-id <trace-id-printed-by-benchmark> --timeline --queues
```

For a one-process sampled EventPipe trace:

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

The exact command and provider syntax must be validated during implementation;
the current pvanalyze provider parser does not yet preserve provider arguments.

Collect CPU, thread-time, allocation, and PMC traces separately. Enabling all
providers in one capture changes the timing being measured.

## Validation and acceptance

Before using phase data for optimization:

1. Verify by reflection/IL inspection that the normal build contains no
   profiling EventSource, fields, continuation wrapper, or call sites.
2. Compare normal and profiling-provider-disabled binaries for latency,
   throughput, allocation, and generated code.
3. Trace one exact probe under saturation and verify that background throughput
   and latency remain within the profiling-disabled control's movement.
4. Verify collector argument delivery and fail a test when two processes
   report different effective sample rates.
5. Assert allocation-free event emission after warmup.
6. Compare enabled-zero-sample and each sample rate against the unchanged
   control.
7. Require zero lost events and report phase completeness.
8. Require every exact trace to contain one unambiguous request/response chain
   from `RequestCreated` through `ContinuationStart`.
9. Confirm per-call derived phase and queue durations reconcile with that
   call's runtime end-to-end duration; percentiles must not be summed across
   independent distributions.
10. Compare runtime end-to-end timing with the driver's probe latency.
11. Confirm activity trace and request/response correlation across separate PIDs.
12. Deliberately inject a delay at one phase and verify that only the expected
   derived duration moves.
13. Deliberately delay each queue consumer and verify that the corresponding
    queue residency moves by the injected amount.
14. Validate `L = lambda * W` against observed queue depth for synthetic queues.
15. Run local delivery, one-hop, forwarded, retried, rejected, and truncated
    samples through the analyzer.
16. Run concurrency one, intermediate loads, and the saturation guard.

Retain an optimization only when the targeted phase moves by more than the
paired control variation, end-to-end latency improves, and saturated throughput
does not regress materially.

## Implementation order

1. Add the `OrleansProfiling` MSBuild property and normal-build absence tests.
2. Add exact probe activity selection, profiling-only `Message` fields, and
   propagation/copy/reset tests.
3. Add the EventSource, phase/resource enums, deterministic sampler, and
   allocation tests.
4. Instrument connection send, inbound batch/ThreadPool, and activation queues
   plus the surrounding execution phases.
5. Carry profiling trace context into both response completion source types and
   instrument continuation signal/start without changing normal or unsampled
   paths.
6. Add one-process exact-probe, measurement-marker, and
   intermediate-concurrency benchmark cases.
7. Implement `pvanalyze phases --timeline --queues` with exact-trace filters,
   correlation, diagnostics,
   per-call reconciliation, and JSON output.
8. Validate compile-time/runtime overhead, queue depth, and injected
   observational delays.
9. Add the split-process benchmark, shared QPC orchestration, and quiescence
   harness.
10. Establish the quiesced observational baseline.
11. Resume runtime optimization using the largest stable phase/queue durations and
   validate each change with paired end-to-end measurements.
