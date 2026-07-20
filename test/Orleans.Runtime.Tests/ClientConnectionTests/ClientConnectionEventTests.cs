using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester;

/// <summary>
/// Tests for client connection events including cluster disconnection and gateway count changes.
/// </summary>
public class ClientConnectionEventTests
{
    [Fact, TestCategory("BVT")]
    public async Task ClientCachesOnlyActiveGrainDestinations()
    {
        var builder = new InProcessTestClusterBuilder();
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var grain = cluster.Client.GetGrain<IGuidTestGrain>(Guid.NewGuid());
        var grainSilo = await grain.GetSiloAddress();
        var destinationCache = (IMessageDestinationCache)(GrainReference)grain;
        Assert.True(destinationCache.TargetSilo.Matches(grainSilo));

        var gateway = ((InProcessSiloHandle)cluster.Silos[0])
            .ServiceProvider
            .GetRequiredService<MessageCenter>()
            .Gateway;
        var message = new Message
        {
            TargetGrain = grain.GetGrainId(),
            TargetSilo = grainSilo,
        };
        Assert.Equal(grainSilo, gateway.TryToReroute(message));

        message.TargetSilo = SiloAddress.New(grainSilo.Endpoint, grainSilo.Generation + 1);
        Assert.Null(gateway.TryToReroute(message));

        message.TargetSilo = cluster.Silos[1].GatewayAddress;
        Assert.Null(gateway.TryToReroute(message));
    }

    [Fact, TestCategory("SlowBVT")]
    public async Task CachedGatewayConnection_ReroutesAfterDisconnect()
    {
        var lostGateway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(client =>
        {
            client.Configure<GatewayOptions>(options => options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(0.5));
            client.AddGatewayCountChangedHandler((_, args) =>
            {
                if (args.NumberOfConnectedGateways == 1)
                {
                    lostGateway.TrySetResult();
                }
            });
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var grain = cluster.Client.GetGrain<ITestGrain>(Random.Shared.Next());
        await grain.SetLabel("before");
        var destinationCache = (IMessageDestinationCache)(GrainReference)grain;
        var originalReceiver = Assert.IsType<ClientOutboundConnection>(destinationCache.MessageReceiver);
        var originalGateway = originalReceiver.RemoteSiloAddress;
        var stoppedSilo = Assert.Single(cluster.Silos, silo => silo.GatewayAddress.Endpoint.Equals(originalGateway.Endpoint));

        await stoppedSilo.StopSiloAsync(stopGracefully: true);
        await lostGateway.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await grain.SetLabel("after").WaitAsync(TimeSpan.FromSeconds(20));
        await grain.GetRuntimeInstanceId().WaitAsync(TimeSpan.FromSeconds(20));

        var newReceiver = Assert.IsType<ClientOutboundConnection>(destinationCache.MessageReceiver);
        Assert.NotSame(originalReceiver, newReceiver);
        Assert.NotEqual(originalGateway.Endpoint, newReceiver.RemoteSiloAddress.Endpoint);
    }

    [Fact, TestCategory("SlowBVT")]
    public async Task EventSendWhenDisconnectedFromCluster()
    {
        var tcs = new TaskCompletionSource();
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(c =>
        {
            c.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(0.5));
            c.AddClusterConnectionLostHandler((sender, args) => tcs.TrySetResult());
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        // Burst lot of call, to be sure that we are connected to all silos
        for (int i = 0; i < 100; i++)
        {
            var grain = cluster.Client.GetGrain<ITestGrain>(i);
            await grain.SetLabel(i.ToString());
        }

        await cluster.StopAllSilosAsync();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact, TestCategory("SlowBVT")]
    public async Task GatewayChangedEventSentOnDisconnectAndReconnect()
    {
        var regainedGatewayTcs = new TaskCompletionSource();
        var lostGatewayTcs = new TaskCompletionSource();
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(c =>
        {
            c.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(0.5));
            c.AddGatewayCountChangedHandler((sender, args) =>
            {
                if (args.NumberOfConnectedGateways == 1)
                {
                    lostGatewayTcs.TrySetResult();
                }
                if (args.NumberOfConnectedGateways == 2)
                {
                    regainedGatewayTcs.TrySetResult();
                }
            });
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var silo = cluster.Silos[0];
        await silo.StopSiloAsync(true);

        await lostGatewayTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await cluster.RestartStoppedSecondarySiloAsync(silo.Name);

        // Clients need prodding to reconnect.
        var remainingAttempts = 90;
        bool reconnected;
        do
        {
            cluster.Client.GetGrain<ITestGrain>(Guid.NewGuid().GetHashCode()).SetLabel("test").Ignore();
            await regainedGatewayTcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            reconnected = regainedGatewayTcs.Task.IsCompleted;
        } while (!reconnected && --remainingAttempts > 0);

        Assert.True(reconnected, "Failed to reconnect to restarted gateway.");
    }
}
