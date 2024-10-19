using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var redis = builder.AddRedis("orleans-redis");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis);
orleans.EnableDistributedTracing = false;

builder.AddProject<ActivationRebalancing_Cluster>("silo").WithReplicas(5).WithReference(orleans);
builder.AddProject<ActivationRebalancing_Frontend>("frontend").WithReference(orleans);

builder.Build().Run();
