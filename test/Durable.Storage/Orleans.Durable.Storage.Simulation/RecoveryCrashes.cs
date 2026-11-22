using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Crashes nodes while one recovery runs (docs/spec/verification.md §5.1, §7.1, §7.3): first the node which first begins
/// to serve the recovery's seal, as the seal's WAL write leaves for its disk or once it is durable, and then, once every
/// node runs again, the node which serves the recovery's first recovery write, likewise, unless a node serves one of the
/// recovery's first reads before. Each crashed node restarts after a short downtime.
/// </summary>
/// <remarks>
/// <para>
/// A process crash keeps the writes in flight, and a crash just after a write completes races the answers which waited
/// for it, so the seal or recovery write which issued it may land without an answer, which the restarted node, or an
/// answer the crashed process gave first, shows; an operating-system crash loses or tears a write in flight. The
/// recovery repeats every request its replicas did not answer, so it completes all the same, and only one node is ever
/// down at once, so it needs no answer from the crashed node while the others run (ledger-protocol §6.5). The run's
/// trace records each crash, whether the request was still being served when it came, and each restart.
/// </para>
/// <para>
/// Every choice comes from a random source of the crashes' own, drawn in the order of the recovery's requests, so the
/// seed decides them.
/// </para>
/// </remarks>
internal sealed class RecoveryCrashes
{
    private static readonly TimeSpan MaximumDowntime = TimeSpan.FromMilliseconds(20);
    private readonly SimulationEnvironment _environment;
    private readonly IReadOnlyList<LedgerStoreNode> _nodes;
    private readonly LedgerId _ledger;
    private readonly string _recovery;
    private readonly SimulationRandom _random;
    private readonly int _readsBeforeCrash;
    private readonly List<Task> _restarts = [];
    private readonly List<TimeSpan> _faults = [];
    private readonly TaskCompletionSource _disarmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stage _stage = Stage.Seal;
    private int _reads;
    private bool _ended;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryCrashes"/> class, armed for <paramref name="recovery"/>'s
    /// requests to the replicas of <paramref name="ledger"/>.
    /// </summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="replicas">The transport whose nodes serve the recovery.</param>
    /// <param name="ledger">The ledger.</param>
    /// <param name="recovery">The recovery's client.</param>
    /// <param name="window">The ledger's window <c>K</c>: the crash during the recovery comes within its first <c>K</c> reads.</param>
    public RecoveryCrashes(SimulationEnvironment environment, SimulatedLedgerReplicaTransport replicas, LedgerId ledger, string recovery, int window)
    {
        _environment = environment;
        _nodes = replicas.Nodes;
        _ledger = ledger;
        _recovery = recovery;
        _random = environment.CreateRandom(Invariant($"recovery-crashes/{recovery}"));
        _readsBeforeCrash = 1 + _random.Next(window);
        replicas.OnServing(Serving);
    }

    private enum Stage
    {
        Seal,
        Restarting,
        Running,
        Done,
    }

    /// <summary>
    /// Gets the number of nodes crashed.
    /// </summary>
    public int Crashes { get; private set; }

    /// <summary>
    /// Gets the elapsed virtual time of each crash and each restart, in order.
    /// </summary>
    public IReadOnlyList<TimeSpan> Faults => _faults;

    /// <summary>
    /// Disarms the crashes not yet begun, once the recovery has ended.
    /// </summary>
    public void Disarm()
    {
        _ended = true;
        _stage = Stage.Done;
        _ = _disarmed.TrySetResult();
    }

    /// <summary>
    /// Waits until both crashes began, or the crashes were disarmed, and every crashed node restarted.
    /// </summary>
    /// <returns>A task which completes once no crash remains to come and every crashed node runs.</returns>
    public async Task SettledAsync()
    {
        await _disarmed.Task;
        await SimulationTasks.WhenAll([.. _restarts]);
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private static string Describe(ReplicaServing serving) => serving.Operation switch
    {
        LedgerOperation.Seal => "seal",
        LedgerOperation.Read => Invariant($"read {serving.Address}"),
        LedgerOperation.RecoveryWrite => Invariant($"recovery write {serving.Address}"),
        LedgerOperation.WriterWrite or LedgerOperation.TailRead => throw new ArgumentOutOfRangeException(nameof(serving), serving.Operation, "No recovery sends this operation."),
        _ => throw new ArgumentOutOfRangeException(nameof(serving), serving.Operation, "The operation is unknown."),
    };

    /// <summary>
    /// Watches each delivery a node begins to serve, on the node's lane, and starts a crash when the delivery is the one
    /// the current stage waits for.
    /// </summary>
    private void Serving(ReplicaServing serving)
    {
        if (serving.Client != _recovery || serving.Ledger != _ledger)
        {
            return;
        }

        if (_stage == Stage.Seal && serving.Operation == LedgerOperation.Seal)
        {
            _stage = Stage.Restarting;
            _restarts.Add(CrashAndRestartAsync(serving, Stage.Running));
        }
        else if (_stage == Stage.Running && serving.Operation is LedgerOperation.Read or LedgerOperation.RecoveryWrite && _nodes.All(static node => node.IsUp))
        {
            if (serving.Operation == LedgerOperation.Read && ++_reads < _readsBeforeCrash)
            {
                return;
            }

            _stage = Stage.Done;
            _ = _disarmed.TrySetResult();
            _restarts.Add(CrashAndRestartAsync(serving, Stage.Done));
        }
    }

    /// <summary>
    /// Crashes <paramref name="serving"/>'s node as its next WAL write leaves for its disk, or, by an even draw, once the
    /// disk completes that write, so the crash races the answers which waited for it: at once for a read, which writes
    /// nothing, and otherwise within two disk latencies, which covers a seal's wait for the writes before it. The node
    /// then restarts after a short downtime, and the crashes move to <paramref name="next"/>.
    /// </summary>
    private async Task CrashAndRestartAsync(ReplicaServing serving, Stage next)
    {
        var node = serving.Node;
        var operatingSystem = _random.Chance(0.5);
        var durable = _random.Chance(0.5);
        var downtime = _random.NextTimeSpan(TimeSpan.FromMilliseconds(1), MaximumDowntime);
        var limit = serving.Operation == LedgerOperation.Read ? TimeSpan.Zero : 2 * node.MaxDiskLatency + TimeSpan.FromMilliseconds(1);
        var crashed = await node.CrashOnNextWalWriteAsync(operatingSystem, durable, limit, () =>
        {
            var operation = Describe(serving);
            Record(serving.Operation == LedgerOperation.Seal
                ? serving.IsComplete
                    ? Invariant($"crash {node.Address} once it answered {_recovery}'s seal")
                    : Invariant($"crash {node.Address} while it serves {_recovery}'s seal")
                : _ended
                    ? Invariant($"crash {node.Address} once {_recovery} ended")
                    : Invariant($"crash {node.Address} while {_recovery} runs, as it serves its {operation}{(serving.IsComplete ? ", answered" : string.Empty)}"));
            _faults.Add(_environment.Trace.Elapsed);
        });
        if (crashed)
        {
            Crashes++;
            await Task.Delay(downtime, SimulationEnvironment.Clock, CancellationToken.None);
            await node.StartAsync(CancellationToken.None);
            _faults.Add(_environment.Trace.Elapsed);
            Record(Invariant($"restart {node.Address} after its crash during {_recovery}"));
        }

        if (_stage == Stage.Restarting)
        {
            _stage = next;
        }
    }

    private void Record(string entry) => _environment.Trace.Record(entry);
}
