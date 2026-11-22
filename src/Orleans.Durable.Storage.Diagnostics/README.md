# Microsoft Orleans Durable Storage Diagnostics

## Introduction
Microsoft Orleans Durable Storage Diagnostics reports a fatal storage error, such as a failed flush of the write-ahead
log, within a bounded time and then terminates the process at once, so a silo never continues on storage whose
durability it can no longer establish. `FatalErrorHandler` names each fatal site with a `FatalErrorSite`. Every Durable
Storage package depends on it.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Storage.Diagnostics --prerelease
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