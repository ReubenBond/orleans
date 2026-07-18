# Serializer reference-tracking reset

Date: 2026-07-18

## Environment

- Windows 11 10.0.26200.8875
- 11th Gen Intel Core i7-11850H, 8 physical / 16 logical cores, x64
- .NET SDK 10.0.302
- .NET runtime 10.0.10
- BenchmarkDotNet 0.15.6
- Branch `perf/serializer-generic-dispatch` at `aee74e35c9`

## Hypothesis

`perf(serialization): optimize copy context reset` (62566a3552) removed the
`Dictionary.Clear()`/`Buffer.ZeroMemoryInternal` cost from the *deep-copy* reference table by
giving `CopyContext` a generation-stamped identity map, but explicitly left the
serialize/deserialize paths unchanged. Those paths use `ReferencedObjectCollection`, which still
kept its object->id and id->object overflow tables in `Dictionary` instances that are cleared on
every `SerializerSession.Reset()`.

Sampled `dotnet-sampled-thread-time` traces of the sustained `SerializerThroughputBenchmarks`
profile loop (256 items) confirmed the cost:

- `serialize`: `Buffer.ZeroMemoryInternal` was **19.4%** of sampled CPU, called directly from the
  inlined `SerializerSession.Reset()` -> `ReferencedObjectCollection.Reset()` ->
  `Dictionary.Clear()`. Steady-state serialize allocates nothing, so this clear was the dominant
  removable cost.
- `deserialize`: `Buffer.ZeroMemoryInternal` was **16.9%**, part clear of the read-side overflow
  dictionary and part unavoidable zeroing of freshly allocated output objects.

A control experiment that skipped the clears by nulling the dictionaries made throughput *worse*
(serialize 1,106,944 -> 697,344 ops/15s) because every reset then reallocated and regrew the
dictionaries. The fix therefore has to avoid both clearing and reallocation.

## Change

`ReferencedObjectCollection` now resets in O(1) using generation stamps instead of zeroing:

- The object->id (serialize) table uses `ReferenceIdentityMap<uint>` (the same generation-stamped
  identity map already used by `CopyContext`), replacing the inline array plus
  `Dictionary<object, uint>` overflow. A new single-pass `GetOrAdd` avoids the double hash of a
  `TryGetValue`/`Set` pair on the hot path.
- The id->object (deserialize) overflow table uses a new `ReferenceIdMap` (a `uint`-keyed
  generation-stamped map preserving insertion order), replacing the `Dictionary<uint, object>`
  overflow while keeping the existing 32-entry inline fast path and all
  `UnknownFieldMarker`/rewind semantics.

`Reset()` now only clears the small inline array and bumps each map's generation; live object
references are released but hash metadata and capacity are retained, so a pooled session reused
across operations performs no per-reset zeroing and no per-reset allocation.

The wire format is unchanged (only in-memory bookkeeping changed). All 3,973 tests in
`Orleans.Serialization.UnitTests` pass, including reference, version-tolerance, and
unknown-field-marker coverage.

## Results

Paired BenchmarkDotNet runs (`--iterationCount 8 --warmupCount 4`) of
`SerializerThroughputBenchmarks`, baseline captured by stashing the change:

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 -- `
  suite --filter "*SerializerThroughputBenchmarks*" --iterationCount 8 --warmupCount 4
```

| Operation | Items | Before | After | Change |
|---|---:|---:|---:|---:|
| Serialize with session | 1 | 360.8 ns | 350.1 ns | -3.0% |
| Serialize with session | 16 | 992.4 ns | 947.8 ns | -4.5% |
| Serialize with session | 256 | 11,350.2 ns | 9,819.9 ns | **-13.5%** |
| Deserialize with session | 16 | 1,654.3 ns | 1,516.4 ns | -8.3% |
| Deserialize with session | 256 | 16,175.5 ns | 15,108.5 ns | -6.6% |
| Round trip with session | 16 | 2,854.9 ns | 2,644.2 ns | -7.4% |
| Round trip with session | 256 | 27,516.7 ns | 25,668.1 ns | -6.7% |
| Deep copy (control) | 256 | 5,428.1 ns | 5,471.3 ns | +0.8% |

`SerializeWithSession` allocates 0 B at every batch size before and after. The 256-item
deserialize output allocation is unchanged at 28,624 B (the newly constructed object graph). The
deep-copy control is within run-to-run noise, as expected: it already used the identity map.

Sustained profile loop (256 items, 15 s), representative of steady state without BenchmarkDotNet
overhead:

| Operation | Before (ops/15s) | After (ops/15s) | Change |
|---|---:|---:|---:|
| serialize | 1,106,944 | ~1,410,000 | +27% |
| deserialize | 716,800 | ~767,000 | +7% |
| roundtrip | 414,720 | ~461,000 | +11% |

After the change, `Buffer.ZeroMemoryInternal` disappears from the top of both the serialize and
deserialize sampled profiles; the generated codec (`OrleansCodeGen...Codec`) and, for serialize,
`RuntimeHelpers.GetHashCode` (identity hashing, inherent to reference tracking) become the
dominant active frames.

Artifacts:

- Baseline table: `Artifacts\Benchmarks\bdn-before.txt`
- Optimized table: `Artifacts\Benchmarks\bdn-after.txt`
- Traces: `Artifacts\Benchmarks\trace\{serialize,deserialize,roundtrip}.nettrace` (baseline),
  `Artifacts\Benchmarks\trace\{serialize2,deserialize2}.nettrace` (optimized)

## Limitations

Results are from one machine and one BenchmarkDotNet launch without hardware counters (the counter
job requires elevation). The sustained-loop numbers are self-paced and noisier than the paired
BenchmarkDotNet run; both agree on direction and that serialize improves most. The read-side
overflow map is retained for the lifetime of the pooled session once triggered (matching the
existing `CopyContext` behaviour) rather than being dropped above a capacity threshold, a minor
memory trade-off for one-time very large graphs.
