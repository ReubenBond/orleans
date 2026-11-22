# Clog

Clog stores journals, fine-grained per-key logs of entries, for applications co-hosted with it, and is the storage for
Orleans Journaling. Writes are replicated to the local disks of `N` nodes (live ledgers) and migrate to the Archive: an
LSM tree in cloud object storage, or per-key logs on an Orleans journal provider. The keyspace is divided into dyadic
ranges, each owned by one sequenced ledger at a time; a ledger map in a replicated register decides ownership, and
transitions move ranges by sealing and recovering ledgers and writing that register, without copying data.

Start with the [specification](docs/spec/README.md): its overview motivates the design, shows how the layers
work together, and names the owner of each guarantee. [AGENTS.md](AGENTS.md) covers the repository layout and
maintenance rules.

## Implementation status

| Part | Status |
| --- | --- |
| Buffers, the WAL, ledger replicas (memory, LedgerStore), the ledger protocol (`Ledger`, `LedgerQuorum`, `LedgerWriter`), MetaStore (memory, file system, Fast CASPaxos, Azure, AWS, Google Cloud), state database | Implemented building blocks |
| Ledgers, the Archive, Journals over ledgers, Orleans composition (including replica traffic over grain calls) | Specified and not yet implemented |

[§5 of the specification](docs/spec/README.md#5-implementation-status) tracks the details.

## Build and test

Clog builds with [just](https://github.com/casey/just) (`winget install Casey.Just`, `cargo install just`, or
`brew install just`) and PowerShell 7. Clockwork and Fast CASPaxos are git submodules under `vendor/`.

```powershell
git clone --recurse-submodules https://github.com/ReubenBond/clog.git
cd clog
just sync     # submodules at their recorded commits and the pinned .NET SDK under .dotnet
just build    # restore and Release build
just test     # default tests
just check    # build, formatting, default tests, the Debug leg, the documentation checks, and the ledger model
```

`just` lists every recipe, and [AGENTS.md](AGENTS.md#build-format-and-test) describes them. Model checking requires
Java 21, and the documentation checks (`just docs`) Node.js 22.22.2 or later.
`dotnet run --project apphost\Orleans.Durable.Storage.AppHost` starts the cloud storage emulators, and `just sample`
runs an Orleans app which keeps its grains' state in Clog ([samples](samples/README.md)).
