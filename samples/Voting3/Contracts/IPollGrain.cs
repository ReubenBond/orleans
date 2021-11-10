using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace VotingContract
{
    public interface IPollGrain : IGrainWithStringKey
    {
        Task CreatePoll(PollState initialState);
        Task<PollState> Get();
        Task<PollState> AddVote(int optionId);
        Task<PollState> RemoveVote(int optionId);
    }

    [Serializable]
    public class PollState
    {
        public string Question { get; set; }
        public List<(string Option, int Votes)> Options { get; set; }
    }
}
