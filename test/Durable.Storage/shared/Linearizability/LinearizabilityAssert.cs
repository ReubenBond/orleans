using System.Globalization;
using System.Text;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Asserts that client histories are linearizable (docs/spec/verification.md §3.4).
/// </summary>
internal static class LinearizabilityAssert
{
    private const int DescribedPartitions = 3;
    private const int DescribedOperations = 5;

    /// <summary>
    /// Asserts that a history is linearizable. A history which is not, or whose check is cancelled first, fails the test
    /// with the partitions which could not be linearized, and leaves Porcupine's visualization of the history in the
    /// results directory.
    /// </summary>
    /// <param name="model">The sequential specification.</param>
    /// <param name="history">The operations.</param>
    /// <param name="annotations">Notes the visualization draws beside the history, such as injected faults.</param>
    /// <param name="cancellationToken">Ends the check inconclusively, which fails the assertion.</param>
    public static void Linearizable<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryOperation<TInput, TOutput>> history,
        IEnumerable<LinearizabilityAnnotation>? annotations = null,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        // The search records no partial linearizations unless it must explain a failure, which costs time on long
        // histories; it is deterministic, so the second search reaches the first one's verdict.
        var verdict = LinearizabilityChecker.Check(model, history, cancellationToken);
        if (verdict == LinearizabilityVerdict.Ok)
        {
            return;
        }

        var analysis = LinearizabilityChecker.CheckVerbose(model, history, stopAtFirstIllegal: true, cancellationToken);
        if (annotations is not null)
        {
            analysis.AddAnnotations(annotations);
        }

        var path = TestDiagnostics.PathFor(TestContext.Current.Test?.TestDisplayName ?? "history", ".linearizability.html");
        PorcupineVisualization.WriteFile(analysis, path);
        Assert.Fail(Describe(analysis, verdict, path));
    }

    /// <summary>
    /// Describes the partitions of an analysis which have the check's verdict: the illegal ones, or those a cancelled
    /// check left unknown.
    /// </summary>
    internal static string Describe<TState, TInput, TOutput>(
        LinearizabilityAnalysis<TState, TInput, TOutput> analysis,
        LinearizabilityVerdict verdict,
        string visualization)
        where TInput : notnull
    {
        var message = new StringBuilder(verdict == LinearizabilityVerdict.Illegal
            ? "The history is not linearizable."
            : "The linearizability check was cancelled before it reached a verdict.");
        var failed = analysis.Partitions
            .Select(static (partition, index) => (Partition: partition, Index: index))
            .Where(partition => partition.Partition.Verdict == verdict)
            .ToArray();
        foreach (var (partition, index) in failed.Take(DescribedPartitions))
        {
            var operations = partition.Operations;
            var longest = partition.PartialLinearizations.Count > 0 ? partition.PartialLinearizations[0] : [];
            _ = message.AppendLine().Append(CultureInfo.InvariantCulture, $"Partition {index} ({operations.Count} operations) is {partition.Verdict}; its longest partial linearization has {longest.Count}");
            if (longest.Count > 0)
            {
                _ = message.Append(", ending ").AppendJoin(
                    "; ",
                    longest.Skip(Math.Max(0, longest.Count - DescribedOperations)).Select(operation => Describe(analysis.Model, operations, operation)));
            }

            var reached = partition.PartialLinearizations.SelectMany(static linearization => linearization).ToHashSet();
            var unreached = Enumerable.Range(0, operations.Count).Where(operation => !reached.Contains(operation)).ToArray();
            if (unreached.Length > 0)
            {
                _ = message.Append(CultureInfo.InvariantCulture, $". No partial linearization reaches {unreached.Length} operations, including ")
                    .AppendJoin("; ", unreached.Take(DescribedOperations).Select(operation => Describe(analysis.Model, operations, operation)));
            }

            _ = message.Append('.');
        }

        if (failed.Length > DescribedPartitions)
        {
            _ = message.AppendLine().Append(CultureInfo.InvariantCulture, $"{failed.Length - DescribedPartitions} more partitions are {verdict}.");
        }

        return message.AppendLine().Append("Visualization: ").Append(visualization).ToString();
    }

    private static string Describe<TState, TInput, TOutput>(
        LinearizabilityModel<TState, TInput, TOutput> model,
        IReadOnlyList<HistoryOperation<TInput, TOutput>> operations,
        int operation)
        where TInput : notnull
    {
        var described = operations[operation];
        var end = described.Return == HistoryOperation.Unbounded ? "∞" : described.Return.ToString(CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"#{operation} client {described.ClientId} [{described.Call}, {end}] {model.DescribeOperation(described.Input, described.Output)}");
    }
}
