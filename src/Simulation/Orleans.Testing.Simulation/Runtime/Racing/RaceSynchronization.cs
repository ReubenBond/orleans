using System.ComponentModel;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>
/// Bridges controlled synchronization transitions into the race detector's happens-before and
/// lockset model.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RaceSynchronization
{
    /// <summary>
    /// Records acquisition of an exclusive controlled lock: one flow holds it at a time, and only the holding flow exits
    /// it. Each acquisition consumes the release edge of the exit before it.
    /// </summary>
    public static void EnterExclusive(object synchronization)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        if (SimulationScheduler.TryGetExecutingOperation(out SimulationScheduler? scheduler, out _))
        {
            scheduler.EnterExclusiveRaceSynchronization(synchronization);
        }
    }

    /// <summary>Records release of an exclusive controlled lock by the flow holding it, publishing a release edge.</summary>
    public static void ExitExclusive(object synchronization)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        if (SimulationScheduler.TryGetExecutingOperation(out SimulationScheduler? scheduler, out _))
        {
            scheduler.ExitExclusiveRaceSynchronization(synchronization);
        }
    }

    /// <summary>Publishes a release/signal happens-before edge without holding a lockset entry.</summary>
    public static void Signal(object synchronization)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        if (SimulationScheduler.TryGetExecutingOperation(out SimulationScheduler? scheduler, out _))
        {
            scheduler.SignalRaceSynchronization(synchronization);
        }
    }

    /// <summary>Consumes an acquire/wait happens-before edge without holding a lockset entry.</summary>
    public static void Wait(object synchronization)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        if (SimulationScheduler.TryGetExecutingOperation(out SimulationScheduler? scheduler, out _))
        {
            if (synchronization is Task task)
            {
                ConsumeTask(scheduler, task);
            }
            else
            {
                scheduler.WaitRaceSynchronization(synchronization);
            }
        }
    }

    private static void ConsumeTask(SimulationScheduler scheduler, Task task)
    {
        IReadOnlyList<Task> dependencies = RaceTaskDependencies.Resolve(task);
        if (dependencies.Count == 0)
        {
            // A task without dependencies is a leaf of the traversal, so it needs no visited set.
            scheduler.WaitRaceSynchronization(task);
            return;
        }

        HashSet<Task> visited = new(ReferenceEqualityComparer.Instance) { task };
        foreach (Task dependency in dependencies)
        {
            ConsumeTask(scheduler, dependency, visited);
        }

        scheduler.WaitRaceSynchronization(task);
    }

    private static void ConsumeTask(
        SimulationScheduler scheduler,
        Task task,
        HashSet<Task> visited)
    {
        if (!visited.Add(task))
        {
            return;
        }

        foreach (Task dependency in RaceTaskDependencies.Resolve(task))
        {
            ConsumeTask(scheduler, dependency, visited);
        }

        scheduler.WaitRaceSynchronization(task);
    }
}
