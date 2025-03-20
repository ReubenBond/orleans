// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<ChaoticCluster_Silo>("silo");

builder.Build().Run();
