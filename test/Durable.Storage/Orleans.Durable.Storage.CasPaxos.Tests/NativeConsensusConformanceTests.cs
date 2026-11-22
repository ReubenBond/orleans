using System.Globalization;
using Microsoft.Accordant;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// The MetaStore register the native consensus serves conforms to <see cref="ConditionalRegisterSpec"/> under the
/// transport's faults: seeded sequences of conditional writes and reads, alternating between two proposers, each under a
/// fault of its own (lost accept replies, late replies, a voter partitioned or stopped and restarted, a misrouted voter),
/// form a history which the spec must allow, every call which fails counted as an unknown outcome (MS-I3).
/// </summary>
[Trait("Category", "Integration")]
public sealed class NativeConsensusConformanceTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("caspaxos-conformance-");

    private enum Fault
    {
        None,
        LostAcceptReplies,
        LateReplies,
        OneVoterPartitioned,
        TwoVotersPartitioned,
        VoterRestarted,
        Misrouted,
    }

    [Theory(Timeout = 300_000)]
    [InlineData(17)]
    [InlineData(4242)]
    public async Task FaultedHistoriesConformToTheRegisterSpec(int seed)
    {
        var token = TestContext.Current.CancellationToken;
        var random = new Random(seed);
        var spec = ConditionalRegisterSpec.Create(unknownOutcomes: true, appliedMismatch: false);
        var profile = new StateProfile(new ConditionalRegisterState());
        var trace = new List<string>();
        await using var cluster = await NativeVoterCluster.OpenAsync(
            _directory.FullName, token, attemptTimeout: TimeSpan.FromMilliseconds(150), operationTimeout: TimeSpan.FromMilliseconds(900));
        using var first = new CasPaxosMetaStoreFactory(cluster.Registers[0]);
        using var second = new CasPaxosMetaStoreFactory(cluster.Registers[1]);
        IConditionalRegister[] registers = [await first.GetRegisterAsync(CasPaxosTestFixture.Cluster, "history", token), await second.GetRegisterAsync(CasPaxosTestFixture.Cluster, "history", token)];

        // One caller issues the history through either proposer, knowing every version it learned through both.
        var executor = new ConditionalRegisterExecutor(registers[0]);
        var written = new List<byte>();
        byte next = 1;
        for (var step = 0; step < 24; step++)
        {
            var proposer = random.Next(2);
            var fault = (Fault)random.Next(Enum.GetValues<Fault>().Length);
            object request = random.Next(3) == 0 || next == byte.MaxValue
                ? RegisterRead.Instance
                : new RegisterWrite(written.Count == 0 || random.Next(4) == 0 ? null : written[random.Next(written.Count)], next++);
            if (request is RegisterWrite write)
            {
                written.Add(write.Value);
            }

            executor = executor.Over(registers[proposer]);
            await ApplyAsync(cluster, proposer, fault, token);
            RegisterOutcome outcome;
            try
            {
                outcome = request is RegisterWrite w
                    ? await executor.WriteAsync(w, token)
                    : await executor.ReadAsync(token);
            }
            catch (Exception failure) when (failure is IOException or RegisterUnavailableException or RegisterBusyException)
            {
                outcome = RegisterOutcome.Unknown;
            }

            await HealAsync(cluster, token);
            trace.Add(string.Create(CultureInfo.InvariantCulture, $"#{step} p{proposer} {fault}: {request} -> {outcome}"));
            var name = request is RegisterWrite ? ConditionalRegisterSpec.Write : ConditionalRegisterSpec.Read;
            var (allowed, explanation, after) = spec.Allows(spec.GetOperation(name), request, outcome, profile);
            Assert.True(allowed, $"Seed {seed}: {explanation}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
            profile = after;
        }

        // Once healed, both proposers read one state, which the spec allows after the whole history.
        foreach (var register in registers)
        {
            executor = executor.Over(register);
            var (allowed, explanation, after) = spec.Allows(spec.GetOperation(ConditionalRegisterSpec.Read), RegisterRead.Instance, await executor.ReadAsync(token), profile);
            Assert.True(allowed, $"Seed {seed}: {explanation}{Environment.NewLine}{string.Join(Environment.NewLine, trace)}");
            profile = after;
        }

        Assert.Empty(cluster.FatalErrors.Failures);
    }

    public void Dispose()
    {
        _leaks.Dispose();
        _directory.Delete(recursive: true);
    }

    private static async Task ApplyAsync(NativeVoterCluster cluster, int proposer, Fault fault, CancellationToken cancellationToken)
    {
        switch (fault)
        {
            case Fault.LostAcceptReplies:
                cluster.Each(proposer, static link => link.LoseAcceptReplies = true);
                break;
            case Fault.LateReplies:
                cluster.Links[proposer, 0].ReplyDelay = cluster.AttemptTimeout * 2;
                break;
            case Fault.OneVoterPartitioned:
                cluster.Links[proposer, 1].Partitioned = true;
                break;
            case Fault.TwoVotersPartitioned:
                cluster.Links[proposer, 1].Partitioned = true;
                cluster.Links[proposer, 2].Partitioned = true;
                break;
            case Fault.VoterRestarted:
                await cluster.CloseVoterAsync(2);
                await cluster.OpenVoterAsync(2, cancellationToken);
                break;
            case Fault.Misrouted:
                cluster.Links[proposer, 0].MisrouteTo = () => cluster.Services[1]!;
                break;
            case Fault.None:
            default:
                break;
        }
    }

    private static Task HealAsync(NativeVoterCluster cluster, CancellationToken cancellationToken)
    {
        for (var row = 0; row < cluster.Links.GetLength(0); row++)
        {
            cluster.Each(row, static link => link.Heal());
        }

        // A late reply's delay outlives its call; waiting it out keeps it from overlapping the next call's fault.
        return Task.Delay(cluster.AttemptTimeout * 2, TimeProvider.System, cancellationToken);
    }
}
