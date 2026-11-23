# Orleans integration record

This record completes the [Clog to Orleans integration plan][plan]'s initial integration (phases I2, I1, I3, I4, and
I5; plan §13). It records the integrated revisions, the imported sources, the packages and their API and wire
artifacts, each phase's evidence, the qualified limitations which remain, and the operating procedures for the
mixed-provider deployment: application journals on Durable Storage, Durable Jobs on a named Azure journal provider.
The [specification](spec/README.md) describes the integrated design.

## 1. Revisions

The stack is linear on `ReubenBond/orleans`, from `dotnet/orleans` main at
[`6a0e937f544c35ffcf11b37e4c2b7de5a1580a03`][orleans-base].

| Phase | Branch | Commits | Head |
| --- | --- | --- | --- |
| I2: simulation and consensus families | `rb-import-simulation-and-consensus` | `66b16270e0`, `cfe412e5fc` | `cfe412e5fc` |
| I1: Durable Storage | `rb-import-durable-storage` | `e34d5d1c8e` to `fb4bf66e8d` | `fb4bf66e8d` |
| I3: native CASPaxos hosting | `rb-i3-native-caspaxos-hosting` | `49ec166dd6`, `010df6a432` | `010df6a432` |
| I4: Azure-backed Jobs staging | rebased onto I3 in I5's branch | `d34d4412a5`, `b7b3e5971f`, `415dfcd51c`, `640823442c` | `640823442c` |
| I5: application and publication qualification | `rb-i5-qualify-durable-storage-integration` | from `84404a06f8` to the branch head | the branch head |

I4 was developed on I1 (`79379d018a` to `f5d311ee1c`) and replayed onto I3 without conflicts.

## 2. Imported sources

Each family is a snapshot of a recorded revision with its provenance and license notices. Clog, Clockwork, and Fast
CASPaxos remain authoritative until the integration is accepted into `dotnet/orleans`; changes landing upstream after
the snapshot are ported onto the stack.

| Family | Source revision | Orleans paths | Provenance |
| --- | --- | --- | --- |
| Durable Storage | [Clog `6ffb29aa96d2e4d48fc9c34f80790493e548af96`][clog] | `src/Orleans.Durable.Storage*`, `src/{Azure,AWS,Google}/Orleans.Durable.Storage.*`, `test/Durable.Storage`, `playground/DurableStorage` | [PROVENANCE.md](../PROVENANCE.md) |
| Consensus | [Fast CASPaxos `4dad617353f6251e3c2030872e5bb5f4a5560df9`][caspaxos] | `src/Consensus`, `test/Consensus` | `src/Consensus/PROVENANCE.md` |
| Simulation | [Clockwork `d1e492649ed028991763bd9764d969206e27866d`][clockwork] | `src/Simulation`, `test/Simulation` | `src/Simulation/PROVENANCE.md` |

## 3. Packages and artifacts

Every package carries the Durable family's `alpha.1` suffix and validates against an empty baseline until its first
release. Storage, consensus, and the simulation kernel target `net11.0`; the simulation analyzers target
`netstandard2.0`.

| Package | Role | Direct dependencies beyond the family |
| --- | --- | --- |
| `Microsoft.Orleans.Durable.Storage` | Journal storage provider, silo hosting, placement, replica transport, control grains | Orleans runtime, SDK, and `Microsoft.Orleans.Durable`; the Engine at its exact version |
| `Microsoft.Orleans.Durable.Storage.Engine` | Managed storage layers and their contracts | `Polly.Core`, `System.IO.Hashing` |
| `Microsoft.Orleans.Durable.Storage.Diagnostics` | Fatal-error reporting and process termination | None |
| `Microsoft.Orleans.Durable.Storage.FileSystem` | File-system object store and metadata registers | None |
| `Microsoft.Orleans.Durable.Storage.RocksDb` | RocksDB journal indexes | `RocksDB` |
| `Microsoft.Orleans.Durable.Storage.CasPaxos` | Native CASPaxos metadata registers hosted on silos | The consensus server and SQLite packages; Orleans runtime and core at exact versions |
| `Microsoft.Orleans.Durable.Storage.AzureStorage` | Azure Blob objects and archive logs, Azure Table and Blob metadata registers | `Azure.Storage.Blobs`, `Azure.Data.Tables` |
| `Microsoft.Orleans.Durable.Storage.S3` | S3 objects, DynamoDB metadata registers | `AWSSDK.S3`, `AWSSDK.DynamoDBv2` |
| `Microsoft.Orleans.Durable.Storage.GoogleCloud` | Cloud Storage objects, Firestore metadata registers | `Google.Cloud.Storage.V1`, `Google.Cloud.Firestore` |
| `Microsoft.Orleans.Consensus.CasPaxos`, `.Server`, `.Sqlite` | Transport-neutral Fast CASPaxos kernel, register service, SQLite persistence | `Microsoft.Data.Sqlite` (`.Sqlite`) |
| `Microsoft.Orleans.Testing.Simulation`, `.Analyzers`, `.Instrumentation`, `.Instrumentation.Build`, `.Testing`, `.Testing.MTP`, `.Tool` | Deterministic simulation kernel, analyzers, IL instrumentation, build integration, test support, and tool | `Mono.Cecil` (`.Instrumentation`), `Microsoft.Testing.Platform` (`.Testing.MTP`) |

Artifacts for review before the first release:

- **Public API:** `src/api` holds each package's generated surface, from the GenAPI target CI uses.
- **Grain contracts:** `OrleansContracts.txt` in `src/Orleans.Durable.Storage` and `src/Orleans.Durable.Storage.CasPaxos`
  records the wire identity of each grain, grain interface, and system target; the build holds the grains to it.
- **Wire:** the Orleans composition pins each grain message's encoding in Verify snapshots (`ControlMessageTests`,
  `JournalMessageTests`, `NodeRegistryMessageTests`, and the replica and forwarding message tests in
  `Orleans.Durable.Storage.Tests`), and `OrleansWireBoundaryTests` confines Orleans' buffers to the wire boundary's
  types. The persisted formats are specified in [formats](spec/formats.md), with golden vectors in
  [`shared-vectors`](../shared-vectors) and decoder corpora under [`fuzz`](../fuzz).
- **Closures:** packed into an isolated feed in I5, the Engine and the technology packages carry no Orleans runtime or
  code generation; only the root package and the CASPaxos integration take Orleans' runtime. No Storage or consensus
  closure holds ASP.NET Core or a simulation asset. gRPC appears only in the Google Cloud package's closure, through the
  Firestore SDK. No packed production assembly holds the simulation build's assembly metadata or the instrumentation's
  rewrite signature; both are present in the simulation builds, which checks the check.

## 4. Evidence

Each phase ran its affected build, formatting, and tests on the local machine. I5 reran the combined stack.

| Phase | Evidence |
| --- | --- |
| I2 | One source copy of each family; the simulation and consensus suites under Orleans' xUnit 3 and Microsoft Testing Platform with `Area=Simulation` and `Area=Consensus` CI traits; the packaging, collision, ordinary-IL, and incremental checks of `Clockwork.Instrumentation.Tests`' package smoke tests, which CI's simulation job runs |
| I1 | The nine Storage projects over Orleans' build with direct project references; the journal adapter on `Orleans.Durable.Journaling`; the component and dependency rules of `Orleans.Durable.Storage.Architecture.Tests` over the Orleans layout; Storage's simulations on the in-tree simulation family |
| I3 | `Orleans.Durable.Storage.CasPaxos.Tests` and `Orleans.Consensus.CasPaxos.Sqlite.Tests` over real SQLite voters; `OrleansNativeConsensusTests`: three voter silos over Orleans RPC through voter loss, restart at the same endpoint, and a refused voter whose state was lost |
| I4 | `MixedProviderDurableJobsTests` on Azurite: shard creation, discovery, claim, release, adoption, retry and cancellation provider affinity, and recovery in the named Azure provider beside Storage application journals; the job manager system target's registration fix |
| I5 | The runs below |

I5 ran on Windows 11 Enterprise 10.0.26300 (x64; Xeon Platinum 8370C, 16 cores, 128 GB) with the .NET SDK 11.0.100
and Docker 29.9.3:

| Run | Result |
| --- | --- |
| `dotnet build Orleans.slnx` (Debug), after the central version changes and again at the branch head | Succeeded, no warnings |
| `test/Durable.Storage/eng/validate.ps1 -Check Build` (Release) | Succeeded, no warnings |
| Every Storage test project, Release (simulation projects in `SimulationRelease`) | All passed; ReubenBond/clog#465's test, then failing, excluded The Orleans composition took 16 minutes, the simulated conformance classes 8, the ledgers 4, the seed corpus 2, the WAL 1.5 |
| Every Storage test project, Debug (simulation projects in `SimulationDebug`), as CI runs them | All passed |
| `MixedProviderApplicationTests` and `MixedProviderDurableJobsTests` on Azurite 3.35.0 (the CI image) | All passed, over the in-memory and the native CASPaxos MetaStore |
| `validate.ps1 -Check Emulators`: the cloud and interop suites, Release and Debug, on the AppHost's emulators through Aspire and Docker | All passed but `GcsObjectLedgerFidelityTests.LostAnswerCreateSettlesThroughProperties` on fake-gcs-server ([ReubenBond/clog#468][clog-468]); the live-account cases skipped for their unset variables. With #468's fix ported, the cloud suite passes in Release and Debug: 275 passed, 54 live-account cases skipped |
| `validate.ps1 -Check Format` | Passed, after ordering twenty files' imports |
| `Clockwork.Instrumentation.Tests`' package smoke tests (`ORLEANS_SIMULATION_SMOKE_TESTS=1`), as CI's simulation job runs them | All 47 passed |
| Orleans suites using Verify and CsCheck after the central version changes: `Orleans.CodeGenerator.Tests`, `Orleans.Journaling.Tests`, `Orleans.Journaling.Json.Tests`, `Orleans.Serialization.UnitTests`, `Orleans.Core.Tests` | All passed, with one snapshot's trailing newline corrected for Verify 32 |
| `test/Durable.Storage/eng/check-docs.ps1` | Passed |
| Packing and closure checks of every new package | As section 3 records |
| `Orleans.Durable.Storage.Engine.Archive.Tests`, Release and Debug, with [ReubenBond/clog#465][clog-465]'s fix ported | 175 passed, 4 skipped, none failed |

## 5. Qualified limitations

| Limitation | Status |
| --- | --- |
| [ReubenBond/Clockwork#79][clockwork-79]: `RethrownExceptionStackTraceMatchesTheBclAwaiter` depends on test execution order | Passes under the collection-level parallelism the simulation suites run with |
| A single-voter CASPaxos deployment admits its own voter, so it cannot detect a voter restarted on a lost database | Deploy three or five voters; [operations](operations/caspaxos.md#failures-operators-observe) records it |
| Replacing a lost voter, and changing the voter set | [Deferred work](spec/deferred.md) |
| The Engine's public surface holds its layers' contracts and implementations (387 public types in 26 namespaces) | Review before the first release, with Diagnostics' public `FatalErrorHandler.Interceptor` test seam |
| The specification's prose names the engine Clog | Build and test settings, shipped source, project descriptions, and the sample use the Durable Storage name |
| The cloud and interop suites' emulator cases have no CI lane | Run locally with `just emulators` |
| CI's Linux lanes for the Storage suites | Not run locally |
| Seed and schedule exploration, mutation runs, fuzzing campaigns, extended and nightly model tiers, LazyFS crash soaks, the explicit end-to-end set, soak, benchmarks, and Native AOT publication | Not run in I5; they belong to the after-hours window (verification §12) |

## 6. Operating procedures

The deployment's silos each host one Durable Storage node; Durable Jobs keeps its shards in a named Azure Blob journal
provider; membership, the MetaStore, and the archive store are shared by every silo.

```csharp
builder.UseOrleans(silo => silo
    .UseAzureStorageClustering(options => options.TableServiceClient = tables)
    .AddCasPaxosMetaStore(builder.Configuration.GetSection("CasPaxos"))
    .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage"))
    .AddAzureBlobJournalStorage("jobs", options => options.BlobServiceClient = blobs)
    .UseJournaledDurableJobs(options => options.ActiveProviderName = "jobs")
    .AddDurableTasks());
```

| Procedure | Steps |
| --- | --- |
| Startup | Configure membership independently of the MetaStore. With native CASPaxos, give every silo the same `Voters`, start the voter silos together on their first start, and keep each voter's database on a persistent volume ([CASPaxos lifecycle](operations/caspaxos.md#lifecycle)). Each node registers once the MetaStore serves; Durable Jobs starts on its named provider, which must implement the journal catalog and metadata, and a provider which cannot hold shards fails the silo's start. |
| Voter loss | A minority of voters down leaves writes available; restore each voter silo at its endpoint with its original databases. A voter restarted on an empty disk is refused; restore its databases or keep the silo out of the cluster ([CASPaxos failures](operations/caspaxos.md#failures-operators-observe)). |
| Disk replacement | Retire the old node by identity, or name it in the new silo's `NodeToReplace` ([Orleans composition §8](spec/compositions/orleans.md#8-operation)). A voter's acceptor database is not replaceable. |
| Archive configuration | Choose the LSM over an object store or archive logs in a dedicated Azure container, and keep the cluster's archive store and prefix fixed for its life ([Orleans composition §8](spec/compositions/orleans.md#8-operation)). |
| Recovery | A lost silo's ledgers move to a live node, and its grains recover their journals on survivors. Durable Jobs survivors adopt the lost silo's shards in the Azure provider, and their wakeups reactivate grains with scheduled messages or durable delays, as `MixedProviderApplicationTests` qualifies. A whole-cluster restart follows [Orleans composition §5.10](spec/compositions/orleans.md#510-cluster-restart). |

[plan]: https://github.com/ReubenBond/clog/blob/rb-orleans-integration-plan/docs/orleans-integration-plan.md
[orleans-base]: https://github.com/dotnet/orleans/commit/6a0e937f544c35ffcf11b37e4c2b7de5a1580a03
[clog]: https://github.com/ReubenBond/clog/commit/6ffb29aa96d2e4d48fc9c34f80790493e548af96
[caspaxos]: https://github.com/ReubenBond/fast-caspaxos/commit/4dad617353f6251e3c2030872e5bb5f4a5560df9
[clockwork]: https://github.com/ReubenBond/Clockwork/commit/d1e492649ed028991763bd9764d969206e27866d
[clog-465]: https://github.com/ReubenBond/clog/issues/465
[clog-468]: https://github.com/ReubenBond/clog/issues/468
[clockwork-79]: https://github.com/ReubenBond/Clockwork/issues/79
