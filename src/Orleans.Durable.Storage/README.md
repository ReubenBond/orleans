# Microsoft Orleans Durable Storage

## Introduction
Microsoft Orleans Durable Storage is layered journal storage for `Microsoft.Orleans.Durable`. Each silo hosts one storage
node, which keeps its journals' recent writes in a replicated write-ahead log on the silo's own disk and drains older
history to an archive store. Journaled grains are placed on the silo whose node is their journal's primary, so their
writes need no forwarding, and a surviving silo takes over a lost silo's journals.

`AddDurableJournalStorage` registers the silo's node as Orleans Journaling's journal storage provider, under the default
provider name or a name of your choice. The silo's services supply the cluster's metadata registers (`IMetaStore`) and,
for the LSM archive, its object store (`IObjectStore`), from one of the technology packages:

- `Microsoft.Orleans.Durable.Storage.AzureStorage`, `Microsoft.Orleans.Durable.Storage.S3`, or
  `Microsoft.Orleans.Durable.Storage.GoogleCloud` for cloud object stores and metadata registers.
- `Microsoft.Orleans.Durable.Storage.CasPaxos` for metadata registers served by consensus voters hosted on silos.
- `Microsoft.Orleans.Durable.Storage.FileSystem` for a single machine's object store and metadata registers.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Durable.Storage --prerelease
```

## Example
```csharp
builder.Services.AddSingleton<IMetaStore>(new AzureTableMetaStore(registers));
builder.Services.AddSingleton<IObjectStore>(new AzureBlobObjectStore(objects));
builder.UseOrleans(silo => silo
    .UseAzureStorageClustering(options => options.TableServiceClient = tables)
    .AddDurableJournalStorage(builder.Configuration.GetSection("DurableStorage")));
```

The `DurableStorage` section's `Node` subsection names the cluster, the node's root directory on the silo's disk, and the
archive store; its `Control` subsection tunes the cluster's controller.

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