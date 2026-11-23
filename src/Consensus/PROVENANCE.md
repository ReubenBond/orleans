# Provenance

This directory holds the `Microsoft.Orleans.Consensus.CasPaxos` package family: the transport-neutral Fast CASPaxos
kernel, the replicated register services, and durable SQLite acceptors and ballot allocators. The consensus-owned
simulation, scenarios, simulation runner, and shared register test fixture, with the family's test suites, are under
`test/Consensus`.

## Source

| Item | Value |
| --- | --- |
| Repository | [ReubenBond/fast-caspaxos](https://github.com/ReubenBond/fast-caspaxos) |
| Revision | [`436cc0fbcecb81d9252a5e6119741f86ead359df`](https://github.com/ReubenBond/fast-caspaxos/commit/436cc0fbcecb81d9252a5e6119741f86ead359df) (22 November 2026) |
| Import form | Initial snapshot at [`4dad617353`](https://github.com/ReubenBond/fast-caspaxos/commit/4dad617353f6251e3c2030872e5bb5f4a5560df9), with subsequent commits [`ddcca54667`](https://github.com/ReubenBond/fast-caspaxos/commit/ddcca54667cba652a16ee94f31164125762b3b97) (reconverged from Orleans `49ec166dd6`) and [`67d6a14f34`](https://github.com/ReubenBond/fast-caspaxos/commit/67d6a14f347485ebdd9fae2a1342a49d1a8ebfc8) (voter reconfiguration) ported; [`436cc0fbce`](https://github.com/ReubenBond/fast-caspaxos/commit/436cc0fbcecb81d9252a5e6119741f86ead359df) is Quint-only, which remains upstream |
| License | MIT; [LICENSE](LICENSE) is the upstream notice |

ReubenBond/fast-caspaxos remains the authoritative repository for this family until the integration is accepted into
`dotnet/orleans`. Changes which land upstream after the recorded revision are ported onto the integration branch, each
naming its upstream commit, and this file records the revision the tree then corresponds to.

## Layout

| Upstream path | Orleans path |
| --- | --- |
| `csharp/src/Orleans.Consensus.CasPaxos{,.Server,.Sqlite}/` | `src/Consensus/` |
| `csharp/src/Orleans.Consensus.CasPaxos.{Simulation,Simulation.Scenarios,Simulation.Runner,Testing}/` | `test/Consensus/` |
| `csharp/tests/Orleans.Consensus.CasPaxos{,.Server,.Sqlite,.Simulation}.Tests/` | `test/Consensus/` |
| `LICENSE` | `src/Consensus/LICENSE` |

The simulation, scenario, runner, and testing projects ship no package, so they sit with the test infrastructure, as
Orleans' test-support projects do.

These remain upstream and are not imported: the standalone gRPC, REST, and host projects and their tests, the gRPC smoke
sample, the vendored Clockwork submodule and the build files which mapped it (`csharp/eng/OrleansSimulation.targets`,
`vendor/`), the solution, scripts, and workflows, and the repository's Rust, Python, Go, Quint, TLA+, paper, and
administration UI sources. Storage reaches consensus through Orleans RPC in a later integration phase.

## Integration changes

The import keeps the upstream code and test oracles. These changes adapt it to Orleans' build:

- `src/Consensus/Directory.Build.props` marks the family as outside the Orleans framework parts (no code generation,
  framework-part attribute, or contracts analyzer) and gives its packages the Durable family's `alpha.1` label and empty
  package-validation baseline. The kernel, server, and SQLite packages carry package descriptions and the upstream
  LICENSE; the server and SQLite packages also carry their READMEs.
- The simulation projects reference the in-tree simulation family (`src/Simulation`) directly, so the build has one
  simulation kernel; upstream supplied it from a Clockwork submodule. `Microsoft.Data.Sqlite` takes Orleans' central
  version (11.0.0, as upstream pins). The SQLite package no longer grants internals to the gRPC tests, which remain
  upstream.
- Under Orleans' documentation generation, a cref in `ProposerDiagnosticEvents` names its type's namespace.
- The test suites move from xUnit 2 on VSTest to Orleans' xUnit 3 on the Microsoft Testing Platform:
  `ITestOutputHelper` comes from `Xunit`, `LinuxFactAttribute` forwards its source location (xUnit3003), and calls which
  accept a cancellation token pass the test's token (xUnit1051). Calls inside a simulation pass `CancellationToken.None`,
  so no test-host callback runs outside the simulation, and async enumerators take their token through the iterator's
  own parameter, which the journal tests' cancellation oracles observe.
- Each suite carries Orleans' CI traits (`Area=Consensus`, `Provider=None`, `Suite=BVT`). The shared fixture's README
  describes the in-tree simulation kernel.
