using System.Security.Cryptography;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Runtime.Tests.Shims;

/// <summary>
/// Conformance tests for <see cref="ControlledRandomNumberGenerator"/>: deterministic static and
/// instance behavior, inactive-simulation rejection, replay, validation, and seed-domain isolation.
/// </summary>
public sealed class ControlledRandomNumberGeneratorTests
{
    private static ShimTestHarness.TestEnvironment Environment() =>
        ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock());

    [Fact]
    public void EveryControlledStaticIsDeterministic()
    {
        static string[] Observe()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                using RandomNumberGenerator instance = ControlledRandomNumberGenerator.Create();
                var instanceArray = new byte[8];
                instance.GetBytes(instanceArray);
                Span<byte> instanceSpan = stackalloc byte[8];
                instance.GetBytes(instanceSpan);

                Span<byte> filled = stackalloc byte[8];
                ControlledRandomNumberGenerator.Fill(filled);
                Span<char> hex = stackalloc char[8];
                ControlledRandomNumberGenerator.GetHexString(hex, lowercase: true);
                ReadOnlySpan<int> choices = [10, 20, 30, 40];
                Span<int> selected = stackalloc int[6];
                ControlledRandomNumberGenerator.GetItems(choices, selected);
                int[] selectedArray = ControlledRandomNumberGenerator.GetItems(choices, 6);
                int[] shuffled = [1, 2, 3, 4, 5, 6];
                ControlledRandomNumberGenerator.Shuffle(shuffled);

                return new string[]
                {
                    Convert.ToHexString(instanceArray),
                    Convert.ToHexString(instanceSpan),
                    Convert.ToHexString(filled),
                    Convert.ToHexString(ControlledRandomNumberGenerator.GetBytes(8)),
                    ControlledRandomNumberGenerator.GetInt32(100).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ControlledRandomNumberGenerator.GetInt32(-100, 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new string(hex),
                    ControlledRandomNumberGenerator.GetHexString(8),
                    ControlledRandomNumberGenerator.GetString("abcdef", 8),
                    string.Join(",", selected.ToArray()),
                    string.Join(",", selectedArray),
                    string.Join(",", shuffled),
                };
            });
        }

        Assert.Equal(Observe(), Observe());
    }

    [Fact]
    public void OutsideSimulationCryptoShimsRequireActiveSimulation()
    {
        Assert.False(Orleans.Testing.Simulation.Runtime.Execution.SimulationExecutionContext.IsActive);
        RandomNumberGenerator? generator = null;

        Exception? createException = Record.Exception(
            () => generator = ControlledRandomNumberGenerator.Create());
        Assert.Null(generator);
        SimulationNotActiveExceptionAssert.Equal(
            createException,
            "System.Security.Cryptography.RandomNumberGenerator.Create");

        byte[]? bytes = null;
        Exception? bytesException = Record.Exception(
            () => bytes = ControlledRandomNumberGenerator.GetBytes(16));
        Assert.Null(bytes);
        SimulationNotActiveExceptionAssert.Equal(
            bytesException,
            "System.Security.Cryptography.RandomNumberGenerator.GetBytes");

        Exception? invalidCountException = Record.Exception(
            () => ControlledRandomNumberGenerator.GetBytes(-1));
        SimulationNotActiveExceptionAssert.Equal(
            invalidCountException,
            "System.Security.Cryptography.RandomNumberGenerator.GetBytes");

        var randomInt = -1;
        Exception? intException = Record.Exception(
            () => randomInt = ControlledRandomNumberGenerator.GetInt32(1, 10));
        Assert.Equal(-1, randomInt);
        SimulationNotActiveExceptionAssert.Equal(
            intException,
            "System.Security.Cryptography.RandomNumberGenerator.GetInt32");

        string? hex = null;
        Exception? hexException = Record.Exception(
            () => hex = ControlledRandomNumberGenerator.GetHexString(8));
        Assert.Null(hex);
        SimulationNotActiveExceptionAssert.Equal(
            hexException,
            "System.Security.Cryptography.RandomNumberGenerator.GetHexString");
    }

    [Fact]
    public void ProducesDeterministicBytesThatReplay()
    {
        byte[] Draw()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () => ControlledRandomNumberGenerator.GetBytes(32));
        }

        Assert.Equal(Draw(), Draw());
    }

    [Fact]
    public void GetInt32StaysInRangeAndReplays()
    {
        int Draw()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () => ControlledRandomNumberGenerator.GetInt32(10, 20));
        }

        var value = Draw();
        Assert.InRange(value, 10, 19);
        Assert.Equal(value, Draw());
    }

    [Fact]
    public void HexStringIsDeterministicAndWellFormed()
    {
        string Draw()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () => ControlledRandomNumberGenerator.GetHexString(16, lowercase: true));
        }

        var hex = Draw();
        Assert.Equal(16, hex.Length);
        Assert.All(hex, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.Equal(hex, Draw());
    }

    [Fact]
    public void GetStringDrawsOnlyFromChoices()
    {
        const string Choices = "XYZ";

        string Draw()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () => ControlledRandomNumberGenerator.GetString(Choices, 12));
        }

        var s = Draw();
        Assert.Equal(12, s.Length);
        Assert.All(s, c => Assert.Contains(c, Choices));
        Assert.Equal(s, Draw());
    }

    [Fact]
    public void CreateReturnsADeterministicInstanceGenerator()
    {
        byte[] Draw()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                using var rng = ControlledRandomNumberGenerator.Create();
                var buffer = new byte[24];
                rng.GetBytes(buffer);
                return buffer;
            });
        }

        Assert.Equal(Draw(), Draw());
    }

    [Fact]
    public void InstanceGeneratorArrayAndSpanGetNonZeroBytesHaveNoZeros()
    {
        var env = Environment();

        var bytes = ShimTestHarness.RunInSimulation(env, () =>
        {
            using var rng = ControlledRandomNumberGenerator.Create();
            var buffer = new byte[128];
            rng.GetNonZeroBytes(buffer);
            rng.GetNonZeroBytes(buffer.AsSpan(64));
            return buffer;
        });

        Assert.DoesNotContain((byte)0, bytes);
    }

    [Fact]
    public void CryptoStreamReplaysPerSeedAndIsIsolatedPerNode()
    {
        static byte[] Draw(int seed, string node)
        {
            var env = ShimTestHarness.CreateEnvironment(ShimTestHarness.CreateClock(), simulationSeed: seed);
            return ShimTestHarness.RunInSimulation(
                env,
                () => ControlledRandomNumberGenerator.GetBytes(32),
                nodeAddress: node);
        }

        byte[] baseline = Draw(17, "node-a");
        Assert.Equal(baseline, Draw(17, "node-a"));
        Assert.False(baseline.SequenceEqual(Draw(17, "node-b")));
        Assert.False(baseline.SequenceEqual(Draw(18, "node-a")));
    }

    [Fact]
    public void CryptoConsumptionDoesNotPerturbApplicationRandomOrIdentityStreams()
    {
        (int Random, Guid Identity) WithoutCrypto()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () =>
                (ControlledRandom.GetShared().Next(), ControlledGuid.NewGuid()));
        }

        (int Random, Guid Identity) WithCrypto()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                _ = ControlledRandomNumberGenerator.GetBytes(16);
                return (ControlledRandom.GetShared().Next(), ControlledGuid.NewGuid());
            });
        }

        Assert.Equal(WithoutCrypto(), WithCrypto());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void GetBytesNegativeCountMatchesBclExceptionShape(int count)
    {
        Exception? bclError = Record.Exception(() => _ = RandomNumberGenerator.GetBytes(count));
        var env = Environment();
        Exception? controlledError = ShimTestHarness.RunInSimulation(
            env,
            () => Record.Exception(() => _ = ControlledRandomNumberGenerator.GetBytes(count)));

        AssertExceptionShape<ArgumentOutOfRangeException>(bclError, controlledError, "count");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void GetInt32SingleBoundRejectsNonPositiveValuesLikeBcl(int toExclusive)
    {
        Exception? bclError = Record.Exception(() => _ = RandomNumberGenerator.GetInt32(toExclusive));
        var env = Environment();
        Exception? controlledError = ShimTestHarness.RunInSimulation(
            env,
            () => Record.Exception(() => _ = ControlledRandomNumberGenerator.GetInt32(toExclusive)));

        AssertExceptionShape<ArgumentOutOfRangeException>(bclError, controlledError, "toExclusive");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void GetInt32RangeRejectsEqualOrDescendingBoundsLikeBcl(int fromInclusive, int toExclusive)
    {
        Exception? bclError = Record.Exception(
            () => _ = RandomNumberGenerator.GetInt32(fromInclusive, toExclusive));
        var env = Environment();
        Exception? controlledError = ShimTestHarness.RunInSimulation(
            env,
            () => Record.Exception(
                () => _ = ControlledRandomNumberGenerator.GetInt32(fromInclusive, toExclusive)));

        AssertExceptionShape<ArgumentException>(bclError, controlledError, expectedParamName: null);
    }

    [Fact]
    public void NamedFactoryReturnsDeterministicGeneratorForKnownBclName()
    {
#pragma warning disable SYSLIB0045 // Verify the obsolete named BCL factory contract mirrored by the shim.
        using RandomNumberGenerator? bclGenerator = RandomNumberGenerator.Create("RandomNumberGenerator");
#pragma warning restore SYSLIB0045
        Assert.NotNull(bclGenerator);

        byte[] Draw()
        {
            var env = Environment();
            return ShimTestHarness.RunInSimulation(env, () =>
            {
                using RandomNumberGenerator? generator =
                    ControlledRandomNumberGenerator.Create("RandomNumberGenerator");
                var deterministic = Assert.IsType<SimulationRandomNumberGenerator>(generator);
                var bytes = new byte[24];
                deterministic.GetBytes(bytes);
                return bytes;
            });
        }

        byte[] first = Draw();
        byte[] replay = Draw();

        Assert.Equal(24, first.Length);
        Assert.Equal(first, replay);
        Assert.Contains(first, static value => value != 0);
    }

    [Fact]
    public void NamedFactoryReturnsNullForUnknownBclName()
    {
        const string UnknownName =
            "Orleans.Testing.Simulation.Tests.Unknown.RandomNumberGenerator.7f9c7706-3f19-42a8-b8f2-20af41a52d68";

#pragma warning disable SYSLIB0045 // Verify the obsolete named BCL factory contract mirrored by the shim.
        using RandomNumberGenerator? bclGenerator = RandomNumberGenerator.Create(UnknownName);
#pragma warning restore SYSLIB0045
        Assert.Null(bclGenerator);

        var env = Environment();
        using RandomNumberGenerator? controlledGenerator = ShimTestHarness.RunInSimulation(
            env,
            () => ControlledRandomNumberGenerator.Create(UnknownName));

        Assert.Null(controlledGenerator);
    }

    private static void AssertExceptionShape<TException>(
        Exception? bclError,
        Exception? controlledError,
        string? expectedParamName)
        where TException : ArgumentException
    {
        var bclException = Assert.IsType<TException>(bclError);
        Assert.Equal(expectedParamName, bclException.ParamName);

        var controlledException = Assert.IsType<TException>(controlledError);
        Assert.Equal(expectedParamName, controlledException.ParamName);
    }

    [Fact]
    public void GetBytesZeroCountReturnsEmptyWithoutConsumingCryptoState()
    {
        var withBoundaryEnvironment = Environment();
        (byte[] Empty, byte[] Following) observation = ShimTestHarness.RunInSimulation(
            withBoundaryEnvironment,
            () => (
                ControlledRandomNumberGenerator.GetBytes(0),
                ControlledRandomNumberGenerator.GetBytes(24)));

        var baselineEnvironment = Environment();
        byte[] baseline = ShimTestHarness.RunInSimulation(
            baselineEnvironment,
            () => ControlledRandomNumberGenerator.GetBytes(24));

        Assert.Empty(observation.Empty);
        Assert.Equal(baseline, observation.Following);
        Assert.Contains(observation.Following, static value => value != 0);
    }
}
