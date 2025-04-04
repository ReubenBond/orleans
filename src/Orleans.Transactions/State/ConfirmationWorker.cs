using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State;

internal sealed class ConfirmationWorker<TState>(
    IOptions<TransactionalStateOptions> options,
    ParticipantId me,
    BatchWorker storageWorker,
    Func<StorageBatch<TState>> getStorageBatch,
    ILogger logger,
    ActivationLifetime activationLifetime)
    where TState : class, new()
{
    private readonly TransactionalStateOptions _options = options.Value;
    private readonly ParticipantId _me = me;
    private readonly BatchWorker _storageWorker = storageWorker;
    private readonly Func<StorageBatch<TState>> _getStorageBatch = getStorageBatch;
    private readonly ILogger _logger = logger;
    private readonly ActivationLifetime _activationLifetime = activationLifetime;
    private readonly HashSet<Guid> _pending = [];

    public void Add(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
    {
        if (!IsConfirmed(transactionId))
        {
            _pending.Add(transactionId);
            SendConfirmation(transactionId, timestamp, participants).Ignore();
        }
    }

    public bool IsConfirmed(Guid transactionId)
    {
        return _pending.Contains(transactionId);
    }

    private async Task SendConfirmation(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
    {
        await NotifyAll(transactionId, timestamp, participants);
        await Collect(transactionId);
    }

    private async Task NotifyAll(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
    {
        List<Confirmation> confirmations = participants
                .Where(p => !p.Equals(_me))
                .Select(p => new Confirmation(
                    p,
                    transactionId,
                    timestamp,
                    () => p.Id.AsReference<ITransactionalResourceExtension>()
                        .Confirm(p.Name, transactionId, timestamp),
                    _logger))
                .ToList();

        if (confirmations.Count == 0) return;

        // attempts to confirm all, will retry every ConfirmationRetryDelay until all succeed
        var ct = _activationLifetime.OnDeactivating;

        bool hasPendingConfirmations = true;
        while (!ct.IsCancellationRequested && hasPendingConfirmations)
        {
            using var _ = _activationLifetime.BlockDeactivation();
            var confirmationResults = await Task.WhenAll(confirmations.Select(c => c.Confirmed()));
            hasPendingConfirmations = false;
            foreach (var confirmed in confirmationResults)
            {
                if (!confirmed)
                {
                    hasPendingConfirmations = true;
                    await Task.Delay(_options.ConfirmationRetryDelay, ct);
                    break;
                }
            }
        }
    }

    // retries collect until it succeeds
    private async Task Collect(Guid transactionId)
    {
        var ct = _activationLifetime.OnDeactivating;
        while (!ct.IsCancellationRequested)
        {
            using var _ = _activationLifetime.BlockDeactivation();
            if (await TryCollect(transactionId))
            {
                break;
            }

            await Task.Delay(_options.ConfirmationRetryDelay, ct);
        }
    }

    // attempt to clear transaction from commit log
    private async Task<bool> TryCollect(Guid transactionId)
    {
        try
        {
            var storeComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Now we can remove the commit record.
            StorageBatch<TState> storageBatch = _getStorageBatch();
            storageBatch.Collect(transactionId);
            storageBatch.FollowUpAction(() =>
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Collection completed. TransactionId:{TransactionId}", transactionId);
                }
                _pending.Remove(transactionId);
                storeComplete.TrySetResult(true);
            });

            _storageWorker.Notify();

            // wait for storage call, so we don't free spin
            return await storeComplete.Task;
        }
        catch(Exception ex)
        {
            _logger.LogWarning(ex, "Error occurred while cleaning up transaction {TransactionId} from commit log.  Will retry.", transactionId);
        }

        return false;
    }

    // Tracks the effort to notify a participant, will not call again once it succeeds.
    private struct Confirmation(
        ParticipantId participant,
        Guid transactionId,
        DateTime timestamp,
        Func<Task> call,
        ILogger logger)
    {
        private readonly ILogger _logger = logger;
        private Task _pending = null;
        private bool _complete = false;

        public async Task<bool> Confirmed()
        {
            if (_complete)
            {
                return _complete;
            }

            _pending = _pending ?? call();

            try
            {
                await _pending;
                _complete = true;
            }
            catch (Exception ex)
            {
                _pending = null;
                _logger.LogWarning(ex, "Confirmation of transaction {TransactionId} with timestamp {Timestamp} to participant {Participant} failed.  Retrying", transactionId, timestamp, participant);
            }

            return _complete;
        }
    }
}
