# Activation wake-up investigation

Date: 2026-07-21

## Scope and baseline

This investigation started from the retained global inbound-dispatch change in
`perf/orleans-aware-inbound-scheduling`. The existing activation path already
has two independent coalescing mechanisms:

- `SingleWaiterAutoResetEvent.Signal` returns immediately when a signal is
  already pending.
- `WorkItemGroup.EnqueueTask` schedules only on a `Waiting` to `Runnable`
  transition. Enqueues while `Runnable` or `Running` do not schedule another
  runner.

The remaining question was whether either forced asynchronous continuation
dispatch or worker-local placement added an avoidable scheduling transition.

`FixedPing` gained `--grain-count` and the split-process runner gained
`-GrainCount`. `--grain-count 1` sends every worker to one activation for a
fan-in/wake-up stress case; the default one-grain-per-worker behavior remains
the many-activation fairness control.

Raw logs and pvanalyze phase output are under
`Artifacts\Benchmarks\ActivationWakeup`.

## Rejected: synchronous activation signal continuation

`ActivationData` normally configures its work signal with
`RunContinuationsAsynchronously=true`. The candidate set it to false. The
captured activation scheduler still queued the actual grain turn, so grain
code did not run inline on the signaler.

Balanced `B,O,O,B,B,O,O,B` runs were strongly affected by frequency drift:

| Workload | Throughput | Mean latency |
| --- | ---: | ---: |
| One activation, concurrency 32 | +4.12% | -3.78% |
| Many activations, concurrency 225 | +1.70% | -1.66% |

The first and last fan-in baselines differed by 19%, making those elapsed-time
means insufficient evidence. Low-overhead matched phase traces resolved the
result:

| One-activation phase | Baseline | Candidate |
| --- | ---: | ---: |
| Throughput under trace | 264,784/s | 261,713/s |
| Activation queue mean | 10.74 us | 11.07 us |
| Activation queue p99 | 31.60 us | 32.40 us |

Both traces had zero lost events and 96.8% call completeness. The intended
phase did not improve, so the candidate was removed.

## Rejected: global activation runner placement

The second candidate retained asynchronous signaling but changed
`WorkItemGroup.ScheduleExecution` from worker-local to global ThreadPool
placement. This preserves coalescing and never executes a turn inline.

Balanced concurrency-32 results:

| Workload | Throughput | Mean latency |
| --- | ---: | ---: |
| One activation | -0.21% | +0.22% |
| Many activations | +1.52% | -1.50% |

The many-activation sequence increased steadily over time on both sides.
Matched fan-in phase traces again showed no causal gain:

| One-activation phase | Baseline | Candidate |
| --- | ---: | ---: |
| Throughput under trace | 264,784/s | 264,708/s |
| Activation queue mean | 10.74 us | 11.12 us |
| Activation queue p50 | 8.90 us | 10.10 us |
| Activation queue p99 | 31.60 us | 32.30 us |

The candidate was removed.

## Conclusion

No runtime change is retained. Activation signals and runner scheduling are
already coalesced, and neither removing the signal's asynchronous boundary nor
moving the coalesced runner to the global queue reduced the measured activation
phase. Future activation scheduling work needs a different ownership model or
new profile evidence; further flag and queue-locality changes are exhausted.

The fan-in benchmark is retained for subsequent scheduling and completion
investigations. No serialization or wire-format code changed.
