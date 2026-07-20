# Hosted-client response performance

Date: 2026-07-20

## Environment

- Windows 11 Enterprise 10.0.26200
- Intel Core i7-11850H, 8 cores and 16 logical processors
- .NET SDK 10.0.302
- .NET runtime 10.0.10, x64
- Baseline commit `4351ed4ac3`
- Optimized commit `98a2743abf`
- `pvanalyze` built from `C:\dev\pvanalyze`
- PerfView at `C:\tools\PerfView.exe`

## Workload and finding

`FixedPing local` uses the silo-hosted client to call allocation-free `ValueTask`
ping grains in the same process. Each worker keeps one call in flight.

```powershell
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  FixedPing local 100 5 10 1
```

A lossless PerfView CPU/JIT trace attributed 5.4% inclusive CPU to
`MessageFactory.CreateResponseMessage`, including 3.0% in the object pool and
0.6% in response request-context export. A local response was renting and
initializing a complete `Message`, routing it through `MessageCenter` and
`HostedClient`, then unpacking it only to complete the callback.

The optimized path completes a response directly when the reply receiver is the
local hosted client. It retains response deep-copying, timeout and cancellation
races, callback accounting, destination-cache updates, exceptions, and RPC
phase tracing. Grain-to-grain, external-client, remote, rejection, and transport
paths are unchanged. No serialized type, header, body, or wire-format logic
changed.

## End-to-end results

Balanced runs used a detached baseline worktree and `B,O,O,B,B,O,O,B` ordering.
Each process used a 5-second warmup and one 10-second measurement.

| Pair | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| 1 | 5,668,420/s | 6,742,332/s | +18.9% |
| 2 | 6,101,717/s | 6,572,981/s | +7.7% |
| 3 | 5,532,166/s | 6,287,045/s | +13.6% |
| 4 | 5,432,838/s | 6,294,671/s | +15.9% |
| Mean | 5,683,785/s | 6,474,257/s | **+13.9%** |

All four adjacent pairs favored the optimized build. Uncontaminated allocation
intervals fell from 552 B/call to 376 B/call, removing **176 B/call (31.9%)**.
The removed response envelope accounts for that reduction.

## JIT disassembly

Disassembly used ReadyToRun and tiering disabled. Baseline
`CreateResponseMessage` is 371 bytes and contains virtual object-pool dispatch,
header-flag branches, GC write barriers, TTL conversion, and request-context
export. The optimized `CompleteHostedClientResponse` is 165 bytes and calls only
callback removal, destination-cache update, and callback completion on its hot
path.

The added local-response test in `InsideRuntimeClient.SendResponse` is a type
test, null/identity checks, and `SiloAddress.Matches`. It increases that method
from 373 to 449 bytes, but successful hosted-client calls bypass the 371-byte
response-construction routine and subsequent local routing.

## Hardware counters

PerfView collected cache/instruction and branch/misprediction pairs in separate
15-second process-focused traces. `pvanalyze info` reported zero lost events.
Per-call estimates use the trace duration, each run's measured throughput, and
the configured sampling intervals.

| Metric | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| Retired instructions/call | 7,406 | 6,660 | -10.1% |
| Cache misses/call | 7.17 | 5.00 | -30.2% |
| Cache misses/instruction | 0.0968% | 0.0751% | -22.4% |
| Retired branches/call | 1,689 | 1,551 | -8.2% |
| Branch mispredictions/call | 7.59 | 4.54 | -40.2% |
| Mispredictions/branch | 0.449% | 0.293% | -34.9% |

The optimized CPU trace no longer contains `CreateResponseMessage`,
`DefaultObjectPool<Message>.Get`, or response `RequestContextExtensions.Export`
among its top methods.

## Profiling validation

An `OrleansProfiling=true` build was captured with
`Microsoft-Orleans-RpcLatency` and analyzed using `pvanalyze phases`. The direct
path emits response-direction `ResponseCreated`, `RuntimeReceived`,
`CallbackStart`, and `CallbackComplete` events without allocating a response
message. Response runtime routing and callback phases were 100% complete.
Local request placement/routing phases remain absent as they were before this
change because receiver-cache delivery bypasses those boundaries.

Raw logs, ETW traces, EventPipe traces, pvanalyze JSON, and disassembly are under
`Artifacts\Benchmarks\HostedClientCallPath`.
