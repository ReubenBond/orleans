using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using VotingContract;

namespace Frontend.Data
{
    public class PollService
    {
        private IGrainFactory _grainFactory;
        public PollService(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory;
        }

        public async Task<string> CreatePollAsync(string question, List<string> options)
        {
            var pollId = Guid.NewGuid().ToString("N").Substring(0, 6);
            var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);
            var pollState = new PollState
            {
                Question = question,
                Options = options.Select(o => (o, 0)).ToList()
            };
            await pollGrain.CreatePoll(pollState);
            return pollId;
        }

        public async Task<PollState> GetPollAsync(string pollId)
        {
            var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);
            var result = await pollGrain.Get();
            return result;
        }

        public async Task<PollState> VoteForOption(string pollId, int index)
        {
            var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);
            var updatedState = await pollGrain.AddVote(index);
            return updatedState;
        }
    }
}