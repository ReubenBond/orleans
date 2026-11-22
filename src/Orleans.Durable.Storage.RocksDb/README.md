# Microsoft Orleans Durable Storage RocksDB Index

## Introduction
Microsoft Orleans Durable Storage RocksDB Index persists the derived journal indexes of Durable Storage in RocksDB on the
silo's disk. The index is derived from the journals' committed state: the node rebuilds it after its loss, and the
journals remain authoritative.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Storage.RocksDb --prerelease
```

## Example
```csharp
builder.UseOrleans(silo => silo
    .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage"))
    .AddDurableJournalIndex(root => new RocksDbJournalIndex(Path.Combine(root, "index"))));
```

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