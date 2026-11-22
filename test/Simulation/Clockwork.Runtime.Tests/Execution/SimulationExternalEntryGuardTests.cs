using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Tests.Execution;

/// <summary>
/// Covers <see cref="SimulationExternalEntryGuard"/>: it must not falsely reject the normal case
/// of no ambient context at all, must not reject legitimate re-entrancy into the same runtime, and
/// must throw an actionable exception when a callback is about to enter one runtime's boundary
/// while a *different* runtime's ambient context is already active on the calling thread.
/// </summary>
public sealed class SimulationExternalEntryGuardTests
{
    private static SimulationRuntimeIdentity NewRuntime(string description) =>
        RuntimeTestHarness.NewRuntime(description: description);

    [Fact]
    public void ValidateEntryDoesNotThrowWhenNoAmbientContextIsPresent()
    {
        // The normal/expected case: first entry into a simulation, or a boundary that never
        // opted into ambient-context integration. Must never be falsely flagged.
        Assert.False(SimulationExecutionContext.IsActive);
        SimulationExternalEntryGuard.ValidateEntry(NewRuntime("expected"), "unit-test-boundary");
    }

    [Fact]
    public void ValidateEntryDoesNotThrowWhenAmbientContextMatchesTheExpectedRuntime()
    {
        var runtime = NewRuntime("same");

        using (SimulationExecutionContext.EnterRuntime(runtime))
        {
            // Ordinary re-entrancy (e.g. a nested pump) into the *same* runtime must not throw.
            SimulationExternalEntryGuard.ValidateEntry(runtime, "unit-test-boundary");
        }
    }

    [Fact]
    public void ValidateEntryThrowsWhenAmbientContextBelongsToADifferentRuntime()
    {
        var ambientRuntime = NewRuntime("ambient");
        var expectedRuntime = NewRuntime("expected");

        using (SimulationExecutionContext.EnterRuntime(ambientRuntime))
        {
            var exception = Assert.Throws<SimulationExternalEntryException>(
                () => SimulationExternalEntryGuard.ValidateEntry(expectedRuntime, "unit-test-boundary"));

            Assert.Contains("unit-test-boundary", exception.Message, StringComparison.Ordinal);
            Assert.Contains(ambientRuntime.Id.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.Contains(expectedRuntime.Id.ToString(), exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidateEntryThrowsForNullExpectedRuntime()
    {
        Assert.Throws<ArgumentNullException>(() => SimulationExternalEntryGuard.ValidateEntry(null!, "boundary"));
    }

    [Fact]
    public void ValidateEntryThrowsForNullOrEmptyBoundaryName()
    {
        Assert.Throws<ArgumentException>(() => SimulationExternalEntryGuard.ValidateEntry(NewRuntime("x"), string.Empty));
    }

    [Fact]
    public void ExternalEntryExceptionMentionsARecentMatchingSuppressionEvent()
    {
        var ambientRuntime = NewRuntime("ambient-with-suppression");
        var expectedRuntime = NewRuntime("expected");
        var reason = $"guard-diagnostics-test {Guid.NewGuid()}";

        using (SimulationExecutionContext.EnterRuntime(ambientRuntime))
        {
            // Record a suppression event for this exact ambient runtime, then immediately trigger
            // an external-entry mismatch: the guard's message should note the suppression as a
            // plausible explanation rather than leaving the reader to guess.
            using (SimulationExecutionContext.SuppressFlow(reason))
            {
            }

            var exception = Assert.Throws<SimulationExternalEntryException>(
                () => SimulationExternalEntryGuard.ValidateEntry(expectedRuntime, "unit-test-boundary"));

            Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        }
    }
}
