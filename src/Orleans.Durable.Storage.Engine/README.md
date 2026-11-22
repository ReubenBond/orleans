# Microsoft Orleans Durable Storage Engine

## Introduction
Microsoft Orleans Durable Storage Engine holds the managed layers of `Microsoft.Orleans.Durable.Storage`: buffers,
threading, group commit, the write-ahead log, ledgers and the LedgerStore, object-store ledgers, metadata registers
(`IMetaStore`), the archive stores and archive logs, journals, and the cluster controller's decisions. It defines the
contracts which the technology packages implement, such as `IObjectStore` and `IMetaStore`, and has no dependency on the
Orleans runtime.

Applications reference `Microsoft.Orleans.Durable.Storage`, which hosts the engine in an Orleans silo, and a technology
package for their object store and metadata registers.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Storage.Engine --prerelease
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