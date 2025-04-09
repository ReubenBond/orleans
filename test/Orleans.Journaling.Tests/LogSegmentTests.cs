using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

public sealed class AzureStorageLogSegmentTests : LogSegmentTests
{
    public AzureStorageLogSegmentTests()
    {
        JournalingAzureStorageTestConfiguration.CheckPreconditionsOrThrow();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.Configure<AzureAppendBlobStateMachineStorageOptions>(options => JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options));
        services.AddSingleton<AzureAppendBlobStateMachineStorageProvider>();
        services.AddFromExisting<IStateMachineStorageProvider, AzureAppendBlobStateMachineStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureAppendBlobStateMachineStorageProvider>();
    }
}

public sealed class InMemoryLogSegmentTests : LogSegmentTests
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IStateMachineStorageProvider, VolatileStateMachineStorageProvider>();
    }
}

public abstract class LogSegmentTests : IAsyncLifetime
{
    private IServiceProvider _serviceProvider = null!;
    private SiloLifecycleSubject? _siloLifecycle;
    private IStateMachineStorageProvider _storageProvider = null!;

    public virtual async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        _siloLifecycle = new SiloLifecycleSubject(_serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        _storageProvider = _serviceProvider.GetRequiredService<IStateMachineStorageProvider>();
        var participants = _serviceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
        foreach (var participant in participants)
        {
            participant.Participate(_siloLifecycle);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _siloLifecycle.OnStart(cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (_siloLifecycle is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _siloLifecycle.OnStop(cts.Token);
        }
    }

    protected abstract void ConfigureServices(IServiceCollection services);

    /*
    [Fact]
    public void AppendedSegmentsAreEnumerable()
    {
        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        using var segment = new LogSegment();
        var expectedBuffers = new List<(uint Id, byte[] Data)>();
        for (var i = 0; i < 100; i++)
        {
            var buf = new byte[i * 100];
            Array.Fill(buf, (byte)i);

            for (var id = 0U; id < 5; id++)
            {
                var streamId = new StateMachineId(id);
                expectedBuffers.Add((id, buf));
                segment.AppendEntry(0, streamId, buf);
            }
        }

        var entries = new List<LogSegment.Entry>();
        foreach (var entry in segment.GetEntryEnumerator(0))
        {
            entries.Add(entry);
        }

        Assert.Equal(expectedBuffers.Count, entries.Count);
        for (var i = 0; i < expectedBuffers.Count; i++)
        {
            var (expectedId, expectedData) = expectedBuffers[i];
            var (actualId, actualData) = entries[i];
            Assert.Equal(expectedId, actualId.Value);
            Assert.Equal(expectedData, actualData.ToArray());
        }
    }
    */

    [Fact]
    public async Task DurableListTest()
    {
        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", Guid.NewGuid().ToString()));
        var storage = _storageProvider.Create(grainContext);

        var manager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var list = new DurableList<string>("fooey", manager, codecProvider.GetCodec<string>(), sessionPool);
        await manager.InitializeAsync(CancellationToken.None);

        // NOTE TO SELF: when we register the state machine, we need a signal which indicates when the state machine is ready to be used.
        // In practice, we are waiting for OnActivateAsync
        for (var i = 0; i < 10; ++i)
        {
            list.Add(i.ToString());
        }
        //Assert.Equal(10, list.Count);

        await manager.WriteStateAsync(CancellationToken.None);

        // TODO: make sure that OnRecoveryCompleted is not called before recovery has completed!

        // TODO: throw in state machine if trying to mutate before recovery has completed

        // TODO: throw in state machine MANAGER if trying to append log entries before recovery has completed

        var newManager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var newList = new DurableList<string>("fooey", newManager, codecProvider.GetCodec<string>(), sessionPool);
        await newManager.InitializeAsync(CancellationToken.None);

        var originalList = list.ToList();
        var recreatedList = newList.ToList();
        Assert.Equal(originalList, recreatedList);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DurableList_Snapshot_Test()
    {
        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
        var grainContext = new TestGrainContext(GrainId.Create("test-grain", Guid.NewGuid().ToString()));
        var storage = _storageProvider.Create(grainContext);

        var manager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var list = new DurableList<string>("fooey", manager, codecProvider.GetCodec<string>(), sessionPool);
        await manager.InitializeAsync(CancellationToken.None);

        // NOTE TO SELF: when we register the state machine, we need a signal which indicates when the state machine is ready to be used.
        // In practice, we are waiting for OnActivateAsync
        list.Clear();
        for (var c = 0; c < 15; ++c)
        {
            for (var i = 0; i < 10; ++i)
            {
                list.Add(i.ToString());
            }
            //Assert.Equal(10, list.Count);

            await manager.WriteStateAsync(CancellationToken.None);
        }

        // TODO: make sure that OnRecoveryCompleted is not called before recovery has completed!

        // TODO: throw in state machine if trying to mutate before recovery has completed

        // TODO: throw in state machine MANAGER if trying to append log entries before recovery has completed

        var newManager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var newList = new DurableList<string>("fooey", newManager, codecProvider.GetCodec<string>(), sessionPool);
        await newManager.InitializeAsync(CancellationToken.None);

        var originalList = list.ToList();
        var recreatedList = newList.ToList();
        Assert.Equal(originalList, recreatedList);
        await Task.CompletedTask;
    }

    internal sealed class TestGrainContext(GrainId grainId) : IGrainContext
    {
        public GrainReference GrainReference => throw new NotImplementedException();
        public GrainId GrainId => grainId;
        public object? GrainInstance  => throw new NotImplementedException();
        public ActivationId ActivationId  => throw new NotImplementedException();
        public GrainAddress Address  => throw new NotImplementedException();
        public IServiceProvider ActivationServices  => throw new NotImplementedException();
        public IGrainLifecycle ObservableLifecycle  => throw new NotImplementedException();
        public IWorkItemScheduler Scheduler  => throw new NotImplementedException();
        public Task Deactivated  => throw new NotImplementedException();

        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public bool Equals(IGrainContext? other) => throw new NotImplementedException();
        public TComponent? GetComponent<TComponent>() where TComponent : class => throw new NotImplementedException();
        public TTarget? GetTarget<TTarget>() where TTarget : class => throw new NotImplementedException();
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void ReceiveMessage(object message) => throw new NotImplementedException();
        public void Rehydrate(IRehydrationContext context) => throw new NotImplementedException();
        public void SetComponent<TComponent>(TComponent? value) where TComponent : class => throw new NotImplementedException();
    }
}
