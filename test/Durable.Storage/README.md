# Durable Storage (Clog)

Clog stores journals, fine-grained per-key logs of entries, for applications co-hosted with it, and is the storage for
Orleans Journaling. Writes are replicated to the local disks of `N` nodes (live ledgers) and migrate to the Archive: an
LSM tree in cloud object storage, or per-key logs on an Orleans journal provider. The keyspace is divided into dyadic
ranges, each owned by one sequenced ledger at a time; a ledger map in a replicated register decides ownership, and
transitions move ranges by sealing and recovering ledgers and writing that register, without copying data.

The `Microsoft.Orleans.Durable.Storage` packages implement it. Start with the [specification](docs/spec/README.md): its
overview motivates the design, shows how the layers work together, and names the owner of each guarantee.
[AGENTS.md](AGENTS.md) covers the layout and maintenance rules, and [PROVENANCE.md](PROVENANCE.md) the snapshot this
tree was imported from.

## Implementation status

| Part | Status |
| --- | --- |
| Buffers, the WAL, ledger replicas (memory, LedgerStore), the ledger protocol (`Ledger`, `LedgerQuorum`, `LedgerWriter`), MetaStore (memory, file system, Fast CASPaxos, Azure, AWS, Google Cloud), state database | Implemented building blocks |
| Ledgers, the Archive, Journals over ledgers, Orleans composition (including replica traffic over grain calls) | Specified and not yet implemented |

[§5 of the specification](docs/spec/README.md#5-implementation-status) tracks the details.

## Layout

| Path | Contents |
| --- | --- |
| [`src/Orleans.Durable.Storage`](../../src/Orleans.Durable.Storage) | The Orleans composition: journal storage provider, silo hosting, routing, placement, replica transport, and control grains |
| [`src/Orleans.Durable.Storage.Engine`](../../src/Orleans.Durable.Storage.Engine) | The managed storage layers and their contracts |
| [`src/Orleans.Durable.Storage.Diagnostics`](../../src/Orleans.Durable.Storage.Diagnostics) | The fatal-error handler |
| [`src/Orleans.Durable.Storage.FileSystem`](../../src/Orleans.Durable.Storage.FileSystem), [`.RocksDb`](../../src/Orleans.Durable.Storage.RocksDb), [`.CasPaxos`](../../src/Orleans.Durable.Storage.CasPaxos) | File-system, RocksDB, and Fast CASPaxos technology packages |
| [`src/Azure/Orleans.Durable.Storage.AzureStorage`](../../src/Azure/Orleans.Durable.Storage.AzureStorage), [`src/AWS/Orleans.Durable.Storage.S3`](../../src/AWS/Orleans.Durable.Storage.S3), [`src/Google/Orleans.Durable.Storage.GoogleCloud`](../../src/Google/Orleans.Durable.Storage.GoogleCloud) | Cloud technology packages |
| `Orleans.Durable.Storage.*.Tests` | One test project per contract and layer, with the architecture, model, interop, and sample tests |
| [`Orleans.Durable.Storage.Simulation`](Orleans.Durable.Storage.Simulation), [`shared`](shared) | The deterministic simulation kit, and the oracles, conformance kit, leak checks, and diagnostics the tests share |
| [`shared-vectors`](shared-vectors) | Golden vectors |
| [`fuzz`](fuzz) | Decoder fuzz targets, the campaign harness, and each target's corpus |
| [`benchmarks`](benchmarks) | BenchmarkDotNet microbenchmarks |
| [`specs`](specs) | Quint models |
| [`docs/spec`](docs/spec/README.md) | The specification |
| [`apphost`](apphost) | The Aspire host of the cloud storage emulators |
| [`eng`](eng), [`Justfile`](Justfile) | Validation, model-checking, trace, fuzzing, mutation, coverage, and documentation scripts |
| [`playground/DurableStorage`](../../playground/DurableStorage/README.md) | A sample Orleans app over Durable Storage |

## Build and test

From the repository root, `dotnet build test/Durable.Storage/Durable.Storage.slnf` builds every Storage project, and
`dotnet test --project test/Durable.Storage/<project>/<project>.csproj` runs one test project. The
[Justfile](Justfile) runs each check over the solution filter; run its recipes from this directory with
[just](https://github.com/casey/just) and PowerShell 7:

```powershell
just build    # restore and Release build
just format   # formatting and analyzer fixes, verified
just test     # default tests
just check    # build, formatting, default tests, the Debug leg, the documentation checks, and the ledger model
```

`just` lists every recipe, and [AGENTS.md](AGENTS.md#build-format-and-test) describes them. Model checking requires
Java 21, and the documentation checks (`just docs`) Node.js 22.22.2 or later.
`dotnet run --project apphost/Orleans.Durable.Storage.AppHost` starts the cloud storage emulators, and `just sample`
runs an Orleans app which keeps its grains' state in Durable Storage
([sample](../../playground/DurableStorage/README.md)).