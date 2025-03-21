using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Base class for journaling tests with common setup using InProcessTestCluster
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected InProcessTestCluster Cluster { get; }
    protected IClusterClient Client => Cluster.Client;

    public IntegrationTestBase()
    {
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureSilo((options, siloBuilder) =>
        {
            siloBuilder.AddStateMachineStorage();
            siloBuilder.Services.AddScoped<IStateMachineStorage, VolatileStateMachineStorage>();
        });
        ConfigureTestCluster(builder);
        Cluster = builder.Build();
    }

    protected virtual void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
    }

    public virtual async Task InitializeAsync()
    {
        await Cluster.DeployAsync();
    }

    public virtual async Task DisposeAsync()
    {
        if (Cluster != null)
        {
            await Cluster.DisposeAsync();
        }
    }
}
