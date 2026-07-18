# End-to-end RPC performance

Date: 2026-07-18

## Environment

- Windows 11 10.0.26200.8875
- Snapdragon X 12-core X1E80100, ARM64
- .NET SDK 10.0.302
- .NET runtime 10.0.10
- Branch based on `perf/rpc-invokable-dispatch` at `11bb8083fc`

## Workload

`AdaptivePing_SiloToSilo_Profile` starts two silos in one process and removes the
ping grain from the first silo, forcing every allocation-free `ValueTask` ping
and response across the loopback silo connection. The fixed-concurrency mode
uses 225 workers, a 5-second warmup, and one measurement interval. All benchmark
and profiler launches used an external process-tree watchdog in addition to
tool-specific duration limits.

Representative command:

```powershell
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  AdaptivePing_SiloToSilo_Profile 30 225
```

Final results use eight alternating 30-second runs with balanced ordering. The
original side was an isolated worktree at benchmark commit `bc24ae5ad4`; the
optimized side was the complete branch.

| Version | Runs (requests/second) | Median |
|---|---|---:|
| Original | 697,413; 663,954; 674,338; 658,249 | 669,146/s |
| Optimized | 774,072; 805,923; 792,855; 828,996 | 799,389/s |

The optimized median is **19.5% higher**, and every adjacent original/optimized
pair favored the optimized build.

## Retained optimizations

### Batched inbound dispatch

The original connection reader queued one pooled thread-pool work item per
decoded frame. ETW attributed 6.5% exclusive CPU to
`Connection.ProcessIncoming`, in addition to thread-pool dispatch overhead.

The reader now groups up to eight frames from one pipe read into each pooled
work item. The first fixed-concurrency comparison improved the median from
694,748/s to 794,318/s (+14.3%). A later eight-run comparison including inbound
batching and larger transport writes measured +16.1%.

In the final ETW profile, `ProcessIncoming` uses 1.1% exclusive CPU. The target
cost therefore fell by 5.4 percentage points and is no longer the dominant
connection cost.

### Larger transport writes

The sender previously called `PipeWriter.FlushAsync` after at most four queued
messages. Increasing the high-load batch to sixteen improved the alternating
run medians from 802,578/s to 839,614/s (+4.6%). The sender still flushes
immediately when fewer messages are available, so low-load latency is
unchanged.

### Pooled outbound responses

Allocation traces identified `Orleans.Runtime.Message` as the largest managed
allocation source. Successfully serialized and flushed response messages now
return to a bounded pool after all mutable state is cleared. Requests, one-way
messages, serialization failures, retries, and interrupted flushes retain their
previous ownership.

The change won all four alternating pairs and improved the median by 3.8%.
Paired allocation profiles reduced total allocation from approximately 1,175
to 1,009 bytes per completed call (-14.1%); sampled `Message` allocations per
call fell by approximately 27%.

## ETW and hardware counters

PerfView collected ETW CPU samples and the following ARM64 PMCs:

- branch instructions
- branch mispredictions
- data-cache misses
- instructions retired
- total cycles

Counter intervals were raised above their defaults to keep collection overhead
bounded. Both the original and final traces contain zero lost events. The final
measurement-window CPU stack contains 167,848 samples versus 182,890 in the
original trace, while the inbound-loop exclusive share fell from 6.5% to 1.1%.
PMC samples were retained for inspection but not converted into per-call rates.

`pvanalyze` was extended locally to convert ETL to ETLX, filter CPU stacks and
call trees by process/event type, aggregate duplicate method nodes, and safely
apply open-ended time filters. This avoids the parked-thread sampling issue
observed when collecting CPU stacks with EventPipe on Windows ARM64.

## Rejected experiments

| Experiment | Result |
|---|---|
| Last-reference silo-address encoding cache | -4.5% median in alternating long runs |
| Last body-codec cache | +1.0% median with pair swings from about -13% to +11%; noise |
| Increase transport batch from 16 to 32 | +1.7% median with one pair regressing 12.9%; not stable |
| Increase inbound work-item batch from 8 to 16 | -3.4% median |

## Artifacts

- Final alternating logs:
  `Artifacts\Benchmarks\Rpc\e2e\complete-paired-*.stdout.log`
- Original hardware-counter trace:
  `Artifacts\Benchmarks\Rpc\e2e\baseline-pmc-low.etl`
- Final hardware-counter trace:
  `Artifacts\Benchmarks\Rpc\e2e\complete-pmc.etl`
- Original/final CPU summaries:
  `Artifacts\Benchmarks\Rpc\e2e\baseline-pmc-low-cpu-aggregated.json` and
  `Artifacts\Benchmarks\Rpc\e2e\complete-pmc-cpu.json`
- Pre-pool/final allocation traces:
  `Artifacts\Benchmarks\Rpc\e2e\final-gc.nettrace` and
  `Artifacts\Benchmarks\Rpc\e2e\pooled-gc.nettrace`

## Validation

- Pooled-message state reset coverage in `MessageSerializerTests`
- Basic request/response and one-way grain calls
- Client connection and transport lifecycle tests
- Release benchmark build on .NET 10
- Complete `Orleans.slnx` build

The remaining dominant managed costs are message serialization/deserialization,
callback bookkeeping, and request/message allocation. They require broader
protocol or ownership changes than the retained connection-local optimizations.

## Follow-up from the complete branch

A follow-up on `perf/optimize-end-to-end-silo-calls` measured the complete branch,
including reference-table reset and response-envelope pooling, at **880,101
calls/s** with 3.72% run standard deviation and 961.7 B/call. The unchanged
hosted-client control measured 4,025,150 calls/s with 4.64% standard deviation.

PerfView collected a 44.38-second ETW trace with 166,971 CPU samples, 532,658
PMC samples, and zero lost events. The measurement window attributed the largest
Orleans-exclusive costs to:

- `MessageSerializer.Write`: 3,392 ms
- `MessageSerializer.TryRead`: 2,446 ms
- `MessageCenter.SendMessage`: 1,439 ms
- `CachingIdSpanCodec.ReadRaw`: 1,269 ms
- `CachingSiloAddressCodec.WriteRaw`: 1,224 ms
- `CachingSiloAddressCodec.ReadRaw`: 1,058 ms
- callback dictionary removal/addition: 864 ms / 397 ms

The PMC capture includes branch instructions, branch mispredictions, data-cache
misses, retired instructions, and cycles. The events are retained but are not
converted into normalized per-call rates, so no counter-based improvement is
claimed.

Two follow-up experiments were rejected:

| Experiment | Result |
|---|---|
| Replace the ID-span reader dictionary and time-based maintenance with a bounded direct-mapped cache | 878,677 calls/s, versus 880,101/s baseline; noise |
| Key inside-silo callbacks only by correlation ID, removing the redundant grain ID from dictionary nodes | 872,360 calls/s; median moved from 862k/s to 854k/s |

Both experiments and their tests were removed. The trace and summaries are under
`Artifacts\Benchmarks\Rpc\next`. The pvanalyze fork gained native EventPipe
collection in [ReubenBond/pvanalyze#4](https://github.com/ReubenBond/pvanalyze/pull/4);
PerfView remains the collector used for Windows ETW CPU and hardware-counter data.
