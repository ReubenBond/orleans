// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("orleans-redis");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis);

builder.AddProject<DashboardToy_Frontend>("frontend")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithReplicas(5);

builder.Build().Run();
