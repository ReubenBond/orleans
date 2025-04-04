#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

internal sealed class TransactionalStateStorageProviderWrapper<TState> : ITransactionalStateStorage<TState>
    where TState : class, new()
{
    private readonly IGrainStorage _grainStorage;
    private readonly IGrainContext _context;
    private readonly string _stateName;

    private StateStorageBridge<TransactionalStateRecord<TState>>? _stateStorage;

    [MemberNotNull(nameof(_stateStorage))]
    private StateStorageBridge<TransactionalStateRecord<TState>> StateStorage => _stateStorage ??= GetStateStorage();

    public TransactionalStateStorageProviderWrapper(IGrainStorage grainStorage, string stateName, IGrainContext context)
    {
        _grainStorage = grainStorage;
        _context = context;
        _stateName = stateName;
    }

    public async Task<TransactionalStorageLoadResponse<TState>> Load()
    {
        await StateStorage.ReadStateAsync();
        var state = _stateStorage.State;
        return new TransactionalStorageLoadResponse<TState>(_stateStorage.Etag, state.CommittedState, state.CommittedSequenceId, state.Metadata, state.PendingStates);
    }

    public async Task<string> Store(string expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo, long? abortAfter)
    {
        if (StateStorage.Etag != expectedETag)
            throw new ArgumentException(nameof(expectedETag), "Etag does not match");
        var state = _stateStorage.State;
        state.Metadata = metadata;

        var pendingList = state.PendingStates;

        // abort
        if (abortAfter.HasValue && pendingList.Count != 0)
        {
            var pos = pendingList.FindIndex(t => t.SequenceId > abortAfter.Value);
            if (pos != -1)
            {
                pendingList.RemoveRange(pos, pendingList.Count - pos);
            }
        }

        // prepare
        if (statesToPrepare?.Count > 0)
        {
            foreach (var p in statesToPrepare)
            {
                var pos = pendingList.FindIndex(t => t.SequenceId >= p.SequenceId);
                if (pos == -1)
                {
                    pendingList.Add(p); //append
                }
                else if (pendingList[pos].SequenceId == p.SequenceId)
                {
                    pendingList[pos] = p;  //replace
                }
                else
                {
                    pendingList.Insert(pos, p); //insert
                }
            }
        }

        // commit
        if (commitUpTo.HasValue && commitUpTo.Value > state.CommittedSequenceId)
        {
            var pos = pendingList.FindIndex(t => t.SequenceId == commitUpTo.Value);
            if (pos != -1)
            {
                var committedState = pendingList[pos];
                state.CommittedSequenceId = committedState.SequenceId;
                state.CommittedState = committedState.State;
                pendingList.RemoveRange(0, pos + 1);
            }
            else
            {
                throw new InvalidOperationException($"Transactional state corrupted. Missing prepare record (SequenceId={commitUpTo.Value}) for committed transaction.");
            }
        }

        await _stateStorage.WriteStateAsync();
        return _stateStorage.Etag!;
    }

    private StateStorageBridge<TransactionalStateRecord<TState>> GetStateStorage()
    {
        return new(_stateName, _context, _grainStorage);
    }
}

[Serializable]
[GenerateSerializer]
public sealed class TransactionalStateRecord<TState>
    where TState : class, new()
{
    [Id(0)]
    public TState CommittedState { get; set; } = new TState();

    [Id(1)]
    public long CommittedSequenceId { get; set; }

    [Id(2)]
    public TransactionalStateMetaData Metadata { get; set; } = new TransactionalStateMetaData();

    [Id(3)]
    public List<PendingTransactionState<TState>> PendingStates { get; set; } = new List<PendingTransactionState<TState>>();
}