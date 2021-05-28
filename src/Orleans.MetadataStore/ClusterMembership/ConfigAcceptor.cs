using Orleans.Concurrency;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using System;
using System.Threading.Tasks;

namespace Orleans.MetadataStore
{
    public interface IConfigurationManagerMediator
    {
        ValueTask<ConfigPrepareResponse> Prepare(SiloAddress server, ConfigBallot proposerConfig, ConfigBallot ballot);
        ValueTask<ConfigAcceptResponse> Accept(SiloAddress server, ConfigBallot proposerConfig, ConfigBallot ballot, ClusterMembers value);
        ValueTask Learn(SiloAddress server, ClusterMembers value);
    }

    public class ConfigurationManagerMediator : IConfigurationManagerMediator
    {
        private readonly IGrainFactory _grainFactory;
        public ConfigurationManagerMediator(IGrainFactory grainFactory) => _grainFactory = grainFactory;

        public ValueTask<ConfigAcceptResponse> Accept(SiloAddress server, ConfigBallot proposerConfig, ConfigBallot ballot, ClusterMembers value)
        {
            var grain = GetConfigurationManagerReference(server);
            return grain.Accept(proposerConfig, ballot, value);
        }

        public ValueTask<ConfigPrepareResponse> Prepare(SiloAddress server, ConfigBallot proposerConfig, ConfigBallot ballot)
        {
            var grain = GetConfigurationManagerReference(server);
            return grain.Prepare(proposerConfig, ballot);
        }

        public ValueTask Learn(SiloAddress server, ClusterMembers value)
        {
            var grain = GetConfigurationManagerReference(server);
            return grain.Learn(value);
        }

        private IConfigurationManagerGrain GetConfigurationManagerReference(SiloAddress server) => _grainFactory.GetGrain<IConfigurationManagerGrain>(FixedPlacement.CreateGrainId(ConfigurationManagerGrain.GrainType, server));
    }

    [DefaultGrainType(ConfigurationManagerGrain.GrainTypeName)]
    public interface IConfigurationManagerGrain : IGrain
    {
        ValueTask<ConfigPrepareResponse> Prepare(ConfigBallot proposerConfig, ConfigBallot ballot);
        ValueTask<ConfigAcceptResponse> Accept(ConfigBallot proposerConfig, ConfigBallot ballot, ClusterMembers value);
        ValueTask Learn(ClusterMembers value);
        ValueTask<ClusterMembers> GetCommittedConfiguration();
    }

    [GrainType(GrainTypeName)]
    [SiloServicePlacement]
    internal class ConfigurationManagerGrain : Grain, IConfigurationManagerGrain
    {
        public const string GrainTypeName = "sys.membership";
        public static readonly GrainType GrainType = GrainType.Create(GrainTypeName);
        private readonly IConfigurationManager _configurationManager;

        public ConfigurationManagerGrain(IConfigurationManager configurationManager)
        {
            _configurationManager = configurationManager;
        }

        public ValueTask<ConfigPrepareResponse> Prepare(ConfigBallot proposerConfig, ConfigBallot ballot) => new(_configurationManager.Acceptor.Prepare(proposerConfig, ballot));

        public ValueTask<ConfigAcceptResponse> Accept(ConfigBallot proposerConfig, ConfigBallot ballot, ClusterMembers value) => new(_configurationManager.Acceptor.Accept(proposerConfig, ballot, value));

        public ValueTask Learn(ClusterMembers value)
        {
            _configurationManager.OnCommittedConfiguration(value);
            return default;
        }

        public ValueTask<ClusterMembers> GetCommittedConfiguration() => new(_configurationManager.CommittedConfiguration);
    }

    public class ConfigAcceptor : ConfigAcceptor.ITestAccessor
    {
        private ConfigBallot _promised;
        private ConfigBallot _accepted;
        private ClusterMembers _value;

        ConfigBallot ITestAccessor.Promised { get => _promised; set => _promised = value; }
        ConfigBallot ITestAccessor.Accepted { get => _accepted; set => _accepted = value; }
        ClusterMembers ITestAccessor.Value { get => _value; set => _value = value; }

        public ConfigPrepareResponse Prepare(ConfigBallot proposerConfig, ConfigBallot ballot)
        {
            lock (this)
            {
                ConfigPrepareResponse result;
                if (_value is { } acceptedConfiguration && proposerConfig < acceptedConfiguration.Stamp)
                {
                    // If this acceptor has already accepted a configuration with a higher stamp than the proper's, reject this.
                    // This passivates proposers which are operating on out-dated configurations, ensuring that they are not able to commit
                    // values when they may not know of the most recent quorum configuration.
                    result = ConfigPrepareResponse.ConfigConflict(acceptedConfiguration);
                }
                else if (_promised > ballot)
                {
                    // If a Prepare with a higher ballot has already been encountered, reject this.
                    result = ConfigPrepareResponse.Conflict(_promised);
                }
                else if (_accepted > ballot)
                {
                    // If an Accept with a higher ballot has already been encountered, reject this.
                    result = ConfigPrepareResponse.Conflict(_accepted);
                }
                else
                {
                    // Record a tentative promise to accept this proposer's value.
                    _promised = ballot;
                    result = ConfigPrepareResponse.Success(_accepted, _value);
                }

                return result;
            }
        }

        public ConfigAcceptResponse Accept(ConfigBallot proposerConfig, ConfigBallot ballot, ClusterMembers value)
        {
            lock (this)
            {
                ConfigAcceptResponse result;

                if (_value is { } acceptedConfiguration && proposerConfig < acceptedConfiguration.Stamp)
                {
                    // If this acceptor has already accepted a configuration with a higher stamp than the proper's, reject this.
                    // This passivates proposers which are operating on out-dated configurations, ensuring that they are not able to commit
                    // values when they may not know of the most recent quorum configuration.
                    result = ConfigAcceptResponse.ConfigConflict(acceptedConfiguration.Stamp);
                }
                else if (_promised > ballot)
                {
                    // If a Prepare with a higher ballot has already been encountered, reject this.
                    result = ConfigAcceptResponse.Conflict(_promised);
                }
                else if (_accepted > ballot)
                {
                    // If an Accept with a higher ballot has already been encountered, reject this.
                    result = ConfigAcceptResponse.Conflict(_accepted);
                }
                else
                {
                    // Record the new state.
                    _promised = ballot;
                    _accepted = ballot;
                    _value = value;
                    result = ConfigAcceptResponse.Success();
                }

                return result;
            }
        }

        internal void OnCommittedConfiguration(ClusterMembers newState)
        {
            if (newState is null)
            {
                throw new ArgumentNullException(nameof(newState));
            }

            lock (this)
            {
                if (_value is null || _value is { } acceptedConfiguration && acceptedConfiguration.Stamp < newState.Stamp)
                {
                    _accepted = newState.Stamp;
                    _promised = newState.Stamp;
                    _value = newState;
                }
            }
        }

        internal void ForceState(ClusterMembers newState)
        {
            lock (this)
            {
                _accepted = ConfigBallot.Zero;
                _promised = ConfigBallot.Zero;
                _value = newState;
            }
        }

        public interface ITestAccessor
        {
            ConfigBallot Promised { get; set; }
            ConfigBallot Accepted { get; set; }
            ClusterMembers Value { get; set; }
        }
    }

    [Immutable]
    [GenerateSerializer]
    public struct ConfigPrepareResponse
    {
        [Id(0)]
        public byte _status;

        public PrepareStatus Status => (PrepareStatus)_status;

        [Id(1)]
        public ConfigBallot Ballot;

        [Id(2)]
        public ClusterMembers Value;

        public static ConfigPrepareResponse Success(ConfigBallot accepted, ClusterMembers value) => new()
        {
            _status = (byte)PrepareStatus.Success,
            Ballot = accepted,
            Value = value,
        };

        public static ConfigPrepareResponse Conflict(ConfigBallot conflicting) => new() 
        {
            _status = (byte)PrepareStatus.Conflict,
            Ballot = conflicting,
        };

        public static ConfigPrepareResponse ConfigConflict(ClusterMembers value) => new()
        {
            _status = (byte)PrepareStatus.ConfigConflict,
            Ballot = value.Stamp,
            Value = value,
        };

        public void Deconstruct(out PrepareStatus status, out ConfigBallot accepted, out ClusterMembers value)
        {
            status = Status;
            accepted = Ballot;
            value = Value;
        }

        public void Deconstruct(out PrepareStatus status, out ConfigBallot conflict)
        {
            status = Status;
            conflict = Ballot;
        }
    }

    [Immutable]
    [GenerateSerializer]
    public struct ConfigAcceptResponse
    {
        [Id(0)]
        public byte _status;

        public AcceptStatus Status => (AcceptStatus)_status;

        [Id(1)]
        public ConfigBallot Ballot;

        public static ConfigAcceptResponse Success() => new()
        {
            _status = (byte)AcceptStatus.Success,
        };

        public static ConfigAcceptResponse Conflict(ConfigBallot conflicting) => new() 
        {
            _status = (byte)AcceptStatus.Conflict,
            Ballot = conflicting,
        };

        public static ConfigAcceptResponse ConfigConflict(ConfigBallot conflicting) => new()
        {
            _status = (byte)AcceptStatus.ConfigConflict,
            Ballot = conflicting,
        };

        public void Deconstruct(out AcceptStatus status, out ConfigBallot conflict)
        {
            status = (AcceptStatus)_status;
            conflict = Ballot;
        }

        public void Deconstruct(out AcceptStatus status)
        {
            status = (AcceptStatus)_status;
        }
    }

    [GenerateSerializer]
    public enum PrepareStatus : byte
    {
        Unknown = 0,
        Conflict = 1,
        ConfigConflict = 2,
        Success = 3
    }

    [GenerateSerializer]
    public enum AcceptStatus : byte
    {
        Unknown = 0,
        Conflict = 1,
        ConfigConflict = 2,
        Success = 3
    }
}