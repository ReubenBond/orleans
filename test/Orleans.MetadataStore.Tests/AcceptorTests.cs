using Orleans.Runtime;
using System;
using System.Data;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.MetadataStore.Tests
{
    public class LocalConfiguration : ILocalConfiguration
    {
        private readonly object _lock = new();

        public ClusterConfiguration CommittedConfiguration { get; set; }

        public void OnCommittedConfiguration(ClusterConfiguration state)
        {
            lock (_lock)
            {
                if (CommittedConfiguration is null || CommittedConfiguration.Version < state.Version)
                {
                    CommittedConfiguration = state;
                }
            }
        }
    }

    [Trait("Category", "BVT"), Trait("Category", "MetadataStore")]
    public class AcceptorTests
    {
        private readonly LocalConfiguration _localConfig;
        private readonly ConfigAcceptor _acceptor;
        private readonly ConfigAcceptor.ITestAccessor _testAccessor;
        private readonly ClusterConfiguration[] _committedConfigs;

        private static SiloAddress Silo(int num) => SiloAddress.FromParsableString($"127.0.0.1:{num}@1");

        public AcceptorTests(ITestOutputHelper output)
        {
            _committedConfigs = new[]
            {
                new ClusterConfiguration(new Ballot(1, Guid.NewGuid()), new MembershipVersion(1), new[] { Silo(1), Silo(2), Silo(3) }),
                new ClusterConfiguration(new Ballot(2, Guid.NewGuid()), new MembershipVersion(2), new[] { Silo(1), Silo(2), Silo(3), Silo(4) }),
                new ClusterConfiguration(new Ballot(3, Guid.NewGuid()), new MembershipVersion(2), new[] { Silo(1), Silo(2), Silo(3), Silo(4), Silo(5) }),
            };

            _localConfig = new LocalConfiguration
            {
                CommittedConfiguration = _committedConfigs[1]
            };

            _acceptor = new ConfigAcceptor(_localConfig);
            _testAccessor = _acceptor;
        }

        [Fact]
        public void PrepareRejectsSupersededParentBallot()
        {
            _testAccessor.Value = _committedConfigs[2];
            _localConfig.CommittedConfiguration = _committedConfigs[2];

            var response = _acceptor.Prepare(_committedConfigs[1].Stamp, new Ballot(10, Guid.NewGuid()));
            Assert.Equal(PrepareStatus.ConfigConflict, response.Status);
            Assert.Equal(response.Ballot, _testAccessor.Value.Stamp);

            // No change
            Assert.Equal(Ballot.Zero, _testAccessor.Promised);
            Assert.Equal(Ballot.Zero, _testAccessor.Accepted);
            Assert.Equal(_committedConfigs[2], _testAccessor.Value);
        }

        [Fact]
        public void PrepareRejectsSupersededBallot()
        {
            var committedBallot = _localConfig.CommittedConfiguration.Stamp;
            var acceptedBallot = new Ballot(3, Guid.NewGuid());
            var supersededBallot = new Ballot(2, Guid.NewGuid());
            var initialValue = _testAccessor.Value;

            // Set an accepted ballot
            _testAccessor.Accepted = acceptedBallot;
            var response = _acceptor.Prepare(proposerConfig: committedBallot, ballot: supersededBallot);
            Assert.Equal(PrepareStatus.Conflict, response.Status);
            Assert.Equal(response.Ballot, acceptedBallot);

            // No change
            Assert.Equal(Ballot.Zero, _testAccessor.Promised);
            Assert.Equal(acceptedBallot, _testAccessor.Accepted);
            Assert.Equal(initialValue, _testAccessor.Value);

            // Set a promised ballot instead
            _testAccessor.Promised = _testAccessor.Accepted;
            _testAccessor.Accepted = Ballot.Zero;
            response = _acceptor.Prepare(proposerConfig: committedBallot, ballot: supersededBallot);
            Assert.Equal(PrepareStatus.Conflict, response.Status);
            Assert.Equal(response.Ballot, acceptedBallot);

            // No change
            Assert.Equal(acceptedBallot, _testAccessor.Promised);
            Assert.Equal(Ballot.Zero, _testAccessor.Accepted);
            Assert.Equal(initialValue, _testAccessor.Value);
        }

        [Fact]
        public void PrepareAcceptsSupersedingBallot()
        {
            var committedBallot = _localConfig.CommittedConfiguration.Stamp;
            var committedValue = _localConfig.CommittedConfiguration;
            _testAccessor.Accepted = committedValue.Stamp;
            _testAccessor.Value = committedValue;

            var proposedBallot = new Ballot(4, Guid.NewGuid());
            var response = _acceptor.Prepare(committedBallot, ballot: proposedBallot);
            Assert.Equal(PrepareStatus.Success, response.Status);
            Assert.Equal(committedValue, response.Value);
            Assert.Equal(committedValue.Stamp, response.Ballot);
            Assert.Equal(proposedBallot, _testAccessor.Promised);
        }

        [Fact]
        public void AcceptRejectsSupersededParentBallot()
        {
            var promisedBallot = _testAccessor.Promised = new Ballot(2, Guid.NewGuid());
            var committedBallot = _localConfig.CommittedConfiguration.Stamp;
            var committedValue = _localConfig.CommittedConfiguration;
            _testAccessor.Accepted = committedValue.Stamp;
            _testAccessor.Value = committedValue;

            // The acceptor has a higher parent ballot than the proposer
            var response = _acceptor.Accept(proposerConfig: _committedConfigs[0].Stamp, ballot: committedBallot.Successor(Guid.NewGuid()), null, default);
            Assert.Equal(AcceptStatus.ConfigConflict, response.Status);
            Assert.Equal(committedBallot, response.Ballot);

            // No change
            Assert.Equal(promisedBallot, _testAccessor.Promised);
            Assert.Equal(committedBallot, _testAccessor.Accepted);
            Assert.Equal(committedValue, _testAccessor.Value);
        }

        [Fact]
        public void AcceptRejectsSupersededBallot()
        {
            _testAccessor.Promised = Ballot.Zero;
            var acceptedBallot = new Ballot(10, Guid.NewGuid());
            _testAccessor.Accepted = acceptedBallot;
            _testAccessor.Value = _committedConfigs[1];

            // The acceptor has a higher accepted ballot than the proposer, which results in a rejection.
            var response = _acceptor.Accept(proposerConfig: _committedConfigs[1].Stamp, ballot: new Ballot(9, Guid.NewGuid()), _committedConfigs[2], default);
            Assert.Equal(AcceptStatus.Conflict, response.Status);
            Assert.Equal(acceptedBallot, response.Ballot);

            // No change
            Assert.Equal(Ballot.Zero, _testAccessor.Promised);
            Assert.Equal(acceptedBallot, _testAccessor.Accepted);
            Assert.Equal(_committedConfigs[1], _testAccessor.Value);

            // The acceptor has a higher promised ballot than the proposer, which results in a rejection.
            var promisedBallot = acceptedBallot.Successor(Guid.NewGuid());
            _testAccessor.Promised = promisedBallot;
            response = _acceptor.Accept(proposerConfig: _committedConfigs[1].Stamp, ballot: new Ballot(9, Guid.NewGuid()), _committedConfigs[2], default);
            Assert.Equal(AcceptStatus.Conflict, response.Status);
            Assert.Equal(promisedBallot, response.Ballot);

            // No change
            Assert.Equal(promisedBallot, _testAccessor.Promised);
            Assert.Equal(acceptedBallot, _testAccessor.Accepted);
            Assert.Equal(_committedConfigs[1], _testAccessor.Value);
        }

        [Fact]
        public void AcceptAcceptsPromisedBallot()
        {
            var committedBallot = _localConfig.CommittedConfiguration.Stamp;
            var promisedBallot = new Ballot(3, Guid.NewGuid());
            _testAccessor.Promised = promisedBallot;

            var response = _acceptor.Accept(committedBallot, promisedBallot, _committedConfigs[2], default);
            Assert.Equal(AcceptStatus.Success, response.Status);

            // The promised ballot is incremented to support the distinguished proposer optimization.
            // i.e, the next Prepare call is piggy-backed onto each Accept call.
            Assert.Equal(promisedBallot, _testAccessor.Promised);

            Assert.Equal(promisedBallot, _testAccessor.Accepted);
            Assert.Equal(_committedConfigs[2], _testAccessor.Value);
        }
    }
}
