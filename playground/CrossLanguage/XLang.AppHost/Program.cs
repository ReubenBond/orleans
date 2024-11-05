using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var redis = builder.AddRedis("orleans-redis");

var orleansConfig = builder.AddOrleans("cluster")
    .WithClustering(redis);

builder.AddProject<XLang_Server>("server")
    .WithReference(orleansConfig)
    .WithReplicas(2)
    .WaitFor(redis);

builder.Build().Run();
