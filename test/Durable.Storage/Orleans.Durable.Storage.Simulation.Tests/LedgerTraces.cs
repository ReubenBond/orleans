using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Writes a simulation's ledger trace as the Quint module which the ledger model's trace specification validates, when
/// <see cref="Variable"/> names a directory (test/Durable.Storage/docs/spec/verification.md §5.1).
/// </summary>
internal static class LedgerTraces
{
    /// <summary>
    /// The environment variable naming the directory which receives the traces.
    /// </summary>
    public const string Variable = "DURABLE_STORAGE_LEDGER_TRACES";

    /// <summary>
    /// Gets a value indicating whether <see cref="Variable"/> names a directory, so the run's trace is exported and its
    /// nodes' restarts log what their recoveries established (<see cref="LedgerTrace.LogsRecoveredReplicas"/>).
    /// </summary>
    public static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable));

    /// <summary>
    /// Writes <paramref name="trace"/>'s events of <paramref name="ledger"/> to
    /// <c>&lt;directory&gt;/&lt;scenario&gt;.seed-&lt;seed&gt;/LedgerTraceData.qnt</c>, from within the run.
    /// </summary>
    public static void Export(
        SimulationEnvironment environment,
        string scenario,
        LedgerTrace trace,
        LedgerId ledger,
        IReadOnlyList<LedgerStoreNode> nodes,
        int quorum,
        int window)
    {
        if (!Enabled)
        {
            return;
        }

        var directory = Environment.GetEnvironmentVariable(Variable)!;

        var module = Render(trace, ledger, nodes, quorum, window);
        var target = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{scenario}.seed-{environment.Seed}"));
        _ = Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, LedgerTraceExport.ModuleName + ".qnt"), module);
    }

    /// <summary>
    /// Renders <paramref name="trace"/>'s events of <paramref name="ledger"/> as the module trace validation checks.
    /// </summary>
    public static string Render(LedgerTrace trace, LedgerId ledger, IReadOnlyList<LedgerStoreNode> nodes, int quorum, int window)
    {
        if (!trace.LogsRecoveredReplicas)
        {
            throw new InvalidOperationException("A trace exported for validation logs what each restarted node's recovery established.");
        }

        return LedgerTraceExport.Render(trace.Events, ledger, [.. nodes.Select(static node => node.Address)], quorum, window);
    }
}
