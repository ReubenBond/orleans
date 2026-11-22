using System.Globalization;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Runs seeded simulations so every failure replays from its seed (test/Durable.Storage/docs/spec/verification.md §7.4).
/// </summary>
/// <remarks>
/// The seed is printed before the scenario starts, and <c>CLOG_TEST_SEED</c> replaces it to replay a failure. A
/// failing run prints the replay command and writes its history to the results directory. A seeded simulation must
/// replay identically: it schedules through its seed and a virtual clock such as <c>FakeTimeProvider</c>, never through
/// thread-pool timing or the wall clock. <see cref="RunAndReplayAsync"/> checks that. A simulation project on Clockwork
/// also links <c>SeededSimulation.Clockwork.cs</c>, which runs a scenario in a Clockwork cluster.
/// </remarks>
internal static partial class SeededSimulation
{
    internal const string SeedVariable = "CLOG_TEST_SEED";

    /// <summary>
    /// Runs the scenario with <paramref name="seed"/>, or with <c>CLOG_TEST_SEED</c> when it is set. A debug assertion
    /// which the scenario raised fails the run whether or not the code under test caught it, and takes precedence over
    /// a failure which followed it, which it carries as its inner exception (§11.2).
    /// </summary>
    internal static async Task<SimulationRun> RunAsync(int seed, Func<SimulationRun, Task> scenario)
    {
        var run = new SimulationRun(Seed(seed));
        TestDiagnostics.WriteLine(run.Seed == seed
            ? Invariant($"Seed {run.Seed}; replay with {SeedVariable}={run.Seed}.")
            : Invariant($"Seed {run.Seed} from {SeedVariable}, replacing {seed}."));
        var test = TestContext.Current.Test;
        var assertions = DebugAssertions.RaisedCount;
        try
        {
            await scenario(run);
        }
        catch (Exception exception)
        {
            ReportFailure(run);
            var assertion = DebugAssertions.TakeRaisedBy(test, assertions).FirstOrDefault()?.WithContext(exception);
            if (assertion is null || ReferenceEquals(assertion, exception))
            {
                throw;
            }

            throw assertion;
        }

        if (DebugAssertions.TakeRaisedBy(test, assertions) is [var first, ..])
        {
            ReportFailure(run);
            throw first;
        }

        return run;
    }

    /// <summary>
    /// Runs the scenario twice from the same seed and fails unless both runs record the same history. Opt in for a
    /// simulation which must be deterministic.
    /// </summary>
    internal static async Task RunAndReplayAsync(int seed, Func<SimulationRun, Task> scenario)
    {
        var first = (await RunAsync(seed, scenario)).History;
        var second = (await RunAsync(seed, scenario)).History;
        AssertSameHistory(Seed(seed), first, second);
    }

    /// <summary>
    /// Fails unless two recordings from <paramref name="seed"/> hold the same history, writing both to the results
    /// directory when they differ.
    /// </summary>
    internal static void AssertSameHistory(int seed, IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var length = Math.Min(first.Count, second.Count);
        var divergence = 0;
        while (divergence < length && string.Equals(first[divergence], second[divergence], StringComparison.Ordinal))
        {
            divergence++;
        }

        if (divergence == length && first.Count == second.Count)
        {
            return;
        }

        var firstPath = WriteHistory(first, Invariant($".seed-{seed}.first.history.log"));
        var secondPath = WriteHistory(second, Invariant($".seed-{seed}.replay.history.log"));
        Assert.Fail(Invariant(
            $"Seed {seed} did not replay: the histories diverge at entry {divergence} ('{Entry(first, divergence)}' then '{Entry(second, divergence)}'). Histories: {firstPath}, {secondPath}"));
    }

    private static int Seed(int seed) =>
        Environment.GetEnvironmentVariable(SeedVariable) is { Length: > 0 } value
            ? int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : seed;

    private static void ReportFailure(SimulationRun run)
    {
        var test = DisplayNameCommand();
        TestDiagnostics.WriteLine(Invariant($"Seed {run.Seed} failed. Replay:"));
        TestDiagnostics.WriteLine(Invariant($"  {SeedVariable}={run.Seed} {test}"));
        TestDiagnostics.WriteLine(Invariant($"  $env:{SeedVariable}='{run.Seed}'; {test}; Remove-Item Env:{SeedVariable}"));
        try
        {
            TestDiagnostics.WriteLine(Invariant($"History: {WriteHistory(run.History, Invariant($".seed-{run.Seed}.history.log"))}"));
        }
        catch (IOException error)
        {
            TestDiagnostics.WriteLine(Invariant($"The history could not be written: {error.Message}"));
        }
    }

    /// <summary>
    /// Returns the command which runs the current test case alone: a theory's row by its display name, which holds its
    /// arguments.
    /// </summary>
    private static string DisplayNameCommand()
    {
        var name = TestContext.Current.Test?.TestDisplayName ?? "*";
        var project = Invariant($"test/Durable.Storage/{TestDiagnostics.AssemblyName}/{TestDiagnostics.AssemblyName}.csproj");
        return Invariant(
            $"dotnet test --project {project} -c {TestDiagnostics.Configuration} -- --filter-display-name \"{name}\"");
    }

    private static string WriteHistory(IReadOnlyList<string> history, string suffix)
    {
        var path = TestDiagnostics.PathFor(TestContext.Current.Test?.TestDisplayName ?? "simulation", suffix);
        File.WriteAllLines(path, history);
        return path;
    }

    private static string Entry(IReadOnlyList<string> history, int index) => index < history.Count ? history[index] : "<end>";

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}
