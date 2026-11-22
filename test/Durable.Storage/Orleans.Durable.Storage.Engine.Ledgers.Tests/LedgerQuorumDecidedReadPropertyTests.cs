using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The fast path against the slow one (LP-I10, ledger-protocol.md §8.3, verification §4.2): a read below the known
/// contiguous tail, which asks one replica at a time and hedges, returns the batch a read of every replica at once returns,
/// over any <c>N</c> from 1 to 5, any <c>Q</c>, with or without a preferred replica, and up to <c>N − Q</c> replicas
/// which lack the decided slot and answer it missing, with backpressure, dropped, with a transient or permanent failure,
/// with corruption, or slowly, past the hedge delay. Since a decided slot is on <c>Q</c> replicas, the read finds it below
/// any retained contiguous tail; only a replica's drop, which is definite, may answer it first (LP-I5).
/// </summary>
public sealed class LedgerQuorumDecidedReadPropertyTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 8 };
    private static readonly Slot Below = new(3);
    private static readonly Slot Tail = new(5);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// How a replica which lacks the decided slot answers a read of it.
    /// </summary>
    internal enum Lacking
    {
        Missing,
        Backpressure,
        Dropped,
        Transient,
        Permanent,
        Corrupt,
        Slow,
    }

    [Fact(Timeout = 120_000)]
    public async Task ADecidedReadReturnsWhatAReadOfEveryReplicaReturns()
    {
        var cases =
            from n in Gen.Int[1, 5]
            from q in Gen.Int[1, n]
            from preferred in Gen.Int[-1, n - 1]
            from lacks in Gen.Bool.Array[n]
            from answers in Gen.Enum<Lacking>().Array[n]
            select new Case(n, q, preferred < 0 ? null : preferred, LackingMask(lacks, n - q), answers);

        var token = TestContext.Current.CancellationToken;
        await cases.SampleAsync(@case => _leaks.InScopeAsync(() => CheckAsync(@case, token)), iter: 400);
    }

    /// <summary>Returns the mask of the first <paramref name="most"/> replicas <paramref name="lacks"/> marks.</summary>
    private static int LackingMask(bool[] lacks, int most)
    {
        var mask = 0;
        for (var index = 0; index < lacks.Length && int.PopCount(mask) < most; index++)
        {
            if (lacks[index])
            {
                mask |= 1 << index;
            }
        }

        return mask;
    }
    private static async Task CheckAsync(Case @case, CancellationToken token)
    {
        var time = new TimerSignallingTimeProvider();
        var replicas = Enumerable.Range(0, @case.N).Select(index => Replica(@case, index, time)).ToArray();
        await using var quorum = LedgerQuorumTestData.Create(replicas, @case.Q, Window, time, @case.Preferred);
        ReplicaRead read;
        try
        {
            var timers = time.Timers;
            read = await time.AdvanceUntilCompletedAsync(
                    quorum.ReadAsync(Below, Tail, token).AsTask(), LedgerQuorum.ReadHedgeDelay, timers, token)
                .WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, token);
        }
        finally
        {
            // The slow replicas' reads which lost answer, so the quorum's disposal does not wait on them.
            time.Advance(TimeSpan.FromHours(1));
        }

        var asked = replicas.Select(static replica => replica.ReadCalls.Count).ToArray();
        Assert.All(asked, static count => Assert.InRange(count, 0, 1));
        if (@case.Preferred is { } preferred)
        {
            Assert.Equal(1, asked[preferred]);
        }

        if (read.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
        {
            Assert.Contains(
                Enumerable.Range(0, @case.N),
                index => @case.Lacks(index) && @case.Answers[index] == Lacking.Dropped && asked[index] == 1);
            return;
        }

        using var batch = read.Batch;
        Assert.Equal("decided"u8.ToArray(), batch.ToArray());
    }

    private static RecordingLedgerReplica Replica(Case @case, int index, TimeProvider time)
    {
        var lacks = @case.Lacks(index);
        var answer = @case.Answers[index];
        return new RecordingLedgerReplica
        {
            ReadAsyncHandler = (slot, cancellationToken) => !lacks
                ? ValueTask.FromResult(new ReplicaRead(LedgerReplicaTestData.Buffer("decided"u8)))
                : answer switch
                {
                    Lacking.Missing => ValueTask.FromResult(ReplicaAnswers.Missing),
                    Lacking.Backpressure => ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance)),
                    Lacking.Dropped => ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Dropped.Instance)),
                    Lacking.Transient => ValueTask.FromException<ReplicaRead>(new IOException("The replica is unreachable.")),
                    Lacking.Permanent => ValueTask.FromException<ReplicaRead>(new ObjectDisposedException("replica")),
                    Lacking.Corrupt => ValueTask.FromException<ReplicaRead>(new LedgerReplicaCorruptException(slot, "The batch fails its checksum.")),
                    Lacking.Slow => SlowlyMissingAsync(time, cancellationToken),
                    _ => throw new ArgumentOutOfRangeException(nameof(@case)),
                },
        };
    }

    private static async ValueTask<ReplicaRead> SlowlyMissingAsync(TimeProvider time, CancellationToken cancellationToken)
    {
        await Task.Delay(3 * LedgerQuorum.ReadHedgeDelay, time, cancellationToken);
        return ReplicaAnswers.Missing;
    }

    /// <summary>
    /// One case: <paramref name="N"/> replicas with quorum <paramref name="Q"/>, an optional preferred replica, the
    /// replicas which lack the slot as a bit mask, and how each would answer if it lacks the slot.
    /// </summary>
    internal sealed record Case(int N, int Q, int? Preferred, int LackingMask, Lacking[] Answers)
    {
        internal bool Lacks(int index) => (LackingMask & (1 << index)) != 0;

        public override string ToString() =>
            $"N={N} Q={Q} preferred={Preferred?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} lacking=[{string.Join(", ", Enumerable.Range(0, N).Where(Lacks).Select(index => $"{index}: {Answers[index]}"))}]";
    }
}
