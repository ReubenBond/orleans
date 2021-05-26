using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using System;
using System.Threading.Tasks;

namespace Orleans.MetadataStore
{
    public interface IConfigAcceptorManager
    {
        ValueTask<ConfigPrepareResponse> Prepare(SiloAddress server, ConfigBallot ballot);
        ValueTask<ConfigAcceptResponse> Accept(SiloAddress server, ConfigBallot ballot, ClusterMembers value);
    }

    public class ConfigAcceptorManager : IConfigAcceptorManager
    {
        private readonly IGrainFactory _grainFactory;
        public ConfigAcceptorManager(IGrainFactory grainFactory) => _grainFactory = grainFactory;

        public ValueTask<ConfigAcceptResponse> Accept(SiloAddress server, ConfigBallot ballot, ClusterMembers value)
        {
            var grain = GetAcceptorReference(server);
            return grain.Accept(ballot, value);
        }

        public ValueTask<ConfigPrepareResponse> Prepare(SiloAddress server, ConfigBallot ballot)
        {
            var grain = GetAcceptorReference(server);
            return grain.Prepare(ballot);
        }

        private IConfigAcceptorGrain GetAcceptorReference(SiloAddress server) => _grainFactory.GetGrain<IConfigAcceptorGrain>(FixedPlacement.CreateGrainId(ConfigAcceptorGrain.GrainType, server));
    }

    [DefaultGrainType(ConfigAcceptorGrain.GrainTypeName)]
    public interface IConfigAcceptorGrain : IGrain
    {
        ValueTask<ConfigPrepareResponse> Prepare(ConfigBallot ballot);
        ValueTask<ConfigAcceptResponse> Accept(ConfigBallot ballot, ClusterMembers value);
    }

    [GrainType(GrainTypeName)]
    [SiloServicePlacement]
    public class ConfigAcceptorGrain : Grain, IConfigAcceptorGrain
    {
        public const string GrainTypeName = "sys.cfg.acceptor";
        public static readonly GrainType GrainType = GrainType.Create(GrainTypeName);

        private readonly ConfigAcceptor _acceptor = new(accepted => { });
        public ValueTask<ConfigPrepareResponse> Prepare(ConfigBallot ballot) => new(_acceptor.Prepare(ballot));
        public ValueTask<ConfigAcceptResponse> Accept(ConfigBallot ballot, ClusterMembers value) => new(_acceptor.Accept(ballot, value));
    }

    public class ConfigAcceptor : ConfigAcceptor.ITestAccessor
    {
        private readonly Action<ClusterMembers> _onAcceptState;
        private ConfigBallot _promised;
        private ConfigBallot _accepted;
        private ClusterMembers _value;
        
        public ConfigAcceptor(Action<ClusterMembers> onAcceptState)
        {
            _onAcceptState = onAcceptState;
        }

        ConfigBallot ITestAccessor.Promised { get => _promised; set => _promised = value; }
        ConfigBallot ITestAccessor.Accepted { get => _accepted; set => _accepted = value; }
        ClusterMembers ITestAccessor.VolatileState { get => _value; set => _value = value; }

        public ConfigPrepareResponse Prepare(ConfigBallot ballot)
        {
            ConfigPrepareResponse result;
            if (_promised > ballot)
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

        public ConfigAcceptResponse Accept(ConfigBallot ballot, ClusterMembers value)
        {
            ConfigAcceptResponse result;
            if (_promised > ballot)
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
                _onAcceptState?.Invoke(_value);
                result = ConfigAcceptResponse.Success();
            }

            return result;
        }

        internal void ForceState(ClusterMembers newState)
        {
            _accepted = ConfigBallot.Zero;
            _promised = ConfigBallot.Zero;
            _value = newState;
        }

        public interface ITestAccessor
        {
            ConfigBallot Promised { get; set; }
            ConfigBallot Accepted { get; set; }
            ClusterMembers VolatileState { get; set; }
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

        public static ConfigPrepareResponse ConfigConflict(ConfigBallot conflicting) => new()
        {
            _status = (byte)PrepareStatus.ConfigConflict,
            Ballot = conflicting,
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
}