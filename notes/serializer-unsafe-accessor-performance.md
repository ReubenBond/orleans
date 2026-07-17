# Serializer UnsafeAccessor performance

## Goal

Replace reflection-created member-access delegates in generated serializers and copiers with
`UnsafeAccessor` calls for supported target frameworks. The focused cases are private fields,
readonly fields, init-only properties, and get-only properties.

## Method

- Use `MemberAccessBenchmarks` for a fixed-shape payload represented using public mutable
  properties, private readonly fields, init-only properties, and get-only properties.
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
Use runtime traces and `pvanalyze` for hot-path confirmation on this machine.
