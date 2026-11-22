using System.Globalization;
using Orleans.Durable.Storage.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Azurite at the image test/Durable.Storage/apphost/Orleans.Durable.Storage.AppHost pins, through the file both AppHosts share (test/Durable.Storage/docs/spec/build-packaging.md §2).
var azurite = builder.AddEmulator("azurite", EmulatorImages.Azurite)
    .WithArgs("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck")
    .WithEndpoint(targetPort: 10000, name: "blob", scheme: "http")
    .WithEndpoint(targetPort: 10002, name: "table", scheme: "http");

// Azurite's well-known development account, which Microsoft publishes for the emulator; a deployment authenticates with
// Microsoft Entra ID instead. The client addresses the account by IP address, from which the SDK reads the account from
// the path.
const string Account = "devstoreaccount1";
const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
var blob = azurite.GetEndpoint("blob");
var table = azurite.GetEndpoint("table");
var storage = ReferenceExpression.Create(
    $"DefaultEndpointsProtocol=http;AccountName={Account};AccountKey={AccountKey};BlobEndpoint=http://127.0.0.1:{blob.Property(EndpointProperty.Port)}/{Account};TableEndpoint=http://127.0.0.1:{table.Property(EndpointProperty.Port)}/{Account};");

// Each run is a new Clog cluster over a new Azurite: the AppHost, as the deployment, creates each silo's node root anew
// under test/Durable.Storage/artifacts/sample, where it survives the silo's restarts within the run.
var roots = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "test", "Durable.Storage", "artifacts", "sample"));
if (Directory.Exists(roots))
{
    Directory.Delete(roots, recursive: true);
}

// Three silos, each ledger's Control:Replicas (N = 3): when one dies, a takeover keeps its dead replica while a quorum of
// two lives, and repairs it out once a node is free (orleans.md §5.7, OR-I5).
const int Silos = 3;
var cluster = Guid.NewGuid();
for (var ordinal = 0; ordinal < Silos; ordinal++)
{
    var name = string.Create(CultureInfo.InvariantCulture, $"silo-{ordinal}");
    var root = Directory.CreateDirectory(Path.Combine(roots, name)).FullName;
    _ = builder.AddProject<Projects.Orleans_Durable_Storage_Samples_Journaling>(name, launchProfileName: null)
        .WithHttpEndpoint(port: 5080 + ordinal)
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("ConnectionStrings__storage", storage)
        .WithEnvironment("Sample__SiloName", name)
        .WithEnvironment("Sample__SiloPort", (11111 + ordinal).ToString(CultureInfo.InvariantCulture))
        .WithEnvironment("Sample__GatewayPort", (30000 + ordinal).ToString(CultureInfo.InvariantCulture))
        .WithEnvironment("DurableStorage__Node__Cluster", cluster.ToString())
        .WithEnvironment("DurableStorage__Node__Root", root)
        .WithEnvironment("DurableStorage__Node__Ordinal", ordinal.ToString(CultureInfo.InvariantCulture))
        .WaitFor(azurite);
}

await builder.Build().RunAsync();
