# Microsoft Orleans Durable Storage Azure Storage Providers

## Introduction
Microsoft Orleans Durable Storage Azure Storage Providers store Durable Storage's archive objects in Azure Blob Storage
(`AzureBlobObjectStore`) and its metadata registers in Azure Table Storage (`AzureTableMetaStore`) or Azure Blob Storage
(`AzureBlobMetaStore`). `AddAzureArchiveLogs` registers the archive-log store, which keeps each journal's history as
append blobs that the `Microsoft.Orleans.Durable.AzureStorage` journal provider can read.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Storage.AzureStorage --prerelease
```

## Example
```csharp
builder.Services.AddSingleton<IMetaStore>(new AzureTableMetaStore(registers));
builder.Services.AddSingleton<IObjectStore>(new AzureBlobObjectStore(objects));
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