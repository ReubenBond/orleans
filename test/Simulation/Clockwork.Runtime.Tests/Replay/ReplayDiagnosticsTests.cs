using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Tests.Replay;

public sealed class ReplayDiagnosticsTests
{
    [Fact]
    public void DeadlockTraceRendersStableWaitGraphAndCanonicalJson()
    {
        ReplayExecutionResult execution = ReplayRunner.Record(
            new ReplayRecordingOptions
            {
                SimulationSeed = 88,
                SchedulingPolicy = ReplaySchedulingPolicy.RoundRobin,
                IncludeDiagnosticMessages = true,
            },
            DeadlockScenario,
            TestContext.Current.CancellationToken);

        string first = ReplayTraceRenderer.RenderText(execution.Artifact);
        ReplayArtifact roundTripped = ReplayArtifactSerializer.Deserialize(
            ReplayArtifactSerializer.Serialize(execution.Artifact));
        string second = ReplayTraceRenderer.RenderText(roundTripped);

        Assert.Equal(first, second);
        Assert.Contains("Deadlock cycle 1:", first, StringComparison.Ordinal);
        Assert.Contains("op1 -> res2 -> op2", first, StringComparison.Ordinal);
        Assert.Contains("Resources:", first, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', first);
    }

    [Fact]
    public void DefaultDiagnosticsExcludeCallerDescriptionsAndSourcePaths()
    {
        ReplayExecutionResult execution = ReplayRunner.Record(
            new ReplayRecordingOptions
            {
                SimulationSeed = 89,
                SchedulingPolicy = ReplaySchedulingPolicy.RoundRobin,
            },
            static scheduler => scheduler.Schedule("secret-work-description", static () => { }),
            TestContext.Current.CancellationToken);

        string json = ReplayArtifactSerializer.ToJson(execution.Artifact);

        Assert.DoesNotContain("secret-work-description", json, StringComparison.Ordinal);
        Assert.All(execution.Artifact.Diagnostics.Operations, static operation => Assert.Null(operation.Description));
    }

    private static void DeadlockScenario(SimulationScheduler scheduler)
    {
        SimulationResource first = scheduler.CreateResource(SimulationResourceKind.Monitor, "first");
        SimulationResource second = scheduler.CreateResource(SimulationResourceKind.Monitor, "second");
        scheduler.Schedule(
            "one",
            () =>
            {
                scheduler.MarkResourceOwner(first, scheduler.CurrentOperation);
                scheduler.Yield();
                scheduler.WaitOnResource(second, SimulationPauseReason.ResourceWait("second"));
            });
        scheduler.Schedule(
            "two",
            () =>
            {
                scheduler.MarkResourceOwner(second, scheduler.CurrentOperation);
                scheduler.Yield();
                scheduler.WaitOnResource(first, SimulationPauseReason.ResourceWait("first"));
            });
    }
}
