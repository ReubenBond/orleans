using System.Globalization;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Writes a simulation's ledger map trace as the Quint module which replays it against <c>test/Durable.Storage/specs/ledgers/LedgerMap.qnt</c>,
/// when <see cref="Variable"/> names a directory (test/Durable.Storage/docs/spec/verification.md §5.1); <c>test/Durable.Storage/eng/ledger-map-traces.ps1</c> sets it.
/// </summary>
internal static class LedgerMapTraces
{
    /// <summary>The environment variable naming the directory which receives the traces.</summary>
    public const string Variable = "CLOG_LEDGER_MAP_TRACES";

    /// <summary>Gets a value indicating whether <see cref="Variable"/> names a directory, so runs export their traces.</summary>
    public static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable));

    /// <summary>
    /// Renders <paramref name="trace"/> of <paramref name="simulation"/> as the module <c>LedgerMapTrace&lt;scenario&gt;&lt;seed&gt;</c> and writes it to
    /// <c>&lt;directory&gt;/&lt;module&gt;.qnt</c>, when enabled.
    /// </summary>
    public static void Export(SimulationEnvironment environment, string scenario, string simulation, LedgerMapTrace trace, int keyBits, bool tracesWrites)
    {
        if (!Enabled)
        {
            return;
        }

        var directory = Environment.GetEnvironmentVariable(Variable)!;
        var module = string.Create(CultureInfo.InvariantCulture, $"LedgerMapTrace{scenario}{environment.Seed}");
        var source = string.Create(CultureInfo.InvariantCulture, $"{simulation} at seed {environment.Seed}");
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, module + ".qnt"), LedgerMapTraceExport.Render(trace, module, source, keyBits, tracesWrites));
    }
}
