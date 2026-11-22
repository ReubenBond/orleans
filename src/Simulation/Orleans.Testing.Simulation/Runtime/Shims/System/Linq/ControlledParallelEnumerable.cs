using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Linq;

/// <summary>
/// Rejection of the PLINQ entry points of <see cref="global::System.Linq.ParallelEnumerable"/>: every <c>AsParallel</c>
/// overload, and <c>Range</c>, <c>Repeat</c>, and <c>Empty</c>. PLINQ runs a query on worker tasks of
/// <see cref="global::System.Threading.Tasks.TaskScheduler.Default"/> and blocks the calling thread on framework waits
/// until they finish, outside the simulation's coordinator, so the rewriter injects this rejection before each of these
/// call sites. Every other <see cref="global::System.Linq.ParallelEnumerable"/> member operates on a
/// <see cref="global::System.Linq.ParallelQuery{TSource}"/>, which these members alone create.
/// </summary>
public static class ControlledParallelEnumerable
{
    /// <summary>Rejection injected before a PLINQ entry point.</summary>
    /// <param name="apiName">The rejected API, supplied by the rewriter.</param>
    public static void RejectUnsupported(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.Parallel,
            apiName,
            "PLINQ runs the query on worker tasks of TaskScheduler.Default and blocks the calling thread until they " +
            "finish, outside the coordinator; use LINQ to Objects, or Parallel.ForEach or Parallel.ForEachAsync, which " +
            "run on controlled workers.");
    }
}
