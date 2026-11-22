using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

/// <summary>
/// Semantic conformance tests for <see cref="ControlledGuid"/>: RFC variant/version shape, V7
/// timestamp extraction, determinism/replay, per-node isolation, independence from the application
/// random streams, active missing-context failure, and inactive-simulation rejection.
/// </summary>
public sealed class ControlledGuidTests
{
    [Fact]
    public void NewGuidHasVersion4AndRfcVariant()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
        var guid = ShimTestHarness.RunInSimulation(env, ControlledGuid.NewGuid);

        Assert.Equal(4, guid.Version);
        AssertRfcVariant(guid);
        Assert.NotEqual(Guid.Empty, guid);
    }

    [Fact]
    public void CreateVersion7HasVersion7AndRfcVariant()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
        var guid = ShimTestHarness.RunInSimulation(env, ControlledGuid.CreateVersion7);

        Assert.Equal(7, guid.Version);
        AssertRfcVariant(guid);
    }

    [Fact]
    public void CreateVersion7EmbedsTheVirtualUnixMillisecondTimestamp()
    {
        var instant = new DateTimeOffset(2031, 7, 8, 9, 10, 11, 123, TimeSpan.Zero);
        var clock = ShimTestHarness.CreateClock(instant);
        var env = ShimTestHarness.CreateEnvironment(clock);

        var guid = ShimTestHarness.RunInSimulation(env, ControlledGuid.CreateVersion7);

        Assert.Equal(instant.ToUnixTimeMilliseconds(), ExtractVersion7UnixMs(guid));
    }

    [Fact]
    public void CreateVersion7WithExplicitTimestampEmbedsThatTimestamp()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
        var explicitTs = new DateTimeOffset(2040, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

        var guid = ShimTestHarness.RunInSimulation(env, () => ControlledGuid.CreateVersion7(explicitTs));

        Assert.Equal(7, guid.Version);
        Assert.Equal(explicitTs.ToUnixTimeMilliseconds(), ExtractVersion7UnixMs(guid));
    }

    [Fact]
    public void SameSeedAndScheduleReplaysIdenticalGuids()
    {
        Guid Run()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, ControlledGuid.NewGuid);
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void DifferentNodesProduceDifferentGuidStreams()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var nodeA = ShimTestHarness.RunInSimulation(env, ControlledGuid.NewGuid, nodeAddress: "10.0.0.1");
        var nodeB = ShimTestHarness.RunInSimulation(env, ControlledGuid.NewGuid, nodeAddress: "10.0.0.2");

        Assert.NotEqual(nodeA, nodeB);
    }

    [Fact]
    public void RepeatedNewGuidCallsAdvanceTheIdentityStream()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

        var (first, second) = ShimTestHarness.RunInSimulation(env, () =>
            (ControlledGuid.NewGuid(), ControlledGuid.NewGuid()));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GuidGenerationDoesNotPerturbTheApplicationRandomStream()
    {
        // Draw a shared-random value with and without an intervening GUID generation; the identity
        // stream is independent, so the application draw must be unaffected.
        int WithoutGuid()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, () => env.GetSharedRandom(null!).Next());
        }

        int WithGuid()
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                _ = ControlledGuid.NewGuid();
                _ = ControlledGuid.CreateVersion7();
                return env.GetSharedRandom(null!).Next();
            });
        }

        Assert.Equal(WithoutGuid(), WithGuid());
    }

    [Fact]
    public void OutsideSimulationGuidShimsRequireActiveSimulation()
    {
        Assert.False(Orleans.Testing.Simulation.Runtime.Execution.SimulationExecutionContext.IsActive);
        Guid result = default;

        Exception? newGuidException = Record.Exception(() => result = ControlledGuid.NewGuid());
        Assert.Equal(default, result);
        SimulationNotActiveExceptionAssert.Equal(newGuidException, "System.Guid.NewGuid");

        Exception? version7Exception = Record.Exception(() => result = ControlledGuid.CreateVersion7());
        Assert.Equal(default, result);
        SimulationNotActiveExceptionAssert.Equal(version7Exception, "System.Guid.CreateVersion7");
    }

    private static void AssertRfcVariant(Guid guid)
    {
        // RFC 4122 variant: the two most-significant bits of byte 8 (big-endian) are 1 0.
        var bytes = guid.ToByteArray(bigEndian: true);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    private static long ExtractVersion7UnixMs(Guid guid)
    {
        var bytes = guid.ToByteArray(bigEndian: true);
        long ms = 0;
        for (var i = 0; i < 6; i++)
        {
            ms = (ms << 8) | bytes[i];
        }

        return ms;
    }

    public static TheoryData<DateTimeOffset> PreUnixEpochTimestamps =>
    [
        DateTimeOffset.UnixEpoch.AddTicks(-1),
        DateTimeOffset.MinValue,
    ];

    [Theory]
    [MemberData(nameof(PreUnixEpochTimestamps))]
    public void CreateVersion7RejectsPreUnixEpochTimestamp(DateTimeOffset timestamp)
    {
        var bclException = Assert.Throws<ArgumentOutOfRangeException>(
            () => Guid.CreateVersion7(timestamp));
        Assert.Equal("timestamp", bclException.ParamName);

        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
        var controlledException = ShimTestHarness.RunInSimulation(
            env,
            () => Assert.Throws<ArgumentOutOfRangeException>(
                () => ControlledGuid.CreateVersion7(timestamp)));

        Assert.Equal("timestamp", controlledException.ParamName);
    }

    [Fact]
    public void CreateVersion7AtUnixEpochSetsTimestampVersionAndVariantBits()
    {
        var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());
        Guid guid = ShimTestHarness.RunInSimulation(
            env,
            () => ControlledGuid.CreateVersion7(DateTimeOffset.UnixEpoch));
        byte[] bytes = guid.ToByteArray(bigEndian: true);

        Assert.Equal(new byte[6], bytes[..6]);
        Assert.Equal(0x70, bytes[6] & 0xF0);
        Assert.Equal(7, guid.Version);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}
