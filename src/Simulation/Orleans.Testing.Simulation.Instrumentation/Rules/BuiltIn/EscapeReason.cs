namespace Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

/// <summary>
/// Why a framework member which reaches a concurrency escape needs no rule. Each value names one behavior that the
/// rendered rule inventory describes, and <see cref="ConcurrencyEscapeAudit"/> assigns one to every such member that no
/// rule controls or rejects and <see cref="UnrewrittenMembers"/> does not list.
/// </summary>
internal enum EscapeReason
{
    /// <summary>A framework async method or adapter, whose awaits register BCL continuations.</summary>
    FrameworkAwait,

    /// <summary>Performs I/O on an OS file, socket, or connection.</summary>
    OsResource,

    /// <summary>Reads or stores a task scheduler without queueing work to it.</summary>
    SchedulerValue,

    /// <summary>Belongs to a task scheduler other than the coordinator's, which every controlled API rejects.</summary>
    CustomScheduler,

    /// <summary>Completes, or registers a continuation on, an <c>IValueTaskSource</c>.</summary>
    ValueTaskSource,

    /// <summary>Rethrows an unhandled exception on the thread pool.</summary>
    UnhandledException,

    /// <summary>Runs a callback of the caller's on a thread of the BCL's choosing.</summary>
    AffinityBackstop,

    /// <summary>Completes synchronously on the caller.</summary>
    CompletesOnCaller,

    /// <summary>Runs framework code on another thread while the caller waits for it.</summary>
    WaitsForFrameworkWork,
}
