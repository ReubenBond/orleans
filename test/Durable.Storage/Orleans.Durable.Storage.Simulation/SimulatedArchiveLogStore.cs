using System.Buffers;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// A process's view of an archive-log store in simulation (archive-logs.md §6, §10; docs/spec/verification.md §7.2): it
/// delays each write and properties read by a random latency, now and then long enough that the request lands as a
/// straggler's, after its drain's segment completed elsewhere; a straggler's write runs detached from its caller, so a
/// caller cancelled as a crash abandons it and it lands later, after the drain restarted; it holds every request at a gate a
/// scenario closes; and it refuses the writes of the logs a scenario names, as a store which no retry makes accept them.
/// </summary>
/// <remarks>
/// The kit is rewritten with the simulation's closure, so its waits run as controlled operations; a wrapper in an
/// uninstrumented test assembly, awaited from the code under test, would resume it off the simulation's thread.
/// </remarks>
public sealed class SimulatedArchiveLogStore(IArchiveLogStore inner, SimulationEnvironment environment, string name) : IArchiveLogStore
{
    private readonly SimulationRandom _random = environment.CreateRandom(name);
    private TaskCompletionSource? _gate;
    private TaskCompletionSource? _waiting;

    /// <summary>Gets the logs whose writes the store refuses.</summary>
    public HashSet<string> Refused { get; } = [with(StringComparer.Ordinal)];

    /// <summary>Gets or sets the probability that a request waits up to <see cref="StragglerDelay"/>, as a straggler's.</summary>
    public double StragglerRate { get; set; }

    /// <summary>Gets or sets the longest latency of a request.</summary>
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>Gets or sets the longest delay of a straggler's request.</summary>
    public TimeSpan StragglerDelay { get; set; } = TimeSpan.FromMilliseconds(60);

    /// <summary>Gets the number of straggler's writes which landed after their caller was cancelled: a crashed drain's.</summary>
    public int LandedAfterCrash { get; private set; }

    /// <inheritdoc/>
    public ArchiveLogLayout Layout => inner.Layout;

    /// <summary>Closes the gate, and returns a task which completes once a request waits at it.</summary>
    public Task Hold()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _waiting.Task;
    }

    /// <summary>Opens the gate.</summary>
    public void Release() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

    /// <inheritdoc/>
    public bool AdmitsName(string name) => inner.AdmitsName(name);

    /// <inheritdoc/>
    public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.ReadAsync(name, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken)
    {
        await DelayAsync(cancellationToken);
        return await inner.GetPropertiesAsync(name, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
    {
        if (_random.Chance(StragglerRate))
        {
            // The caller may release its buffers once it abandons the request, so the straggler carries copies.
            ReadOnlySequence<byte>[] units = [.. run.Select(static unit => new ReadOnlySequence<byte>(unit.ToArray()))];
            return await StraggleAsync(name, () => inner.AppendAsync(name, from, units, CancellationToken.None), cancellationToken);
        }

        await DelayAsync(cancellationToken);
        ThrowIfRefused(name);
        return await inner.AppendAsync(name, from, run, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken)
    {
        if (_random.Chance(StragglerRate))
        {
            var copy = new ReadOnlySequence<byte>(content.ToArray());
            return await StraggleAsync(name, () => inner.OpenAsync(name, from, opener, copy, CancellationToken.None), cancellationToken);
        }

        await DelayAsync(cancellationToken);
        ThrowIfRefused(name);
        return await inner.OpenAsync(name, from, opener, content, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken) => inner.ListGenerationsAsync(continuation, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken) => inner.DeleteGenerationAsync(log, generation, cancellationToken);

    private void ThrowIfRefused(ArchiveLogName name)
    {
        if (Refused.Contains(name.Value))
        {
            throw new ArchiveLogRefusedException($"The store refuses the log '{name}'.");
        }
    }

    /// <summary>
    /// Runs a straggler's write detached from its caller's cancellation: the caller, cancelled, stops waiting for it, and the
    /// write lands at its delay all the same, as a request already sent does when its sender crashes.
    /// </summary>
    private async Task<ArchiveLogWriteOutcome> StraggleAsync(ArchiveLogName name, Func<ValueTask<ArchiveLogWriteOutcome>> write, CancellationToken cancellationToken)
    {
        await GateAsync();
        var landing = LandAsync(name, write, cancellationToken);
        try
        {
            return await landing.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveAsync(landing);
            throw;
        }
    }

    private async Task<ArchiveLogWriteOutcome> LandAsync(ArchiveLogName name, Func<ValueTask<ArchiveLogWriteOutcome>> write, CancellationToken caller)
    {
        await Task.Delay(SimulationEnvironment.WholeMilliseconds(_random.NextTimeSpan(StragglerDelay)), SimulationEnvironment.Clock, CancellationToken.None);
        ThrowIfRefused(name);
        var outcome = await write();
        LandedAfterCrash += caller.IsCancellationRequested ? 1 : 0;
        return outcome;
    }

    // An abandoned straggler's failure has no caller left to answer.
    private static async Task ObserveAsync(Task landing)
    {
        try
        {
            await landing;
        }
        catch (Exception exception) when (exception is IOException or ArchiveLogRefusedException)
        {
        }
    }

    private async Task GateAsync()
    {
        if (_gate is { } gate)
        {
            _ = _waiting?.TrySetResult();
            await gate.Task;
        }
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        await GateAsync();
        await Task.Delay(SimulationEnvironment.WholeMilliseconds(_random.NextTimeSpan(_random.Chance(StragglerRate) ? StragglerDelay : MaxLatency)), SimulationEnvironment.Clock, cancellationToken);
    }
}
