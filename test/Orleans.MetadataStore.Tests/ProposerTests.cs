using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;
#if false

namespace Orleans.MetadataStore.Tests
{

    public class TestConfigManagerMediator<TValue> : IConfigurationManagerMediator<TValue>
    {
        public delegate ValueTask<AcceptResponse> AcceptHandler(Ballot proposerConfig, Ballot ballot, TValue value);
        public delegate ValueTask<PrepareResponse<TValue>> PrepareHandler(Ballot proposerConfig, Ballot ballot);

        public Dictionary<SiloAddress, (AcceptHandler Accept, PrepareHandler Prepare)> Acceptors { get; } = new();

        public ValueTask<AcceptResponse> Accept(SiloAddress server, Ballot proposerConfig, Ballot ballot, TValue value)
        {
            return Acceptors[server].Accept(proposerConfig, ballot, value);
        }

        public ValueTask Committed(SiloAddress server, ClusterConfiguration value)
        {
            throw new NotImplementedException();
        }

        public ValueTask<PrepareResponse<TValue>> Prepare(SiloAddress server, Ballot proposerConfig, Ballot ballot)
        {
            return Acceptors[server].Prepare(proposerConfig, ballot);
        }
    }

    [Trait("Category", "BVT"), Trait("Category", "MetadataStore")]
    public class ProposerTests
    {
        private readonly ConfigProposer _proposer;
        private readonly ConfigProposer.ITestAccessor _proposerAccessor;
        private readonly SiloAddress[] _silos;
        private readonly LocalConfiguration _localConfig;
        private readonly TestConfigManagerMediator<int> _remotes;
        private readonly ChangeFunction<int, int> permitIncrement = (existing, val) => val == existing + 1 ? val : existing;

        public ProposerTests(ITestOutputHelper output)
        {
            _silos = new[]
            {
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1), 1),
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2), 2),
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 3), 3)
            };

            _localConfig = new LocalConfiguration
            {
                CommittedConfiguration = GetConfigWithVersion(1)
            };

            _remotes = new TestConfigManagerMediator();
            _proposer = new ConfigProposer(
                _localConfig,
                _silos[0],
                new XunitLogger(output, "Proposer"),
                _remotes);
            _proposerAccessor = _proposer;
        }

        private ClusterConfiguration GetConfigWithVersion(long version) => new(new(1, _silos[0]), new(version), _silos, 2, 2);

        [Fact]
        public async Task TryUpdateSucceeds()
        {
            foreach (var server in _silos)
            {
                _remotes.OnPrepare[server] = (_, _) => new ValueTask<PrepareResponse<>(PrepareResponse.Success(new Ballot(1, _silos[0]), _localConfig.CommittedConfiguration));
                _remotes.OnAccept[server] = (_, _, _) => new ValueTask<AcceptResponse>(AcceptResponse.Success());
            }

            _proposerAccessor.Ballot = new Ballot(2, _silos[0]);
            var expectedBallot = _proposerAccessor.Ballot.Successor();

            var updatedConfig = GetConfigWithVersion(2);
            var result = await _proposer.TryUpdate(updatedConfig, CancellationToken.None);
            await this.AssertSuccess(expectedValue: updatedConfig, expectedBallot: expectedBallot);

            // Now try calling again. The 'distinguished leader' optimization should allow us to avoid the prepare round.
            expectedBallot = _proposerAccessor.Ballot.Successor();
            result = await _proposer.TryUpdate(44, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Success, result.Status);
            Assert.Equal(44, result.Value);
            await this.AssertDistinguishedLeaderSuccess(44, expectedBallot);
        }

        [Fact]
        public async Task TryUpdateRequiresPrepareQuorum()
        {
            this.remoteStores[0].OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(PrepareResponse<object>.Success(new Ballot(1, 1), 42));
            this.remoteStores[0].OnAccept = (args) => new ValueTask<AcceptResponse>(AcceptResponse.Success());

            this.remoteStores[1].OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(PrepareResponse<object>.Success(new Ballot(1, 1), 42));
            this.remoteStores[1].OnAccept = (args) => new ValueTask<AcceptResponse>(AcceptResponse.Success());

            // Conflict!
            this.remoteStores[2].OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(PrepareResponse<object>.Conflict(new Ballot(3, 1)));
            this.remoteStores[2].OnAccept = (args) => new ValueTask<AcceptResponse>(AcceptResponse.Success());

            _proposerAccessor.Ballot = new Ballot(2, 1);
            var expectedBallot = _proposerAccessor.Ballot.Successor();

            var result = await _proposer.TryUpdate(43, permitIncrement, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Success, result.Status);
            Assert.Equal(43, result.Value);

            await this.AssertSuccess(expectedValue: 43, expectedBallot: expectedBallot);

            this.remoteStores[0].OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(PrepareResponse<object>.Success(new Ballot(1, 2), 99));
            this.remoteStores[0].OnAccept = (args) => new ValueTask<AcceptResponse>(AcceptResponse.Success());

            // Conflict!
            this.remoteStores[1].OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(PrepareResponse<object>.Conflict(new Ballot(7, 2)));
            this.remoteStores[1].OnAccept = (args) => new ValueTask<AcceptResponse>(AcceptResponse.Conflict(new Ballot(3, 2)));

            this.remoteStores[2].OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(PrepareResponse<object>.Conflict(new Ballot(7, 2)));
            this.remoteStores[2].OnAccept = (args) => new ValueTask<AcceptResponse>(AcceptResponse.Conflict(new Ballot(3, 2)));

            _proposerAccessor.Ballot = new Ballot(2, 1);
            expectedBallot = _proposerAccessor.Ballot.Successor();

            _proposerAccessor.SkipPrepare = false;
            result = await _proposer.TryUpdate(43, permitIncrement, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Failed, result.Status);
            Assert.Equal(99, result.Value);

            foreach (var store in this.remoteStores)
            {
                Assert.True(await store.PrepareCalls.WaitToReadAsync());
                Assert.True(store.PrepareCalls.TryRead(out var prepareArgs));
                Assert.Equal(config.Stamp, prepareArgs.ProposerParentBallot);
                Assert.Equal(Key, prepareArgs.Key);
                Assert.Equal(expectedBallot, prepareArgs.Ballot);

                Assert.True(await store.PrepareCalls.WaitToReadAsync());
                Assert.True(store.PrepareCalls.TryRead(out prepareArgs));
                Assert.Equal(config.Stamp, prepareArgs.ProposerParentBallot);
                Assert.Equal(Key, prepareArgs.Key);

                // Fast-forward to the new ballot
                Assert.Equal(new Ballot(8, 1), prepareArgs.Ballot);

                // Accept should not be called
                Assert.False(store.AcceptCalls.TryRead(out _));
            }
        }

        [Fact]
        public async Task TryUpdateRequiresPrepareQuorum_HardFailure()
        {
            foreach (var store in this.remoteStores)
            {
                store.OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(Task.FromException<PrepareResponse<object>>(new Exception("nope!")));
                store.OnAccept = (args) => new ValueTask<AcceptResponse>(Task.FromException<AcceptResponse>(new Exception("nope!")));
            }

            _proposerAccessor.Ballot = new Ballot(2, 1);
            var expectedBallot1 = _proposerAccessor.Ballot.Successor();
            var expectedBallot2 = expectedBallot1.Successor();

            var (status, value) = await _proposer.TryUpdate(43, permitIncrement, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Failed, status);
            Assert.Equal(0, value);

            foreach (var store in this.remoteStores)
            {
                Assert.True(await store.PrepareCalls.WaitToReadAsync());
                Assert.True(store.PrepareCalls.TryRead(out var prepareArgs));
                Assert.Equal(config.Stamp, prepareArgs.ProposerParentBallot);
                Assert.Equal(Key, prepareArgs.Key);
                Assert.Equal(expectedBallot1, prepareArgs.Ballot);

                // Allow for one retry
                Assert.True(await store.PrepareCalls.WaitToReadAsync());
                Assert.True(store.PrepareCalls.TryRead(out prepareArgs));
                Assert.Equal(config.Stamp, prepareArgs.ProposerParentBallot);
                Assert.Equal(Key, prepareArgs.Key);
                Assert.Equal(expectedBallot2, prepareArgs.Ballot);

                // Accept should not be called
                Assert.False(store.AcceptCalls.TryRead(out _));
            }
        }

        [Fact]
        public async Task TryUpdateRequiresAcceptQuorum_HardFailure()
        {
            foreach (var store in this.remoteStores)
            {
                store.OnPrepare = (args) => new ValueTask<PrepareResponse<object>>(PrepareResponse.Success<object>(new Ballot(1, 2), 42));
                store.OnAccept = (args) => new ValueTask<AcceptResponse>(Task.FromException<AcceptResponse>(new Exception("nope!")));
            }

            _proposerAccessor.Ballot = new Ballot(2, 1);
            var expectedBallot1 = _proposerAccessor.Ballot.Successor();
            var expectedBallot2 = expectedBallot1.Successor();

            var (status, value) = await _proposer.TryUpdate(43, permitIncrement, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Uncertain, status);
            Assert.Equal(42, value);

            foreach (var store in this.remoteStores)
            {
                Assert.True(await store.PrepareCalls.WaitToReadAsync());
                Assert.True(store.PrepareCalls.TryRead(out var prepareArgs));
                Assert.Equal(config.Stamp, prepareArgs.ProposerParentBallot);
                Assert.Equal(Key, prepareArgs.Key);
                Assert.Equal(expectedBallot1, prepareArgs.Ballot);

                // Accept should fail on the first call.
                Assert.True(await store.AcceptCalls.WaitToReadAsync());
                Assert.True(store.AcceptCalls.TryRead(out var acceptArgs));
                Assert.Equal(config.Stamp, acceptArgs.ProposerParentBallot);
                Assert.Equal(expectedBallot1, acceptArgs.Ballot);
                Assert.Equal(43, acceptArgs.Value);
                Assert.Equal(Key, acceptArgs.Key);

                // After Accept initially fails, we should retry from Prepare.
                Assert.True(await store.PrepareCalls.WaitToReadAsync());
                Assert.True(store.PrepareCalls.TryRead(out prepareArgs));
                Assert.Equal(config.Stamp, prepareArgs.ProposerParentBallot);
                Assert.Equal(Key, prepareArgs.Key);
                Assert.Equal(expectedBallot2, prepareArgs.Ballot);
            }
        }

        private async Task AssertSuccess(ClusterConfiguration expectedValue, Ballot expectedBallot)
        {
            foreach (var store in this.remoteStores)
            {
                Assert.True(await store.PrepareCalls.WaitToReadAsync());
                Assert.True(store.PrepareCalls.TryRead(out var prepareArgs));
                Assert.False(store.PrepareCalls.TryRead(out _));

                // Validate call arguments.
                Assert.Equal(config.Stamp, prepareArgs.ProposerParentBallot);
                Assert.Equal(Key, prepareArgs.Key);
                Assert.Equal(expectedBallot, prepareArgs.Ballot);

                Assert.True(await store.AcceptCalls.WaitToReadAsync());
                Assert.True(store.AcceptCalls.TryRead(out var acceptArgs));
                Assert.False(store.AcceptCalls.TryRead(out _));

                // Validate call arguments.
                Assert.Equal(config.Stamp, acceptArgs.ProposerParentBallot);
                Assert.Equal(expectedBallot, acceptArgs.Ballot);
                Assert.Equal(expectedValue, acceptArgs.Value);
                Assert.Equal(Key, acceptArgs.Key);
            }
        }

        private async Task AssertDistinguishedLeaderSuccess(int expectedValue, Ballot expectedBallot)
        {
            foreach (var store in this.remoteStores)
            {
                Assert.True(await store.AcceptCalls.WaitToReadAsync());
                Assert.True(store.AcceptCalls.TryRead(out var acceptArgs));
                Assert.False(store.AcceptCalls.TryRead(out _));

                // Validate call arguments.
                Assert.Equal(config.Stamp, acceptArgs.ProposerParentBallot);
                Assert.Equal(expectedBallot, acceptArgs.Ballot);
                Assert.Equal(expectedValue, acceptArgs.Value);
                Assert.Equal(Key, acceptArgs.Key);

                // Prepare is checked second because even though it would have been called first, we do not expect it to have been called at all.
                // So we wait until we know Accept has been called before checking the prepare.
                Assert.False(store.PrepareCalls.TryRead(out _));
            }
        }
    }
}
#endif
