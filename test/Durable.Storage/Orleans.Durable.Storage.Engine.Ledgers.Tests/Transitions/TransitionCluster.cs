using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions;

/// <summary>
/// A cluster for transition tests: in-memory replicas on every node, one <c>ledger-map</c> register with one committer,
/// and a process per node with its own map view, reader, registry, executor, clock, logs, and replica hooks; and the
/// helpers the transition tests share.
/// </summary>
internal sealed class TransitionCluster : IAsyncDisposable
{
    public const int Window = 4;

    private readonly List<TransitionProcess> _processes = [];

    public TransitionCluster(int maxValueLength = 1 << 20)
    {
        Committer = new LedgerMapCommitter(Register, maxValueLength);
    }

    public static ImmutableArray<NodeId> Nodes { get; } = [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2), LedgerMapSamples.Node(3)];

    /// <summary>Gets the longest a test waits for a step another task takes.</summary>
    public static TimeSpan Bound { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the empty set of unserved ledgers a caller already handles.</summary>
    public static IReadOnlySet<LedgerId> NoneKnown { get; } = ImmutableHashSet<LedgerId>.Empty;

    public InMemoryLedgerReplicaTransport Transport { get; } = new();

    public ScriptedConditionalRegister Register { get; } = new();

    public LedgerMapCommitter Committer { get; }

    public static LedgerMapCommitOutcome Applied(ulong epoch) => new LedgerMapCommitOutcome.Applied(epoch);

    public static LedgerMapCommitOutcome Superseded(ulong epoch) => new LedgerMapCommitOutcome.Superseded(epoch);

    public static ProposalOutcome DurableAt(long slot) => new(new ProposalOutcome.Durable(new Slot(slot)));

    public static ProposalOutcome Fenced => new(default(ProposalOutcome.Fenced));

    /// <summary>The choice of a transition executed by <paramref name="executor"/>, over every node, from <paramref name="epoch"/>.</summary>
    public static TransitionChoice Choice(NodeId executor, ulong epoch, ulong chosenAt = 0) => new(executor, Nodes, Window, chosenAt, epoch);

    /// <summary>Returns the unserved ledgers a caller already handles.</summary>
    public static IReadOnlySet<LedgerId> Known(params LedgerId[] ledgers) => ImmutableHashSet.Create(ledgers);

    /// <summary>Proposes one row under <paramref name="key"/>.</summary>
    public static async Task<ProposalOutcome> ProposeAsync(LedgerWriter writer, byte key, CancellationToken cancellationToken)
    {
        using var payload = WriterTestData.CreatePayload("row"u8);
        byte[] keyBytes = [key];
        using var row = new Row(keyBytes, RowKind.Append, payload);
        return await writer.ProposeAsync([row], cancellationToken);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, failing after <see cref="Bound"/>.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Bound, $"Timed out waiting until {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Starts the process of <paramref name="node"/>.</summary>
    public async Task<TransitionProcess> StartAsync(NodeId node, CancellationToken cancellationToken)
    {
        var process = new TransitionProcess(node, await LedgerMapView.OpenAsync(Register, cancellationToken), Transport);
        _processes.Add(process);
        return process;
    }

    /// <summary>Starts the process of <paramref name="node"/> and bootstraps the map from it.</summary>
    public async Task<TransitionProcess> BootstrapAsync(NodeId node, CancellationToken cancellationToken)
    {
        var process = await StartAsync(node, cancellationToken);
        Assert.Equal(Applied(1), await process.ExecuteAsync(this, new Transition.Bootstrap(Choice(node, 0)), cancellationToken));
        return process;
    }

    /// <summary>Writes <paramref name="value"/> to the register as a committer would, bypassing it.</summary>
    public void Write(LedgerMapValue value) => Register.Put(LedgerMapSamples.Encoded(value));

    /// <summary>Applies <paramref name="change"/> to the register's value outside the committer, without reading it into any view.</summary>
    public async Task WriteAppliedAsync(LedgerMapChange change, CancellationToken cancellationToken)
    {
        var current = await LedgerMapView.OpenAsync(Register, cancellationToken);
        var value = current.Current;
        var (result, _, applied) = LedgerMapRules.Fold(value, [change], int.MaxValue);
        _ = Assert.Single(applied);
        Write(new LedgerMapValue(value.Epoch + 1, result.NextLedgerId, result.RetirementFloor, result.Ledgers));
    }

    /// <summary>Applies <paramref name="change"/> to the register's value outside the committer, and reads it into the view.</summary>
    public async Task PublishAppliedAsync(TransitionProcess process, LedgerMapChange change, CancellationToken cancellationToken)
    {
        await WriteAppliedAsync(change, cancellationToken);
        _ = await process.View.ReadAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            await process.DisposeAsync();
        }

        await Committer.DisposeAsync();
        Transport.Dispose();
    }
}

/// <summary>One node's process: its map view, reader, registry, and executor, over its own clock, logs, and replica hooks.</summary>
internal sealed class TransitionProcess : IAsyncDisposable
{
    public TransitionProcess(NodeId node, LedgerMapView view, ILedgerReplicaTransport transport)
    {
        Node = node;
        View = view;
        Reader = new LedgerReader(view, new HookedTransport(transport, Hooks), Time, node);
        Registry = new LedgerWriterRegistry(Reader, 1 << 25, LedgerWriterOptions.Default, Time, Logs, keys => OnSegmentEnded?.Invoke(keys));
        Logger = new RecordingLogger<TransitionExecutor>(Logs);
        Executor = new TransitionExecutor(Registry, Logger);
    }

    public NodeId Node { get; }

    public LedgerMapView View { get; }

    public LedgerReader Reader { get; }

    public LedgerWriterRegistry Registry { get; }

    public TransitionExecutor Executor { get; }

    /// <summary>Gets the clock of the process's reader, registry, and writers.</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>Gets the logs of the process's registry, writers, and executor.</summary>
    public RecordingLoggerFactory Logs { get; } = new();

    public RecordingLogger<TransitionExecutor> Logger { get; }

    /// <summary>Gets the hooks on the replica calls the process's ledgers make.</summary>
    public ReplicaHooks Hooks { get; } = new();

    /// <summary>Gets or sets the sink of the segments the process's writers give (ledgers.md §2.4).</summary>
    public Action<LedgerSegmentKeys>? OnSegmentEnded { get; set; }

    /// <summary>Executes <paramref name="transition"/> through <paramref name="cluster"/>'s committer.</summary>
    public Task<LedgerMapCommitOutcome> ExecuteAsync(TransitionCluster cluster, Transition transition, CancellationToken cancellationToken) =>
        Executor.ExecuteAsync(transition, cluster.Committer.CommitAsync, cancellationToken);

    /// <summary>Returns the ledger the view lists open over <paramref name="range"/>.</summary>
    public LedgerMetadata Open(KeyRange range) => View.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == range);

    /// <summary>Returns the writer the process holds of <paramref name="ledger"/>, served or not.</summary>
    public LedgerWriter? HeldWriter(LedgerId ledger)
    {
        using var lease = Reader.LeaseLedger(View.Current.Get(ledger)!.Value);
        return Registry.HeldWriter(lease.Ledger);
    }

    public async ValueTask DisposeAsync()
    {
        await Registry.DisposeAsync();
        await Reader.DisposeAsync();
    }
}

/// <summary>One replica call a <see cref="ReplicaHooks"/> hook sees.</summary>
internal readonly record struct ReplicaCall(NodeId Node, LedgerId Ledger, Slot Slot, bool Seal, bool Recovery, CancellationToken CancellationToken);

/// <summary>
/// Hooks on a process's replica calls: each receives the call and a continuation which makes it on the replica, and
/// answers in its place, before it, or after it.
/// </summary>
internal sealed class ReplicaHooks
{
    public Func<ReplicaCall, Func<ValueTask<ReplicaTailRead>>, ValueTask<ReplicaTailRead>>? ReadTail { get; set; }

    public Func<ReplicaCall, Func<ValueTask<ReplicaWriteOutcome>>, ValueTask<ReplicaWriteOutcome>>? Write { get; set; }

    public Func<ReplicaCall, Func<ValueTask<ReplicaRead>>, ValueTask<ReplicaRead>>? Read { get; set; }

    /// <summary>Gets or sets the hook which sees each replica the process opens, before it opens it.</summary>
    public Action<NodeId, LedgerId>? Open { get; set; }
}

/// <summary>A transport whose replicas pass each call through a process's <see cref="ReplicaHooks"/>.</summary>
internal sealed class HookedTransport(ILedgerReplicaTransport inner, ReplicaHooks hooks) : ILedgerReplicaTransport
{
    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
    {
        hooks.Open?.Invoke(node, ledger);
        return new HookedReplica(inner.Open(node, ledger, options), node, ledger, hooks);
    }

    public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
        inner.HeartbeatAsync(node, ledgers, cancellationToken);

    private sealed class HookedReplica(ILedgerReplica inner, NodeId node, LedgerId ledger, ReplicaHooks hooks) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            hooks.Write is { } hook
                ? hook(new ReplicaCall(node, ledger, slot, Seal: false, recovery, cancellationToken), () => inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken))
                : inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            hooks.Read is { } hook
                ? hook(new ReplicaCall(node, ledger, slot, Seal: false, Recovery: false, cancellationToken), () => inner.ReadAsync(slot, contiguousTail, cancellationToken))
                : inner.ReadAsync(slot, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            hooks.ReadTail is { } hook
                ? hook(new ReplicaCall(node, ledger, contiguousTail, seal, Recovery: false, cancellationToken), () => inner.ReadTailAsync(contiguousTail, seal, cancellationToken))
                : inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>One log entry: its logger's category, level, message, and exception.</summary>
internal readonly record struct LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>A logger factory which records every entry its loggers write.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Returns the entries the loggers of <typeparamref name="T"/> wrote.</summary>
    public IReadOnlyList<LogEntry> From<T>() => [.. Entries.Where(static entry => entry.Category == typeof(T).FullName)];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }

    private sealed class Logger(RecordingLoggerFactory factory, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            factory.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }
}

/// <summary>A logger of <typeparamref name="T"/> which records each entry in a <see cref="RecordingLoggerFactory"/>.</summary>
internal sealed class RecordingLogger<T>(RecordingLoggerFactory factory) : ILogger<T>
{
    private readonly ILogger _logger = factory.CreateLogger(typeof(T).FullName!);

    /// <summary>Gets the level and message of each entry this logger's category wrote.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. factory.From<T>().Select(static entry => (entry.Level, entry.Message))];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _logger.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _logger.Log(logLevel, eventId, state, exception, formatter);
}
