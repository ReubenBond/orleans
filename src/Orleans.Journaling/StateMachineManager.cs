using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Internal;
using Orleans.Runtime;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

internal sealed class StateMachineManager : IStateMachineManager, ILifecycleParticipant<IGrainLifecycle>, ILifecycleObserver
{
    private const int MinApplicationStateMachineId = 8;
    private static readonly StringCodec StringCodec = new();
    private static readonly UInt32Codec UInt32Codec = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, IDurableStateMachine> _stateMachines = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, IDurableStateMachine> _stateMachinesMap = [];
    private readonly IStateMachineStorage _storage;
    private readonly ILogger<StateMachineManager> _logger;
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Queue<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly StateMachineManagerState _stateMachineIds;
#pragma warning disable IDE0052 // Remove unread private members
    private readonly Task? _workLoop; // Retained for diagnostics.
#pragma warning restore IDE0052 // Remove unread private members
    private ManagerState _state;
    private Task? _pendingWrite;
    private uint _nextStateMachineId = MinApplicationStateMachineId;
    private LogExtentBuilder? _currentLogSegment;

    public StateMachineManager(
        IStateMachineStorage storage,
        ILogger<StateMachineManager> logger,
        SerializerSessionPool serializerSessionPool)
    {
        _storage = storage;
        _logger = logger;

        // The list of known state machines is itself stored as a durable state machine with the implicit id 0.
        // This allows us to recover the list of state machines ids without having to store it separately.
        _stateMachineIds = new StateMachineManagerState(this, StringCodec, UInt32Codec, serializerSessionPool);
        _stateMachinesMap[0] = _stateMachineIds;

        _workLoop = Start();
    }

    public void RegisterStateMachine(string name, IDurableStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);

        lock (_lock)
        {
            _stateMachines.Add(name, stateMachine);
            _workQueue.Enqueue(new WorkItem(WorkItemType.RegisterStateMachine, completion: null)
            {
                Context = name
            });
        }

        _workSignal.Signal();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task task;
        lock (_lock)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            _workQueue.Enqueue(new WorkItem(WorkItemType.Initialize, completion));
        }

        _workSignal.Signal();
        await task;
    }

    private Task Start()
    {
        using var suppressExecutionContext = new ExecutionContextSuppressor();
        return WorkLoop();
    }

    private async Task WorkLoop()
    {
        await Task.Yield();
        var needsRecovery = true;
        while (true)
        {
            try
            {
                await _workSignal.WaitAsync().ConfigureAwait(false);

                while (true)
                {
                    if (needsRecovery)
                    {
                        await RecoverAsync().ConfigureAwait(false);
                        needsRecovery = false;
                    }

                    WorkItem workItem;
                    lock (_lock)
                    {
                        if (!_workQueue.TryDequeue(out workItem))
                        {
                            // Wait for the queue to be signaled again.
                            break;
                        }
                    }

                    try
                    {
                        // Note that the implementation of each command is inlined to avoid allocating unnecessary async state machines.
                        // We are ok sacrificing some code organization for performance in the inner loop.
                        if (workItem.Type is WorkItemType.AppendLog or WorkItemType.WriteSnapshot)
                        {
                            // TODO: decide whether it's best to snapshot or append. Eg, by summing the size of the most recent snapshots and the current log length.
                            //       If the current log length is greater than the snapshot size, then take a snapshot instead of appending more log entries.
                            var isSnapshot = workItem.Type is WorkItemType.WriteSnapshot;
                            LogExtentBuilder? logSegment;
                            lock (_lock)
                            {
                                if (isSnapshot && _currentLogSegment is { } existingSegment)
                                {
                                    // If there are pending writes, reset them since they will be captured by the snapshot instead.
                                    // If we did not do this, the log would begin with some writes which would be followed by a snapshot which also included those writes.
                                    existingSegment.Reset();
                                }
                                else
                                {
                                    _currentLogSegment ??= new();
                                }

                                // The map of state machine ids is itself stored as a durable state machine with the id 0.
                                // This must be stored first, since it includes the identities of all other state machines, which are needed when replaying the log.
                                AppendUpdatesOrSnapshotStateMachine(_currentLogSegment, isSnapshot, 0, _stateMachineIds);

                                foreach (var (id, stateMachine) in _stateMachinesMap)
                                {
                                    if (id is 0 || stateMachine is null)
                                    {
                                        // Skip state machines which have been removed.
                                        continue;
                                    }

                                    AppendUpdatesOrSnapshotStateMachine(_currentLogSegment, isSnapshot, id, stateMachine);
                                }

                                if (_currentLogSegment.IsEmpty)
                                {
                                    logSegment = null;
                                }
                                else
                                {
                                    logSegment = _currentLogSegment;
                                    _currentLogSegment = null;
                                }
                            }

                            if (logSegment is not null)
                            {
                                if (isSnapshot)
                                {
                                    await _storage.ReplaceAsync(logSegment, _shutdownCancellation.Token).ConfigureAwait(false);
                                }
                                else
                                {
                                    await _storage.AppendAsync(logSegment, _shutdownCancellation.Token).ConfigureAwait(false);
                                }

                                // Notify all state machines that the operation completed.
                                lock (_lock)
                                {
                                    foreach (var stateMachine in _stateMachines.Values)
                                    {
                                        stateMachine.OnWriteCompleted();
                                    }
                                }
                            }
                        }
                        else if (workItem.Type is WorkItemType.DeleteState)
                        {
                            // Clear storage.
                            await _storage.DeleteAsync(_shutdownCancellation.Token).ConfigureAwait(false);

                            lock (_lock)
                            {
                                // Reset the state machine id collection.
                                _stateMachineIds.ResetVolatileState();

                                // Allocate new state machine ids for each state machine.
                                // Doing so will trigger a reset, since _stateMachineIds will call OnSetStateMachineId, which resets the state machine in question.
                                _nextStateMachineId = 1;
                                foreach (var (name, stateMachine) in _stateMachines)
                                {
                                    var id = _nextStateMachineId++;
                                    _stateMachineIds[name] = id;
                                }
                            }
                        }
                        else if (workItem.Type is WorkItemType.Initialize)
                        {
                            lock (_lock)
                            {
                                _state = ManagerState.Ready;
                            }
                        }
                        else if (workItem.Type is WorkItemType.RegisterStateMachine)
                        {
                            lock (_lock)
                            {
                                if (_state is not ManagerState.Unknown)
                                {
                                    throw new InvalidOperationException("Registering a state machine after activation is invalid");
                                    /*
                                    // Re-enqueue the work item without completing it, while waiting for the state machine manager to be initialized.
                                    _workQueue.Enqueue(workItem);
                                    continue;
                                    */
                                }

                                var name = (string)workItem.Context!;
                                if (!_stateMachineIds.ContainsKey(name))
                                {
                                    // Doing so will trigger a reset, since _stateMachineIds will call OnSetStateMachineId, which resets the state machine in question.
                                    _stateMachineIds[name] = _nextStateMachineId++;
                                }
                            }
                        }
                        else
                        {
                            Debug.Fail($"The command {workItem.Type} is unsupported");
                        }

                        workItem.CompletionSource?.SetResult();
                    }
                    catch (Exception exception)
                    {
                        workItem.CompletionSource?.SetException(exception);
                        needsRecovery = true;
                    }
                }
            }
            catch (Exception exception)
            {
                needsRecovery = true;
                _logger.LogError(exception, "Error processing work items.");
            }
        }
    }

    private void AppendUpdatesOrSnapshotStateMachine(LogExtentBuilder logSegment, bool isSnapshot, uint id, IDurableStateMachine stateMachine)  
    {
        var writer = logSegment.CreateLogWriter(new(id));
        if (isSnapshot)
        {
            stateMachine.AppendSnapshot(writer);
        }
        else
        {
            stateMachine.AppendEntries(writer);
        }
    }

    public async ValueTask DeleteStateAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_lock)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            _workQueue.Enqueue(new WorkItem(WorkItemType.DeleteState, completion));
        }

        _workSignal.Signal();
        await task;
    }

    private async Task RecoverAsync()
    {
        _stateMachineIds.ResetVolatileState();
        await foreach (var segment in _storage.ReadAsync(_shutdownCancellation.Token))
        {
            try
            {
                foreach (var entry in segment.Entries)
                {
                    var stateMachine = _stateMachinesMap[entry.StreamId.Value];
                    stateMachine.Apply(entry.Payload);
                }
            }
            finally
            {
                segment.Dispose();
            }
        }

        foreach (var stateMachine in _stateMachines.Values)
        {
            stateMachine.OnRecoveryCompleted();
        }
    }

    public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task? pendingWrite;
        var didEnqueue = false;
        lock (_lock)
        {
            // If the pending write is faulted, recovery will need to be performed.
            // For now, await it so that we can propagate the exception consistently.
            if (_pendingWrite is not { IsFaulted: true })
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingWrite = completion.Task;
                var workItemType = _storage.IsCompactionRequested switch
                {
                    true => WorkItemType.WriteSnapshot,
                    false => WorkItemType.AppendLog,
                };

                _workQueue.Enqueue(new WorkItem(workItemType, completion));
                didEnqueue = true;
            }

            pendingWrite = _pendingWrite;
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        if (pendingWrite is { } task)
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    private void OnSetStateMachineId(string name, uint id)
    {
        lock (_lock)
        {
            if (id >= _nextStateMachineId)
            {
                _nextStateMachineId = id + 1;
            }

            if (_stateMachines.TryGetValue(name, out var stateMachine))
            {
                _stateMachinesMap[id] = stateMachine;
                stateMachine.Reset(new StateMachineLogWriter(this, new(id)));
            }
            else
            {
                throw new InvalidOperationException($"State machine \"{name}\" (id: {id}) has not been registered on this state machine manager.");
            }
        }
    }

    public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IDurableStateMachine? stateMachine) => _stateMachines.TryGetValue(name, out stateMachine);

    void ILifecycleParticipant<IGrainLifecycle>.Participate(IGrainLifecycle observer) => observer.Subscribe(GrainLifecycleStage.SetupState, this);
    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken) => InitializeAsync(cancellationToken).AsTask();
    Task ILifecycleObserver.OnStop(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class StateMachineLogWriter(StateMachineManager manager, StateMachineId streamId) : IStateMachineLogWriter
    {
        private readonly StateMachineManager _manager = manager;
        private readonly StateMachineId _id = streamId;

        public void AppendEntry<TState>(Action<TState, IBufferWriter<byte>> action, TState state)
        {
            lock (_manager._lock)
            {
                var segment = _manager._currentLogSegment ??= new();
                var logWriter = segment.CreateLogWriter(_id);
                logWriter.AppendEntry(action, state);
            }
        }

        public void AppendEntries<TState>(Action<TState, StateMachineStorageWriter> action, TState state)
        {
            lock (_manager._lock)
            {
                var segment = _manager._currentLogSegment ??= new();
                var logWriter = segment.CreateLogWriter(_id);
                action(state, logWriter);
            }
        }
    }

    private readonly struct WorkItem(StateMachineManager.WorkItemType type, TaskCompletionSource? completion)
    {
        public WorkItemType Type { get; } = type;
        public TaskCompletionSource? CompletionSource { get; } = completion;
        public object? Context { get; init; }
    }

    private enum WorkItemType
    {
        Initialize,
        AppendLog,
        WriteSnapshot,
        DeleteState,
        RegisterStateMachine
    }

    private enum ManagerState
    {
        Unknown,
        Ready,
    }

    private sealed class StateMachineManagerState(
        StateMachineManager manager,
        IFieldCodec<string> keyCodec,
        IFieldCodec<uint> valueCodec,
        SerializerSessionPool serializerSessionPool) : DurableDictionary<string, uint>(keyCodec, valueCodec, serializerSessionPool)
    {
        private readonly StateMachineManager _manager = manager;

        public void ResetVolatileState() => ((IDurableStateMachine)this).Reset(new StateMachineLogWriter(_manager, new(0)));

        protected override void OnSet(string key, uint value) => _manager.OnSetStateMachineId(key, value);
    }
}

