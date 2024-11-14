using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("orleans-redis");

var orleansConfig = builder.AddOrleans("cluster")
    .WithClustering(redis);

builder.AddProject<DashboardToy_Frontend>("frontend")
    .WithReference(orleansConfig)
    .WithReplicas(5)
    .WaitFor(redis);

builder.Build().Run();
