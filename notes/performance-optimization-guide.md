# Performance optimization guide

Use a tight, evidence-driven loop:

1. **Create representative benchmarks.** Include realistic inputs, hot and cold cases, batch
   sizes, and an unchanged control. Put reusable benchmarks in `test\Benchmarks`.
2. **Record a baseline.** Run Release builds with fixed warmup/iteration counts and preserve the
   timestamped benchmark log.
3. **Analyze with pvanalyze before changing code.** Inspect mean, variance, allocations, CPU or
   thread-time stacks, JIT behavior, and relevant runtime events. Form one specific hypothesis.
4. **Implement one optimization.** Keep the change narrow and add correctness coverage.
5. **Repeat the same measurement.** Compare against both the baseline and control; treat changes
   near the control's run-to-run movement as noise.
6. **Write down the evidence and commit.** Record environment, commands, before/after numbers,
   interpretation, and limitations. Commit benchmarks, implementation, and results separately.
7. **Repeat.** Let each result determine the next hypothesis; stop when the target cost disappears
   from profiles or further changes do not produce a stable gain.

Keep raw data under `Artifacts\Benchmarks\<area>` and concise conclusions under `notes\`. Prefer
measured explanations such as "the call now inlines and interface-dispatch frames disappeared"
over explanations inferred only from elapsed time.

## pvanalyze first

Use [ReubenBond/pvanalyze](https://github.com/ReubenBond/pvanalyze) as the default trace collection
and analysis front end. It collects EventPipe traces natively and accepts ETW traces from PerfView
(`.etl`, `.etl.zip`, and `.etlx`). It covers the routine PerfView analysis workflows in a
scriptable CLI: CPU and thread-time stacks, async activities, call trees, allocations, GC and
DATAS, JIT statistics, exceptions, arbitrary events, and hardware-counter events.

Use `pvanalyze collect` or PerfView to **collect only the events needed for the question**.
Collecting EventPipe data directly avoids an unnecessary `dotnet-trace` intermediary. PerfView
remains the preferred Windows collector for ETW thread-time and hardware-counter traces;
pvanalyze should be the first tool used to inspect every trace.

### Install or build

The repository requires .NET 10 or later. Build the fork when a `pvanalyze` executable is not
already available:

```powershell
git clone https://github.com/ReubenBond/pvanalyze C:\dev\pvanalyze
dotnet build C:\dev\pvanalyze\pvanalyze.csproj -c Release

# Run the built tool directly, or publish it and add the output directory to PATH.
dotnet C:\dev\pvanalyze\bin\Release\net10.0\pvanalyze.dll --help
dotnet publish C:\dev\pvanalyze\pvanalyze.csproj -c Release -o C:\dev\pvanalyze\publish
```

The README also documents running a packed build through `dnx`:

```powershell
dotnet pack C:\dev\pvanalyze\pvanalyze.csproj -c Release
dnx pvanalyze --source C:\dev\pvanalyze\bin\Release --version 0.1.0 -- info target.nettrace
```

The examples below assume `pvanalyze` is on `PATH`. Use `pvanalyze <command> --help` when a trace
needs an option not shown here.

### Start every investigation with `info`

Always inspect capture contents before analyzing:

```powershell
pvanalyze info target.nettrace
pvanalyze info target.etl.zip
```

`info` reports trace duration, processes, event counts, and which analyses the captured events
support. In particular, verify that it reports the intended stack source, allocation events, GC,
JIT, exceptions, or hardware counters. `stacks` and `calltree` reject unavailable stack sources
instead of silently producing incomplete output.

Raw and zipped traces are converted to an ETLX cache beside the source. Repeated commands reuse the
cache. Remove it after an investigation or when regenerating a trace in place:

```powershell
pvanalyze clean target.etl.zip
```

### Choose the smallest useful capture

| Question | Collector | Capture | Primary pvanalyze commands |
| --- | --- | --- | --- |
| Managed CPU hotspots, cross-platform | `pvanalyze collect` | `cpu` | `cpustacks`, `calltree` |
| Windows CPU hotspots | PerfView | Default CPU collection | `cpustacks --stack-source cpu` |
| Blocking or off-CPU time | PerfView | `/ThreadTime` | `stacks --stack-source threadtime` |
| Async request/activity latency | PerfView | `/ThreadTime` plus application providers | `stacks`/`calltree --stack-source activity` |
| Allocations and GC | Either | `gc-verbose` or CLR allocation/GC providers | `alloc`, `gcstats`, `datas` |
| JIT activity | Either | CLR JIT events | `jitstats` |
| Exceptions | Either | CLR exception events | `exceptions` |
| Cache misses or branch behavior | PerfView, elevated | `/CpuCounters:<counter>:<interval>` | `info`, `events --type PMCSample` |
| A specific EventSource/provider | Either | Targeted provider | `events` |

Collecting unnecessary providers changes timing and trace size. Do not enable `/ThreadTime`,
allocation sampling, verbose GC, or additional EventSources unless the hypothesis needs them.

## Representative sustained workload

BenchmarkDotNet is best for isolated operations. For end-to-end runtime paths, add a sustained
profile mode which:

- performs one representative operation;
- uses fixed concurrency, input, warmup, and measurement duration;
- prints its PID and phase boundaries;
- runs long enough to exclude startup and warmup using `--from`/`--to`; and
- has an unchanged local or no-op control.

Build and launch Release binaries directly so collection does not profile `dotnet run` build work:

```powershell
dotnet build test\Benchmarks\Benchmarks.csproj -c Release -f net10.0
dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll <profile-command>
```

Record the commit, OS, CPU, runtime, architecture, GC mode, benchmark parameters, and any
environment variables beside each trace.

## Cross-platform EventPipe collection

Use pvanalyze itself to attach to a warmed process or launch a built workload. Give the outer
command a hard timeout in addition to pvanalyze's fixed collection duration:

```powershell
pvanalyze collect --process-id <pid> --profile cpu `
  --duration-seconds 15 --output target.nettrace

pvanalyze collect --profile cpu --duration-seconds 30 `
  --output target.nettrace -- `
  dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll <profile-command>
```

For allocation sampling:

```powershell
pvanalyze collect --profile gc-verbose --duration-seconds 30 `
  --output allocations.nettrace -- `
  dotnet test\Benchmarks\bin\Release\net10.0\Benchmarks.dll <profile-command>

# Equivalent targeted provider when a custom profile is preferable.
pvanalyze collect --profile none `
  --providers "Microsoft-Windows-DotNETRuntime:0x8003:5" `
  --output allocations.nettrace -- dotnet <application>.dll
```

For .NET 9+ DATAS decisions, enable DATAS and collect its verbose GC events:

```powershell
$env:DOTNET_GCDynamicAdaptationMode = "1"
pvanalyze collect --process-id <pid> --profile none `
  --providers "Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:5" `
  --output datas.nettrace
```

## Windows ETW collection

Use PerfView as a headless collector and pvanalyze as the analyzer. Stop an interactive PerfView
capture by pressing `S` in its console.

```powershell
# CPU and default CLR events.
PerfView /AcceptEula /NoGui collect cpu.etl.zip

# CPU plus context switches for blocked/off-CPU analysis.
PerfView /AcceptEula /NoGui /ThreadTime collect threadtime.etl.zip

# Add only the EventSource needed to correlate application activities.
PerfView /AcceptEula /NoGui /ThreadTime `
  /Providers:*MyProvider collect activity.etl.zip
```

Thread-time collection has higher overhead than CPU sampling. Use it only for lock contention,
thread-pool starvation, I/O waits, scheduler delays, or request latency which CPU stacks cannot
explain.

### Hardware counters

Hardware counters generally require an elevated Windows shell. Discover the counters and valid
sampling intervals on the current CPU instead of copying names or intervals from another machine:

```powershell
PerfView listCpuCounters
```

Collect one or a small related set at a time:

```powershell
PerfView /AcceptEula /NoGui `
  /CpuCounters:<CounterName>:<ValidInterval> collect counters.etl.zip

pvanalyze info counters.etl.zip
pvanalyze events counters.etl.zip --type PMCSample
pvanalyze events counters.etl.zip --type PMCSample --format json
```

For cache and branch investigations, collect comparable runs for cache misses, retired
instructions, branch instructions, branch mispredictions, and cycles when those counters are
available. Compare normalized rates such as misses per retired instruction and mispredictions per
branch, not raw samples from runs with different durations or throughput. Record unsupported
counters, elevation failures, multiplexing, and sampling intervals as limitations; do not infer
cache or branch improvements from elapsed time alone.

## pvanalyze analysis workflow

Use the same time window for related commands. `--from` and `--to` are milliseconds relative to
trace start, so exclude startup and warmup explicitly:

```powershell
$trace = "Artifacts\Benchmarks\Rpc\target.nettrace"

pvanalyze info $trace
pvanalyze cpustacks $trace --from 10000 --to 25000 --top 30
pvanalyze cpustacks $trace --from 10000 --to 25000 --top 30 --inclusive
pvanalyze calltree $trace --from 10000 --to 25000 --hot-path --depth 12
pvanalyze calltree $trace --from 10000 --to 25000 --caller-callee "TargetMethod"
```

Use exclusive CPU to find leaf costs, inclusive CPU to find expensive subsystems, the hot path to
follow dominant call chains, and caller/callee to test a specific dispatch, serialization, or
scheduling hypothesis. Group broad traces before drilling into methods:

```powershell
pvanalyze cpustacks $trace --group-by module --top 20
pvanalyze cpustacks $trace --group-by namespace --top 30 --inclusive
pvanalyze cpustacks $trace --format json --output cpu.json
pvanalyze cpustacks $trace --format speedscope --output cpu.speedscope.json
```

Use SpeedScope only for interactive visualization after the CLI output identifies the relevant
area. Preserve JSON output when results need automated comparison.

### Thread-time and async activities

Analyze an ETW `/ThreadTime` trace when on-CPU stacks do not explain latency:

```powershell
pvanalyze stacks threadtime.etl.zip `
  --stack-source threadtime --inclusive --top 40
pvanalyze calltree threadtime.etl.zip `
  --stack-source threadtime --hot-path --depth 12
```

For EventSource Start/Stop activities with activity IDs:

```powershell
pvanalyze stacks activity.etl.zip `
  --stack-source activity --inclusive --top 40
pvanalyze calltree activity.etl.zip `
  --stack-source activity --hot-path --depth 12
```

The activity source attributes CPU, blocked, task, and await time to async operations. Keep events
before the selected `--from` boundary in the trace so pvanalyze can reconstruct activity state.

### Allocations, GC, and DATAS

```powershell
pvanalyze alloc allocations.nettrace --from 10000 --to 25000 --top 30
pvanalyze alloc allocations.nettrace --group-by namespace --top 30

pvanalyze gcstats allocations.nettrace
pvanalyze gcstats allocations.nettrace --timeline
pvanalyze gcstats allocations.nettrace --longest 10

pvanalyze datas datas.nettrace
pvanalyze datas datas.nettrace --changes-only
pvanalyze datas datas.nettrace --samples --changes-only
pvanalyze datas datas.nettrace --gen2
```

Allocation events are sampled. Compare normalized bytes or samples per completed operation using
identical collection settings. Correlate allocation changes with GC frequency, pause time, heap
count, and DATAS decisions instead of assuming fewer allocations must improve throughput.

### JIT, exceptions, and arbitrary events

```powershell
pvanalyze jitstats target.nettrace
pvanalyze jitstats target.nettrace --format json

pvanalyze exceptions target.nettrace
pvanalyze exceptions target.nettrace --type NullReference

pvanalyze events target.nettrace --list
pvanalyze events target.nettrace --provider DotNETRuntime --limit 50
pvanalyze events target.nettrace --type GCStart --from 10000 --to 25000
pvanalyze events target.nettrace --payload "ConnectionReset"
```

Use `events --list` to discover exact provider and event names instead of guessing. Filter by
provider, event type, PID, TID, payload, and time window to validate the sequence behind a profile
finding.

## BenchmarkDotNet and disassembly

Use BenchmarkDotNet for isolated methods and pvanalyze for sustained or end-to-end paths. Follow
`SerializerBenchmarkConfig` as the template: `MemoryDiagnoser`, JSON/GitHub exporters,
`DisassemblyDiagnoser` with source and combined reports, and optional hardware counters.

```powershell
dotnet run --project test\Benchmarks\Benchmarks.csproj -c Release -f net10.0 -- `
  suite --filter "*TargetBenchmarks*" --iterationCount 10 --warmupCount 5

$env:ORLEANS_BENCHMARK_HARDWARE_COUNTERS = "1" # Run elevated on Windows.
```

Use short jobs while iterating, then longer paired runs for final numbers. Inspect generated
assembly for interface dispatch, failed inlining, bounds checks, branches, unnecessary loads, and
allocation helpers. `pvanalyze jitstats` measures JIT activity but does not replace generated
assembly inspection. If BenchmarkDotNet cannot export disassembly on the current architecture,
use runtime JIT-disassembly environment variables and preserve the output beside the trace.

## Escalation

Escalate beyond pvanalyze only when its output demonstrates a missing capability:

- use PerfView's GUI for an ETW view which pvanalyze cannot yet express;
- use SpeedScope for interactive flame navigation after exporting from pvanalyze; and
- use ProcMon only when file, registry, process, or other OS activity is part of the hypothesis.

Document why escalation was necessary. Keep the pvanalyze commands and machine-readable output as
the reproducible primary analysis.
