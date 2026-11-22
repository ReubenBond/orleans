using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// An <see cref="OrleansLedgerReplicaTransport"/> on a registered node with a LedgerStore of its own, over
/// <see cref="GatedWalStorage"/>, whose remote calls go to a <see cref="GrainDouble"/>, with each node's lanes, the limits,
/// and the clock the test chooses. Disposing it checks that the transport ends quiescent: every lane empty and no sender
/// copy held.
/// </summary>
internal sealed class TransportScenario : IAsyncDisposable
{
    /// <summary>The data lane's bytes, <c>MaxNodeCallBytes</c>.</summary>
    public const long Budget = 100;

    /// <summary>The payload limit, <c>MaxNodeCallBytes / 2</c>.</summary>
    public const int PayloadLimit = 50;

    /// <summary>The reply a remote whole read reserves, <c>MaxReadReplyBytes</c>.</summary>
    public const int ReadReply = 20;

    /// <summary>The control lane's calls, <c>MaxNodeControlCalls</c>.</summary>
    public const int ControlCalls = 2;

    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(1);
    public static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    public static readonly LedgerId Ledger = new(7);

    private readonly string _root;
    private readonly List<ILedgerReplica> _replicas = [];
    private int _invalidations;

    private TransportScenario(string root, GatedWalStorage storage, LedgerStore store, CallBudgetMutant mutant, int seed, bool registered)
    {
        _root = root;
        Storage = storage;
        Store = store;
        Local.Opened(store);
        if (registered)
        {
            Register();
        }

        Transport = new OrleansLedgerReplicaTransport(
            Local,
            _ => Grain,
            () => Interlocked.Increment(ref _invalidations),
            Limits,
            (node, bytes, calls, time) => new MutantCallBudget(bytes, calls, time, waited => new LedgerReplicaQueueTimeoutException(node, waited), mutant),
            new Random(seed),
            Time);
    }

    public static ReplicaCallLimits Limits { get; } = TestReplicaLimits.Of(
        static options =>
        {
            options.MaxNodeCallBytes = Budget;
            options.MaxReplicaCallBytes = 1 << 20;
            options.MaxReadReplyBytes = ReadReply;
            options.MaxNodeControlCalls = ControlCalls;
        },
        Deadline);

    public CountingTimeProvider Time { get; } = new();

    public LocalNode Local { get; } = new();

    public NodeId Remote { get; } = new(Guid.NewGuid());

    public GrainDouble Grain { get; } = new();

    public GatedWalStorage Storage { get; }

    public LedgerStore Store { get; }

    public OrleansLedgerReplicaTransport Transport { get; }

    public int Invalidations => Volatile.Read(ref _invalidations);

    public static async Task<TransportScenario> CreateAsync(CallBudgetMutant mutant = CallBudgetMutant.None, int seed = 0, bool registered = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "clog-orleans-transport-tests", Guid.NewGuid().ToString("N"));
        _ = System.IO.Directory.CreateDirectory(root);
        var storage = new GatedWalStorage();
        var store = await LedgerStore.OpenAsync(
            storage,
            new LedgerStoreOptions { DirectoryPath = root, FileSize = 1 << 20, PreallocateFiles = false },
            Guid.NewGuid(),
            create: true,
            new LedgerStoreCheckpointOptions(),
            TimeProvider.System);
        return new TransportScenario(root, storage, store, mutant, seed, registered);
    }

    /// <summary>Registers the scenario's node, so its serving gate admits calls.</summary>
    public void Register() => Local.Register(new NodeRegistryEntry(Store.NodeId, NodeRegistrySamples.Silo(1, 1)), new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, new ArchiveStorePrefix("t"), ArchiveStoreId.NewRandom()));

    /// <summary>Returns a pooled payload of <paramref name="length"/> bytes, which the caller disposes.</summary>
    public static ArcBuffer Payload(int length, byte fill = 1)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(Enumerable.Repeat(fill, length).ToArray());
        return writer.ConsumeSlice(length);
    }

    /// <summary>Returns a deadline <see cref="Deadline"/> from now on the scenario's clock, for entering a lane directly.</summary>
    public long DeadlineFromNow() => Transport.Budget(Remote).DeadlineAfter(Deadline);

    /// <summary>Opens a replica of <see cref="Ledger"/> on the local node or the remote one, which the scenario disposes.</summary>
    public ILedgerReplica Open(bool local)
    {
        var replica = Transport.Open(local ? Local.Node : Remote, Ledger, Options);
        _replicas.Add(replica);
        return replica;
    }

    /// <summary>Returns the node a local or remote call names.</summary>
    public NodeId Node(bool local) => local ? Local.Node : Remote;

    public async ValueTask DisposeAsync()
    {
        Storage.Open();
        Grain.ReleaseAll();
        foreach (var replica in _replicas)
        {
            await replica.DisposeAsync();
        }

        // Quiescence: every call, an abandoned one included, returns its grant and its sender copy.
        await ReplicaCluster.EventuallyAsync(() => Transport.Lanes.All(static lane => lane is { OutstandingBytes: 0, OutstandingCalls: 0, Waiting: 0 }) && Transport.SenderCopyCount == 0);
        Grain.Delivered.Dispose();
        await Store.DisposeAsync();
        if (System.IO.Directory.Exists(_root))
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>
/// A node's <see cref="ILedgerStoreGrain"/> as a test drives it: each call is recorded and held until the test answers it,
/// or answered at once by <see cref="Answer"/>; a held call honours its caller's cancellation, as an Orleans call does,
/// unless <see cref="HonoursCancellation"/> is cleared.
/// </summary>
internal sealed class GrainDouble : ILedgerStoreGrain
{
    private readonly Lock _lock = new();
    private readonly List<Call> _calls = [];
    private bool _released;

    /// <summary>Gets or sets the answer each call gets at once, a value or an exception to throw; null holds every call.</summary>
    public Func<object, object>? Answer { get; set; }

    /// <summary>
    /// Gets or sets whether a held call ends at its caller's cancellation; cleared, it waits for its answer whatever the
    /// caller does, as a call an outgoing grain call filter holds before Orleans registers its token does.
    /// </summary>
    public bool HonoursCancellation { get; set; } = true;

    /// <summary>Gets the DATA answers delivered to callers, each of which its caller must release (OR-I18).</summary>
    public OrleansBufferLedger Delivered { get; } = new();

    /// <summary>Gets the calls made, in order.</summary>
    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public int Sent
    {
        get
        {
            lock (_lock)
            {
                return _calls.Count;
            }
        }
    }

    public Task<RemoteReplicaWriteOutcome> WriteAsync(LedgerReplicaWriteRequest request, CancellationToken cancellationToken) =>
        CallAsync(WireBytes.AsSent(request), ReplicaWriteOutcome.Written.Instance, WriteOf, cancellationToken);

    public Task<RemoteReplicaRead> ReadAsync(LedgerReplicaReadRequest request, CancellationToken cancellationToken) =>
        CallAsync(request, ReplicaRead.Missing.Instance, ReadOf, cancellationToken);

    public Task<RemoteReplicaTailRead> ReadTailAsync(LedgerReplicaTailRequest request, CancellationToken cancellationToken) =>
        CallAsync(request, new RemoteReplicaTailRead.Tail(request.ContiguousTail, IsSealed: false), TailOf, cancellationToken);

    public Task<RemoteReplicaHeartbeatRead> HeartbeatAsync(LedgerReplicaHeartbeatRequest request, CancellationToken cancellationToken) =>
        CallAsync(request, new RemoteReplicaHeartbeatRead.Reply(0, new byte[request.Ledgers?.Length ?? 0]), HeartbeatOf, cancellationToken);

    /// <summary>Waits until <paramref name="count"/> calls were made.</summary>
    public Task WaitSentAsync(int count) => ReplicaCluster.EventuallyAsync(() => Sent >= count);

    /// <summary>Answers the held call <paramref name="index"/> with its default answer.</summary>
    public void Release(int index) => Calls[index].Reply.TrySetResult(null);

    /// <summary>Answers every held call, and every later one, with its default answer.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            _released = true;
            foreach (var call in _calls)
            {
                _ = call.Reply.TrySetResult(null);
            }
        }
    }

    internal static RemoteReplicaWriteOutcome WriteOf(object value) => value switch
    {
        RemoteReplicaWriteOutcome outcome => outcome,
        ReplicaWriteOutcome.Written written => written,
        ReplicaWriteOutcome.Sealed @sealed => @sealed,
        ReplicaWriteOutcome.Dropped dropped => dropped,
        ReplicaWriteOutcome.Backpressure backpressure => backpressure,
        NotThisNode notThisNode => notThisNode,
        ReplicaCallRefused refused => refused,
        ReplicaCorrupt corrupt => corrupt,
        _ => throw new InvalidOperationException($"{value} is no write answer."),
    };

    internal static RemoteReplicaRead ReadOf(object value) => value switch
    {
        RemoteReplicaRead read => read,
        RemoteReplicaRead.Data data => data,
        RemoteReplicaRead.TooLarge tooLarge => tooLarge,
        ReplicaRead.Missing missing => missing,
        ReplicaRead.OutsideBatch outside => outside,
        ReplicaWriteOutcome.Dropped dropped => dropped,
        ReplicaWriteOutcome.Backpressure backpressure => backpressure,
        NotThisNode notThisNode => notThisNode,
        ReplicaCallRefused refused => refused,
        ReplicaCorrupt corrupt => corrupt,
        _ => throw new InvalidOperationException($"{value} is no read answer."),
    };

    internal static RemoteReplicaTailRead TailOf(object value) => value switch
    {
        RemoteReplicaTailRead read => read,
        RemoteReplicaTailRead.Tail tail => tail,
        ReplicaWriteOutcome.Dropped dropped => dropped,
        ReplicaWriteOutcome.Backpressure backpressure => backpressure,
        NotThisNode notThisNode => notThisNode,
        ReplicaCallRefused refused => refused,
        ReplicaCorrupt corrupt => corrupt,
        _ => throw new InvalidOperationException($"{value} is no tail answer."),
    };

    internal static RemoteReplicaHeartbeatRead HeartbeatOf(object value) => value switch
    {
        RemoteReplicaHeartbeatRead read => read,
        RemoteReplicaHeartbeatRead.Reply reply => reply,
        ReplicaWriteOutcome.Backpressure backpressure => backpressure,
        NotThisNode notThisNode => notThisNode,
        ReplicaCallRefused refused => refused,
        _ => throw new InvalidOperationException($"{value} is no heartbeat answer."),
    };

    private async Task<T> CallAsync<T>(object request, T fallback, Func<object, T> answerOf, CancellationToken cancellationToken)
    {
        var call = new Call(request);
        if (Answer is { } answer)
        {
            _ = call.Reply.TrySetResult(answer(request));
        }

        lock (_lock)
        {
            _calls.Add(call);
            if (_released)
            {
                _ = call.Reply.TrySetResult(null);
            }
        }

        try
        {
            return await (HonoursCancellation ? call.Reply.Task.WaitAsync(cancellationToken) : call.Reply.Task) switch
            {
                null => fallback,
                Exception failure => throw failure,
                var value => answerOf(Deliver(value)),
            };
        }
        finally
        {
            // The double releases the pin it took for the message once the call ends, as each owner must: it checks the
            // caller's half of OR-I18, which Orleans' proxy never releases.
            if (request is LedgerReplicaWriteRequest write)
            {
                write.Payload.Dispose();
            }
        }
    }

    private object Deliver(object value)
    {
        var data = value switch
        {
            RemoteReplicaRead.Data delivered => delivered,
            RemoteReplicaRead read when read.Value is RemoteReplicaRead.Data delivered => delivered,
            _ => null,
        };
        if (data is not null)
        {
            _ = Delivered.Track(data);
        }

        return value;
    }

    /// <summary>One call: its request, and its answer, a value, an exception, or null for the default.</summary>
    internal sealed class Call(object request)
    {
        public object Request { get; } = request;

        public TaskCompletionSource<object?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
