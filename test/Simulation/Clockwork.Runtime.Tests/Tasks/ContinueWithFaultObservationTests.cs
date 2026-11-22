using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Runtime.Tests.Tasks;

/// <summary>
/// A continuation which ignores its antecedent's fault leaves that fault unobserved, as in the BCL: once the faulted
/// antecedent is collected, <see cref="TaskScheduler.UnobservedTaskException"/> reports the fault exactly once. The
/// controlled <c>ContinueWith</c> completes a private gate from the antecedent's status alone, so neither copying the
/// outcome to the gate nor observing the gate's own fault marks the antecedent's exception as observed.
/// </summary>
public sealed class ContinueWithFaultObservationTests
{
    [Fact]
    public void AFaultTheContinuationIgnoresIsReportedOnceAsInTheBcl()
    {
        Assert.Equal(1, CountUnobserved(fault => BclContinuation(fault, observe: false)));
        Assert.Equal(1, CountUnobserved(fault => ControlledContinuation(fault, observe: false)));
    }

    [Fact]
    public void AFaultTheContinuationObservesIsNotReportedAsInTheBcl()
    {
        Assert.Equal(0, CountUnobserved(fault => BclContinuation(fault, observe: true)));
        Assert.Equal(0, CountUnobserved(fault => ControlledContinuation(fault, observe: true)));
    }

    private static int CountUnobserved(Action<Exception> scenario)
    {
        var fault = new InvalidTimeZoneException("unobserved " + Guid.NewGuid().ToString("N"));
        int reported = 0;
        void Count(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.InnerExceptions.Any(inner => ReferenceEquals(inner, fault)))
            {
                Interlocked.Increment(ref reported);
            }
        }

        TaskScheduler.UnobservedTaskException += Count;
        try
        {
            scenario(fault);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Count;
        }

        return Volatile.Read(ref reported);
    }

    // Each scenario runs in its own frame, so its tasks are unreachable once it returns.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BclContinuation(Exception fault, bool observe)
    {
        var source = new TaskCompletionSource();
        Task continuation = source.Task.ContinueWith(
            antecedent => Observe(antecedent, observe),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        source.SetException(fault);

        Assert.True(continuation.IsCompletedSuccessfully);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ControlledContinuation(Exception fault, bool observe)
    {
        using var host = new SimulationSchedulerTestHost();
        TaskTestHarness.RunInSimulation(host, () =>
        {
            var source = new TaskCompletionSource();
            Task continuation = ControlledTask.ContinueWith(source.Task, antecedent => Observe(antecedent, observe));
            host.Scheduler.Schedule(() => ControlledTaskCompletionSource.SetException(source, fault));
            host.Scheduler.DrainUntil(() => continuation.IsCompleted, "test", TestContext.Current.CancellationToken);

            Assert.True(continuation.IsCompletedSuccessfully);
        });
    }

    private static void Observe(Task antecedent, bool observe)
    {
        Assert.True(antecedent.IsFaulted);
        if (observe)
        {
            _ = antecedent.Exception;
        }
    }
}
