# Serializer generic dispatch gate on x64

Date: 2026-07-21

## Entry gate

The retained `CodecDispatchBenchmarks` were rerun unchanged on:

- Windows 11 10.0.26200
- Intel Core i7-11850H, x64
- .NET 10.0.10, RyuJIT x86-64-v4
- BenchmarkDotNet 0.15.6
- five measurement and three warmup iterations

This is a new architecture relative to the earlier ARM64 rejection. The suite
compares generated concrete calls, `IFieldCodec<T>`/`IDeepCopier<T>` calls, and
constrained generic serialization at 1/16/256 items.

Raw BenchmarkDotNet logs are under
`Artifacts\Benchmarks\GenericDispatchGate\x64`.

## Results

Serialization:

| Items | Concrete | Interface | Constrained |
| ---: | ---: | ---: | ---: |
| 1 | 36.79 ns | 39.30 ns (+7%) | 38.83 ns (+6%) |
| 16 | 518.84 ns | 562.95 ns (+9%) | 548.06 ns (+6%) |
| 256 | 8.408 us | 8.708 us (+4%) | 8.774 us (+4%) |

Deserialization:

| Items | Concrete | Interface | Result |
| ---: | ---: | ---: | ---: |
| 1 | 76.24 ns | 57.82 ns | Interface 24% faster |
| 16 | 867.22 ns | 972.62 ns | Interface 12% slower |
| 256 | 13.666 us | 14.460 us | Interface 6% slower |

Deep copy:

| Items | Concrete | Interface | Result |
| ---: | ---: | ---: | ---: |
| 1 | 17.23 ns | 16.82 ns | Interface 2% faster |
| 16 | 271.38 ns | 265.65 ns | Interface 2% faster |
| 256 | 4.397 us | 4.562 us | Interface 4% slower |

The sub-100 ns cases are sensitive to measurement floor effects, but the
larger cases still do not support one dispatch policy across operations.

## JIT/code-size interpretation

BenchmarkDotNet disassembly reports:

| Serialization path | Representative code size |
| --- | ---: |
| Concrete | approximately 9.3-9.5 KB |
| Interface | approximately 1.74 KB |
| Constrained | approximately 1.81 KB |

Concrete calls expose the generated 4 KB item codec for inlining and improve
serialization, but multiply code size by more than five. Constrained calls do
not recover that gain. Interface dispatch keeps the container loop compact and
allows dynamic PGO to optimize copier paths, but remains somewhat slower for
large serialization/deserialization loops.

Production container codecs obtain element codecs from `CodecProvider` as
interfaces. Replacing them with concrete calls would require generator-specific
container implementations for each element type, cloning large codec bodies
into array/list/dictionary loops. The serializer codec investigation already
showed that outlining those bodies regresses small and medium payloads.

## Decision

The entry gate is closed without a runtime change:

- there is no consistent interface penalty across serialize, deserialize, and
  deep copy;
- constrained dispatch does not improve on interface dispatch;
- concrete serialization's 4-9% isolated gain comes with severe code growth;
  and
- a generator-wide specialized-container design would increase instruction
  footprint for a workload-specific gain.

Keep `CodecDispatchBenchmarks` as the detector for future JIT/runtime changes.
Revisit only when constrained generic calls devirtualize to concrete-quality
code without the current expansion, or when a measured production payload is
dominated by one container/codec pair strongly enough to justify explicit
specialization. No wire-format code changed.
