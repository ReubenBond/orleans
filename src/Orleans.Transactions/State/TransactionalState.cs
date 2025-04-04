#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions;

/// <summary>
/// Stateful facet that respects Orleans transaction semantics
/// </summary>
public sealed class TransactionalState<TState> : ITransactionalState<TState>, ILifecycleParticipant<IGrainLifecycle>
    where TState : class, new()
{
    private readonly TransactionalStateConfiguration _config;
    private readonly IGrainContext _context;
    private readonly ITransactionDataCopier<TState> _copier;
    private readonly Dictionary<Type,object> _copiers;
    private readonly ILogger _logger;
    private readonly ParticipantId _participantId;
    private readonly TransactionQueue<TState> _queue;
    private bool _detectReentrancy;

    public TransactionalState(
        TransactionalStateConfiguration transactionalStateConfiguration, 
        IGrainContextAccessor contextAccessor, 
        ITransactionDataCopier<TState> copier,
        ILogger<TransactionalState<TState>> logger,
        IOptions<TransactionalStateOptions> options,
        TimeProvider timeProvider)
    {
        _config = transactionalStateConfiguration;
        _context = contextAccessor.GrainContext;
        _copier = copier;
        _logger = logger;
        _copiers = new Dictionary<Type, object>
        {
            { typeof(TState), copier }
        };

        _participantId = new ParticipantId(_config.StateName, _context.GrainReference, _config.SupportedRoles);

        var storageFactory = _context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
        ITransactionalStateStorage<TState> storage = storageFactory.Create<TState>(_config.StorageName, _config.StateName);

        // setup transaction processing pipe
        _queue = new TransactionQueue<TState>(options, _participantId, _context, storage, timeProvider, logger);
    }

    /// <summary>
    /// Read the current state.
    /// </summary>
    public Task<TResult> PerformRead<TResult>(Func<TState, TResult> operation)
    {
        if (_detectReentrancy)
        {
            throw new LockRecursionException("Cannot perform a read operation from within another operation");
        }

        var info = TransactionContext.GetRequiredTransactionInfo();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("StartRead {Info}", info);

        info.Participants.TryGetValue(_participantId, out var recordedaccesses);

        // schedule read access to happen under the lock
        return _queue!.RWLock.EnterLock<TResult>(info.TransactionId, info.Priority, recordedaccesses, true,
             () =>
             {
                 // check if our record is gone because we expired while waiting
                 if (!_queue.RWLock.TryGetRecord(info.TransactionId, out TransactionRecord<TState> record))
                 {
                     throw new OrleansCascadingAbortException(info.TransactionId.ToString());
                 }

                 // merge the current clock into the transaction time stamp
                 record.Timestamp = _queue.Clock.MergeUtcNow(info.TimeStamp);

                 if (record.State == null)
                 {
                     _queue.GetMostRecentState(out record.State, out record.SequenceNumber);
                 }

                 if (_logger.IsEnabled(LogLevel.Debug))
                     _logger.LogDebug("Update-lock read v{SequenceNumber} {TransactionId} {Timestamp}", record.SequenceNumber, record.TransactionId, record.Timestamp.ToString("o"));

                 // record this read in the transaction info data structure
                 info.RecordRead(_participantId, record.Timestamp);

                 // perform the read 
                 TResult? result = default;
                 try
                 {
                     _detectReentrancy = true;

                     result = CopyResult(operation(record.State));
                 }
                 finally
                 {
                     if (_logger.IsEnabled(LogLevel.Trace))
                         _logger.LogTrace("EndRead {Info} {Result} {State}", info, result, record.State);

                     _detectReentrancy = false;
                 }

                 return result;
             });
    }

    /// <inheritdoc/>
    public Task<TResult> PerformUpdate<TResult>(Func<TState, TResult> updateAction)
    {
        if (updateAction == null) throw new ArgumentNullException(nameof(updateAction));
        if (_detectReentrancy)
        {
            throw new LockRecursionException("Cannot perform an update operation from within another operation");
        }

        var info = TransactionContext.GetRequiredTransactionInfo();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("StartWrite {Info}", info);

        if (info.IsReadOnly)
        {
            throw new OrleansReadOnlyViolatedException(info.Id);
        }

        info.Participants.TryGetValue(_participantId, out var recordedAccesses);

        return _queue!.RWLock.EnterLock<TResult>(info.TransactionId, info.Priority, recordedAccesses, false,
            () =>
            {
                // check if we expired while waiting
                if (!_queue.RWLock.TryGetRecord(info.TransactionId, out TransactionRecord<TState> record))
                {
                    throw new OrleansCascadingAbortException(info.TransactionId.ToString());
                }

                // merge the current clock into the transaction time stamp
                record.Timestamp = _queue.Clock.MergeUtcNow(info.TimeStamp);

                // link to the latest state
                if (record.State == null)
                {
                    _queue.GetMostRecentState(out record.State, out record.SequenceNumber);
                }

                // if this is the first write, make a deep copy of the state
                if (!record.HasCopiedState)
                {
                    record.State = _copier.DeepCopy(record.State);
                    record.SequenceNumber++;
                    record.HasCopiedState = true;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Update-lock write v{SequenceNumber} {TransactionId} {Timestamp}",
                        record.SequenceNumber,
                        record.TransactionId,
                        record.Timestamp.ToString("o"));
                }

                // record this write in the transaction info data structure
                info.RecordWrite(_participantId, record.Timestamp);

                // perform the write
                try
                {
                    _detectReentrancy = true;

                    return CopyResult(updateAction(record.State));
                }
                finally
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("EndWrite {Info} {TransactionId} {Timestamp}", info, record.TransactionId, record.Timestamp);

                    _detectReentrancy = false;
                }
            }
        );
    }

    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe<TransactionalState<TState>>(GrainLifecycleStage.SetupState, ct => OnSetupState(SetupResourceFactory, ct));
    }

    private static void SetupResourceFactory(IGrainContext context, string stateName, TransactionQueue<TState> queue)
    {
        // Add resources factory to the grain context
        context.RegisterResourceFactory<ITransactionalResource>(stateName, () => new TransactionalResource<TState>(queue));

        // Add tm factory to the grain context
        context.RegisterResourceFactory<ITransactionManager>(stateName, () => new TransactionManager<TState>(queue));
    }

    internal async Task OnSetupState(Action<IGrainContext, string, TransactionQueue<TState>> setupResourceFactory, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        setupResourceFactory(_context, _config.StateName, _queue);

        // recover state
        await _queue.NotifyOfRestore();
    }

    private TResult CopyResult<TResult>(TResult result)
    {
        ITransactionDataCopier<TResult> resultCopier;
        if (!_copiers.TryGetValue(typeof(TResult), out var cp))
        {
            resultCopier = _context.ActivationServices.GetRequiredService<ITransactionDataCopier<TResult>>();
            _copiers.Add(typeof(TResult), resultCopier);
        }
        else
        {
            resultCopier = (ITransactionDataCopier<TResult>)cp;
        }
        return resultCopier.DeepCopy(result);
    }
}
