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

## Iteration 2: one-byte varint fast paths

Values below 128 previously used the general varint path: logarithm, division, shifting, a wide unaligned write, and the corresponding wide read and trailing-zero count. The writer and reader now branch directly to a single-byte operation for this dominant case.

| Operation | Iteration 1 | Iteration 2 | Change |
|---|---:|---:|---:|
| Serialize, 1 item | 269 ns | 254 ns | 5.8% faster |
| Serialize, 16 items | 962 ns | 900 ns | 6.4% faster |
| Serialize, 256 items | 10.10 us | 9.83 us | 2.6% faster |
| Deserialize, 1 item | 454 ns | 429 ns | 5.5% faster |
| Deserialize, 16 items | 1.60 us | 1.47 us | 7.8% faster |
| Deserialize, 256 items | 16.43 us | 13.22 us | 19.6% faster |

## Iteration 3: outline uncommon numeric wire formats

The deserialization trace attributed 26.6% of sampled CPU to the generated item reader. Generated codecs inline the numeric reader helpers, so their multi-case wire-type switches enlarge every generated read method. `uint` and `long` readers now keep the common varint branch inline and outline fixed-width, compatibility, and error cases, matching the existing `int` reader design.

Focused deserialization measured 420 ns for 1 item and 12.79 us for 256 items, improving on iteration 2 by a further 2.2% and 3.2%, respectively. The 16-item short run was noisy and is excluded from the comparison.

## Rejected: fused field-header/end checks

A generated-code helper which combined `ReadFieldHeader` with the following end-marker check regressed 1-item and 256-item deserialization by approximately 5% on Arm64. The additional return/control-flow shape outweighed the redundant comparison it removed, so the experiment was reverted.

## Rejected: branch-oriented field-header writes

Replacing the embedded-field-id conditional value with an early common-case branch regressed serialization by 11% for 1 item, 7% for 16 items, and 3% for 256 items. The existing shape produces better branchless code, so the experiment was reverted.

## Iteration 4: reduce the inline reference table

Reusing overflow dictionaries changes the inline table tradeoff. Reducing it from 64 to 32 entries cuts quadratic identity scans for medium graphs and removes 1 KB from every serializer session's two preallocated reference tables.

| Operation | Iteration 3 | Iteration 4 | Change |
|---|---:|---:|---:|
| Serialize, 16 items | 900 ns | 786 ns | 12.7% faster |
| Deserialize, 16 items | 1.47 us | 1.19 us | 19.3% faster |
| Serialize, 256 items | 9.83 us | 9.55 us | 2.9% faster |
| Deserialize, 256 items | 12.79 us | 12.49 us | 2.3% faster |

The final serialization trace still showed only 9 collections and 39 MB of process-startup allocation. Dictionary resize and application allocation remained absent.

## Final comparison

| Operation | Payload | Baseline | Final | Change |
|---|---:|---:|---:|---:|
| Serialize with reused session | 1 item | 262 ns | 253 ns | 3.6% faster |
| Serialize with reused session | 16 items | 964 ns | 786 ns | 18.5% faster |
| Serialize with reused session | 256 items | 14.24 us / 29,776 B | 9.55 us / 0 B | 32.9% faster, allocation eliminated |
| Serialize to array | 256 items | 14.71 us / 37,288 B | 10.94 us / 7,512 B | 25.6% faster, 79.9% less allocation |
| Deserialize with reused session | 1 item | 452 ns | 416 ns | 7.9% faster |
| Deserialize with reused session | 16 items | 1.58 us | 1.19 us | 24.6% faster |
| Deserialize with reused session | 256 items | 20.24 us / 58,400 B | 12.49 us / 28,624 B | 38.3% faster, 51.0% less allocation |
| Round-trip with reused session | 1 item | 762 ns | 723 ns | 5.1% faster |
| Round-trip with reused session | 16 items | 2.66 us | 2.20 us | 17.1% faster |
| Round-trip with reused session | 256 items | 32.12 us / 88,176 B | 26.09 us / 28,624 B | 18.8% faster, 67.5% less allocation |

These are same-machine BenchmarkDotNet `ShortRun` comparisons. Small-run confidence intervals are intentionally wider than publication-quality long runs; only changes which repeated across payload sizes and agreed with profiler evidence were retained.
