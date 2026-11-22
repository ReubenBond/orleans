using Orleans.Durable.Storage.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Each image is pinned by its digest in EmulatorImages, which docs/spec/build-packaging.md §2 records, so a run is
// reproducible.

builder.AddEmulator("localstack", EmulatorImages.LocalStack)
    .WithEnvironment("SERVICES", "s3")
    .WithHttpEndpoint(targetPort: 4566, name: "gateway");

builder.AddEmulator("azurite", EmulatorImages.Azurite)
    .WithArgs("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck")
    .WithEndpoint(targetPort: 10000, name: "blob", scheme: "http")
    .WithEndpoint(targetPort: 10002, name: "table", scheme: "http");

// The Cloud Storage emulator: it checks preconditions and an upload's crc32c as the service does, and names a resumable
// session by the host its client addressed.
builder.AddEmulator("gcs", EmulatorImages.FakeGcsServer)
    .WithArgs("-scheme", "http", "-port", "4443")
    .WithHttpEndpoint(targetPort: 4443, name: "http");

// Google's Cloud Storage testbench answers a resumable session's status query as the service does, which fake-gcs-server
// does not, so the client's recovery of an interrupted upload runs against it.
builder.AddEmulator("gcs-testbench", EmulatorImages.GcsTestbench)
    .WithHttpEndpoint(targetPort: 9000, name: "http");

builder.AddEmulator("dynamodb", EmulatorImages.DynamoDb)
    .WithArgs("-jar", "DynamoDBLocal.jar", "-sharedDb", "-inMemory")
    .WithHttpEndpoint(targetPort: 8000, name: "http");

builder.AddEmulator("firestore", EmulatorImages.Firestore)
    .WithArgs(
        "gcloud",
        "beta",
        "emulators",
        "firestore",
        "start",
        "--host-port=0.0.0.0:8080",
        "--project=durable-storage-dev")
    .WithHttpEndpoint(targetPort: 8080, name: "http");

await builder.Build().RunAsync();
