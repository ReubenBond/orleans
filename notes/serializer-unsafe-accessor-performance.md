# Serializer UnsafeAccessor performance

## Goal

Replace reflection-created member-access delegates in generated serializers and copiers with
`UnsafeAccessor` calls for supported target frameworks. The focused cases are private fields,
readonly fields, init-only properties, and get-only properties.

## Method

- Use `MemberAccessBenchmarks` for a fixed-shape payload represented using public mutable
  properties, private readonly fields, private properties, init-only properties, and get-only
  properties.
- Measure serialization, deserialization, and deep copying at 1, 16, and 256 elements.
- Keep the public mutable representation as the direct-access control.
- Use BenchmarkDotNet short runs for iteration, including allocation data, generated assembly,
  and optional hardware counters.
- Use `dotnet-trace` and `pvanalyze` on a sustained profile loop to validate the CPU hot path.

## Baseline

Environment: Windows 11, Snapdragon X1E80100 (Arm64), .NET 10.0.10. BenchmarkDotNet
0.15.6, short job (3 warmups and 3 measurements).

Representative 256-element results:

| Operation | Public mutable | Private readonly fields | Init-only | Get-only |
| --- | ---: | ---: | ---: | ---: |
| Serialize | 10.77 us | 10.30 us | 10.01 us | 9.60 us |
| Deserialize | 14.19 us | 16.27 us | 14.62 us | 16.19 us |
| Deep copy | 5.83 us | 10.56 us | 8.18 us | 8.76 us |

The clearest access penalty is in deep copy, where the private readonly representation is 81%
slower than direct public access. Deserialization of private readonly and get-only members is
approximately 14-15% slower. Serialization is codec-dominated for this payload and did not show
a stable access penalty in the short run.

The configured BenchmarkDotNet disassembly exporter produced an empty assembly report on Arm64.

## UnsafeAccessor implementation

Generated serializers and copiers now emit ref-returning field accessors:

```csharp
[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_value")]
private static extern ref int getField0(MyType instance);
```

The same accessor reads and writes an inaccessible field, replacing two static delegates and
allowing the JIT to optimize the access as a field operation. Struct receivers are passed by
reference. The generator retains the reflection delegate path when `UnsafeAccessorAttribute` is
not present or when a newly optimized field signature contains generic parameters.

No polyfill is needed. `UnsafeAccessorAttribute` is available on .NET 8 and later; unsupported
compilations continue to use the existing reflection path.

## Paired results

The final comparison used 5 warmup and 10 measurement iterations for each build. The reflection
and UnsafeAccessor builds were run from separate worktrees using the same benchmark source.

Deep-copy results:

| Shape | Count | Reflection | UnsafeAccessor | Improvement |
| --- | ---: | ---: | ---: | ---: |
| Private readonly fields | 1 | 67.56 ns | 51.08 ns | 24.4% |
| Private properties | 1 | 65.36 ns | 46.25 ns | 29.2% |
| Init-only properties | 1 | 56.75 ns | 46.84 ns | 17.5% |
| Get-only properties | 1 | 57.81 ns | 50.72 ns | 12.3% |
| Private readonly fields | 16 | 711.28 ns | 469.37 ns | 34.0% |
| Private properties | 16 | 705.74 ns | 441.25 ns | 37.5% |
| Init-only properties | 16 | 542.85 ns | 439.76 ns | 19.0% |
| Get-only properties | 16 | 613.56 ns | 501.37 ns | 18.3% |
| Private readonly fields | 256 | 10.67 us | 7.12 us | 33.3% |
| Private properties | 256 | 10.24 us | 5.98 us | 41.6% |
| Init-only properties | 256 | 8.52 us | 6.15 us | 27.8% |
| Get-only properties | 256 | 8.77 us | 6.38 us | 27.2% |

The 256-element public-mutable control was effectively unchanged (6.14 us reflection build,
6.16 us UnsafeAccessor build).

Deserialization improved most consistently for private fields and get-only properties:

| Shape | Count | Reflection | UnsafeAccessor | Improvement |
| --- | ---: | ---: | ---: | ---: |
| Private readonly fields | 1 | 108.35 ns | 102.55 ns | 5.4% |
| Private properties | 1 | 112.44 ns | 105.24 ns | 6.4% |
| Init-only properties | 1 | 107.48 ns | 101.19 ns | 5.9% |
| Get-only properties | 1 | 111.34 ns | 99.73 ns | 10.4% |
| Private readonly fields | 16 | 1.07 us | 0.98 us | 8.1% |
| Private properties | 16 | 0.98 us | 0.92 us | 6.4% |
| Get-only properties | 16 | 1.07 us | 1.03 us | 3.2% |
| Private readonly fields | 256 | 17.51 us | 15.55 us | 11.2% |
| Init-only properties | 256 | 15.12 us | 14.82 us | 2.0% |
| Get-only properties | 256 | 16.62 us | 14.95 us | 10.1% |

Results below approximately 5% are within the observed run-to-run movement of the public control.
Private-property deserialization at 256 elements and init-only deserialization at 16 elements did
not improve. Serialization remained codec-dominated and showed no consistent access-related gain.
All measured operations retained the same steady-state allocation size.

## Trace analysis

`dotnet-trace` sampled the sustained 256-element private-field deep-copy profile, and `pvanalyze`
analyzed the resulting `.nettrace` files. Under the profiler, the reflection build completed
536,576 operations in 15.01 seconds; the UnsafeAccessor build completed 2,194,432 operations in
15.00 seconds (4.1x throughput). The optimized trace collapses the generated copier/accessors into
the benchmark frame; allocation zeroing is the dominant mapped CPU cost.

The BenchmarkDotNet results above are the primary comparison because they isolate process launch,
warmup, and measurement more rigorously than the sustained trace loop.
