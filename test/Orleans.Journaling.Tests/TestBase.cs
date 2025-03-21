using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Base class for journaling tests with common setup
/// </summary>
public abstract class TestBase
{
    protected readonly ServiceProvider ServiceProvider;
    protected readonly SerializerSessionPool SessionPool;
    protected readonly ICodecProvider CodecProvider;
    protected readonly ILoggerFactory LoggerFactory;

    protected TestBase()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging(builder => builder.AddConsole());
        
        ServiceProvider = services.BuildServiceProvider();
        SessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        CodecProvider = ServiceProvider.GetRequiredService<ICodecProvider>();
        LoggerFactory = ServiceProvider.GetRequiredService<ILoggerFactory>();
    }

    /// <summary>
    /// Creates an in-memory storage for testing
    /// </summary>
    protected VolatileStateMachineStorage CreateInMemoryStorage()
    {
        return new VolatileStateMachineStorage();
    }

    /// <summary>
    /// Creates a state machine manager with in-memory storage
    /// </summary>
    protected async Task<StateMachineManager> CreateManagerAsync()
    {
        var storage = CreateInMemoryStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        var manager = new StateMachineManager(storage, logger, SessionPool);
        await manager.InitializeAsync(CancellationToken.None);
        return manager;
    }
}