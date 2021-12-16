using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace BackCompatGrains.V1
{
    public interface IBasicStatefulGrain : IGrainWithGuidKey
    {
        Task SetState(BasicGrainState state);
        Task<BasicGrainState> GetState();
    }

    public class BasicStatefulGrain : Grain<BasicGrainState>, IBasicStatefulGrain
    {
        public Task<BasicGrainState> GetState() => Task.FromResult(State);

        public Task SetState(BasicGrainState state)
        {
            State = state;
            return WriteStateAsync();
        }
    }

    [Serializable]
    public class BasicGrainState
    {
        public int Int { get; set; }

        public List<object> Objects { get; set; }
    }
}
