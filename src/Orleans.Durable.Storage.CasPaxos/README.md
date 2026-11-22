# Microsoft Orleans Durable Storage CASPaxos Metadata Registers

## Introduction
Microsoft Orleans Durable Storage CASPaxos Metadata Registers serve Durable Storage's metadata registers (`IMetaStore`)
through Fast CASPaxos consensus hosted on the cluster's own silos. A fixed set of voters, each with a durable identity,
a silo endpoint, and a SQLite database, keeps the registers' accepted state; every silo runs a proposer, whose SQLite
database reserves its ballots durably. Voters communicate over Orleans RPC.

Every silo configures the same voters. The silo whose advertised endpoint matches a voter's endpoint hosts that voter,
which opens and validates its database before the silo's storage node registers. A new voter joins once a majority of
the other voters admits it, so a deployment starts its voter silos together. A voter restarted on a lost database under
its old identity is refused at startup; replacing a voter whose state is lost is not supported.

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
the silo's `ProposerId`, positive and unique in the cluster, and its `ProposerDatabase`.

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Durable Storage specification](https://github.com/dotnet/orleans/blob/main/test/Durable.Storage/docs/spec/README.md)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)