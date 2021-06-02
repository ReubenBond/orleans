using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using System.Threading.Tasks;

namespace Orleans.MetadataStore
{
    public interface IAcceptorRouter<TValue>
    {
        ValueTask<PrepareResponse<TValue>> Prepare(SiloAddress server, Ballot proposerConfig, Ballot ballot);
        ValueTask<AcceptResponse> Accept(SiloAddress server, Ballot proposerConfig, Ballot ballot, TValue value);
    }

    public interface ILearnerRouter<TValue>
    {
        /// <summary>
        /// Informs the specified server that a value was committed.
        /// </summary>
        ValueTask Committed(SiloAddress server, TValue value);
    }

    public class ConfigurationManagerRouter : IAcceptorRouter<ClusterConfiguration>, ILearnerRouter<ClusterConfiguration>
    {
        private readonly IGrainFactory _grainFactory;
        public ConfigurationManagerRouter(IGrainFactory grainFactory) => _grainFactory = grainFactory;

        public ValueTask<AcceptResponse> Accept(SiloAddress server, Ballot proposerConfig, Ballot ballot, ClusterConfiguration value)
        {
            var grain = GetConfigurationManagerReference(server);
            return grain.Accept(proposerConfig, ballot, value);
        }

        public ValueTask<PrepareResponse<ClusterConfiguration>> Prepare(SiloAddress server, Ballot proposerConfig, Ballot ballot)
        {
            var grain = GetConfigurationManagerReference(server);
            return grain.Prepare(proposerConfig, ballot);
        }

        public ValueTask Committed(SiloAddress server, ClusterConfiguration value)
        {
            var grain = GetConfigurationManagerReference(server);
            return grain.Committed(value);
        }

        private IConfigurationManagerGrain GetConfigurationManagerReference(SiloAddress server) =>
            _grainFactory.GetGrain<IConfigurationManagerGrain>(FixedPlacement.CreateGrainId(ConfigurationManagerGrain.GrainType, server));
    }

    [DefaultGrainType(ConfigurationManagerGrain.GrainTypeName)]
    public interface IConfigurationManagerGrain : IGrain
    {
        ValueTask<PrepareResponse<ClusterConfiguration>> Prepare(Ballot proposerConfig, Ballot ballot);
        ValueTask<AcceptResponse> Accept(Ballot proposerConfig, Ballot ballot, ClusterConfiguration value);
        ValueTask Committed(ClusterConfiguration value);
        ValueTask<ClusterConfiguration> GetCommittedConfiguration();
    }

    [GrainType(GrainTypeName)]
    [SiloServicePlacement]
    internal class ConfigurationManagerGrain : Grain, IConfigurationManagerGrain
    {
        public const string GrainTypeName = "sys.membership";
        public static readonly GrainType GrainType = GrainType.Create(GrainTypeName);
        private readonly IInternalConfigurationManager _configurationManager;
        private readonly ILocalConfiguration _localConfiguration;

        public ConfigurationManagerGrain(IInternalConfigurationManager configurationManager, ILocalConfiguration localConfiguration)
        {
            _configurationManager = configurationManager;
            _localConfiguration = localConfiguration;
        }

        public ValueTask<PrepareResponse<ClusterConfiguration>> Prepare(Ballot proposerConfig, Ballot ballot) => new(_configurationManager.Acceptor.Prepare(proposerConfig, ballot));

        public ValueTask<AcceptResponse> Accept(Ballot proposerConfig, Ballot ballot, ClusterConfiguration value) => new(_configurationManager.Acceptor.Accept(proposerConfig, ballot, value));

        public ValueTask Committed(ClusterConfiguration value)
        {
            _localConfiguration.OnCommittedConfiguration(value);
            return default;
        }

        public ValueTask<ClusterConfiguration> GetCommittedConfiguration() => new(_localConfiguration.CommittedConfiguration);
    }

    public struct AcceptorRegister<TValue>
    {
        public Ballot PromisedBallot { get; set; }
        public Ballot AcceptedBallot { get; set; }
        public TValue AcceptedValue { get; set; }

        public PrepareResponse<TValue> Prepare(Ballot ballot)
        {
            if (PromisedBallot > ballot)
            {
                // If a Prepare with a higher ballot has already been encountered, reject this.
                return PrepareResponse<TValue>.Conflict(PromisedBallot);
            }

            if (AcceptedBallot > ballot)
            {
                // If an Accept with a higher ballot has already been encountered, reject this.
                return PrepareResponse<TValue>.Conflict(AcceptedBallot);
            }

            // Record a tentative promise to accept this proposer's value.
            PromisedBallot = ballot;
            return PrepareResponse<TValue>.Success(AcceptedBallot, AcceptedValue);
        }

        public AcceptResponse Accept(Ballot ballot, TValue value)
        {
            if (PromisedBallot > ballot)
            {
                // If a Prepare with a higher ballot has already been encountered, reject this.
                return AcceptResponse.Conflict(PromisedBallot);
            }

            if (AcceptedBallot > ballot)
            {
                // If an Accept with a higher ballot has already been encountered, reject this.
                return AcceptResponse.Conflict(AcceptedBallot);
            }

            // Record the new state.
            PromisedBallot = ballot;
            AcceptedBallot = ballot;
            AcceptedValue = value;
            return AcceptResponse.Success();
        }
    }

    public sealed class ConfigAcceptor : ConfigAcceptor.ITestAccessor
    {
        private readonly ILocalConfiguration _localConfiguration;
        private AcceptorRegister<ClusterConfiguration> _register;

        public ConfigAcceptor(ILocalConfiguration localConfiguration)
        {
            _localConfiguration = localConfiguration;
        }

        Ballot ITestAccessor.Promised { get => _register.PromisedBallot; set => _register.PromisedBallot = value; }
        Ballot ITestAccessor.Accepted { get => _register.AcceptedBallot; set => _register.AcceptedBallot = value; }
        ClusterConfiguration ITestAccessor.Value { get => _register.AcceptedValue; set => _register.AcceptedValue = value; }

        public PrepareResponse<ClusterConfiguration> Prepare(Ballot proposerConfig, Ballot ballot)
        {
            lock (this)
            {
                var activeConfiguration = GetActiveConfiguration();
                if (activeConfiguration is not null && proposerConfig < activeConfiguration.Stamp)
                {
                    // If this acceptor has already accepted a configuration with a higher stamp than the proper's, reject this.
                    // This passivates proposers which are operating on out-dated configurations, ensuring that they are not able to commit
                    // values when they may not know of the most recent quorum configuration.
                    return PrepareResponse<ClusterConfiguration>.ConfigConflict(activeConfiguration.Stamp, activeConfiguration);
                }

                return _register.Prepare(ballot);
            }
        }


        public AcceptResponse Accept(Ballot proposerConfig, Ballot ballot, ClusterConfiguration value)
        {
            lock (this)
            {
                var activeConfiguration = GetActiveConfiguration();
                if (activeConfiguration is not null && proposerConfig < activeConfiguration.Stamp)
                {
                    // If this acceptor has already accepted a configuration with a higher stamp than the proper's, reject this.
                    // This passivates proposers which are operating on out-dated configurations, ensuring that they are not able to commit
                    // values when they may not know of the most recent quorum configuration.
                    return AcceptResponse.ConfigConflict(activeConfiguration.Stamp);
                }

                return _register.Accept(ballot, value);
            }
        }

        internal void ForceState(ClusterConfiguration newState)
        {
            lock (this)
            {
                _register.PromisedBallot = newState?.Stamp ?? Ballot.Zero;
                _register.AcceptedBallot = newState?.Stamp ?? Ballot.Zero;
                _register.AcceptedValue = newState;
            }
        }

        private ClusterConfiguration GetActiveConfiguration()
        {
            var committed = _localConfiguration.CommittedConfiguration;
            var accepted = _register.AcceptedValue;
            return (committed, accepted) switch
            {
                (not null, not null) when committed.Stamp >= accepted.Stamp => committed,
                _ => accepted ?? committed,
            };
        }

        public interface ITestAccessor
        {
            Ballot Promised { get; set; }
            Ballot Accepted { get; set; }
            ClusterConfiguration Value { get; set; }
        }
    }

    [Immutable]
    [GenerateSerializer]
    public struct PrepareResponse<TValue>
    {
        [Id(0)]
        public byte _status;

        public PrepareStatus Status => (PrepareStatus)_status;

        [Id(1)]
        public Ballot Ballot;

        [Id(2)]
        public TValue Value;

        public static PrepareResponse<TValue> Success(Ballot accepted, TValue value) => new()
        {
            _status = (byte)PrepareStatus.Success,
            Ballot = accepted,
            Value = value,
        };

        public static PrepareResponse<TValue> Conflict(Ballot conflicting) => new() 
        {
            _status = (byte)PrepareStatus.Conflict,
            Ballot = conflicting,
        };

        public static PrepareResponse<TValue> ConfigConflict(Ballot conflicting, TValue value) => new()
        {
            _status = (byte)PrepareStatus.ConfigConflict,
            Ballot = conflicting,
            Value = value,
        };

        public void Deconstruct(out PrepareStatus status, out Ballot accepted, out TValue value)
        {
            status = Status;
            accepted = Ballot;
            value = Value;
        }

        public void Deconstruct(out PrepareStatus status, out Ballot conflict)
        {
            status = Status;
            conflict = Ballot;
        }
    }

    [Immutable]
    [GenerateSerializer]
    public struct AcceptResponse
    {
        [Id(0)]
        public byte _status;

        public AcceptStatus Status => (AcceptStatus)_status;

        [Id(1)]
        public Ballot Ballot;

        public static AcceptResponse Success() => new()
        {
            _status = (byte)AcceptStatus.Success,
        };

        public static AcceptResponse Conflict(Ballot conflicting) => new() 
        {
            _status = (byte)AcceptStatus.Conflict,
            Ballot = conflicting,
        };

        public static AcceptResponse ConfigConflict(Ballot conflicting) => new()
        {
            _status = (byte)AcceptStatus.ConfigConflict,
            Ballot = conflicting,
        };

        public void Deconstruct(out AcceptStatus status, out Ballot conflict)
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