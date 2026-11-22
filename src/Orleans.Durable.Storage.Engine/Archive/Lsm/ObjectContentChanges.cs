using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// Stops the process when an object write's content changed after its writer verified it (archive §9): the archive
/// writers' report of <see cref="ObjectContentChangedException"/>, which counts it and stops through
/// <see cref="ObjectContentChangedDefect"/>.
/// </summary>
/// <remarks>
/// The writer's buffer changed after verification, through a use-after-free or a memory fault, which may have damaged
/// other in-flight state, write-ahead log pages among it, so a retry would only hide the defect (conventions.md §14).
/// </remarks>
internal static class ObjectContentChanges
{
    /// <summary>
    /// Counts the refused write and reports <paramref name="exception"/> as a defect, which stops the process; returns only
    /// when a test intercepts the report.
    /// </summary>
    /// <param name="exception">The store's refusal.</param>
    public static void Stop(ObjectContentChangedException exception)
    {
        ArchiveMetrics.RecordChangedContent();
        ObjectContentChangedDefect.Stop(exception);
    }

    /// <summary>
    /// Makes an object write, and stops the process if the store refused its content as changed after verification,
    /// whether the store refused it at the call or once the write ran.
    /// </summary>
    /// <typeparam name="T">The write's outcome.</typeparam>
    /// <param name="write">Starts the create or replace.</param>
    /// <returns>The write's outcome.</returns>
    /// <exception cref="ObjectContentChangedException">The store refused the content, and a test intercepted the report.</exception>
    public static async ValueTask<T> StopOnChangeAsync<T>(Func<ValueTask<T>> write)
    {
        try
        {
            return await write().ConfigureAwait(false);
        }
        catch (ObjectContentChangedException exception)
        {
            Stop(exception);
            throw;
        }
    }
}
