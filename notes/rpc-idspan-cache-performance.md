# RPC IdSpan cache performance

Date: 2026-07-18

## Environment

- Windows 11 10.0.26200.8875
- Intel Core i7-11850H, 8 cores and 16 logical processors
- .NET SDK 10.0.302
- .NET runtime 10.0.10, x64
- Baseline commit `8a6c3136de`
- `pvanalyze` from `ReubenBond/pvanalyze` at `f323aed`

## Workload

`AdaptivePing_SiloToSilo_Profile` runs two silos in one process and forces
allocation-free `ValueTask` calls over the loopback silo connection. Runs use
225 workers, a 5-second warmup, and a 30-second measurement. Baseline and
optimized binaries were built in separate worktrees and launched in balanced
alternating order.

```powershell
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  AdaptivePing_SiloToSilo_Profile 30 225
```

## Finding

ETW CPU stacks ranked `CachingIdSpanCodec.ReadRaw` among the remaining managed
message-serialization costs. Each message repeatedly decodes the same grain and
interface type identifiers, but every decode entered a dictionary. Every encode
also entered the process-wide concurrent LRU even when the same `IdSpan` array
had just been written.

The codec now uses bounded eight-entry direct-mapped caches:

- reads compare the hash and payload before falling back to the existing
  dictionary and shared interning cache;
- writes compare the hash and source array before entering the shared cache;
- cache maintenance refreshes one in every 1,024 read hits;
- the private fallback dictionary is capped at 1,024 entries; and
- collisions retain the previous dictionary/shared-cache behavior.

The caches are local implementation details. Header and body bytes, field order,
hashes, lengths, and all other wire-format behavior are unchanged.

## JIT disassembly and isolated results

`IdSpanCodecBenchmarks` measures repeated read and write operations with
`RpcBenchmarkConfig`, including memory and disassembly diagnostics.

| Operation | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| Hot read | 10.33 ns | 8.37 ns | -19.0% |
| Hot write | 15.94 ns | 9.08 ns | -43.0% |
| Allocation | 0 B | 0 B | unchanged |

The baseline read assembly enters the inlined dictionary bucket/probe path.
The optimized hit validates the direct-mapped entry and returns before
`CollectionsMarshal.GetValueRefOrAddDefault`. The JIT originally retained an
array range-check helper despite the masked 0-7 index; using the private
fixed-size-array invariant removes that range check from the hit path. The
write hit similarly returns before `ConcurrentLruCache.GetOrAdd`.

## End-to-end results

The final implementation won all four adjacent baseline/optimized comparisons.
Paired improvements were +13.6%, +1.6%, +0.5%, and +14.9%; the median paired
improvement was **+7.6%**. The machine exhibited two throughput regimes, so the
range is more informative than the overall medians.

A separate five-iteration run measured 748,741 calls/s. Its earlier baseline
was 757,968 calls/s while the unchanged local control moved from 4,638,813 to
4,618,825 calls/s. That single-run movement does not establish a gain; the
retained end-to-end evidence is the direction of the balanced paired runs.
Uncontaminated allocation intervals remained 984.9 B/call cross-silo and
600.0 B/call locally.

## Hardware counters

PerfView collected paired, process-focused ETW traces with identical 15-second
windows and counter intervals. `pvanalyze info` reported zero lost events, and
`pvanalyze pmcstats --pid` aggregated profile sources.

| Metric | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| Cache misses / retired instruction | 0.0999% | 0.0929% | -7.1% |
| Estimated cache misses / call | 34.28 | 30.74 | -10.3% |
| Retired branches / call | 7,838 | 6,789 | -13.4% |
| Estimated branch mispredictions / call | 62.13 | 57.70 | -7.1% |
| Branch mispredictions / retired branch | 0.793% | 0.850% | +7.2% |

The branch-rate denominator matters: the optimized workload retires 13.4%
fewer branches per completed call, so mispredictions per call fall even though
mispredictions per remaining branch rise. Per-call estimates use the configured
sampling intervals, the 15-second PMC window, and the workload's 30-second
steady-state throughput.

## Rejected experiments

| Experiment | Result |
| --- | --- |
| Pool deserialized response envelopes | Allocation fell 17%, but paired throughput fell 2.8% |
| Generated cancellation-token scan marker | +0.1% paired median; noise |
| Dispatch responses directly on the connection reader | Approximately -10%; batching provides useful parallelism |
| Read-only IdSpan hot cache | Improved the isolated read but did not produce stable end-to-end results until writes and maintenance were optimized |

## Artifacts

Raw logs, ETW traces, pvanalyze output, benchmark JSON, and assembly reports are
under `Artifacts\Benchmarks\Rpc\two-silo-optimization`.
