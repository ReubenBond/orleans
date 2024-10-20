using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Host.UseOrleans((ctx, orleans) =>
{
#pragma warning disable ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    orleans.AddDistributedGrainDirectory();
    orleans.AddActivationRebalancer();
#pragma warning restore ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    orleans.AddActivationRepartitioner();
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    if (ctx.HostingEnvironment.IsDevelopment())
    {
        // During development time, we don't want to have to deal with
        // storage emulators or other dependencies. Just "Hit F5" to run.
        orleans
            .UseLocalhostClustering();
    }
    else
    {
        // In Kubernetes, we use environment variables and the pod manifest
        //orleansBuilder.UseKubernetesHosting();

        // Use Redis for clustering
        var redisAddress = $"redis:6379";
        orleans.UseRedisClustering(options => options.ConfigurationOptions = ConfigurationOptions.Parse(redisAddress));
    }

    /*
    orleans.UseDashboard(o =>
    {
        o.HostSelf = true;
        o.Port = 8888;
    });
    */
});

builder.Logging.AddFilter("Orleans.Runtime.GrainDirectory.DistributedGrainDirectory", LogLevel.Debug);
builder.Logging.AddFilter("Orleans.Runtime.GrainDirectory.GrainDirectoryReplica", LogLevel.Debug);
builder.Logging.AddFilter("Orleans.Runtime.SiloLifecycleSubject", LogLevel.Trace);
builder.Services.AddGrpc();
builder.Services.AddHostedService<WorkerService>();
var app = builder.Build();
app.MapGrpcService<ChaosService>();
app.Run();
