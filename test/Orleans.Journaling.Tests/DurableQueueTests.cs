using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Journaling.Tests;

public class DurableQueueTests : TestBase
{
    [Fact]
    public async Task DurableQueue_BasicOperations_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("testQueue", manager, codec, SessionPool);
        
        // Act - Enqueue items
        queue.Enqueue("one");
        queue.Enqueue("two");
        queue.Enqueue("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(3, queue.Count);
        
        // Act - Peek
        var peeked = queue.Peek();
        
        // Assert - Peek doesn't remove the item
        Assert.Equal("one", peeked);
        Assert.Equal(3, queue.Count);
        
        // Act - Dequeue
        var dequeued1 = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("one", dequeued1);
        Assert.Equal(2, queue.Count);
        
        // Act - Dequeue again
        var dequeued2 = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("two", dequeued2);
        Assert.Equal(1, queue.Count);
        
        // Act - Dequeue last item
        var dequeued3 = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("three", dequeued3);
        Assert.Equal(0, queue.Count);
    }
    
    [Fact]
    public async Task DurableQueue_Persistence_Test()
    {
        // Arrange
        var storage = CreateInMemoryStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        
        // First manager and queue
        var manager1 = new StateMachineManager(storage, logger, SessionPool);
        await manager1.InitializeAsync(CancellationToken.None);
        
        var codec = CodecProvider.GetCodec<string>();
        var queue1 = new DurableQueue<string>("testQueue", manager1, codec, SessionPool);
        
        // Act - Enqueue items and persist
        queue1.Enqueue("one");
        queue1.Enqueue("two");
        queue1.Enqueue("three");
        await manager1.WriteStateAsync(CancellationToken.None);
        
        // Create a new manager with the same storage
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);
        
        var queue2 = new DurableQueue<string>("testQueue", manager2, codec, SessionPool);
        
        // Assert - Queue should be recovered
        Assert.Equal(3, queue2.Count);
        Assert.Equal("one", queue2.Peek());
        
        // Act - Dequeue from recovered queue
        var dequeued = queue2.Dequeue();
        await manager2.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal("one", dequeued);
        Assert.Equal(2, queue2.Count);
    }
    
    [Fact]
    public async Task DurableQueue_ComplexValues_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<TestPerson>();
        var queue = new DurableQueue<TestPerson>("personQueue", manager, codec, SessionPool);
        
        // Act
        var person1 = new TestPerson { Id = 1, Name = "John", Age = 30 };
        var person2 = new TestPerson { Id = 2, Name = "Jane", Age = 25 };
        
        queue.Enqueue(person1);
        queue.Enqueue(person2);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(2, queue.Count);
        var peeked = queue.Peek();
        Assert.Equal("John", peeked.Name);
        
        // Act - Dequeue
        var dequeued = queue.Dequeue();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(1, queue.Count);
        Assert.Equal("John", dequeued.Name);
        Assert.Equal(30, dequeued.Age);
    }
    
    [Fact]
    public async Task DurableQueue_Clear_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("clearQueue", manager, codec, SessionPool);
        
        // Add items
        queue.Enqueue("one");
        queue.Enqueue("two");
        queue.Enqueue("three");
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act - Clear
        queue.Clear();
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue);
    }
    
    [Fact]
    public async Task DurableQueue_EmptyQueueOperations_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("emptyQueue", manager, codec, SessionPool);
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(0, queue.Count);
        
        // Act & Assert - Peek and Dequeue on empty queue should throw
        Assert.Throws<InvalidOperationException>(() => queue.Peek());
        Assert.Throws<InvalidOperationException>(() => queue.Dequeue());
    }
    
    [Fact]
    public async Task DurableQueue_Enumeration_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<string>();
        var queue = new DurableQueue<string>("enumQueue", manager, codec, SessionPool);
        
        // Add items
        var expectedItems = new List<string> { "one", "two", "three" };
        
        foreach (var item in expectedItems)
        {
            queue.Enqueue(item);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Act
        var actualItems = queue.ToList();
        
        // Assert - Items should be in same order as enqueued
        Assert.Equal(expectedItems, actualItems);
    }
    
    [Fact]
    public async Task DurableQueue_LargeNumberOfOperations_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<int>();
        var queue = new DurableQueue<int>("largeQueue", manager, codec, SessionPool);
        
        // Act - Enqueue many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            queue.Enqueue(i);
        }
        
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(itemCount, queue.Count);
        Assert.Equal(0, queue.Peek());
        
        // Create a new manager with the same storage to test recovery
        var storage = (VolatileStateMachineStorage)manager.Storage;
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);
        
        var queue2 = new DurableQueue<int>("largeQueue", manager2, codec, SessionPool);
        
        // Assert - Large queue is correctly recovered
        Assert.Equal(itemCount, queue2.Count);
        
        // Act - Dequeue all items and verify order
        for (int i = 0; i < itemCount; i++)
        {
            var item = queue2.Dequeue();
            Assert.Equal(i, item);
        }
        
        await manager2.WriteStateAsync(CancellationToken.None);
        Assert.Equal(0, queue2.Count);
    }
    
    [Fact]
    public async Task DurableQueue_Concurrent_EnqueueDequeue_Test()
    {
        // Arrange
        var manager = await CreateManagerAsync();
        var codec = CodecProvider.GetCodec<int>();
        var queue = new DurableQueue<int>("concurrentQueue", manager, codec, SessionPool);
        
        // Act - Simulate a queue with concurrent operations
        const int batchSize = 100;
        
        // First batch: add 100 items
        for (int i = 0; i < batchSize; i++)
        {
            queue.Enqueue(i);
        }
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Remove 50 items
        for (int i = 0; i < batchSize / 2; i++)
        {
            queue.Dequeue();
        }
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Add another 100 items
        for (int i = batchSize; i < batchSize * 2; i++)
        {
            queue.Enqueue(i);
        }
        await manager.WriteStateAsync(CancellationToken.None);
        
        // Assert
        Assert.Equal(batchSize + batchSize / 2, queue.Count); // Should have 150 items
        
        // Create a new manager with the same storage to test recovery
        var storage = (VolatileStateMachineStorage)manager.Storage;
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        var manager2 = new StateMachineManager(storage, logger, SessionPool);
        await manager2.InitializeAsync(CancellationToken.None);
        
        var queue2 = new DurableQueue<int>("concurrentQueue", manager2, codec, SessionPool);
        
        // Assert - Queue should be recovered with correct state and ordering
        Assert.Equal(batchSize + batchSize / 2, queue2.Count);
        
        // First values should be the second half of first batch
        for (int i = batchSize / 2; i < batchSize; i++)
        {
            var item = queue2.Dequeue();
            Assert.Equal(i, item);
        }
        
        // Then we should get the second batch
        for (int i = batchSize; i < batchSize * 2; i++)
        {
            var item = queue2.Dequeue();
            Assert.Equal(i, item);
        }
        
        await manager2.WriteStateAsync(CancellationToken.None);
        Assert.Equal(0, queue2.Count);
    }
}