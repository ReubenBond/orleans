# Serializer performance optimization

## Method

- Run on .NET 10 using BenchmarkDotNet's `ShortRun` job for rapid iteration.
- Measure generated serializer throughput with 1, 16, and 256 item request payloads.
- Cover preallocated serialization, allocating serialization, deserialization, round-trips, and deep copies.
- Record managed allocations, JIT disassembly, branch behavior, cache misses, instructions, and cycles where the host supports the requested hardware counters.
- Use `dotnet-trace` and `pvanalyze` to identify inclusive CPU and allocation hot paths before changing production code.

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
