namespace Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

/// <summary>
/// The API family a built-in <see cref="RewriteRule"/> belongs to. Families are the unit of granular
/// include/exclude selection for the built-in deterministic BCL rule set: a caller can opt a whole
/// family in or out, but never edit individual signatures, so the shipped inventory stays coherent.
/// Instrumented closures are simulation/test artifacts whose Controlled entry points require an active
/// Clockwork simulation; uninstrumented production binaries retain ordinary BCL behavior.
/// </summary>
public enum BuiltInRuleFamily
{
    /// <summary>
    /// Wall-clock and monotonic time: <see cref="System.DateTime"/>, <see cref="System.DateTimeOffset"/>,
    /// <see cref="System.Diagnostics.Stopwatch"/> static timestamp APIs, and
    /// <see cref="System.Environment"/> tick counters.
    /// </summary>
    Clock,

    /// <summary>Identity: <see cref="System.Guid"/> factory methods (<c>NewGuid</c>, <c>CreateVersion7</c>).</summary>
    Identity,

    /// <summary>
    /// General-purpose pseudo-randomness: the <see cref="System.Random"/> shared instance and constructors, whose
    /// deterministic instances every instance member draws from.
    /// </summary>
    Random,

    /// <summary>
    /// Random-number generation: the static <see cref="System.Security.Cryptography.RandomNumberGenerator"/>
    /// APIs and factories, redirected to deterministic non-cryptographic simulation streams.
    /// </summary>
    Crypto,

    /// <summary>
    /// Task combinators: the static <see cref="System.Threading.Tasks.Task.WhenAll(System.Threading.Tasks.Task[])"/>,
    /// <c>WhenAny</c>, and <c>WhenEach</c> families and <c>TaskExtensions.Unwrap</c>, redirected to controlled
    /// equivalents whose completion order is a deterministic function of when the antecedents complete on the logical
    /// thread.
    /// </summary>
    TaskCombinators,

    /// <summary>
    /// Task synchronous waits: every <c>Task.Wait</c>, <c>Task.WaitAll</c>, and <c>Task.WaitAny</c> overload (timeouts elapse in virtual time), and
    /// <c>TaskAsyncEnumerableExtensions.ToBlockingEnumerable</c>, redirected to controlled waits that pump the
    /// deterministic loop instead of blocking a physical thread (a never-satisfiable wait surfaces as a precise
    /// deadlock diagnostic).
    /// </summary>
    TaskSynchronization,

    /// <summary>
    /// Task continuations: every .NET 11 <c>Task.ContinueWith</c> and <c>Task&lt;TResult&gt;.ContinueWith</c>
    /// overload, redirected so the BCL registers nothing on the antecedent: the coordinator observes the antecedent
    /// complete and runs the genuine BCL continuation task on the logical thread.
    /// </summary>
    TaskContinuations,

    /// <summary>
    /// Task completion sources: calls to the completion members of the real
    /// <see cref="System.Threading.Tasks.TaskCompletionSource"/> and
    /// <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/> (<c>SetResult</c>, <c>SetException</c>,
    /// <c>SetCanceled</c>, <c>SetFromTask</c>, and their <c>TrySet*</c> forms), redirected to shims which complete the
    /// source through the same member and publish the race-detection release edge an await acquires, and the
    /// constructors which take creation options, redirected to factories which reject <c>AttachedToParent</c> inside a
    /// simulation. The completion source type, its options, and its task are unchanged.
    /// </summary>
    TaskCompletionSource,

    /// <summary>
    /// Virtual-time task APIs: every .NET 11 <c>Task.Delay</c> and <c>Task.WaitAsync</c> overload,
    /// including cancellation and <see cref="System.TimeProvider"/> forms.
    /// </summary>
    TaskTime,

    /// <summary>
    /// Task thread-pool scheduling: the <c>Task.Run</c> family (every <c>Action</c>/<c>Func&lt;TResult&gt;</c>/
    /// <c>Func&lt;Task&gt;</c>/<c>Func&lt;Task&lt;TResult&gt;&gt;</c> overload, with and without a
    /// <see cref="System.Threading.CancellationToken"/>). Redirected to controlled equivalents
    /// that queue the body as a fresh controlled operation on the simulation coordinator - it runs on the
    /// single logical thread interleaved with all other controlled work instead of on an uncontrolled
    /// physical thread-pool thread - preserving unwrap, cancellation, and fault semantics; and <c>Task.Start</c> and
    /// <c>Task.RunSynchronously</c>, which run a task constructed with <c>new Task(...)</c> on the coordinator's scheduler.
    /// </summary>
    TaskScheduling,

    /// <summary>
    /// Compiler-generated async machinery: the <c>SubstituteType</c> rules that retarget an
    /// <c>async</c> state machine's builder and awaiter types
    /// (<see cref="System.Runtime.CompilerServices.AsyncTaskMethodBuilder"/>, <c>TaskAwaiter</c>,
    /// <c>ConfiguredTaskAwaitable</c>/<c>YieldAwaitable</c> and their awaiters, generic and non-generic)
    /// onto Clockwork's controlled equivalents, plus the <c>Task.Yield()</c> redirect. Applied by the
    /// member-aware substitution pass so every awaited continuation is scheduled through the simulation
    /// coordinator instead of the thread pool, while <c>ConfigureAwait(false)</c> stays controlled.
    /// <c>AsyncHelpers.HandleAsyncEntryPoint</c>, through which an async <c>Main</c> returns, waits through the
    /// controlled awaiter. A method compiled with runtime async has no state machine, so rewriting one fails with
    /// <c>CWR0013</c>; the runtime-async <c>AsyncHelpers.Await</c> intrinsics taking a configured task awaitable are
    /// rejected.
    /// </summary>
    AsyncMachinery,

    /// <summary>
    /// Compiler-generated <see cref="System.Threading.Tasks.ValueTask"/> machinery: the
    /// <c>SubstituteType</c> rules that retarget an <c>async ValueTask</c>/<c>async ValueTask&lt;T&gt;</c>
    /// state machine's builder and awaiter types
    /// (<see cref="System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder"/>, <c>ValueTaskAwaiter</c>,
    /// <c>ConfiguredValueTaskAwaitable</c> and their awaiters, generic and non-generic) onto Clockwork's
    /// controlled equivalents, so awaiting a <see cref="System.Threading.Tasks.ValueTask"/> is scheduled
    /// through the coordinator and <c>ConfigureAwait(false)</c> stays controlled. The configured async disposable
    /// and enumerable that <c>await using</c> and <c>await foreach</c> consume, and the
    /// <c>TaskAsyncEnumerableExtensions</c> methods that produce them, are controlled with them. The runtime-async
    /// <c>AsyncHelpers.Await</c> intrinsics taking a configured value-task awaitable are rejected.
    /// </summary>
    ValueTaskMachinery,

    /// <summary>
    /// <see cref="System.Threading.Tasks.ValueTask"/> conversions: <c>AsTask</c> and <c>Preserve</c> on
    /// <see cref="System.Threading.Tasks.ValueTask"/> and <see cref="System.Threading.Tasks.ValueTask{TResult}"/>,
    /// redirected so a value task backed by a pending <see cref="System.Threading.Tasks.Sources.IValueTaskSource"/> is
    /// polled through the coordinator instead of registering a completion callback the source may run on the thread
    /// pool.
    /// </summary>
    ValueTaskConversions,

    /// <summary>
    /// <see cref="System.Threading.Tasks.TaskFactory"/> / <see cref="System.Threading.Tasks.TaskFactory{TResult}"/>
    /// scheduling: <c>StartNew</c> offloads work onto a task scheduler (the thread pool by default).
    /// Classified <c>Controlled</c> - the shim queues the delegate body as a fresh controlled
    /// operation on the coordinator (honouring state, cancellation, and results) instead of escaping onto
    /// a physical thread. The complete .NET 11 overload set is classified; custom schedulers and every
    /// non-<c>None</c> option are rejected.
    /// </summary>
    TaskFactory,

    /// <summary>
    /// <see cref="System.Threading.Tasks.TaskFactory"/> / <see cref="System.Threading.Tasks.TaskFactory{TResult}"/>
    /// multi-task continuations: every .NET 11 <c>ContinueWhenAll</c> and <c>ContinueWhenAny</c> overload, redirected
    /// so the BCL registers nothing on the antecedents: the coordinator observes them complete and runs the genuine BCL
    /// continuation task on the logical thread.
    /// </summary>
    TaskFactoryContinuations,

    /// <summary>
    /// The asynchronous programming model bridge: every <c>TaskFactory.FromAsync</c> and
    /// <c>TaskFactory&lt;TResult&gt;.FromAsync</c> overload, rejected at the call site, since the returned task completes
    /// when an <see cref="System.IAsyncResult"/> does, outside Clockwork's control.
    /// </summary>
    TaskFactoryFromAsync,

    /// <summary>
    /// <see cref="System.Threading.Thread"/> surface: construction and the <c>Start</c>/<c>Join</c>
    /// instance members are <c>Controlled</c> - a controlled thread is a real thread object
    /// whose body is scheduled as a fresh controlled operation on the coordinator instead of running on a
    /// physical OS thread, and <c>Join</c> pumps the deterministic loop; the static <c>Sleep</c>,
    /// <c>Yield</c>, and <c>SpinWait</c> hints yield cooperatively without blocking or using real time.
    /// The OS-specific surface (<c>Priority</c>, apartment state including the obsolete <c>ApartmentState</c> setter, and
    /// <c>Interrupt</c>) is classified
    /// <c>Rejected</c>: it cannot be modelled faithfully by the cooperative scheduler, so the rewritten
    /// call site fails precisely under simulation.
    /// </summary>
    Thread,

    /// <summary>
    /// <see cref="System.Threading.ThreadPool"/> queueing surface. The <c>QueueUserWorkItem</c> /
    /// <c>UnsafeQueueUserWorkItem</c> family is classified <c>Controlled</c> - the shim queues
    /// the callback as a fresh controlled operation on the coordinator instead of dispatching it to a
    /// physical thread-pool thread, preserving the safe-vs-unsafe <see cref="System.Threading.ExecutionContext"/>
    /// flow distinction (safe variants capture and flow the caller's context; unsafe variants do not).
    /// The registered-wait surface is controlled through modelled wait handles. The native-I/O surface
    /// (<c>UnsafeQueueNativeOverlapped</c>) is <c>Rejected</c> because it cannot be modelled by the
    /// deterministic scheduler. This goes beyond Coyote, which routes thread-pool work through its
    /// controlled task types.
    /// </summary>
    ThreadPool,

    /// <summary>
    /// Virtual timer types and provider bridges: <see cref="System.Threading.Timer"/>,
    /// <see cref="System.Timers.Timer"/>, <see cref="System.Threading.PeriodicTimer"/>, and
    /// <see cref="System.TimeProvider"/> timer creation.
    /// </summary>
    Timers,

    /// <summary>
    /// Timer-driven <see cref="System.Threading.CancellationTokenSource"/> construction,
    /// <c>CancelAfter</c>, reset, cancellation, and disposal.
    /// </summary>
    CancellationTimers,

    /// <summary>
    /// <see cref="System.Threading.Tasks.Parallel"/> surface: the loops whose body takes only the element are
    /// <c>Controlled</c>, running on a deterministic pool of controlled workers on the coordinator, and the overloads whose
    /// body receives a <see cref="System.Threading.Tasks.ParallelLoopState"/> are <c>Rejected</c>.
    /// <see cref="System.Environment.ProcessorCount"/> and the range factories <c>Partitioner.Create(int, int)</c> and
    /// <c>Partitioner.Create(long, long)</c>, which the BCL sizes by it, are <c>Controlled</c>: they use the simulation's
    /// processor count, which also sizes the loops without a limit, so one family decides both the loops and the count they
    /// are sized by. The normative description is <c>docs/compatibility.md#parallel-loops</c>.
    /// </summary>
    Parallel,

    /// <summary>
    /// <see cref="System.Threading.Channels.ChannelReader{T}"/> <c>ReadAllAsync</c> and <c>ReadAsync</c> and
    /// <see cref="System.Threading.Channels.ChannelWriter{T}"/> <c>WriteAsync</c>, whose BCL implementations are async
    /// methods compiled into the channels assembly, are classified <c>Controlled</c>: the shim calls a reader's or writer's
    /// override as written, and otherwise runs the BCL's algorithm with controlled awaits on the coordinator.
    /// </summary>
    Channels,

    /// <summary>
    /// The PLINQ entry points of <see cref="System.Linq.ParallelEnumerable"/>: every <c>AsParallel</c> overload, and
    /// <c>Range</c>, <c>Repeat</c>, and <c>Empty</c>, which are the only public ways to create a
    /// <see cref="System.Linq.ParallelQuery{TSource}"/>. Classified <c>Rejected</c>: PLINQ runs a query on worker tasks
    /// of <see cref="System.Threading.Tasks.TaskScheduler.Default"/> and blocks the calling thread on framework waits
    /// until they finish, so the rewritten call site throws a precise diagnostic. The operators over a parallel query stay
    /// BCL calls, since rewritten code has no parallel query to apply them to.
    /// </summary>
    Plinq,

    /// <summary>
    /// The <see cref="System.Collections.Concurrent.BlockingCollection{T}"/> members which add, take, or complete adding,
    /// including the static <c>AddToAny</c>/<c>TakeFromAny</c> families and <c>GetConsumingEnumerable</c>. Classified
    /// <c>Controlled</c>: the BCL blocks a taker on an empty collection, and an adder on a full bounded one, on framework
    /// semaphores and wait handles. Each shim makes the BCL's own zero-timeout attempt on the real collection and, when
    /// that would have waited, parks the caller on the coordinator until the collection can satisfy it, adding is
    /// completed, the token is cancelled, or the timeout elapses in virtual time.
    /// </summary>
    BlockingCollection,

    /// <summary>
    /// The asynchronous members of <see cref="System.IO.Stream"/>, <see cref="System.IO.TextReader"/>, and
    /// <see cref="System.IO.TextWriter"/> whose base implementations, which a type that does not override them inherits,
    /// run the synchronous member on the thread pool, block the caller on a semaphore or a task (<c>BeginRead</c>,
    /// <c>EndRead</c>, and the write pair), or await the other members through BCL awaits. Classified <c>Controlled</c>:
    /// the shim calls an override as written and otherwise runs the BCL's base algorithm on the coordinator, each
    /// synchronous read, write, or flush as a fresh controlled operation; an override's base call runs the base
    /// algorithm. The non-virtual members which forward to them (<c>ReadExactlyAsync</c>, <c>ReadAtLeastAsync</c>, and
    /// the overloads without a token or buffer size) run the BCL's algorithm over the controlled virtual members.
    /// </summary>
    AsyncIO,

    /// <summary>
    /// <see cref="System.ComponentModel.BackgroundWorker"/>.<c>RunWorkerAsync</c> and
    /// <c>Microsoft.Extensions.Hosting.BackgroundService.StartAsync</c>, which start the application's <c>DoWork</c> handler
    /// or <c>ExecuteAsync</c> on the thread pool (<c>Task.Factory.StartNew</c> on <c>TaskScheduler.Default</c>, and the real
    /// <c>Task.Run</c>). Classified <c>Rejected</c>: each type raises or stores the work through private state, so a shim
    /// cannot run it on the coordinator with the BCL's semantics, and the rewritten call site throws a precise diagnostic.
    /// </summary>
    BackgroundWork,

    /// <summary>
    /// TPL Dataflow: the constructor of every block of <c>System.Threading.Tasks.Dataflow</c>, and the
    /// <c>DataflowBlock</c> helpers which schedule work or wait (<c>Choose</c>, <c>Receive</c>, <c>ReceiveAsync</c>,
    /// <c>ReceiveAllAsync</c>, <c>SendAsync</c>, <c>OutputAvailableAsync</c>). Classified <c>Rejected</c>: a block wires
    /// cancellation and completion propagation through continuations on <c>TaskScheduler.Default</c> whatever its options'
    /// scheduler, and the helpers time out on real timers, so no scheduler substitution would keep its work on the
    /// coordinator.
    /// </summary>
    Dataflow,

    /// <summary>
    /// The <see cref="System.Net.WebSockets.WebSocket"/> factories over a caller's stream (<c>CreateFromStream</c> and
    /// <c>CreateClientWebSocket</c>), rejected: the framework's protocol implementation sends keep-alive pings from a real
    /// timer on the thread pool and times out its closing handshake in real time. And the <c>WebSocketStream.Create</c>
    /// overloads which may make the stream own its socket, controlled: a stream which owns its socket bounds the close on
    /// disposal with a real timer, so they reject a finite, non-zero close timeout.
    /// </summary>
    WebSockets,

    /// <summary>
    /// The <see cref="System.Net.Http.HttpClient"/> members which send a request, <c>HttpMessageInvoker.Send</c> and
    /// <c>SendAsync</c> (which dispatch to the client's when the receiver is one), and the
    /// <c>System.Net.Http.Json.HttpClientJsonExtensions</c> members, which send through a client. The BCL bounds each
    /// request with <c>HttpClient.Timeout</c> on a real timer, so each shim rejects a client whose timeout is finite where
    /// the call runs <c>HttpClient</c>'s implementation, and otherwise calls the BCL member.
    /// </summary>
    HttpClient,

    /// <summary>
    /// The <c>Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions</c> and
    /// <c>HostingAbstractionsHostBuilderExtensions</c> members, which await the host's work in
    /// framework code, block the caller on it (<c>Start</c>, <c>Run</c>, <c>WaitForShutdown</c>), or bound a stop with a
    /// real timer (<c>StopAsync(IHost, TimeSpan)</c>). The shims await through the coordinator, wait with the controlled
    /// wait, and time out in virtual time.
    /// </summary>
    Hosting,

    /// <summary>
    /// Process control and abrupt-termination surfaces that cannot be modelled by the deterministic
    /// scheduler: the static <see cref="System.Diagnostics.Process"/> <c>Start</c>, <c>Run</c>, <c>RunAsync</c>,
    /// <c>RunAndCaptureText</c>, <c>RunAndCaptureTextAsync</c>, and <c>StartAndForget</c> families (spawning an
    /// uncontrolled OS process), the <see cref="System.Diagnostics.Process"/> instance <c>Kill</c> / <c>Signal</c>
    /// members and the <c>WaitForExit</c> / <c>WaitForExitAsync</c> / <c>WaitForExitStatus</c> /
    /// <c>TryWaitForExitStatus</c> / <c>WaitForExitStatusAsync</c> waits and <c>ReadAllBytes</c> / <c>ReadAllText</c> /
    /// <c>ReadAllLines</c> reads and their async forms (signalling or blocking on an uncontrolled process), the
    /// <c>WaitForInputIdle</c> waits, the <c>Responding</c> getter (which messages the process's main window and waits for
    /// a reply), <c>CloseMainWindow</c>, the <c>EnableRaisingEvents</c> setter and the
    /// <c>BeginOutputReadLine</c> / <c>BeginErrorReadLine</c> reads (which raise events on threads of the BCL's choosing as
    /// the process exits or writes), the priority, processor-affinity, and working-set setters of a
    /// <see cref="System.Diagnostics.Process"/> and the scheduling setters of a <see cref="System.Diagnostics.ProcessThread"/>,
    /// the <see cref="Microsoft.Win32.SafeHandles.SafeProcessHandle"/> <c>Start</c>, <c>Signal</c>, <c>Resume</c>,
    /// <c>Kill</c>, and wait members and the <c>WindowsProcessStartArguments</c> / <c>UnixProcessStartArguments</c>
    /// <c>Start</c> launchers, and <see cref="System.Environment"/> <c>Exit</c> / <c>FailFast</c> (tearing the host
    /// process down out from under the simulation). Classified <c>Rejected</c>: the rewritten call site throws a precise
    /// uncontrolled-invocation diagnostic that names the exact API and IL offset, so a rewritten assembly can never
    /// escape the deterministic model by launching, signalling, waiting on, rescheduling, or terminating a real process.
    /// This mirrors Coyote's uncontrolled-invocation pass, which rewrites such call sites to throw rather than run.
    /// </summary>
    UncontrolledInvocation,

    /// <summary>
    /// <see cref="System.Threading.Monitor"/> surface, and therefore every C# <c>lock (object)</c>
    /// statement (which the compiler lowers to <c>Monitor.Enter(obj, ref bool)</c> +
    /// <c>finally Monitor.Exit(obj)</c>). Classified <c>Controlled</c>: the shim models each
    /// monitored object's ownership, recursion count, and condition-variable wait set on the cooperative
    /// logical thread. <c>Enter</c>/<c>TryEnter</c> acquire (a contended acquire pumps the deterministic
    /// loop until the owner releases), <c>Exit</c> unwinds one recursion level, <c>Wait</c> atomically
    /// releases the full recursion count and re-acquires it after being pulsed, and <c>Pulse</c>/
    /// <c>PulseAll</c> move waiters to the ready set with replayable ordering. A never-satisfiable acquire
    /// or wait surfaces as the loop-model deadlock diagnostic. Zero timeouts are faithful non-blocking
    /// tries; finite positive timeouts use deterministic simulated deadlines. Ownership/argument/timeout
    /// errors throw exactly as the BCL under the active simulation.
    /// </summary>
    Monitor,

    /// <summary>
    /// <see cref="System.Threading.Lock"/> (the .NET 9+ dedicated lock type) and its C# <c>lock (Lock)</c>
    /// lowering (<c>Lock.Scope scope = obj.EnterScope(); try { ... } finally { scope.Dispose(); }</c>).
    /// Classified <c>Controlled</c> via <c>SubstituteType</c>: every reference to
    /// <see cref="System.Threading.Lock"/> and its nested <c>Scope</c> ref struct is retargeted onto the
    /// controlled equivalents, so <c>Enter</c>/<c>Exit</c>/<c>EnterScope</c>/<c>TryEnter</c>/
    /// <c>IsHeldByCurrentThread</c> and the scope's <c>Dispose</c> run on the controlled monitor kernel
    /// with the same reentrancy and mutual-exclusion semantics.
    /// </summary>
    Lock,

    /// <summary>
    /// <see cref="System.Threading.SemaphoreSlim"/> surface: construction, <c>CurrentCount</c>, the
    /// synchronous <c>Wait</c> overloads, the asynchronous <c>WaitAsync</c> overloads, <c>Release</c>, and
    /// <c>Dispose</c>. Classified <c>Controlled</c>: the permit count and waiter set are
    /// modelled on the cooperative logical thread. A synchronous <c>Wait</c> with no permit pumps the loop
    /// until a permit is released (a never-satisfiable wait surfaces as the loop-model deadlock diagnostic);
    /// <c>WaitAsync</c> returns a task completed when a permit is released; <c>Release</c> enforces the
    /// maximum count (<see cref="System.Threading.SemaphoreFullException"/>) and serves waiters in a
    /// deterministic, replayable order; cancellation is honoured on the logical thread. Zero timeouts are
    /// deterministic, replayable order; cancellation is honoured on the logical thread. Zero timeouts are
    /// faithful non-blocking tries; finite positive timeouts use deterministic simulated deadlines.
    /// <c>AvailableWaitHandle</c> is completed by the wait-handle and atomic control controlled wait-handle bridge: it returns a
    /// controlled <see cref="System.Threading.ManualResetEvent"/> whose signaled state tracks the permit
    /// count and disposal.
    /// </summary>
    Semaphore,

    /// <summary>
    /// <see cref="System.Threading.Interlocked"/> atomic surface: the full .NET 11 overload set of
    /// <c>Increment</c>/<c>Decrement</c>/<c>Add</c>/<c>And</c>/<c>Or</c>/<c>Exchange</c>/
    /// <c>CompareExchange</c> (every primitive, native-int, floating-point, reference, and generic
    /// overload), <c>Read</c>, and the memory barriers. Classified <c>Controlled</c>:
    /// each call site is redirected to a shim with the identical <c>ref</c>-first signature. Clockwork's
    /// cooperative single-logical-thread scheduler makes every interlocked read-modify-write an indivisible
    /// step - it is never split and never interleaved mid-operation - so the shim delegates to the real
    /// primitive, preserving exact atomic return, overflow, and reference-write semantics under the active
    /// simulation. The documented exploration policy adds no mid-operation scheduling point (the
    /// natural points remain the surrounding await/yield boundaries), and the single delegation site is
    /// where race-exploration access tracking attaches without ever splitting an atomic operation.
    /// </summary>
    Interlocked,

    /// <summary>
    /// <see cref="System.Threading.Volatile"/> acquire/release surface: the full .NET 11 overload set of
    /// <c>Read</c>/<c>Write</c> (every primitive, native-int, floating-point, and generic reference
    /// overload) plus <c>ReadBarrier</c>/<c>WriteBarrier</c>. Classified <c>Controlled</c>: each
    /// call site is redirected to a shim with the identical <c>ref</c>-first signature that delegates to the
    /// real primitive, preserving the exact value read/written and the acquire (read) / release (write)
    /// fence intent. The single delegation site is where race-exploration access tracking attaches.
    /// </summary>
    Volatile,

    /// <summary>
    /// <see cref="System.Threading.SpinWait"/> busy-spin surface, retargeted by <c>SubstituteType</c> onto
    /// the controlled equivalent (the struct mirrors <c>Count</c>, <c>NextSpinWillYield</c>, <c>Reset</c>,
    /// both <c>SpinOnce</c> overloads, and the three static <c>SpinUntil</c> overloads). Classified
    /// <c>Controlled</c>: a controlled spin yields cooperatively to the deterministic scheduler
    /// instead of burning CPU, <c>SpinUntil</c> pumps the loop until its predicate holds, and its finite
    /// overload uses a virtual-time deadline so a spin timeout consumes modelled - never real - time.
    /// </summary>
    SpinWait,

    /// <summary>
    /// The controlled event / wait-handle surface: <see cref="System.Threading.AutoResetEvent"/>,
    /// <see cref="System.Threading.ManualResetEvent"/>, <see cref="System.Threading.EventWaitHandle"/>, and
    /// the shared <see cref="System.Threading.WaitHandle"/> operations (<c>WaitOne</c>, <c>WaitAny</c>,
    /// <c>WaitAll</c>, <c>SignalAndWait</c>). Classified <c>Controlled</c>: each event's signaled
    /// state and deterministic FIFO waiter set are modelled on the cooperative logical thread. A
    /// <c>WaitOne</c> with no signal pumps the loop until <c>Set</c> (a never-satisfiable wait surfaces as
    /// the loop-model deadlock diagnostic); an auto-reset event wakes and consumes exactly one eligible
    /// waiter while a manual-reset event releases all and stays signaled; finite timeouts use virtual time;
    /// <c>WaitAny</c>/<c>WaitAll</c> register across multiple handles with no lost signals. Named/cross-process
    /// event APIs cannot be modelled in a single simulated process and are rejected precisely.
    /// </summary>
    WaitHandle,

    /// <summary>
    /// <see cref="System.Threading.ReaderWriterLockSlim"/> surface: construction, all state and
    /// recursion properties, every read/upgradeable-read/write enter/try-enter/exit member, and disposal.
    /// Classified <c>Controlled</c>: real BCL instances remain identity objects while a
    /// receiver-first shim models logical-strand ownership, recursion, queued waiters, and virtual-time
    /// timeouts in side state.
    /// </summary>
    ReaderWriterLockSlim,

    /// <summary>
    /// <see cref="System.Threading.ManualResetEventSlim"/> surface: all constructors, properties,
    /// signal/reset/wait overloads, bridge handle access, and disposal. Classified <c>Controlled</c>
    ///: the BCL object is an identity object and the receiver-first shim models signal state,
    /// waiters, virtual-time deadlines, cancellation, and its controlled wait-handle bridge.
    /// </summary>
    ManualResetEventSlim,

    /// <summary>
    /// Kernel <see cref="System.Threading.Mutex"/> construction, release, and named-object APIs.
    /// Unnamed construction and <c>ReleaseMutex</c> are <c>Controlled</c>, using a real BCL
    /// object only as an identity handle whose ownership is modelled by the controlled wait-handle kernel.
    /// Named constructors (including null-name forms, which the shim permits as the unnamed case) and
    /// open APIs are classified <c>Rejected</c> because a non-null name is cross-process state which a
    /// single-process simulation cannot faithfully model.
    /// </summary>
    Mutex,

    /// <summary>
    /// Kernel <see cref="System.Threading.Semaphore"/> construction, release, and named-object APIs.
    /// The unnamed constructor and both release overloads are <c>Controlled</c>, retaining a
    /// BCL instance only for identity while the controlled wait-handle kernel owns the permit state.
    /// Named constructors (including conditionally-supported null-name forms) and open APIs are
    /// <c>Rejected</c> because they address cross-process kernel state.
    /// </summary>
    KernelSemaphore,

    /// <summary>
    /// <see cref="System.Threading.SpinLock"/> is wholly substituted with
    /// <c>ControlledSpinLock</c>. Its value-type state, constructors, properties, and all
    /// enter/try-enter/exit members therefore run on controlled logical strands instead of CPU spinning.
    /// </summary>
    SpinLock,

    /// <summary>
    /// <see cref="System.Threading.ExecutionContext"/> capture/run/flow-control and receiver members.
    /// The supported logical-context members are <c>Controlled</c>; the legacy serialization member is
    /// <c>Rejected</c> so a rewritten simulation cannot invoke legacy BCL serialization behavior.
    /// </summary>
    ExecutionContext,

    /// <summary>
    /// <see cref="System.Threading.SynchronizationContext"/> ambient-context and callback-dispatch
    /// members. Ambient state and callback methods are <c>Controlled</c>; the raw OS handle
    /// <c>Wait</c> member is <c>Rejected</c> before it can block a physical thread.
    /// </summary>
    SynchronizationContext,

    /// <summary>
    /// <see cref="System.Threading.Barrier"/> is wholly substituted with
    /// <c>ControlledBarrier</c>, including nested generic references such as
    /// <c>Action&lt;Barrier&gt;</c>, so phase participation and callbacks execute in the simulation.
    /// </summary>
    Barrier,

    /// <summary>
    /// <see cref="System.Threading.CountdownEvent"/> is wholly substituted with
    /// <c>ControlledCountdownEvent</c>, so count updates, waits, bridge handles, and disposal
    /// all remain on the controlled scheduler.
    /// </summary>
    CountdownEvent,
}
