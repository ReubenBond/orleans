using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests.General;

[TestCategory("BVT")]
public class MessageDestinationCacheForwardingTests : OrleansTestingBase, IClassFixture<MessageDestinationCacheForwardingTests.Fixture>
{
    private readonly Fixture _fixture;

    public MessageDestinationCacheForwardingTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ForwardedRequest_DoesNotCachePreviousHopConnection()
    {
        var cluster = _fixture.HostedCluster;
        var source = (InProcessSiloHandle)cluster.Primary;
        var originalTarget = cluster.SecondarySilos[0];
        var migrationTarget = cluster.SecondarySilos[1];
        var grainFactory = source.ServiceProvider.GetRequiredService<IGrainFactory>();

        RequestContext.Set(IPlacementDirector.PlacementHintKey, originalTarget.SiloAddress);
        var grain = grainFactory.GetGrain<IMigrationTestGrain>(Random.Shared.NextInt64());
        var originalAddress = await grain.GetGrainAddress();
        Assert.Equal(originalTarget.SiloAddress, originalAddress.SiloAddress);

        var observer = ((InProcessSiloHandle)migrationTarget).ServiceProvider.GetRequiredService<ForwardedMessageObserver>();
        var forwardedMessage = observer.Observe(grain.GetGrainId());

        RequestContext.Set(IPlacementDirector.PlacementHintKey, migrationTarget.SiloAddress);
        await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

        GrainAddress newAddress;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        do
        {
            cts.Token.ThrowIfCancellationRequested();
            newAddress = await grain.GetGrainAddress();
        } while (newAddress.ActivationId == originalAddress.ActivationId);

        Assert.Equal(migrationTarget.SiloAddress, newAddress.SiloAddress);
        Assert.True(
            await forwardedMessage.WaitAsync(TimeSpan.FromSeconds(10)),
            "The forwarded request cached its previous-hop connection as the response receiver.");
    }

    public sealed class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 3;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }

    public sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ForwardedMessageObserver>();
                services.AddSingleton<IMessageStatisticsSink>(serviceProvider => serviceProvider.GetRequiredService<ForwardedMessageObserver>());
            });
        }
    }

    internal sealed class ForwardedMessageObserver : IMessageStatisticsSink
    {
        private readonly SiloAddress _localSilo;
        private readonly ConcurrentDictionary<GrainId, TaskCompletionSource<bool>> _observations = new();

        public ForwardedMessageObserver(ILocalSiloDetails localSiloDetails)
        {
            _localSilo = localSiloDetails.SiloAddress;
        }

        public Task<bool> Observe(GrainId grainId)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_observations.TryAdd(grainId, completion));
            return completion.Task;
        }

        public Action<Message> GetMessageObserver() => OnMessage;

        private void OnMessage(Message message)
        {
            if (message.Direction is Message.Directions.Request
                && message.ForwardCount > 0
                && message.TargetSilo is { } targetSilo
                && targetSilo.Matches(_localSilo)
                && _observations.TryRemove(message.TargetGrain, out var completion))
            {
                completion.TrySetResult(message.MessageReceiver is null);
            }
        }
    }
}
