using Orleans.Durable.Storage.Diagnostics;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// Stops the process when an object write's content changed after its writer verified it (object-store.md §2.1): every
/// writer's one report of <see cref="ObjectContentChangedException"/>.
/// </summary>
/// <remarks>
/// The writer's buffer changed after verification, through a use-after-free or a memory fault, which may have damaged
/// other in-flight state, so a retry would only hide the defect, or write the damaged bytes under a checksum computed
/// afresh (conventions.md §14).
/// </remarks>
public static class ObjectContentChangedDefect
{
    /// <summary>The defect's name in the fatal-error report.</summary>
    public const string Defect = "ObjectContentChanged";

    /// <summary>
    /// Reports <paramref name="exception"/> as a defect, which stops the process; returns only when a test intercepts the
    /// report, after which <see cref="FatalErrorHandler.IsReported"/> marks the exception, so no caller repeats the write.
    /// </summary>
    /// <param name="exception">The store's refusal.</param>
    public static void Stop(ObjectContentChangedException exception) => FatalErrorHandler.StopOnDefect(Defect, exception);
}
