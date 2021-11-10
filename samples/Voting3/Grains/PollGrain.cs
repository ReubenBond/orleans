using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grains;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using VotingContract;

namespace VotingData
{
    public class PollGrain : Grain, IPollGrain
    {
        private readonly ILogger _logger;
        private readonly IPersistentState<PollState> _votes;

        public PollGrain(
            [PersistentState("votes", storageName: "votes")] IPersistentState<PollState> state,
            ILogger<PollGrain> logger)
        {
            _logger = logger;
            _votes = state;
        }

        public async Task CreatePoll(PollState initialState)
        {
            _votes.State = initialState;
            await _votes.WriteStateAsync();
        }

        public Task<PollState> GetCurrentResults() => Task.FromResult(_votes.State);

        public async Task<PollState> AddVote(int optionId)
        {
            var options = _votes.State.Options;
            if (optionId < 0 || optionId >= options.Count)
            {
                _logger.LogWarning("Invalid option {Option}", optionId);
                throw new KeyNotFoundException($"Invalid option {optionId}");
            }

            var (optionName, optionVotes) = options[optionId];
            options[optionId] = (optionName, optionVotes + 1);
            await _votes.WriteStateAsync();

            _logger.LogInformation("Added vote to option {Option}", optionId);

            // Notify the watchers.
            _pollWatchers.Notify(watcher => watcher.OnPollUpdated(_votes.State));

            return _votes.State;
        }

        private readonly ObserverManager<IPollWatcher> _pollWatchers = new(TimeSpan.FromMinutes(1));

        public Task StartWatching(IPollWatcher watcher)
        {
            _pollWatchers.Subscribe(watcher);
            return Task.CompletedTask;
        }

        public Task StopWatching(IPollWatcher watcher)
        {
            _pollWatchers.Unsubscribe(watcher);
            return Task.CompletedTask;
        }
    }
}