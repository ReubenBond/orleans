# Microsoft Orleans Durable Storage File System Providers

## Introduction
Microsoft Orleans Durable Storage File System Providers store Durable Storage's archive objects and metadata registers in
a local or shared file system: `FileSystemObjectStore` implements `IObjectStore`, and `FileSystemMetaStore` implements
`IMetaStore`. They suit development, tests, and single-machine deployments.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Storage.FileSystem --prerelease
```

## Example
```csharp
builder.Services.AddSingleton<IMetaStore>(new FileSystemMetaStore(registersDirectory, TimeProvider.System));
builder.Services.AddSingleton<IObjectStore>(new FileSystemObjectStore(objectsDirectory, TimeProvider.System));
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