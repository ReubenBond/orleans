using Orleans.Durable.Storage.Engine;

namespace Orleans.Durable.Storage.Diagnostics.Tests;

/// <summary>
/// The shared backoff (conventions.md §6, #335): every wait lies between its step, the first wait doubling with each
/// attempt up to the longest, and twice the step, at most the longest, so none is shorter than the first, whatever the
/// attempt, and no step overflows.
/// </summary>
public sealed class BackoffTests
{
    [Theory]
    [InlineData(20, 1_000)]
    [InlineData(50, 2_000)]
    [InlineData(100, 5_000)]
    [InlineData(30, 30)]
    public void EveryWaitLiesBetweenItsStepAndTwiceItCappedAtTheLongest(int firstMilliseconds, int longestMilliseconds)
    {
        var (first, longest) = (TimeSpan.FromMilliseconds(firstMilliseconds), TimeSpan.FromMilliseconds(longestMilliseconds));
        for (var attempt = 0; attempt < 70; attempt++)
        {
            var step = TimeSpan.FromTicks((long)Math.Min(first.Ticks * Math.Pow(2, attempt), longest.Ticks));
            var upper = TimeSpan.FromTicks(Math.Min(2 * step.Ticks, longest.Ticks));
            for (var draw = 0; draw < 1_000; draw++)
            {
                Assert.InRange(Backoff.Delay(attempt, first, longest, draw / 1_000.0), step, upper);
            }
        }
    }

    [Fact]
    public void AStepNearTheLongestRepresentableWaitDoesNotOverflow()
    {
        var longest = TimeSpan.FromTicks(long.MaxValue / 3);
        Assert.InRange(Backoff.Delay(int.MaxValue, TimeSpan.FromMilliseconds(20), longest, 0.999), longest, longest);
    }

    [Fact]
    public void AFirstWaitWhichIsNotPositiveOrLongerThanTheLongestOrAJitterOutsideTheUnitIntervalIsRefused()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Delay(0, TimeSpan.Zero, TimeSpan.FromSeconds(1), 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Delay(0, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Delay(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Delay(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), double.NaN));
    }
}
