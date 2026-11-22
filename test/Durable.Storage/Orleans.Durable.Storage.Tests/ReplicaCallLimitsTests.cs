using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The replica call limits a silo computes at its start (orleans.md §5.12, OR-I17): the payload limit is the least of
/// <c>MaxNodeCallBytes / 2</c>, <c>MaxReplicaCallBytes / 4</c>, and <c>MaxMessageBodySize − 64 KiB</c>, 32 MiB by default;
/// a silo whose limits leave no payload, whose read reply passes the payload limit, or whose deadline is not finite and
/// positive does not start; and each replica option is positive.
/// </summary>
[NoPageLeakCheck("The limits rent no pages.")]
public sealed class ReplicaCallLimitsTests
{
    private const long MiB = 1 << 20;

    [Theory]
    [InlineData(64 * MiB, 128 * MiB, 100 * MiB, 32 * MiB)]
    [InlineData(16 * MiB, 128 * MiB, 100 * MiB, 8 * MiB)]
    [InlineData(64 * MiB, 32 * MiB, 100 * MiB, 8 * MiB)]
    [InlineData(64 * MiB, 128 * MiB, 4 * MiB, 4 * MiB - (64 << 10))]
    public void ThePayloadLimitIsTheLeastOfItsTerms(long maxNodeCallBytes, long maxReplicaCallBytes, long maxMessageBodySize, long payloadLimit)
    {
        var limits = TestReplicaLimits.Of(
            options =>
            {
                options.MaxNodeCallBytes = maxNodeCallBytes;
                options.MaxReplicaCallBytes = maxReplicaCallBytes;
            },
            maxMessageBodySize: (int)maxMessageBodySize);

        Assert.Equal(payloadLimit, limits.PayloadLimit);
    }

    [Fact]
    public void TheDefaultsGiveA32MiBPayloadLimitAnd1MiBReadReply()
    {
        var limits = TestReplicaLimits.Of();

        Assert.Equal((32 * MiB, MiB), (limits.PayloadLimit, limits.MaxReadReplyBytes));
    }

    [Fact]
    public void LimitsWhichLeaveNoPayloadStopTheSilo() =>
        _ = Assert.Throws<ArgumentException>(() => TestReplicaLimits.Of(maxMessageBodySize: 64 << 10));

    /// <summary>
    /// OR-I17: a forwarded write's lane, <c>MaxForwardCallBytes</c>, holds at least the payload limit, the largest record a
    /// forwarded write carries, or the silo does not start; at the limit it starts.
    /// </summary>
    [Fact]
    public void AForwardingLaneBelowThePayloadLimitStopsTheSilo()
    {
        Assert.Equal("MaxForwardCallBytes", Assert.Throws<ArgumentException>(() => TestReplicaLimits.Of(static options => options.MaxForwardCallBytes = 32 * MiB - 1)).ParamName);
        Assert.Equal((int)(32 * MiB), TestReplicaLimits.Of(static options => options.MaxForwardCallBytes = 32 * MiB).PayloadLimit);
    }

    [Fact]
    public void AReadReplyAboveThePayloadLimitStopsTheSilo()
    {
        _ = TestReplicaLimits.Of(static options => options.MaxReadReplyBytes = (int)(32 * MiB));
        _ = Assert.Throws<ArgumentException>(() => TestReplicaLimits.Of(static options => options.MaxReadReplyBytes = (int)(32 * MiB) + 1));
    }

    public static TheoryData<TimeSpan> Deadlines =>
        [TimeSpan.Zero, -TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan, TimeSpan.MaxValue, ReplicaCallLimits.LongestDeadline + TimeSpan.FromMilliseconds(1)];

    [Theory]
    [MemberData(nameof(Deadlines))]
    public void ADeadlineWhichIsNotPositiveOrPassesATimersLongestDueTimeStopsTheSilo(TimeSpan deadline) =>
        _ = Assert.Throws<ArgumentException>(() => TestReplicaLimits.Of(deadline: deadline));

    /// <summary>The longest deadline the limits accept arms a timer, so a validated limit never throws later.</summary>
    [Fact]
    public void TheLongestDeadlineArmsATimer()
    {
        var limits = TestReplicaLimits.Of(deadline: ReplicaCallLimits.LongestDeadline);
        using var timer = TimeProvider.System.CreateTimer(static _ => { }, null, limits.Deadline, Timeout.InfiniteTimeSpan);
        Assert.Equal(ReplicaCallLimits.LongestDeadline, limits.Deadline);
    }

    [Theory]
    [InlineData(nameof(DurableStorageOptions.MaxReplicaCalls))]
    [InlineData(nameof(DurableStorageOptions.MaxReplicaCallBytes))]
    [InlineData(nameof(DurableStorageOptions.MaxNodeCalls))]
    [InlineData(nameof(DurableStorageOptions.MaxNodeCallBytes))]
    [InlineData(nameof(DurableStorageOptions.MaxNodeControlCalls))]
    [InlineData(nameof(DurableStorageOptions.MaxReadReplyBytes))]
    [InlineData(nameof(DurableStorageOptions.MaxForwardedReadBytes))]
    [InlineData(nameof(DurableStorageOptions.MaxForwardCallBytes))]
    [InlineData(nameof(DurableStorageOptions.MaxForwardReadCalls))]
    public void EachReplicaOptionIsPositive(string option)
    {
        var options = TestReplicaLimits.Options(options =>
        {
            var property = typeof(DurableStorageOptions).GetProperty(option)!;
            property.SetValue(options, Convert.ChangeType(0, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
        });

        Assert.Equal(option, Assert.ThrowsAny<ArgumentException>(options.Validate).ParamName);
    }
}
