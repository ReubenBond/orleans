# Microsoft Orleans Durable Storage CASPaxos Metadata Registers

## Introduction
Microsoft Orleans Durable Storage CASPaxos Metadata Registers serve Durable Storage's metadata registers (`IMetaStore`)
through Fast CASPaxos consensus hosted on the cluster's own silos. Voters on selected silos keep the registers' accepted
state in SQLite databases; every silo runs a proposer whose SQLite database reserves configuration-qualified ballots
durably. Voters communicate over Orleans RPC.

At bootstrap, every silo configures the identical `Voters` list (`InitialConfiguration = 1`), and bootstrap voters
validate agreement on its canonical digest during admission before serving. After the first reconfiguration, the
reserved configuration register (`__caspaxos/config`) is authoritative, and stale proposers discover active
configurations automatically. Operators replace lost voters, add voters, remove voters, or resume interrupted
reconfigurations through `CasPaxosReconfigurationService` (registered by `AddCasPaxosMetaStore`). A lost or retired
voter restarted on an empty database under its old identity is refused at startup; replacements use a fresh identity
configured with `JoiningVoter`.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Storage.CasPaxos --prerelease
```

## Example
```csharp
builder.UseOrleans(silo => silo
    .AddCasPaxosMetaStore(builder.Configuration.GetSection("CasPaxos"))
    .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage")));
```

The `CasPaxos` section lists `Voters`, each with an `Id`, an `Endpoint` (`ip:port`), and a `Database` path, and names
the silo's `ProposerId`, positive and unique in the cluster, and its `ProposerDatabase`. New voters joining in a later
configuration also set `JoiningVoter` with their fresh `Id`, `Database`, `JoinConfiguration`, and `AdmissionPeers`.
Resolve `CasPaxosReconfigurationService` from a silo's service provider to inspect or change voter membership
(`GetCurrentConfigurationAsync`, `ReplaceVoterAsync`, `AddVoterAsync`, `RemoveVoterAsync`, `ReconfigureAsync`,
`ResumeAsync`).

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Durable Storage specification](https://github.com/dotnet/orleans/blob/main/test/Durable.Storage/docs/spec/README.md)
- [Fast CASPaxos MetaStore operations guide](https://github.com/dotnet/orleans/blob/main/test/Durable.Storage/docs/operations/caspaxos.md)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)