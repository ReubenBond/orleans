using System.Reflection;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Tests.Scheduling;
using OracleTracker = Orleans.Testing.Simulation.Runtime.Tests.Racing.Oracle.RaceTracker;

namespace Orleans.Testing.Simulation.Runtime.Tests.Racing;

/// <summary>
/// Model-based check of the race detector against the detector it replaced (<see cref="OracleTracker"/>), which keeps
/// one clock component per operation, a full clock per access, and every piece of state. Seeded random programs of
/// forks, continuations, hand-offs, root operations, accesses to static, object, and array-element locations, lock
/// sections, signals and waits (including a long-lived waiter on a signal many operations fire), resource releases,
/// completions, and dropped and collected objects drive both. The two decide every access alike, and name the same
/// last write whenever it races; the detector's bookkeeping holds after every step. A long-lived operation keeps every
/// program running to its last step. Lock sections are atomic, as a controlled lock's are, since a held lock suppresses
/// the scheduling points inside it. A failing program is shrunk by deleting steps, and reported with its seed and the
/// steps that remain.
/// </summary>
public sealed class RaceTrackerModelTests
{
    private const int Programs = 1_000;
    private const int StepsPerProgram = 200;

    private static readonly PropertyInfo OracleFirstRace =
        typeof(OracleTracker).GetProperty(nameof(OracleTracker.FirstRace))!;

    private static readonly string[] StaticMembers = ["Model::A", "Model::B", "Model::C"];

    // The relative frequency of each step kind.
    private static readonly (StepKind Kind, int Weight)[] Weights =
    [
        (StepKind.Fork, 5),
        (StepKind.Continue, 12),
        (StepKind.Handoff, 8),
        (StepKind.Root, 3),
        (StepKind.Access, 22),
        (StepKind.LockSection, 14),
        (StepKind.ConcurrentHolder, 4),
        (StepKind.Signal, 6),
        (StepKind.Wait, 6),
        (StepKind.HubSignal, 4),
        (StepKind.HubWait, 3),
        (StepKind.CaptureRelease, 3),
        (StepKind.ConsumeRelease, 3),
        (StepKind.Complete, 6),
        (StepKind.DropObject, 3),
        (StepKind.DropSynchronization, 2),
        (StepKind.Collect, 3),
    ];

    private enum StepKind
    {
        Fork,
        Continue,
        Handoff,
        Root,
        Access,
        LockSection,
        ConcurrentHolder,
        Signal,
        Wait,
        HubSignal,
        HubWait,
        CaptureRelease,
        ConsumeRelease,
        Complete,
        DropObject,
        DropSynchronization,
        Collect,
    }

    [Fact]
    public void ClockSlotsDecideEveryAccessAsPerOperationClocksDo()
    {
        var totals = new Counts();
        for (int seed = 0; seed < Programs; seed++)
        {
            Step[] program = Generate(seed);
            if (Execute(program, totals) is { } failure)
            {
                Step[] shrunk = Shrink(program);
                Assert.Fail(
                    $"Seed {seed} fails: {failure}{Environment.NewLine}" +
                    $"Shrunk to {shrunk.Length} steps:{Environment.NewLine}{string.Join(Environment.NewLine, shrunk)}");
            }
        }

        // Every outcome the comparison distinguishes is well represented, so none is checked vacuously.
        Assert.InRange(totals.Accesses, 50_000, int.MaxValue);
        Assert.InRange(totals.Races, totals.Accesses / 50, totals.Accesses / 2);
        Assert.InRange(totals.RacesWithLastWrite, 1_000, int.MaxValue);
        Assert.InRange(totals.RacesWithReads, 1_000, int.MaxValue);
        // Sweeps released collected objects' locations, which the programs' forced collections ensure whatever the GC's
        // own schedule.
        Assert.InRange(totals.Sweeps, 50, int.MaxValue);
    }

    private static Step[] Generate(int seed)
    {
        var random = new System.Random(seed);
        int totalWeight = Weights.Sum(static weight => weight.Weight);

        // Only a fifth of the programs force collections, which are slow.
        bool collects = seed % 5 == 0;
        var steps = new Step[StepsPerProgram];
        for (int index = 0; index < steps.Length; index++)
        {
            StepKind kind;
            do
            {
                int pick = random.Next(totalWeight);
                kind = Weights.First(weight => (pick -= weight.Weight) < 0).Kind;
            }
            while (kind == StepKind.Collect && !collects);

            steps[index] = new Step(kind, random.Next(1_000), random.Next(1_000), random.Next(1_000));
        }

        return steps;
    }

    private static string? Execute(IReadOnlyList<Step> program, Counts counts)
    {
        using var model = new Model(counts);
        try
        {
            for (int index = 0; index < program.Count; index++)
            {
                model.Run(program[index]);
                List<string> violations = model.Tracker.CheckInvariants();
                if (violations.Count != 0)
                {
                    return $"after step {index} ({program[index]}): {string.Join("; ", violations)}";
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return exception is Xunit.Sdk.XunitException ? exception.Message : exception.ToString();
        }
    }

    // Deletes steps while the program still fails.
    private static Step[] Shrink(Step[] program)
    {
        List<Step> current = [.. program];
        bool shrunk = true;
        while (shrunk)
        {
            shrunk = false;
            for (int index = current.Count - 1; index >= 0; index--)
            {
                List<Step> candidate = [.. current];
                candidate.RemoveAt(index);
                if (Execute(candidate, new Counts()) is not null)
                {
                    current = candidate;
                    shrunk = true;
                }
            }
        }

        return [.. current];
    }

    // A step's choices select among whatever exists when it runs, so the steps of a shrunk program still run.
    private readonly record struct Step(StepKind Kind, int Operation, int First, int Second);

    private sealed class Counts
    {
        public int Accesses;
        public int Races;
        public int RacesWithLastWrite;
        public int RacesWithReads;
        public int Sweeps;
    }

    private sealed class Model : IDisposable
    {
        private readonly SimulationScheduler _scheduler = SchedulerTestHarness.NewScheduler();
        private readonly OracleTracker _oracle = new();
        private readonly Counts _counts;
        private readonly List<SimulationOperation> _live = [];
        private readonly SimulationOperation _main;
        private readonly object[] _locks = [new(), new()];
        private readonly object[] _signals = [new(), new(), new()];
        private readonly object[] _targets = [new(), new(), new()];
        private readonly int[][] _arrays = [new int[2], new int[2]];
        private readonly List<(RaceTracker.Clock Tracked, Dictionary<long, long> Oracle)> _releases = [];
        private object _hub = new();
        private int _nextOffset;

        public Model(Counts counts)
        {
            _counts = counts;
            _main = Start(parent: null);
        }

        // Sweeps for collected objects from four object locations up, so short programs exercise it.
        public RaceTracker Tracker { get; } = new(minimumSweepThreshold: 4);

        public void Run(Step step)
        {
            SimulationOperation operation = _live[step.Operation % _live.Count];
            switch (step.Kind)
            {
                case StepKind.Fork:
                    Start(operation);
                    break;
                case StepKind.Continue when operation != _main:
                    // A continuation: the operation registers its successor and ends.
                    Start(operation);
                    Complete(operation);
                    break;
                case StepKind.Handoff when operation != _main:
                    // A continuation which acquires the operation's last event, as an await of a task it completes.
                    object handoff = _signals[step.First % _signals.Length];
                    SimulationOperation successor = Start(operation);
                    Signal(operation, handoff);
                    Complete(operation);
                    Wait(successor, handoff);
                    break;
                case StepKind.Root:
                    // A root operation, as a timer callback, ordered after nothing.
                    Start(parent: null);
                    break;
                case StepKind.Access:
                    Access(operation, step.First, step.Second);
                    break;
                case StepKind.LockSection:
                    LockSection(operation, step.First, step.Second);
                    break;
                case StepKind.ConcurrentHolder:
                    ConcurrentHolder(operation, step.First, step.Second);
                    break;
                case StepKind.Signal:
                    Signal(operation, _signals[step.First % _signals.Length]);
                    break;
                case StepKind.Wait:
                    Wait(operation, _signals[step.First % _signals.Length]);
                    break;
                case StepKind.HubSignal:
                    Signal(operation, _hub);
                    break;
                case StepKind.HubWait:
                    // The long-lived waiter acquires every signal of the hub so far.
                    Wait(_main, _hub);
                    break;
                case StepKind.CaptureRelease:
                    _releases.Add((Tracker.CaptureRelease(operation), _oracle.CaptureRelease(operation)));
                    break;
                case StepKind.ConsumeRelease when _releases.Count != 0:
                    int index = step.First % _releases.Count;
                    Tracker.ConsumeRelease(operation, _releases[index].Tracked);
                    _oracle.ConsumeRelease(operation, _releases[index].Oracle);
                    _releases.RemoveAt(index);
                    break;
                case StepKind.Complete when operation != _main:
                    Complete(operation);
                    break;
                case StepKind.DropObject:
                    if (step.First % 2 == 0)
                    {
                        _targets[step.Second % _targets.Length] = new object();
                    }
                    else
                    {
                        _arrays[step.Second % _arrays.Length] = new int[2];
                    }

                    break;
                case StepKind.DropSynchronization:
                    switch (step.First % 3)
                    {
                        case 0:
                            _locks[step.Second % _locks.Length] = new object();
                            break;
                        case 1:
                            _signals[step.Second % _signals.Length] = new object();
                            break;
                        default:
                            _hub = new object();
                            break;
                    }

                    break;
                case StepKind.Collect:
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    break;
                default:
                    // A step which needs a non-main operation or a pending release, and has neither, does nothing.
                    break;
            }
        }

        public void Dispose()
        {
            _counts.Sweeps += Tracker.Statistics.Sweeps;
            _scheduler.Dispose();
        }

        private SimulationOperation Start(SimulationOperation? parent)
        {
            SimulationOperation operation = _scheduler.Register("model", static () => { });
            Tracker.RegisterOperation(operation, parent);
            _oracle.RegisterOperation(operation, parent);
            _live.Add(operation);
            return operation;
        }

        private void Complete(SimulationOperation operation)
        {
            Tracker.CompleteOperation(operation);
            _live.Remove(operation);
        }

        private void Access(SimulationOperation operation, int first, int second)
        {
            RaceAccessKind kind = first % 2 == 0 ? RaceAccessKind.Read : RaceAccessKind.Write;
            (RaceMemoryLocationKind locationKind, object? target, string member, long? element) = (first / 2 % 3) switch
            {
                0 => (RaceMemoryLocationKind.StaticField, (object?)null, StaticMembers[second % StaticMembers.Length], (long?)null),
                1 => (RaceMemoryLocationKind.InstanceField, _targets[second % _targets.Length], "Model::Field", null),
                _ => (RaceMemoryLocationKind.ArrayElement, _arrays[second % _arrays.Length], "element", second / 2 % 2),
            };
            var source = new RaceSourceLocation("model", ++_nextOffset, null, -1);
            RaceTracker.LocationState location = Tracker.ResolveLocation(locationKind, target, member, element)!;
            RaceTracker.RacingAccess? actual = Tracker.RecordAccess(operation, kind, location, source, []);
            RaceMemoryLocation oracleLocation = _oracle.ResolveLocation(locationKind, target, member, element)!.Value;
            _oracle.RecordAccess(operation, kind, oracleLocation, source, []);

            // The oracle reports only its first race, so it is reset to report the next access's.
            RaceReport? expected = _oracle.FirstRace;
            OracleFirstRace.SetValue(_oracle, null);
            _counts.Accesses++;
            string access = $"{kind} by {operation.Id} at offset {source.ILOffset}";
            Assert.True(
                (expected is null) == (actual is null),
                $"{access}: the oracle reports {expected?.ToString() ?? "no race"}, the detector {actual?.ToString() ?? "none"}.");
            if (expected is null)
            {
                return;
            }

            _counts.Races++;
            if (expected.FirstAccess.Kind == RaceAccessKind.Write)
            {
                _counts.RacesWithLastWrite++;
                Assert.True(
                    actual!.Value.Kind == RaceAccessKind.Write &&
                    actual.Value.OperationId == expected.FirstAccess.OperationId &&
                    actual.Value.Source == expected.FirstAccess.Source,
                    $"{access}: the oracle names the last write {expected.FirstAccess}, the detector {actual}.");
            }
            else
            {
                _counts.RacesWithReads++;
                Assert.True(actual!.Value.Kind == RaceAccessKind.Read, $"{access}: the detector names {actual} for a race with reads.");
            }
        }

        // An atomic section under one lock or both, sometimes entered recursively, and sometimes preceded by an exit of a
        // lock the operation does not hold, which records nothing.
        private void LockSection(SimulationOperation operation, int first, int second)
        {
            object[] held = first % 3 == 0 ? _locks : [_locks[first % _locks.Length]];
            object[] entered = held.Length == 1 && first % 4 == 1 ? [held[0], held[0]] : held;
            if (second % 8 == 0)
            {
                object unheld = _locks[second / 8 % _locks.Length];
                Tracker.ExitExclusive(operation, unheld);
                _oracle.ExitSynchronization(operation, unheld);
            }

            foreach (object synchronization in entered)
            {
                Tracker.EnterExclusive(operation, synchronization);
                _oracle.EnterSynchronization(operation, synchronization);
            }

            for (int access = 0; access <= second % 2; access++)
            {
                Access(operation, first + access, second / 3 + access);
            }

            for (int exit = entered.Length - 1; exit >= 0; exit--)
            {
                Tracker.ExitExclusive(operation, entered[exit]);
                _oracle.ExitSynchronization(operation, entered[exit]);
            }
        }

        // The operation forks a child and enters a lock, and the child, running as the operation's flow, enters it too and
        // accesses the locations it guards before both exit, the operation first. The child acquires the releases by
        // other flows before the operation's hold; the operation records nothing between the fork and its exit, so
        // whether the child's exit replaces or joins its release changes no verdict. The oracle publishes every exit,
        // inner exits of a recursive hold included, which hides a race only from a concurrent holder of the flow; the
        // step makes no inner exit, and RaceDetectionTests covers that case directly.
        private void ConcurrentHolder(SimulationOperation operation, int first, int second)
        {
            object synchronization = _locks[first % _locks.Length];
            SimulationOperation child = Start(operation);
            Tracker.EnterExclusive(operation, synchronization);
            _oracle.EnterSynchronization(operation, synchronization);
            Tracker.EnterExclusive(child, synchronization);
            _oracle.EnterSynchronization(child, synchronization);
            for (int access = 0; access <= second % 2; access++)
            {
                Access(child, first + access, second / 3 + access);
            }

            Tracker.ExitExclusive(operation, synchronization);
            _oracle.ExitSynchronization(operation, synchronization);
            Tracker.ExitExclusive(child, synchronization);
            _oracle.ExitSynchronization(child, synchronization);
        }

        private void Signal(SimulationOperation operation, object signal)
        {
            Tracker.SignalSynchronization(operation, signal);
            _oracle.SignalSynchronization(operation, signal);
        }

        private void Wait(SimulationOperation operation, object signal)
        {
            Tracker.WaitSynchronization(operation, signal);
            _oracle.WaitSynchronization(operation, signal);
        }
    }
}
