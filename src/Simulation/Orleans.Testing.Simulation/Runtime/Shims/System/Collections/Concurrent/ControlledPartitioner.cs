using System.Collections.Concurrent;
using System.ComponentModel;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Collections.Concurrent;

/// <summary>
/// <para>
/// Controlled rewrite targets for the <see cref="Partitioner"/> range factories which size their ranges by the
/// processor count: <see cref="Partitioner.Create(int, int)"/> and <see cref="Partitioner.Create(long, long)"/>. The BCL
/// divides the range into three times <see cref="Environment.ProcessorCount"/> ranges, reading the host's count inside the
/// factory, where no rewrite reaches it. The shims compute the BCL's range size from the simulation's processor count
/// (see <see cref="ISimulationRuntimeEnvironment.GetProcessorCount"/>) and pass it to the BCL's
/// <c>Create(fromInclusive, toExclusive, rangeSize)</c>, so the ranges, and the iterations each worker of a
/// <see cref="global::System.Threading.Tasks.Parallel"/> loop takes, are the same on every host.
/// </para>
/// <para>
/// The other <see cref="Partitioner"/> factories stay BCL calls. <c>Create(IEnumerable&lt;TSource&gt;)</c> also reads
/// the processor count, to size a fill buffer, but the BCL never fills that buffer: a partition reads the source only
/// for the chunk it takes, so the partitioner reads the same elements at the same points on every host.
/// </para>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ControlledPartitioner
{
    private const string CreateApi = "System.Collections.Concurrent.Partitioner.Create";

    // The BCL's ranges per processor (PartitionerStatic.CoreOversubscriptionRate).
    private const int CoreOversubscriptionRate = 3;

    /// <summary>Controlled <see cref="Partitioner.Create(int, int)"/>.</summary>
    /// <param name="fromInclusive">The lower, inclusive bound of the range.</param>
    /// <param name="toExclusive">The upper, exclusive bound of the range.</param>
    /// <returns>A partitioner over ranges of the BCL's size for the simulated processor count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="toExclusive"/> is less than or equal to <paramref name="fromInclusive"/>.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OrderablePartitioner<Tuple<int, int>> Create(int fromInclusive, int toExclusive)
    {
        long rangeCount = RangeCount();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(toExclusive, fromInclusive);
        long range = (long)toExclusive - fromInclusive;
        int rangeSize = (int)(range / rangeCount);
        if (rangeSize == 0)
        {
            rangeSize = 1;
        }

        return Partitioner.Create(fromInclusive, toExclusive, rangeSize);
    }

    /// <summary>Controlled <see cref="Partitioner.Create(long, long)"/>.</summary>
    /// <param name="fromInclusive">The lower, inclusive bound of the range.</param>
    /// <param name="toExclusive">The upper, exclusive bound of the range.</param>
    /// <returns>A partitioner over ranges of the BCL's size for the simulated processor count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="toExclusive"/> is less than or equal to <paramref name="fromInclusive"/>.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static OrderablePartitioner<Tuple<long, long>> Create(long fromInclusive, long toExclusive)
    {
        long rangeCount = RangeCount();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(toExclusive, fromInclusive);

        // The BCL divides in decimal, because the width of a long range can exceed long.MaxValue.
        decimal range = (decimal)toExclusive - fromInclusive;
        long rangeSize = (long)(range / rangeCount);
        if (rangeSize == 0)
        {
            rangeSize = 1;
        }

        return Partitioner.Create(fromInclusive, toExclusive, rangeSize);
    }

    // The number of ranges the BCL divides a range into: the processor count, oversubscribed. It is a long so that no
    // configurable processor count overflows it.
    private static long RangeCount()
    {
        var (_, environment, node) = SimulationRuntimeDispatch.RequireEnvironment(CreateApi);
        return (long)environment.GetProcessorCount(node) * CoreOversubscriptionRate;
    }
}
