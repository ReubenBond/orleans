# Serializer performance optimization

## Method

- Run on .NET 10 using BenchmarkDotNet's `ShortRun` job for rapid iteration.
- Measure generated serializer throughput with 1, 16, and 256 item request payloads.
- Cover preallocated serialization, allocating serialization, deserialization, round-trips, and deep copies.
- Record managed allocations, JIT disassembly, branch behavior, cache misses, instructions, and cycles where the host supports the requested hardware counters.
- Use `dotnet-trace` and `pvanalyze` to identify inclusive CPU and allocation hot paths before changing production code.

Set `ORLEANS_BENCHMARK_HARDWARE_COUNTERS=1` to request ETW hardware counters on a supported host. They are opt-in because unavailable counters cause BenchmarkDotNet to reject the run. BenchmarkDotNet's disassembly diagnoser did not produce output on the Windows Arm64 host.

Run the focused suite from the repository root:

```powershell
dotnet run -c Release -f net10.0 --project test\Benchmarks\Benchmarks.csproj -- Serializer
```

## Baseline

Environment: Windows 11 on Arm64, .NET 10.0.10, server GC, commit `ad0ac273b0`.

| Operation | 1 item | 16 items | 256 items | Allocation at 256 |
|---|---:|---:|---:|---:|
| Serialize with reused session and buffer | 262 ns | 964 ns | 14.24 us | 29,776 B |
| Serialize to a new array | 349 ns | 1.18 us | 14.71 us | 37,288 B |
| Deserialize with reused session | 452 ns | 1.58 us | 20.24 us | 58,400 B |
| Round-trip with reused session | 762 ns | 2.66 us | 32.12 us | 88,176 B |
| Deep copy | 171 ns | 520 ns | 6.04 us | 15,808 B |

The 256-item serialization case crosses the reference table's 64-object inline capacity. `ReferencedObjectCollection` creates overflow dictionaries at that point and discards them on every session reset, accounting for the unexpected 29,776 B per-operation allocation. The first optimization will reuse bounded overflow storage between operations.

## Iteration 1: reuse reference overflow storage

The baseline `pvanalyze` trace attributed 63.9% of sampled CPU time to `Dictionary<object, uint>.Resize()`, called by the generated item codec through `ReferencedObjectCollection`. The 15-second trace observed 5,175 collections and 28,066 MB of allocation traffic.

`ReferencedObjectCollection.Reset` now clears and reuses overflow dictionaries with capacity up to 1,024. Larger tables are discarded so unusually large graphs do not permanently inflate pooled sessions.

| Operation, 256 items | Baseline | Iteration 1 | Change |
|---|---:|---:|---:|
| Serialize with reused session | 14.24 us / 29,776 B | 10.10 us / 0 B | 29% faster, allocation eliminated |
| Deserialize with reused session | 20.24 us / 58,400 B | 16.43 us / 28,624 B | 19% faster, 51% less allocation |

A second `pvanalyze` trace confirmed that `Dictionary.Resize` disappeared from the hot list. Collections fell from 5,175 to 9 and allocation traffic fell from 28,066 MB to 38 MB; the remaining trace allocation is process startup.
