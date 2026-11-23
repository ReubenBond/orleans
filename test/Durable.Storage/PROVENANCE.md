# Provenance

The `Orleans.Durable.Storage` family was imported from [Clog](https://github.com/ReubenBond/clog) at commit
[`6ffb29aa96d2e4d48fc9c34f80790493e548af96`](https://github.com/ReubenBond/clog/commit/6ffb29aa96d2e4d48fc9c34f80790493e548af96)
as a snapshot. Clog's history remains in its repository. Clog's author, Reuben Bond, contributes the snapshot under
this repository's license. Clog remains authoritative for this code until the integration is accepted into
`dotnet/orleans`; changes landing in Clog after the snapshot are ported onto the integration branch.

## Ported changes

Clog changes which landed after the snapshot, ported onto the integration branch:

| Clog commit | Change |
| --- | --- |
| [`673755c40962ddad9ae6afbfa84583a68169090d`](https://github.com/ReubenBond/clog/commit/673755c40962ddad9ae6afbfa84583a68169090d) | `IndexedReadPropagatesNonCallerOperationCanceledExceptionFromSlotRead` reaches its canceling object store (ReubenBond/clog#465) |
| [`73e5b2d21`](https://github.com/ReubenBond/clog/commit/73e5b2d21) | `LostAnswerCreateSettlesThroughProperties` asserts fake-gcs-server's unknown outcome and its repetition's `Exists` (ReubenBond/clog#468) |

## Layout

| Clog path | Orleans path |
| --- | --- |
| `src/Orleans.Durable.Storage`, `.Engine`, `.Diagnostics`, `.FileSystem`, `.RocksDb`, `.CasPaxos` | `src/<project>` |
| `src/Orleans.Durable.Storage.AzureStorage` | `src/Azure/Orleans.Durable.Storage.AzureStorage` |
| `src/Orleans.Durable.Storage.S3` | `src/AWS/Orleans.Durable.Storage.S3` |
| `src/Orleans.Durable.Storage.GoogleCloud` | `src/Google/Orleans.Durable.Storage.GoogleCloud` |
| `tests/` | `test/Durable.Storage/` |
| `fuzz/`, `benchmarks/`, `apphost/`, `specs/`, `eng/` | `test/Durable.Storage/fuzz/`, `benchmarks/`, `apphost/`, `specs/`, `eng/` |
| `docs/spec/`, `docs/operations/`, `docs/performance/`, `docs/engineering-principles/` | `test/Durable.Storage/docs/` |
| `samples/` | `playground/DurableStorage/` |
| `Justfile`, `README.md`, `AGENTS.md`, `BannedSymbols.txt`, `.markdownlint*.jsonc` | `test/Durable.Storage/` |

## Not imported

| Clog path | Reason |
| --- | --- |
| `vendor/` | Clockwork and Fast CASPaxos are imported once, as the `Microsoft.Orleans.Testing.Simulation.*` and `Microsoft.Orleans.Consensus.CasPaxos.*` families. |
| `eng/Submodules.targets`, `eng/OrleansSimulation.SourceBuild.targets`, `eng/Orleans.Consensus.CasPaxos.props`, `eng/sync.ps1`, `eng/test-submodule-guard.ps1`, `eng/install-dotnet.*`, `eng/dotnet-env.ps1` | Submodule and SDK bootstrap; Orleans builds those families from source and selects the SDK through its `global.json`. |
| Root build, package, solution, SDK, editor, and Git configuration; `**/packages.lock.json` | Orleans' build, central package management, solution, and repository configuration govern the imported projects. |
| `.github/`, `.config/dotnet-tools.json` | Orleans' CI and tool manifests. |
| `docs/implementation-plan.md`, `docs/rust-migration-plan.md`, `clogd.jsonc*` | Clog's planning records and local tool state. |
| `research/` | Third-party papers. Code and specification citations of `research/papers/<paper>` name the papers in Clog's repository at the snapshot revision. |

## Third-party notices

The imported code retains its notices:

- `test/Durable.Storage/shared/Linearizability/` is ported from [Porcupine](https://github.com/anishathalye/porcupine)
  (MIT, `PORCUPINE-LICENSE.md`).
- `test/Durable.Storage/specs/ledger-protocol/` derives from Jack Vanlightly's BookKeeper models (MIT, `LICENSE`).
- `src/Orleans.Durable.Storage.Engine/Threading/AdmissionGate.cs` and its tests carry the .NET Foundation notice.
