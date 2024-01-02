var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureProvisioning();

var azureStorage = builder.AddAzureStorage("az-storage").RunAsEmulator(builder => builder.WithImageTag("3.33.0"));
var azureBlobs = azureStorage.AddBlobs("state");
var azureTables = azureStorage.AddTables("clustering");

var orleans = builder.AddOrleans("orleans")
    .WithClustering(azureTables);

builder.AddProject<Projects.WorkflowsApp_Service>("workflowsapp-service")
    .WithReference(orleans)
    .WithReference(azureBlobs, "state")
    .WaitFor(azureStorage);

builder.Build().Run();
