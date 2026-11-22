using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A node's measure of each primary ledger's load (orleans.md §5.6): proposed bytes per second over <c>LoadWindow</c> on
/// the monotonic clock, unknown until a writer's samples span the window, and never negative, since samples are kept per
/// writer lifetime.
/// </summary>
[NoPageLeakCheck("The load sampler rents no pages.")]
public sealed class LedgerLoadSamplerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly LedgerId A = new(1);
    private static readonly LedgerId B = new(2);
    private static readonly SequencerInstanceId First = SequencerInstanceId.New();
    private static readonly SequencerInstanceId Second = SequencerInstanceId.New();

    private readonly FakeTimeProvider _time = new();

    /// <summary>
    /// A ledger's load is reported only once its samples span the window, then as the bytes proposed since the newest
    /// sample at least a window old, per second over that span.
    /// </summary>
    [Fact]
    public void ALoadIsReportedOnceItsSamplesSpanTheWindow()
    {
        var load = new LedgerLoadSampler(Window, _time);

        Assert.Empty(Sample(load, TimeSpan.Zero, (A, First, 1_000)));
        Assert.Empty(Sample(load, TimeSpan.FromSeconds(30), (A, First, 4_000)));
        Assert.Empty(Sample(load, TimeSpan.FromSeconds(29), (A, First, 4_000)));
        Assert.Equal(1_000, Sample(load, TimeSpan.FromSeconds(1), (A, First, 61_000))[A]);
        Assert.Equal(2_000, Sample(load, TimeSpan.FromSeconds(30), (A, First, 124_000))[A]);
        Assert.Equal(1_600, Sample(load, TimeSpan.FromSeconds(60), (A, First, 220_000))[A]);
    }

    /// <summary>A ledger missing from a sample loses its samples: sampled again, its load is unknown for a whole window.</summary>
    [Fact]
    public void ALedgerMissingFromASampleStartsAgain()
    {
        var load = new LedgerLoadSampler(Window, _time);
        _ = Sample(load, TimeSpan.Zero, (A, First, 0), (B, First, 0));

        var both = Sample(load, Window, (A, First, 60), (B, First, 120));
        Assert.Equal((1, 2), (both[A], both[B]));

        _ = Sample(load, TimeSpan.FromSeconds(30), (A, First, 90));
        var again = Sample(load, TimeSpan.FromSeconds(30), (A, First, 120), (B, First, 240));
        Assert.Equal(1, Assert.Single(again).Value);
        Assert.False(again.ContainsKey(B));
    }

    /// <summary>
    /// A ledger served under a new writer lifetime, whose proposed bytes count from zero again, starts over: its load is
    /// unknown for a whole window, and never negative.
    /// </summary>
    [Fact]
    public void ANewWriterLifetimeStartsOverAndNoLoadIsNegative()
    {
        var load = new LedgerLoadSampler(Window, _time);
        _ = Sample(load, TimeSpan.Zero, (A, First, 1_000));
        Assert.Equal(1_000, Sample(load, Window, (A, First, 61_000))[A]);

        Assert.Empty(Sample(load, TimeSpan.FromSeconds(1), (A, Second, 10)));
        Assert.Equal(1, Sample(load, Window, (A, Second, 70))[A]);
    }

    [Fact]
    public void AWindowWhichIsNotPositiveIsRefused()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerLoadSampler(TimeSpan.Zero, _time));
    }

    private System.Collections.Immutable.ImmutableDictionary<LedgerId, long> Sample(LedgerLoadSampler load, TimeSpan after, params (LedgerId, SequencerInstanceId, long)[] proposed)
    {
        _time.Advance(after);
        return load.Sample(proposed);
    }
}
