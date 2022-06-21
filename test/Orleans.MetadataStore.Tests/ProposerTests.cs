using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.MetadataStore.Tests
{
    public class TestConfigRemotes<TValue> : IAcceptorRouter<TValue>, ILearnerRouter<TValue>
    {
        public delegate ValueTask<AcceptResponse> AcceptHandler(Ballot proposerConfig, Ballot ballot, TValue value, AcceptOptions options);
        public delegate ValueTask<PrepareResponse<TValue>> PrepareHandler(Ballot proposerConfig, Ballot ballot);

        public Dictionary<SiloAddress, (PrepareHandler Prepare, AcceptHandler Accept)> Acceptors { get; } = new();

        public ValueTask<AcceptResponse> Accept(SiloAddress server, Ballot proposerConfig, Ballot ballot, TValue value, AcceptOptions options)
        {
            return Acceptors[server].Accept(proposerConfig, ballot, value, options);
        }

        public ValueTask Committed(SiloAddress server, TValue value)
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
        private readonly List<(ConfigAcceptor Acceptor, ConfigAcceptor.ITestAccessor Accessor, ILocalConfiguration Config)> _acceptors;
        private readonly SiloAddress[] _silos;
        private readonly LocalConfiguration _localConfig;
        private readonly TestConfigRemotes<ClusterConfiguration> _remotes;
        private readonly Guid _proposerServerId;

        public ProposerTests(ITestOutputHelper output)
        {
            _silos = new[]
            {
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1), 1),
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2), 2),
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 3), 3)
            };

            _proposerServerId = Guid.NewGuid();
            _localConfig = new LocalConfiguration
            {
                CommittedConfiguration = GetConfigWithVersion(1)
            };

            _remotes = new TestConfigRemotes<ClusterConfiguration>();
            _proposer = new ConfigProposer(
                _localConfig,
                _proposerServerId,
                new XunitLogger(output, "Proposer"),
                _remotes);
            _proposerAccessor = _proposer;

            _acceptors = new List<(ConfigAcceptor Acceptor, ConfigAcceptor.ITestAccessor Accessor, ILocalConfiguration Config)>();
            foreach (var server in _silos)
            {
                var acceptor = new ConfigAcceptor(_localConfig);
                var config = new LocalConfiguration
                {
                    CommittedConfiguration = GetConfigWithVersion(1)
                };
                _acceptors.Add((acceptor, acceptor, config));

                ValueTask<AcceptResponse> OnAccept(Ballot proposerConfig, Ballot ballot, ClusterConfiguration value, AcceptOptions options) => new(acceptor.Accept(proposerConfig, ballot, value, options));
                ValueTask<PrepareResponse<ClusterConfiguration>> OnPrepare(Ballot proposerConfig, Ballot ballot) => new(acceptor.Prepare(proposerConfig, ballot));
                _remotes.Acceptors[server] = (OnPrepare, OnAccept);
            }
        }

        private ClusterConfiguration GetConfigWithVersion(long version) => new(new((int)version, Guid.Empty), new(version), _silos);

        [Fact]
        public async Task TryUpdateSucceeds()
        {
            _proposerAccessor.Ballot = new Ballot(2, _proposerServerId);
            var expectedBallot = _proposerAccessor.Ballot.Successor(_proposerAccessor.Ballot.Proposer);

            var updatedConfig = GetConfigWithVersion(2);
            var result = await _proposer.TryUpdate(updatedConfig, numRetries: 0, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Success, result.Status);
            Assert.Equal(updatedConfig, result.Value);
            foreach (var (_, accessor, _) in _acceptors)
            {
                Assert.Equal(updatedConfig, accessor.Value);
                Assert.Equal(expectedBallot, accessor.Accepted);

                // Promise for distinguished leader commit.
                Assert.Equal(expectedBallot.Successor(_proposerAccessor.Ballot.Proposer), accessor.Promised);
            }

            // Now try calling again. The 'distinguished leader' optimization should allow us to avoid the prepare round.
            expectedBallot = _proposerAccessor.Ballot.Successor(_proposerAccessor.Ballot.Proposer);
            var updatedConfig2 = GetConfigWithVersion(3);
            result = await _proposer.TryUpdate(updatedConfig2, numRetries: 0, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Success, result.Status);
            Assert.Equal(updatedConfig2, result.Value);
            foreach (var (_, accessor, _) in _acceptors)
            {
                Assert.Equal(updatedConfig2, accessor.Value);
                Assert.Equal(expectedBallot, accessor.Accepted);

                // Promise for distinguished leader commit.
                Assert.Equal(expectedBallot.Successor(expectedBallot.Proposer), accessor.Promised);
            }
        }

        [Fact]
        public async Task TryUpdateRequiresPrepareQuorum()
        {
            var otherServerId = Guid.NewGuid();
            _acceptors[0].Accessor.Accepted = new Ballot(1, _proposerServerId);
            _acceptors[0].Accessor.Value = GetConfigWithVersion(1);

            _acceptors[1].Accessor.Accepted = new Ballot(1, _proposerServerId);
            _acceptors[1].Accessor.Value = GetConfigWithVersion(1);

            // Conflict!
            _acceptors[2].Accessor.Accepted = new Ballot(3, otherServerId);
            _acceptors[2].Accessor.Value = GetConfigWithVersion(3);

            _proposerAccessor.Ballot = new Ballot(2, _proposerServerId);
            var expectedBallot = _proposerAccessor.Ballot.Successor(_proposerServerId);

            var valueVersion2 = GetConfigWithVersion(2);
            var result = await _proposer.TryUpdate(valueVersion2, numRetries: 0, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Success, result.Status);
            Assert.Equal(valueVersion2, result.Value);

            foreach (var (_, accessor, _) in _acceptors.GetRange(0, 2))
            {
                Assert.Equal(valueVersion2, accessor.Value);
                Assert.Equal(expectedBallot, accessor.Accepted);

                // Promise for distinguished leader commit.
                Assert.Equal(expectedBallot.Successor(expectedBallot.Proposer), accessor.Promised);
            }

            // Ok
            _acceptors[0].Accessor.Promised = expectedBallot.Successor(expectedBallot.Proposer);
            _acceptors[0].Accessor.Accepted = new Ballot(2, otherServerId);
            _acceptors[0].Accessor.Value = valueVersion2;

            // Conflict!
            _acceptors[1].Accessor.Promised = new Ballot(7, otherServerId);
            _acceptors[1].Accessor.Accepted = new Ballot(2, otherServerId);
            _acceptors[1].Accessor.Value = valueVersion2;

            _acceptors[2].Accessor.Promised = new Ballot(7, otherServerId);
            _acceptors[2].Accessor.Accepted = new Ballot(2, otherServerId);
            _acceptors[2].Accessor.Value = valueVersion2;

            _proposerAccessor.Ballot = new Ballot(3, _proposerServerId);

            _proposerAccessor.Prepared = Ballot.Zero;
            var valueVersion3 = GetConfigWithVersion(3);
            result = await _proposer.TryUpdate(valueVersion3, numRetries: 0, CancellationToken.None);
            Assert.Equal(ReplicationStatus.Failed, result.Status);
            Assert.Equal(valueVersion2, result.Value);
        }

        /*
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
        */
    }
}
