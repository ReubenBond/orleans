using System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Runtime.Racing;

/// <summary>Weakly associates task proxies with the antecedents whose completion they represent.</summary>
internal static class RaceTaskDependencies
{
    private sealed class Dependencies
    {
        public required Func<IReadOnlyList<Task>> Resolve { get; init; }
    }

    private static readonly ConditionalWeakTable<Task, Dependencies> Values = new();

    public static TTask Register<TTask>(TTask task, Func<IReadOnlyList<Task>> resolve)
        where TTask : Task
    {
        Values.AddOrUpdate(task, new Dependencies { Resolve = resolve });
        return task;
    }

    public static IReadOnlyList<Task> Resolve(Task task) =>
        Values.TryGetValue(task, out Dependencies? dependencies)
            ? dependencies.Resolve()
            : [];
}
