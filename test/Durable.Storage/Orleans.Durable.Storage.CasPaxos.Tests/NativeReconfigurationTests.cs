using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Consensus.CasPaxos.Messages;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Consensus.CasPaxos.Sqlite;
using Orleans.Consensus.CasPaxos.Testing;
using Orleans.Durable.Storage.CasPaxos.Hosting;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// Operator-driven voter reconfiguration over the Orleans transport boundary (<see cref="CasPaxosVoterService"/>,
/// <see cref="RemoteVoter"/>, <see cref="VoterAdmission"/>, <see cref="BallotConverter"/>) and real SQLite voters:
/// replace-1 (including lost voter), add-1, remove-1, crash/resume at every step, racing operators, stale-configuration
/// refusal and recovery, retired-identity refusal, fenced ballot-free reads/listing, and bootstrap digest validation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NativeReconfigurationTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("caspaxos-reconfig-");

    [Fact]
    public void BallotSurrogateRoundTripsConfigurationRoundAndProposer()
    {
        var converter = new BallotConverter();
        Ballot[] samples =
        [
            Ballot.Zero,
            Ballot.Floor(1),
            Ballot.Floor(7),
            Ballot.InitialFast(),
            Ballot.InitialClassic(1, 3),
            new Ballot(4, 19, 0),
            new Ballot(42, 123, 9),
        ];

        foreach (var expected in samples)
        {
            var surrogate = converter.ConvertToSurrogate(in expected);
            var actual = converter.ConvertFromSurrogate(in surrogate);
            Assert.Equal(expected, actual);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task SetFloorFencesProtocolWritesWhileKeepingBallotFreeReadAndListKeysAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await ReconfigRig.OpenAsync(_directory.FullName, ["a", "b", "c"], ct);
        var proposer = rig.CreateProposer(1, ["a", "b", "c"], enableRefresh: false);

        var created = await proposer.CompareExchangeAsync("held", null, Bytes("v1"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, created.Status);

        var remoteA = rig.Remote("a");
        Assert.Equal(Ballot.Floor(2), await remoteA.SetFloorAsync(Ballot.Floor(2), ct));
        Assert.Equal(Ballot.Floor(2), await remoteA.SetFloorAsync(Ballot.Floor(1), ct));

        var rejectedHeld = await remoteA.PrepareAsync("held", new PrepareRequest(new Ballot(1, 99, 1)), ct);
        Assert.False(rejectedHeld.IsPromised);
        Assert.Equal(Ballot.Floor(2), rejectedHeld.ConflictingBallot);

        var rejectedUnseen = await remoteA.PrepareAsync("unseen", new PrepareRequest(Ballot.InitialClassic(1, 1)), ct);
        Assert.False(rejectedUnseen.IsPromised);
        Assert.Equal(Ballot.Floor(2), rejectedUnseen.ConflictingBallot);

        // Ballot-free Read and ListKeys remain available on a fenced voter and report effPromised >= floor.
        var readHeld = await remoteA.ReadAsync("held", cancellationToken: ct);
        Assert.True(readHeld is KeyedAcceptorState heldState && heldState.PromisedBallot == Ballot.Floor(2));

        var readUnseen = await remoteA.ReadAsync("unseen", cancellationToken: ct);
        Assert.True(readUnseen is AcceptorMissing missingState && missingState.PromisedBallot == Ballot.Floor(2));

        var listed = await remoteA.ListKeysAsync(cancellationToken: ct);
        Assert.Equal(Ballot.Floor(2), listed.FloorBallot);
        var listedEntry = Assert.Single(listed.Entries);
        Assert.Equal("held", listedEntry.Key);
        Assert.Equal(Ballot.Floor(2), listedEntry.PromisedBallot);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReplaceLostVoterPreservesStateRefreshesStaleProposerAndKeepsRetiredVoterRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await ReconfigRig.OpenAsync(_directory.FullName, ["a", "b", "c"], ct);
        var stale = rig.CreateProposer(1, ["a", "b", "c"], enableRefresh: true);
        var created = await stale.CompareExchangeAsync("ledger-map", null, Bytes("epoch-1"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, created.Status);
        var initialVersion = created.Current!.Version!;

        // Permanently lose voter c and wipe its disk.
        await rig.CloseAndDeleteVoterAsync("c");

        // Introduce fresh replacement voter d at joinConfiguration = 2; verify floor = (2,0,0) before serving and admission.
        await rig.AddJoiningVoterAsync("d", joinConfiguration: 2, admissionPeers: ["a", "b"], ct);
        var preJoinReject = await rig.Remote("d").PrepareAsync("ledger-map", new PrepareRequest(Ballot.InitialClassic(1, 1)), ct);
        Assert.False(preJoinReject.IsPromised);
        Assert.Equal(Ballot.Floor(2), preJoinReject.ConflictingBallot);

        var reconfigurator = rig.CreateReconfigurator(2);
        var active = await reconfigurator.ReconfigureAsync(
            Descriptors("a", "b", "c"), 1, Descriptors("a", "b", "d"), ct);
        Assert.Equal(2, active.Number);
        Assert.Equal(VoterConfigurationState.Active, active.State);
        Assert.True(active.HasSameVoters(Descriptors("a", "b", "d")));

        // Stale proposer on c = 1 hits floor refusal, discovers c = 2, and preserves logical versions.
        Assert.Equal(1, stale.ActiveConfiguration);
        var read = await stale.ReadAsync("ledger-map", ct);
        Assert.Equal(2, stale.ActiveConfiguration);
        Assert.Equal(Bytes("epoch-1"), read.Value);
        Assert.Equal(initialVersion, read.Version);

        var updated = await stale.CompareExchangeAsync("ledger-map", initialVersion, Bytes("epoch-2"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, updated.Status);

        // Reviving the retired voter c on a fresh disk under its old identity is still refused by surviving voters' admissions.
        var revived = await rig.OpenUnadmittedVoterAsync("c", joinConfiguration: 1, bootstrapDigest: VoterConfiguration.ComputeBootstrapDigest(["a", "b", "c"]), ct);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.AdmitVoterAsync(revived, ["a", "b"], ct));
        Assert.Contains("state was lost", refusal.Message, StringComparison.Ordinal);
        Assert.False(revived.IsServing);
        await revived.DisposeAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task AddOneThenRemoveOneVoterWorksThroughChainedConfigurations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await ReconfigRig.OpenAsync(_directory.FullName, ["a", "b", "c"], ct);
        var stale = rig.CreateProposer(1, ["a", "b", "c"], enableRefresh: true);
        var w1 = await stale.CompareExchangeAsync("k", null, Bytes("v1"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, w1.Status);

        // Add voter d: 3 -> 4 (c = 1 -> 2).
        await rig.AddJoiningVoterAsync("d", joinConfiguration: 2, admissionPeers: ["a", "b", "c"], ct);
        var reconfigurator = rig.CreateReconfigurator(2);
        var c2 = await reconfigurator.ReconfigureAsync(
            Descriptors("a", "b", "c"), 1, Descriptors("a", "b", "c", "d"), ct);
        Assert.Equal(2, c2.Number);
        Assert.Equal(VoterConfigurationState.Active, c2.State);
        Assert.Equal(4, c2.Voters.Length);

        var w2 = await stale.CompareExchangeAsync("k", w1.Current!.Version, Bytes("v2"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, w2.Status);
        Assert.Equal(2, stale.ActiveConfiguration);

        // Remove voter a: 4 -> 3 (c = 2 -> 3).
        var c3 = await reconfigurator.ReconfigureAsync(
            Descriptors("a", "b", "c", "d"), 2, Descriptors("b", "c", "d"), ct);
        Assert.Equal(3, c3.Number);
        Assert.Equal(VoterConfigurationState.Active, c3.State);
        Assert.Equal(3, c3.Voters.Length);

        // Stop retired voter a; writes and reads continue on {b, c, d}.
        await rig.CloseVoterAsync("a");
        var read = await stale.ReadAsync("k", ct);
        Assert.Equal(3, stale.ActiveConfiguration);
        Assert.Equal(Bytes("v2"), read.Value);
        var w3 = await stale.CompareExchangeAsync("k", read.Version, Bytes("v3"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, w3.Status);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("AfterChoose")]
    [InlineData("AfterFence")]
    [InlineData("MidInstallAfterConfigTransferred")]
    [InlineData("BeforeActivate")]
    public async Task CrashAtEveryOperatorStepResumesCleanly(string crashPoint)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await ReconfigRig.OpenAsync(_directory.FullName, ["a", "b", "c"], ct);
        var c1 = Descriptors("a", "b", "c");
        var c2 = Descriptors("a", "b", "d");

        var writer = rig.CreateProposer(1, ["a", "b", "c"], enableRefresh: true);
        Assert.Equal(CompareExchangeStatus.Applied, (await writer.CompareExchangeAsync("k1", null, Bytes("alpha"), ct)).Status);
        Assert.Equal(CompareExchangeStatus.Applied, (await writer.CompareExchangeAsync("k2", null, Bytes("beta"), ct)).Status);

        await rig.CloseVoterAsync("c");
        await rig.AddJoiningVoterAsync("d", joinConfiguration: 2, admissionPeers: ["a", "b"], ct);

        // Step 1: Choose (2, V(2), Pending) in c = 1.
        var pending = new VoterConfiguration(2, c2, VoterConfigurationState.Pending);
        var choose = await writer.CompareExchangeAsync(VoterConfiguration.RegisterKey, null, VoterConfigurationEncoding.Encode(pending), ct);
        Assert.Equal(CompareExchangeStatus.Applied, choose.Status);

        var reconfigurator = rig.CreateReconfigurator(2);
        ImmutableArray<AcceptorPeer> prevPeers = [.. c1.Select(d => new AcceptorPeer(d.Id, rig.Remote(d.Id)))];
        ImmutableArray<AcceptorPeer> nextPeers = [.. c2.Select(d => new AcceptorPeer(d.Id, rig.Remote(d.Id)))];

        if (crashPoint is "AfterFence" or "MidInstallAfterConfigTransferred" or "BeforeActivate")
        {
            Assert.Equal(Ballot.Floor(2), await rig.Remote("a").SetFloorAsync(Ballot.Floor(2), ct));
            Assert.Equal(Ballot.Floor(2), await rig.Remote("b").SetFloorAsync(Ballot.Floor(2), ct));
        }

        if (crashPoint == "MidInstallAfterConfigTransferred")
        {
            // Transfer K_cfg prematurely while leaving k2 untransferred on d; ResumeAsync must still transfer k2.
            _ = await reconfigurator.TransferKeyHandOffAsync(VoterConfiguration.RegisterKey, 2, prevPeers, nextPeers, ct);
            Assert.True(await rig.Remote("d").ReadAsync("k2", cancellationToken: ct) is AcceptorMissing);
        }
        else if (crashPoint == "BeforeActivate")
        {
            _ = await reconfigurator.TransferKeyHandOffAsync("k1", 2, prevPeers, nextPeers, ct);
            _ = await reconfigurator.TransferKeyHandOffAsync("k2", 2, prevPeers, nextPeers, ct);
            _ = await reconfigurator.TransferKeyHandOffAsync(VoterConfiguration.RegisterKey, 2, prevPeers, nextPeers, ct);
        }

        // Resume with a fresh operator proposer after the simulated crash.
        var resuming = rig.CreateReconfigurator(3);
        var active = await resuming.ResumeAsync(c1, 1, ct);
        Assert.Equal(2, active.Number);
        Assert.Equal(VoterConfigurationState.Active, active.State);
        Assert.True(active.HasSameVoters(c2));

        // Verify both keys transferred into a classic quorum of V(2) at configuration 2 and are readable through the stale proposer.
        foreach (var key in (string[])["k1", "k2"])
        {
            var reads = await Task.WhenAll(c2.Select(d => rig.Remote(d.Id).ReadAsync(key, cancellationToken: ct).AsTask()));
            Assert.True(
                reads.Count(static r => r is KeyedAcceptorState state && state.AcceptedBallot.Configuration == 2) >= 2,
                $"Expected classic quorum of V(2) to hold '{key}' at configuration 2.");
        }

        Assert.Equal(Bytes("alpha"), (await writer.ReadAsync("k1", ct)).Value);
        Assert.Equal(Bytes("beta"), (await writer.ReadAsync("k2", ct)).Value);
        Assert.Equal(2, writer.ActiveConfiguration);

        // Resuming again once Active is idempotent.
        var idempotent = await resuming.ResumeAsync(c1, 1, ct);
        Assert.Equal(2, idempotent.Number);
        Assert.Equal(VoterConfigurationState.Active, idempotent.State);
    }

    [Fact(Timeout = 60_000)]
    public async Task RacingOperatorsChooseOneWinnerAndConcurrentResumeConverges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await ReconfigRig.OpenAsync(_directory.FullName, ["a", "b", "c"], ct);
        await rig.AddJoiningVoterAsync("d", joinConfiguration: 2, admissionPeers: ["a", "b"], ct);
        await rig.AddJoiningVoterAsync("e", joinConfiguration: 2, admissionPeers: ["a", "c"], ct);

        var c1 = Descriptors("a", "b", "c");
        var targetA = Descriptors("a", "b", "d");
        var targetB = Descriptors("a", "c", "e");

        var op1 = rig.CreateReconfigurator(10);
        var op2 = rig.CreateReconfigurator(11);

        // Two operators race with different target configurations: exactly one wins, the other gets ReconfigurationConflictException.
        var task1 = TryReconfigureAsync(op1, c1, 1, targetA, ct);
        var task2 = TryReconfigureAsync(op2, c1, 1, targetB, ct);
        var results = await Task.WhenAll(task1, task2);

        var winners = results.Where(static r => r.Config is not null).ToArray();
        var conflicts = results.Where(static r => r.Conflict is not null).ToArray();
        Assert.Single(winners);
        Assert.Single(conflicts);
        Assert.Equal(2, winners[0].Config!.Number);
        Assert.Equal(VoterConfigurationState.Active, winners[0].Config!.State);
        Assert.True(conflicts[0].Conflict!.WinningConfiguration.HasSameVoters(winners[0].Config!.Voters));

        // Concurrent resumes on the activated configuration both converge cleanly.
        var resumed = await Task.WhenAll(op1.ResumeAsync(c1, 1, ct), op2.ResumeAsync(c1, 1, ct));
        Assert.All(resumed, cfg =>
        {
            Assert.Equal(2, cfg.Number);
            Assert.Equal(VoterConfigurationState.Active, cfg.State);
            Assert.True(cfg.HasSameVoters(winners[0].Config!.Voters));
        });
    }

    [Fact(Timeout = 60_000)]
    public async Task StaleProposerRefreshesOnAbsentKeyAndRestartedAllocatorWithHigherConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await ReconfigRig.OpenAsync(_directory.FullName, ["a", "b", "c"], ct);
        var stale = rig.CreateProposer(1, ["a", "b", "c"], enableRefresh: true);

        await rig.AddJoiningVoterAsync("d", joinConfiguration: 2, admissionPeers: ["d", "e", "f"], ct, admit: false);
        await rig.AddJoiningVoterAsync("e", joinConfiguration: 2, admissionPeers: ["d", "e", "f"], ct, admit: false);
        await rig.AddJoiningVoterAsync("f", joinConfiguration: 2, admissionPeers: ["d", "e", "f"], ct, admit: false);
        await Task.WhenAll(
            rig.AdmitVoterAsync(rig.Voter("d"), ["e", "f"], ct),
            rig.AdmitVoterAsync(rig.Voter("e"), ["d", "f"], ct),
            rig.AdmitVoterAsync(rig.Voter("f"), ["d", "e"], ct));

        var reconfigurator = rig.CreateReconfigurator(2);
        var c2 = await reconfigurator.ReconfigureAsync(
            Descriptors("a", "b", "c"), 1, Descriptors("d", "e", "f"), ct);
        Assert.Equal(2, c2.Number);

        // Write a brand-new key in c = 2 that never existed on V(1).
        var writer2 = rig.CreateProposer(3, ["d", "e", "f"], enableRefresh: true, initialConfiguration: 2);
        var w = await writer2.CompareExchangeAsync("only-in-c2", null, Bytes("fresh"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, w.Status);

        // Stale proposer on c = 1 reads "only-in-c2"; V(1) answers AcceptorMissing with floor (2,0,0), triggering refresh to c = 2.
        var read = await stale.ReadAsync("only-in-c2", ct);
        Assert.Equal(2, stale.ActiveConfiguration);
        Assert.Equal(Bytes("fresh"), read.Value);

        // Stale lister on c = 1 calling ListKeysAsync receives FloorBallot = (2,0,0) from fenced V(1), triggering refresh to c = 2.
        var staleLister = rig.CreateProposer(4, ["a", "b", "c"], enableRefresh: true, initialConfiguration: 1);
        var listed = await staleLister.ListKeysAsync(prefix: "only-", cancellationToken: ct);
        Assert.Equal(2, staleLister.ActiveConfiguration);
        Assert.Equal<string>(["only-in-c2"], listed.Keys);

        // A restarted proposer whose SQLite ballot source already reserved c = 2 starts with seed c = 1 and refreshes cleanly on write.
        var restarted = rig.CreateProposerFromExistingBallots(2, ["a", "b", "c"], enableRefresh: true, initialConfiguration: 1);
        var updated = await restarted.CompareExchangeAsync("only-in-c2", read.Version, Bytes("from-restarted"), ct);
        Assert.Equal(CompareExchangeStatus.Applied, updated.Status);
        Assert.Equal(2, restarted.ActiveConfiguration);
    }

    [Fact(Timeout = 60_000)]
    public async Task BootstrapDigestMismatchFailsAdmissionAndReopenImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var failures = new RecordingFatalErrorHandler();
        var digest1 = VoterConfiguration.ComputeBootstrapDigest(["a", "b", "c"]);
        var digest2 = VoterConfiguration.ComputeBootstrapDigest(["a", "b", "z"]);

        await using var voterA = await LocalVoter.OpenAsync("a", Path.Combine(_directory.FullName, "ba.db"), 64, 16, failures, 1, digest1, ct);
        await using var voterB = await LocalVoter.OpenAsync("b", Path.Combine(_directory.FullName, "bb.db"), 64, 16, failures, 1, digest2, ct);
        var serviceB = new CasPaxosVoterService(voterB);
        var remoteB = new RemoteVoter("b", () => serviceB);

        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => VoterAdmission.AdmitAsync(
            voterA,
            [remoteB],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(50),
            TimeProvider.System,
            NullLogger.Instance,
            ct));
        Assert.Contains("bootstrap voter configurations disagree", mismatch.Message, StringComparison.Ordinal);
        Assert.False(voterA.IsServing);

        // Reopening voterA with a different bootstrap digest fails immediately before serving.
        await voterA.DisposeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalVoter.OpenAsync(
            "a", Path.Combine(_directory.FullName, "ba.db"), 64, 16, failures, 1, digest2, ct));
    }

    public void Dispose()
    {
        _leaks.Dispose();
        _directory.Delete(recursive: true);
    }

    private static ByteString Bytes(string value) => new(Encoding.UTF8.GetBytes(value));

    private static ImmutableArray<VoterDescriptor> Descriptors(params string[] ids) =>
        [.. ids.Select(id => new VoterDescriptor(id, $"127.0.0.1:{11000 + Math.Abs(StringComparer.Ordinal.GetHashCode(id)) % 10000}"))];

    private static async Task<(VoterConfiguration? Config, ReconfigurationConflictException? Conflict)> TryReconfigureAsync(
        VoterReconfigurationDriver reconfigurator,
        ImmutableArray<VoterDescriptor> current,
        long currentConfig,
        ImmutableArray<VoterDescriptor> next,
        CancellationToken ct)
    {
        try
        {
            return (await reconfigurator.ReconfigureAsync(current, currentConfig, next, ct), null);
        }
        catch (ReconfigurationConflictException conflict)
        {
            return (null, conflict);
        }
    }

    /// <summary>
    /// Dynamic multi-voter test harness over real SQLite databases, <see cref="CasPaxosVoterService"/>, and
    /// <see cref="RemoteVoter"/> links.
    /// </summary>
    private sealed class ReconfigRig(string directory) : IAsyncDisposable
    {
        private readonly Dictionary<string, LocalVoter> _voters = [with(StringComparer.Ordinal)];
        private readonly Dictionary<string, CasPaxosVoterService> _services = [with(StringComparer.Ordinal)];
        private readonly Dictionary<string, VoterLink> _links = [with(StringComparer.Ordinal)];
        private readonly Dictionary<int, SqliteProposerBallotSource> _ballots = [];
        private readonly List<ReplicatedRegisterService> _registers = [];

        public RecordingFatalErrorHandler FatalErrors { get; } = new();

        public static async Task<ReconfigRig> OpenAsync(string directory, string[] bootstrapIds, CancellationToken ct)
        {
            var rig = new ReconfigRig(directory);
            try
            {
                var digest = VoterConfiguration.ComputeBootstrapDigest(bootstrapIds);
                foreach (var id in bootstrapIds)
                {
                    _ = await rig.OpenVoterInternalAsync(id, joinConfiguration: 1, digest, ct);
                }

                await Task.WhenAll(bootstrapIds.Select(id =>
                    rig.AdmitVoterAsync(rig._voters[id], bootstrapIds.Where(other => other != id), ct)));
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public LocalVoter Voter(string id) => _voters[id];

        public RemoteVoter Remote(string id) => new(id, () =>
            _links.TryGetValue(id, out var link)
                ? link
                : throw new CasPaxosVoterUnavailableException($"Voter '{id}' is unknown."));

        public IKeyedAcceptor Resolve(VoterDescriptor descriptor) => Remote(descriptor.Id);

        public ReplicatedRegisterService CreateProposer(int proposerId, string[] initialVoters, bool enableRefresh, long initialConfiguration = 1)
        {
            var ballots = GetOrCreateBallots(proposerId);
            return CreateProposerWithBallots(ballots, initialVoters, enableRefresh, initialConfiguration);
        }

        public ReplicatedRegisterService CreateProposerFromExistingBallots(int proposerId, string[] initialVoters, bool enableRefresh, long initialConfiguration = 1)
        {
            if (_ballots.Remove(proposerId, out var old))
            {
                old.Dispose();
            }

            var ballots = GetOrCreateBallots(proposerId);
            return CreateProposerWithBallots(ballots, initialVoters, enableRefresh, initialConfiguration);
        }

        public VoterReconfigurationDriver CreateReconfigurator(int proposerId) =>
            new(GetOrCreateBallots(proposerId), Resolve, CreateOptions(enableRefresh: true));

        public async Task AddJoiningVoterAsync(string id, long joinConfiguration, string[] admissionPeers, CancellationToken ct, bool admit = true)
        {
            var local = await OpenVoterInternalAsync(id, joinConfiguration, bootstrapDigest: null, ct);
            if (admit)
            {
                await AdmitVoterAsync(local, admissionPeers.Where(peer => peer != id), ct);
            }
        }

        public Task<LocalVoter> OpenUnadmittedVoterAsync(string id, long joinConfiguration, ByteString? bootstrapDigest, CancellationToken ct) =>
            LocalVoter.OpenAsync(id, DatabasePath(id), 256, 64, FatalErrors, joinConfiguration, bootstrapDigest, ct);

        public Task AdmitVoterAsync(LocalVoter voter, IEnumerable<string> others, CancellationToken ct) =>
            VoterAdmission.AdmitAsync(
                voter,
                [.. others.Select(Remote)],
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(50),
                TimeProvider.System,
                NullLogger.Instance,
                ct);

        public async Task CloseVoterAsync(string id)
        {
            _ = _services.Remove(id);
            if (_voters.Remove(id, out var local))
            {
                await local.DisposeAsync();
            }
        }

        public async Task CloseAndDeleteVoterAsync(string id)
        {
            await CloseVoterAsync(id);
            foreach (var file in Directory.EnumerateFiles(directory, $"voter-{id}.db*"))
            {
                File.Delete(file);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Task.WhenAll(_registers.Select(static r => r.DisposeAsync().AsTask()));
            }
            finally
            {
                foreach (var ballots in _ballots.Values)
                {
                    ballots.Dispose();
                }

                foreach (var voter in _voters.Values)
                {
                    await voter.DisposeAsync();
                }
            }
        }

        private async Task<LocalVoter> OpenVoterInternalAsync(string id, long joinConfiguration, ByteString? bootstrapDigest, CancellationToken ct)
        {
            var local = await LocalVoter.OpenAsync(id, DatabasePath(id), 256, 64, FatalErrors, joinConfiguration, bootstrapDigest, ct);
            _voters[id] = local;
            _services[id] = new CasPaxosVoterService(local);
            if (!_links.ContainsKey(id))
            {
                _links[id] = new VoterLink(() =>
                    _services.TryGetValue(id, out var service)
                        ? service
                        : throw new CasPaxosVoterUnavailableException($"Voter '{id}' is closed."));
            }

            return local;
        }

        private SqliteProposerBallotSource GetOrCreateBallots(int proposerId)
        {
            if (!_ballots.TryGetValue(proposerId, out var ballots))
            {
                ballots = new SqliteProposerBallotSource(
                    Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"proposer-{proposerId}.db")),
                    proposerId,
                    FatalErrors);
                _ballots[proposerId] = ballots;
            }

            return ballots;
        }

        private ReplicatedRegisterService CreateProposerWithBallots(
            SqliteProposerBallotSource ballots, string[] initialVoters, bool enableRefresh, long initialConfiguration)
        {
            var service = new ReplicatedRegisterService(
                [.. initialVoters.Select(id => new AcceptorPeer(id, Remote(id)))],
                ballots,
                CreateOptions(enableRefresh, initialConfiguration));
            _registers.Add(service);
            return service;
        }

        private RegisterServiceOptions CreateOptions(bool enableRefresh, long initialConfiguration = 1) => new()
        {
            OperationTimeout = TimeSpan.FromSeconds(5),
            AttemptTimeout = TimeSpan.FromMilliseconds(500),
            RetryBackoff = TimeSpan.FromMilliseconds(5),
            InitialConfiguration = initialConfiguration,
            PeerResolver = enableRefresh ? Resolve : null,
        };

        private string DatabasePath(string id) => Path.Combine(directory, $"voter-{id}.db");
    }
}
