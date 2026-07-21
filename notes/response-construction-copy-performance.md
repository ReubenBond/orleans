# Remaining response construction and copy costs

Date: 2026-07-21

## Fresh profile

This investigation reprofiled the retained runtime after inbound scheduling,
activation, completion, and local-path work. A zero-loss concurrency-225 CPU
trace completed 590K calls/s under EventPipe.

`MessageFactory.CreateResponseMessage` accounted for 1.32% inclusive CPU.
Its mapped callees were:

| Component | Inclusive CPU share |
| --- | ---: |
| GC poll associated with response construction | 0.99% |
| Request TTL read | 0.29% |
| Response TTL write | 0.04% |
| Message pool dequeue | below 0.01% |

The allocation/GC poll is inherent to acquiring and publishing the response
object when the pool is empty or unavailable. Response-envelope pooling is
already retained, request-context export was not prominent for the empty
context benchmark, and the allocation-free ping response has no mutable result
graph to deep-copy.

Raw CPU traces, call trees, benchmark logs, and JIT output are under
`Artifacts\Benchmarks\ResponseCosts`.

## Rejected: copy absolute TTL state

Response construction copied TTL using:

```csharp
response.TimeToLive = request.TimeToLive;
```

The getter reads elapsed time and creates a remaining `TimeSpan`; the setter
converts it back to milliseconds and starts another stopwatch. The candidate
copied the in-process absolute-expiry stopwatch and `HasTimeToLive` flag
directly. Serialization still read the remaining milliseconds from the
response, so wire bytes and expiration semantics were unchanged.

A correctness test verified finite expiry within one millisecond and the
infinite-TTL case.

Tier-1-style full-optimization disassembly with ReadyToRun and tiering disabled
showed:

| `CreateResponseMessage` | Code size |
| --- | ---: |
| Baseline | 424 bytes |
| Direct TTL copy | 408 bytes |

The candidate replaced separate TTL getter/setter calls with one
`CopyTimeToLiveFrom` call. Despite the smaller code and removal of the measured
subcomponent, balanced `B,O,O,B,B,O,O,B` saturation runs measured:

| Version | Mean throughput | Mean latency |
| --- | ---: | ---: |
| Baseline | 639,754/s | 351.88 us |
| Direct TTL copy | 632,266/s | 355.75 us |
| Change | **-1.17%** | **+1.10%** |

The expected ceiling was only 0.29% CPU, below end-to-end run movement. The
candidate was removed.

## Conclusion

No runtime change is retained. Response construction is no longer a dominant
fixed cost for allocation-free ping calls, and its largest independently
removable subcomponent is below end-to-end precision. Further changes to
pooling, request context, or result copying require a payload that actually
exercises those features and belong in the serializer codec/identity
investigation rather than this fixed empty-response path.
