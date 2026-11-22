using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Tests.Execution;

/// <summary>
/// Covers <see cref="SimulationExecutionContext"/>: activity checks, nesting, restoration on
/// disposal (including under exceptions), async flow, and isolation across parallel logical
/// call contexts.
/// </summary>
public sealed class SimulationExecutionContextTests
{
    private static readonly SimulationLogicalExecutionIdSource LogicalExecutionIds = new();

    private static SimulationRuntimeIdentity NewRuntime(int seed = 1, string? description = null) =>
        RuntimeTestHarness.NewRuntime(seed, description);

    [Fact]
    public void IsActiveIsFalseOutsideAnySimulation()
    {
        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public void CurrentIsNullOutsideAnySimulation()
    {
        Assert.Null(SimulationExecutionContext.Current);
    }

    [Fact]
    public void TryGetCurrentRuntimeReturnsFalseOutsideAnySimulation()
    {
        Assert.False(SimulationExecutionContext.TryGetCurrentRuntime(out var runtime));
        Assert.Null(runtime);
    }

    [Fact]
    public void EnterRuntimeMakesIsActiveTrueAndCurrentReflectsTheRuntime()
    {
        var runtime = NewRuntime();

        using (SimulationExecutionContext.EnterRuntime(runtime))
        {
            Assert.True(SimulationExecutionContext.IsActive);
            var current = SimulationExecutionContext.Current;
            Assert.NotNull(current);
            Assert.Same(runtime, current!.Runtime);
            Assert.Null(current.Node);

            Assert.True(SimulationExecutionContext.TryGetCurrentRuntime(out var ambientRuntime));
            Assert.Same(runtime, ambientRuntime);
        }

        Assert.False(SimulationExecutionContext.IsActive);
        Assert.Null(SimulationExecutionContext.Current);
    }

    [Fact]
    public void EnterRuntimeThrowsForNullRuntime()
    {
        Assert.Throws<ArgumentNullException>(() => SimulationExecutionContext.EnterRuntime(null!));
    }

    [Fact]
    public void EnterNodeThrowsWithoutAnEnclosingRuntimeScope()
    {
        Assert.Throws<InvalidOperationException>(() => SimulationExecutionContext.EnterNode(new SimulationNodeIdentity("node-1")));
    }

    [Fact]
    public void EnterLogicalExecutionThrowsWithoutAnEnclosingRuntimeScope()
    {
        Assert.Throws<InvalidOperationException>(() => SimulationExecutionContext.EnterLogicalExecution(LogicalExecutionIds.Next()));
    }

    [Fact]
    public void NestedScopesComposeRuntimeNodeAndLogicalExecutionCorrectly()
    {
        var runtime = NewRuntime();
        var node = new SimulationNodeIdentity("node-1");
        var logicalExecutionId = LogicalExecutionIds.Next();

        using (SimulationExecutionContext.EnterRuntime(runtime))
        {
            Assert.Null(SimulationExecutionContext.Current!.Node);

            using (SimulationExecutionContext.EnterNode(node))
            {
                var withNode = SimulationExecutionContext.Current!;
                Assert.Same(runtime, withNode.Runtime);
                Assert.Equal(node, withNode.Node);

                using (SimulationExecutionContext.EnterLogicalExecution(logicalExecutionId))
                {
                    var withLogicalExecution = SimulationExecutionContext.Current!;
                    Assert.Same(runtime, withLogicalExecution.Runtime);
                    Assert.Equal(node, withLogicalExecution.Node);
                    Assert.Equal(logicalExecutionId, withLogicalExecution.LogicalExecutionId);
                }

                // Disposing the logical-execution scope restores exactly the enclosing node scope.
                Assert.Equal(node, SimulationExecutionContext.Current!.Node);
                Assert.Equal(SimulationLogicalExecutionId.None, SimulationExecutionContext.Current!.LogicalExecutionId);
            }

            // Disposing the node scope restores exactly the enclosing runtime-only scope.
            Assert.Null(SimulationExecutionContext.Current!.Node);
        }

        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public void DisposalRestoresThePreviousFrameEvenWhenTheGuardedCodeThrows()
    {
        var outerRuntime = NewRuntime(seed: 1, description: "outer");
        var innerRuntime = NewRuntime(seed: 2, description: "inner");

        using (SimulationExecutionContext.EnterRuntime(outerRuntime))
        {
            var caught = false;
            try
            {
                using (SimulationExecutionContext.EnterRuntime(innerRuntime))
                {
                    Assert.Same(innerRuntime, SimulationExecutionContext.Current!.Runtime);
                    throw new InvalidOperationException("boom");
                }
            }
            catch (InvalidOperationException)
            {
                caught = true;
            }

            Assert.True(caught);

            // The inner scope's `using` unwound via the exception path and still restored the
            // outer frame - exception safety without any explicit try/finally at the call site.
            Assert.Same(outerRuntime, SimulationExecutionContext.Current!.Runtime);
        }

        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public async Task AmbientContextFlowsAcrossAwaitPointsWithoutExplicitPropagation()
    {
        var runtime = NewRuntime();

        using (SimulationExecutionContext.EnterRuntime(runtime))
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
            await Task.Yield();

            // AsyncLocal-backed flow means the ambient runtime survived the awaits above with no
            // explicit propagation at any await point.
            Assert.Same(runtime, SimulationExecutionContext.Current!.Runtime);
        }
    }

    [Fact]
    public async Task ParallelLogicalCallContextsDoNotObserveEachOthersAmbientRuntime()
    {
        var runtimeA = NewRuntime(seed: 1, description: "A");
        var runtimeB = NewRuntime(seed: 2, description: "B");

        async Task<bool> RunUnderRuntime(SimulationRuntimeIdentity runtime)
        {
            using (SimulationExecutionContext.EnterRuntime(runtime))
            {
                // Yield repeatedly so the two tasks interleave on the thread pool; each should
                // still observe only its own ambient runtime the entire time.
                for (var i = 0; i < 25; i++)
                {
                    await Task.Yield();
                    if (!ReferenceEquals(SimulationExecutionContext.Current?.Runtime, runtime))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        var taskA = RunUnderRuntime(runtimeA);
        var taskB = RunUnderRuntime(runtimeB);

        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, Assert.True);
        Assert.False(SimulationExecutionContext.IsActive);
    }

    [Fact]
    public void SuppressFlowPreventsANewUnflowedThreadFromObservingAmbientContext()
    {
        var runtime = NewRuntime();

        using (SimulationExecutionContext.EnterRuntime(runtime))
        {
            bool observedInsideSuppressedWork;

            using (SimulationExecutionContext.SuppressFlow("test: verifying suppression"))
            {
                // A newly started thread captures ExecutionContext at Start; with flow suppressed
                // that capture is empty, so the AsyncLocal-backed ambient runtime must not be visible
                // inside. A fresh dedicated thread (rather than Task.Run) is used deliberately: a
                // thread-pool thread can carry a stale AsyncLocal value from a prior work item, and
                // when flow is suppressed the pool does not restore a clean context, which makes the
                // pool-based form intermittently observe leftover ambient state. A brand-new thread
                // always begins with empty AsyncLocals, isolating exactly the suppression semantic.
                observedInsideSuppressedWork = RunOnNewThread(static () => SimulationExecutionContext.IsActive);
            }

            Assert.False(observedInsideSuppressedWork);

            // Flow is restored once the suppression scope is disposed, so a thread started now
            // captures the ambient context and observes it.
            var observedAfterRestoration = RunOnNewThread(static () => SimulationExecutionContext.IsActive);
            Assert.True(observedAfterRestoration);
        }
    }

    private static bool RunOnNewThread(Func<bool> work)
    {
        var result = false;
        var thread = new Thread(() => result = work())
        {
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void SuppressFlowRecordsADiagnosticEntryDescribingWhy()
    {
        var runtime = NewRuntime();
        var reason = $"unit test suppression reason {Guid.NewGuid()}";

        using (SimulationExecutionContext.EnterRuntime(runtime))
        using (SimulationExecutionContext.SuppressFlow(reason))
        {
        }

        var events = SimulationFlowSuppressionDiagnostics.GetRecentEvents();
        Assert.Contains(events, e => e.Reason == reason && e.CapturedContext?.Runtime == runtime);
    }

    [Fact]
    public void SuppressFlowThrowsForNullOrEmptyReason()
    {
        Assert.Throws<ArgumentException>(() => SimulationExecutionContext.SuppressFlow(string.Empty));
    }
}
