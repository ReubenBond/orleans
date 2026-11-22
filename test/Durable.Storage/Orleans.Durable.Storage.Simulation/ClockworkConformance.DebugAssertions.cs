#define DEBUG

// Debug's calls compile only where DEBUG is defined, so this file defines it: the probe fails its assertion in every
// configuration, the Release tests' among them.
using System.Diagnostics;

namespace Orleans.Durable.Storage.Simulation;

public static partial class ClockworkConformance
{
    /// <summary>
    /// Fails a debug assertion on the scheduler lane after an <c>await</c> and catches its exception, as code under test
    /// which counts a failure toward an outcome does, then waits on virtual time and continues. The trace records
    /// <c>asserting</c>, <c>caught</c>, and <c>continued</c> as the probe reaches each (test/Durable.Storage/docs/spec/verification.md §11.2).
    /// </summary>
    /// <param name="environment">The run's environment, whose trace records the probe's progress.</param>
    /// <param name="message">The assertion's message.</param>
    /// <returns>A task which completes once the probe continues past its waits.</returns>
    public static async Task CatchADebugAssertionAsync(SimulationEnvironment environment, string message)
    {
        ArgumentNullException.ThrowIfNull(environment);
        await Task.Yield();
        environment.Trace.Record("asserting");
        try
        {
            Debug.Fail(message);
        }
#pragma warning disable CA1031 // As code under test which counts any failure toward an outcome.
        catch (Exception)
#pragma warning restore CA1031
        {
            environment.Trace.Record("caught");
        }

        for (var wait = 0; wait < 3; wait++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System);
        }

        environment.Trace.Record("continued");
    }
}
