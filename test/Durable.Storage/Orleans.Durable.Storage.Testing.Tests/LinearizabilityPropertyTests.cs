using CsCheck;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// The checker accepts every history generated from a sequential execution, rejects every history with an injected
/// stale read or lost write, and reaches one verdict whether it partitions a history or not, and whether the history is
/// operations or events.
/// </summary>
public sealed class LinearizabilityPropertyTests
{
    private const int Keys = 3;

    [Fact]
    public void GeneratedJournalHistoriesAreLinearizable() =>
        HistoryGenerators.JournalSteps.Array[0, 60].Sample(
            static steps =>
            {
                var history = HistoryGenerators.AssignClients(HistoryGenerators.Journal(steps, Keys, expire: true).Operations);

                var analysis = LinearizabilityChecker.CheckVerbose(JournalModel.Instance, history, TestContext.Current.CancellationToken);

                Assert.Equal(LinearizabilityVerdict.Ok, analysis.Verdict);
                Assert.All(analysis.Partitions, static partition =>
                    Assert.Equal(Enumerable.Range(0, partition.Operations.Count), Assert.Single(partition.PartialLinearizations).Order()));
            },
            iter: 300);

    [Fact]
    public void AJournalStaleReadIsRejected() =>
        Gen.Select(HistoryGenerators.JournalSteps.Array[0, 40], Gen.Int[0, 1_000], Gen.Int[0, 30]).Sample(
            static (steps, choice, gap) =>
            {
                var history = HistoryGenerators.AssignClients(
                    HistoryGenerators.InjectStaleRead(HistoryGenerators.Journal(steps, Keys, expire: false), choice, gap));

                Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(JournalModel.Instance, history, TestContext.Current.CancellationToken));
            },
            iter: 300);

    [Fact]
    public void AJournalLostWriteIsRejected() =>
        Gen.Select(HistoryGenerators.JournalSteps.Array[0, 40], Gen.Int[0, 1_000], Gen.Int[0, 1_000]).Sample(
            static (steps, choice, position) =>
            {
                var history = HistoryGenerators.AssignClients(
                    HistoryGenerators.InjectLostWrite(HistoryGenerators.Journal(steps, Keys, expire: false), choice, position));

                Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(JournalModel.Instance, history, TestContext.Current.CancellationToken));
            },
            iter: 300);

    /// <summary>
    /// P-compositionality: checking each journal alone reaches the verdict of checking every journal at once, over
    /// generated histories, histories with injected violations, and histories with a read's answer replaced by an
    /// arbitrary one. Checking the history as events reaches it too.
    /// </summary>
    [Fact]
    public void PartitionedUnpartitionedAndEventChecksAgree()
    {
        var unpartitioned = new UnpartitionedModel<JournalModelState, JournalInput, JournalOutput>(JournalModel.Instance, static input => input.Key);
        Gen.Select(HistoryGenerators.JournalSteps.Array[0, 20], Gen.Int[0, 3], Gen.Int[0, 1_000], Gen.Bool, Gen.Int[0, 4]).Sample(
            (steps, variant, choice, exists, version) =>
            {
                var execution = HistoryGenerators.Journal(steps, Keys, expire: variant == 0);
                var history = HistoryGenerators.AssignClients(variant switch
                {
                    1 => HistoryGenerators.InjectStaleRead(execution, choice, choice % 30),
                    2 => HistoryGenerators.InjectLostWrite(execution, choice, choice),
                    3 => HistoryGenerators.PerturbRead(execution.Operations, choice, exists, version),
                    _ => execution.Operations,
                });

                var partitioned = LinearizabilityChecker.Check(JournalModel.Instance, history, TestContext.Current.CancellationToken);

                Assert.Equal(partitioned, LinearizabilityChecker.Check(unpartitioned, history, TestContext.Current.CancellationToken));
                Assert.Equal(partitioned, LinearizabilityChecker.Check(JournalModel.Instance, HistoryGenerators.ToEvents(history), TestContext.Current.CancellationToken));
                Assert.Equal(partitioned, LinearizabilityChecker.CheckVerbose(JournalModel.Instance, history, TestContext.Current.CancellationToken).Verdict);
                if (variant is 1 or 2)
                {
                    Assert.Equal(LinearizabilityVerdict.Illegal, partitioned);
                }
            },
            iter: 300);
    }

    /// <summary>
    /// Etcd-shaped histories like Porcupine's Jepsen vectors, which are not copied (tests/shared-vectors/linearizability):
    /// reads which time out, and writes and compare-and-sets with indeterminate outcomes.
    /// </summary>
    [Fact]
    public void GeneratedEtcdHistoriesAreLinearizable() =>
        HistoryGenerators.EtcdSteps.Array[0, 80].Sample(
            static steps =>
            {
                var history = HistoryGenerators.AssignClients(HistoryGenerators.Etcd(steps).Operations);

                Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(EtcdModel.Instance, history, TestContext.Current.CancellationToken));
                Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(EtcdModel.Instance, HistoryGenerators.ToEvents(history), TestContext.Current.CancellationToken));
            },
            iter: 300);

    [Fact]
    public void AnEtcdStaleReadIsRejected() =>
        Gen.Select(HistoryGenerators.EtcdSteps.Array[0, 60], Gen.Int[0, 1_000], Gen.Int[0, 30]).Sample(
            static (steps, choice, gap) =>
            {
                var history = HistoryGenerators.AssignClients(HistoryGenerators.InjectStaleRegisterRead(HistoryGenerators.Etcd(steps), choice, gap));

                Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(EtcdModel.Instance, history, TestContext.Current.CancellationToken));
            },
            iter: 300);
}
