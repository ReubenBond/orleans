using System.Text;
using Orleans.Consensus.CasPaxos.Messages;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.CasPaxos.Hosting;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Contracts = Orleans.Durable.Storage.Testing.ConditionalRegisterContract;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// The native consensus's voter transport, identity, and admission (caspaxos.md): proposers' <see cref="RemoteVoter"/>s
/// over each voter's <see cref="CasPaxosVoterService"/> and real SQLite, through links which inject the transport's
/// faults. The MetaStore's register contract is the oracle for the registers the native consensus serves; the register
/// service's own protocol faults are the consensus simulation's to qualify, and real Orleans RPC is durable storage's
/// <c>OrleansNativeConsensusTests</c>'.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NativeConsensusTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("caspaxos-native-");

    public static TheoryData<string> ContractNames =>
    [
        nameof(Contracts.EmptyStoreReturnsNull),
        nameof(Contracts.NullVersionCreatesOnlyWhenAbsent),
        nameof(Contracts.EmptyValueReadsAsPresent),
        nameof(Contracts.NonNullVersionDoesNotCreateMissingRegister),
        nameof(Contracts.MatchingVersionUpdatesAndChangesVersion),
        nameof(Contracts.AnAppliedWriteReturnsTheRegistersNewVersion),
        nameof(Contracts.StaleVersionDoesNotOverwriteValue),
        nameof(Contracts.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues),
        nameof(Contracts.ConcurrentCreatesHaveOneWinner),
        nameof(Contracts.ConcurrentWritesWithOneVersionHaveOneWinner),
        nameof(Contracts.CancellationIsObserved),
    ];

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(ContractNames))]
    public async Task TheRegisterContractHoldsOverRemoteVoters(string contract)
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        using var factory = new CasPaxosMetaStoreFactory(cluster.Registers[0]);
        var register = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "contracts", TestContext.Current.CancellationToken);
        await (contract switch
        {
            nameof(Contracts.EmptyStoreReturnsNull) => Contracts.EmptyStoreReturnsNull(register),
            nameof(Contracts.NullVersionCreatesOnlyWhenAbsent) => Contracts.NullVersionCreatesOnlyWhenAbsent(register),
            nameof(Contracts.EmptyValueReadsAsPresent) => Contracts.EmptyValueReadsAsPresent(register),
            nameof(Contracts.NonNullVersionDoesNotCreateMissingRegister) => Contracts.NonNullVersionDoesNotCreateMissingRegister(register),
            nameof(Contracts.MatchingVersionUpdatesAndChangesVersion) => Contracts.MatchingVersionUpdatesAndChangesVersion(register),
            nameof(Contracts.AnAppliedWriteReturnsTheRegistersNewVersion) => Contracts.AnAppliedWriteReturnsTheRegistersNewVersion(register),
            nameof(Contracts.StaleVersionDoesNotOverwriteValue) => Contracts.StaleVersionDoesNotOverwriteValue(register),
            nameof(Contracts.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues) => Contracts.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(register),
            nameof(Contracts.ConcurrentCreatesHaveOneWinner) => Contracts.ConcurrentCreatesHaveOneWinner(register),
            nameof(Contracts.ConcurrentWritesWithOneVersionHaveOneWinner) => Contracts.ConcurrentWritesWithOneVersionHaveOneWinner(register),
            nameof(Contracts.CancellationIsObserved) => Contracts.CancellationIsObserved(register),
            _ => throw new ArgumentOutOfRangeException(nameof(contract)),
        });
    }

    /// <summary>
    /// A fresh cluster's voters admit one another; reopening the same databases restores each voter's identity and
    /// incarnation, already admitted, with its committed promises and accepts, and the proposers' ballot reservations.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task VotersKeepTheirIdentityPromisesAcceptsAndReservationsAcrossRestart()
    {
        Guid[] incarnations;
        LocalAcceptorState?[] before;
        RegisterValue written;
        await using (var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken))
        {
            Assert.All(cluster.Voters, voter => Assert.True(voter!.IsServing));
            incarnations = [.. cluster.Voters.Select(static voter => voter!.Incarnation)];
            var applied = await cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("first"), TestContext.Current.CancellationToken);
            Assert.Equal(CompareExchangeStatus.Applied, applied.Status);
            written = applied.Current!;
            before = await Task.WhenAll(cluster.Voters.Select(voter => voter!.Inspection.InspectAsync("key", TestContext.Current.CancellationToken).AsTask()));
            Assert.True(before.Count(static state => state?.AcceptedValue is not null) >= 2);
        }

        await using (var reopened = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken, admit: false))
        {
            // Restarting at the same identity needs no admission: each database still holds its admitted binding.
            Assert.All(reopened.Voters, voter => Assert.True(voter!.IsServing));
            Assert.Equal(incarnations, reopened.Voters.Select(static voter => voter!.Incarnation));
            var after = await Task.WhenAll(reopened.Voters.Select(voter => voter!.Inspection.InspectAsync("key", TestContext.Current.CancellationToken).AsTask()));
            Assert.Equal(before, after);

            var read = await reopened.Registers[1].ReadAsync("key", TestContext.Current.CancellationToken);
            Assert.Equal(written.Version, read.Version);
            Assert.Equal(Bytes("first"), read.Value);
            var next = await reopened.Registers[1].CompareExchangeAsync("key", read.Version, Bytes("second"), TestContext.Current.CancellationToken);
            Assert.Equal(CompareExchangeStatus.Applied, next.Status);
            Assert.NotEqual(read.Version, next.Current!.Version);
            Assert.Empty(reopened.FatalErrors.Failures);
        }

        // The proposers' reservations are durable: the allocator reopened under its identity reserves above every round
        // the cluster reserved, and refuses another identity.
        var proposer = Path.Combine(_directory.FullName, "proposer-0.db");
        var failures = new Consensus.CasPaxos.Testing.RecordingFatalErrorHandler();
        Ballot first;
        using (var ballots = new Consensus.CasPaxos.Sqlite.SqliteProposerBallotSource(proposer, 1, failures))
        {
            first = ballots.Reserve(1);
            Assert.True(first.Round > 1, "The cluster's proposer reserved no round.");
        }

        using (var ballots = new Consensus.CasPaxos.Sqlite.SqliteProposerBallotSource(proposer, 1, failures))
        {
            Assert.True(ballots.Reserve(1).Round > first.Round);
        }

        _ = Assert.Throws<InvalidOperationException>(() => new Consensus.CasPaxos.Sqlite.SqliteProposerBallotSource(proposer, 2, failures));
    }

    /// <summary>A database serves the one voter it is bound to: opened as another voter, it refuses before it answers.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ADatabaseRefusesToServeAnotherVoter()
    {
        await using (var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken))
        {
            Assert.Equal(CompareExchangeStatus.Applied, (await cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("v"), TestContext.Current.CancellationToken)).Status);
        }

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => LocalVoter.OpenAsync(
            "b", NativeVoterCluster.DatabaseOf(_directory.FullName, 0), 16, 4, new Consensus.CasPaxos.Testing.RecordingFatalErrorHandler(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("bound to voter 'a'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A voter whose database was lost starts on a fresh one under its old identity; the other voters, which admitted its
    /// first incarnation, refuse the new one, so it never answers with forgotten promises.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AVoterWhoseStateWasLostIsRefused()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        Assert.Equal(CompareExchangeStatus.Applied, (await cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("v"), TestContext.Current.CancellationToken)).Status);
        var lost = cluster.Voters[2]!.Incarnation;
        await cluster.CloseVoterAsync(2);
        foreach (var file in Directory.EnumerateFiles(_directory.FullName, "voter-c.db*"))
        {
            File.Delete(file);
        }

        await cluster.OpenVoterAsync(2, TestContext.Current.CancellationToken);
        Assert.NotEqual(lost, cluster.Voters[2]!.Incarnation);
        Assert.False(cluster.Voters[2]!.IsServing);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => cluster.AdmitAsync(2, TestContext.Current.CancellationToken));
        Assert.Contains("state was lost", refusal.Message, StringComparison.Ordinal);
        Assert.False(cluster.Voters[2]!.IsServing);
        await Assert.ThrowsAsync<CasPaxosVoterUnavailableException>(() => cluster.Voters[2]!.ReadAsync("key", cancellationToken: TestContext.Current.CancellationToken).AsTask());

        // The remaining majority still serves the register.
        var read = await cluster.Registers[1].ReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(Bytes("v"), read.Value);
    }

    /// <summary>A fresh voter answers nothing until a majority of the other voters admit it, and serves once they do.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AFreshVoterWaitsForAMajorityOfTheOtherVoters()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken, admit: false);
        var admission = cluster.Links.GetLength(0) - 1;
        cluster.Links[admission, 2].Partitioned = true;
        var admitting = cluster.AdmitAsync(0, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(admitting.IsCompleted);
        Assert.False(cluster.Voters[0]!.IsServing);
        await Assert.ThrowsAsync<CasPaxosVoterUnavailableException>(() => cluster.Voters[0]!.PrepareAsync("key", new PrepareRequest(new Ballot(1, 1)), TestContext.Current.CancellationToken).AsTask());

        cluster.Links[admission, 2].Heal();
        await admitting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(cluster.Voters[0]!.IsServing);
    }

    /// <summary>
    /// A request addressed to one voter which reaches another changes nothing there; a reply which names another voter
    /// than the one addressed does not count toward a quorum.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OnlyTheAddressedVotersRepliesCount()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        var misrouted = cluster.Links[0, 1];
        misrouted.MisrouteTo = () => cluster.Services[2]!;
        await Assert.ThrowsAsync<CasPaxosVoterUnavailableException>(() => cluster.Remote(0, 1).PrepareAsync("misrouted", new PrepareRequest(new Ballot(5, 1)), TestContext.Current.CancellationToken).AsTask());
        Assert.Null(await cluster.Voters[2]!.Inspection.InspectAsync("misrouted", TestContext.Current.CancellationToken));

        // With voter c partitioned and voter b's replies forged as c's, only voter a's replies count: no quorum.
        misrouted.Heal();
        cluster.Links[0, 1].ForgedVoter = "c";
        cluster.Links[0, 2].Partitioned = true;
        var forged = await TryCompareExchangeAsync(cluster.Registers[0], "forged", Bytes("x"));
        Assert.True(forged is null or { Status: CompareExchangeStatus.Unknown }, $"A write without a quorum of genuine replies applied: {forged?.Status}");

        cluster.Each(0, static link => link.Heal());
        var read = await cluster.Registers[1].ReadAsync("forged", TestContext.Current.CancellationToken);
        var after = await cluster.Registers[0].CompareExchangeAsync("forged", read.Version, Bytes("y"), TestContext.Current.CancellationToken);
        Assert.Equal(CompareExchangeStatus.Applied, after.Status);
    }

    /// <summary>Losing one voter leaves a quorum: reads and writes continue.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ReadsAndWritesContinueWithOneVoterLost()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        var created = await cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("1"), TestContext.Current.CancellationToken);
        await cluster.CloseVoterAsync(2);

        var updated = await cluster.Registers[1].CompareExchangeAsync("key", created.Current!.Version, Bytes("2"), TestContext.Current.CancellationToken);
        Assert.Equal(CompareExchangeStatus.Applied, updated.Status);
        Assert.Equal(Bytes("2"), (await cluster.Registers[0].ReadAsync("key", TestContext.Current.CancellationToken)).Value);
    }

    /// <summary>
    /// A proposer partitioned from a majority of the voters establishes nothing, while the majority side's proposer keeps
    /// writing; once healed, both proposers observe one register state.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task APartitionedProposerEstablishesNothingAndTheMajoritySideWrites()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        var created = await cluster.Registers[1].CompareExchangeAsync("key", null, Bytes("1"), TestContext.Current.CancellationToken);
        cluster.Links[0, 1].Partitioned = true;
        cluster.Links[0, 2].Partitioned = true;

        var minority = await TryCompareExchangeAsync(cluster.Registers[0], "key", Bytes("minority"), created.Current!.Version);
        Assert.True(minority is null or { Status: CompareExchangeStatus.Unknown }, $"A minority applied a write: {minority?.Status}");
        var majority = await cluster.Registers[1].CompareExchangeAsync("key", created.Current!.Version, Bytes("majority"), TestContext.Current.CancellationToken);

        cluster.Each(0, static link => link.Heal());
        var left = await cluster.Registers[0].ReadAsync("key", TestContext.Current.CancellationToken);
        var right = await cluster.Registers[1].ReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(left.Version, right.Version);
        Assert.Equal(left.Value, right.Value);
        Assert.True(left.Value == Bytes("majority") || left.Value == Bytes("minority"));
        Assert.True(majority.Status == CompareExchangeStatus.Applied || left.Value == Bytes("minority"));
    }

    /// <summary>A reply which arrives after its phase ended is ignored: the operation completes on the timely replies.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ALateReplyDoesNotEnterTheOperation()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        cluster.Links[0, 2].ReplyDelay = cluster.AttemptTimeout * 3;
        var created = await cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("1"), TestContext.Current.CancellationToken);
        Assert.Equal(CompareExchangeStatus.Applied, created.Status);
        var updated = await cluster.Registers[0].CompareExchangeAsync("key", created.Current!.Version, Bytes("2"), TestContext.Current.CancellationToken);
        Assert.Equal(CompareExchangeStatus.Applied, updated.Status);

        await Task.Delay(cluster.AttemptTimeout * 4, TimeProvider.System, TestContext.Current.CancellationToken);
        var read = await cluster.Registers[1].ReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(updated.Current!.Version, read.Version);
        Assert.Equal(Bytes("2"), read.Value);
    }

    /// <summary>
    /// A write whose accepts reached the voters but whose replies were lost has an unknown outcome, which the MetaStore
    /// reports as such; a re-read decides it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ALostReplyLeavesTheOutcomeUnknownAndAReReadDecidesIt()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        using var factory = new CasPaxosMetaStoreFactory(cluster.Registers[0]);
        var register = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "unknown", TestContext.Current.CancellationToken);
        cluster.Each(0, static link => link.LoseAcceptReplies = true);

        _ = await Assert.ThrowsAsync<IOException>(() => register.WriteAsync(null, "written"u8.ToArray(), TestContext.Current.CancellationToken).AsTask());

        cluster.Each(0, static link => link.Heal());
        using var otherFactory = new CasPaxosMetaStoreFactory(cluster.Registers[1]);
        var other = await otherFactory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "unknown", TestContext.Current.CancellationToken);
        using var entry = Assert.IsType<MetaStoreEntry>(await other.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("written"u8.ToArray(), entry.Value.ToArray());
    }

    /// <summary>
    /// A cancelled call dispatches nothing; one cancelled while its requests are held fails or reports an unknown
    /// outcome, and a re-read decides the register either way.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CancellationKeepsItsMeaning()
    {
        await using var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("x"), cancelled.Token).AsTask());
        }

        Assert.All(Enumerable.Range(0, 3), voter => Assert.Equal(0, cluster.Links[0, voter].Delivered));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.Each(0, link => link.Hold = hold.Task);
        using var cancellation = new CancellationTokenSource();
        var writing = cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("held"), cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        try
        {
            var outcome = await writing;
            Assert.Equal(CompareExchangeStatus.Unknown, outcome.Status);
        }
        catch (OperationCanceledException)
        {
        }

        hold.SetResult();
        cluster.Each(0, static link => link.Heal());
        var read = await cluster.Registers[1].ReadAsync("key", TestContext.Current.CancellationToken);
        Assert.True(read.Value is null || read.Value == Bytes("held"));
        Assert.Equal(read.Version, (await cluster.Registers[0].ReadAsync("key", TestContext.Current.CancellationToken)).Version);
    }

    /// <summary>
    /// Shutdown drains the register service's operations, including one whose requests are held, and a stopped voter
    /// answers nothing more.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ShutdownDrainsOperationsAndAStoppedVoterAnswersNothing()
    {
        var cluster = await NativeVoterCluster.OpenAsync(_directory.FullName, TestContext.Current.CancellationToken);
        try
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cluster.Each(0, link => link.Hold = hold.Task);
            var writing = cluster.Registers[0].CompareExchangeAsync("key", null, Bytes("v"), TestContext.Current.CancellationToken).AsTask();
            var stopping = cluster.Registers[0].DisposeAsync().AsTask();
            hold.SetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(writing.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => cluster.Registers[0].ReadAsync("key", TestContext.Current.CancellationToken).AsTask());

            var voter = cluster.Voters[0]!;
            await cluster.CloseVoterAsync(0);
            await Assert.ThrowsAsync<CasPaxosVoterUnavailableException>(() => voter.ReadAsync("key", cancellationToken: TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _leaks.Dispose();
        _directory.Delete(recursive: true);
    }

    private static ByteString Bytes(string value) => new(Encoding.UTF8.GetBytes(value));

    private static async Task<CompareExchangeResult?> TryCompareExchangeAsync(IRegisterService register, string key, ByteString value, ByteString? expected = null)
    {
        try
        {
            return await register.CompareExchangeAsync(key, expected, value, TestContext.Current.CancellationToken);
        }
        catch (RegisterUnavailableException)
        {
            return null;
        }
    }
}
