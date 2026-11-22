using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A fault a replica injects into an operation.
/// </summary>
internal enum InjectedFault
{
    /// <summary>The operation runs.</summary>
    None,

    /// <summary>The operation fails without applying, as a lost request does.</summary>
    BeforeApplying,

    /// <summary>The operation applies and then fails, as a lost response does.</summary>
    AfterApplying,

    /// <summary>The operation waits until a harness action releases it or its hold limit passes, as on a slow replica.</summary>
    Held,

    /// <summary>
    /// The replica answers <c>Backpressure</c> without applying the operation, as an overloaded one does
    /// (ledger-replicas.md §6.4, outcomes.md §1).
    /// </summary>
    Backpressure,
}

/// <summary>
/// The next <paramref name="Count"/> operations of replica <paramref name="Replica"/>, or only those named
/// <paramref name="Operation"/>: <c>Write</c>, <c>RecoveryWrite</c>, <c>Read</c>, <c>ReadTail</c>, or <c>Seal</c>, meet
/// <paramref name="Fault"/>.
/// </summary>
internal sealed record ReplicaFault(int Replica, int Count, InjectedFault Fault, string? Operation = null)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"replica {Replica} meets {Fault} for its next {Count} {Operation ?? "operation"}s");
}

/// <summary>
/// The failure of a replica operation into which <see cref="FaultyLedgerReplica"/> injected a fault; a recovery asks the
/// replica again, as after any transport failure (ledger-protocol.md §6.5).
/// </summary>
internal sealed class InjectedFaultException : IOException
{
    public InjectedFaultException()
    {
    }

    public InjectedFaultException(string message)
        : base(message)
    {
    }

    public InjectedFaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A replica whose operations meet injected faults, on demand or at random, and which reports each fault that fires; at
/// random, only <paramref name="chaosFault"/> when it is set.
/// </summary>
internal sealed class FaultyLedgerReplica(
    int index, ILedgerReplica inner, TimeProvider time, ConcurrentQueue<string> events, Random? chaos, InjectedFault? chaosFault = null) : ILedgerReplica
{
    private const double ChaosProbability = 0.15;

    /// <summary>
    /// How long a replica holds an operation on its clock, unless a harness action releases it first.
    /// </summary>
    internal static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(3);

    private readonly Lock _lock = new();
    private readonly List<Task> _held = [];
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Random? _chaos = chaos;
    private InjectedFault _fault;
    private string? _operation;
    private int _remaining;
    private long _fired;
    private long _heldFired;
    private long _holdsExpired;
    private long _reads;
    private long _missingBelowTail;
    private long _answersSince = long.MaxValue;
    private long _lastAnsweredAt;

    /// <summary>
    /// Gets how many injected faults have fired.
    /// </summary>
    internal long Fired => Interlocked.Read(ref _fired);

    /// <summary>Gets how many <see cref="InjectedFault.Held"/> faults have fired.</summary>
    internal long HeldFired => Interlocked.Read(ref _heldFired);

    /// <summary>Gets how many held operations waited out their hold limit, rather than a harness action releasing them.</summary>
    internal long HoldsExpired => Interlocked.Read(ref _holdsExpired);

    /// <summary>Gets how many reads the replica received.</summary>
    internal long Reads => Interlocked.Read(ref _reads);

    /// <summary>Gets how many reads below the tail they carried the replica answered missing.</summary>
    internal long MissingBelowTail => Interlocked.Read(ref _missingBelowTail);

    /// <summary>Sets when, on the fake clock, the reads begin whose answers <see cref="LastAnsweredAt"/> records.</summary>
    internal DateTimeOffset AnswersSince
    {
        set => _ = Interlocked.Exchange(ref _answersSince, value.UtcTicks);
    }

    /// <summary>Gets when, on the fake clock, the replica last answered a read asked at or after <see cref="AnswersSince"/>.</summary>
    internal DateTimeOffset LastAnsweredAt => new(Interlocked.Read(ref _lastAnsweredAt), TimeSpan.Zero);

    /// <summary>
    /// Injects <paramref name="fault"/> into the next <paramref name="count"/> operations, or only those named
    /// <paramref name="operation"/>.
    /// </summary>
    internal void Inject(int count, InjectedFault fault, string? operation)
    {
        lock (_lock)
        {
            _remaining = count;
            _fault = fault;
            _operation = operation;
        }
    }

    /// <summary>
    /// Releases every held operation and waits until each has applied.
    /// </summary>
    internal async Task ReleaseAsync()
    {
        Task[] held;
        lock (_lock)
        {
            _ = _release.TrySetResult();
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            held = [.. _held];
            _held.Clear();
        }

        // Each held operation completes its task in a finally block, so awaiting them one at a time waits for all.
        foreach (var operation in held)
        {
            await operation;
        }
    }

    /// <summary>
    /// Clears every fault, so no more fire, and releases every held operation.
    /// </summary>
    internal Task HealAsync()
    {
        lock (_lock)
        {
            _remaining = 0;
            _chaos = null;
        }

        return ReleaseAsync();
    }

    // A replica rejects an invalid argument before anything else (ledger-replicas.md §3.2), so no fault meets it.
    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(slot);
        LedgerReplicaOperations.ValidateBatch(payload);
        return RunAsync(recovery ? "RecoveryWrite" : "Write", slot, () => inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken), ReplicaAnswers.Backpressure);
    }

    public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(slot);
        _ = Interlocked.Increment(ref _reads);
        var asked = time.GetUtcNow().UtcTicks;
        var read = await RunAsync("Read", slot, () => inner.ReadAsync(slot, contiguousTail, cancellationToken), ReplicaWriteOutcome.Backpressure.Instance, static result => result.Release());
        if (asked >= Interlocked.Read(ref _answersSince))
        {
            _ = Interlocked.Exchange(ref _lastAnsweredAt, time.GetUtcNow().UtcTicks);
        }

        if (slot < contiguousTail && read.Case == ReplicaReadCase.Missing)
        {
            _ = Interlocked.Increment(ref _missingBelowTail);
        }

        return read;
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
        RunAsync(seal ? "Seal" : "ReadTail", contiguousTail, () => inner.ReadTailAsync(contiguousTail, seal, cancellationToken), ReplicaWriteOutcome.Backpressure.Instance);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// Runs the replica operation <paramref name="operation"/> at <paramref name="slot"/> under the next fault, which
    /// answers <paramref name="backpressure"/> in place of running it; <paramref name="discard"/> releases a result which
    /// a fault after applying replaces with a failure.
    /// </summary>
    private async ValueTask<T> RunAsync<T>(string operation, Slot slot, Func<ValueTask<T>> run, T backpressure, Action<T>? discard = null)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{operation}({slot.Value})");
        var fault = Next(operation, name);
        if (fault == InjectedFault.Backpressure)
        {
            return backpressure;
        }

        if (fault == InjectedFault.BeforeApplying)
        {
            throw Failure(name, fault);
        }

        if (fault == InjectedFault.Held)
        {
            return await HoldAsync(run);
        }

        var result = await run();
        if (fault != InjectedFault.AfterApplying)
        {
            return result;
        }

        discard?.Invoke(result);
        throw Failure(name, fault);
    }

    private async ValueTask<T> HoldAsync<T>(Func<ValueTask<T>> operation)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task release;
        lock (_lock)
        {
            _held.Add(applied.Task);
            release = _release.Task;
        }

        try
        {
            if (!await WaitForReleaseAsync(release))
            {
                _ = Interlocked.Increment(ref _holdsExpired);
            }

            return await operation();
        }
        finally
        {
            applied.SetResult();
        }
    }

    /// <summary>
    /// Waits for <paramref name="release"/> or the hold limit on the replica's clock, whichever comes first, and returns
    /// whether the release came first. In a simulation the test assembly is not rewritten, so the wait runs in the
    /// simulation kit, whose timers and cancellations the simulation schedules (verification.md §7.1).
    /// </summary>
    private async Task<bool> WaitForReleaseAsync(Task release)
    {
#if CLOG_SIMULATION_TESTS
        return await Orleans.Durable.Storage.Simulation.SimulationTasks.CompletesWithinAsync(release, HoldLimit, time);
#else
        await release.WaitAsync(HoldLimit, time).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return release.IsCompleted;
#endif
    }

    private InjectedFaultException Failure(string name, InjectedFault fault) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Replica {index}: {name} met an injected fault ({fault})."));

    private InjectedFault Next(string operation, string name)
    {
        InjectedFault fault;
        lock (_lock)
        {
            if (_remaining > 0 && (_operation is null || string.Equals(_operation, operation, StringComparison.Ordinal)))
            {
                _remaining--;
                fault = _fault;
            }
            else
            {
                fault = _chaos is not null && _chaos.NextDouble() < ChaosProbability
                    ? chaosFault ?? (InjectedFault)(_chaos.Next(4) + 1)
                    : InjectedFault.None;
            }
        }

        if (fault != InjectedFault.None)
        {
            if (fault == InjectedFault.Held)
            {
                _ = Interlocked.Increment(ref _heldFired);
            }

            _ = Interlocked.Increment(ref _fired);
            events.Enqueue(string.Create(CultureInfo.InvariantCulture, $"replica {index}: {name} meets {fault}"));
        }

        return fault;
    }
}

/// <summary>
/// Presents a <see cref="Ledger"/> to the conformance spec as a replica: a writer write, a read, and a seal
/// check, each carrying the request's contiguous tail as the ledger's known one is raised to it (LP-I11). A
/// <see cref="Ledger"/> has no seal and no recovery write outside its recovery, so it refuses both.
/// </summary>
/// <param name="ledger">The ledger.</param>
/// <param name="mutantRead">
/// A defective read a test builds from the ledger's parts, which reads a slot carrying the known contiguous tail in place
/// of the ledger's read, or <see langword="null"/> for the ledger's own.
/// </param>
internal sealed class LedgerAsReplica(Ledger ledger, Func<Slot, Slot, CancellationToken, ValueTask<ReplicaRead>>? mutantRead = null) : ILedgerReplica
{
    public async ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot address, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
    {
        if (recovery)
        {
            throw new NotSupportedException("A Ledger sends recovery writes only in its recovery.");
        }

        // A write the ledger rejects as an argument raises nothing, so the carried tail is known only once it is valid.
        LedgerReplicaOperations.ValidateSlot(address);
        LedgerReplicaOperations.ValidateBatch(payload);
        _ = ledger.RaiseKnownContiguousTail(contiguousTail);
        return await ledger.WriteAsync(address, payload, cancellationToken) switch
        {
            ReplicaWriteOutcome.Written written => written,
            ReplicaWriteOutcome.Sealed answer => answer,
            ReplicaWriteOutcome.Backpressure backpressure => backpressure,
            null => throw new UnreachableException(),
        };
    }

    public ValueTask<ReplicaRead> ReadAsync(Slot address, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        // A rejected request takes no effect, so the carried tail is known only once the arguments are valid.
        LedgerReplicaOperations.ValidateSlot(address);
        _ = ledger.RaiseKnownContiguousTail(contiguousTail);
        return mutantRead is null
            ? ledger.ReadAsync(address, cancellationToken)
            : mutantRead(address, ledger.KnownContiguousTail, cancellationToken);
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot address, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        LedgerReplicaOperations.ValidateSlot(address);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (mutantRead is not null)
        {
            return LedgerReplicaOperations.ReadRangeOfValueAsync(this, address, offset, length, contiguousTail, cancellationToken);
        }

        _ = ledger.RaiseKnownContiguousTail(contiguousTail);
        return ledger.ReadAsync(address, offset, length, cancellationToken);
    }

    public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
    {
        if (seal)
        {
            throw new NotSupportedException("A Ledger seals only in its recovery.");
        }

        _ = ledger.RaiseKnownContiguousTail(contiguousTail);
        return await ledger.ReadTailAsync(cancellationToken) switch
        {
            LedgerTail tail => new LedgerReplicaTail(tail.ContiguousTail, tail.IsSealed),
            ReplicaWriteOutcome.Backpressure backpressure => backpressure,
            null => throw new UnreachableException(),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
