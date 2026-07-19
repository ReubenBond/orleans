# RPC callback-key performance

Date: 2026-07-18

## Environment

- Windows 11 10.0.26200.8875
- Intel Core i7-11850H, 8 cores and 16 logical processors
- .NET SDK 10.0.302
- .NET runtime 10.0.10, x64
- Baseline commit `273024419c`
- Optimized commit `c68bcbeac2`
- `pvanalyze` built from `C:\dev\pvanalyze`
- PerfView at `C:\tools\PerfView.exe`

## Workload and profile

`FixedPing` starts two silos in one process and removes the ping grain from the
first silo. Each of 225 workers keeps one allocation-free `ValueTask` call in
flight to a grain on the second silo. The local hosted-client mode is the
unchanged control.

```powershell
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  FixedPing silo-to-silo 225 5 30 1

PerfView /AcceptEula /NoGui /FocusProcess:<pid> /MaxCollectSec:15 `
  /NoNGenPdbs /KernelEvents:Process,Thread,ImageLoad,Profile `
  /ClrEvents:JITSymbols collect jitsymbol-cpu.etl.zip

pvanalyze info jitsymbol-cpu.etl.zip
pvanalyze cpustacks jitsymbol-cpu.etl.zip --pid <pid> `
  --from 1000 --to 14000 --top 120
```

The lossless, low-overhead PerfView trace ranked callback dictionary insertion
and removal among the remaining managed costs. `InsideRuntimeClient` used
`(GrainId, CorrelationId)` as its key, while `OutsideRuntimeClient` already used
only `CorrelationId`. Correlation IDs are unique within the `MessageFactory`
owned by an `InsideRuntimeClient`, so the grain ID was redundant local state.

The optimized implementation uses
`ConcurrentDictionary<CorrelationId, CallbackData>` for registration, response,
status, timeout, and cancellation paths. Message IDs, headers, serialization,
and all transmitted bytes are unchanged.

## Isolated results, disassembly, and counters

`CallbackKeyBenchmarks` performs 256 callback add/remove pairs. It uses
`RpcBenchmarkConfig`, including memory, disassembly, and elevated Windows
hardware counters.

| Metric | Compound key | Correlation ID | Change |
| --- | ---: | ---: | ---: |
| Mean | 17.64 us | 12.29 us | -30.3% |
| Retired instructions | 179,767 | 128,323 | -28.6% |
| Cycles | 91,638 | 61,368 | -33.0% |
| Cache misses | 282 | 171 | -39.4% |
| Branch instructions | 32,471 | 25,940 | -20.1% |
| Branch mispredictions | 48 | 33 | -31.3% |
| Allocation | 20 KB | 12 KB | -40.0% |
| Code size | 4,595 B | 2,512 B | -45.3% |

The compound-key assembly calls `ValueTuple<GrainId, CorrelationId>.GetHashCode`
and enters `IdSpan.Equals` for both the grain type and key. The correlation-only
assembly passes the 64-bit ID directly to the specialized concurrent-dictionary
paths. Those tuple hashing and `IdSpan` comparison frames disappear.

## End-to-end results

Balanced runs used separate worktrees and alternated baseline/optimized order.
All four adjacent comparisons favored the optimized build.

| Run set | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| 20-second pair 1 | 734,709/s | 736,221/s | +0.21% |
| 20-second pair 2 | 729,787/s | 734,197/s | +0.60% |
| 30-second pair 1 | 823,709/s | 832,497/s | +1.07% |
| 30-second pair 2 | 828,779/s | 830,219/s | +0.17% |
| 30-second mean | 826,244/s | 831,358/s | +0.62% |

Steady-state allocation fell from 984.8 B/call to 952.8 B/call cross-silo
(-32 B, -3.2%) and from 600 B/call to 568 B/call in the local control (-5.3%).
The reduction matches the smaller concurrent-dictionary node allocated for each
pending request.

## End-to-end hardware counters

PerfView collected four counters together over identical 15-second windows.
`pvanalyze info` reported zero lost events, and `pvanalyze pmcstats --pid`
aggregated the benchmark process. Per-call estimates use each run's 45-second
throughput, so they are approximate when throughput changes inside the run.

| Metric | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| Retired instructions/call | 40,005 | 33,145 | -17.1% |
| Cache misses/call | 43.02 | 37.10 | -13.8% |
| Branch instructions/call | 8,161 | 6,821 | -16.4% |
| Branch mispredictions/call | 64.29 | 58.63 | -8.8% |
| Cache misses/instruction | 0.1075% | 0.1119% | +4.1% |
| Mispredictions/branch | 0.7878% | 0.8595% | +9.1% |

The optimized run completed 2.8% more calls under the counter profiler while
recording fewer samples for every counter. Mispredictions per remaining branch
and misses per remaining instruction increased, but the larger reduction in
retired branches and instructions lowered both costs per completed call.

## Rejected experiments

- A contiguous frame-prefix fast path cut the isolated read from 7.99 ns to
  4.22 ns, 184 to 101 instructions, and 28 to 13 branches. Fragmented prefixes
  regressed about 28%, and balanced end-to-end runs did not improve, so the
  implementation was reverted.
- Initializing each inbound message handler's connection once removed nine
  branches per eight-message batch but changed cycles by less than measurement
  precision, so it was not applied.
- A direct-mapped grain-locator resolver cache cut the isolated lookup from
  2.58 ns to 1.01 ns. The absolute saving was too small, and bypassing the
  directory cache would duplicate its TTL and invalidation semantics, so no
  runtime cache was added.

Raw logs, ETW traces, pvanalyze output, benchmark JSON, and assembly reports are
under `Artifacts\Benchmarks\Rpc\two-silo-next`.
