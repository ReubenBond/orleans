using System.Buffers;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// A simulated MetaStore register which injects the faults its contract allows: outages, failed reads, writes whose
/// outcome is unknown, which apply before the caller hears, never, or later, and writes which apply and answer
/// <see cref="RegisterWriteOutcome.Mismatch"/>, as a backend-SDK retry of an attempt which applied does (metastore.md
/// §2.2, §6; test/Durable.Storage/docs/spec/verification.md §7.2).
/// </summary>
/// <remarks>
/// <para>
/// The register is linearizable (MS-I1): each request reaches it after a random latency, takes effect there at one
/// instant, and its answer returns after another. Its version is the decimal count of applied writes, so every applied
/// write gives it a version it never had (MS-I5), and absent reads as <see langword="null"/> while empty reads as
/// present (MS-I2).
/// </para>
/// <para>
/// Any exception leaves a write's outcome unknown, and its type, <see cref="IOException"/> or
/// <see cref="TimeoutException"/>, is drawn independently of the outcome: the request was lost or met an outage, and
/// never applies; or it applied and its answer was lost; or it lands after a random delay, applying only if the register
/// still holds the version it named (MS-I3). A token cancelled before the call sends nothing; one cancelled after it
/// ends the caller's wait with <see cref="OperationCanceledException"/> while the request may still apply, and a
/// mismatch is reported only while the token is not cancelled (metastore.md §6). A write copies its value before it is
/// dispatched, and an applied write replaces the register's array rather than changing it, so a read wraps the array it
/// finds without a copy.
/// </para>
/// <para>
/// The register tracks itself in its environment; disposal disarms every request in flight and every delayed landing.
/// </para>
/// </remarks>
public sealed class SimulatedConditionalRegister : IConditionalRegister, IAsyncDisposable
{
    private readonly SimulationEnvironment _environment;
    private readonly SimulationRandom _random;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stopped;
    private readonly List<byte[]> _applied = [];
    private readonly List<long> _appliedDispatches = [];
    private readonly List<int> _appliedWriters = [];
    private byte[]? _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedConditionalRegister"/> class, absent, and tracks it in
    /// <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="name">The register's name in the trace, which also selects its random source.</param>
    public SimulatedConditionalRegister(SimulationEnvironment environment, string name)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _environment = environment;
        _random = environment.CreateRandom(name);
        _stopped = _stopping.Token;
        Name = name;
        _ = environment.Track(this);
    }

    /// <summary>
    /// Gets the register's name in the trace.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets the probability that a write's outcome is unknown.
    /// </summary>
    public double UnknownOutcomeRate { get; set; }

    /// <summary>
    /// Gets or sets the number of writes, from the next, whose request is lost: each fails with an unknown outcome and
    /// never applies, drawing no random number, so a scenario forces an unknown outcome by construction.
    /// </summary>
    public int LoseNextWrites { get; set; }

    /// <summary>
    /// Gets or sets the probability that a write which applies answers <see cref="RegisterWriteOutcome.Mismatch"/>: a
    /// backend SDK's first attempt applied and its answer was lost, and its retry, naming the same version, mismatched.
    /// </summary>
    public double AppliedMismatchRate { get; set; }

    /// <summary>
    /// Gets or sets the probability that a read fails.
    /// </summary>
    public double ReadFailureRate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the backend is reachable; while it is not, every request fails before it
    /// reaches the register.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets the longest latency of a request, and separately of its answer.
    /// </summary>
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Gets or sets the longest delay after which a delayed write lands.
    /// </summary>
    public TimeSpan MaxLandingDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets or sets a value indicating whether a request and its answer yield to the scheduler in place of waiting a random
    /// latency: each still takes effect at one instant, between two scheduling points, but every interleaving of
    /// concurrent requests is the scheduler's choice, which an exploration enumerates, rather than the order of their
    /// latency draws (verification.md §4.1).
    /// </summary>
    public bool YieldsForLatency { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a write checks its expected version in one request and writes
    /// unconditionally in a second, so a concurrent write which lands between them is overwritten: a race mutant, which
    /// only an interleaving between the two requests reveals and the conformance kit's explored schedules must flag.
    /// </summary>
    public bool ChecksBeforeWritingMutant { get; set; }

    /// <summary>
    /// Gets the values of the applied writes, in the order they applied.
    /// </summary>
    public IReadOnlyList<byte[]> AppliedWrites => _applied;

    /// <summary>
    /// Gets, for each applied write, in the order they applied, the number of writes dispatched before it.
    /// </summary>
    public IReadOnlyList<long> AppliedDispatches => _appliedDispatches;

    /// <summary>
    /// Gets, for each applied write, in the order they applied, the writer which dispatched it: the one a
    /// <see cref="Writer"/> view names, or 0 for a write through the register itself.
    /// </summary>
    public IReadOnlyList<int> AppliedWriters => _appliedWriters;

    /// <summary>
    /// Gets the number of writes dispatched to the register.
    /// </summary>
    public long Dispatches { get; private set; }

    /// <summary>
    /// Gets the number of writes whose outcome was unknown.
    /// </summary>
    public int UnknownOutcomes { get; private set; }

    /// <summary>
    /// Gets the number of writes which applied although their caller saw an unknown outcome.
    /// </summary>
    public int AppliedUnknownOutcomes { get; private set; }

    /// <summary>
    /// Gets the number of writes which applied and answered <see cref="RegisterWriteOutcome.Mismatch"/>.
    /// </summary>
    public int AppliedMismatches { get; private set; }

    /// <summary>
    /// Gets the number of writes scheduled to land after their caller saw an unknown outcome.
    /// </summary>
    public int DelayedLandings { get; private set; }

    /// <summary>
    /// Gets the number of requests which failed.
    /// </summary>
    public int Failures { get; private set; }

    /// <summary>
    /// Gets the register's current version, or <see langword="null"/> while it is absent.
    /// </summary>
    public MetaStoreVersion? Version => _value is null ? null : CreateVersion(_applied.Count);

    /// <inheritdoc/>
    public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = new TaskCompletionSource<MetaStoreEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunAsync(answer, Read);
        return await WaitAsync(answer, cancellationToken);
    }

    /// <summary>
    /// Returns a view of the register whose writes it records as <paramref name="writer"/>'s.
    /// </summary>
    /// <param name="writer">The writer the view's writes are recorded as.</param>
    /// <returns>The view.</returns>
    public IConditionalRegister Writer(int writer) => new WriterView(this, writer);

    /// <inheritdoc/>
    public ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        CancellationToken cancellationToken = default) =>
        WriteAsync(expectedVersion, value, writer: 0, cancellationToken);

    private async ValueTask<RegisterWriteOutcome> WriteAsync(
        MetaStoreVersion? expectedVersion,
        ReadOnlySequence<byte> value,
        int writer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = value.ToArray();
        var dispatch = Dispatches++;
        RegisterWriteOutcome outcome;
        if (ChecksBeforeWritingMutant)
        {
            var check = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = RunAsync(check, () => new Outcome<bool>(Matches(expectedVersion), null));
            var matches = await WaitAsync(check, cancellationToken);
            var answer = new TaskCompletionSource<RegisterWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = RunAsync(answer, () => Write(expectedVersion, copy, dispatch, writer, checkResult: matches));
            outcome = await WaitAsync(answer, cancellationToken);
        }
        else
        {
            var answer = new TaskCompletionSource<RegisterWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = RunAsync(answer, () => Write(expectedVersion, copy, dispatch, writer, checkResult: null));
            outcome = await WaitAsync(answer, cancellationToken);
        }

        // A mismatch is reported only while the caller's token is not cancelled (metastore.md §6).
        if (outcome.Value is RegisterWriteOutcome.Mismatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return outcome;
    }

    /// <summary>
    /// Disarms every request in flight and every delayed landing.
    /// </summary>
    /// <returns>A task which completes once the register has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync();
            _stopping.Dispose();
        }
    }

    // MS-I5 (metastore.md §5): the count of applied writes only grows, so a version is never reissued.
    private static MetaStoreVersion CreateVersion(int applied) => new(applied.ToString(CultureInfo.InvariantCulture));

    private static string Describe(MetaStoreVersion? version) => version is null ? "absent" : Invariant($"version {version}");

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// Waits for a request's answer until the caller's token is cancelled, which ends the wait while the request goes on;
    /// the answer and the cancellation race, and the first wins.
    /// </summary>
    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> answer, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _ = answer.TrySetException(new OperationCanceledException(cancellationToken)));
        return await answer.Task;
    }

    /// <summary>
    /// Carries one request to the register and its answer back, independently of the caller, who may stop waiting.
    /// </summary>
    private async Task RunAsync<T>(TaskCompletionSource<T> answer, Func<Outcome<T>> execute)
    {
        Outcome<T> outcome = default;
        try
        {
            await DelayAsync(MaxLatency);
            outcome = execute();
            await DelayAsync(MaxLatency);
            if (outcome.Failure is not null)
            {
                _ = answer.TrySetException(outcome.Failure);
            }
            else if (!answer.TrySetResult(outcome.Result!))
            {
                // The caller stopped waiting: the register releases the entry no one awaits (conventions.md §1).
                (outcome.Result as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            (outcome.Result as IDisposable)?.Dispose();
            _ = answer.TrySetException(new ObjectDisposedException(Name, "The register stopped."));
        }
    }

    private Outcome<MetaStoreEntry?> Read()
    {
        if (!IsAvailable || _random.Chance(ReadFailureRate))
        {
            Record("read failed");
            return Outcome<MetaStoreEntry?>.Failed(Failure());
        }

        if (_value is null)
        {
            return new(null, null);
        }

#pragma warning disable CA2000 // The answer carries the entry to its reader, or the register releases it when no one awaits it.
        return new(new MetaStoreEntry(CreateVersion(_applied.Count), ArcBuffer.Wrap(_value)), null);
#pragma warning restore CA2000
    }

    private Outcome<RegisterWriteOutcome> Write(MetaStoreVersion? expectedVersion, byte[] value, long dispatch, int writer, bool? checkResult)
    {
        if (!IsAvailable)
        {
            Record(Invariant($"write at {Describe(expectedVersion)} failed: unavailable"));
            return Outcome<RegisterWriteOutcome>.Failed(Failure());
        }

        if (LoseNextWrites > 0)
        {
            LoseNextWrites--;
            UnknownOutcomes++;
            Record(Invariant($"write at {Describe(expectedVersion)}: request lost, as scripted"));
            return Outcome<RegisterWriteOutcome>.Failed(Failure());
        }

        bool Apply() => checkResult is { } matches
            ? matches && ApplyUnconditional(value, dispatch, writer)
            : TryApply(expectedVersion, value, dispatch, writer);

        if (!_random.Chance(UnknownOutcomeRate))
        {
            if (!Apply())
            {
                return new(RegisterWriteOutcome.Mismatch.Instance, null);
            }

            if (_random.Chance(AppliedMismatchRate))
            {
                // A mismatch asserts only that this write can no longer land (metastore.md §2.2).
                AppliedMismatches++;
                Record(Invariant($"write at {Describe(expectedVersion)}: applied, a retry mismatched"));
                return new(RegisterWriteOutcome.Mismatch.Instance, null);
            }

            return new(new RegisterWriteOutcome.Applied(CreateVersion(_applied.Count)), null);
        }

        UnknownOutcomes++;
        switch (_random.Next(3))
        {
            case 0:
                Record(Invariant($"write at {Describe(expectedVersion)}: request lost"));
                break;
            case 1:
                var applied = Apply();
                AppliedUnknownOutcomes += applied ? 1 : 0;
                Record(Invariant($"write at {Describe(expectedVersion)}: {(applied ? "applied" : "mismatched")}, answer lost"));
                break;
            default:
                DelayedLandings++;
                var delay = _random.NextTimeSpan(MaxLandingDelay);
                Record(Invariant($"write at {Describe(expectedVersion)}: lands in {delay.TotalMilliseconds:0.###} ms"));
                _ = LandAsync(expectedVersion, value, dispatch, writer, delay);
                break;
        }

        return Outcome<RegisterWriteOutcome>.Failed(Failure());
    }

    /// <summary>
    /// Returns the exception which reports a failed request, its type drawn independently of the outcome.
    /// </summary>
    private Exception Failure()
    {
        Failures++;
        return _random.Chance(0.5)
            ? new IOException(Invariant($"The request to the simulated register {Name} failed."))
            : new TimeoutException(Invariant($"The request to the simulated register {Name} timed out."));
    }

    private async Task LandAsync(MetaStoreVersion? expectedVersion, byte[] value, long dispatch, int writer, TimeSpan delay)
    {
        try
        {
            await Task.Delay(SimulationEnvironment.WholeMilliseconds(delay), SimulationEnvironment.Clock, _stopped);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var applied = TryApply(expectedVersion, value, dispatch, writer);
        AppliedUnknownOutcomes += applied ? 1 : 0;
        Record(Invariant($"delayed write at {Describe(expectedVersion)} {(applied ? "applied" : "mismatched")}"));
    }

    private bool Matches(MetaStoreVersion? expectedVersion) =>
        expectedVersion is null ? _value is null : _value is not null && expectedVersion == CreateVersion(_applied.Count);

    /// <summary>
    /// Applies a write iff the register holds <paramref name="expectedVersion"/>, or is absent for <see langword="null"/>
    /// (MS-I1, MS-I2): the write's linearization point.
    /// </summary>
    private bool TryApply(MetaStoreVersion? expectedVersion, byte[] value, long dispatch, int writer) =>
        Matches(expectedVersion) && ApplyUnconditional(value, dispatch, writer);

    private bool ApplyUnconditional(byte[] value, long dispatch, int writer)
    {
        _value = value;
        _applied.Add(value);
        _appliedDispatches.Add(dispatch);
        _appliedWriters.Add(writer);
        return true;
    }

    private Task DelayAsync(TimeSpan maximum) =>
        YieldsForLatency
            ? YieldAsync()
            : maximum <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(SimulationEnvironment.WholeMilliseconds(_random.NextTimeSpan(maximum)), SimulationEnvironment.Clock, _stopped);

    private async Task YieldAsync()
    {
        await Task.Yield();
        _stopped.ThrowIfCancellationRequested();
    }

    private void Record(string entry) => _environment.Trace.Record(Invariant($"register {Name}: {entry}"));

    private readonly record struct Outcome<T>(T? Result, Exception? Failure)
    {
        public static Outcome<T> Failed(Exception failure) => new(default, failure);
    }

    /// <summary>
    /// The register as one writer sees it: its writes are recorded as that writer's.
    /// </summary>
    private sealed class WriterView(SimulatedConditionalRegister register, int writer) : IConditionalRegister
    {
        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default) => register.ReadAsync(cancellationToken);

        public ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
            register.WriteAsync(expectedVersion, value, writer, cancellationToken);
    }
}
