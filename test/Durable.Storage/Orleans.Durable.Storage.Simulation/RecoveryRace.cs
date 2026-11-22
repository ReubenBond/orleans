using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// A writer, recovery executors, a seal checker, and a reader of the recovered positions, racing the partition of one
/// replica, the pause of another, the crashes of nodes while they serve the first recovery's seal and while that
/// recovery runs, and message loss and duplication until the faults heal (test/Durable.Storage/docs/spec/verification.md §7.1).
/// </summary>
/// <remarks>
/// The race is the client process's work, so it runs on the client node's scheduler lane: the build rewrites the kit, so
/// each of its awaits, timers, and combinators is a controlled operation of that lane, where the test assembly's would
/// run through the BCL. A check which fails throws an <see cref="InvariantViolationException"/>, which faults the race's
/// task and so fails the scenario which awaits it.
/// </remarks>
/// <param name="environment">The simulation.</param>
/// <param name="replicas">The transport from the client node to the replicas, whose ledger trace logs the race.</param>
/// <param name="writer">The writer, appending through its own <see cref="Ledger"/>.</param>
/// <param name="checker">The seal checker's <see cref="Ledger"/>.</param>
/// <param name="reader">The reader's <see cref="Ledger"/>.</param>
/// <param name="ledger">The ledger.</param>
/// <param name="options">The replicas' options, whose window <c>K</c> bounds the writer's appends in flight.</param>
/// <param name="quorum">The quorum <c>Q</c> each recovery's <see cref="Ledger"/> uses.</param>
/// <param name="writes">How many addresses the writer appends.</param>
/// <param name="payload">The value the writer appends at an address.</param>
/// <param name="cancellationToken">The scenario's token, which nothing cancels.</param>
internal sealed class RecoveryRace(
    SimulationEnvironment environment,
    SimulatedLedgerReplicaTransport replicas,
    LedgerWriter writer,
    Ledger checker,
    Ledger reader,
    LedgerId ledger,
    LedgerReplicaOptions options,
    int quorum,
    int writes,
    Func<long, byte[]> payload,
    CancellationToken cancellationToken)
{
    private readonly SimulationRandom _random = environment.Random;
    private readonly LedgerTrace _ledger = replicas.LedgerTrace;
    private readonly LedgerStoreNode[] _nodes = [.. replicas.Nodes];
    private readonly HashSet<long> _written = [];
    private readonly List<long> _ends = [];
    private long _prefix;
    private TimeSpan? _healed;
    private bool _checking = true;

    /// <summary>
    /// Starts the race on the client node's scheduler lane.
    /// </summary>
    /// <returns>A task which completes once the race ends, faulted with the first check which failed.</returns>
    public Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas.Client.Context.SchedulerLane.Enqueue(() => _ = RunOnLaneAsync());
        return completion.Task;

        async Task RunOnLaneAsync()
        {
            try
            {
                await RaceAsync();
                _ = completion.TrySetResult();
            }
#pragma warning disable CA1031 // The race's failure is the scenario's outcome.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _ = completion.TrySetException(exception);
            }
        }
    }

    private async Task RaceAsync()
    {
        var faulty = _random.Choose(_nodes);
        var slow = _random.Choose(_nodes.Where(node => node != faulty).ToArray());
        var checks = CheckSealsAsync();

        // A node crashes while it serves the first recovery's seal, and another while that recovery runs, before the
        // faults heal; the healing waits for both to restart.
        var crashes = new RecoveryCrashes(environment, replicas, ledger, "recovery-1", options.WindowSize);
        var racing = RecoverAsync("recovery-1", maximumDelayMilliseconds: 400, closes: false, crashes);
        await HealAsync(Task.WhenAll(WriteAsync(), PartitionAsync(faulty), PauseAsync(slow), crashes.SettledAsync()));
        await racing;

        // Once the faults heal, a recovery needs only max(Q, N − Q + 1) responsive replicas (ledger-protocol §6.5). The
        // last recovery closes the ledger in the map at its candidate end; the first wrote no map.
        await RecoverAsync("recovery-2", maximumDelayMilliseconds: 20, closes: true);
        _checking = false;
        await checks;

        // A seal check begun after the recoveries completed reports sealed (LP-I9).
        var tail = await _ledger.CheckSealAsync(checker, "checker", ledger, cancellationToken);
        Require(tail.Tail.IsSealed, $"LP-I9: a seal check begun after the recoveries completed reported {tail.Tail}.");
        Record($"writer prefix {_prefix}, {_written.Count} written; ends [{string.Join(", ", _ends)}]");
    }

    /// <summary>
    /// Appends the addresses in order through the writer, appending address <c>p</c> only once its completed prefix is at
    /// least <c>p + 1 − K</c>, so its lifetime has room and grants the slot <c>p</c> at once, until every address is
    /// written or an append meets a seal or cannot complete. The writer repeats each write after a failure the faults
    /// explain, with the prefix it has completed by each attempt (LP-I12, LR-I1).
    /// </summary>
    private async Task WriteAsync()
    {
        var pending = new Dictionary<Task<AppendOutcome>, long>();
        var next = 0L;
        var stopped = false;
        await DelayAsync(10);
        while (true)
        {
            while (!stopped && next < writes && next < _prefix + options.WindowSize)
            {
                pending.Add(AppendAsync(next), next);
                next++;
            }

            if (pending.Count == 0)
            {
                return;
            }

            var completed = await Task.WhenAny(pending.Keys);
            var address = pending[completed];
            _ = pending.Remove(completed);
            switch ((await completed).Value)
            {
                case AppendOutcome.Appended appended:
                    Require(appended.Slot == new Slot(address), $"Address {address} was appended at {appended.Slot}.");
                    _ = _written.Add(address);
                    while (_written.Contains(_prefix))
                    {
                        _prefix++;
                    }

                    break;
                case AppendOutcome.Sealed sealedAt:
                    Require(sealedAt.Slot == new Slot(address), $"Address {address} met a seal at {sealedAt.Slot}.");
                    stopped = true;
                    break;
                case AppendOutcome.Unresolved unresolved:
                    // A stopped lifetime grants no slot; one granted is the address's.
                    Require(unresolved.Slot is null || unresolved.Slot == new Slot(address), $"Address {address} was unresolved at {unresolved.Slot}.");
                    stopped = true;
                    break;
                default:
                    throw new InvalidOperationException("An append outcome holds no case.");
            }
        }
    }

    /// <summary>
    /// Acquires the writer's next slot and appends the value of <paramref name="address"/> at it, the slot the writer's
    /// lifetime grants it, since the lifetime has room; an acquisition after the lifetime stopped is unresolved with no
    /// slot (ledger-protocol §2.3, §2.4).
    /// </summary>
    private async Task<AppendOutcome> AppendAsync(long address)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(payload(address));
        using var value = buffer.PeekSlice(buffer.Length);
        return await writer.AcquireAsync(cancellationToken) is { } slot
            ? await writer.AppendAsync(slot, value, cancellationToken)
            : new AppendOutcome(new AppendOutcome.Unresolved(Slot: null));
    }

    /// <summary>
    /// Recovers the ledger with a <see cref="Ledger"/> of its own, which repeats every replica request until it answers, so it
    /// admits no failure; once the faults have healed it completes within <see cref="RecoveryLiveness"/>'s bound,
    /// and every position below its end reads back the writer's value (LP-I7, LR-I5). With <paramref name="closes"/>,
    /// its conditional write closes the ledger in the map at its end, the recorded <c>T</c> (LG-I9). Once it ends, it
    /// disarms <paramref name="crashes"/>, the crashes injected while it runs.
    /// </summary>
    private async Task RecoverAsync(string client, int maximumDelayMilliseconds, bool closes, RecoveryCrashes? crashes = null)
    {
        await DelayAsync(maximumDelayMilliseconds);
        Slot end;
        var started = environment.Trace.Elapsed;
        await using (var replica = replicas.OpenLedger(ledger, options, quorum, client))
        {
            end = (await _ledger.RecoverAsync(replica, client, ledger, cancellationToken)).End;
        }

        crashes?.Disarm();

        if (closes)
        {
            _ledger.Closed(ledger, client, end.Value);
        }

        var ended = environment.Trace.Elapsed;
        _ends.Add(end.Value);
        if (_healed is { } healed && ended > healed)
        {
            var roundTrip = RecoveryLiveness.RoundTrip(environment, _nodes.Max(static node => node.MaxDiskLatency));
            var (from, bound) = started >= healed
                ? (started, RecoveryLiveness.Bound(options.WindowSize, roundTrip))
                : (healed, RecoveryLiveness.BoundFromHealing(options.WindowSize, roundTrip, replicas.Deadline));
            var took = ended - from;
            Record($"{client} took {took.TotalMilliseconds:0.###} ms of its {bound.TotalMilliseconds:0.###} ms bound");
            Require(took <= bound, $"{client} took {took.TotalMilliseconds} ms after the faults healed, over its bound of {bound.TotalMilliseconds} ms.");
        }

        await ReadBelowAsync(end);
    }

    /// <summary>
    /// Reads every position below a recovery's end, repeating a read which fails as the faults explain: each holds the
    /// value the writer wrote there (LP-I7, LR-I5).
    /// </summary>
    private async Task ReadBelowAsync(Slot end)
    {
        for (var address = 0L; address < end.Value; address++)
        {
            while (true)
            {
                try
                {
                    var read = await reader.ReadAsync(new Slot(address), cancellationToken);
                    Require(read.Case == ReplicaReadCase.Batch, $"LP-I7: address {address}, below an end at {end}, read {read.Case}.");
                    using var value = read.Batch;
                    Require(value.ToArray().AsSpan().SequenceEqual(payload(address)), $"LP-I7: address {address}, below an end at {end}, read another value than the writer's.");
                    break;
                }
                catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
                {
                    await DelayAsync(10);
                }
            }
        }
    }

    /// <summary>
    /// Checks the seal at random times until the recoveries end; the ledger trace requires a check begun after a
    /// recovery completed to report sealed (LP-I9).
    /// </summary>
    private async Task CheckSealsAsync()
    {
        while (_checking)
        {
            await DelayAsync(40);
            try
            {
                _ = await _ledger.CheckSealAsync(checker, "checker", ledger, cancellationToken);
            }
            catch (AggregateException failure) when (QuorumFailures.IsAdmitted(failure, mediaDamage: false))
            {
                // No quorum answered (ledger-replicas §6.4).
            }
        }
    }

    private async Task PartitionAsync(LedgerStoreNode node)
    {
        await DelayAsync(20);
        node.Isolate();
        await DelayAsync(30);
        node.Reconnect();
    }

    private async Task PauseAsync(LedgerStoreNode node)
    {
        await DelayAsync(20);
        var pause = _random.NextTimeSpan(TimeSpan.FromMilliseconds(100));
        node.Pause(pause);
        await Task.Delay(pause + TimeSpan.FromMilliseconds(1), SimulationEnvironment.Clock, cancellationToken);
    }

    /// <summary>
    /// Stops message loss and duplication at a random time after the writer ends, the partition heals, the paused node
    /// resumes, and the crashed nodes restart, so a recovery may run on, or start, over healed faults.
    /// </summary>
    private async Task HealAsync(Task faults)
    {
        await faults;
        await Task.Delay(TimeSpan.FromMilliseconds(1) + _random.NextTimeSpan(TimeSpan.FromMilliseconds(20)), SimulationEnvironment.Clock, cancellationToken);
        environment.Network.MessageDropRate = 0;
        environment.Rpc.DuplicationRate = 0;
        _healed = environment.Trace.Elapsed;
        Record($"faults healed");
    }

    private Task DelayAsync(int maximumMilliseconds) =>
        Task.Delay(_random.NextTimeSpan(TimeSpan.FromMilliseconds(maximumMilliseconds)), SimulationEnvironment.Clock, cancellationToken);

    private void Record(FormattableString entry) => environment.Trace.Record(entry.ToString(CultureInfo.InvariantCulture));

    private static void Require(bool holds, FormattableString violation)
    {
        if (!holds)
        {
            throw new InvariantViolationException(violation.ToString(CultureInfo.InvariantCulture));
        }
    }
}
