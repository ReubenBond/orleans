# Performance optimization guide

Use a tight, evidence-driven loop:

1. **Create representative benchmarks.** Include realistic inputs, hot and cold cases, batch
   sizes, and an unchanged control. Put reusable benchmarks in `test\Benchmarks`.
2. **Record a baseline.** Run Release builds with fixed warmup/iteration counts and preserve the
   timestamped BenchmarkDotNet log.
3. **Analyze before changing code.** Inspect mean, variance, allocations, generated assembly, and
   CPU traces. Form one specific hypothesis about the observed cost.
4. **Implement one optimization.** Keep the change narrow and add correctness coverage.
5. **Repeat the same measurement.** Compare against both the baseline and control; treat changes
   near the control's run-to-run movement as noise.
6. **Write down the evidence and commit.** Record environment, commands, before/after numbers,
   interpretation, and limitations. Commit benchmarks, implementation, and results separately.
7. **Repeat.** Let each result determine the next hypothesis; stop when the target cost disappears
   from profiles or further changes do not produce a stable gain.

## BenchmarkDotNet

Follow `SerializerBenchmarkConfig` as the template: `MemoryDiagnoser`, JSON/GitHub exporters,
`DisassemblyDiagnoser` with source and combined reports, and optional hardware counters.

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 -- `
  suite --filter "*TargetBenchmarks*" --iterationCount 10 --warmupCount 5

$env:ORLEANS_BENCHMARK_HARDWARE_COUNTERS = "1" # Run elevated on Windows.
```

Use short jobs while iterating, then longer paired runs for final numbers. Inspect the generated
assembly for interface dispatch, failed inlining, bounds checks, branches, and unnecessary loads.
If disassembly export is unavailable on the current architecture, rely on runtime traces.

## Tracing and pvanalyze

Add a sustained profile loop which exercises one operation without BenchmarkDotNet overhead, then:

```powershell
dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time `
  --duration 00:00:00:10 --output target.nettrace

pvanalyze cpustacks target.nettrace --top 30 --inclusive
pvanalyze calltree target.nettrace --hot-path --depth 12
pvanalyze calltree target.nettrace --caller-callee "TargetMethod"
pvanalyze alloc target.nettrace --top 30
```

Build `pvanalyze` from <https://github.com/adityamandaleeka/pvanalyze> if the tool is not available
from NuGet. Use PerfView for deeper ETW inspection and ProcMon only when file/process activity is
part of the suspected cost.

Keep logs under `Artifacts\Benchmarks\<area>` and concise conclusions under `notes\`. Prefer
measured explanations such as "the call now inlines and interface-dispatch frames disappeared"
over explanations inferred only from elapsed time.
