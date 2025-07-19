#nullable enable
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests;

/// <summary>
/// Tests support for grain methods which return <see cref="IAsyncEnumerable{T}"/>.
/// These tests verify Orleans' ability to handle streaming results from grain methods,
/// including batching, error handling, cancellation, and proper resource cleanup.
/// Orleans uses a grain extension mechanism to manage the lifecycle of async enumerators
/// across the distributed system.
/// </summary>
public class AsyncEnumerableGrainCallTests : HostedTestClusterEnsureDefaultStarted
{
    public AsyncEnumerableGrainCallTests(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    /// <summary>
    /// Tests basic async enumerable functionality where a grain produces values that are consumed by the client.
    /// Verifies that values are correctly transmitted and the enumerator is properly disposed after use.
    /// This demonstrates Orleans' support for streaming data from grains without keeping all data in memory.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(200);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        await foreach (var entry in grain.GetValues())
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(5, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests error handling in async enumerable streams when an exception is thrown during enumeration.
    /// Verifies that exceptions are properly propagated to the client and resources are cleaned up.
    /// The errorIndex parameter determines when the error occurs, testing both immediate and delayed errors.
    /// The waitAfterYield parameter tests error handling with and without async delays after yielding values.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_Throws(int errorIndex, bool waitAfterYield)
    {
        const string ErrorMessage = "This is my error!";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            await foreach (var entry in grain.GetValuesWithError(errorIndex, waitAfterYield, ErrorMessage).WithBatchSize(10))
            {
                values.Add(entry);
                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (InvalidOperationException iox)
        {
            Assert.Equal(ErrorMessage, iox.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests cancellation handling in async enumerable streams when the grain cancels the enumeration.
    /// Verifies that OperationCanceledException is properly propagated and resources are cleaned up.
    /// This tests Orleans' ability to handle cooperative cancellation in distributed streaming scenarios.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_Cancellation(int errorIndex, bool waitAfterYield)
    {
        // This special error message is interpreted to indicate that cancellation
        // should occur when the index is reached.
        const string ErrorMessage = "cancel";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            await foreach (var entry in grain.GetValuesWithError(errorIndex, waitAfterYield, ErrorMessage).WithBatchSize(10))
            {
                values.Add(entry);
                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (OperationCanceledException oce)
        {
            var expectedMessage = new OperationCanceledException().Message;
            Assert.Equal(expectedMessage, oce.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests client-side cancellation of async enumerable streams using CancellationToken.
    /// Verifies that cancellation requests from the client are properly handled, including:
    /// - Preemptive cancellation (before enumeration starts)
    /// - Mid-stream cancellation (during enumeration)
    /// - Proper cleanup and disposal of server-side resources
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_CancellationToken(int errorIndex, bool waitAfterYield)
    {
        const string ErrorMessage = "Throwing!";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            using var cts = new CancellationTokenSource();
            if (errorIndex == 0)
            {
                cts.Cancel();
            }

            await foreach (var entry in grain.GetValuesWithError(int.MaxValue, waitAfterYield, ErrorMessage, cts.Token).WithBatchSize(10))
            {
                values.Add(entry);
                if (values.Count == errorIndex)
                {
                    cts.Cancel();
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (OperationCanceledException oce)
        {
            var expectedMessage = new OperationCanceledException().Message;
            Assert.Equal(expectedMessage, oce.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        if (errorIndex == 0)
        {
            // Check that the enumerator was not disposed since it was cancelled preemptively and therefore no call should have been made.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
        if (errorIndex > 0)
        {
            // Check that the enumerator is disposed, but only if it was not cancelled preemptively.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
    }

    /// <summary>
    /// Tests client-side cancellation using the WithCancellation extension method.
    /// Similar to CancellationToken test but uses the extension method approach for cancellation.
    /// Verifies that the WithCancellation extension properly integrates with Orleans' async enumerable support.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Observable")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public async Task ObservableGrain_AsyncEnumerable_CancellationToken_WithCancellationExtension(int errorIndex, bool waitAfterYield)
    {
        const string ErrorMessage = "Throwing!";
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        try
        {
            using var cts = new CancellationTokenSource();
            if (errorIndex == 0)
            {
                cts.Cancel();
            }

            await foreach (var entry in grain.GetValuesWithError(int.MaxValue, waitAfterYield, ErrorMessage).WithBatchSize(10).WithCancellation(cts.Token))
            {
                values.Add(entry);
                if (values.Count == errorIndex)
                {
                    cts.Cancel();
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        }
        catch (OperationCanceledException oce)
        {
            var expectedMessage = new OperationCanceledException().Message;
            Assert.Equal(expectedMessage, oce.Message);
        }

        Assert.Equal(errorIndex, values.Count);

        if (errorIndex == 0)
        {
            // Check that the enumerator was not disposed since it was cancelled preemptively and therefore no call should have been made.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
        if (errorIndex > 0)
        {
            // Check that the enumerator is disposed, but only if it was not cancelled preemptively.
            var grainCalls = await grain.GetIncomingCalls();
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
            Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
        }
    }

    /// <summary>
    /// Tests batching optimization for async enumerable streams.
    /// Verifies that Orleans automatically batches multiple values to reduce network round-trips.
    /// This optimization is crucial for performance when streaming many small values.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Batch()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues())
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));
        Assert.True(moveNextCallCount < values.Count);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests custom batch size configuration for async enumerable streams.
    /// Verifies that the WithBatchSize extension method correctly controls the number of items per batch.
    /// This allows clients to tune the trade-off between latency and throughput.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SplitBatch()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues().WithBatchSize(25))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));
        Assert.True(moveNextCallCount < values.Count);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests disabling batching by setting batch size to 1.
    /// Verifies that each value results in a separate network call when batching is disabled.
    /// This mode provides lowest latency but highest overhead for streaming scenarios.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_NoBatching()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        foreach (var value in Enumerable.Range(0, 50))
        {
            await grain.OnNext(value.ToString());
        }

        await grain.Complete();

        var values = new List<string>();
        await foreach (var entry in grain.GetValues().WithBatchSize(1))
        {
            values.Add(entry);
            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(50, values.Count);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));

        // One call for every value and one final call to complete the enumeration
        Assert.Equal(values.Count + 1, moveNextCallCount);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests cancellation during active enumeration.
    /// Verifies that cancelling mid-stream properly stops enumeration and cleans up resources.
    /// This simulates real-world scenarios where clients need to stop consuming data early.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_WithCancellation()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(200);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        using var cts = new CancellationTokenSource();
        try
        {
            await foreach (var entry in grain.GetValues().WithCancellation(cts.Token))
            {
                values.Add(entry);
                if (values.Count == 3)
                {
                    cts.Cancel();
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }

            Assert.Fail("Expected an exception to be thrown");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.Equal(3, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests async enumerable behavior with a slow-producing grain.
    /// Verifies that the client can stop consuming before all values are produced.
    /// This tests Orleans' ability to handle backpressure and early termination in streaming scenarios.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SlowProducer()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await Task.Delay(2000);
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        await foreach (var entry in grain.GetValues())
        {
            values.Add(entry);
            if (values.Count == 2)
            {
                break;
            }

            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(2, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests async enumerable behavior with a slow-consuming client.
    /// Verifies that the enumerator is not prematurely cleaned up when the client consumes slowly.
    /// Uses diagnostic listeners to monitor the cleanup timer behavior.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SlowConsumer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cleanupInterval = TimeSpan.FromMilliseconds(1_000);
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());
        using var listener = new AsyncEnumerableGrainExtensionListener(grain.GetGrainId(), cleanupInterval);

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 3))
            {
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        await foreach (var entry in grain.GetValues().WithBatchSize(1))
        {
            values.Add(entry);

            // Sleep for 1 cycle before reading the next value.
            // The enumerator should not be cleaned up.
            var initialCleanupCount = listener.CleanupCount;
            while (listener.CleanupCount == initialCleanupCount)
            {
                await Task.Delay(cleanupInterval / 10, cts.Token);
            }

            Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
        }

        Assert.Equal(3, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests enumerator eviction when a client consumes too slowly.
    /// Verifies that Orleans properly cleans up abandoned enumerators after a timeout period.
    /// This prevents resource leaks when clients fail to complete enumeration.
    /// The test ensures proper error handling when trying to continue after eviction.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_SlowConsumer_Evicted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cleanupInterval = TimeSpan.FromMilliseconds(1_000);
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());
        using var listener = new AsyncEnumerableGrainExtensionListener(grain.GetGrainId(), cleanupInterval);

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 5))
            {
                await grain.OnNext(value.ToString());
            }

            await grain.Complete();
        });

        var values = new List<string>();
        try
        {
            await foreach (var entry in grain.GetValues().WithBatchSize(1))
            {
                values.Add(entry);

                // After the 3rd iteration, sleep for longer than the cleanup duration
                // and wait for the enumerator to be cleaned up.
                if (values.Count >= 3)
                {
                    var initialCleanupCount = listener.CleanupCount;
                    while (listener.CleanupCount < initialCleanupCount + 2)
                    {
                        await Task.Delay(cleanupInterval, cts.Token);
                    }
                }

                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }

            Assert.Fail("Expected an exception to be thrown");
        }
        catch (EnumerationAbortedException ex)
        {
            Assert.Contains("the remote target does not have a record of this enumerator", ex.Message);
        }

        Assert.Equal(3, values.Count);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests async enumerable behavior when the grain is deactivated during enumeration.
    /// Verifies that grain deactivation properly terminates active enumerations with an appropriate error.
    /// This ensures clean shutdown and prevents hanging clients when grains are deactivated.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Deactivate()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var producer = Task.Run(async () =>
        {
            foreach (var value in Enumerable.Range(0, 2))
            {
                await Task.Delay(200);
                await grain.OnNext(value.ToString());
            }

            await grain.Deactivate();
        });

        var values = new List<string>();
        await Assert.ThrowsAsync<EnumerationAbortedException>(async () =>
        {
            await foreach (var entry in grain.GetValues())
            {
                values.Add(entry);
                Logger.LogInformation("ObservableGrain_AsyncEnumerable: {Entry}", entry);
            }
        });

        Assert.Equal(2, values.Count);
    }

    /// <summary>
    /// Tests basic generator-based async enumerable functionality.
    /// Verifies that GetValuesWithGenerator produces continuous values until cancelled.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_BasicFunctionality()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource();

        await foreach (var entry in grain.GetValuesWithGenerator(cts.Token))
        {
            values.Add(entry);
            Logger.LogInformation("Generator produced: {Entry}", entry);

            // Stop after collecting 10 values
            if (values.Count >= 10)
            {
                break;
            }
        }

        Assert.Equal(10, values.Count);
        Assert.Equal(Enumerable.Range(0, 10), values);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests generator cancellation through CancellationToken.
    /// Verifies that the generator stops producing values when cancelled.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_Cancellation()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource();

        try
        {
            await foreach (var entry in grain.GetValuesWithGenerator(cts.Token))
            {
                values.Add(entry);
                Logger.LogInformation("Generator produced: {Entry}", entry);

                // Cancel after 5 values
                if (values.Count == 5)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.Equal(5, values.Count);
        Assert.Equal(Enumerable.Range(0, 5), values);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests WaitForGeneratorCancellation method functionality.
    /// Verifies that the method completes when the generator is cancelled.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_WaitForCancellation()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource();
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var valuesProduced = new TaskCompletionSource();

        var enumeratorTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in grain.GetValuesWithGenerator(cts.Token))
                {
                    values.Add(entry);
                    Logger.LogInformation("Generator produced: {Entry}", entry);

                    // Signal that we've produced some values
                    if (values.Count == 3)
                    {
                        valuesProduced.TrySetResult();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        // Wait for some values to be produced
        await valuesProduced.Task;
        Assert.True(values.Count > 0);

        // Cancel the generator
        cts.Cancel();

        // Wait for generator cancellation to complete
        await grain.WaitForGeneratorCancellation(waitCts.Token);

        // Ensure the enumerator task completes
        await enumeratorTask;

        Assert.True(values.Count > 0);
        Assert.Equal(Enumerable.Range(0, values.Count), values);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests generator behavior with WithBatchSize.
    /// Verifies that batching works correctly with generator-based async enumerables.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_WithBatchSize()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource();

        try
        {
            await foreach (var entry in grain.GetValuesWithGenerator(cts.Token).WithBatchSize(5))
            {
                values.Add(entry);
                Logger.LogInformation("Generator produced: {Entry}", entry);

                // Cancel after 15 values to test multiple batches
                if (values.Count == 15)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.Equal(15, values.Count);
        Assert.Equal(Enumerable.Range(0, 15), values);

        var grainCalls = await grain.GetIncomingCalls();
        var moveNextCallCount = grainCalls.Count(element =>
            element.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension))
            && (element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.MoveNext)) || element.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration))));

        // With batch size of 5, we should have fewer calls than total values
        Assert.True(moveNextCallCount < values.Count);

        // Check that the enumerator is disposed
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests generator behavior with WithCancellation extension.
    /// Verifies that the WithCancellation extension works correctly with generators.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_WithCancellation()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource();

        try
        {
            await foreach (var entry in grain.GetValuesWithGenerator().WithCancellation(cts.Token))
            {
                values.Add(entry);
                Logger.LogInformation("Generator produced: {Entry}", entry);

                // Cancel after 8 values
                if (values.Count == 8)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.Equal(8, values.Count);
        Assert.Equal(Enumerable.Range(0, 8), values);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests generator behavior with slow consumer.
    /// Verifies that the generator continues producing values even when consumer is slow.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_SlowConsumer()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource();

        try
        {
            await foreach (var entry in grain.GetValuesWithGenerator(cts.Token).WithBatchSize(1))
            {
                values.Add(entry);
                Logger.LogInformation("Generator produced: {Entry}", entry);

                // Stop after 3 values
                if (values.Count == 3)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.Equal(3, values.Count);
        Assert.Equal(Enumerable.Range(0, 3), values);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Tests generator behavior with preemptive cancellation.
    /// Verifies that cancelling before enumeration starts prevents the generator from starting.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_PreemptiveCancellation()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource();

        // Cancel before enumeration starts
        cts.Cancel();

        try
        {
            await foreach (var entry in grain.GetValuesWithGenerator(cts.Token))
            {
                values.Add(entry);
                Logger.LogInformation("Generator produced: {Entry}", entry);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.Empty(values);

        // Check that the enumerator was not started since it was cancelled preemptively
        var grainCalls = await grain.GetIncomingCalls();
        Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.StartEnumeration)));
        Assert.DoesNotContain(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncEnumerableGrainExtension.DisposeAsync)));
    }

    /// <summary>
    /// Tests generator behavior with grain deactivation.
    /// Verifies that deactivating the grain properly terminates the generator.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_Deactivation()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        var values = new List<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var valuesProduced = new TaskCompletionSource();

        var deactivationTask = Task.Run(async () =>
        {
            // Wait for some values to be produced
            await valuesProduced.Task;
            await grain.Deactivate();
        });

        await Assert.ThrowsAsync<EnumerationAbortedException>(async () =>
        {
            await foreach (var entry in grain.GetValuesWithGenerator(cts.Token))
            {
                values.Add(entry);
                Logger.LogInformation("Generator produced: {Entry}", entry);

                // Signal that we've produced some values
                if (values.Count == 3)
                {
                    valuesProduced.TrySetResult();
                }
            }
        });

        await deactivationTask;

        Assert.True(values.Count > 0);
        Assert.Equal(Enumerable.Range(0, values.Count), values);
    }

    /// <summary>
    /// Tests concurrent generator enumeration.
    /// Verifies that multiple concurrent enumerations of the same generator work correctly.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_ConcurrentEnumeration()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        using var cts = new CancellationTokenSource();
        var values1 = new List<int>();
        var values2 = new List<int>();

        var task1 = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in grain.GetValuesWithGenerator(cts.Token))
                {
                    values1.Add(entry);
                    Logger.LogInformation("Generator 1 produced: {Entry}", entry);

                    if (values1.Count == 5)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        var task2 = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in grain.GetValuesWithGenerator(cts.Token))
                {
                    values2.Add(entry);
                    Logger.LogInformation("Generator 2 produced: {Entry}", entry);

                    if (values2.Count == 7)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        await Task.WhenAll(task1, task2);

        Assert.Equal(5, values1.Count);
        Assert.Equal(7, values2.Count);

        // Both should start from 0 since they're separate enumerations
        Assert.Equal(Enumerable.Range(0, 5), values1);
        Assert.Equal(Enumerable.Range(0, 7), values2);

        // Check that enumerators are disposed
        var grainCalls = await grain.GetIncomingCalls();
        var disposeCallCount = grainCalls.Count(c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
        Assert.Equal(2, disposeCallCount); // Two separate enumerations
    }

    /// <summary>
    /// Tests WaitForGeneratorCancellation timeout behavior.
    /// Verifies that WaitForGeneratorCancellation respects the provided CancellationToken.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Observable")]
    public async Task ObservableGrain_AsyncEnumerable_Generator_WaitForCancellation_Timeout()
    {
        var grain = GrainFactory.GetGrain<IObservableGrain>(Guid.NewGuid());

        using var shortTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Start generator without cancelling it
        var values = new List<int>();
        var generatorStarted = new TaskCompletionSource();
        var enumeratorTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in grain.GetValuesWithGenerator())
                {
                    values.Add(entry);
                    Logger.LogInformation("Generator produced: {Entry}", entry);

                    if (values.Count == 1)
                    {
                        generatorStarted.TrySetResult();
                    }

                    if (values.Count >= 20)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        // Wait for the generator to start producing values
        await generatorStarted.Task;

        // WaitForGeneratorCancellation should timeout since generator is not cancelled
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await grain.WaitForGeneratorCancellation(shortTimeout.Token);
        });

        // Clean up
        await enumeratorTask;

        Assert.True(values.Count > 0);

        // Check that the enumerator is disposed
        var grainCalls = await grain.GetIncomingCalls();
        Assert.Contains(grainCalls, c => c.InterfaceName.Contains(nameof(IAsyncEnumerableGrainExtension)) && c.MethodName.Contains(nameof(IAsyncDisposable.DisposeAsync)));
    }

    /// <summary>
    /// Diagnostic listener for monitoring AsyncEnumerableGrainExtension behavior during tests.
    /// This helper class allows tests to observe internal cleanup operations and verify
    /// that enumerators are properly managed according to their lifecycle requirements.
    /// </summary>
    private sealed class AsyncEnumerableGrainExtensionListener : IObserver<KeyValuePair<string, object?>>, IObserver<DiagnosticListener>, IDisposable
    {
        private readonly IDisposable _allListenersSubscription;
        private readonly GrainId _targetGrainId;
        private readonly TimeSpan _enumeratorCleanupInterval;
        private IDisposable? _instanceSubscription;

        public AsyncEnumerableGrainExtensionListener(GrainId targetGrainId, TimeSpan enumeratorCleanupInterval)
        {
            _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
            _targetGrainId = targetGrainId;
            _enumeratorCleanupInterval = enumeratorCleanupInterval;
        }

        public int CleanupCount { get; private set; }

        void IObserver<KeyValuePair<string, object?>>.OnCompleted()
        {
            _instanceSubscription?.Dispose();
        }

        void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
        {
        }

        void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
        {
            var extension = (AsyncEnumerableGrainExtension)value.Value!;
            if (extension.GrainContext.GrainId != _targetGrainId)
            {
                return;
            }

            if (value.Key == "OnAsyncEnumeratorGrainExtensionCreated")
            {
                extension.Timer.Change(_enumeratorCleanupInterval, _enumeratorCleanupInterval);
            }

            if (value.Key == "OnEnumeratorCleanupCompleted")
            {
                ++CleanupCount;
            }
        }

        void IObserver<DiagnosticListener>.OnCompleted() { }
        void IObserver<DiagnosticListener>.OnError(Exception error) { }
        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
        {
            if (value.Name == "Orleans.Runtime.AsyncEnumerableGrainExtension")
            {
                _instanceSubscription = value.Subscribe(this);
            }
        }

        public void Dispose()
        {
            _allListenersSubscription.Dispose();
            _instanceSubscription?.Dispose();
        }
    }
}
