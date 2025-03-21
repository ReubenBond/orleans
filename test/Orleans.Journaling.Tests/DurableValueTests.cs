using Microsoft.Extensions.Logging;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Journaling.Tests;

public class DurableValueTests : TestBase
{
    [Fact]
    public async Task DurableValue_BasicOperations_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var durableValue = new DurableValue<string>("testValue", manager, codec, SessionPool);

        // Act - Set initial value
        durableValue.Value = "Hello World";
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Hello World", durableValue.Value);

        // Act - Update value
        durableValue.Value = "Updated Value";
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Updated Value", durableValue.Value);
    }

    [Fact]
    public async Task DurableValue_Persistence_Test()
    {
        // Arrange
        var storage = CreateInMemoryStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();

        // First manager and value
        var manager1 = new StateMachineManager(storage, logger, SessionPool);
        await manager1.InitializeAsync(CancellationToken.None);

        var codec = CodecProvider.GetCodec<int>();
        var durableValue1 = new DurableValue<int>("counter", manager1, codec, SessionPool);

        // Act - Modify and persist
        durableValue1.Value = 42;
        await manager1.WriteStateAsync(CancellationToken.None);

        // Create a new manager with the same storage
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);

        var durableValue2 = new DurableValue<int>("counter", manager2, codec, SessionPool);

        // Assert - Value should be recovered
        Assert.Equal(42, durableValue2.Value);
    }

    [Fact]
    public async Task DurableValue_NullValue_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string?>();
        var durableValue = new DurableValue<string?>("nullableValue", manager, codec, SessionPool);

        // Act - Set to null
        durableValue.Value = null;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Null(durableValue.Value);

        // Act - Update to non-null
        durableValue.Value = "Not null anymore";
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Not null anymore", durableValue.Value);

        // Act - Update back to null
        durableValue.Value = null;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Null(durableValue.Value);
    }

    [Fact]
    public async Task DurableValue_ComplexType_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<TestPerson>();
        var durableValue = new DurableValue<TestPerson>("person", manager, codec, SessionPool);

        // Act
        var person = new TestPerson { Id = 1, Name = "John Doe", Age = 30 };
        durableValue.Value = person;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(durableValue.Value);
        Assert.Equal(1, durableValue.Value.Id);
        Assert.Equal("John Doe", durableValue.Value.Name);
        Assert.Equal(30, durableValue.Value.Age);

        // Act - Update property
        durableValue.Value.Age = 31;
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert
        Assert.Equal(31, durableValue.Value.Age);
    }
}
