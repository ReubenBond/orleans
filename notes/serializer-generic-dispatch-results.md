# Serializer generic dispatch investigation

Date: 2026-07-17

## Environment

- Windows 11 10.0.26200.8737
- Snapdragon X 12-core X1E80100, ARM64
- .NET SDK 10.0.302
- .NET runtime 10.0.10
- BenchmarkDotNet 0.15.6
- Branch based on `perf/serializer-unsafe-accessors` at `9ac9b4ec8a`

## Generic interface dispatch

`CodecDispatchBenchmarks` compares concrete, `IFieldCodec<T>`/`IDeepCopier<T>`, and constrained
calls using the generated `SerializerBenchmarkItem` codec and copier at batch sizes 1, 16, and
256. The benchmark was run with:

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 -- `
  suite --filter "*CodecDispatchBenchmarks*" --iterationCount 10 --warmupCount 5
```

At 256 items, interface serialization was 9,031.53 ns versus 9,674.63 ns for a concrete call and
9,371.02 ns for constrained dispatch. Interface deserialization was 13,333.06 ns versus
13,233.99 ns concrete. Interface deep copying was 5,304.13 ns versus 5,095.09 ns concrete.
Smaller batches did not show a consistent opportunity either. Sampled traces showed
`VirtualDispatchHelpers` for generic codec methods, but copier interface calls were optimized
away by dynamic PGO.

The evidence rejects replacing interface calls with concrete or constrained calls: the potential
gain is small for deserialization and copying, while serialization regresses. The benchmark is
retained to detect changes in future runtimes.

## Copy context reset

The same traces showed a larger shared cost outside dispatch. Before the change,
`Buffer.ZeroMemoryInternal` consumed 22.58% of sampled deep-copy CPU and
`CopyContextPool.Return` was 22.6% inclusive because `Dictionary.Clear()` zeroed its entries and
buckets after every operation.

`CopyContext` now uses a reference-identity map with generation-stamped buckets. Reset releases
all live keys and values but does not clear hash metadata or unused capacity.

The representative throughput suite was run before and after using:

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 -- `
  suite --filter "*SerializerThroughputBenchmarks*" --iterationCount 10 --warmupCount 5
```

| Operation | Items | Before | After | Change |
|---|---:|---:|---:|---:|
| Deep copy | 1 | 165.8 ns | 152.5 ns | -8.0% |
| Deep copy | 16 | 485.3 ns | 429.1 ns | -11.6% |
| Deep copy | 256 | 6,066.1 ns | 5,225.0 ns | -13.9% |
| Serialize with session | 16 | 790.1 ns | 795.2 ns | +0.6% |
| Deserialize with session | 256 | 12,541.8 ns | 12,453.8 ns | -0.7% |

Allocations were unchanged. The 256-item serialize control moved +2.6% with higher variance and
the other controls moved in both directions, so no serialization or deserialization change is
attributed to this deep-copy-only implementation.

In a sustained 256-item deep-copy profile, throughput increased from 2,416,640 to 2,779,136
operations per 15 seconds (+15.0%). `CopyContextPool.Return` dropped to 1.25% of sampled CPU and
`Buffer.ZeroMemoryInternal` disappeared from the hot path.

Artifacts:

- Baseline log:
  `Artifacts\Benchmarks\Serializer\Benchmarks.Serialization.SerializerThroughputBenchmarks-20260717-194205.log`
- Optimized log:
  `Artifacts\Benchmarks\Serializer\Benchmarks.Serialization.SerializerThroughputBenchmarks-20260717-202803.log`
- Baseline trace:
  `Artifacts\Benchmarks\Serializer\traces\generic-dispatch-copy-baseline.nettrace`
- Optimized trace:
  `Artifacts\Benchmarks\Serializer\traces\generic-dispatch-copy-identity-map.nettrace`

## Limitations

The ARM64 disassembly exporter did not emit code-size or assembly data, so this investigation
used sampled runtime traces. Results are from one machine and one BenchmarkDotNet launch without
hardware counters. The rejected dispatch hypothesis should be re-evaluated on other
architectures or after runtime generic virtual method dispatch changes.
