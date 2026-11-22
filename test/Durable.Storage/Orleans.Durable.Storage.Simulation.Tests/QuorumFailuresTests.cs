using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// <see cref="QuorumFailures"/> admits a quorum's failure only when the simulation's faults explain every replica's.
/// </summary>
public sealed class QuorumFailuresTests
{
    /// <summary>
    /// Missed deadlines are admitted, nested or not; corruption only where the simulation damages media.
    /// </summary>
    [Fact]
    public void TheFaultsFailuresAreAdmitted()
    {
        var failure = new AggregateException(new TimeoutException(), new AggregateException(new TimeoutException()));
        var damaged = new AggregateException(new TimeoutException(), new LedgerReplicaCorruptException());

        Assert.True(QuorumFailures.IsAdmitted(failure, mediaDamage: false));
        Assert.True(QuorumFailures.IsAdmitted(damaged, mediaDamage: true));
        Assert.False(QuorumFailures.IsAdmitted(damaged, mediaDamage: false));
    }

    /// <summary>
    /// A replica's defect, a stopped transport, and a failure which names no replica's failure are not admitted, even
    /// beside admitted ones.
    /// </summary>
    [Fact]
    public void OtherFailuresAreNotAdmitted()
    {
        Assert.False(QuorumFailures.IsAdmitted(new AggregateException(new TimeoutException(), new InvalidOperationException()), mediaDamage: true));
        Assert.False(QuorumFailures.IsAdmitted(new AggregateException(new OperationCanceledException(SimulatedRpc.StoppedMessage)), mediaDamage: true));
        Assert.False(QuorumFailures.IsAdmitted(new AggregateException("The write could not reach its required quorum."), mediaDamage: true));
    }
}
