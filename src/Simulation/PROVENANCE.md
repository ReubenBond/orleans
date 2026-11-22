# Provenance

This directory holds the `Microsoft.Orleans.Testing.Simulation` package family: the deterministic simulation kernel,
its IL instrumentation engine and MSBuild integration, analyzers, framework-neutral and Microsoft Testing Platform test
support, and the `dotnet-orleans-simulation` tool. Their test suites are under `test/Simulation`.

## Source

| Item | Value |
| --- | --- |
| Repository | [ReubenBond/Clockwork](https://github.com/ReubenBond/Clockwork) |
| Revision | [`d1e492649ed028991763bd9764d969206e27866d`](https://github.com/ReubenBond/Clockwork/commit/d1e492649ed028991763bd9764d969206e27866d) (merge of ReubenBond/Clockwork#75, 18 November 2026) |
| Import form | Snapshot of that revision's tree (`git archive`), committed as one change |
| License | MIT; [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) are the upstream notices |

ReubenBond/Clockwork remains the authoritative repository for this family until the integration is accepted into
`dotnet/orleans`. Changes which land upstream after the recorded revision are ported onto the integration branch,
each naming its upstream commit, and this file records the revision the tree then corresponds to.

## Layout

| Upstream path | Orleans path |
| --- | --- |
| `src/Orleans.Testing.Simulation*/` | `src/Simulation/Orleans.Testing.Simulation*/` |
| `tests/Clockwork.*.Tests/` | `test/Simulation/Clockwork.*.Tests/` |
| `docs/` | `src/Simulation/docs/` |
| `README.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md` | `src/Simulation/` |
| `eng/Microsoft.Orleans.Testing.Simulation.SourceBuild.props` | `eng/Microsoft.Orleans.Testing.Simulation.SourceBuild.props` |

The source-build map keeps its upstream location relative to the repository root, so a build which names an Orleans
checkout as its `OrleansSimulationSourceRoot` resolves the same seven packages to their source projects.

Upstream's `benchmarks/Clockwork.Benchmarks`, GitHub workflow, solution, and root build files (`Directory.Build.props`,
`Directory.Packages.props`, `global.json`) are not imported; Orleans' build, central package versions, and SDK
selection replace them.

## Integration changes

The import keeps the upstream code, identities, and test oracles. These changes adapt it to Orleans' build:

- `src/Simulation/Directory.Build.props` marks the family as outside the Orleans framework parts (no code generation,
  framework-part attribute, or contracts analyzer) and gives its packages the Durable family's `alpha.1` label and empty
  package-validation baseline. Package versions follow Orleans' versioning rather than upstream's `0.1.0`; assembly
  versions follow the package version, as upstream's do, because the engine records its assembly version in manifests
  and idempotence markers. The analyzer, build-task, and tool packages, which ship no `lib/` assets, skip package
  validation. The package smoke tests expect Orleans' package metadata (project URL and package icon) in place of
  upstream's repository URL.
- Package references take Orleans' central versions, which add `Microsoft.Build.Framework`,
  `Microsoft.Build.Utilities.Core`, and `Mono.Cecil` at upstream's versions. The instrumentation tests name their
  `Microsoft.NET.ILLink.Tasks` version locally, since other Orleans projects reference it implicitly. The test
  projects take Orleans' xUnit and Microsoft Testing Platform versions from `test/Directory.Build.props`, and run under
  workstation garbage collection, as upstream's do, for their heap-measuring oracles.
- Under Orleans' analyzers, `ControlledPeriodicTimer` locks a private object instead of `this` (CA2002). The
  `Task.Wait`/`WaitAll`/`WaitAny` shims keep their zero-timeout BCL calls, which reproduce the BCL's result and
  exception semantics after the controlled scheduler completes the wait. The kernel therefore takes its own
  `BannedSymbols.txt` in place of Orleans' blocking-API list; it permits exactly those three non-blocking overloads.
  `BlockingEnumerableConformanceTests` passes the test's cancellation token explicitly
  (xUnit1069). `dotnet format whitespace` normalizes indentation and final newlines.
- Tests locate the repository by `Orleans.slnx` and use the Orleans paths above. Each suite carries Orleans' CI traits
  (`Area=Simulation`, `Provider=None`, and `Suite=BVT`, or `SlowBVT` for the instrumentation and conformance suites),
  and runs with collection-level parallelism, as upstream's do, through `test/Simulation/testconfig.json` in place of
  Orleans' sequential configuration; `RethrownExceptionStackTraceMatchesTheBclAwaiter` fails under sequential order
  upstream as well (ReubenBond/Clockwork#79). The `Simulation packaging and lowering` CI job runs the package smoke tests
  and the Debug and untiered conformance lanes of upstream's CI.

## Instrumented test projects

A test project under `test/` opts into instrumentation from source with `OrleansSimulationInstrumentedTestProject=true`
and a rule-set selection (`OrleansSimulationUseBuiltInRules`, `OrleansSimulationBuiltInRuleSet`, or
`OrleansSimulationRuleSet`). `test/Directory.Build.targets` then imports `eng/OrleansSimulation.targets`, which supplies
what the packages supply a package consumer: the kernel, the framework-neutral and Microsoft Testing Platform test
support and its builder hook, and the instrumentation props and targets, whose task assembly the build compiles and runs
in a task host. Test-host infrastructure (`TestUtilities.dll` and the dump extensions' `Microsoft.Diagnostics.*`
assemblies) keeps ordinary IL; a project adds its own `OrleansSimulationExclude` items for further ordinary-IL
dependencies. `test/Simulation/Orleans.Simulation.SourceBuild.Tests` proves the boundary: its dependency is rewritten,
while the test assembly and the dependency's own build output keep ordinary IL.
