# Silo-to-silo call performance

## Goal

Reduce the cost of end-to-end Orleans calls between two silos without changing the
message wire format.

## Method

`FixedPing` runs 100 workers from the primary silo's hosted client to grains placed
only on the secondary silo. Each worker has one request in flight. Runs use .NET
10.0.10 Release builds, a 10-second warmup, and five fixed measurement intervals.
The one-silo hosted-client mode is the control.

Environment:

- Windows 10.0.26200
- Intel Core i7-11850H, 16 logical processors
- Baseline commit `ecce8819b703abb9168e8448c9218709d576134e`

Representative commands:

```powershell
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  FixedPing silo-to-silo 100 10 10 5

dotnet-trace collect --profile gc-verbose --duration 00:00:00:25 `
  --output alloc-silo-to-silo.nettrace -- `
  dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll `
  FixedPing silo-to-silo 100 5 25 1

pvanalyze alloc alloc-silo-to-silo.nettrace --from 10000 --to 25000 --top 30
```

Logs, traces, and JIT output are under `Artifacts\Benchmarks\SiloCalls`.

## Baseline

| Metric | Result |
| --- | ---: |
| Throughput | 416,503 calls/s |
| Run standard deviation | 1.41% |
| Allocation | 1,169.4 B/call |
| Local-control variation | 1.20% |

The allocation trace ranked `Message` first, followed by `byte[]`,
`CallbackData`, the callback dictionary node, `Task`, and the generated ping
invokable. A 20-second steady-state allocation window sampled 49,924 `Message`
allocations.

## Response-envelope pooling

The callee previously allocated a new `Message` for every response. Response
messages are now rented from a per-`MessageFactory` object pool and returned only
after a successful transport flush. Client-local terminal failures are returned
after synchronous dispatch. Server-local responses fall back to GC because gateway
and proxy dispatch can transfer ownership asynchronously. Reset clears all body,
routing, header, retry, expiry, interface, and request-context state.

Serialization-failure retries retain ownership of the response and remove it from
the returnable in-flight set before re-enqueuing it. This prevents a queued retry
from observing a reset pooled message.

The serializer and its header/body encoding are unchanged. The pool marker is
non-serialized, so the wire representation is unchanged.

## Results

| Metric | Baseline | Pooled | Change |
| --- | ---: | ---: | ---: |
| Allocation | 1,169.4 B/call | 1,001.7 B/call | -14.3% |
| Throughput, short paired run | 416,503 calls/s | 417,673 calls/s | +0.3% |
| Sampled `Message` allocations/second | 2,496 | 1,835 | -26.5% |

The message-allocation result matches the expected removal of one of the four
request/response envelopes involved in a cross-silo round trip. Throughput results
ranged from +0.3% in the final run to +3.5% in a longer A/B run, while its local
control moved +2.8%. Therefore, no throughput improvement is claimed beyond the
clear allocation and GC-pressure reduction.

## JIT and hardware counters

With ReadyToRun and tiering disabled for inspection, the optimized
`MessageFactory.CreateResponseMessage` replaces `CORINFO_HELP_NEWSFAST` with the
pool's `Get()` call. The method changes from 367 to 371 bytes and retains the same
conditional structure; reset/return is inlined into the connection path.

WPR reports support for `CacheMisses`, `LLCMisses`, `BranchInstructions`,
`BranchMispredictions`, `InstructionRetired`, and cycle counters on this machine.
Collection requires an elevated shell, and the current shell failed to enable the
system profiling policy with error `0xc5585011`. No cache or branch-counter claim is
made without those measurements.

`dotnet-sampled-thread-time` includes wall-time samples from blocked threads on
Windows, so its absolute CPU percentages were not used. Its managed stacks and the
allocation trace identified GC polling and socket submission as the remaining
large costs.

## Rejected experiments

- Changing the inbound runtime boundary from `Task` to `ValueTask` left allocation
  unchanged and moved cross-silo throughput -0.7%.
- Pooling `CallbackData` reduced allocation further but lowered throughput 2.6%
  and increased variance, consistent with cross-thread pool/cache costs.
- Increasing connection flush batches produced one +12.9% run at 16 messages, but
  did not reproduce and both 8- and 32-message batches regressed. The original
  four-message policy is retained.
