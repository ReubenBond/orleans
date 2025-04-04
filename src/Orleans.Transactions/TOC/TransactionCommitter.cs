using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;
using Orleans.Transactions.TOC;

namespace Orleans.Transactions;

public sealed class TransactionCommitter<TService> : ITransactionCommitter<TService>, ILifecycleParticipant<IGrainLifecycle>
    where TService : class
{
    private readonly ITransactionCommitterConfiguration _config;
    private readonly IGrainContext _context;
    private readonly ITransactionDataCopier<OperationState> _copier;
    private readonly ILogger _logger;
    private readonly ParticipantId _participantId;
    private readonly TransactionQueue<OperationState> _queue;

    private bool _detectReentrancy;

    public TransactionCommitter(
        ITransactionCommitterConfiguration config,
        IGrainContextAccessor contextAccessor,
        ITransactionDataCopier<OperationState> copier,
        ILogger<TransactionCommitter<TService>> logger,
        IOptions<TransactionalStateOptions> options,
        INamedTransactionalStateStorageFactory storageFactory,
        TimeProvider timeProvider)
    {
        _config = config;
        _context = contextAccessor.GrainContext;
        _copier = copier;
        _logger = logger;
        _participantId = new ParticipantId(_config.ServiceName, _context.GrainReference, ParticipantId.Role.Resource | ParticipantId.Role.PriorityManager);

        ITransactionalStateStorage<OperationState> storage = storageFactory.Create<OperationState>(_config.StorageName, _config.ServiceName);

        // setup transaction processing pipe
        TService service = _context.ActivationServices.GetRequiredKeyedService<TService>(_config.ServiceName);
        _queue = new TocTransactionQueue<TService>(service, options, _participantId, _context, storage, timeProvider, logger);

        // Add transaction manager factory to the grain context
        _context.RegisterResourceFactory<ITransactionManager>(_config.ServiceName, () => new TransactionManager<OperationState>(_queue));
    }

    /// <inheritdoc/>
    public Task OnCommit(ITransactionCommitOperation<TService> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        if (_detectReentrancy)
        {
            throw new LockRecursionException("cannot perform an update operation from within another operation");
        }

        var info = TransactionContext.GetRequiredTransactionInfo();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("StartWrite {Info}", info);

        if (info.IsReadOnly)
        {
            throw new OrleansReadOnlyViolatedException(info.Id);
        }

        info.Participants.TryGetValue(_participantId, out var recordedaccesses);

        return _queue.RWLock.EnterLock(info.TransactionId, info.Priority, recordedaccesses, false,
            () =>
            {
                // check if we expired while waiting
                if (!_queue.RWLock.TryGetRecord(info.TransactionId, out TransactionRecord<OperationState> record))
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

                    record.State.Operation = operation;
                    return true;
                }
                finally
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace(
                            "EndWrite {Info} {TransactionId} {Timestamp}",
                            info,
                            record.TransactionId,
                            record.Timestamp);

                    _detectReentrancy = false;
                }
            }
        );
    }

    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe<TransactionalState<OperationState>>(GrainLifecycleStage.SetupState, OnSetupState);
    }

    private async Task OnSetupState(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        // recover state
        await _queue.NotifyOfRestore();
    }

    [Serializable]
    [GenerateSerializer]
    public sealed class OperationState
    {
        [Id(0)]
        public ITransactionCommitOperation<TService> Operation { get; set; }
    }
}
