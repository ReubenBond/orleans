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

Artifacts:

- Benchmark log:
  `Artifacts\Benchmarks\Rpc\Benchmarks.Rpc.RpcInvocationBenchmarks-20260718-071941.log`
- Baseline trace:
  `Artifacts\Benchmarks\Rpc\traces\invokable-baseline.nettrace`

## Limitations

The BenchmarkDotNet disassembly exporter emitted no assembly on ARM64, so the investigation uses
sampled runtime traces. Results are from one machine and one BenchmarkDotNet launch without
hardware counters. `Task.FromResult` accounts for the allocation in the `Task` benchmark target;
the RPC invocation wrapper itself does not add a steady-state allocation in the `ValueTask`
cases.
