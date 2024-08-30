using Projects;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureProvisioning();
var cosmos = builder.AddAzureCosmosDB("cosmos");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(cosmos);

// Comment this out once Aspire no longer requires a 'workload' to build.
builder.AddProject<DashboardToy_Frontend>("frontend")
    .WithReference(orleans)
    .WithReplicas(1);

builder.Build().Run();
