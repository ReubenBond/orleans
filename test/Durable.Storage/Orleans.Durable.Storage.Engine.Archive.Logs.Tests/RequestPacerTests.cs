using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// <see cref="RequestPacer"/> (archive-logs.md §8.6): each call is admitted at the later of now and the end of the requests
/// admitted before it, in arrival order; requests charged as debt delay the calls after them; a rate set later applies
/// to the calls after it; and an unbounded rate admits every call at once.
/// </summary>
[NoPageLeakCheck("The pacer counts requests; the tests rent no pages.")]
public sealed class RequestPacerTests
{
    [Fact]
    public void CallsAreAdmittedWithinTheRateInArrivalOrder()
    {
        var time = new FakeTimeProvider();
        var pacer = new RequestPacer(time, 2);

        var first = pacer.WaitAsync(2, CancellationToken.None);
        var second = pacer.WaitAsync(1, CancellationToken.None);
        var third = pacer.WaitAsync(1, CancellationToken.None);

        Assert.True(first.IsCompleted);
        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(second.IsCompleted);
        Assert.False(third.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(third.IsCompleted);
    }

    [Fact]
    public void ChargedRequestsDelayTheCallsAfterThem()
    {
        var time = new FakeTimeProvider();
        var pacer = new RequestPacer(time, 1);

        Assert.True(pacer.WaitAsync(1, CancellationToken.None).IsCompleted);
        pacer.Charge(3);
        var next = pacer.WaitAsync(1, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(3.5));
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.True(next.IsCompleted);
    }

    [Fact]
    public void ARateSetLaterAppliesToTheCallsAfterIt()
    {
        var time = new FakeTimeProvider();
        var pacer = new RequestPacer(time, double.PositiveInfinity);

        Assert.True(pacer.WaitAsync(1_000_000, CancellationToken.None).IsCompleted);
        pacer.Charge(1_000_000);
        pacer.RequestsPerSecond = 10;
        Assert.True(pacer.WaitAsync(10, CancellationToken.None).IsCompleted);
        var next = pacer.WaitAsync(1, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(next.IsCompleted);
        Assert.Throws<ArgumentOutOfRangeException>(() => pacer.RequestsPerSecond = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = pacer.WaitAsync(-1, CancellationToken.None); });
    }

    /// <summary>
    /// orleans.md §5.6, OR-I22: a pacer with a source paces at the source's rate, read at each admission and charge, not at
    /// the rate it was built with: at a node's assigned share, and at its floor once the share's deadline passed.
    /// </summary>
    [Fact]
    public void ASourcesRateIsReadAtEachAdmissionAndCharge()
    {
        var time = new FakeTimeProvider();
        var rate = 2.0;
        var pacer = new RequestPacer(time, 1_000) { Source = () => rate };
        Assert.Equal(2, pacer.RequestsPerSecond);

        Assert.True(pacer.WaitAsync(2, CancellationToken.None).IsCompleted);
        var shared = pacer.WaitAsync(1, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(shared.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(shared.IsCompleted);

        // The share's deadline passed: the source answers the floor, which the next charge and admission take.
        rate = 0.5;
        pacer.Charge(1);
        var floored = pacer.WaitAsync(1, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(2_499));
        Assert.False(floored.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(floored.IsCompleted);
    }
}
