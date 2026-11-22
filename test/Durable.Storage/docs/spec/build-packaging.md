# Build and packaging

This file owns the framework, toolchain, analyzer, dependency-acquisition, and executable-artifact
contracts. [Verification](verification.md) owns the evidence required to qualify a change.

## 1. Framework and toolchain boundaries

Every project targets `net11.0` alone, as `Directory.Build.props` sets it: the first-party libraries, tests,
benchmarks, and fuzz harness; the Aspire AppHost `apphost/Orleans.Durable.Storage.AppHost`, which runs the cloud storage
emulators; the sample under `samples/`, an Orleans app and the AppHost which runs it; and the simulation test projects
which Clockwork instruments, whose controlled rules cover the .NET 11 BCL ([verification
§7.1](verification.md#71-clockwork)).

`global.json` pins the GA SDK `11.0.100`, rolls forward to its latest patch and never to a prerelease, and selects
Microsoft.Testing.Platform. `Directory.Build.targets` pins `Microsoft.NET.ILLink.Tasks`, the package of the trim and AOT
analyzers, which each SDK patch otherwise names at its own runtime patch, so the lock files hold under every 11.0.1xx
patch. `eng/install-dotnet.ps1` and `eng/install-dotnet.sh` acquire the SDK `global.json` names and the .NET 10 runtime,
which runs the tools in the repository's manifest that target .NET 10: Stryker.NET, the SharpFuzz command line, and
ReportGenerator. Workflows take the SDK from `global.json` through `actions/setup-dotnet@v5`, with the .NET 10 SDK for
its runtime. The .NET 11 shared framework carries the Microsoft.Extensions abstractions, `Microsoft.Extensions.Options`,
and `Microsoft.Extensions.Primitives`, and Clog takes them from it: NuGet prunes their packages from every restore
graph, and a direct reference to one raises NU1510, an error under warnings as errors. Every other dependency is a
stable release where one exists, restored from the configured sources, except Orleans, which comes from its nightly
feed at one pinned build of its `main` branch (§2).

Every run of an executable project, a benchmark included, runs on `net11.0`. Validation runs each selected test project
once and keeps its reports under `artifacts/validation/<check>`.

Four build configurations exist. Debug and Release build every project, and every shipped artifact is a Release
build. `SimulationRelease` and `SimulationDebug` build the Clockwork simulation projects
(`tests/Orleans.Durable.Storage.Simulation` and each project which sets `OrleansSimulationInstrumentedTestProject`) and
the `src` projects they rewrite; `Clog.slnx` maps the solution's Release and Debug to them for those projects, and an
instrumented project built in another configuration fails (`CLOG0008`). Each defines
`ORLEANS_DURABLE_STORAGE_SIMULATION`, which compiles in the conditional seams of [verification
§7.2](verification.md#72-seams), and otherwise builds as its base configuration: `SimulationRelease` optimized, and
`SimulationDebug` unoptimized with `DEBUG`, so debug assertions run under simulation in the Debug leg. Clockwork's
projects keep their own build settings and build in the base configuration. Every assembly a simulation configuration
builds carries `OrleansDurableStorageSimulation` assembly metadata, and `ConditionalSeamTests`
(`Orleans.Durable.Storage.Architecture.Tests`) checks that no Debug or Release build of a `src` project carries it, that
the simulation builds do, and that each `#if ORLEANS_DURABLE_STORAGE_SIMULATION` block in `src` names a seam which
verification §7.2 lists. Every configuration of a `src` project builds with the trim, AOT, and single-file analyzers.
The simulation outputs live beside the others under `artifacts/bin/<project>/simulationrelease` and `simulationdebug`.

## 2. Source and dependency policy

The compiler enables nullable references, preview language features, strict features, checked
arithmetic, and warnings as errors. The analyzers are the .NET SDK's (its .NET analyzers at the preview analysis level
in `All` mode, and the code-style rules, enforced in the build) and `Microsoft.CodeAnalysis.BannedApiAnalyzers` over
`BannedSymbols.txt`. Each `src` project builds with trim, AOT, and single-file analyzers. First-party warnings are
resolved through source generation, static catalogs, or precise annotations; none is suppressed. One diagnostic is an
opt-in rather than a warning: Orleans marks Orleans.Journaling's API experimental (`ORLEANSEXP005`), and the
composition, whose journaling adapter implements it, and its test project, which call it, opt into that one ID in their
project files. The opt-in goes once Orleans.Journaling ships stable.

Clog builds two upstream projects from source, each a git submodule under `vendor/` pinned to an upstream commit: Fast
CASPaxos (`vendor/fast-caspaxos`), whose register service backs the Fast CASPaxos MetaStore, and Clockwork
(`vendor/Clockwork`), the simulation framework ([verification §7.1](verification.md#71-clockwork)). The commit the
repository records for a submodule is its pin, and a build uses exactly upstream's tree at that commit: the submodule
checked out at its pin, with no local change. A fix goes upstream, and the pin then moves to the commit which carries
it: `git -C vendor/<name> fetch`, `git -C vendor/<name> checkout <commit>`, then `git add vendor/<name>`. To build a fix
before it merges upstream, commit it in the submodule and `git add vendor/<name>` to record that commit; the pin moves
to upstream's commit once the fix merges. At their pins both submodules target `net11.0` alone. The upstream Fast
CASPaxos host is built separately for deployment; the [MetaStore contract](components/metastore.md) owns its protocol
semantics.

`just sync` (`eng/sync.ps1`) brings a clone or worktree to a buildable state: it updates every submodule to its recorded
commit, initializing it if need be, and installs the SDK `global.json` selects and the .NET 10 runtime when either is
missing. It refuses a submodule with local changes, and one checked out at a commit which no remote branch holds and
which is not an ancestor of the recorded commit, naming the commit, so it never leaves work reachable only from the
reflog. A Clog project which references a submodule's project checks, before restore and before build, that the
submodule is initialized, at its recorded commit, and clean (`eng/Submodules.targets`). One `git status` of the
submodules the project uses decides it, by git's exit code and the submodule flags it reports, whatever
`submodule.<name>.ignore` or `diff.ignoreSubmodules` says (`--ignore-submodules=none`): `CLOG0001` names an
uninitialized submodule, `CLOG0002` a failed `git` (a dangling gitfile among its causes), `CLOG0003` a submodule at
another commit, with both remedies, `just sync` to return to the recorded commit and `git add vendor/<name>` to record
the new one, and `CLOG0004` local changes, tracked or untracked. A restore or build through `just` or `dotnet` therefore
fails the same way, and `global.json` names `just sync` when the pinned SDK is missing. `eng/test-submodule-guard.ps1`,
which CI runs, checks the guard and `just sync` in a temporary worktree.

The submodules keep their upstream build boundary: they build with upstream's `Directory.Build.props`, package
management, and editor configuration, and carry no lock files. `vendor/.editorconfig` (`root = true`),
`vendor/Directory.Build.props`, and `vendor/Directory.Build.targets` end the upward search for those files at `vendor/`,
so none of Clog's reaches a submodule which lacks its own. The root `Directory.Build.rsp` sets
`ShouldUnsetParentConfigurationAndPlatform=false` for every build, so a project outside `Clog.slnx`, including one which
a submodule's project references, builds in the configuration of the build which references it, and each builds once.
`Clog.slnx` lists Clog's projects alone, so a submodule's projects build through the references to them. Formatting
passes `--exclude vendor/`, because the workspace `dotnet format` loads opens every referenced project, a submodule's
among them. Upstream's `global.json` applies only to commands run inside its submodule: a Clog build resolves the SDK
from Clog's. `eng/Orleans.Consensus.CasPaxos.props` names the Fast CASPaxos source root, whose adapter and tests use
Clog's policies; Fast CASPaxos' projects are the `Orleans.Consensus.CasPaxos.*` family. Clockwork supplies the
`Microsoft.Orleans.Testing.Simulation.*` packages, which Clog's projects reference by package ID: the simulation kernel,
its framework-neutral testing support (`.Testing`), its Microsoft Testing Platform integration (`.Testing.MTP`), which
each simulation test project references itself as a test application, and the instrumentation build targets.
`eng/OrleansSimulation.SourceBuild.targets` maps each such reference, through Clockwork's source-build map
(`eng/Microsoft.Orleans.Testing.Simulation.SourceBuild.props`), to the package's source project in `vendor/Clockwork`,
and adds the test-platform integration's hook; no feed supplies them. `eng/OrleansSimulation.targets` adds to a
simulation test project the kernel, the testing support, and the instrumentation build targets, imported when the
submodule is initialized, whose task assembly builds from the submodule's source and runs in a task host process. One
Clockwork serves the whole build: `vendor/Orleans.Consensus.CasPaxos.Embedding.props`, which Fast CASPaxos' build
imports when it lies beside it, points that build's `OrleansSimulationSourceRoot` at `vendor/Clockwork`, and `just sync`
initializes the submodules without recursing, so Fast CASPaxos' own Clockwork submodule stays uncloned. The two pins
move together, in one pull request.

The committed NuGet lock files describe ordinary build, test, and formatting restores. Aspire's
development graph contains host-specific dashboard packages: Windows validates its committed graph,
and Linux/macOS validation restores with the pinned SDK and central packages into
`artifacts/validation/locks/<host-rid>`, using `ClogPublishLockDirectory` and `RestoreLockedMode=false`.

The Orleans composition takes `Microsoft.Orleans.Runtime`, `Microsoft.Orleans.Sdk`, and `Microsoft.Orleans.Journaling`,
and its tests `Microsoft.Orleans.TestingHost`, from Orleans' nightly feed
(`https://pkgs.dev.azure.com/dnceng/public/_packaging/orleans-nightly/nuget/v3/index.json`) at one build of Orleans'
`main` branch, the recorded exception to taking stable releases. The pin is that build, one Orleans commit:
`Directory.Packages.props` names it once, as `OrleansVersion`, the version of Orleans' packages, and `OrleansCommit`,
the commit each of their nuspecs records, which `Orleans.Durable.Storage.Architecture.Tests` checks against the restored
packages; Orleans' alpha-tagged packages of the same build, such as `Microsoft.Orleans.Journaling` and
`Microsoft.Orleans.Journaling.AzureStorage`, are at `$(OrleansVersion).alpha.1`. `main` carries what the composition
builds on and no stable release has: Orleans' own `ArcBuffer` and its wire codec, which the replica calls are to carry,
and Orleans.Journaling's current `IJournalStorage`, which the journaling adapter implements. `NuGet.config` maps every
`Microsoft.Orleans.*` package to the nightly feed alone and every other package to the configured proxy alone, so no
Orleans package resolves from another source and the nightly feed supplies nothing else. Only the composition and the
projects which reference it, its tests, its fuzz target, and the architecture tests, take Orleans, which
`Orleans.Durable.Storage.Architecture.Tests` checks against every project's lock file and every production assembly's
references.

The feed's version order does not follow `main`: an older build of another release line, such as
`10.2.0-nightly.20260611.1`, sorts above `10.0.0-nightly.20261114.2`, so an update tool offers older builds as newer,
and a host which also references a stable Orleans release resolves that release for the whole process. A pin therefore
moves only by this procedure, in a pull request of its own, since a nightly carries no compatibility promise between
builds:

1. Choose the build by its commit and date on Orleans' `main`, never by version order, and set `OrleansVersion` and
   `OrleansCommit` together.
2. Regenerate the lock files.
3. Re-read, at the new commit, the Orleans code behind each Orleans guarantee the composition uses
   ([orleans §3](compositions/orleans.md#3-requirements-met)), Orleans' buffer ownership among them, and update the
   composition's specification and its deferred work to match; `OrleansBufferOwnershipContractTests` checks that
   ownership against the new build.
4. Rerun `OrleansReplicaTransportMeasurements` when Orleans' serialization or messaging changed, and update the
   measurements of [orleans §5.12](compositions/orleans.md#512-replica-transport).
5. Pass the full gate (`just check`).

Silos built at two pins do not form one cluster. Restoring the pin depends on the feed retaining it: it holds builds
back to 2024 today, and a build it deletes restores only from a populated package cache, so the pin then moves.

The nightly's libraries target `net10.0` and `net11.0`. `Microsoft.Orleans.Runtime` brings Roslyn's workspaces for
Orleans' code generator, which `Directory.Packages.props` pins at the compiler API version the architecture tests use
(`Microsoft.CodeAnalysis.Workspaces.Common` 5.9.0, at or above the nightly's 5.0.0 floor), so restore resolves one
Roslyn; the pin follows Orleans' code generator. Orleans' build targets add the namespaces
`Orleans`, `Orleans.Hosting`, and `Orleans.Runtime` to the implicit global usings; `Directory.Build.targets` removes
them in every project, so Orleans' names, such as its `FatalErrorHandler`, never shadow Clog's.

The cloud technology packages take their services' SDKs as stable releases, with their lock files: `Azure.Storage.Blobs`
and `Azure.Data.Tables` in `Orleans.Durable.Storage.AzureStorage`, `AWSSDK.S3` and `AWSSDK.DynamoDBv2` in`
Orleans.Durable.Storage.S3`, and `Google.Cloud.Storage.V1` and `Google.Cloud.Firestore` in`
Orleans.Durable.Storage.GoogleCloud`, which also takes `Polly.Core` for the retries its object store adds
to its client's ([archive LSM §9](components/archive-lsm.md#9-providers-and-implementations)).
`Orleans.Durable.Storage.Cloud.Tests` takes `Aspire.Hosting.Testing`, at the AppHost's Aspire release, to start the
AppHost's emulators, and `Azure.Identity`, whose `DefaultAzureCredential` authenticates the live Azure fidelity run with
Microsoft Entra ID ([verification §10](verification.md#10-integration-end-to-end-and-soak)).
`Orleans.Durable.Storage.Interop.Tests`, one of the composition's tests, takes both for the same runs, and Orleans'
alpha-tagged `Microsoft.Orleans.Journaling.AzureStorage`, whose reader is its oracle. The sample
`samples/Orleans.Durable.Storage.Samples.Journaling` takes `Microsoft.Orleans.Clustering.AzureStorage`, Orleans'
first-party membership on Azure Table storage, at the nightly pin. It also takes the OpenTelemetry project's
`OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol`, third-party packages, for one
requirement: exporting the sample's metrics and logs over OTLP to the Aspire dashboard, where its README directs the
reader. No `src` project takes them. The AppHosts pin each emulator's image by digest, once, in
`apphost/shared/EmulatorImages.cs`, which both `apphost/Orleans.Durable.Storage.AppHost` and the sample's
`samples/Orleans.Durable.Storage.Samples.AppHost` compile, so a run starts the same bytes whatever its registry's tags
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
package in the campaign executable `fuzz/Orleans.Durable.Storage.Fuzz` alone; the SharpFuzz.CommandLine 2.3.0 tool in
the repository's tool manifest, which instruments the published decoder assemblies; and the libfuzzer-dotnet
`v2025.05.02.0904` driver, which `eng/fuzz.ps1` downloads under `artifacts/tools` and verifies against a pinned SHA-256
per platform. All three are MIT-licensed. The per-layer target libraries reference their layer and the oracle framework
only, and each layer's corpus replay tests run its targets over uninstrumented decoders.

The documentation checks ([verification §12](verification.md#12-gates-and-measurements)) run on Node.js with the
packages `eng/docs/package-lock.json` pins: markdownlint-cli2 0.24.0 (markdownlint 0.42.0), remark-cli 12.0.1 with
remark-validate-links 13.1.0, and Mermaid 12.2.0 under jsdom 30.1.3. `eng/check-docs.ps1` installs them with `npm ci`
under `artifacts/tools/docs` whenever the lock file's hash changes, as `eng/model-check-quint.ps1` installs Quint
from `eng/quint/package-lock.json`, and the install fails on a Node.js release the packages do not support. Mermaid
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

## 6. Orleans source-tree preparation

Clog's production code is the nine assemblies of the `Orleans.Durable.Storage` family ([spec README
§2.6](README.md#26-project-map)), with the `Orleans.Durable.Storage` namespaces and the `Microsoft.Orleans.Durable.Storage`
package IDs (each src project's `PackageId` is `Microsoft.` and its name, from `Directory.Build.props`), in preparation
for their import into the Orleans source tree ([implementation plan, decision 19](../implementation-plan.md#decisions));
the import itself happens in Orleans' repository. Until then Clog keeps §1's framework and §2's compiler settings, and
the import qualifies against the boundaries below, each measured on this tree: a Release build of every `src` project
with `LangVersion=latest`, and a scan of every `src` assembly's platform type and member references against the .NET 10
shared framework.

| Boundary | Clog today | Required for an import which targets an older framework or language version |
| --- | --- | --- |
| Language version | `preview` everywhere. Every `src` project but the Engine compiles under C# 15 (`latest`), each against the Engine built with the preview: unions (in the Engine, the composition, and the Azure and file-system packages) and collection expression arguments are released C# 15 features. The WAL's io_uring interop (`IoUringInterop` and its `LibraryImport` stubs), in the Engine, declares pointer types outside an unsafe context, which only C# 15's preview memory-safety model admits. | The WAL's interop marks its pointer declarations unsafe, or the import adopts the preview. A language version before C# 15 has no unions: each becomes the hand-written form of [conventions §15](conventions.md#15-outcomes-unions-and-errors). |
| .NET 11 APIs | The union support types (`System.Runtime.CompilerServices.IUnion`, `UnionAttribute`) in each assembly which declares a union; `string.StartsWith(char, StringComparison)` in `Orleans.Durable.Storage.FileSystem`; and the Microsoft.Extensions logging, options, and dependency-injection abstractions, which the .NET 11 shared framework carries (§1), in `Orleans.Durable.Storage.Engine.Ledgers`, the LSM, archive logs, `Orleans.Durable.Storage.FileSystem`, the Orleans composition, and its controllers' decisions. Every other platform reference resolves in .NET 10. | For an older framework: the union support types supplied to the compiler, the `StartsWith` call replaced, and the Microsoft.Extensions abstractions taken as packages, which NuGet prunes only from a `net11.0` graph (§1). |
| Fast CASPaxos | `Orleans.Durable.Storage.CasPaxos` references the single upstream `Orleans.Consensus.CasPaxos.Grpc` project, so it takes the ASP.NET Core shared framework and `Grpc.AspNetCore` with it. | A client-only adapter needs an upstream split of the gRPC package, a request of its own. |

## 7. Design rationale

One framework, .NET 11 on its GA SDK, gives every project the runtime and compiler which unions need ([conventions
§15](conventions.md#15-outcomes-unions-and-errors)), and each project one build, one test run, and one mutation run.
Clog has no deployment on an older runtime to serve (§5), so a second framework would double validation and keep a
polyfill for the union support types. The Microsoft.Extensions abstractions come from the shared framework, the one copy
every `net11.0` process loads, so NuGet prunes their packages and rejects an explicit reference (NU1510). The ILLink pin
keeps an SDK patch out of the restore graph: `latestPatch` takes the SDK's fixes, and the lock files change only when
the pin moves. A separate lock directory for host-specific restores preserves the ordinary reproducible graph.

Verify enters as its package rather than as vendored source: 32.0.0 runs on xUnit v3 4.0 and Microsoft.Testing.Platform
2 (facts, theories, new and changed snapshots, and attachments behave as documented on `net11.0`), so an exact
pin keeps the graph before the fee's cutover without maintaining a copy of Verify. Version 32.0.1 is excluded because
it was released after the cutover, although it carries no build-time sponsorship check.

Orleans enters as an exact nightly package rather than as a submodule of its `main` branch: the nightly is built and
published from `main` by Orleans' own pipeline, so the pin names a reviewed upstream commit as a submodule would, while
the composition keeps Orleans' packaging (its code generator and analyzers ship as analyzer assets, its build targets
as package targets) and Clog's build stays free of Orleans' repository, toolchain, and build boundary. The source
mapping makes the feed's reach exact in both directions. A floating version (`10.0.0-nightly.*`) was rejected: every
restore could move Orleans under the composition without a change in Clog. Recording the commit beside the version, and
checking it, names the build by the one identity the feed's version order does not scramble. Only the composition and
the projects which reference it take Orleans, so the nightly cannot reach the storage layers, whose dependencies point
down.

A git submodule pins upstream source by commit, so the build uses exactly upstream's tree at that commit and holds no
local patch: a fix goes upstream, where it is reviewed and tested with the code it changes, and the pin moves to it. A
copied tree would need its identity with upstream checked and every local difference recorded; a fetch into `artifacts/`
would keep its pin in a file of its own and need an acquisition script. A submodule's pin is git's own, which `git
submodule update` acquires and every git client shows, and the build's guard checks the checkout against it with git
itself. The guard requires a clean checkout because a build of changed upstream source is not the pinned build;
committing a change in the submodule and recording that commit keeps local iteration possible and visible. The
submodules keep their upstream build boundary because Clog's analyzers and formatting would otherwise require rewriting
code Clog does not own; Clog's integration lives in `eng/`. The configuration rule is global, through
`Directory.Build.rsp`, because a property set in Clog's projects alone does not reach a reference between two upstream
projects, which would then build in Debug beside its Release build. The instrumentation task runs in a task host process
because the repository builds it: an MSBuild node which loaded it in process would lock the assembly and keep running a
stale build of it.

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
