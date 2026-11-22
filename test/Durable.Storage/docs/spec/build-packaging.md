# Build and packaging

This file owns the framework, toolchain, analyzer, dependency-acquisition, and executable-artifact
contracts. [Verification](verification.md) owns the evidence required to qualify a change.

## 1. Framework and toolchain boundaries

Storage builds in the Orleans repository with its toolchain. Every Storage project targets `net11.0` alone, as
`eng/durable-storage/Build.props` sets it: the nine production assemblies under `src/`, `src/Azure/`, `src/AWS/`, and
`src/Google/`; the tests, simulation kit, benchmarks, fuzz harness, and the Aspire AppHost
`test/Durable.Storage/apphost/Orleans.Durable.Storage.AppHost`, which runs the cloud storage emulators, under
`test/Durable.Storage/`; and the sample under `playground/DurableStorage/`, an Orleans app and the AppHost which runs it.
The simulation test projects which Clockwork instruments rely on its controlled rules, which cover the .NET 11 BCL
([verification §7.1](verification.md#71-clockwork)).

The repository's `global.json` selects the .NET SDK and Microsoft.Testing.Platform. The .NET 11 shared framework
carries the Microsoft.Extensions abstractions, `Microsoft.Extensions.Options`, and `Microsoft.Extensions.Primitives`,
and Storage takes them from it: NuGet prunes their packages from every restore graph, and a direct reference to one
raises NU1510, an error under warnings as errors. The repository's central package management
(`Directory.Packages.props`) supplies every other dependency, each a stable release where one exists (§2).

Every run of an executable project, a benchmark included, runs on `net11.0`. The Storage scripts under
`test/Durable.Storage/eng/` keep their reports, evidence, traces, campaign outputs, and tools under
`test/Durable.Storage/artifacts/`, which Git ignores; validation runs each selected test project once and keeps its
reports under `artifacts/validation/<check>`.

Four build configurations exist for Storage projects. Debug and Release build every project, and every shipped
artifact is a Release build. `SimulationRelease` and `SimulationDebug` build the Clockwork simulation projects
(`test/Durable.Storage/Orleans.Durable.Storage.Simulation` and each project which sets
`OrleansSimulationInstrumentedTestProject`) and the `src` projects they rewrite; the solution maps its Release and
Debug to them for those projects, and an instrumented project built in another configuration fails (`CLOG0008`). Each
defines `ORLEANS_DURABLE_STORAGE_SIMULATION`, which compiles in the conditional seams of [verification
§7.2](verification.md#72-seams), and otherwise builds as its base configuration: `SimulationRelease` optimized, and
`SimulationDebug` unoptimized with `DEBUG`, so debug assertions run under simulation in the Debug leg. Every assembly a
simulation configuration builds carries `OrleansDurableStorageSimulation` assembly metadata, and `ConditionalSeamTests`
(`Orleans.Durable.Storage.Architecture.Tests`) checks that no Debug or Release build of a `src` project carries it, that
the simulation builds do, and that each `#if ORLEANS_DURABLE_STORAGE_SIMULATION` block in `src` names a seam which
verification §7.2 lists. Every configuration of a `src` project builds with the trim, AOT, and single-file analyzers.
Each configuration's outputs lie under its project's `bin/<configuration>/net11.0`.

`test/Durable.Storage/Durable.Storage.slnf` filters the repository's solution to the Storage projects; the `Justfile`
and the scripts under `test/Durable.Storage/eng/` build, format, and test through it.

## 2. Source and dependency policy

Storage builds stricter than Orleans' repository defaults. `eng/durable-storage/Build.props`, which
`src/Directory.Build.props` imports for the Storage production projects and `test/Durable.Storage/Directory.Build.props`
and `playground/DurableStorage/Directory.Build.props` import for the rest, enables nullable references, preview language
features, strict features, warning level 9999, checked arithmetic, and warnings as errors. The analyzers are the .NET
SDK's (its .NET analyzers at the preview analysis level in `All` mode, and the code-style rules, enforced in the build)
and `Microsoft.CodeAnalysis.BannedApiAnalyzers` over `eng/durable-storage/BannedSymbols.txt`, beside the banned symbols
Orleans applies to every `src` project. `eng/durable-storage/DurableStorage.globalconfig` configures them for every
Storage project, with `DurableStorage.Tests.globalconfig` for its tests and `DurableStorage.Benchmarks.globalconfig` for
its benchmarks. Each Storage directory carries a root `.editorconfig` with editor settings alone, which keeps the
repository's `.editorconfig`, whose severities suit Orleans' other projects, from applying to Storage code. Each `src`
project builds with the trim, AOT, and single-file analyzers. First-party warnings are resolved through source
generation, static catalogs, or precise annotations; none is suppressed.

The Orleans composition references the repository's `Orleans.Durable`, `Orleans.Runtime`, and `Orleans.Sdk` projects
and implements `Orleans.Durable.Journaling`'s `IJournalStorage`, registered through `AddJournalStorage<TProvider>`. Its
tests reference `Orleans.TestingHost`, and the interop tests reference `Orleans.Durable.AzureStorage`, whose reader is
their oracle. The composition is the one production assembly which uses Orleans' build-time code generation
(`OrleansBuildTimeCodeGen`) and is an Orleans framework part: the Engine, Diagnostics, and the technology packages name
no Orleans type, so `eng/durable-storage/Build.targets` removes the framework-part attribute Orleans' source build adds
to them. The composition reads the Engine's internals under its grant, so its package depends on the Engine's exact
version (`PackExactVersion`). Orleans' build targets add the namespaces `Orleans`, `Orleans.Hosting`, and
`Orleans.Runtime` to the implicit global usings; `eng/durable-storage/Build.targets` removes them in every Storage
project, so Orleans' names, such as its `FatalErrorHandler`, never shadow Storage's. Only the composition and the
projects which reference it (its tests, its fuzz target, the interop tests, the benchmarks, and the sample), and the
architecture tests, take Orleans, which `Orleans.Durable.Storage.Architecture.Tests` checks against every production
assembly's references.

Fast CASPaxos and Clockwork are imported into the repository once each, as the `Orleans.Consensus.CasPaxos.*` family
under `src/Consensus/` and the `Orleans.Testing.Simulation.*` family under `src/Simulation/`, each keeping its domain
boundary and its own build settings. `Orleans.Durable.Storage.CasPaxos` references `Orleans.Consensus.CasPaxos.Server`
and reaches consensus through a caller-owned `IRegisterService`; its dependency closure holds the consensus kernel and
its transport-neutral register services. The standalone gRPC, REST, and host deployments of Fast CASPaxos remain
upstream, and the Orleans-hosted consensus integration reaches remote voters over Orleans RPC. The adapter's tests
reach the consensus family's in-process register services through `test/Consensus/Orleans.Consensus.CasPaxos.Testing`
and `Orleans.Consensus.CasPaxos.Sqlite`. A simulation test project sets `OrleansSimulationInstrumentedTestProject`, and
the repository's simulation build integration (`eng/OrleansSimulation.targets`, imported by `test/Directory.Build.targets`)
references the simulation kernel, its framework-neutral testing support (`.Testing`), and its Microsoft Testing Platform
integration (`.Testing.MTP`) and test-host hook, and runs the instrumentation build task from source.
`test/Durable.Storage/eng/OrleansSimulation.targets` adds Storage's part: the kernel and its testing support build in the
base configuration, the configuration check, the ordinary-IL exclusions, the simulation analyzer bans, and the
`SeededSimulation` driver and the simulation kit.

The cloud technology packages take their services' SDKs as stable releases: `Azure.Storage.Blobs`
and `Azure.Data.Tables` in `Orleans.Durable.Storage.AzureStorage`, `AWSSDK.S3` and `AWSSDK.DynamoDBv2` in
`Orleans.Durable.Storage.S3`, and `Google.Cloud.Storage.V1` and `Google.Cloud.Firestore` in
`Orleans.Durable.Storage.GoogleCloud`, which also takes `Polly.Core` for the retries its object store adds
to its client's ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations)). The Engine and the
composition take `Polly.Core` and `System.IO.Hashing`, and `Orleans.Durable.Storage.RocksDb` takes `RocksDB`. The SDK,
`Polly.Core`, CsCheck, and Verify releases are those Storage's suites qualify: `Directory.Packages.props` applies them
to Storage projects alone, so the dependency closures of Orleans' other packages stay unchanged, and a process which
loads both resolves the later release. `Azure.Storage.Blobs` 12.30.1 supplies the Blob service version Storage requests.

`Orleans.Durable.Storage.Cloud.Tests` takes `Aspire.Hosting.Testing`, at the AppHost's Aspire release, to start the
AppHost's emulators, and `Azure.Identity`, whose `DefaultAzureCredential` authenticates the live Azure fidelity run with
Microsoft Entra ID ([verification §10](verification.md#10-integration-end-to-end-and-soak)).
`Orleans.Durable.Storage.Interop.Tests`, one of the composition's tests, takes both for the same runs. The sample
`playground/DurableStorage/Orleans.Durable.Storage.Samples.Journaling` references `Orleans.Clustering.AzureStorage`, Orleans'
first-party membership on Azure Table storage. It also takes the OpenTelemetry project's
`OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol`, third-party packages, for one
requirement: exporting the sample's metrics and logs over OTLP to the Aspire dashboard, where its README directs the
reader. No `src` project takes them. The AppHosts pin each emulator's image by digest, once, in
`test/Durable.Storage/apphost/shared/EmulatorImages.cs`, which both `test/Durable.Storage/apphost/Orleans.Durable.Storage.AppHost` and the sample's
`playground/DurableStorage/Orleans.Durable.Storage.Samples.AppHost` compile, so a run starts the same bytes whatever its registry's tags
later name ([verification §13](verification.md#13-qualification-boundaries)):

| Image | Release | Digest (`sha256:`) |
| --- | --- | --- |
| `mcr.microsoft.com/azure-storage/azurite` | 3.37.0 | `830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5` |
| `localstack/localstack` | 4.14.0, the last release whose community image serves S3 without an auth token | `3ebc37595918b8accb852f8048fef2aff047d465167edd655528065b07bc364a` |
| `fsouza/fake-gcs-server` | 1.56.1 | `797ce226d62f947c009dc40246b30cfb456b8473d8241407f9d6f2c04e4d69ef` |
| `gcr.io/cloud-devrel-public-resources/storage-testbench` | v0.67.0 | `06729d407e8bba055798707dccfffd1f792ea818937954c56a776811116ede5c` |
| `amazon/dynamodb-local` | untagged; the digest names it | `6ada631ec7aa5fc83446855eb17233e3dad184fec47477e8140c6c569cfe0c86` |
| `gcr.io/google.com/cloudsdktool/google-cloud-cli` | `emulators` | `3989a42372d3b984bade6db02213d456d94a0ba3765e8b744b95c4188b6fcc89` |

Snapshot tests ([verification §8](verification.md#8-snapshot-testing)) use Verify at exactly 32.0.0, both `Verify`
and `Verify.XunitV3`: Verify's last release on or before 1 September 2026. Its Open Source Maintenance Fee covers
every official release after that date, 32.0.1 (12 September 2026) included. The exact ranges, with transitive
pinning, make restore fail rather than admit a later Verify package. Its dependencies, Argon 0.35.2, DiffEngine
20.0.0, EmptyFiles 8.18.2, and SimpleInfoName 3.2.0, are MIT-licensed releases from before that date. Verify's build
properties turn off deterministic compilation and source-path mapping in the test projects which reference it, so the
compiler records each test's source path, beside which Verify keeps the test's snapshots.

Decoder fuzzing ([verification §6.1](verification.md#61-decoder-fuzzing)) takes three pinned parts: the SharpFuzz 2.3.0
package in the campaign executable `test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz` alone; the SharpFuzz.CommandLine 2.3.0 tool in
Storage's tool manifest (`test/Durable.Storage/.config/dotnet-tools.json`), with Stryker.NET and ReportGenerator, which instruments the published decoder assemblies; and the libfuzzer-dotnet
`v2025.05.02.0904` driver, which `test/Durable.Storage/eng/fuzz.ps1` downloads under `test/Durable.Storage/artifacts/tools` and verifies against a pinned SHA-256
per platform. All three are MIT-licensed. The per-layer target libraries reference their layer and the oracle framework
only, and each layer's corpus replay tests run its targets over uninstrumented decoders.

The documentation checks ([verification §12](verification.md#12-gates-and-measurements)) run on Node.js with the
packages `test/Durable.Storage/eng/docs/package-lock.json` pins: markdownlint-cli2 0.24.0 (markdownlint 0.42.0), remark-cli 12.0.1 with
remark-validate-links 13.1.0, and Mermaid 12.2.0 under jsdom 30.1.3. `test/Durable.Storage/eng/check-docs.ps1` installs them with `npm ci`
under `artifacts/tools/docs` whenever the lock file's hash changes, as `test/Durable.Storage/eng/model-check-quint.ps1` installs Quint
from `test/Durable.Storage/eng/quint/package-lock.json`, and the install fails on a Node.js release the packages do not support. Mermaid
12.2.0 is the release GitHub's Markdown renderer runs, and its pin moves with GitHub's.

## 3. Executable artifacts

This design specifies no executable artifact. Clog's layers and the Orleans composition are libraries, which an
application's own host loads. Executables, such as a silo host for the Orleans composition and its deployment
images, arrive with the Orleans composition's implementation, which states here, for each, its frameworks and
runtime identifiers, its artifact shape, its reproducible publishing entry point, and its smoke evidence.

## 4. Native AOT qualification

No Native AOT artifact is produced. Every `src` project builds with the trim, AOT, and single-file analyzers and
warnings as errors (§2), so first-party code stays analyzable for AOT whatever its host.

An executable's AOT qualification covers its whole dependency closure, which the analyzers do not. Two closure
constraints are measured: on Windows, ILC rejects the COM interop in `System.Management`, which `Google.Apis.Auth`
and the Firestore client reach; and RocksDbSharp, under the state database, emits native stubs at run time. An
AOT artifact therefore requires a clean target-native publish and integration evidence for its actual provider
closure; reported dependency warnings stay visible rather than suppressed.

## 5. Representation and compatibility

Clog is a prototype. Each API, configuration schema, wire contract, and durable format has exactly one
current representation. A change replaces that representation together with its callers, owning
specification, and golden vectors. Derived state is rebuilt by the recovery procedure its owner
specifies. Retained-state migration, a mixed-version window, or another compatibility obligation
requires an explicit spec change stating the affected surfaces and qualification before compatibility
code is introduced. See [format-change rules](formats.md#7-rules-for-format-changes).

## 6. Orleans packages and source layout

The production code is the nine assemblies of the `Orleans.Durable.Storage` family ([spec README
§2.6](README.md#26-project-map)), with the `Orleans.Durable.Storage` namespaces and the `Microsoft.Orleans.Durable.Storage`
package IDs (each project's `PackageId` is `Microsoft.` and its name, from `eng/durable-storage/Build.props`). The
packages follow the Durable family's versioning: each carries the `alpha.1` suffix and validates its framework assets
against an empty package-validation baseline until its first release, and each names its title and description in
its project file. The root, Engine, Diagnostics, file-system, RocksDB, and CASPaxos projects lie under `src/`, and the
Azure, AWS, and Google packages under `src/Azure/`, `src/AWS/`, and `src/Google/`, beside Orleans' other providers for
those services. The tests, simulation kit, fuzz harness and corpora, benchmarks, golden vectors, Quint models, this
specification, and the validation scripts lie under `test/Durable.Storage/`, whose README is their entry point;
`test/Durable.Storage/PROVENANCE.md` records the snapshot they were imported from.

The family keeps §1's framework and §2's compiler settings. The boundaries below, each measured on this tree with a
Release build of every `src` project with `LangVersion=latest` and a scan of every `src` assembly's platform type and
member references against the .NET 10 shared framework, are what a target of an older framework or language version
would change.

| Boundary | Current tree | Required for an older framework or language version |
| --- | --- | --- |
| Language version | `preview` everywhere. Every `src` project but the Engine compiles under C# 15 (`latest`), each against the Engine built with the preview: unions (in the Engine, the composition, and the Azure and file-system packages) and collection expression arguments are released C# 15 features. The WAL's io_uring interop (`IoUringInterop` and its `LibraryImport` stubs), in the Engine, declares pointer types outside an unsafe context, which only C# 15's preview memory-safety model admits. | The WAL's interop marks its pointer declarations unsafe, or the target adopts the preview. A language version before C# 15 has no unions: each becomes the hand-written form of [conventions §15](conventions.md#15-outcomes-unions-and-errors). |
| .NET 11 APIs | The union support types (`System.Runtime.CompilerServices.IUnion`, `UnionAttribute`) in each assembly which declares a union; `string.StartsWith(char, StringComparison)` in `Orleans.Durable.Storage.FileSystem`; and the Microsoft.Extensions logging, options, and dependency-injection abstractions, which the .NET 11 shared framework carries (§1), in `Orleans.Durable.Storage.Engine.Ledgers`, the LSM, archive logs, `Orleans.Durable.Storage.FileSystem`, the Orleans composition, and its controllers' decisions. Every other platform reference resolves in .NET 10. | For an older framework: the union support types supplied to the compiler, the `StartsWith` call replaced, and the Microsoft.Extensions abstractions taken as packages, which NuGet prunes only from a `net11.0` graph (§1). |
| Fast CASPaxos | `Orleans.Durable.Storage.CasPaxos` references the transport-neutral `Orleans.Consensus.CasPaxos.Server` project, whose closure is the consensus kernel. | None. |
## 7. Design rationale

One framework, .NET 11 on its GA SDK, gives every project the runtime and compiler which unions need ([conventions
§15](conventions.md#15-outcomes-unions-and-errors)), and each project one build, one test run, and one mutation run.
Clog has no deployment on an older runtime to serve (§5), so a second framework would double validation and keep a
polyfill for the union support types. The Microsoft.Extensions abstractions come from the shared framework, the one copy
every `net11.0` process loads, so NuGet prunes their packages and rejects an explicit reference (NU1510).

Verify enters as its package rather than as vendored source: 32.0.0 runs on xUnit v3 4.0 and Microsoft.Testing.Platform
2 (facts, theories, new and changed snapshots, and attachments behave as documented on `net11.0`), so an exact
pin keeps the graph before the fee's cutover without maintaining a copy of Verify. Version 32.0.1 is excluded because
it was released after the cutover, although it carries no build-time sponsorship check.

Storage references Orleans' projects in the same repository, so the composition builds against the Orleans source it
ships with: one commit identifies both, a change to the journal storage contract and its adapter is one reviewable
change, and Orleans' build supplies the code generator and analyzers to the composition as it does to Orleans' other
packages. Only the composition and the projects which reference it take Orleans, so Orleans cannot reach the storage
layers, whose dependencies point down.

Storage's stricter settings apply to its projects alone. One props file, which the source and test trees import for
Storage projects, keeps the family's compiler settings and analyzer coverage without changing the warnings of Orleans'
other projects. The analyzer configuration is a global analyzer config, so one file serves every Storage directory; a
root `.editorconfig` in each Storage directory keeps the repository's `.editorconfig`, which takes precedence over a
global config, from lowering Storage's severities. The package versions Storage's suites qualify apply to Storage
projects alone for the same reason: raising them across the repository would change the dependencies of Orleans'
published packages, a decision of its own.

The consensus and simulation families are imported once, each with its own build settings and domain boundary, and
Storage references their projects rather than a copy of its own, so a fix to either lands in one place and every
consumer builds it.

The verification assets share one home, `test/Durable.Storage/`, laid out as the snapshot's repository was, so the
relative links among the specification, models, scripts, vectors, and corpora hold, and the tests' lookups of those
assets resolve from the repository root to that one directory.
The SDK's analyzers move with the compiler, so a new language feature such as a union declaration builds under every
analyzer as soon as the SDK ships it, and the banned-API list carries the project's own rules. Each analyzer package
outside the SDK added rules to configure and suppress, and lagged the compiler's language features.

Analyzers on every `src` project keep trimming and AOT a property of the code rather than of a packaging step, so
an executable added later inherits analyzable layers. Executable artifacts are specified with the executables
themselves: an artifact contract without its executable would constrain nothing and would be qualified by nothing.

One current prototype representation keeps the codec, specification, and callers aligned.
Version control retains earlier designs while current readers and writers implement the current one.

The assemblies follow deployment and the components follow dependency ([spec README
§2.7](README.md#27-design-rationale)): a host deploys the Engine, the technology packages it configures, and the
composition, and the architecture tests keep the layering inside each assembly. The controllers' decisions compile into
the Engine rather than the composition, which the approved organization named for the controller policies: the decisions
name no Orleans type, and in the composition's assembly every simulation of them would load Orleans and its hosting
stack, against verification §7.1. No technology package reads the Engine's internals: each seam it uses is a public
contract which states its fail-stop or ownership guarantee, so the Engine's internals stay free to change without a
provider's release.
