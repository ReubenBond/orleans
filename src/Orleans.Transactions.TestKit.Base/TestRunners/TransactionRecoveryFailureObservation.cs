using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit;

internal static class TransactionRecoveryFailureObservation
{
    public static async Task ObserveAsync(Task task, Action<Exception, long> onFailure)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            onFailure(exception, Stopwatch.GetTimestamp());
        }
    }

    public static bool IsPremature(long observedAt, long shutdownRequestedAt) => observedAt < shutdownRequestedAt;
}
