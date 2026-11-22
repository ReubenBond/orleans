using JournalOperation = Orleans.Durable.Storage.Testing.HistoryOperation<Orleans.Durable.Storage.Testing.JournalInput, Orleans.Durable.Storage.Testing.JournalOutput>;

namespace Orleans.Durable.Storage.Testing.Tests;

public sealed class LinearizabilityAssertTests
{
    private static readonly ReadOnlyMemory<byte> One = "a"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> Nine = "z"u8.ToArray();

    [Fact]
    public void ALinearizableHistoryPasses()
    {
        JournalOperation[] history =
        [
            new(0, JournalInput.Append("j", JournalModel.Origin, One), 0, JournalOutput.Accepted("v1"), 10),
            new(1, JournalInput.Read("j"), 5, JournalOutput.Read(false, JournalModel.Origin, default), 15),
        ];

        LinearizabilityAssert.Linearizable(JournalModel.Instance, history, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A failing assertion names the partition which is not linearizable, its longest partial linearization, and the
    /// operations no partial linearization reaches, and writes the visualization, with its annotations, to the results
    /// directory.
    /// </summary>
    [Fact]
    public void AnIllegalHistoryFailsWithItsVisualization()
    {
        JournalOperation[] history =
        [
            new(0, JournalInput.Append("other", JournalModel.Origin, Nine), 0, JournalOutput.Accepted("v1"), 10),
            new(0, JournalInput.Append("j", JournalModel.Origin, One), 20, JournalOutput.Accepted("v1"), 30),
            new(1, JournalInput.Read("j"), 40, JournalOutput.Read(false, JournalModel.Origin, default), 50),
        ];

        var failure = Record.Exception(() => LinearizabilityAssert.Linearizable(
            JournalModel.Instance,
            history,
            [new LinearizabilityAnnotation(35, "partition healed") { Tag = "Test Framework" }],
            TestContext.Current.CancellationToken));

        Assert.NotNull(failure);
        var lines = failure.Message.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("The history is not linearizable.", lines[0]);
        Assert.Equal(
            "Partition 1 (2 operations) is Illegal; its longest partial linearization has 1, ending #0 client 0 [20, 30] append(j, AAA, 61) -> Accepted(v1). "
                + "No partial linearization reaches 1 operations, including #1 client 1 [40, 50] read(j) -> absent at AAA [].",
            lines[1]);
        var path = lines[^1]["Visualization: ".Length..];
        try
        {
            Assert.StartsWith(TestDiagnostics.ResultsDirectory, path, StringComparison.Ordinal);
            Assert.EndsWith(".linearizability.html", path, StringComparison.Ordinal);
            var html = File.ReadAllText(path);
            Assert.Contains("partition healed", html, StringComparison.Ordinal);
            Assert.Contains("absent at AAA", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A check cancelled before its verdict fails too: an oracle which runs out of time has established nothing.
    /// </summary>
    [Fact]
    public void ACancelledCheckFails()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        JournalOperation[] history = [new(0, JournalInput.Read("j"), 0, JournalOutput.Read(false, JournalModel.Origin, default), 1)];

        var failure = Record.Exception(() => LinearizabilityAssert.Linearizable(JournalModel.Instance, history, cancellationToken: cancelled.Token));

        Assert.NotNull(failure);
        Assert.StartsWith("The linearizability check was cancelled before it reached a verdict.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Partition 0 (1 operations) is Unknown; its longest partial linearization has 0.", failure.Message, StringComparison.Ordinal);
        File.Delete(failure.Message.ReplaceLineEndings("\n").Split('\n')[^1]["Visualization: ".Length..]);
    }
}
