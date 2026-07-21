# Orleans-aware inbound scheduling

Date: 2026-07-20

## Environment

- Windows 11 Enterprise 10.0.26200
- Intel Core i7-11850H, 8 physical / 16 logical cores
- .NET SDK 10.0.302 and runtime 10.0.10, x64
- Baseline commit `ba4721a2ec`
- PerfView at `C:\tools\PerfView.exe`
- pvanalyze built from `C:\dev\pvanalyze`

## Finding

`Connection.ProcessIncoming` decodes up to eight messages into a pooled
`MessageHandler`, then queues that handler to the ThreadPool. It used
`preferLocal: true`, so a handler produced by a ThreadPool thread normally
entered that same worker's local queue while the worker continued reading and
decoding frames.

A lossless PerfView ThreadTime/RPC-phase trace at concurrency 32 measured the
following queue times:

| Queue | Mean | p99 |
| --- | ---: | ---: |
| Target request ThreadPool | 30.59 us | 94.30 us |
| Driver response ThreadPool | 29.15 us | 94.40 us |
| Target activation | 34.42 us | 94.20 us |
| Driver caller continuation | 38.45 us | 116.60 us |

The retained change queues the existing eight-message handlers globally using
`preferLocal: false`. It does not change batching, handler ownership, dispatch
logic, message ordering guarantees, serialization, or the wire format.

## Rejected per-connection coalescing queue

An isolated `InboundSchedulingBenchmarks` benchmark compared direct ThreadPool
dispatch with a per-connection cooperative queue which processed at most four
handlers per turn.

| Handler batches | Direct | Coalesced | Change |
| ---: | ---: | ---: | ---: |
| 1 | 706.6 ns | 954.0 ns | 35% slower |
| 4 | 1,399.6 ns | 1,007.2 ns | 28% faster |
| 16 | 3,457.7 ns | 2,118.1 ns | 39% faster |
| 64 | 14,182.5 ns | 7,416.1 ns | 48% faster |

Despite the isolated high-batch improvement, end-to-end concurrency 32
regressed 12.5% and concurrency 225 regressed 8.5%. Serializing handler batches
per connection added head-of-line delay. The runtime change and its state
machine were removed; the isolated benchmark remains to record the rejected
design.

The first benchmark run also exposed a lost-wakeup window in the experimental
queue. Matching the existing `IOQueue` memory barrier fixed 10,000 repeated
idle transitions before the candidate was measured and rejected.

## End-to-end results

Balanced runs used independent baseline and optimized worktrees in
`B,O,O,B,B,O,O,B` order. Each process used a 5-second warmup and one 10-second
measurement.

| Concurrency | Baseline mean | Global dispatch mean | Throughput | Mean latency |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 11,768/s | 11,688/s | -0.68% | +0.69% |
| 32 | 210,972/s | 214,639/s | **+1.74%** | **-1.91%** |
| 225 | 694,784/s | 714,878/s | **+2.89%** | **-2.82%** |

The unchanged hosted-client control moved -0.35% between the same baseline and
optimized binaries. The single-flight movement is therefore treated as noise;
the load-bearing comparisons exceed control movement and agree on direction.

## Queue evidence

A second lossless concurrency-32 ThreadTime/RPC trace used the same sampling
rate and measurement window:

| Queue | Baseline mean | Global mean | Change | Baseline p99 | Global p99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Request ThreadPool | 30.59 us | 24.75 us | **-19.1%** | 94.30 us | 54.20 us |
| Response ThreadPool | 29.15 us | 24.77 us | **-15.0%** | 94.40 us | 63.90 us |
| Request inbound batch | 8.58 us | 6.26 us | -27.1% | 34.40 us | 27.20 us |
| Response inbound batch | 5.53 us | 8.22 us | +48.7% | 17.50 us | 24.20 us |
| Request head of line | 16.24 us | 18.32 us | +12.8% | 58.20 us | 70.90 us |
| Response head of line | 16.87 us | 17.87 us | +6.0% | 45.20 us | 51.80 us |

Global placement reduces the intended ThreadPool residency at the cost of
modestly higher within-batch head-of-line time. The end-to-end result shows
that the scheduling reduction dominates under moderate and saturated load.

## JIT and hardware counters

With tiering and ReadyToRun disabled, the async `ProcessIncoming` state
machine grew from 2,604 to 2,613 bytes. Batching and dispatch methods were
otherwise unchanged.

PerfView collected cache/instruction and branch/misprediction pairs in
separate process-focused runs. Every retained trace reported zero lost events.
Estimates below normalize configured counter intervals by measured calls. They
include warmup/startup counter samples, so relative movement is more reliable
than the absolute per-call values.

| Metric | Change |
| --- | ---: |
| Retired instructions / measured call | -6.1% |
| Cache misses / measured call | -7.1% |
| Cache misses / instruction | -1.1% |
| Branches / measured call | -5.4% |
| Branch mispredictions / measured call | -12.0% |
| Mispredictions / branch | -6.9% |

Separate `TotalCycles` captures produced implausibly different process
attribution despite zero lost events and similar throughput. No cycle claim is
made from those traces.

## Validation and artifacts

- Eight client/gateway/connection event tests passed.
- Split-process request/response calls completed at concurrency 1, 32, and 225.
- Hosted-client calls remained unaffected.
- Raw logs, ETW traces, pvanalyze JSON, PMC summaries, and disassembly are under
  `Artifacts\Benchmarks\InboundScheduling`.

The result retains eight-message batching and the asynchronous connection-reader
boundary. The next direction should start from this change and target activation
wake-up coalescing without running grain turns inline.
