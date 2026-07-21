# Serializer codec bodies and identity hashing

Date: 2026-07-21

## Workloads

The investigation used the retained `SerializerThroughputBenchmarks` at
1/16/256 items and the existing mega-graph and generated-code suites as graph
controls. The representative payload is acyclic with unique item objects,
strings, arrays, and a dictionary; the broader serializer tests cover shared
references, cycles, unknown fields, and version tolerance.

Sustained 256-item profile throughput on .NET 10 x64:

| Operation | Operations / 15 seconds |
| --- | ---: |
| Serialize | 1,569,792 |
| Deserialize | 1,062,912 |
| Deep copy | 2,743,296 |

All EventPipe traces reported zero lost events. Raw traces, pvanalyze JSON,
BenchmarkDotNet logs, and disassembly are under
`Artifacts\Benchmarks\SerializerCodecIdentity`.

## Profile result

Previous work removed dictionary clearing, capacity regrowth, reflection member
access, and most generic-dispatch overhead. The new profiles confirm that those
costs did not return.

Serialize exclusive CPU:

| Method | Sampled exclusive CPU |
| --- | ---: |
| Generated item `WriteField` | 4,845 ms |
| Array codec `WriteField` | 380 ms |
| Generated payload `WriteField` | 71 ms |
| String interface codec | 71 ms |
| `ReferenceIdentityMap.GetOrAdd` | 45 ms |
| Runtime identity hash helpers | approximately 1 ms mapped |

Deserialize is similarly dominated by generated item/payload readers
(4,349 ms and 1,205 ms). Reference recording is 7 ms and does not perform
identity hashing. Deep copy is dominated by the generated payload copier
(6,439 ms); identity-map set/lookup total 20 ms and hash helpers total
approximately 2 ms mapped.

Identity hashing is therefore no longer a material target. The generated codec
body dominates because it performs the required field headers, varints,
strings, bounds handling, and reference semantics.

## JIT disassembly

With tiering and ReadyToRun disabled, the generated
`Codec_SerializerBenchmarkItem.WriteField` is 4,103 bytes for both
`PooledBuffer` and `SpanBufferWriter`. The method contains inlined primitive
writer fast paths, slow-path calls, reference lookup, and wire-type handling.
There is no remaining reflection or generic virtual-dispatch frame in this hot
method.

The size suggested testing whether outlining reference lookup could improve
instruction locality.

## Rejected: outline reference lookup

`ReferenceIdentityMap.GetOrAdd` was changed from aggressive inline to no-inline.
This changes only in-memory reference bookkeeping and cannot affect wire bytes.

Paired-shape BenchmarkDotNet results:

| Items | Baseline | Outlined | Change |
| ---: | ---: | ---: | ---: |
| 1 | 360.6 ns | 383.7 ns | **+6.4% slower** |
| 16 | 934.4 ns | 992.5 ns | **+6.2% slower** |
| 256 | 10,445.5 ns | 10,540.7 ns | +0.9% slower |

Allocations remained zero. The call overhead hurts small and medium payloads,
while any instruction-cache benefit is below noise for the large payload. The
candidate was removed.

## Conclusion

No runtime change is retained. Codec bodies now represent required wire work,
and identity hashing is below useful optimization precision. Future codec work
needs a newly measured repeated operation inside a generated body, not generic
code-size reduction. Reattempting fused header checks, branch-oriented header
writes, or outlined reference tracking is not supported by current x64 JIT
evidence.
