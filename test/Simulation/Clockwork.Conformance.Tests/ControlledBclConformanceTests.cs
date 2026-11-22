using System.Reflection;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end semantic conformance for the built-in controlled BCL rule set: ordinary source is
/// rewritten, deterministic under a live simulation, and rejected before real BCL work outside one.
/// See <see cref="RewriteFixture"/> for the rewrite-and-load harness.
/// </summary>
public sealed class ControlledBclConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start =
        new(2024, 6, 15, 12, 30, 45, 123, TimeSpan.Zero);

    private const string ClockSource = """
        using System;
        using System.Diagnostics;
        namespace Conf { public static class ClockProbe {
            public static long UtcNowTicks() => DateTime.UtcNow.Ticks;
            public static long NowTicks() => DateTime.Now.Ticks;
            public static long TodayTicks() => DateTime.Today.Ticks;
            public static long OffsetUtcNowTicks() => DateTimeOffset.UtcNow.UtcTicks;
            public static long OffsetNowTicks() => DateTimeOffset.Now.Ticks;
            public static long Timestamp() => Stopwatch.GetTimestamp();
            public static long ElapsedTicks(long start) => Stopwatch.GetElapsedTime(start).Ticks;
            public static int TickCount() => Environment.TickCount;
            public static long TickCount64() => Environment.TickCount64;
        } }
        """;

    private const string GuidSource = """
        using System;
        namespace Conf { public static class GuidProbe {
            public static Guid New() => Guid.NewGuid();
            public static Guid V7() => Guid.CreateVersion7();
            public static Guid V7At(DateTimeOffset t) => Guid.CreateVersion7(t);
        } }
        """;

    private const string RandomSource = """
        using System;
        namespace Conf { public static class RandomProbe {
            public static int Shared() => Random.Shared.Next();
            public static int Unseeded() { var r = new Random(); return r.Next(); }
            public static int Seeded(int s) { var r = new Random(s); return r.Next(); }
        } }
        """;

    private const string CryptoSource = """
        using System;
        using System.Security.Cryptography;
        namespace Conf { public static class CryptoProbe {
            public static byte[] Bytes(int n) => RandomNumberGenerator.GetBytes(n);
            public static int Int(int max) => RandomNumberGenerator.GetInt32(max);
            public static int[] Items(int n) => RandomNumberGenerator.GetItems<int>([1, 2, 3, 4], n);
            public static int[] Shuffle(int[] values) { RandomNumberGenerator.Shuffle<int>(values); return values; }
        } }
        """;

    private readonly RewriteFixture _fixture = new();

    [Fact]
    public void ClockShimsReturnVirtualTimeUnderSimulation()
    {
        StagedProbe probe = _fixture.Stage("Conf.Clock", "Conf.ClockProbe", ClockSource);

        using var host = new SimulationHost(Start);
        Assert.Equal(Start.UtcDateTime.Ticks, (long)host.Invoke(probe.Method("UtcNowTicks"))!);
        Assert.Equal(Start.UtcDateTime.Ticks, (long)host.Invoke(probe.Method("OffsetUtcNowTicks"))!);
        Assert.Equal(0L, (long)host.Invoke(probe.Method("TickCount64"))!);
        Assert.Equal(0L, (long)host.Invoke(probe.Method("Timestamp"))!);
        Assert.Equal(0L, (long)host.Invoke(probe.Method("ElapsedTicks"), 0L)!);

        // Default UTC simulation zone: local time coincides with UTC, so Now == UtcNow and Today is
        // that same virtual date at midnight.
        Assert.Equal(Start.UtcDateTime.Ticks, (long)host.Invoke(probe.Method("NowTicks"))!);
        Assert.Equal(Start.UtcDateTime.Date.Ticks, (long)host.Invoke(probe.Method("TodayTicks"))!);
    }

    [Fact]
    public void ClockShimsReflectConfiguredTimeZoneForLocalApis()
    {
        StagedProbe probe = _fixture.Stage("Conf.ClockTz", "Conf.ClockProbe", ClockSource, [BuiltInRuleFamily.Clock]);

        var plus5 = TimeZoneInfo.CreateCustomTimeZone("conf+5", TimeSpan.FromHours(5), "conf+5", "conf+5");
        using var host = new SimulationHost(Start, timeZone: plus5);

        long nowTicks = (long)host.Invoke(probe.Method("NowTicks"))!;
        long utcTicks = (long)host.Invoke(probe.Method("UtcNowTicks"))!;

        Assert.Equal(utcTicks + TimeSpan.FromHours(5).Ticks, nowTicks);
    }

    [Fact]
    public void GuidShimsProduceWellFormedDeterministicValues()
    {
        StagedProbe probe = _fixture.Stage("Conf.Guid", "Conf.GuidProbe", GuidSource, [BuiltInRuleFamily.Identity]);

        using var host = new SimulationHost(Start);
        var v4 = (Guid)host.Invoke(probe.Method("New"))!;
        var v7 = (Guid)host.Invoke(probe.Method("V7"))!;
        var v7At = (Guid)host.Invoke(probe.Method("V7At"), Start)!;

        Assert.Equal(4, VersionOf(v4));
        Assert.Equal(2, VariantOf(v4));
        Assert.Equal(7, VersionOf(v7));
        Assert.Equal(2, VariantOf(v7));
        Assert.Equal(7, VersionOf(v7At));
        Assert.Equal(Start.ToUnixTimeMilliseconds(), UnixMsOf(v7At));
        Assert.NotEqual(Guid.Empty, v4);
    }

    [Fact]
    public void SameSeedReplaysGuidsAndRandomAcrossRuns()
    {
        StagedProbe guids = _fixture.Stage("Conf.GuidR", "Conf.GuidProbe", GuidSource, [BuiltInRuleFamily.Identity]);
        StagedProbe random = _fixture.Stage("Conf.RandR", "Conf.RandomProbe", RandomSource, [BuiltInRuleFamily.Random]);

        (Guid Guid, int Rand) First = RunReplay(guids, random);
        (Guid Guid, int Rand) Second = RunReplay(guids, random);

        Assert.Equal(First.Guid, Second.Guid);
        Assert.Equal(First.Rand, Second.Rand);
    }

    private static (Guid, int) RunReplay(StagedProbe guids, StagedProbe random)
    {
        using var host = new SimulationHost(Start, seed: 99);
        var g = (Guid)host.Invoke(guids.Method("New"))!;
        int r = (int)host.Invoke(random.Method("Shared"))!;
        return (g, r);
    }

    [Fact]
    public void SharedRandomIsIsolatedPerNode()
    {
        StagedProbe probe = _fixture.Stage("Conf.RandIso", "Conf.RandomProbe", RandomSource, [BuiltInRuleFamily.Random]);

        using var host = new SimulationHost(Start, nodeAddresses: ["node-a", "node-b"]);
        int a = (int)host.InvokeOnNode("node-a", probe.Method("Shared"))!;
        int b = (int)host.InvokeOnNode("node-b", probe.Method("Shared"))!;

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ExplicitlySeededRandomPreservesBclBehaviour()
    {
        StagedProbe probe = _fixture.Stage("Conf.RandSeed", "Conf.RandomProbe", RandomSource, [BuiltInRuleFamily.Random]);

        using var host = new SimulationHost(Start);
        int shimmed = (int)host.Invoke(probe.Method("Seeded"), 4242)!;

        Assert.Equal(new System.Random(4242).Next(), shimmed);
    }

    [Fact]
    public void OnlyRewrittenBclShimsRequireAnActiveSimulation()
    {
        StagedProbe clock = _fixture.Stage(
            "Conf.RequiresClock",
            "Conf.ClockProbe",
            ClockSource,
            [BuiltInRuleFamily.Clock]);
        StagedProbe guid = _fixture.Stage(
            "Conf.RequiresGuid",
            "Conf.GuidProbe",
            GuidSource,
            [BuiltInRuleFamily.Identity]);
        StagedProbe random = _fixture.Stage(
            "Conf.RequiresRandom",
            "Conf.RandomProbe",
            RandomSource,
            [BuiltInRuleFamily.Random]);
        StagedProbe crypto = _fixture.Stage(
            "Conf.RequiresCrypto",
            "Conf.CryptoProbe",
            CryptoSource,
            [BuiltInRuleFamily.Crypto]);
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedClock",
            "Conf.ClockProbe",
            ClockSource);

        long before = DateTime.UtcNow.Ticks;
        long actual = (long)uninstrumented.Method("UtcNowTicks").Invoke(null, null)!;
        long after = DateTime.UtcNow.Ticks;

        Assert.InRange(actual, before - TimeSpan.TicksPerSecond, after + TimeSpan.TicksPerSecond);
        Assert.Equal(
            "System.DateTime.UtcNow",
            SimulationNotActiveExceptionAssert.Throws(clock.Method("UtcNowTicks")).ApiName);
        SimulationNotActiveExceptionAssert.Throws(clock.Method("OffsetUtcNowTicks"));
        SimulationNotActiveExceptionAssert.Throws(clock.Method("Timestamp"));
        SimulationNotActiveExceptionAssert.Throws(clock.Method("TickCount64"));
        SimulationNotActiveExceptionAssert.Throws(guid.Method("New"));
        SimulationNotActiveExceptionAssert.Throws(random.Method("Shared"));
        SimulationNotActiveExceptionAssert.Throws(random.Method("Unseeded"));
        SimulationNotActiveExceptionAssert.Throws(random.Method("Seeded"), 4242);
        SimulationNotActiveExceptionAssert.Throws(crypto.Method("Bytes"), 16);
    }

    [Fact]
    public void CryptoCallsAreDeterministicUnderSimulation()
    {
        StagedProbe probe = _fixture.Stage("Conf.Crypto", "Conf.CryptoProbe", CryptoSource, [BuiltInRuleFamily.Crypto]);

        (byte[] Bytes, int Value, int[] Items, int[] Shuffled) RunOnce()
        {
            using var host = new SimulationHost(Start, seed: 5);
            int[] values = [1, 2, 3, 4, 5, 6];
            return (
                (byte[])host.Invoke(probe.Method("Bytes"), 16)!,
                (int)host.Invoke(probe.Method("Int"), 100)!,
                (int[])host.Invoke(probe.Method("Items"), 8)!,
                (int[])host.Invoke(probe.Method("Shuffle"), values)!);
        }

        var first = RunOnce();
        var second = RunOnce();

        Assert.Equal(16, first.Bytes.Length);
        Assert.InRange(first.Value, 0, 99);
        Assert.All(first.Items, item => Assert.InRange(item, 1, 4));
        Assert.Equal([1, 2, 3, 4, 5, 6], first.Shuffled.Order());
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(first.Items, second.Items);
        Assert.Equal(first.Shuffled, second.Shuffled);
    }

    [Fact]
    public void RewriteIsIdempotent()
    {
        StagedProbe probe = _fixture.Stage("Conf.Idem", "Conf.ClockProbe", ClockSource, [BuiltInRuleFamily.Clock]);

        // Rewriting the already-rewritten staged assembly with the same rule set is a verified no-op.
        var second = Orleans.Testing.Simulation.Instrumentation.Rewriting.RewriteEngine.Rewrite(
            new Orleans.Testing.Simulation.Instrumentation.Rewriting.RewriteRequest(
                probe.StagedDll, probe.StagedDll, probe.RuleSet, probe.Options));

        Assert.True(second.Succeeded);
        Assert.True(second.WasNoOp);
    }

    [Fact]
    public void EveryClockCallSiteIsRewritten()
    {
        StagedProbe probe = _fixture.Stage("Conf.Count", "Conf.ClockProbe", ClockSource, [BuiltInRuleFamily.Clock]);

        // Nine controlled clock APIs, one call site each.
        Assert.Equal(9, probe.Result.Manifest.Transformations.Length);
    }

    private static int VersionOf(Guid guid) => (guid.ToByteArray(bigEndian: true)[6] >> 4) & 0x0F;

    private static int VariantOf(Guid guid) => (guid.ToByteArray(bigEndian: true)[8] >> 6) & 0x03;

    private static long UnixMsOf(Guid guid)
    {
        byte[] b = guid.ToByteArray(bigEndian: true);
        return ((long)b[0] << 40) | ((long)b[1] << 32) | ((long)b[2] << 24) | ((long)b[3] << 16) | ((long)b[4] << 8) | b[5];
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void CryptoValidationMatchesBclThroughRewrittenCalls()
    {
        StagedProbe probe = _fixture.Stage(
            "Conf.CryptoContracts",
            "Conf.CryptoContractProbe",
            CryptoContractSource,
            [BuiltInRuleFamily.Crypto]);
        using var host = new SimulationHost(Start);

        var byteErrors = new List<Exception?>();
        foreach (int count in new[] { -1, int.MinValue })
        {
            byteErrors.Add(Record.Exception(() => { _ = host.Invoke(probe.Method("Bytes"), count); }));
        }

        var singleBoundErrors = new List<Exception?>();
        foreach (int toExclusive in new[] { 0, -1, int.MinValue })
        {
            singleBoundErrors.Add(Record.Exception(() => { _ = host.Invoke(probe.Method("Int"), toExclusive); }));
        }

        var rangeErrors = new List<Exception?>();
        foreach ((int fromInclusive, int toExclusive) in new[]
                 {
                     (0, 0),
                     (1, 0),
                     (int.MaxValue, int.MinValue),
                 })
        {
            rangeErrors.Add(Record.Exception(
                () => { _ = host.Invoke(probe.Method("Range"), fromInclusive, toExclusive); }));
        }

        Assert.All(
            byteErrors,
            error => AssertArgumentExceptionShape<ArgumentOutOfRangeException>(error, "count"));
        Assert.All(
            singleBoundErrors,
            error => AssertArgumentExceptionShape<ArgumentOutOfRangeException>(error, "toExclusive"));
        Assert.All(
            rangeErrors,
            error => AssertArgumentExceptionShape<ArgumentException>(error, expectedParamName: null));
    }

    [Fact]
    public void NamedCryptoFactoryHonorsKnownAndUnknownNames()
    {
        const string KnownName = "RandomNumberGenerator";
        const string UnknownName =
            "Orleans.Testing.Simulation.Tests.Unknown.RandomNumberGenerator.7f9c7706-3f19-42a8-b8f2-20af41a52d68";
        StagedProbe probe = _fixture.Stage(
            "Conf.NamedCryptoContracts",
            "Conf.CryptoContractProbe",
            CryptoContractSource,
            [BuiltInRuleFamily.Crypto]);

        (string TypeName, byte[] Bytes) DrawKnown()
        {
            using var host = new SimulationHost(Start, seed: 71);
            string typeName = (string)host.Invoke(probe.Method("NamedType"), KnownName)!;
            byte[] bytes = (byte[])host.Invoke(probe.Method("NamedBytes"), KnownName, 24)!;
            return (typeName, bytes);
        }

        (string TypeName, byte[] Bytes) first = DrawKnown();
        (string TypeName, byte[] Bytes) replay = DrawKnown();

        Assert.Equal(typeof(SimulationRandomNumberGenerator).FullName, first.TypeName);
        Assert.Equal(24, first.Bytes.Length);
        Assert.Equal(first.TypeName, replay.TypeName);
        Assert.Equal(first.Bytes, replay.Bytes);
        Assert.Contains(first.Bytes, static value => value != 0);

        using var unknownHost = new SimulationHost(Start);
        Assert.Null(unknownHost.Invoke(probe.Method("NamedType"), UnknownName));
    }

    [Fact]
    public void GuidVersion7RejectsPreEpochThroughRewrittenCall()
    {
        StagedProbe probe = _fixture.Stage(
            "Conf.GuidPreEpoch",
            "Conf.GuidProbe",
            GuidSource,
            [BuiltInRuleFamily.Identity]);
        using var host = new SimulationHost(Start);
        DateTimeOffset[] timestamps =
        [
            DateTimeOffset.UnixEpoch.AddTicks(-1),
            DateTimeOffset.MinValue,
        ];
        Exception?[] errors = timestamps
            .Select(timestamp => Record.Exception(
                () => { _ = host.Invoke(probe.Method("V7At"), timestamp); }))
            .ToArray();

        Assert.All(
            errors,
            error => AssertArgumentExceptionShape<ArgumentOutOfRangeException>(error, "timestamp"));
    }

    [Fact]
    public void GuidVersion7UnixEpochBitsAreWellFormed()
    {
        StagedProbe probe = _fixture.Stage(
            "Conf.GuidUnixEpoch",
            "Conf.GuidProbe",
            GuidSource,
            [BuiltInRuleFamily.Identity]);
        using var host = new SimulationHost(Start);

        var guid = (Guid)host.Invoke(probe.Method("V7At"), DateTimeOffset.UnixEpoch)!;
        byte[] bytes = guid.ToByteArray(bigEndian: true);

        Assert.Equal(new byte[6], bytes[..6]);
        Assert.Equal(0x70, bytes[6] & 0xF0);
        Assert.Equal(7, guid.Version);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    private static void AssertArgumentExceptionShape<TException>(
        Exception? error,
        string? expectedParamName)
        where TException : ArgumentException
    {
        var exception = Assert.IsType<TException>(error);
        Assert.Equal(expectedParamName, exception.ParamName);
    }

    private const string CryptoContractSource = """
        using System;
        using System.Security.Cryptography;
        namespace Conf { public static class CryptoContractProbe {
            public static byte[] Bytes(int n) => RandomNumberGenerator.GetBytes(n);
            public static int Int(int max) => RandomNumberGenerator.GetInt32(max);
            public static int Range(int min, int max) => RandomNumberGenerator.GetInt32(min, max);

        #pragma warning disable SYSLIB0045
            public static string NamedType(string name) {
                using var rng = RandomNumberGenerator.Create(name);
                return rng?.GetType().FullName;
            }
            public static byte[] NamedBytes(string name, int count) {
                using var rng = RandomNumberGenerator.Create(name);
                if (rng is null) return null;
                var bytes = new byte[count];
                rng.GetBytes(bytes);
                return bytes;
            }
        #pragma warning restore SYSLIB0045
        } }
        """;
}
