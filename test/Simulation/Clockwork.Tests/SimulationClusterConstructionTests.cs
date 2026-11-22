using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Random;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Tests;

public sealed class SimulationClusterConstructionTests
{
    [Fact]
    public async Task ClusterIsUsableImmediatelyWithNoNodes()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);

        Assert.Empty(cluster.Nodes);
        Assert.NotNull(cluster.Network);
        Assert.Same(cluster.Scheduler, cluster.RuntimeIdentity.Scheduler);
        Assert.Same(cluster.Scheduler, cluster.SchedulerLane.Scheduler);
        Assert.Equal(SimulationExecutionReason.Idle, cluster.RunUntilIdle(TestContext.Current.CancellationToken).Reason);
    }

    [Fact]
    public async Task PlainNodesAreFullyAttachedWhenReturned()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);

        SimulationNode first = cluster.AddNode("first");
        SimulationNode second = cluster.AddNode("second");

        Assert.True(first.IsInitialized);
        Assert.True(second.IsInitialized);
        Assert.Same(first, cluster.FindNode("first"));
        Assert.Same(second, cluster.FindNode("second"));
        Assert.Same(cluster.Scheduler, first.Context.Scheduler);
        Assert.Same(cluster.Scheduler, second.Context.Scheduler);
    }

    [Fact]
    public async Task DuplicateAddressAcrossNodeKindsThrowsBeforeInvokingTheFactory()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        _ = cluster.AddNode("dup");
        var invoked = false;

        Assert.Throws<ArgumentException>(() => cluster.AddCustomNode("dup", context =>
        {
            invoked = true;
            return new CustomNode("dup", context);
        }));
        Assert.False(invoked);
    }

    [Fact]
    public async Task FailedCustomAttachmentLeavesPreviouslyAttachedNodesUsable()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode first = cluster.AddNode("first");
        var failure = new InvalidOperationException("factory failed");
        var orphanedWorkRan = false;

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => cluster.AddCustomNode<CustomNode>("second", context =>
            {
                context.SchedulerLane.Enqueue(
                    () => orphanedWorkRan = true);
                throw failure;
            }));

        Assert.Same(failure, actual);
        Assert.Same(first, cluster.FindNode("first"));
        Assert.Null(cluster.FindNode("second"));
        CustomNode second = cluster.AddCustomNode(
            "second",
            context => new CustomNode("second", context));
        Assert.Same(second, cluster.FindNode("second"));
        _ = cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        Assert.True(orphanedWorkRan);
        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public async Task FailedAttachmentContextAndLaneRemainInvalidAfterAddressReuse()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNodeContext? staleContext = null;
        SimulationSchedulerLane? staleLane = null;

        _ = Assert.Throws<InvalidOperationException>(
            () => cluster.AddCustomNode<CustomNode>("reused", context =>
            {
                staleContext = context;
                staleLane = context.SchedulerLane;
                throw new InvalidOperationException("factory failed");
            }));

        Assert.NotNull(staleContext);
        Assert.NotNull(staleLane);
        SimulationNode reused = cluster.AddNode("reused");
        var reusedWorkRan = false;

        Assert.Throws<ObjectDisposedException>(
            () => staleLane.Enqueue(() => reusedWorkRan = true));
        Assert.Throws<ObjectDisposedException>(
            () => staleLane.EnqueueAfter(() => reusedWorkRan = true, TimeSpan.Zero));
        Assert.Throws<ObjectDisposedException>(() => staleLane.RunOnce(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => staleLane.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => staleLane.CaptureScheduledItems());
        Assert.Throws<ObjectDisposedException>(() => staleLane.HasItems);
        Assert.Throws<ObjectDisposedException>(() => staleLane.NextWaitingDueTime);
        Assert.Throws<ObjectDisposedException>(() => staleLane.UtcNow);
        Assert.Throws<ObjectDisposedException>(() => staleLane.SynchronizationContext);
        Assert.Throws<ObjectDisposedException>(() => staleContext.Suspend());
        Assert.Throws<ObjectDisposedException>(
            () => staleContext.SuspendFor(TimeSpan.FromSeconds(1)));
        Assert.Throws<ObjectDisposedException>(() => staleContext.Resume());
        Assert.Throws<ObjectDisposedException>(() => staleContext.Step(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => staleContext.RunUntilIdle(TestContext.Current.CancellationToken));

        reused.Context.SchedulerLane.Enqueue(
            () => reusedWorkRan = true);
        Assert.Equal(SimulationExecutionReason.Idle, cluster.RunUntilIdle(TestContext.Current.CancellationToken).Reason);
        Assert.True(reusedWorkRan);
    }

    [Fact]
    public async Task FailedCustomAttachmentCleanupDrainsSuspendedLaneWorkBeforeDisposingTheNode()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var preexistingWorkRan = false;
        var laterWorkRan = false;
        WorkObservingDisposalNode? failedNode = null;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => cluster.AddCustomNode(
                "requested",
                context =>
                {
                    failedNode = new WorkObservingDisposalNode(
                        "actual",
                        context,
                        () => preexistingWorkRan);
                    context.SuspendFor(TimeSpan.FromMinutes(1));
                    context.SchedulerLane.Enqueue(
                        () => preexistingWorkRan = true);
                    Assert.True(context.IsSuspended);
                    Assert.False(preexistingWorkRan);

                    // Select a later operation before attachment cleanup starts. The cleanup
                    // operation must not jump ahead of the re-enabled node-lane callback.
                    cluster.SchedulerLane.Enqueue(
                        () => laterWorkRan = true);
                    Assert.Equal(
                        SimulationExecutionReason.ConditionMet,
                        cluster.RunUntil(() => laterWorkRan, TestContext.Current.CancellationToken).Reason);
                    return failedNode;
                }));

        Assert.Equal(
            "The factory for node 'requested' returned a node with address 'actual'. Custom node addresses must exactly match the requested address.",
            failure.Message);
        Assert.NotNull(failedNode);
        Assert.True(preexistingWorkRan);
        Assert.True(failedNode.Disposed);
        Assert.True(failedNode.WorkRanBeforeDisposal);
        Assert.Null(cluster.FindNode("requested"));
        Assert.Throws<ObjectDisposedException>(
            () => failedNode.Context.SchedulerLane.CaptureScheduledItems());
    }

    [Fact]
    public async Task DisposeAsyncDuringCustomNodeFactoryIsRejectedWithoutDisposingTheCluster()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SimulationNode plainNode = cluster.AddNode("plain");

        InvalidOperationException failure = null!;
        CustomNode customNode = cluster.AddCustomNode("custom", context =>
        {
            failure = Assert.Throws<InvalidOperationException>(
                () => cluster.DisposeAsync().AsTask().GetAwaiter().GetResult());
            return new CustomNode("custom", context);
        });

        Assert.Contains(
            "attachment factory",
            failure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(cluster.TeardownCancellationToken.IsCancellationRequested);
        Assert.Same(plainNode, cluster.FindNode("plain"));
        Assert.Same(customNode, cluster.FindNode("custom"));
        Assert.NotNull(cluster.AddNode("later"));
    }

    [Fact]
    public async Task NullCustomFactoryResultIsRejected()
    {
        await using var cluster = new SimulationCluster(seed: 1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => cluster.AddCustomNode<CustomNode>("node", _ => null!));

        Assert.Contains("node", exception.Message, StringComparison.Ordinal);
        Assert.Contains("null", exception.Message, StringComparison.Ordinal);
        Assert.Empty(cluster.Nodes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("different")]
    public async Task InvalidCustomNodeAddressIsRejectedAndTheNodeIsDisposed(string actualAddress)
    {
        await using var cluster = new SimulationCluster(seed: 1);
        var events = new List<string>();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => cluster.AddCustomNode(
                "requested",
                context => new TrackingNode(actualAddress, context, events)));

        Assert.Contains("address", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([actualAddress], events);
        Assert.Empty(cluster.Nodes);
    }

    [Theory]
    [InlineData("unattached")]
    [InlineData("other-node")]
    [InlineData("other-cluster")]
    public async Task CustomNodeWithForeignContextIsRejectedAndDisposed(
        string source)
    {
        await using var cluster = new SimulationCluster(seed: 1);
        await using var otherCluster = new SimulationCluster(seed: 2);
        SimulationNode localNode = cluster.AddNode("local");
        SimulationNode otherNode = otherCluster.AddNode("other");
        SimulationNodeContext invalidContext = source switch
        {
            "unattached" =>
                SimulationTestHarness.NewNodeComponents("unattached").Context,
            "other-node" => localNode.Context,
            "other-cluster" => otherNode.Context,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        var events = new List<string>();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => cluster.AddCustomNode(
                "requested",
                _ => new TrackingNode("requested", invalidContext, events)));

        Assert.Contains("context", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["requested"], events);
        Assert.Same(localNode, cluster.FindNode("local"));
        Assert.Same(otherNode, otherCluster.FindNode("other"));
        Assert.Null(cluster.FindNode("requested"));
        Assert.NotNull(cluster.AddCustomNode(
            "requested",
            context => new CustomNode("requested", context)));
    }

    [Fact]
    public async Task InvalidCustomNodeCleanupRunsAsyncDisposalWithinSimulation()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        AwaitingDisposeNode? node = null;

        Task<InvalidOperationException> attachment = Task.Run(
            () => Assert.Throws<InvalidOperationException>(
                () => cluster.AddCustomNode(
                    "expected",
                    context =>
                    {
                        node = new AwaitingDisposeNode("different", context);
                        return node;
                    })),
            TestContext.Current.CancellationToken);
        InvalidOperationException exception = await attachment.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Contains("address", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(node);
        Assert.True(node.DisposeStarted);
        Assert.True(node.DisposeSawActiveSimulation);
        Assert.True(node.DisposeContinuationRan);
        Assert.True(node.DisposeCompleted);
        Assert.Empty(cluster.Nodes);
    }

    [Fact]
    public async Task FailedCustomNodeCleanupDrainsCallbackFailuresBeforeDetaching()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        AwaitingDisposeNode? node = null;

        AggregateException exception = Assert.Throws<AggregateException>(
            () => cluster.AddCustomNode(
                "expected",
                context =>
                {
                    context.SchedulerLane.Enqueue(
                        () => throw new InvalidOperationException("queued callback failed"));
                    node = new AwaitingDisposeNode("different", context);
                    return node;
                }));

        Assert.NotNull(node);
        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.Contains(
                "address",
                failure.Message,
                StringComparison.OrdinalIgnoreCase),
            failure => Assert.Equal("queued callback failed", failure.Message));
        Assert.True(node.DisposeStarted);
        Assert.True(node.DisposeContinuationRan);
        Assert.True(node.DisposeCompleted);
        Assert.Throws<ObjectDisposedException>(
            () => node.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.Nodes);
        Assert.Equal(SimulationExecutionReason.Idle, cluster.RunUntilIdle(TestContext.Current.CancellationToken).Reason);
    }

    [Fact]
    public async Task FailedCustomNodeCleanupRunsLifecycleDependenciesAndRemovesResidualWork()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        LifecycleDependencyAwaitingDisposeNode? node = null;
        LifecycleDependencies? dependencies = null;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => cluster.AddCustomNode(
                "expected",
                context =>
                {
                    dependencies = new LifecycleDependencies(context);
                    context.SuspendFor(TimeSpan.FromMinutes(1));
                    node = new LifecycleDependencyAwaitingDisposeNode(
                        "different",
                        context,
                        dependencies);
                    return node;
                }));

        Assert.Contains("address", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(node);
        Assert.NotNull(dependencies);
        Assert.True(dependencies.TaskSchedulerTaskRan);
        Assert.True(dependencies.TimeProviderTimerRan);
        Assert.True(dependencies.SynchronizationContextCallbackRan);
        Assert.True(node.DisposeStarted);
        Assert.True(node.DisposeCompleted);
        Assert.False(dependencies.ResidualWorkRan);
        Assert.Throws<ObjectDisposedException>(
            () => node.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.Nodes);
        Assert.Equal(SimulationExecutionReason.Idle, cluster.RunUntilIdle(TestContext.Current.CancellationToken).Reason);
        Assert.False(dependencies.ResidualWorkRan);
        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public async Task FailedCustomNodeCleanupAggregatesFailuresAfterDisposalAndDetachment()
    {
        await using var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var events = new List<string>();
        TrackingNode? node = null;

        AggregateException exception = Assert.Throws<AggregateException>(
            () => cluster.AddCustomNode(
                "expected",
                context =>
                {
                    context.SchedulerLane.Enqueue(
                        () => throw new InvalidOperationException("pre-drain failed"));
                    node = new TrackingNode(
                        "different",
                        context,
                        events,
                        throwOnDispose: true);
                    return node;
                }));

        Assert.NotNull(node);
        Assert.True(node.Disposed);
        Assert.Equal(["different"], events);
        Assert.Collection(
            exception.InnerExceptions,
            failure => Assert.Contains(
                "address",
                failure.Message,
                StringComparison.OrdinalIgnoreCase),
            failure => Assert.Equal("pre-drain failed", failure.Message),
            failure => Assert.Equal("different disposal failed", failure.Message));
        Assert.Throws<ObjectDisposedException>(
            () => node.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.Nodes);
        Assert.Equal(SimulationExecutionReason.Idle, cluster.RunUntilIdle(TestContext.Current.CancellationToken).Reason);
    }

    [Fact]
    public async Task CustomNodeIsReturnedImmediatelyAlongsidePlainNodes()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        SimulationNode plainNode = cluster.AddNode("plain");

        CustomNode custom = cluster.AddCustomNode(
            "custom",
            context => new CustomNode("custom", context));

        Assert.Equal(2, cluster.Nodes.Count);
        Assert.Same(custom, cluster.FindNode<CustomNode>("custom"));
        Assert.Same(plainNode, cluster.FindNode("plain"));
        custom.RecordGreeting("hello");
        Assert.Equal("hello", custom.LastGreeting);
    }

    [Fact]
    public async Task NetworkRoutesBetweenImmediatelyAttachedNodes()
    {
        await using var cluster = new SimulationCluster(seed: 1);
        _ = cluster.AddNode("node-1");
        _ = cluster.AddNode("node-2");

        cluster.Network.CreatePartition("node-1", "node-2");

        Assert.Equal(
            DeliveryStatus.Partitioned,
            cluster.Network.CheckDelivery("node-1", "node-2"));
        Assert.Equal(
            DeliveryStatus.Success,
            cluster.Network.CheckDelivery("node-2", "node-1"));
    }

    [Fact]
    public async Task ConstructorSettingsFlowToTheRuntime()
    {
        using var cts = new CancellationTokenSource();
        DateTimeOffset start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TimeZoneInfo timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "Clockwork/Test",
            TimeSpan.FromHours(3),
            "Clockwork Test",
            "Clockwork Test");
        await using var cluster = new SimulationCluster(
            seed: 7,
            startDateTime: start,
            simulationTimeZone: timeZone,
            cancellationToken: cts.Token);

        Assert.Equal(7, cluster.Seed);
        Assert.Equal(start, cluster.StartDateTime);
        Assert.Same(timeZone, cluster.SimulationTimeZone);

        // The next drive step cancels the teardown token, on the simulation's logical thread.
        await cts.CancelAsync();
        Assert.Equal(
            SimulationExecutionReason.TeardownCancellationRequested,
            cluster.RunUntilIdle(TestContext.Current.CancellationToken).Reason);
        Assert.True(cluster.TeardownCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposeAsyncDisposesCustomNodesInAttachmentOrder()
    {
        var events = new List<string>();
        var cluster = new SimulationCluster(seed: 1);
        TrackingNode first = cluster.AddCustomNode(
            "first",
            context => new TrackingNode("first", context, events));
        TrackingNode second = cluster.AddCustomNode(
            "second",
            context => new TrackingNode("second", context, events));

        await cluster.DisposeAsync();

        Assert.Equal(["first", "second"], events);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Empty(cluster.Nodes);
    }

    [Fact]
    public async Task DisposeAsyncCompletesCleanupWhenPreDrainReachesAnExecutionBound()
    {
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch)
        {
            MaxSimulatedTimeAdvance = TimeSpan.FromSeconds(1),
        };
        SimulationNode node = cluster.AddNode("node");
        var workRan = false;
        node.Context.SchedulerLane.EnqueueAfter(
            () => workRan = true,
            TimeSpan.FromSeconds(10));

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            () => cluster.DisposeAsync().AsTask());

        Assert.Contains(exception.InnerExceptions, static failure => failure is TimeoutException);
        Assert.False(workRan);
        Assert.Throws<ObjectDisposedException>(
            () => node.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.Nodes);
    }

    [Fact]
    public async Task DisposeAsyncContinuesCleanupAggregatesFailuresAndIsIdempotent()
    {
        var events = new List<string>();
        var cluster = new SimulationCluster(seed: 1);
        _ = cluster.AddCustomNode(
            "first-failure",
            context => new TrackingNode("first-failure", context, events, throwOnDispose: true));
        TrackingNode failingNode = cluster.AddCustomNode(
            "second-failure",
            context => new TrackingNode("second-failure", context, events, throwOnDispose: true));
        _ = cluster.AddCustomNode(
            "first-success",
            context => new TrackingNode("first-success", context, events));
        _ = cluster.AddCustomNode(
            "second-success",
            context => new TrackingNode("second-success", context, events));
        var residualWorkRan = false;
        failingNode.Context.SchedulerLane.EnqueueAfter(
            () => residualWorkRan = true,
            TimeSpan.FromMinutes(1));
        failingNode.SuspendFor(TimeSpan.FromMinutes(1));
        CancellationToken teardown = cluster.TeardownCancellationToken;

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            () => cluster.DisposeAsync().AsTask());

        Assert.Equal(
            ["first-failure", "second-failure", "first-success", "second-success"],
            events);
        Assert.Equal(
            ["first-failure disposal failed", "second-failure disposal failed"],
            exception.InnerExceptions.Select(static error => error.Message));
        Assert.Empty(cluster.Nodes);
        Assert.Throws<ObjectDisposedException>(
            () => failingNode.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.SchedulerLane.CaptureScheduledItems());
        Assert.True(residualWorkRan);
        Assert.True(teardown.IsCancellationRequested);
        Assert.False(SimulationExecutionContext.IsActive);

        await cluster.DisposeAsync();
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public async Task DisposeAsyncRunsSuspendedCustomNodeLifecycleWorkAndPendingWork()
    {
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        var ordinaryWorkRan = false;
        AsynchronousDisposalProbe? probe = null;
        AsynchronouslyDisposingNode node = cluster.AddCustomNode(
            "custom",
            context => new AsynchronouslyDisposingNode(
                "custom",
                context,
                probe = new AsynchronousDisposalProbe(
                    "custom",
                    context,
                    cluster.SynchronizationContext)));
        node.Context.SchedulerLane.Enqueue(
            () => ordinaryWorkRan = true);
        node.SuspendFor(TimeSpan.FromMinutes(1));

        await cluster.DisposeAsync();

        Assert.NotNull(probe);
        Assert.True(probe.CallbackRan);
        Assert.True(probe.ContinuationRan);
        Assert.True(probe.ContinuationSawActiveSimulation);
        Assert.True(probe.ContinuationRanOnSimulationContext);
        Assert.True(ordinaryWorkRan);
        Assert.Throws<ObjectDisposedException>(
            () => node.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.Nodes);
    }

    [Fact]
    public async Task DisposeAsyncRunsLifecycleDependenciesAndRemovesResidualWork()
    {
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        LifecycleDependencies? dependencies = null;
        LifecycleDependencyAwaitingDisposeNode node = cluster.AddCustomNode(
            "custom",
            context =>
            {
                dependencies = new LifecycleDependencies(context);
                return new LifecycleDependencyAwaitingDisposeNode(
                    "custom",
                    context,
                    dependencies);
            });
        node.SuspendFor(TimeSpan.FromMinutes(1));

        Assert.NotNull(dependencies);

        await cluster.DisposeAsync();

        Assert.True(dependencies.TaskSchedulerTaskRan);
        Assert.True(dependencies.TimeProviderTimerRan);
        Assert.True(dependencies.SynchronizationContextCallbackRan);
        Assert.True(node.DisposeStarted);
        Assert.True(node.DisposeCompleted);
        Assert.False(dependencies.ResidualWorkRan);
        Assert.Throws<ObjectDisposedException>(
            () => node.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.SchedulerLane.CaptureScheduledItems());
        Assert.Empty(cluster.Nodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsyncKeepsNodeLaneRunnableWhenLifecycleCallbackSuspends(
        bool timedSuspension)
    {
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        SuspendingDisposalNode node = cluster.AddCustomNode(
            "custom",
            context => new SuspendingDisposalNode(
                "custom",
                context,
                timedSuspension));

        await cluster.DisposeAsync();

        Assert.True(node.SuspensionCallbackRan);
        Assert.True(node.FollowupCallbackRan);
        Assert.True(node.DisposeCompleted);
        Assert.Empty(cluster.Nodes);
    }

    [Fact]
    public async Task DisposeAsyncDrainsCallbackFailuresBeforeAggregatingDisposalFailures()
    {
        var events = new List<string>();
        var cluster = new SimulationCluster(seed: 1, DateTimeOffset.UnixEpoch);
        TrackingNode firstFailingNode = cluster.AddCustomNode(
            "first-failure",
            context => new TrackingNode(
                "first-failure",
                context,
                events,
                throwOnDispose: true));
        TrackingNode failingNode = cluster.AddCustomNode(
            "second-failure",
            context => new TrackingNode(
                "second-failure",
                context,
                events,
                throwOnDispose: true));
        TrackingNode succeedingNode = cluster.AddCustomNode(
            "node-success",
            context => new TrackingNode("node-success", context, events));
        firstFailingNode.Context.SchedulerLane.Enqueue(
            () => throw new InvalidOperationException("first callback failed"));
        cluster.SchedulerLane.Enqueue(
            () => throw new InvalidOperationException("second callback failed"));

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            () => cluster.DisposeAsync().AsTask());

        Assert.Equal(
            [
                "first callback failed",
                "second callback failed",
                "first-failure disposal failed",
                "second-failure disposal failed",
            ],
            exception.InnerExceptions.Select(static error => error.Message));
        Assert.Equal(["first-failure", "second-failure", "node-success"], events);
        Assert.True(firstFailingNode.Disposed);
        Assert.True(failingNode.Disposed);
        Assert.True(succeedingNode.Disposed);
        Assert.Empty(cluster.Nodes);
        Assert.Throws<ObjectDisposedException>(
            () => firstFailingNode.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Throws<ObjectDisposedException>(
            () => failingNode.Context.SchedulerLane.CaptureScheduledItems());
        Assert.Throws<ObjectDisposedException>(
            () => succeedingNode.Context.SchedulerLane.CaptureScheduledItems());
    }

    [Fact]
    public async Task AddNodeAfterDisposalThrows()
    {
        var cluster = new SimulationCluster(seed: 1);
        await cluster.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => cluster.AddNode("late"));
    }

    [Fact]
    public async Task SameSeedProducesReproduciblePerNodeRandomValues()
    {
        await using var first = new SimulationCluster(seed: 42);
        SimulationNode firstNode = first.AddNode("node");
        await using var second = new SimulationCluster(seed: 42);
        SimulationNode secondNode = second.AddNode("node");

        Assert.Equal(firstNode.Context.Random.Next(), secondNode.Context.Random.Next());
    }

    [Fact]
    public async Task NodeRandomUsesStableApplicationDomainSeedAcrossTopologyEdits()
    {
        const int seed = 42;
        await using var first = new SimulationCluster(seed);
        SimulationNode firstStable = first.AddNode("stable");
        _ = first.AddNode("peer");

        await using var edited = new SimulationCluster(seed);
        _ = edited.AddNode("added");
        _ = edited.AddNode("peer");
        SimulationNode editedStable = edited.AddNode("stable");

        int expectedSeed = new SimulationSeedAuthority(seed)
            .GetSiteSeed(SimulationSeedDomain.Application, "stable");
        Assert.Equal(expectedSeed, firstStable.Context.Random.Seed);
        Assert.Equal(expectedSeed, editedStable.Context.Random.Seed);
        Assert.Equal(firstStable.Context.Random.Next(), editedStable.Context.Random.Next());
    }

    [Fact]
    public async Task NetworkRandomUsesFixedDomainAcrossTopologyEdits()
    {
        const int seed = 42;
        const double dropRate = 0.5;
        await using var first = new SimulationCluster(seed);
        _ = first.AddNode("stable");
        _ = first.AddNode("peer");

        await using var edited = new SimulationCluster(seed);
        _ = edited.AddNode("added");
        _ = edited.AddNode("peer");
        _ = edited.AddNode("stable");

        var expectedRandom = new SimulationRandom(
            new SimulationSeedAuthority(seed).GetDomainSeed(SimulationSeedDomain.Network));
        DeliveryStatus[] expected = Enumerable.Range(0, 32)
            .Select(_ => expectedRandom.Chance(dropRate)
                ? DeliveryStatus.Dropped
                : DeliveryStatus.Success)
            .ToArray();

        Assert.Equal(expected, GetDeliveryOutcomes(first.Network, dropRate));
        Assert.Equal(expected, GetDeliveryOutcomes(edited.Network, dropRate));
    }

    private static DeliveryStatus[] GetDeliveryOutcomes(
        SimulationNetwork network,
        double dropRate)
    {
        network.MessageDropRate = dropRate;
        return Enumerable.Range(0, 32)
            .Select(_ => network.CheckDelivery("stable", "peer"))
            .ToArray();
    }

    private sealed class CustomNode(
        string address,
        SimulationNodeContext context) : SimulationNode
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public string? LastGreeting { get; private set; }

        public void RecordGreeting(string greeting) => LastGreeting = greeting;
    }

    private sealed class TrackingNode(
        string address,
        SimulationNodeContext context,
        List<string> events,
        bool throwOnDispose = false) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            events.Add(NetworkAddress);
            Disposed = true;
            if (throwOnDispose)
            {
                throw new InvalidOperationException(
                    $"{NetworkAddress} disposal failed");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class WorkObservingDisposalNode(
        string address,
        SimulationNodeContext context,
        Func<bool> workRan) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public bool Disposed { get; private set; }

        public bool WorkRanBeforeDisposal { get; private set; }

        public ValueTask DisposeAsync()
        {
            WorkRanBeforeDisposal = workRan();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AwaitingDisposeNode(
        string address,
        SimulationNodeContext context) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public bool DisposeStarted { get; private set; }

        public bool DisposeSawActiveSimulation { get; private set; }

        public bool DisposeContinuationRan { get; private set; }

        public bool DisposeCompleted { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeStarted = true;
            DisposeSawActiveSimulation = SimulationExecutionContext.IsActive;
            if (!DisposeSawActiveSimulation)
            {
                throw new InvalidOperationException("Attachment cleanup must dispose within an active simulation.");
            }

            var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Context.SchedulerLane.Enqueue(
                () =>
                {
                    DisposeContinuationRan = true;
                    continuation.SetResult();
                });

            await continuation.Task;
            DisposeCompleted = true;
        }
    }

    private sealed class LifecycleDependencyAwaitingDisposeNode(
        string address,
        SimulationNodeContext context,
        LifecycleDependencies dependencies) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public bool DisposeStarted { get; private set; }

        public bool DisposeCompleted { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeStarted = true;
            await dependencies.WaitAsync();
            dependencies.ScheduleResidualWork();
            DisposeCompleted = true;
        }
    }

    private sealed class SuspendingDisposalNode(
        string address,
        SimulationNodeContext context,
        bool timedSuspension) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public bool SuspensionCallbackRan { get; private set; }

        public bool FollowupCallbackRan { get; private set; }

        public bool DisposeCompleted { get; private set; }

        public async ValueTask DisposeAsync()
        {
            var suspension = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var followup = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Context.SchedulerLane.Enqueue(
                () =>
                {
                    if (timedSuspension)
                    {
                        Context.SuspendFor(TimeSpan.FromDays(1));
                    }
                    else
                    {
                        Context.Suspend();
                    }

                    SuspensionCallbackRan = true;
                    suspension.SetResult();
                });
            Context.SchedulerLane.Enqueue(
                () =>
                {
                    FollowupCallbackRan = true;
                    followup.SetResult();
                });

            await suspension.Task;
            await followup.Task;
            DisposeCompleted = true;
        }
    }

    private sealed class LifecycleDependencies
    {
        private readonly SimulationNodeContext _context;
        private readonly ITimer _dependencyTimer;
        private readonly Task _schedulerTask;
        private readonly Task _timerTask;
        private readonly Task _synchronizationContextTask;

        public LifecycleDependencies(SimulationNodeContext context)
        {
            _context = context;
            _schedulerTask = Task.Factory.StartNew(
                () => TaskSchedulerTaskRan = true,
                CancellationToken.None,
                TaskCreationOptions.None,
                context.TaskScheduler);

            var timerCompletion = new TaskCompletionSource();
            _dependencyTimer = context.TimeProvider.CreateTimer(
                _ =>
                {
                    TimeProviderTimerRan = true;
                    timerCompletion.SetResult();
                },
                null,
                TimeSpan.FromSeconds(2),
                Timeout.InfiniteTimeSpan);
            _timerTask = timerCompletion.Task;

            var callbackCompletion = new TaskCompletionSource();
            context.SynchronizationContext.Post(
                _ =>
                {
                    SynchronizationContextCallbackRan = true;
                    callbackCompletion.SetResult();
                },
                null);
            _synchronizationContextTask = callbackCompletion.Task;
        }

        public bool TaskSchedulerTaskRan { get; private set; }

        public bool TimeProviderTimerRan { get; private set; }

        public bool SynchronizationContextCallbackRan { get; private set; }

        public bool ResidualWorkRan { get; private set; }

        public async Task WaitAsync()
        {
            await _schedulerTask;
            await _timerTask;
            await _synchronizationContextTask;
        }

        public void ScheduleResidualWork()
        {
            _dependencyTimer.Dispose();
            _context.SchedulerLane.EnqueueAfter(
                () => ResidualWorkRan = true,
                TimeSpan.FromDays(1));
            _context.SynchronizationContext.Post(_ => ResidualWorkRan = true, null);
            _ = Task.Factory.StartNew(
                () => ResidualWorkRan = true,
                CancellationToken.None,
                TaskCreationOptions.None,
                _context.TaskScheduler);
            _ = _context.TimeProvider.CreateTimer(
                _ => ResidualWorkRan = true,
                null,
                TimeSpan.FromDays(1),
                Timeout.InfiniteTimeSpan);
            _context.SuspendFor(TimeSpan.FromDays(1));
        }
    }

    private sealed class AsynchronouslyDisposingNode(
        string address,
        SimulationNodeContext context,
        AsynchronousDisposalProbe probe) : SimulationNode, IAsyncDisposable
    {
        public override SimulationNodeContext Context { get; } = context;

        public override string NetworkAddress { get; } = address;

        public override bool IsInitialized => true;

        public ValueTask DisposeAsync() => probe.DisposeAsync();
    }

    private sealed class AsynchronousDisposalProbe(
        string name,
        SimulationNodeContext callbackContext,
        SimulationSynchronizationContext expectedSynchronizationContext)
    {
        public bool CallbackRan { get; private set; }

        public bool ContinuationRan { get; private set; }

        public bool ContinuationSawActiveSimulation { get; private set; }

        public bool ContinuationRanOnSimulationContext { get; private set; }

        public async ValueTask DisposeAsync()
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            callbackContext.SchedulerLane.Enqueue(
                () =>
                {
                    CallbackRan = true;
                    completion.SetResult();
                });

            await completion.Task;
            ContinuationRan = true;
            ContinuationSawActiveSimulation = SimulationExecutionContext.IsActive;
            ContinuationRanOnSimulationContext =
                SynchronizationContext.Current is SimulationSynchronizationContext current
                && expectedSynchronizationContext.IsSameScheduler(current);
            Assert.True(ContinuationSawActiveSimulation, $"{name} disposal escaped the simulation.");
        }
    }

}
