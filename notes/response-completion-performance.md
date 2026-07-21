# Response completion mechanics

Date: 2026-07-21

## Hypothesis

`ResponseCompletionSource<T>` uses
`ManualResetValueTaskSourceCore<T>.RunContinuationsAsynchronously=true`.
This prevents `GetResult` from resetting and pooling the source before
`SetResult` has unwound, but it adds the measured caller-continuation queue.

The candidate allowed inline continuations and made pooling safe with a
two-flag ownership handshake:

- the completing thread marked the source as actively completing before
  calling the core;
- `GetResult` marked it consumed but did not reset it while completion was
  active; and
- whichever side finished last reset and returned the source to the pool.

Typed and untyped tests verified that a continuation could consume the result
inline, attempt another pool rental, and not receive the source whose
completion was still unwinding. Exception and traced completion paths used the
same handshake.

No wire-format or serialization behavior changed.

## Isolated benchmark

`ResponseCompletionBenchmarks` registers a continuation on the real pooled
source, completes it, consumes the result, and waits for reclamation. A second
case adds consumer CPU work.

| Consumer | Asynchronous baseline | Safe inline | Change |
| --- | ---: | ---: | ---: |
| Immediate | 1.263 us / 88 B | 51.44 ns / 0 B | 95.9% faster |
| 64 `SpinWait` iterations | 4.878 us / 120 B | 3.657 us / 0 B | 25.0% faster |

The ownership mechanism therefore removed the isolated ThreadPool hop and its
allocation as intended.

## End-to-end result

Balanced split-process runs used independent worktrees in
`B,O,O,B,B,O,O,B` order with a 5-second warmup and 10-second measurement:

| Workload | Throughput | Mean latency |
| --- | ---: | ---: |
| Single-flight silo call | **-1.51%** | **+1.59%** |
| Concurrency-225 silo calls | **-3.09%** | **+3.28%** |

The external-client and hosted-client screens did not expose a compensating
gain. Inline completion causes the response-dispatch thread to execute the
caller until its next asynchronous suspension, moving variable application
work onto connection dispatch.

## Phase evidence

Matched low-overhead EventPipe captures at concurrency 32 had zero lost events:

| Phase | Baseline | Safe inline |
| --- | ---: | ---: |
| Throughput under tracing | 207,744/s | 203,423/s |
| Response ThreadPool mean | 9.26 us | 8.59 us |
| Caller-continuation mean | 10.56 us | 4.38 us |
| Caller-continuation p99 | 29.10 us | 11.30 us |
| Runtime end-to-end mean | 277.29 us | 277.17 us |
| Runtime end-to-end p99 | 483.40 us | 640.40 us |

The caller queue shrank 58.5%, proving that the candidate changed the intended
boundary. End-to-end mean did not improve and tail latency worsened because
the work was transferred rather than removed.

## Conclusion

The safe lifecycle handshake is correct and highly effective in isolation,
but inline caller execution is the wrong scheduling policy. No runtime change
is retained. A future completion design must hand the continuation to an
Orleans-aware queue which preserves isolation from response dispatch, rather
than running it inline or using another generic ThreadPool work item.

The isolated benchmark is retained. Raw BenchmarkDotNet logs, paired runs, and
phase traces are under `Artifacts\Benchmarks\CompletionMechanics`.
