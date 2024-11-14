// Copyright (c) Microsoft Corporation. All rights reserved.
// AgentWorkerHostingExtensions.cs

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Grpc.Server.Gateway;

namespace Orleans.Grpc.Server;

public static class WorkerHostingExtensions
{
    public static WebApplicationBuilder AddAgentService(this WebApplicationBuilder builder)
    {
        builder.Services.AddGrpc();
        builder.UseOrleans();
        builder.Services.TryAddSingleton(DistributedContextPropagator.Current);
        builder.Services.AddSingleton<WorkerConnectionManager>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WorkerConnectionManager>());

        return builder;
    }

    public static WebApplication MapAgentService(this WebApplication app)
    {
        app.MapGrpcService<GrpcWorkerGateway>();
        return app;
    }
}
