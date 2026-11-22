# AGENTS.md - Durable Storage (Clog)

These rules apply to the `Orleans.Durable.Storage` family: its projects under `src/`, `src/Azure/`, `src/AWS/`, and
`src/Google/`, the verification assets under `test/Durable.Storage/`, and the sample under `playground/DurableStorage/`.
The repository-level guidance also applies. Paths below are relative to the repository root; the `Justfile`, scripts,
and `artifacts/` named here lie under `test/Durable.Storage/`.

Clog is a layered storage system for fine-grained keyed journals: reusable building blocks for durable,
ordered, replicated logs, composed into dyadic key-range ledgers (live ledgers) and the Archive they drain into (an
LSM tree in object storage, or per-key logs on an Orleans journal provider), and used as the storage for Orleans
Journaling.

## Read first

- [Specification](docs/spec/README.md): the normative design. It specifies each layer's contract,
  invariants, data types, and formats, and the compositions built from them. Its
  [implementation status](docs/spec/README.md#5-implementation-status) lists the parts not yet in code.
- [Verification](docs/spec/verification.md): the testing methodology, oracles, models, gates, and
  qualification boundaries.
- [Build and packaging](docs/spec/build-packaging.md): framework, compiler, artifact, and compatibility policy.
- [Implementation plan](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md): the ordered pull requests which take the code to the spec,
  with each row's status.
- [Engineering principles](docs/engineering-principles/README.md): how we design, verify, and write code.

Keep the canonical spec current. A change to behavior, a contract, invariant, data type, durable or wire
format, dependency rule, or resource/failure guarantee updates its owning spec sections in the same
change, together with the affected indexes, traceability, and golden vectors. The spec describes one
current design and its rationale. Record significant choices and material rejected alternatives in
the owning file's Design rationale section. Proposals and open questions live in deferred work and
explicit qualification boundaries; version control preserves the decision history.

## Project status

Clog is a prototype with no deployed clusters or external compatibility commitments. Each API,
configuration schema, wire contract, and durable format has one current implementation. Replace a
superseded representation in place and update its callers, spec, and golden vectors together. A
compatibility obligation requires an explicit spec change before compatibility code is written.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/` | Nine assemblies of the `Orleans.Durable.Storage` family: the Engine (every managed layer, its contracts, and the controllers' decisions), Diagnostics (the fatal-error types), one per technology package (`.FileSystem`, `.AzureStorage`, `.S3`, `.GoogleCloud`, `.CasPaxos`, `.RocksDb`), and the Orleans composition. Inside them, components keep the layering; a layer has an abstractions component only for interfaces several providers implement. The [spec's project map](docs/spec/README.md#26-project-map) lists them. |
| `test/Durable.Storage/` | One test project per layer, plus architecture and model tests; `test/Durable.Storage/shared` holds simulation and diagnostics helpers; `test/Durable.Storage/Orleans.Durable.Storage.Simulation` is the simulation kit which Clockwork simulation projects reference; `test/Durable.Storage/shared-vectors` holds golden vectors. |
| `test/Durable.Storage/specs/` | Quint formal models of the ledger protocol (`test/Durable.Storage/specs/ledger-protocol`: BookKeeper quorum and `ObjectLedger`, both refining `AtomicLedger`), of the ledger map and ledger drops (`test/Durable.Storage/specs/ledgers`), of the LSM (`test/Durable.Storage/specs/archive`), and of archive logs (`test/Durable.Storage/specs/archive-logs`). |
| `test/Durable.Storage/benchmarks/` | BenchmarkDotNet microbenchmarks. |
| `test/Durable.Storage/fuzz/` | The SharpFuzz/libFuzzer decoder fuzzing harness: each layer's targets (`test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz.<Layer>`) over a shared oracle framework, the campaign executable (`test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz`), and each target's corpus (`test/Durable.Storage/fuzz/corpus`). |
| `test/Durable.Storage/apphost/` | Aspire host for the cloud storage emulators. |
| `playground/DurableStorage/` | An Orleans app which runs Clog as its journal storage, and the Aspire host which runs it as three silos over Azurite (`just sample`); `test/Durable.Storage/Orleans.Durable.Storage.Samples.Tests` is its smoke test. |
| `test/Durable.Storage/eng/` | The scripts the `Justfile` recipes run (validation, coverage, fuzzing, mutation testing, documentation checks, and model checking). |
| `test/Durable.Storage/docs/` | The specification (`docs/spec`), and the operations, performance, and engineering-principles documents it cites. |
| `eng/durable-storage/` | The family's build settings, banned APIs, and analyzer configuration, which every Storage project imports. |
| `src/Consensus/`, `src/Simulation/` | The Fast CASPaxos (`Orleans.Consensus.CasPaxos.*`) and Clockwork (`Orleans.Testing.Simulation.*`) families, imported once and built with their own settings. |

A production project's namespaces are those of the components it compiles, each component's namespace starting with
`Orleans.Durable.Storage`, and the Engine's folders follow them; an abstractions
component uses its layer's namespace, and any other project's namespaces start with its name. Each layer keeps its
control-plane types in a `.Control` namespace, which no data-plane type references. Dependencies point down only, and a
component reads another's internals only under its grant; `Orleans.Durable.Storage.Architecture.Tests` enforces the
spec's dependency rules over components.

## Build, format, and test

The [Justfile](Justfile) is the entry point for builds, tests, and every other check; run its recipes from
`test/Durable.Storage/`, and each runs a script under `test/Durable.Storage/eng/` over the Storage solution filter,
`test/Durable.Storage/Durable.Storage.slnf`. Install [just](https://github.com/casey/just) with `winget install Casey.Just`, `cargo install just`, or
`brew install just`; its recipes run in PowerShell 7 (`pwsh`).

```powershell
just restore         # restore the Storage solution filter (test/Durable.Storage/eng/validate.ps1)
just build           # Release build
just format          # verify formatting and analyzer fixes; just format-fix applies them
just test            # default tests, with per-project xUnit reports under artifacts/validation
just test-debug      # Debug build and tests of the Debug leg's projects, so their debug assertions run
just docs            # Markdown style, relative links and anchors, Mermaid syntax (test/Durable.Storage/eng/check-docs.ps1)
just check           # build, format, test, test-debug, docs, the ledger protocol models' quick tier if changed, trace validation
just check-full      # check, with every Quint model's extended bounds
```

The repository's `global.json` selects the SDK. Dot-source `test/Durable.Storage/eng/dotnet-env.ps1` before a direct
`dotnet test` on Linux: it points `TMPDIR` at `artifacts/tmp`, since the WAL refuses tmpfs, which many distributions
mount at `/tmp`.

The simulation projects (`test/Durable.Storage/Orleans.Durable.Storage.Simulation` and each project which sets
`OrleansSimulationInstrumentedTestProject`) and the `src` projects they rewrite build in `SimulationRelease` and
`SimulationDebug`, which the solution maps from Release and Debug and which define `ORLEANS_DURABLE_STORAGE_SIMULATION`;
`just test` and `just test-debug` run them so, and a direct `dotnet` command names the configuration
(`-c SimulationRelease`). A seam which only simulation needs inside `src` is an
`#if ORLEANS_DURABLE_STORAGE_SIMULATION // seam: <name>` block listed in verification §7.2; Debug, Release, and every
shipped artifact hold none ([build and packaging
§1](docs/spec/build-packaging.md#1-framework-and-toolchain-boundaries)).

Several worktrees run their tests on one machine, and every worktree's test hosts share their project's process name.
Stop a stale test host, such as one which locks a build's output, by the process ID of a host under this worktree's
`test/Durable.Storage` directory, which this filter selects on Windows and Linux from any directory of the worktree:

```powershell
Get-Process Orleans.Durable.Storage.* | Where-Object Path -like (Join-Path ([IO.Path]::GetFullPath((git rev-parse --show-toplevel))) test Durable.Storage '*')
```

A host chosen by name alone may belong to another worktree's run, which then ends with no failed test and the exit
code of a terminated process: -1 on Windows, 137 (`SIGKILL`) or 143 (`SIGTERM`) on Linux.

`just format-fix` applies analyzer and style fixes. When an analyzer has no reliable fixer, write a small
repository-local tool for the mechanical change rather than suppressing the rule.

```powershell
just caspaxos               # the Fast CASPaxos adapter, its shared simulation, and the architecture suite
just models                 # ledger protocol Quint models' default set (test/Durable.Storage/eng/model-check-quint.ps1: -Tier, -Since, -Only, -Workers)
just models-quick           # their quick tier: small exhaustive cases, every mutant and witness
just quint-models           # Quint models of the ledger protocol, Ledgers, the LSM, and archive logs (test/Durable.Storage/eng/model-check-quint.ps1: -Model, -Tier, -Since, -Only, -Workers)
just ledger-traces          # the simulations' ledger traces replayed by quint test, with trace mutants (test/Durable.Storage/eng/ledger-traces.ps1; a seed range)
just archive-log-traces     # the archive-log drain simulation's traces replayed by quint test (test/Durable.Storage/eng/archive-log-traces.ps1; a seed range)
just ledger-map-traces      # the ledger map simulations' traces replayed by quint test, with trace mutants (test/Durable.Storage/eng/ledger-map-traces.ps1)
just archive-traces         # the LSM simulation's traces replayed by quint test, with trace mutants (test/Durable.Storage/eng/archive-traces.ps1)
just coverage               # Cobertura coverage, CRAP >= 30 hotspots, HTML report (test/Durable.Storage/eng/coverage.ps1)
just fuzz all 60            # libFuzzer campaigns per decoder (test/Durable.Storage/eng/fuzz.ps1; x64 Windows or Linux; -Merge keeps the corpus)
just explore 1..500         # the simulation corpora over a seed range (CLOG_SIM_SEEDS)
just explore-schedules       # the simulated conformance classes' windows explored at CLOG_SCHEDULE_BOUND=deep (a class filter optional)
just emulators              # the cloud stores' suites and the Orleans interop tests on the AppHost's emulators (Docker; CLOG_EMULATORS=1)
just e2e                    # the Orleans composition's multi-silo scenarios, the smoke set and the explicit rest
just bench '*' --job Dry    # the benchmarks a required filter selects (test/Durable.Storage/eng/bench.ps1)
just sample                 # the sample Orleans app: three silos over Azurite, with the Aspire dashboard (Docker; playground/DurableStorage/README.md)
just mutation-since main     # Stryker.NET on the core units' changes since a commit (test/Durable.Storage/eng/mutation.ps1); deferred (plan decision 16)
just mutation-core          # Stryker.NET on the core units in full against test/Durable.Storage/eng/mutation-baseline.json; milestones
just mutation Orleans.Durable.Storage.Engine.Ledgers.Store # Stryker.NET on the named units, each a component
```

## Conventions

- **Git:** linear history. Rebase before integrating and update branches by fast-forward only; merge
  commits are prohibited. Commits are focused and use Conventional Commit messages.
- **Frameworks:** every project, the Aspire AppHost and the Clockwork simulation test projects among them, targets
  `net11.0` alone. Take the Microsoft.Extensions abstractions from the shared framework, never as package references.
  The consensus and simulation families keep their own build settings.
- **Compiler and analyzers:** nullable reference types, preview language features, strict features, and
  the .NET SDK's analyzers and `Microsoft.CodeAnalysis.BannedApiAnalyzers`, with warnings as errors. Every `src`
  project builds with the trim, AOT, and single-file analyzers. Fix a warning with source generation, a static
  catalog, or a precise annotation;
  never suppress one (no `UnconditionalSuppressMessage`, blanket `NoWarn`, or hiding trimmer roots).
- **Time:** inject `TimeProvider` (tests use `FakeTimeProvider`).
- **Buffers:** a large payload, one whose contract states no fixed bound of at most 64 KiB, crosses an API as an
  `ArcBuffer`, or a type which owns one, where the callee may keep the bytes or the API returns them, and as a
  `ReadOnlySequence<byte>` where the callee reads it only until the operation completes. At an external library's
  boundary, use that library's native type, such as `Stream`, `BinaryData`, or `ByteString`; `WalFileSystem` and the
  I/O engines keep the BCL's `RandomAccess` shapes. Small bounded values (keys, tags, IDs) cross as
  `ReadOnlyMemory<byte>` or arrays, `default(ArcBuffer)` is the empty payload, and synchronous work on small values or
  on bytes a component holds takes `ReadOnlySpan<T>` ([conventions
  §1](docs/spec/conventions.md#1-buffers-and-ownership)). Keep hot paths allocation-conscious.
- **Wire contracts:** Orleans serialization carries the Orleans composition's replica traffic, and generated
  serializers stay out of domain abstractions; Fast CASPaxos is reached through a caller-owned register service, and
  remote voters over Orleans RPC.
- **Retries:** use a client SDK's built-in retries, otherwise `Microsoft.Extensions.Resilience` and Polly
  with bounded jittered backoff, cancellation, and an idempotency analysis per operation.
- **Durable answers:** a crash never revokes an answer. What an answer asserts (a write, a seal, a drop, a commit, a
  read value, an absence, a tail) is durable before the answer leaves its component, and an operation which meets an
  in-flight change waits for its durable outcome ([conventions §13](docs/spec/conventions.md#13-durable-answers)).
- **Local storage failures stop the process:** a failed write, flush, rename, or read of local storage is reported by
  its storage seam (`WalFileSystem`, the io_uring storage, `RocksDbJournalIndex`) to the static fatal-error handler
  (`Orleans.Durable.Storage.Diagnostics.FatalErrorHandler`), and a defect which would leave a component's memory out of
  step with its write-ahead log is reported where the component detects it. The handler writes the report and exits at
  once with the site's own exit code. Components never end the process themselves, recover in place only when it is
  trivially simple and safe, and never retry a failure or stay alive in a degraded state. Tests take the reports they
  cause from `FatalErrors`, which intercepts the stop ([conventions
  §14](docs/spec/conventions.md#14-local-storage-failures-stop-the-process)).
- **Outcomes and unions:** an operation returns as values the answers its caller decides on, and throws those its
  caller only propagates. Absence is `T?`; an outcome is a C# union, a `union` declaration with nested `sealed record`
  cases, or a hand-written non-boxing union on per-record and per-slot paths. A decoder returns a union of its value and
  the `InvalidDataException` naming its rejection; a per-record decoder returns that exception, or null when the input
  is valid. A union switch names every case and ends with a throwing `null` arm; a verdict is named `…Outcome`. Each
  shape comes from the platform and maps onto Rust for the Roux port
  ([conventions §15](docs/spec/conventions.md#15-outcomes-unions-and-errors)).
- **Citations in code:** cite the spec's invariant IDs (for example `LG-I5`). Code which implements a
  paper-defined algorithm or invariant quotes the paper verbatim, citing the paper and its section (`research/papers/<paper>`, in Clog's repository at the snapshot
  `PROVENANCE.md` names).
- **Requirements are explicit.** A concern becomes a requirement only through a supported use case, a layer
  contract, a paper-defined invariant, a realistic failure mode, or a measurement. Each layer validates
  only the invariants it owns.
- **Dependencies:** use the BCL and Microsoft.Extensions (hosting, dependency injection, options, logging, metrics,
  resilience, `TimeProvider`, object pools, hashing) before building our own; a Clog component replaces one only for a
  requirement it cannot meet, recorded with the rationale. Prefer stable packages, versioned centrally in the repository's `Directory.Packages.props`; the Orleans composition
  references Orleans' projects in this repository ([build and packaging §2](docs/spec/build-packaging.md#2-source-and-dependency-policy)).
  Copied source (as `ArcBuffer` from Orleans) is adapted into Clog namespaces and maintained as Clog code with its own
  tests and attribution.

## Testing

[Verification](docs/spec/verification.md) is the normative testing methodology; in summary:

- **Oracles come from the spec**, written once per contract and reused at every layer: an Accordant spec per
  stateful contract, invariant checkers, the fold of the history for every optimization, and per-journal
  linearizability (the Porcupine algorithm in `test/Durable.Storage/shared`). Generators (Accordant, CsCheck, simulation
  workloads, hand-written scenarios) choose sequences; the oracle decides.
- **Layers:** Quint models with a mutant per invariant; xUnit v3 unit tests and golden vectors; Verify
  snapshots (pinned at 32.0.0); CsCheck properties, including metamorphic ones; Accordant conformance through the
  CsCheck ⇄ Accordant kit, whose cases run each in a Clockwork simulation of its own where a contract's target can, with
  their concurrent windows' schedules explored within a preemption bound and a race mutant per explored contract
  (verification §4.1; `CLOG_SCHEDULE_BOUND=deep` at milestones, `CLOG_SCHEDULE` replays a schedule); deterministic
  simulation with fault injection on Clockwork;
  integration over real boundaries and Aspire emulators; `Orleans.TestingHost` end-to-end; Stryker.NET mutation
  testing (core units break below 80%, or below the measured score `test/Durable.Storage/eng/mutation-baseline.json` holds while a gap
  closes; reviewed equivalent mutants in the same file; each unit a component, configured by a
  `stryker-config.<Component>.json` beside the `src` project which compiles it, unless that project builds on
  the consensus or simulation family); and soak runs.
- **Also adopted:** trace validation against the models, coverage-guided fuzzing of decoders, crash-state enumeration,
  and leak checks where they matter most: every test class of the Buffers, the WAL, Ledgers, MetaStore, CASPaxos,
  Archive, archive-log, state database, cloud, and model test projects holds an `ArcBufferLeakCheck`
  (`test/Durable.Storage/shared/LeakChecks`) and releases every buffer it owns, every `MetaStoreEntry` among them, before teardown,
  unless it carries `[NoPageLeakCheck(reason)]`; a property runs each case in `_leaks.InScope(() => ...)` or `_leaks.InScopeAsync(...)`;
  `CLOG_TEST_LEAK_TRACES=1` reports where each leaked page was rented.
- **Gates:** every change runs the build, formatting, unit, snapshot, property, and conformance
  tests, the simulation seed corpus, the Debug leg, and affected models; milestones add exploration, full mutation,
  fuzzing campaigns, emulators, and end-to-end tests; soak runs are reserved for final validation of the finished
  system.
- **Expensive work runs after hours, off the critical path:** mutation runs (incremental ones included), seed
  exploration, fuzzing campaigns, extended model runs, and quiet-machine benchmarks run in the nightly window, and a
  pull request lands without waiting for them; their findings are fixed in follow-up pull requests (verification §12).
  Until the implementation is complete, [decision 16](https://github.com/ReubenBond/clog/blob/6ffb29aa96d2e4d48fc9c34f80790493e548af96/docs/implementation-plan.md#decisions) defers them.
- **Debug assertions:** Release builds compile `Debug.Assert` away, so the Debug leg (`just test-debug`) builds its
  projects in Debug so their debug assertions run. A failed assertion throws `DebugAssertionException` at its site and
  is recorded, so it fails its seeded simulation at its step with its seed, its test, or its test assembly, even when
  the code under test catches the exception (verification §11.2). A test project whose code under test gains debug
  assertions, or whose tests check that no pages are read after their buffer's release (only Debug builds poison
  returned pages), joins the leg by setting `ClogDebugLeg` in its project file;
  `Orleans.Durable.Storage.Architecture.Tests` checks that the leg reaches every `src` project with debug assertions.
- Add a regression test for every bug. Every spec invariant links to a test, model, or golden vector in
  [the traceability table](docs/spec/invariants.md). Maintain the models with the protocols they model.
- Every randomized or simulated test replays from its seed (`CLOG_TEST_SEED`); every run writes crash, hang, and
  failure dumps, traces, and simulation histories to its results directory.
- Judge coverage by branch coverage, CRAP scores, mutation scores, and the quality of assertions, not line count.

## Performance

- Claim an optimization only with measurements: throughput, latency distributions, and allocations.
- Permanent microbenchmarks live in `test/Durable.Storage/benchmarks/Orleans.Durable.Storage.Benchmarks`. Validate new ones with `--job Dry`,
  always pass a filter, and compare against an explicit baseline.

```powershell
just bench '*' --job Dry
```
