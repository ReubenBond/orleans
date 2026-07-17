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

Pending the first run on branch `perf/serializer-unsafe-accessors`.
