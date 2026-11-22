using System.Buffers;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A conditional register whose reads and writes a test scripts (metastore.md §2.2): reads may wait at a gate, and a
/// held write waits until the test decides its outcome: answered as the register decides it, applied and answered
/// <see cref="RegisterWriteOutcome.Mismatch"/> as after a backend-SDK retry, failed with or without applying, or failed
/// and landed later, at most once and only at the version it named (MS-I3).
/// </summary>
/// <remarks>
/// Its version is the decimal count of applied writes (MS-I5). Each read's entry owns pages rented under the page
/// accounting of the context which created the register, so a test's leak check sees every entry its reader fails to
/// dispose, also when the reader runs without the test's execution context (conventions.md §12).
/// </remarks>
internal sealed class ScriptedConditionalRegister : IConditionalRegister
{
    private readonly ArcBufferPageAccounting? _accounting = ArcBufferPageAccounting.Current;
    private readonly Lock _lock = new();
    private readonly Queue<HeldWrite> _held = [];
    private readonly List<byte[]> _applied = [];
    private readonly List<long> _appliedDispatches = [];
    private readonly List<int> _appliedWriters = [];
    private byte[]? _value;
    private long _dispatches;

    /// <summary>Gets the values of the applied writes, in the order they applied.</summary>
    public IReadOnlyList<byte[]> AppliedWrites
    {
        get
        {
            lock (_lock)
            {
                return [.. _applied];
            }
        }
    }

    /// <summary>
    /// Gets, for each applied write, in the order they applied, the number of writes dispatched before it: its place in
    /// the order in which writes reached the register.
    /// </summary>
    public IReadOnlyList<long> AppliedDispatches
    {
        get
        {
            lock (_lock)
            {
                return [.. _appliedDispatches];
            }
        }
    }

    /// <summary>
    /// Gets, for each applied write, in the order they applied, the writer which dispatched it: the one a
    /// <see cref="Writer"/> view names, 0 for a write through the register itself, and −1 for <see cref="Put"/>.
    /// </summary>
    public IReadOnlyList<int> AppliedWriters
    {
        get
        {
            lock (_lock)
            {
                return [.. _appliedWriters];
            }
        }
    }

    /// <summary>Gets the number of writes dispatched to the register, its own and <see cref="Put"/>'s.</summary>
    public long Dispatches
    {
        get
        {
            lock (_lock)
            {
                return _dispatches;
            }
        }
    }

    /// <summary>Gets the number of writes which applied and answered <see cref="RegisterWriteOutcome.Mismatch"/>.</summary>
    public int AppliedMismatches { get; private set; }

    /// <summary>Gets the number of reads the register has served.</summary>
    public int Reads { get; private set; }

    /// <summary>Gets the number of writes which reached the register.</summary>
    public int Writes { get; private set; }

    /// <summary>Gets or sets a gate every read waits at before it reads, or <see langword="null"/> for none.</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    /// <summary>Gets or sets the exception every read throws, or <see langword="null"/> for none.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>Gets the register's current version, or <see langword="null"/> while it is absent.</summary>
    public MetaStoreVersion? Version
    {
        get
        {
            lock (_lock)
            {
                return _value is null ? null : VersionOf(_applied.Count);
            }
        }
    }

    /// <summary>Holds the next write to arrive until the test decides its outcome.</summary>
    public HeldWrite HoldNextWrite()
    {
        var held = new HeldWrite(this);
        lock (_lock)
        {
            _held.Enqueue(held);
        }

        return held;
    }

    /// <summary>Replaces the register's value as another writer would, unconditionally.</summary>
    public void Put(byte[] value)
    {
        lock (_lock)
        {
            _value = value;
            _applied.Add(value);
            _appliedDispatches.Add(_dispatches++);
            _appliedWriters.Add(-1);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        byte[]? value;
        int count;
        lock (_lock)
        {
            Reads++;
            value = _value;
            count = _applied.Count;
        }

        return value is null ? null : new MetaStoreEntry(VersionOf(count), Rent(value));
    }

    /// <summary>Returns a view of the register whose writes it records as <paramref name="writer"/>'s.</summary>
    public IConditionalRegister Writer(int writer) => new WriterView(this, writer);

    /// <inheritdoc/>
    public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
        WriteAsync(expectedVersion, value, writer: 0, cancellationToken);

    private async ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, int writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = value.ToArray();
        HeldWrite? held;
        long dispatch;
        lock (_lock)
        {
            Writes++;
            dispatch = _dispatches++;
            _ = _held.TryDequeue(out held);
        }

        if (held is null)
        {
            return Answer(expectedVersion, copy, dispatch, writer);
        }

        var decide = held.Arrive(expectedVersion, copy, dispatch, writer);
#if CLOG_SIMULATION_TESTS
        // In a simulation the committer's rewritten await cannot follow a task this unrewritten register completes later,
        // so a held write takes its decision as it arrives (DecideOnArrival) and applies it a scheduler step later, through
        // the kit's yield (verification.md §7.1).
        await Orleans.Durable.Storage.Simulation.SimulationTasks.Yield();
        return await (await decide)();
#else
        return await (await decide.WaitAsync(cancellationToken))();
#endif
    }

    private static MetaStoreVersion VersionOf(int applied) => new(applied.ToString(CultureInfo.InvariantCulture));

    private RegisterWriteOutcome Answer(MetaStoreVersion? expectedVersion, byte[] value, long dispatch, int writer) =>
        TryApply(expectedVersion, value, dispatch, writer) ? new RegisterWriteOutcome.Applied(Version!) : RegisterWriteOutcome.Mismatch.Instance;

    /// <summary>
    /// Applies a write iff the register holds <paramref name="expectedVersion"/>, or is absent for <see langword="null"/>,
    /// and then, in the same step, the value <paramref name="another"/> makes of it, as another writer's write which no
    /// other write can come between.
    /// </summary>
    private bool TryApply(MetaStoreVersion? expectedVersion, byte[] value, long dispatch, int writer, Func<byte[], byte[]>? another = null)
    {
        lock (_lock)
        {
            if (expectedVersion is null ? _value is not null : _value is null || expectedVersion != VersionOf(_applied.Count))
            {
                return false;
            }

            _value = value;
            _applied.Add(value);
            _appliedDispatches.Add(dispatch);
            _appliedWriters.Add(writer);
            if (another is not null)
            {
                _value = another(value);
                _applied.Add(_value);
                _appliedDispatches.Add(_dispatches++);
                _appliedWriters.Add(-1);
            }

            return true;
        }
    }

    /// <summary>Copies <paramref name="value"/> into pages rented under the creating context's accounting.</summary>
    private ArcBuffer Rent(byte[] value)
    {
        if (value.Length == 0)
        {
            return default;
        }

        var previous = ArcBufferPageAccounting.Current;
        ArcBufferPageAccounting.Current = _accounting;
        try
        {
            using var writer = new ArcBufferWriter(value.Length);
            writer.Write(value);
            return writer.ConsumeSlice(value.Length);
        }
        finally
        {
            ArcBufferPageAccounting.Current = previous;
        }
    }

    /// <summary>A write held at the register until the test decides its outcome.</summary>
    internal sealed class HeldWrite(ScriptedConditionalRegister register)
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Func<ValueTask<RegisterWriteOutcome>>> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<HeldWrite>? _onArrival;
        private MetaStoreVersion? _expected;
        private long _dispatch;
        private int _writer;

        /// <summary>Gets a task which completes once the write reaches the register.</summary>
        public Task Arrived => _arrived.Task;

        /// <summary>Gets the value the write carries, once it has arrived.</summary>
        public byte[] Value { get => field ?? throw new InvalidOperationException("The write has not arrived."); private set; }

        /// <summary>Applies the write if its condition holds, and answers as the register decides.</summary>
        public void Answer() => Decide(() => ValueTask.FromResult(register.Answer(_expected, Value, _dispatch, _writer)));

        /// <summary>
        /// Applies the write if its condition holds, and answers <see cref="RegisterWriteOutcome.Mismatch"/> either way, as
        /// a backend-SDK retry of an attempt which applied does. When it applied and <paramref name="another"/> is given,
        /// another writer then puts the value <paramref name="another"/> makes of it before the answer arrives.
        /// </summary>
        public void ApplyAndAnswerMismatch(Func<byte[], byte[]>? another = null) => Decide(() =>
        {
            if (register.TryApply(_expected, Value, _dispatch, _writer, another))
            {
                register.AppliedMismatches++;
            }

            return ValueTask.FromResult<RegisterWriteOutcome>(RegisterWriteOutcome.Mismatch.Instance);
        });

        /// <summary>Fails the write with an unknown outcome, applying it first when <paramref name="apply"/> is set.</summary>
        public void Fail(bool apply) => Decide(() =>
        {
            if (apply)
            {
                _ = register.TryApply(_expected, Value, _dispatch, _writer);
            }

            return ValueTask.FromException<RegisterWriteOutcome>(new IOException("The scripted write's answer was lost."));
        });

        /// <summary>
        /// Fails the write with an unknown outcome and returns a landing, which applies it later iff the register still
        /// holds the version it named, and returns whether it applied.
        /// </summary>
        public Func<bool> FailAndLandLater()
        {
            Fail(apply: false);
            return () => register.TryApply(_expected, Value, _dispatch, _writer);
        }

        /// <summary>
        /// Decides the write with <paramref name="decide"/> as soon as it arrives, as a simulated history does.
        /// </summary>
        public void DecideOnArrival(Action<HeldWrite> decide) => _onArrival = decide;

        /// <summary>
        /// Ends a hold no write reached, once no more writes can arrive: <see cref="Arrived"/> is cancelled. Returns
        /// whether the hold was abandoned, rather than reached.
        /// </summary>
        public bool Abandon() => _arrived.TrySetCanceled();

        internal Task<Func<ValueTask<RegisterWriteOutcome>>> Arrive(MetaStoreVersion? expected, byte[] value, long dispatch, int writer)
        {
            _writer = writer;
            _expected = expected;
            Value = value;
            _dispatch = dispatch;
            Assert.True(_arrived.TrySetResult(), "A write reached an abandoned hold.");
            _onArrival?.Invoke(this);
            return _decision.Task;
        }

        private void Decide(Func<ValueTask<RegisterWriteOutcome>> outcome)
        {
            Assert.True(_arrived.Task.IsCompleted, "The held write has not arrived.");
            _decision.SetResult(outcome);
        }
    }

    /// <summary>The register as one writer sees it: its writes are recorded as that writer's.</summary>
    private sealed class WriterView(ScriptedConditionalRegister register, int writer) : IConditionalRegister
    {
        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default) => register.ReadAsync(cancellationToken);

        public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
            register.WriteAsync(expectedVersion, value, writer, cancellationToken);
    }
}
