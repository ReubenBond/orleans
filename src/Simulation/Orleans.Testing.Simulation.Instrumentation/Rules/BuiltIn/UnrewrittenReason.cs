namespace Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

/// <summary>
/// Why a public or protected member of a type the built-in rules target stays a BCL call. Each value names one
/// behavior that the rendered rule inventory describes, and <see cref="UnrewrittenMembers"/> assigns one to every
/// such member that no rule rewrites.
/// </summary>
internal enum UnrewrittenReason
{
    /// <summary>Computes its result from the receiver and its arguments, or creates, configures, or releases the receiver.</summary>
    Value,

    /// <summary>Uses or sets the current culture, or converts local time with the host's time zone.</summary>
    CultureOrTimeZone,

    /// <summary>Reads or changes state of the host process or machine.</summary>
    HostState,

    /// <summary>Looks up, configures, reads, or releases an OS process or its handle.</summary>
    Process,

    /// <summary>Runs on the receiver, which the controlled factories supply.</summary>
    Dispatch,

    /// <summary>Reads the host's monotonic clock.</summary>
    RealClock,

    /// <summary>Returns an identifier drawn from a process-wide counter.</summary>
    ProcessWideId,

    /// <summary>Reads or changes the calling thread's state.</summary>
    CurrentThread,

    /// <summary>Reads the BCL object a controlled thread keeps as its identity.</summary>
    IdentityHandle,

    /// <summary>
    /// A constructor that a rule rewrites where <c>newobj</c> calls it, but not where a <c>call</c> does: a derived type's
    /// constructor chaining to it, or a value type constructed in place.
    /// </summary>
    ConstructorCall,

    /// <summary>Performs one memory access or fence.</summary>
    Fence,

    /// <summary>Blocks the calling physical thread.</summary>
    Blocks,

    /// <summary>Runs its work through a BCL scheduler, the thread pool, or a new thread rather than the coordinator.</summary>
    Schedules,

    /// <summary>A runtime-async intrinsic.</summary>
    RuntimeAsync,

    /// <summary>Throws on the supported runtime.</summary>
    AlwaysThrows,

    /// <summary>A protected member of a substituted type, which only a derived type can call.</summary>
    SubstitutedTypeProtected,

    /// <summary>Reads or changes a channel's state on the calling thread, or returns a waiter which that state completes.</summary>
    Channel,

    /// <summary>Operates on a PLINQ query, which rewritten code can create only through the rejected PLINQ entry points.</summary>
    ParallelQuery,

    /// <summary>Reads, writes, flushes, or disposes on the calling thread.</summary>
    SynchronousIO,

    /// <summary>Returns a framework wrapper whose calls to the wrapped instances are framework calls.</summary>
    FrameworkWrapper,

    /// <summary>Operates on a TPL Dataflow block, which rewritten code can create only through the rejected constructors.</summary>
    DataflowBlock,

    /// <summary>Configures a background worker or service, or observes work which only its rejected start member begins.</summary>
    BackgroundWork,

    /// <summary>Runs on a WebSocket, which rewritten code cannot create over its own stream.</summary>
    WebSocket,
}
