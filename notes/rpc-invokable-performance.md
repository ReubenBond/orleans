# RPC invokable performance

Date: 2026-07-18

## Environment

- Windows 11 10.0.26200.8875
- Snapdragon X 12-core X1E80100, ARM64
- .NET SDK 10.0.302
- .NET runtime 10.0.10
- BenchmarkDotNet 0.15.6
- Branch based on `perf/serializer-generic-dispatch` at `aee74e35c9`

## Baseline

`RpcInvocationBenchmarks` invokes real source-generated request types through `IInvokable`.
It covers monomorphic `ValueTask<int>` and `Task<int>` calls, a four-type polymorphic
`ValueTask<int>` call site, batch sizes of 1, 16, and 256, and a direct-call control.

The benchmark was run with:

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 -- `
  suite --filter "*RpcInvocationBenchmarks*" --iterationCount 10 --warmupCount 5
```

Representative results:

| Operation | Count | Mean | Per invocation | Allocated |
|---|---:|---:|---:|---:|
| Direct `ValueTask` control | 256 | 78.89 ns | 0.31 ns | 0 B |
| Monomorphic `ValueTask` invokable | 256 | 5,141.73 ns | 20.08 ns | 0 B |
| Polymorphic `ValueTask` invokable | 256 | 5,558.18 ns | 21.71 ns | 0 B |
| Monomorphic `Task` invokable | 256 | 7,943.56 ns | 31.03 ns | 18,432 B |

The one-invocation `ValueTask` means were 19.25 ns monomorphic and 19.79 ns polymorphic.
The direct control was too short to measure reliably at that batch size.

A 15-second sustained monomorphic profile completed 687,083,520 invocations. A sampled CPU
trace showed the shared `Request<int>.Invoke` frame, `VirtualDispatchHelpers` at 0.51% exclusive
CPU, and response-pool return work. The generated method body is currently reached through
`Request<int>.Invoke` calling the abstract generated `InvokeInner` method. Therefore, the next
hypothesis is that generating the `Invoke` override on each sealed request type will remove that
second virtual dispatch and allow the target call and synchronous completion path to inline.

## Direct generated dispatch

The standard `Request`, `Request<T>`, `TaskRequest`, `TaskRequest<T>`, and `VoidRequest` bases now
opt in to direct invocation generation. Their synchronous response logic is factored into
aggressively inlined `WrapResponse` helpers, and the code generator emits `Invoke` directly on
each sealed request:

```csharp
public override ValueTask<Response> Invoke()
{
    try
    {
        return WrapResponse(_target.Method(arg0));
    }
    catch (Exception exception)
    {
        return new ValueTask<Response>(Response.FromException(exception));
    }
}
```

The generated method no longer enters the shared base implementation and then dispatches
virtually to `InvokeInner`. Custom request bases do not opt in, so transaction and other custom
invocation wrappers retain their existing behavior. Generated-invokable execution tests cover
all five return shapes, synchronous and asynchronous completion, and synchronous and
asynchronous exceptions on .NET 8 and .NET 10.

## Paired results

The final committed-code run used the same command, warmup count, and iteration count as the
baseline.

| Operation | Count | Before | After | Change |
|---|---:|---:|---:|---:|
| Direct `ValueTask` control | 256 | 78.89 ns | 78.65 ns | -0.3% |
| Monomorphic `ValueTask` invokable | 256 | 5,141.73 ns | 4,944.74 ns | -3.8% |
| Polymorphic `ValueTask` invokable | 256 | 5,558.18 ns | 5,331.29 ns | -4.1% |
| Monomorphic `ValueTask` invokable | 1 | 19.25 ns | 18.11 ns | -5.9% |
| Polymorphic `ValueTask` invokable | 1 | 19.79 ns | 18.18 ns | -8.1% |
| Polymorphic `ValueTask` invokable | 16 | 568.13 ns | 317.81 ns | -44.1% |

The 256-call control was effectively unchanged, while both allocation-free invocation cases
improved by approximately 4%. The large 16-call polymorphic improvement indicates that the old
shared call site was particularly sensitive to tiering and type feedback; it is not used as the
general expected gain. Allocations remained zero for the `ValueTask` cases.

`Task` results improved at counts 1 and 16 but regressed at count 256 with high variance. Since
the benchmark target allocates a 72-byte `Task` per invocation, no stable `Task` throughput
conclusion is attributed to the dispatch change. Its allocation size was unchanged.

The optimized 15-second profile completed 783,599,616 invocations, 14.0% more than the baseline
under the profiler. The shared `Request<int>.Invoke` frame was replaced by the sealed generated
invokable's `Invoke` frame, and `VirtualDispatchHelpers` fell from 0.51% to 0.28% exclusive CPU.

Artifacts:

- Benchmark log:
  `Artifacts\Benchmarks\Rpc\Benchmarks.Rpc.RpcInvocationBenchmarks-20260718-071941.log`
- Final optimized benchmark log:
  `Artifacts\Benchmarks\Rpc\Benchmarks.Rpc.RpcInvocationBenchmarks-20260718-083515.log`
- Baseline trace:
  `Artifacts\Benchmarks\Rpc\traces\invokable-baseline.nettrace`
- Optimized trace:
  `Artifacts\Benchmarks\Rpc\traces\invokable-optimized.nettrace`

## Limitations

The BenchmarkDotNet disassembly exporter emitted no assembly on ARM64, so the investigation uses
sampled runtime traces. Results are from one machine and one BenchmarkDotNet launch without
hardware counters. The paired result uses one launch per version; the second optimized run
showed the same direction for all `ValueTask` cases. `Task.FromResult` accounts for the allocation
in the `Task` benchmark target; the RPC invocation wrapper itself does not add a steady-state
allocation in the `ValueTask` cases.
