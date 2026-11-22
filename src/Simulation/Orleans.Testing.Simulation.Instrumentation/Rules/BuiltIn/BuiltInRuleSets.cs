using System.Collections.Immutable;
using Orleans.Testing.Simulation.Runtime.Policy;

namespace Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

/// <summary>
/// The catalogue of Clockwork's built-in, versioned rewrite rule sets. These ship with the
/// instrumentation package so MSBuild and CLI users can turn on deterministic BCL behaviour without
/// hand-authoring JSON signatures. The only rule set today is
/// <see cref="DeterministicBclId"/> - the built-in deterministic BCL shim set - which
/// redirects the direct static time / identity / random surface to the runtime shims in the
/// <c>Clockwork</c> assembly (under the <c>Orleans.Testing.Simulation.Shims</c> namespace hierarchy).
/// </summary>
/// <remarks>
/// <para>
/// Rules are grouped into <see cref="BuiltInRuleFamily"/> values so a caller can include or exclude a
/// whole family (the granular, safe unit) while the exact per-signature mapping stays fixed and
/// coherent. The rule set's <em>content</em> changes when the selected families change, so its
/// <see cref="RewriteRuleSet.ComputeSignature"/> - and therefore the incremental build key - reflects
/// the selection even though the id and version are stable.
/// </para>
/// <para>
/// The clock, identity, random, and crypto families are <see cref="SimulationApiPolicy.Controlled"/>
/// redirections. Controlled rewrite targets require an active simulation.
/// </para>
/// </remarks>
public static class BuiltInRuleSets
{
    /// <summary>The stable id of the first production deterministic BCL rule set.</summary>
    public const string DeterministicBclId = "clockwork.bcl.deterministic";

    /// <summary>The version of the deterministic BCL rule set.</summary>
    public const string DeterministicBclVersion = "3.0.0";

    /// <summary>The stable id of the controlled task / async machinery rule set.</summary>
    public const string ControlledTasksId = "clockwork.tasks.controlled";

    /// <summary>The version of the controlled task rule set.</summary>
    public const string ControlledTasksVersion = "3.0.0";

    /// <summary>The simple name of the assembly declaring every built-in shim.</summary>
    public const string ShimAssemblyName = "Orleans.Testing.Simulation";

    private const string DateTimeShim = "Orleans.Testing.Simulation.Shims.System.ControlledDateTime";
    private const string DateTimeOffsetShim = "Orleans.Testing.Simulation.Shims.System.ControlledDateTimeOffset";
    private const string StopwatchShim = "Orleans.Testing.Simulation.Shims.System.Diagnostics.ControlledStopwatch";
    private const string EnvironmentShim = "Orleans.Testing.Simulation.Shims.System.ControlledEnvironment";
    private const string GuidShim = "Orleans.Testing.Simulation.Shims.System.ControlledGuid";
    private const string RandomShim = "Orleans.Testing.Simulation.Shims.System.ControlledRandom";
    private const string CryptoShim = "Orleans.Testing.Simulation.Shims.System.Security.Cryptography.ControlledRandomNumberGenerator";
    private const string TaskShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTask";
    private const string TaskFactoryShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskFactory";
    private const string ThreadShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread";
    private const string ThreadPoolShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledThreadPool";
    private const string ParallelShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledParallel";
    private const string PartitionerShim = "Orleans.Testing.Simulation.Shims.System.Collections.Concurrent.ControlledPartitioner";
    private const string MonitorShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledMonitor";
    private const string SemaphoreSlimShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledSemaphoreSlim";
    private const string InterlockedShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledInterlocked";
    private const string VolatileShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledVolatile";
    private const string ReaderWriterLockSlimShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledReaderWriterLockSlim";
    private const string ManualResetEventSlimShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledManualResetEventSlim";
    private const string MutexShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledMutex";
    private const string KernelSemaphoreShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledSemaphore";
    private const string ExecutionContextShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledExecutionContext";
    private const string SynchronizationContextShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledSynchronizationContext";
    private const string TimeProviderShim = "Orleans.Testing.Simulation.Shims.System.ControlledTimeProvider";
    private const string CancellationTokenSourceShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledCancellationTokenSource";

    // Cecil full names for the exact overload parameters (from the net11 reference assemblies).
    private const string Int32 = "System.Int32";
    private const string Int64 = "System.Int64";
    private const string String = "System.String";
    private const string Boolean = "System.Boolean";
    private const string DateTimeOffset = "System.DateTimeOffset";
    private const string SpanByte = "System.Span`1<System.Byte>";
    private const string SpanChar = "System.Span`1<System.Char>";
    private const string ReadOnlySpanChar = "System.ReadOnlySpan`1<System.Char>";
    private const string ReadOnlySpanGenericArg0 = "System.ReadOnlySpan`1<!!0>";
    private const string SpanGenericArg0 = "System.Span`1<!!0>";
    private const string ReadOnlySpanTDecl = "System.ReadOnlySpan`1<T>";
    private const string SpanTDecl = "System.Span`1<T>";

    // Cecil full names for the controlled-task overload parameters.
    private const string Task = "System.Threading.Tasks.Task";
    private const string TaskArray = "System.Threading.Tasks.Task[]";
    private const string IEnumerableTask = "System.Collections.Generic.IEnumerable`1<System.Threading.Tasks.Task>";
    private const string ReadOnlySpanTask = "System.ReadOnlySpan`1<System.Threading.Tasks.Task>";
    private const string ActionOfTask = "System.Action`1<System.Threading.Tasks.Task>";
    private const string Action = "System.Action";

    // Cecil full names for the generic Task<TResult> combinator overload parameters. At a call site the
    // C# compiler emits these as a GenericInstanceMethod whose element parameters reference the method
    // generic parameter, which Cecil renders positionally as `!!0` - so the *target* signatures use the
    // `!!0` form. The controlled shim is matched against its *definition*, where Cecil renders the same
    // generic parameter by name (`TResult`), so the *replacement* signatures use the `TResult` form.
    private const string TaskT = "System.Threading.Tasks.Task`1<!!0>";
    private const string TaskTArray = "System.Threading.Tasks.Task`1<!!0>[]";
    private const string IEnumerableTaskT = "System.Collections.Generic.IEnumerable`1<System.Threading.Tasks.Task`1<!!0>>";
    private const string ReadOnlySpanTaskT = "System.ReadOnlySpan`1<System.Threading.Tasks.Task`1<!!0>>";
    private const string TaskTDecl = "System.Threading.Tasks.Task`1<TResult>";
    private const string TaskTArrayDecl = "System.Threading.Tasks.Task`1<TResult>[]";
    private const string IEnumerableTaskTDecl = "System.Collections.Generic.IEnumerable`1<System.Threading.Tasks.Task`1<TResult>>";
    private const string ReadOnlySpanTaskTDecl = "System.ReadOnlySpan`1<System.Threading.Tasks.Task`1<TResult>>";

    // Cecil full names for the TaskExtensions.Unwrap(this Task<Task>) / Unwrap<TResult>(this Task<Task<TResult>>)
    // extension methods. Unwrap is a static extension whose receiver is the first parameter, so the shim is a
    // direct static-to-static redirect. The generic overload uses the `!!0` target / `TResult` replacement split.
    private const string TaskExtensionsType = "System.Threading.Tasks.TaskExtensions";
    private const string TaskOfTask = "System.Threading.Tasks.Task`1<System.Threading.Tasks.Task>";
    private const string TaskOfTaskT = "System.Threading.Tasks.Task`1<System.Threading.Tasks.Task`1<!!0>>";
    private const string TaskOfTaskTDecl = "System.Threading.Tasks.Task`1<System.Threading.Tasks.Task`1<TResult>>";

    // Cecil full names for the TaskFactory scheduling surface (rejected under simulation). Task.Factory
    // is the non-generic TaskFactory; its StartNew(Func<TResult>) is a generic method (`!!0` target,
    // `TResult` replacement), whereas TaskFactory`1's StartNew(Func<TResult>) is a non-generic method
    // over the type parameter (`!0` target).
    private const string TaskFactoryType = "System.Threading.Tasks.TaskFactory";
    private const string TaskFactoryTType = "System.Threading.Tasks.TaskFactory`1";
    private const string TaskFactoryTOfResultDecl = "System.Threading.Tasks.TaskFactory`1<TResult>";
    private const string TaskSchedulerType = "System.Threading.Tasks.TaskScheduler";
    private const string ActionOfObject = "System.Action`1<System.Object>";
    private const string FuncOfMethodResult = "System.Func`1<!!0>";
    private const string FuncOfTypeResult = "System.Func`1<!0>";
    private const string FuncOfResultDecl = "System.Func`1<TResult>";
    private const string FuncOfObjectAndMethodResult = "System.Func`2<System.Object,!!0>";
    private const string FuncOfObjectAndTypeResult = "System.Func`2<System.Object,!0>";
    private const string FuncOfObjectAndResultDecl = "System.Func`2<System.Object,TResult>";

    // Cecil full names for the Task.Run scheduling surface (controlled). The generic overloads are
    // GenericInstanceMethods at the call site (`!!0` target) resolved against their definitions (`TResult`
    // replacement); Func<Task>/Func<Task<TResult>> carry the unwrap overloads.
    private const string CancellationToken = "System.Threading.CancellationToken";
    private const string TimeProvider = "System.TimeProvider";
    private const string TimerCallbackType = "System.Threading.TimerCallback";
    private const string ITimerType = "System.Threading.ITimer";
    private const string FuncOfTask = "System.Func`1<System.Threading.Tasks.Task>";
    private const string FuncOfTaskResult = "System.Func`1<System.Threading.Tasks.Task`1<!!0>>";
    private const string FuncOfTaskResultDecl = "System.Func`1<System.Threading.Tasks.Task`1<TResult>>";

    // Cecil full names for the generic-antecedent Task<TResult>.ContinueWith surface. The
    // declaring type is the open Task`1; a call site renders the type's generic parameter as `!0` and
    // the method's own generic parameter (ContinueWith<TNewResult>) as `!!0`, whereas the controlled
    // shim's definition renders both by name (TResult from the declaring type, TNewResult from the
    // method). The call-site pass binds the shim's two type parameters declaring-type-first.
    private const string TaskTType = "System.Threading.Tasks.Task`1";
    private const string ActionOfTaskTVar = "System.Action`1<System.Threading.Tasks.Task`1<!0>>";
    private const string ActionOfTaskTDecl = "System.Action`1<System.Threading.Tasks.Task`1<TResult>>";
    private const string FuncOfTaskTAndNewResult = "System.Func`2<System.Threading.Tasks.Task`1<!0>,!!0>";
    private const string FuncOfTaskTAndNewResultDecl = "System.Func`2<System.Threading.Tasks.Task`1<TResult>,TNewResult>";
    private const string ActionOfTaskTAndStateVar = "System.Action`2<System.Threading.Tasks.Task`1<!0>,System.Object>";
    private const string ActionOfTaskTAndStateDecl = "System.Action`2<System.Threading.Tasks.Task`1<TResult>,System.Object>";
    private const string FuncOfTaskTStateAndNewResult = "System.Func`3<System.Threading.Tasks.Task`1<!0>,System.Object,!!0>";
    private const string FuncOfTaskTStateAndNewResultDecl = "System.Func`3<System.Threading.Tasks.Task`1<TResult>,System.Object,TNewResult>";

    // Cecil full names for the non-generic Task.ContinueWith surface: the state-carrying Action and the
    // result-producing Func forms (a generic method, `!!0` target and `TResult` replacement).
    private const string ActionOfTaskAndState = "System.Action`2<System.Threading.Tasks.Task,System.Object>";
    private const string FuncOfTaskAndResult = "System.Func`2<System.Threading.Tasks.Task,!!0>";
    private const string FuncOfTaskAndResultDecl = "System.Func`2<System.Threading.Tasks.Task,TResult>";
    private const string FuncOfTaskStateAndResult = "System.Func`3<System.Threading.Tasks.Task,System.Object,!!0>";
    private const string FuncOfTaskStateAndResultDecl = "System.Func`3<System.Threading.Tasks.Task,System.Object,TResult>";
    private const string TaskContinuationOptions = "System.Threading.Tasks.TaskContinuationOptions";

    // Cecil full names for the TaskFactory and TaskFactory<TResult> ContinueWhenAll/ContinueWhenAny surface. A call site
    // renders TaskFactory<TResult>'s type parameter as `!0` and the methods' own parameters (TAntecedentResult, then
    // TResult on the non-generic factory) as `!!0` and `!!1`; the shims' definitions render them by name.
    private const string ActionOfTaskArray = "System.Action`1<System.Threading.Tasks.Task[]>";
    private const string ActionOfTaskTArray = "System.Action`1<System.Threading.Tasks.Task`1<!!0>[]>";
    private const string ActionOfTaskT = "System.Action`1<System.Threading.Tasks.Task`1<!!0>>";
    private const string FuncOfTaskArrayAndMethodResult = "System.Func`2<System.Threading.Tasks.Task[],!!0>";
    private const string FuncOfTaskArrayAndTypeResult = "System.Func`2<System.Threading.Tasks.Task[],!0>";
    private const string FuncOfTaskTArrayAndSecondMethodResult = "System.Func`2<System.Threading.Tasks.Task`1<!!0>[],!!1>";
    private const string FuncOfTaskTArrayAndTypeResult = "System.Func`2<System.Threading.Tasks.Task`1<!!0>[],!0>";
    private const string FuncOfTaskAndTypeResult = "System.Func`2<System.Threading.Tasks.Task,!0>";
    private const string FuncOfTaskTAndSecondMethodResult = "System.Func`2<System.Threading.Tasks.Task`1<!!0>,!!1>";
    private const string FuncOfTaskTAndTypeResult = "System.Func`2<System.Threading.Tasks.Task`1<!!0>,!0>";
    private const string AntecedentArrayDecl = "System.Threading.Tasks.Task`1<TAntecedentResult>[]";
    private const string ActionOfAntecedentArrayDecl = "System.Action`1<System.Threading.Tasks.Task`1<TAntecedentResult>[]>";
    private const string ActionOfAntecedentDecl = "System.Action`1<System.Threading.Tasks.Task`1<TAntecedentResult>>";
    private const string FuncOfTaskArrayAndResultDecl = "System.Func`2<System.Threading.Tasks.Task[],TResult>";
    private const string FuncOfAntecedentArrayAndResultDecl = "System.Func`2<System.Threading.Tasks.Task`1<TAntecedentResult>[],TResult>";
    private const string FuncOfAntecedentAndResultDecl = "System.Func`2<System.Threading.Tasks.Task`1<TAntecedentResult>,TResult>";

    // Cecil full names for the TaskCompletionSource completion members. The generic source's members are
    // non-generic members of the closed generic type (`!0` target), bound to the shim's TResult from the receiver.
    private const string TaskCompletionSourceShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskCompletionSource";
    private const string ValueTaskSourceCoreShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.Sources.ControlledManualResetValueTaskSourceCore";
    private const string ValueTaskSourceCoreType = "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1";
    private const string ValueTaskSourceCoreRefDecl = "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1<TResult>&";
    private const string TaskCompletionSourceType = "System.Threading.Tasks.TaskCompletionSource";
    private const string TaskCompletionSourceTType = "System.Threading.Tasks.TaskCompletionSource`1";
    private const string TaskCompletionSourceTDecl = "System.Threading.Tasks.TaskCompletionSource`1<TResult>";
    private const string TypeArg0 = "!0";
    private const string TResultDecl = "TResult";
    private const string TaskTypeArg0 = "System.Threading.Tasks.Task`1<!0>";
    private const string ExceptionTypeName = "System.Exception";
    private const string IEnumerableOfException = "System.Collections.Generic.IEnumerable`1<System.Exception>";

    // Cecil full names for the ValueTask conversions. The receiver is a value type, so each shim takes it by reference.
    private const string ValueTaskShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledValueTask";
    private const string ValueTaskType = "System.Threading.Tasks.ValueTask";
    private const string ValueTaskTType = "System.Threading.Tasks.ValueTask`1";
    private const string ValueTaskRef = "System.Threading.Tasks.ValueTask&";
    private const string ValueTaskTRefDecl = "System.Threading.Tasks.ValueTask`1<TResult>&";

    // TaskCreationOptions selects the controlled StartNew overloads that carry an explicit options value.
    private const string TaskCreationOptions = "System.Threading.Tasks.TaskCreationOptions";

    // Cecil full names for the controlled System.Threading.Thread surface.
    private const string ThreadType = "System.Threading.Thread";
    private const string TimerType = "System.Threading.Timer";
    private const string ControlledTimerType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledTimer";
    private const string TimersTimerType = "System.Timers.Timer";
    private const string ControlledComponentTimerType = "Orleans.Testing.Simulation.Shims.System.Timers.ControlledTimer";
    private const string PeriodicTimerType = "System.Threading.PeriodicTimer";
    private const string ControlledPeriodicTimerType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledPeriodicTimer";
    private const string CancellationTokenSourceType = "System.Threading.CancellationTokenSource";
    private const string ThreadStart = "System.Threading.ThreadStart";
    private const string ParameterizedThreadStart = "System.Threading.ParameterizedThreadStart";
    private const string ObjectType = "System.Object";
    private const string TimeSpan = "System.TimeSpan";
    private const string ThreadPriority = "System.Threading.ThreadPriority";
    private const string ApartmentState = "System.Threading.ApartmentState";

    // Cecil full names for the controlled System.Threading.ThreadPool queueing surface. The
    // generic QueueUserWorkItem<TState>(Action<TState>, TState, bool) overloads are GenericInstanceMethods
    // at the call site (`!!0` target) resolved against their definitions (`TState` replacement).
    private const string ThreadPoolType = "System.Threading.ThreadPool";
    private const string WaitCallback = "System.Threading.WaitCallback";
    private const string IThreadPoolWorkItem = "System.Threading.IThreadPoolWorkItem";
    private const string ActionOfTStateVar = "System.Action`1<!!0>";
    private const string TStateVar = "!!0";
    private const string ActionOfTStateDecl = "System.Action`1<TState>";
    private const string TStateDecl = "TState";
    private const string NativeOverlappedPtr = "System.Threading.NativeOverlapped*";

    // Cecil full names for the controlled wait-handle and atomic control registered-wait surface. RegisteredWaitHandle is a sealed
    // class returned by the eight ThreadPool.RegisterWaitForSingleObject / UnsafeRegisterWaitForSingleObject
    // factories; it is retargeted by whole-type substitution (like System.Threading.Lock) so every local /
    // field / parameter and the Unregister instance member remap onto the controlled type, while the static
    // factories are redirected to shims returning it.
    private const string RegisteredWaitHandleType = "System.Threading.RegisteredWaitHandle";
    private const string ControlledRegisteredWaitHandleType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledRegisteredWaitHandle";

    // Cecil full names shared by the registered-wait factory redirects. Each of
    // RegisterWaitForSingleObject and UnsafeRegisterWaitForSingleObject has four timeout overloads
    // (UInt32/Int32/Int64/TimeSpan); the callback delegate is WaitOrTimerCallback.
    private const string UInt32 = "System.UInt32";
    private const string WaitHandle = "System.Threading.WaitHandle";
    private const string WaitOrTimerCallback = "System.Threading.WaitOrTimerCallback";

    // Cecil full names for the controlled System.Threading.Tasks.Parallel surface. The
    // generic ForEach<TSource> overloads are GenericInstanceMethods at the call site (`!!0` target) resolved
    // against their definitions (`TSource` replacement); the ParallelLoopState / TLocal / Partitioner
    // overloads are rejected at the call site.
    private const string ParallelType = "System.Threading.Tasks.Parallel";
    private const string ParallelOptionsType = "System.Threading.Tasks.ParallelOptions";
    private const string ActionArray = "System.Action[]";
    private const string ActionOfInt32 = "System.Action`1<System.Int32>";
    private const string ActionOfInt64 = "System.Action`1<System.Int64>";
    private const string IEnumerableOfTSourceVar = "System.Collections.Generic.IEnumerable`1<!!0>";
    private const string IEnumerableOfTSourceDecl = "System.Collections.Generic.IEnumerable`1<TSource>";
    private const string ActionOfTSourceVar = "System.Action`1<!!0>";
    private const string ActionOfTSourceDecl = "System.Action`1<TSource>";
    private const string ActionOfInt32LoopState = "System.Action`2<System.Int32,System.Threading.Tasks.ParallelLoopState>";
    private const string ActionOfInt64LoopState = "System.Action`2<System.Int64,System.Threading.Tasks.ParallelLoopState>";
    private const string ActionOfTSourceLoopStateVar = "System.Action`2<!!0,System.Threading.Tasks.ParallelLoopState>";
    private const string ActionOfTSourceLoopStateInt64Var = "System.Action`3<!!0,System.Threading.Tasks.ParallelLoopState,System.Int64>";
    private const string PartitionerOfTSourceVar = "System.Collections.Concurrent.Partitioner`1<!!0>";
    private const string PartitionerOfTSourceDecl = "System.Collections.Concurrent.Partitioner`1<TSource>";
    private const string OrderablePartitionerOfTSourceVar = "System.Collections.Concurrent.OrderablePartitioner`1<!!0>";
    private const string PartitionerType = "System.Collections.Concurrent.Partitioner";

    // The asynchronous loops: ForEachAsync<TSource> over IEnumerable/IAsyncEnumerable and ForAsync<T>, whose body is a
    // Func<element, CancellationToken, ValueTask>.
    private const string IAsyncEnumerableOfTSourceVar = "System.Collections.Generic.IAsyncEnumerable`1<!!0>";
    private const string IAsyncEnumerableOfTSourceDecl = "System.Collections.Generic.IAsyncEnumerable`1<TSource>";
    private const string AsyncBodyOfTSourceVar = "System.Func`3<!!0,System.Threading.CancellationToken,System.Threading.Tasks.ValueTask>";
    private const string AsyncBodyOfTSourceDecl = "System.Func`3<TSource,System.Threading.CancellationToken,System.Threading.Tasks.ValueTask>";
    private const string AsyncBodyOfTDecl = "System.Func`3<T,System.Threading.CancellationToken,System.Threading.Tasks.ValueTask>";

    // Cecil full names for the controlled System.Threading.Channels surface: virtual members of the open generic reader
    // and writer (`!0` target), redirected to receiver-first generic shims (`T` replacement).
    private const string ChannelReaderShim = "Orleans.Testing.Simulation.Shims.System.Threading.Channels.ControlledChannelReader";
    private const string ChannelWriterShim = "Orleans.Testing.Simulation.Shims.System.Threading.Channels.ControlledChannelWriter";
    private const string ChannelReaderType = "System.Threading.Channels.ChannelReader`1";
    private const string ChannelWriterType = "System.Threading.Channels.ChannelWriter`1";
    private const string ChannelReaderTDecl = "System.Threading.Channels.ChannelReader`1<T>";
    private const string ChannelWriterTDecl = "System.Threading.Channels.ChannelWriter`1<T>";

    // Cecil full names for the PLINQ entry points, rejected at the call site.
    private const string ParallelEnumerableType = "System.Linq.ParallelEnumerable";
    private const string ParallelEnumerableShim = "Orleans.Testing.Simulation.Shims.System.Linq.ControlledParallelEnumerable";
    private const string NonGenericIEnumerable = "System.Collections.IEnumerable";

    // Cecil full names for the background-work, TPL Dataflow, and WebSocket entry points, rejected at the call site.
    private const string BackgroundWorkerType = "System.ComponentModel.BackgroundWorker";
    private const string BackgroundWorkerShim = "Orleans.Testing.Simulation.Shims.System.ComponentModel.ControlledBackgroundWorker";
    private const string BackgroundServiceType = "Microsoft.Extensions.Hosting.BackgroundService";
    private const string BackgroundServiceShim = "Orleans.Testing.Simulation.Shims.Microsoft.Extensions.Hosting.ControlledBackgroundService";
    private const string DataflowNamespace = "System.Threading.Tasks.Dataflow.";
    private const string DataflowShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.Dataflow.ControlledDataflowBlock";
    private const string WebSocketType = "System.Net.WebSockets.WebSocket";
    private const string WebSocketShim = "Orleans.Testing.Simulation.Shims.System.Net.WebSockets.ControlledWebSocket";
    private const string WebSocketStreamType = "System.Net.WebSockets.WebSocketStream";
    private const string WebSocketStreamShim = "Orleans.Testing.Simulation.Shims.System.Net.WebSockets.ControlledWebSocketStream";
    private const string WebSocketMessageType = "System.Net.WebSockets.WebSocketMessageType";

    // Cecil full names for the hosting extensions which block on the host's work or time out on a real timer.
    private const string HostExtensionsType = "Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions";
    private const string HostExtensionsShim = "Orleans.Testing.Simulation.Shims.Microsoft.Extensions.Hosting.ControlledHostingAbstractionsHostExtensions";
    private const string IHostType = "Microsoft.Extensions.Hosting.IHost";
    private const string HostBuilderExtensionsType = "Microsoft.Extensions.Hosting.HostingAbstractionsHostBuilderExtensions";
    private const string HostBuilderExtensionsShim = "Orleans.Testing.Simulation.Shims.Microsoft.Extensions.Hosting.ControlledHostingAbstractionsHostBuilderExtensions";
    private const string IHostBuilderType = "Microsoft.Extensions.Hosting.IHostBuilder";

    // Cecil full names for the members which send through an HttpClient, whose timeout runs on a real timer.
    private const string HttpClientType = "System.Net.Http.HttpClient";
    private const string HttpClientShim = "Orleans.Testing.Simulation.Shims.System.Net.Http.ControlledHttpClient";
    private const string HttpMessageInvokerType = "System.Net.Http.HttpMessageInvoker";
    private const string HttpMessageInvokerShim = "Orleans.Testing.Simulation.Shims.System.Net.Http.ControlledHttpMessageInvoker";
    private const string HttpClientJsonType = "System.Net.Http.Json.HttpClientJsonExtensions";
    private const string HttpClientJsonShim = "Orleans.Testing.Simulation.Shims.System.Net.Http.Json.ControlledHttpClientJsonExtensions";
    private const string HttpRequestMessageType = "System.Net.Http.HttpRequestMessage";
    private const string HttpCompletionOptionType = "System.Net.Http.HttpCompletionOption";
    private const string HttpContentType = "System.Net.Http.HttpContent";
    private const string UriType = "System.Uri";
    private const string SystemType = "System.Type";
    private const string JsonOptionsType = "System.Text.Json.JsonSerializerOptions";
    private const string JsonContextType = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string JsonTypeInfoOfValue = "System.Text.Json.Serialization.Metadata.JsonTypeInfo`1<!!0>";
    private const string MethodTypeArg0 = "!!0";

    // Every HttpClient member which sends a request. Send and SendAsync with a request and a token are the virtual
    // overrides of HttpMessageInvoker's.
    private static readonly (string Member, string[] Parameters)[] HttpClientSends =
    [
        .. HttpUriSends("DeleteAsync", []),
        .. HttpUriSends("GetAsync", []),
        .. HttpUriSends("GetAsync", [HttpCompletionOptionType]),
        .. HttpUriSends("GetByteArrayAsync", []),
        .. HttpUriSends("GetStreamAsync", []),
        .. HttpUriSends("GetStringAsync", []),
        .. HttpUriSends("PatchAsync", [HttpContentType]),
        .. HttpUriSends("PostAsync", [HttpContentType]),
        .. HttpUriSends("PutAsync", [HttpContentType]),
        .. HttpRequestSends("Send"),
        .. HttpRequestSends("SendAsync"),
    ];

    // Every HttpClientJsonExtensions member: each sends through the HttpClient it is given.
    private static readonly (string Member, string[] Parameters)[] HttpClientJsonSends =
    [
        .. HttpJsonReads("DeleteFromJsonAsync"),
        .. HttpJsonReads("GetFromJsonAsync"),
        .. HttpJsonWrites("PatchAsJsonAsync"),
        .. HttpJsonWrites("PostAsJsonAsync"),
        .. HttpJsonWrites("PutAsJsonAsync"),
        .. HttpJsonEnumerations("GetFromJsonAsAsyncEnumerable"),
    ];

    // Every public block of System.Threading.Tasks.Dataflow; ConcurrencyEscapeAuditTests fails on one a runtime adds,
    // since its constructor reaches TaskScheduler.Default.
    private static readonly (string Type, string Id)[] DataflowBlockTypes =
    [
        ("ActionBlock`1", "actionblock"), ("BatchBlock`1", "batchblock"), ("BatchedJoinBlock`2", "batchedjoinblock2"),
        ("BatchedJoinBlock`3", "batchedjoinblock3"), ("BroadcastBlock`1", "broadcastblock"), ("BufferBlock`1", "bufferblock"),
        ("JoinBlock`2", "joinblock2"), ("JoinBlock`3", "joinblock3"), ("TransformBlock`2", "transformblock"),
        ("TransformManyBlock`2", "transformmanyblock"), ("WriteOnceBlock`1", "writeonceblock"),
    ];

    // The DataflowBlock helpers which run work on TaskScheduler.Default, block the caller, or time out on a real timer.
    private static readonly (string Member, string Id)[] DataflowSchedulingHelpers =
    [
        ("Choose", "choose"), ("Receive", "receive"), ("ReceiveAsync", "receiveasync"), ("ReceiveAllAsync", "receiveallasync"),
        ("SendAsync", "sendasync"), ("OutputAvailableAsync", "outputavailableasync"),
    ];

    // Cecil full names for the controlled BlockingCollection<T> surface: members of the open generic type (`!0` target),
    // redirected to generic shims (`T` replacement), receiver first for the instance members.
    private const string BlockingCollectionType = "System.Collections.Concurrent.BlockingCollection`1";
    private const string BlockingCollectionShim = "Orleans.Testing.Simulation.Shims.System.Collections.Concurrent.ControlledBlockingCollection";
    private const string BlockingCollectionTDecl = "System.Collections.Concurrent.BlockingCollection`1<T>";
    private const string BlockingCollectionArrayVar = "System.Collections.Concurrent.BlockingCollection`1<!0>[]";
    private const string BlockingCollectionArrayDecl = "System.Collections.Concurrent.BlockingCollection`1<T>[]";
    private const string TypeArg0Ref = "!0&";

    // Cecil full names for the controlled Stream, TextReader, and TextWriter asynchronous members. The virtual members
    // redirect receiver first to a shim which calls an override as written and otherwise runs the base algorithm, and
    // their base calls to the shim's `Base` form.
    private const string StreamType = "System.IO.Stream";
    private const string TextReaderType = "System.IO.TextReader";
    private const string TextWriterType = "System.IO.TextWriter";
    private const string StreamShim = "Orleans.Testing.Simulation.Shims.System.IO.ControlledStream";
    private const string TextReaderShim = "Orleans.Testing.Simulation.Shims.System.IO.ControlledTextReader";
    private const string TextWriterShim = "Orleans.Testing.Simulation.Shims.System.IO.ControlledTextWriter";
    private const string ByteArray = "System.Byte[]";
    private const string CharArray = "System.Char[]";
    private const string CharType = "System.Char";
    private const string MemoryOfByte = "System.Memory`1<System.Byte>";
    private const string ReadOnlyMemoryOfByte = "System.ReadOnlyMemory`1<System.Byte>";
    private const string MemoryOfChar = "System.Memory`1<System.Char>";
    private const string ReadOnlyMemoryOfChar = "System.ReadOnlyMemory`1<System.Char>";
    private const string AsyncCallbackType = "System.AsyncCallback";
    private const string AsyncResultType = "System.IAsyncResult";
    private const string RuneType = "System.Text.Rune";
    private const string StringBuilderType = "System.Text.StringBuilder";

    // The thread-local (TLocal) overloads: For<TLocal> declares TLocal as its only method generic parameter (`!!0`), while
    // ForEach<TSource, TLocal> declares it second (`!!1`).
    private const string ForLocalInit = "System.Func`1<!!0>";
    private const string ForInt32LocalBody = "System.Func`4<System.Int32,System.Threading.Tasks.ParallelLoopState,!!0,!!0>";
    private const string ForInt64LocalBody = "System.Func`4<System.Int64,System.Threading.Tasks.ParallelLoopState,!!0,!!0>";
    private const string ForLocalFinally = "System.Action`1<!!0>";
    private const string ForEachLocalInit = "System.Func`1<!!1>";
    private const string ForEachLocalBody = "System.Func`4<!!0,System.Threading.Tasks.ParallelLoopState,!!1,!!1>";
    private const string ForEachIndexedLocalBody = "System.Func`5<!!0,System.Threading.Tasks.ParallelLoopState,System.Int64,!!1,!!1>";
    private const string ForEachLocalFinally = "System.Action`1<!!1>";

    // Cecil full names for the uncontrolled-invocation surface: process control and
    // abrupt host termination. These cannot be modelled by the deterministic scheduler, so every rewritten
    // call site is rejected (throws a diagnostic naming the exact API). Process.Start/Run/StartAndForget are
    // static and return Process/Boolean/exit statuses/process ids; the instance Kill/Signal/wait/read members
    // and Environment.Exit/FailFast are void or value-returning - InjectRejection handles every shape
    // uniformly by prepending a throwing call.
    private const string ProcessType = "System.Diagnostics.Process";
    private const string EnvironmentType = "System.Environment";
    private const string ProcessStartInfoType = "System.Diagnostics.ProcessStartInfo";
    private const string SecureStringType = "System.Security.SecureString";
    private const string IEnumerableOfStringType = "System.Collections.Generic.IEnumerable`1<System.String>";
    private const string TimeSpanType = "System.TimeSpan";
    private const string NullableTimeSpanType = "System.Nullable`1<System.TimeSpan>";
    private const string ProcessExitStatusRef = "System.Diagnostics.ProcessExitStatus&";
    private const string PosixSignalType = "System.Runtime.InteropServices.PosixSignal";
    private const string SafeProcessHandleType = "Microsoft.Win32.SafeHandles.SafeProcessHandle";
    private const string ProcessThreadType = "System.Diagnostics.ProcessThread";
    private const string ProcessPriorityClassType = "System.Diagnostics.ProcessPriorityClass";
    private const string ThreadPriorityLevelType = "System.Diagnostics.ThreadPriorityLevel";
    private const string WindowsProcessStartArgumentsType = "System.Runtime.InteropServices.WindowsProcessStartArguments";
    private const string UnixProcessStartArgumentsType = "System.Runtime.InteropServices.UnixProcessStartArguments";
    private const string WindowsProcessStarter = "System.Func`2<System.Runtime.InteropServices.WindowsProcessStartArguments,System.IntPtr>";
    private const string UnixProcessStarter = "System.Func`2<System.Runtime.InteropServices.UnixProcessStartArguments,System.Int32>";
    private const string CancellationTokenType = "System.Threading.CancellationToken";
    private const string ExceptionType = "System.Exception";
    private const string UncontrolledInvocationShim = "Orleans.Testing.Simulation.Runtime.UncontrolledInvocationGuard";

    // Cecil full names for the controlled monitor/lock/semaphore control synchronization surface. Monitor is entirely static, so
    // the shim signatures match the BCL targets exactly. `ref bool` renders as `System.Boolean&`.
    // System.Threading.Lock (and its nested Scope ref struct) is retargeted by type substitution, so the
    // C# `lock (Lock)` lowering (EnterScope/Scope.Dispose) is redirected wholesale. SemaphoreSlim is a
    // sealed class whose controlled shim is receiver-first: each instance member's shim prepends the
    // SemaphoreSlim receiver, and the two constructors are redirected to Create factories.
    private const string MonitorType = "System.Threading.Monitor";
    private const string BooleanRef = "System.Boolean&";
    private const string LockType = "System.Threading.Lock";
    private const string LockScopeType = "System.Threading.Lock/Scope";
    private const string ControlledLockType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledLock";
    private const string ControlledLockScopeType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledLock/Scope";
    private const string SemaphoreSlimType = "System.Threading.SemaphoreSlim";

    // Cecil full names for the controlled wait-handle and atomic control Interlocked atomic surface. Every overload takes its
    // first argument by reference, which Cecil renders with a trailing '&'. The generic Exchange<T>/
    // CompareExchange<T> overloads are GenericInstanceMethods at the call site (`!!0`/`!!0&` target)
    // resolved against their shim definitions, whose generic parameter is named T (`T`/`T&` replacement).
    private const string InterlockedType = "System.Threading.Interlocked";
    private const string Int32Ref = "System.Int32&";
    private const string Int64Ref = "System.Int64&";
    private const string UInt32Ref = "System.UInt32&";
    private const string UInt64 = "System.UInt64";
    private const string UInt64Ref = "System.UInt64&";
    private const string SByteType = "System.SByte";
    private const string SByteRef = "System.SByte&";
    private const string Int16Type = "System.Int16";
    private const string Int16Ref = "System.Int16&";
    private const string ByteType = "System.Byte";
    private const string ByteRef = "System.Byte&";
    private const string UInt16Type = "System.UInt16";
    private const string UInt16Ref = "System.UInt16&";
    private const string SingleType = "System.Single";
    private const string SingleRef = "System.Single&";
    private const string DoubleType = "System.Double";
    private const string DoubleRef = "System.Double&";
    private const string IntPtrType = "System.IntPtr";
    private const string IntPtrRef = "System.IntPtr&";
    private const string UIntPtrType = "System.UIntPtr";
    private const string UIntPtrRef = "System.UIntPtr&";
    private const string ObjectRef = "System.Object&";
    private const string GenericArg0 = "!!0";
    private const string GenericArg0Ref = "!!0&";
    private const string GenericTDecl = "T";
    private const string GenericTRefDecl = "T&";

    // Cecil full name for the controlled wait-handle and atomic control Volatile acquire/release surface. Reuses the primitive
    // ref-type constants above; the generic Read<T>/Write<T> overloads use the `!!0`/`!!0&` target ->
    // `T`/`T&` replacement split.
    private const string VolatileType = "System.Threading.Volatile";

    // System.Threading.SpinWait is a value type (struct) retargeted by whole-type substitution, exactly like
    // System.Threading.Lock/Scope. Every local/field/parameter typed SpinWait, each `new SpinWait()` /
    // `default`, the instance members (Count, NextSpinWillYield, Reset, SpinOnce overloads) and the static
    // SpinUntil overloads remap onto the controlled struct.
    private const string SpinWaitType = "System.Threading.SpinWait";
    private const string ControlledSpinWaitType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledSpinWait";

    // Cecil full names for the controlled wait-handle and atomic control event / wait-handle surface. AutoResetEvent,
    // ManualResetEvent and EventWaitHandle are concrete sealed classes: the real objects are retained as
    // identity handles and side state is held in a ConditionalWeakTable, so each `new` is redirected to a
    // Create factory and every instance member is a receiver-first static shim declared on WaitHandle
    // (WaitOne/Dispose/Close/Handle/SafeWaitHandle) or EventWaitHandle (Set/Reset). Named / cross-process
    // event APIs (named ctors, OpenExisting, TryOpenExisting) are rejected: a single simulation process
    // cannot faithfully model a system-wide kernel object. EventResetMode/NamedWaitHandleOptions select the
    // reset mode / named options; SafeWaitHandle is the raw-handle type surfaced by the rejected accessors.
    private const string WaitHandleType = "System.Threading.WaitHandle";
    private const string EventWaitHandleType = "System.Threading.EventWaitHandle";
    private const string AutoResetEventType = "System.Threading.AutoResetEvent";
    private const string ManualResetEventType = "System.Threading.ManualResetEvent";
    private const string EventResetModeType = "System.Threading.EventResetMode";
    private const string NamedWaitHandleOptionsType = "System.Threading.NamedWaitHandleOptions";
    private const string SafeWaitHandleType = "Microsoft.Win32.SafeHandles.SafeWaitHandle";
    private const string EventWaitHandleRef = "System.Threading.EventWaitHandle&";
    private const string WaitHandleArray = "System.Threading.WaitHandle[]";
    private const string WaitHandleShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledWaitHandle";
    private const string EventWaitHandleShim = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledEventWaitHandle";

    // Cecil full names for the modern synchronization families. ReaderWriterLockSlim and
    // ManualResetEventSlim retain BCL identity objects and use receiver-first static shims. Mutex and the
    // kernel Semaphore do the same, while SpinLock, Barrier, and CountdownEvent are complete substitutions.
    private const string ReaderWriterLockSlimType = "System.Threading.ReaderWriterLockSlim";
    private const string LockRecursionPolicyType = "System.Threading.LockRecursionPolicy";
    private const string ManualResetEventSlimType = "System.Threading.ManualResetEventSlim";
    private const string MutexType = "System.Threading.Mutex";
    private const string MutexRef = "System.Threading.Mutex&";
    private const string KernelSemaphoreType = "System.Threading.Semaphore";
    private const string KernelSemaphoreRef = "System.Threading.Semaphore&";
    private const string ExecutionContextType = "System.Threading.ExecutionContext";
    private const string ContextCallbackType = "System.Threading.ContextCallback";
    private const string AsyncFlowControlType = "System.Threading.AsyncFlowControl";
    private const string SerializationInfoType = "System.Runtime.Serialization.SerializationInfo";
    private const string StreamingContextType = "System.Runtime.Serialization.StreamingContext";
    private const string SynchronizationContextType = "System.Threading.SynchronizationContext";
    private const string SendOrPostCallbackType = "System.Threading.SendOrPostCallback";
    private const string IntPtrArray = "System.IntPtr[]";
    private const string BarrierType = "System.Threading.Barrier";
    private const string ControlledBarrierType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier";
    private const string CountdownEventType = "System.Threading.CountdownEvent";
    private const string ControlledCountdownEventType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledCountdownEvent";
    private const string SpinLockType = "System.Threading.SpinLock";
    private const string ControlledSpinLockType = "Orleans.Testing.Simulation.Shims.System.Threading.ControlledSpinLock";


    // Cecil full names for the compiler-generated async machinery (BCL) and their controlled substitutes.
    // Nested awaiter types use Cecil's '/' separator; generic arities carry the backtick.
    private const string CompilerNs = "System.Runtime.CompilerServices.";
    private const string ControlledNs = "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.";
    private const string BclBuilder = CompilerNs + "AsyncTaskMethodBuilder";
    private const string BclBuilderT = CompilerNs + "AsyncTaskMethodBuilder`1";
    private const string BclTaskAwaiter = CompilerNs + "TaskAwaiter";
    private const string BclTaskAwaiterT = CompilerNs + "TaskAwaiter`1";
    private const string BclConfigured = CompilerNs + "ConfiguredTaskAwaitable";
    private const string BclConfiguredT = CompilerNs + "ConfiguredTaskAwaitable`1";
    private const string BclConfiguredAwaiter = CompilerNs + "ConfiguredTaskAwaitable/ConfiguredTaskAwaiter";
    private const string BclConfiguredAwaiterT = CompilerNs + "ConfiguredTaskAwaitable`1/ConfiguredTaskAwaiter";
    private const string BclYieldAwaitable = CompilerNs + "YieldAwaitable";
    private const string BclYieldAwaiter = CompilerNs + "YieldAwaitable/YieldAwaiter";
    private const string BclValueBuilder = CompilerNs + "AsyncValueTaskMethodBuilder";
    private const string BclValueBuilderT = CompilerNs + "AsyncValueTaskMethodBuilder`1";
    private const string BclValueAwaiter = CompilerNs + "ValueTaskAwaiter";
    private const string BclValueAwaiterT = CompilerNs + "ValueTaskAwaiter`1";
    private const string BclConfiguredValue = CompilerNs + "ConfiguredValueTaskAwaitable";
    private const string BclConfiguredValueT = CompilerNs + "ConfiguredValueTaskAwaitable`1";
    private const string BclConfiguredValueAwaiter = CompilerNs + "ConfiguredValueTaskAwaitable/ConfiguredValueTaskAwaiter";
    private const string BclConfiguredValueAwaiterT = CompilerNs + "ConfiguredValueTaskAwaitable`1/ConfiguredValueTaskAwaiter";
    private const string ControlledBuilder = ControlledNs + "ControlledAsyncTaskMethodBuilder";
    private const string ControlledBuilderT = ControlledNs + "ControlledAsyncTaskMethodBuilder`1";
    private const string ControlledTaskAwaiter = ControlledNs + "ControlledTaskAwaiter";
    private const string ControlledTaskAwaiterT = ControlledNs + "ControlledTaskAwaiter`1";
    private const string ControlledConfigured = ControlledNs + "ControlledConfiguredTaskAwaitable";
    private const string ControlledConfiguredT = ControlledNs + "ControlledConfiguredTaskAwaitable`1";
    private const string ControlledConfiguredAwaiter = ControlledNs + "ControlledConfiguredTaskAwaiter";
    private const string ControlledConfiguredAwaiterT = ControlledNs + "ControlledConfiguredTaskAwaiter`1";
    private const string ControlledYieldAwaitable = ControlledNs + "ControlledYieldAwaitable";
    private const string ControlledYieldAwaiter = ControlledNs + "ControlledYieldAwaiter";
    private const string ControlledValueBuilder = ControlledNs + "ControlledAsyncValueTaskMethodBuilder";
    private const string ControlledValueBuilderT = ControlledNs + "ControlledAsyncValueTaskMethodBuilder`1";
    private const string ControlledValueAwaiter = ControlledNs + "ControlledValueTaskAwaiter";
    private const string ControlledValueAwaiterT = ControlledNs + "ControlledValueTaskAwaiter`1";
    private const string ControlledConfiguredValue = ControlledNs + "ControlledConfiguredValueTaskAwaitable";
    private const string ControlledConfiguredValueT = ControlledNs + "ControlledConfiguredValueTaskAwaitable`1";
    private const string ControlledConfiguredValueAwaiter = ControlledNs + "ControlledConfiguredValueTaskAwaiter";
    private const string ControlledConfiguredValueAwaiterT = ControlledNs + "ControlledConfiguredValueTaskAwaiter`1";
    private const string BclConfiguredAsyncDisposable = CompilerNs + "ConfiguredAsyncDisposable";
    private const string BclConfiguredAsyncEnumerable = CompilerNs + "ConfiguredCancelableAsyncEnumerable`1";
    private const string BclConfiguredAsyncEnumerator = CompilerNs + "ConfiguredCancelableAsyncEnumerable`1/Enumerator";
    private const string ControlledConfiguredAsyncDisposable = ControlledNs + "ControlledConfiguredAsyncDisposable";
    private const string ControlledConfiguredAsyncEnumerable = ControlledNs + "ControlledConfiguredCancelableAsyncEnumerable`1";
    private const string ControlledConfiguredAsyncEnumerator = ControlledNs + "ControlledConfiguredCancelableAsyncEnumerable`1/Enumerator";
    private const string TaskAsyncEnumerableExtensionsType = "System.Threading.Tasks.TaskAsyncEnumerableExtensions";
    private const string AsyncEnumerableShim = "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskAsyncEnumerableExtensions";
    private const string AsyncDisposableType = "System.IAsyncDisposable";
    private const string AsyncEnumerableT = "System.Collections.Generic.IAsyncEnumerable`1<!!0>";
    private const string AsyncEnumerableTDecl = "System.Collections.Generic.IAsyncEnumerable`1<T>";
    private const string AsyncHelpersType = CompilerNs + "AsyncHelpers";
    private const string AsyncHelpersShim = ControlledNs + "ControlledAsyncHelpers";
    private const string TaskOfInt32 = "System.Threading.Tasks.Task`1<System.Int32>";
    private const string BclConfiguredTArg = CompilerNs + "ConfiguredTaskAwaitable`1<!!0>";
    private const string BclConfiguredValueTArg = CompilerNs + "ConfiguredValueTaskAwaitable`1<!!0>";
    private const string ControlledConfiguredTDecl = ControlledNs + "ControlledConfiguredTaskAwaitable`1<T>";
    private const string ControlledConfiguredValueTDecl = ControlledNs + "ControlledConfiguredValueTaskAwaitable`1<T>";

    /// <summary>
    /// Gets the target runtime whose reference assemblies every built-in rule is classified against. Each built-in rule
    /// supports exactly this runtime, so instrumenting for another one fails with <c>CWR0010</c> until the rules are
    /// re-derived for it.
    /// </summary>
    public static Version SupportedRuntime => new(11, 0);

    private static readonly ImmutableArray<BuiltInRuleEntry> DeterministicBcl = ForSupportedRuntime(BuildDeterministicBclEntries());
    private static readonly ImmutableArray<BuiltInRuleEntry> ControlledTasks = ForSupportedRuntime(BuildControlledTasksEntries());
    private static readonly ImmutableHashSet<string> ControlledTaskRuleIds =
        ControlledTasks.Select(entry => entry.Rule.Id).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>Gets the ids of every built-in rule set that can be enabled by name.</summary>
    public static ImmutableArray<string> AvailableIds { get; } = [DeterministicBclId, ControlledTasksId];

    /// <summary>Gets every rule family in canonical (declared) order.</summary>
    public static ImmutableArray<BuiltInRuleFamily> AllFamilies { get; } =
    [
        BuiltInRuleFamily.Clock,
        BuiltInRuleFamily.Identity,
        BuiltInRuleFamily.Random,
        BuiltInRuleFamily.Crypto,
        BuiltInRuleFamily.TaskCombinators,
        BuiltInRuleFamily.TaskSynchronization,
        BuiltInRuleFamily.TaskContinuations,
        BuiltInRuleFamily.TaskCompletionSource,
        BuiltInRuleFamily.TaskTime,
        BuiltInRuleFamily.TaskScheduling,
        BuiltInRuleFamily.AsyncMachinery,
        BuiltInRuleFamily.ValueTaskMachinery,
        BuiltInRuleFamily.ValueTaskConversions,
        BuiltInRuleFamily.TaskFactory,
        BuiltInRuleFamily.TaskFactoryContinuations,
        BuiltInRuleFamily.TaskFactoryFromAsync,
        BuiltInRuleFamily.Thread,
        BuiltInRuleFamily.ThreadPool,
        BuiltInRuleFamily.Timers,
        BuiltInRuleFamily.CancellationTimers,
        BuiltInRuleFamily.Parallel,
        BuiltInRuleFamily.Channels,
        BuiltInRuleFamily.Plinq,
        BuiltInRuleFamily.BlockingCollection,
        BuiltInRuleFamily.AsyncIO,
        BuiltInRuleFamily.BackgroundWork,
        BuiltInRuleFamily.Dataflow,
        BuiltInRuleFamily.WebSockets,
        BuiltInRuleFamily.HttpClient,
        BuiltInRuleFamily.Hosting,
        BuiltInRuleFamily.Monitor,
        BuiltInRuleFamily.Lock,
        BuiltInRuleFamily.Semaphore,
        BuiltInRuleFamily.Interlocked,
        BuiltInRuleFamily.Volatile,
        BuiltInRuleFamily.SpinWait,
        BuiltInRuleFamily.WaitHandle,
        BuiltInRuleFamily.ReaderWriterLockSlim,
        BuiltInRuleFamily.ManualResetEventSlim,
        BuiltInRuleFamily.Mutex,
        BuiltInRuleFamily.KernelSemaphore,
        BuiltInRuleFamily.SpinLock,
        BuiltInRuleFamily.ExecutionContext,
        BuiltInRuleFamily.SynchronizationContext,
        BuiltInRuleFamily.Barrier,
        BuiltInRuleFamily.CountdownEvent,
        BuiltInRuleFamily.UncontrolledInvocation,
    ];

    /// <summary>Gets the (family, rule) entries of the deterministic BCL rule set, for documentation and inventory generation.</summary>
    public static ImmutableArray<(BuiltInRuleFamily Family, RewriteRule Rule)> DeterministicBclInventory { get; } =
        [.. DeterministicBcl.Select(e => (e.Family, e.Rule))];

    /// <summary>Gets the (family, rule) entries of the controlled task rule set, for documentation and inventory generation.</summary>
    public static ImmutableArray<(BuiltInRuleFamily Family, RewriteRule Rule)> ControlledTasksInventory { get; } =
        [.. ControlledTasks.Select(e => (e.Family, e.Rule))];

    /// <summary>Gets a value indicating whether <paramref name="id"/> names a known built-in rule set.</summary>
    public static bool IsKnownId(string id) => AvailableIds.Contains(id, StringComparer.Ordinal);

    internal static bool ContainsControlledTaskRules(RewriteRuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        return ruleSet.Rules.Any(rule => ControlledTaskRuleIds.Contains(rule.Id));
    }

    /// <summary>Parses a case-insensitive family name (e.g. <c>Clock</c>, <c>crypto</c>).</summary>
    public static bool TryParseFamily(string text, out BuiltInRuleFamily family) =>
        Enum.TryParse(text, ignoreCase: true, out family) && Enum.IsDefined(family);

    /// <summary>
    /// Builds the deterministic BCL rule set restricted to <paramref name="families"/>, preserving the
    /// canonical family and rule order so the result is stable across runs.
    /// </summary>
    /// <param name="families">The families to include. An empty set produces an empty rule set.</param>
    /// <returns>The versioned rule set.</returns>
    public static RewriteRuleSet BuildDeterministicBcl(IEnumerable<BuiltInRuleFamily> families)
    {
        ArgumentNullException.ThrowIfNull(families);
        var selected = new HashSet<BuiltInRuleFamily>(families);
        IEnumerable<RewriteRule> rules = DeterministicBcl
            .Where(e => selected.Contains(e.Family))
            .Select(e => e.Rule);
        return new RewriteRuleSet(DeterministicBclId, DeterministicBclVersion, rules);
    }

    /// <summary>
    /// Builds the controlled task rule set restricted to <paramref name="families"/>, preserving the
    /// canonical family and rule order so the result is stable across runs.
    /// </summary>
    /// <param name="families">The families to include. An empty set produces an empty rule set.</param>
    /// <returns>The versioned rule set.</returns>
    public static RewriteRuleSet BuildControlledTasks(IEnumerable<BuiltInRuleFamily> families)
    {
        ArgumentNullException.ThrowIfNull(families);
        var selected = new HashSet<BuiltInRuleFamily>(families);
        IEnumerable<RewriteRule> rules = ControlledTasks
            .Where(e => selected.Contains(e.Family))
            .Select(e => e.Rule);
        return new RewriteRuleSet(ControlledTasksId, ControlledTasksVersion, rules);
    }

    private static ImmutableArray<BuiltInRuleEntry> BuildControlledTasksEntries()
    {
        var builder = ImmutableArray.CreateBuilder<BuiltInRuleEntry>();

        // ---- Combinators: WhenAll / WhenAny non-generic overloads -> controlled equivalents ----
        // Completion order becomes a deterministic function of logical-thread completion instead of the
        // physical thread-pool race. The generic Task<TResult> overloads and Result<T> accessor below are
        // bound through the call-site pass's generic-arity substitution (method and declaring-type args).
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenall.array",
            MemberSignature.Method(Task, "WhenAll", TaskArray), Shim(TaskShim, "WhenAll", TaskArray));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenall.span",
            MemberSignature.Method(Task, "WhenAll", ReadOnlySpanTask), Shim(TaskShim, "WhenAll", ReadOnlySpanTask));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenall.enumerable",
            MemberSignature.Method(Task, "WhenAll", IEnumerableTask), Shim(TaskShim, "WhenAll", IEnumerableTask));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.array",
            MemberSignature.Method(Task, "WhenAny", TaskArray), Shim(TaskShim, "WhenAny", TaskArray));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.span",
            MemberSignature.Method(Task, "WhenAny", ReadOnlySpanTask), Shim(TaskShim, "WhenAny", ReadOnlySpanTask));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.pair",
            MemberSignature.Method(Task, "WhenAny", Task, Task), Shim(TaskShim, "WhenAny", Task, Task));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.enumerable",
            MemberSignature.Method(Task, "WhenAny", IEnumerableTask), Shim(TaskShim, "WhenAny", IEnumerableTask));

        // ---- Generic combinators: WhenAll<TResult> / WhenAny<TResult> overloads -> controlled equivalents.
        // Each call site is a GenericInstanceMethod, so the call-site pass carries its TResult argument onto
        // the controlled generic shim; the parameter signatures use Cecil's positional `!!0` rendering.
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenall.generic.array",
            MemberSignature.Method(Task, "WhenAll", TaskTArray), Shim(TaskShim, "WhenAll", TaskTArrayDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenall.generic.span",
            MemberSignature.Method(Task, "WhenAll", ReadOnlySpanTaskT), Shim(TaskShim, "WhenAll", ReadOnlySpanTaskTDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenall.generic.enumerable",
            MemberSignature.Method(Task, "WhenAll", IEnumerableTaskT), Shim(TaskShim, "WhenAll", IEnumerableTaskTDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.generic.array",
            MemberSignature.Method(Task, "WhenAny", TaskTArray), Shim(TaskShim, "WhenAny", TaskTArrayDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.generic.span",
            MemberSignature.Method(Task, "WhenAny", ReadOnlySpanTaskT), Shim(TaskShim, "WhenAny", ReadOnlySpanTaskTDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.generic.pair",
            MemberSignature.Method(Task, "WhenAny", TaskT, TaskT), Shim(TaskShim, "WhenAny", TaskTDecl, TaskTDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.whenany.generic.enumerable",
            MemberSignature.Method(Task, "WhenAny", IEnumerableTaskT), Shim(TaskShim, "WhenAny", IEnumerableTaskTDecl));

        // ---- WhenEach: the BCL's async iterator awaits a source which runs its continuations on the thread pool; the
        // controlled sequence is observed through the coordinator instead. ----
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.wheneach.array",
            MemberSignature.Method(Task, "WhenEach", TaskArray), Shim(TaskShim, "WhenEach", TaskArray));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.wheneach.span",
            MemberSignature.Method(Task, "WhenEach", ReadOnlySpanTask), Shim(TaskShim, "WhenEach", ReadOnlySpanTask));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.wheneach.enumerable",
            MemberSignature.Method(Task, "WhenEach", IEnumerableTask), Shim(TaskShim, "WhenEach", IEnumerableTask));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.wheneach.generic.array",
            MemberSignature.Method(Task, "WhenEach", TaskTArray), Shim(TaskShim, "WhenEach", TaskTArrayDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.wheneach.generic.span",
            MemberSignature.Method(Task, "WhenEach", ReadOnlySpanTaskT), Shim(TaskShim, "WhenEach", ReadOnlySpanTaskTDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.wheneach.generic.enumerable",
            MemberSignature.Method(Task, "WhenEach", IEnumerableTaskT), Shim(TaskShim, "WhenEach", IEnumerableTaskTDecl));

        // ---- Task extension methods: TaskExtensions.Unwrap -> controlled equivalents. Unwrap is a static
        // extension whose receiver is the first parameter, so this is a direct static-to-static redirect; the
        // unwrapped proxy completes on the logical thread, so delegating to the real API stays deterministic.
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.unwrap",
            MemberSignature.Method(TaskExtensionsType, "Unwrap", TaskOfTask), Shim(TaskShim, "Unwrap", TaskOfTask));
        TaskRule(builder, BuiltInRuleFamily.TaskCombinators, "clockwork.tasks.unwrap.generic",
            MemberSignature.Method(TaskExtensionsType, "Unwrap", TaskOfTaskT), Shim(TaskShim, "Unwrap", TaskOfTaskTDecl));

        // ---- Synchronization: blocking waits -> controlled waits that pump the deterministic loop ----
        // The receiver of the instance Wait() is already on the IL stack, so a redirect to the static
        // shim taking the antecedent as its first parameter is stack-balanced.
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.wait.instance",
            MemberSignature.Method(Task, "Wait"), Shim(TaskShim, "Wait", Task));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitall.array",
            MemberSignature.Method(Task, "WaitAll", TaskArray), Shim(TaskShim, "WaitAll", TaskArray));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitany.array",
            MemberSignature.Method(Task, "WaitAny", TaskArray), Shim(TaskShim, "WaitAny", TaskArray));

        // The timed and cancellable waits pump the loop until the tasks complete, the token is cancelled, or a virtual
        // deadline elapses, then let the BCL compute the outcome from the completed tasks.
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.wait.milliseconds",
            MemberSignature.Method(Task, "Wait", Int32), Shim(TaskShim, "Wait", Task, Int32));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.wait.milliseconds.cancellationtoken",
            MemberSignature.Method(Task, "Wait", Int32, CancellationToken), Shim(TaskShim, "Wait", Task, Int32, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.wait.cancellationtoken",
            MemberSignature.Method(Task, "Wait", CancellationToken), Shim(TaskShim, "Wait", Task, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.wait.timespan",
            MemberSignature.Method(Task, "Wait", TimeSpan), Shim(TaskShim, "Wait", Task, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.wait.timespan.cancellationtoken",
            MemberSignature.Method(Task, "Wait", TimeSpan, CancellationToken), Shim(TaskShim, "Wait", Task, TimeSpan, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitall.array.milliseconds",
            MemberSignature.Method(Task, "WaitAll", TaskArray, Int32), Shim(TaskShim, "WaitAll", TaskArray, Int32));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitall.array.milliseconds.cancellationtoken",
            MemberSignature.Method(Task, "WaitAll", TaskArray, Int32, CancellationToken), Shim(TaskShim, "WaitAll", TaskArray, Int32, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitall.array.cancellationtoken",
            MemberSignature.Method(Task, "WaitAll", TaskArray, CancellationToken), Shim(TaskShim, "WaitAll", TaskArray, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitall.array.timespan",
            MemberSignature.Method(Task, "WaitAll", TaskArray, TimeSpan), Shim(TaskShim, "WaitAll", TaskArray, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitall.enumerable.cancellationtoken",
            MemberSignature.Method(Task, "WaitAll", IEnumerableTask, CancellationToken), Shim(TaskShim, "WaitAll", IEnumerableTask, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitall.span",
            MemberSignature.Method(Task, "WaitAll", ReadOnlySpanTask), Shim(TaskShim, "WaitAll", ReadOnlySpanTask));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitany.array.milliseconds",
            MemberSignature.Method(Task, "WaitAny", TaskArray, Int32), Shim(TaskShim, "WaitAny", TaskArray, Int32));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitany.array.milliseconds.cancellationtoken",
            MemberSignature.Method(Task, "WaitAny", TaskArray, Int32, CancellationToken), Shim(TaskShim, "WaitAny", TaskArray, Int32, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitany.array.cancellationtoken",
            MemberSignature.Method(Task, "WaitAny", TaskArray, CancellationToken), Shim(TaskShim, "WaitAny", TaskArray, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.waitany.array.timespan",
            MemberSignature.Method(Task, "WaitAny", TaskArray, TimeSpan), Shim(TaskShim, "WaitAny", TaskArray, TimeSpan));

        // Blocking Task<TResult>.Result getter -> controlled drain. The getter is a non-generic member on
        // the closed generic Task`1<T>, so the call-site pass binds the shim's TResult from the receiver's
        // declaring type; the drain pumps the loop until the task completes instead of blocking a thread.
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.result.generic",
            MemberSignature.Method("System.Threading.Tasks.Task`1", "get_Result"), Shim(TaskShim, "Result", TaskTDecl));

        // Blocking ValueTask<TResult>.Result getter -> the same controlled drain. The receiver is a value type, so the
        // shim takes it by reference, as the call pushes its address.
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.valuetask.result.generic",
            MemberSignature.Method(ValueTaskTType, "get_Result"), Shim(ValueTaskShim, "Result", ValueTaskTRefDecl));

        // Blocking TaskAsyncEnumerableExtensions.ToBlockingEnumerable -> a controlled enumerable that drains the loop
        // until each MoveNextAsync and the final DisposeAsync complete, instead of blocking the logical thread that
        // would complete them.
        TaskRule(builder, BuiltInRuleFamily.TaskSynchronization, "clockwork.tasks.asyncenumerable.toblockingenumerable",
            MemberSignature.Method(TaskAsyncEnumerableExtensionsType, "ToBlockingEnumerable", AsyncEnumerableT, CancellationToken),
            Shim(AsyncEnumerableShim, "ToBlockingEnumerable", AsyncEnumerableTDecl, CancellationToken));

        // ---- Continuations: every Task and Task<TResult> ContinueWith overload -> a genuine BCL continuation of a
        // private gate which the coordinator completes once it observes the antecedent complete, so the BCL registers
        // nothing on the antecedent. Each shape has the plain, CancellationToken, TaskScheduler, TaskContinuationOptions,
        // and full (CancellationToken, TaskContinuationOptions, TaskScheduler) overloads. A Task<TResult> overload is a
        // member of the closed generic type, so the shim's TResult binds from the receiver and a method type argument
        // (TNewResult) follows it. ----
        ContinueWithRules(builder, "clockwork.tasks.continuewith.action", Task, Task, ActionOfTask, ActionOfTask, hasState: false);
        ContinueWithRules(builder, "clockwork.tasks.continuewith.action.state", Task, Task, ActionOfTaskAndState, ActionOfTaskAndState, hasState: true);
        ContinueWithRules(builder, "clockwork.tasks.continuewith.func", Task, Task, FuncOfTaskAndResult, FuncOfTaskAndResultDecl, hasState: false);
        ContinueWithRules(builder, "clockwork.tasks.continuewith.func.state", Task, Task, FuncOfTaskStateAndResult, FuncOfTaskStateAndResultDecl, hasState: true);
        ContinueWithRules(builder, "clockwork.tasks.continuewith.generic.action", TaskTType, TaskTDecl, ActionOfTaskTVar, ActionOfTaskTDecl, hasState: false);
        ContinueWithRules(builder, "clockwork.tasks.continuewith.generic.action.state", TaskTType, TaskTDecl, ActionOfTaskTAndStateVar, ActionOfTaskTAndStateDecl, hasState: true);
        ContinueWithRules(builder, "clockwork.tasks.continuewith.generic.func", TaskTType, TaskTDecl, FuncOfTaskTAndNewResult, FuncOfTaskTAndNewResultDecl, hasState: false);
        ContinueWithRules(builder, "clockwork.tasks.continuewith.generic.func.state", TaskTType, TaskTDecl, FuncOfTaskTStateAndNewResult, FuncOfTaskTStateAndNewResultDecl, hasState: true);

        // ---- Completion sources: the real BCL TaskCompletionSource and TaskCompletionSource<TResult> are kept, with
        // their real options. Calls to their non-virtual completion members redirect to shims which complete the
        // source through the same member and publish a race-detection release edge on its task. ----
        CompletionSourceRules(builder, "clockwork.tasks.completionsource", TaskCompletionSourceType, TaskCompletionSourceType, [], [], Task, Task);
        CompletionSourceRules(builder, "clockwork.tasks.completionsource.generic", TaskCompletionSourceTType, TaskCompletionSourceTDecl, [TypeArg0], [TResultDecl], TaskTypeArg0, TaskTDecl);

        // ManualResetValueTaskSourceCore<TResult>'s completion members -> shims which complete the core as the completion
        // shims complete a source, so a continuation which the core runs inline (or a task over it which the BCL completes)
        // runs on the logical thread. The receiver is a value type, usually a field, so the shim takes it by reference.
        TaskRule(builder, BuiltInRuleFamily.TaskCompletionSource, "clockwork.tasks.valuetasksourcecore.setresult",
            MemberSignature.Method(ValueTaskSourceCoreType, "SetResult", TypeArg0),
            Shim(ValueTaskSourceCoreShim, "SetResult", ValueTaskSourceCoreRefDecl, TResultDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskCompletionSource, "clockwork.tasks.valuetasksourcecore.setexception",
            MemberSignature.Method(ValueTaskSourceCoreType, "SetException", ExceptionTypeName),
            Shim(ValueTaskSourceCoreShim, "SetException", ValueTaskSourceCoreRefDecl, ExceptionTypeName));

        // The constructors which take creation options redirect to factories which reject AttachedToParent inside a
        // simulation, as the Task constructors do: Clockwork's controlled tasks do not model parent and child tasks.
        // The generic source's factories bind TResult from the constructed type.
        foreach ((string type, string shim, string id) in (ReadOnlySpan<(string, string, string)>)
            [
                (TaskCompletionSourceType, "Create", "clockwork.tasks.completionsource.ctor"),
                (TaskCompletionSourceTType, "CreateGeneric", "clockwork.tasks.completionsource.generic.ctor"),
            ])
        {
            builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.TaskCompletionSource, RewriteRule.RedirectNewObj(
                id + ".options",
                MemberSignature.Constructor(type, TaskCreationOptions),
                Shim(TaskCompletionSourceShim, shim, TaskCreationOptions))));
            builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.TaskCompletionSource, RewriteRule.RedirectNewObj(
                id + ".state.options",
                MemberSignature.Constructor(type, ObjectType, TaskCreationOptions),
                Shim(TaskCompletionSourceShim, shim, ObjectType, TaskCreationOptions))));
        }

        // ---- Virtual-time task delays and asynchronous timeout races. ----
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.delay.milliseconds",
            MemberSignature.Method(Task, "Delay", Int32), Shim(TaskShim, "Delay", Int32));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.delay.timespan",
            MemberSignature.Method(Task, "Delay", TimeSpan), Shim(TaskShim, "Delay", TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.delay.milliseconds.cancellationtoken",
            MemberSignature.Method(Task, "Delay", Int32, CancellationToken), Shim(TaskShim, "Delay", Int32, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.delay.timespan.cancellationtoken",
            MemberSignature.Method(Task, "Delay", TimeSpan, CancellationToken), Shim(TaskShim, "Delay", TimeSpan, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.delay.timespan.timeprovider",
            MemberSignature.Method(Task, "Delay", TimeSpan, TimeProvider), Shim(TaskShim, "Delay", TimeSpan, TimeProvider));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.delay.timespan.timeprovider.cancellationtoken",
            MemberSignature.Method(Task, "Delay", TimeSpan, TimeProvider, CancellationToken),
            Shim(TaskShim, "Delay", TimeSpan, TimeProvider, CancellationToken));

        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.waitasync.cancellationtoken",
            MemberSignature.Method(Task, "WaitAsync", CancellationToken), Shim(TaskShim, "WaitAsync", Task, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.waitasync.timespan",
            MemberSignature.Method(Task, "WaitAsync", TimeSpan), Shim(TaskShim, "WaitAsync", Task, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.waitasync.timespan.timeprovider",
            MemberSignature.Method(Task, "WaitAsync", TimeSpan, TimeProvider), Shim(TaskShim, "WaitAsync", Task, TimeSpan, TimeProvider));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.waitasync.timespan.cancellationtoken",
            MemberSignature.Method(Task, "WaitAsync", TimeSpan, CancellationToken),
            Shim(TaskShim, "WaitAsync", Task, TimeSpan, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.waitasync.timespan.timeprovider.cancellationtoken",
            MemberSignature.Method(Task, "WaitAsync", TimeSpan, TimeProvider, CancellationToken),
            Shim(TaskShim, "WaitAsync", Task, TimeSpan, TimeProvider, CancellationToken));

        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.generic.waitasync.cancellationtoken",
            MemberSignature.Method(TaskTType, "WaitAsync", CancellationToken),
            Shim(TaskShim, "WaitAsync", TaskTDecl, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.generic.waitasync.timespan",
            MemberSignature.Method(TaskTType, "WaitAsync", TimeSpan), Shim(TaskShim, "WaitAsync", TaskTDecl, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.generic.waitasync.timespan.timeprovider",
            MemberSignature.Method(TaskTType, "WaitAsync", TimeSpan, TimeProvider),
            Shim(TaskShim, "WaitAsync", TaskTDecl, TimeSpan, TimeProvider));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.generic.waitasync.timespan.cancellationtoken",
            MemberSignature.Method(TaskTType, "WaitAsync", TimeSpan, CancellationToken),
            Shim(TaskShim, "WaitAsync", TaskTDecl, TimeSpan, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskTime, "clockwork.tasks.generic.waitasync.timespan.timeprovider.cancellationtoken",
            MemberSignature.Method(TaskTType, "WaitAsync", TimeSpan, TimeProvider, CancellationToken),
            Shim(TaskShim, "WaitAsync", TaskTDecl, TimeSpan, TimeProvider, CancellationToken));

        // ---- Scheduling: Task.Run controlled. The body is queued as a fresh controlled
        // operation on the coordinator instead of a physical thread-pool thread. The generic overloads are
        // GenericInstanceMethods (`!!0` target, `TResult` replacement); Func<Task>/Func<Task<TResult>>
        // carry the unwrap overloads. Each has a with- and without-CancellationToken form. ----
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.action",
            MemberSignature.Method(Task, "Run", Action), Shim(TaskShim, "Run", Action));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.action.cancellationtoken",
            MemberSignature.Method(Task, "Run", Action, CancellationToken), Shim(TaskShim, "Run", Action, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.func",
            MemberSignature.Method(Task, "Run", FuncOfMethodResult), Shim(TaskShim, "Run", FuncOfResultDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.func.cancellationtoken",
            MemberSignature.Method(Task, "Run", FuncOfMethodResult, CancellationToken), Shim(TaskShim, "Run", FuncOfResultDecl, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.func.task",
            MemberSignature.Method(Task, "Run", FuncOfTask), Shim(TaskShim, "Run", FuncOfTask));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.func.task.cancellationtoken",
            MemberSignature.Method(Task, "Run", FuncOfTask, CancellationToken), Shim(TaskShim, "Run", FuncOfTask, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.func.task.generic",
            MemberSignature.Method(Task, "Run", FuncOfTaskResult), Shim(TaskShim, "Run", FuncOfTaskResultDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.run.func.task.generic.cancellationtoken",
            MemberSignature.Method(Task, "Run", FuncOfTaskResult, CancellationToken), Shim(TaskShim, "Run", FuncOfTaskResultDecl, CancellationToken));

        // A task constructed with `new Task(...)` starts, or runs inline, on the coordinator's TaskScheduler, which keeps
        // the BCL's validation and status transitions.
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.start",
            MemberSignature.Method(Task, "Start"), Shim(TaskShim, "Start", Task));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.start.scheduler",
            MemberSignature.Method(Task, "Start", TaskSchedulerType), Shim(TaskShim, "Start", Task, TaskSchedulerType));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.runsynchronously",
            MemberSignature.Method(Task, "RunSynchronously"), Shim(TaskShim, "RunSynchronously", Task));
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.runsynchronously.scheduler",
            MemberSignature.Method(Task, "RunSynchronously", TaskSchedulerType), Shim(TaskShim, "RunSynchronously", Task, TaskSchedulerType));

        // `new Task(...)`: the constructed task's delegate runs as the strand which executes it rather than as its
        // creator's, whose strand the BCL restores with the captured ExecutionContext, and AttachedToParent is rejected.
        AddTaskConstructorRules(builder);
        TaskRule(builder, BuiltInRuleFamily.TaskScheduling, "clockwork.tasks.creationoptions",
            MemberSignature.Method(Task, "get_CreationOptions"), Shim(TaskShim, "CreationOptions", Task));

        // ---- Async machinery: retarget the compiler-generated builder/awaiter types of an async state
        // machine onto controlled equivalents (member-aware SubstituteType), plus the Task.Yield redirect.
        Sub(builder, "clockwork.tasks.builder.task", BclBuilder, ControlledBuilder);
        Sub(builder, "clockwork.tasks.builder.task.generic", BclBuilderT, ControlledBuilderT);
        Sub(builder, "clockwork.tasks.awaiter.task", BclTaskAwaiter, ControlledTaskAwaiter);
        Sub(builder, "clockwork.tasks.awaiter.task.generic", BclTaskAwaiterT, ControlledTaskAwaiterT);
        Sub(builder, "clockwork.tasks.configured.awaitable", BclConfigured, ControlledConfigured);
        Sub(builder, "clockwork.tasks.configured.awaitable.generic", BclConfiguredT, ControlledConfiguredT);
        Sub(builder, "clockwork.tasks.configured.awaiter", BclConfiguredAwaiter, ControlledConfiguredAwaiter);
        Sub(builder, "clockwork.tasks.configured.awaiter.generic", BclConfiguredAwaiterT, ControlledConfiguredAwaiterT);
        Sub(builder, "clockwork.tasks.yield.awaitable", BclYieldAwaitable, ControlledYieldAwaitable);
        Sub(builder, "clockwork.tasks.yield.awaiter", BclYieldAwaiter, ControlledYieldAwaiter);
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.AsyncMachinery, RewriteRule.RedirectCall(
            "clockwork.tasks.yield.call",
            MemberSignature.Method(Task, "Yield"),
            Shim(TaskShim, "Yield"))));

        // Runtime-async intrinsics: a method compiled with runtime async awaits a configured awaitable through
        // AsyncHelpers.Await, which resumes it through the runtime's own continuation machinery rather than the
        // awaitable, so it cannot be controlled. The configured awaitable is already substituted at the call site, so
        // the call redirects to a shim taking the substitute that rejects it by name, rather than failing the rewrite
        // as a reference whose substituted signature the framework does not declare.
        RejectedRule(builder, BuiltInRuleFamily.AsyncMachinery, "clockwork.tasks.asynchelpers.await.configured",
            MemberSignature.Method(AsyncHelpersType, "Await", BclConfigured), Shim(AsyncHelpersShim, "Await", ControlledConfigured));
        RejectedRule(builder, BuiltInRuleFamily.AsyncMachinery, "clockwork.tasks.asynchelpers.await.configured.generic",
            MemberSignature.Method(AsyncHelpersType, "Await", BclConfiguredTArg), Shim(AsyncHelpersShim, "Await", ControlledConfiguredTDecl));

        // An async Main returns through AsyncHelpers.HandleAsyncEntryPoint, which waits for its task with
        // task.GetAwaiter().GetResult(); the shim waits through the controlled awaiter, which pumps the coordinator.
        TaskRule(builder, BuiltInRuleFamily.AsyncMachinery, "clockwork.tasks.asynchelpers.handleasyncentrypoint",
            MemberSignature.Method(AsyncHelpersType, "HandleAsyncEntryPoint", Task), Shim(AsyncHelpersShim, "HandleAsyncEntryPoint", Task));
        TaskRule(builder, BuiltInRuleFamily.AsyncMachinery, "clockwork.tasks.asynchelpers.handleasyncentrypoint.int32",
            MemberSignature.Method(AsyncHelpersType, "HandleAsyncEntryPoint", TaskOfInt32), Shim(AsyncHelpersShim, "HandleAsyncEntryPoint", TaskOfInt32));

        // ---- ValueTask machinery: retarget the compiler-generated builder/awaiter types of an
        // async ValueTask / async ValueTask<T> state machine onto controlled equivalents. The awaiter
        // source calls (ValueTask.GetAwaiter / ValueTask.ConfigureAwait) are recognised by the
        // member-aware pass, which is why no explicit call redirect is needed for them.
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.builder.valuetask", BclValueBuilder, ControlledValueBuilder);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.builder.valuetask.generic", BclValueBuilderT, ControlledValueBuilderT);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.awaiter.valuetask", BclValueAwaiter, ControlledValueAwaiter);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.awaiter.valuetask.generic", BclValueAwaiterT, ControlledValueAwaiterT);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.configured.valuetask.awaitable", BclConfiguredValue, ControlledConfiguredValue);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.configured.valuetask.awaitable.generic", BclConfiguredValueT, ControlledConfiguredValueT);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.configured.valuetask.awaiter", BclConfiguredValueAwaiter, ControlledConfiguredValueAwaiter);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.configured.valuetask.awaiter.generic", BclConfiguredValueAwaiterT, ControlledConfiguredValueAwaiterT);
        RejectedRule(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.asynchelpers.await.configured.valuetask",
            MemberSignature.Method(AsyncHelpersType, "Await", BclConfiguredValue), Shim(AsyncHelpersShim, "Await", ControlledConfiguredValue));
        RejectedRule(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.asynchelpers.await.configured.valuetask.generic",
            MemberSignature.Method(AsyncHelpersType, "Await", BclConfiguredValueTArg), Shim(AsyncHelpersShim, "Await", ControlledConfiguredValueTDecl));

        // The configured async disposable and enumerable that `await using` and `await foreach` consume return
        // configured value-task awaitables from BCL members, so they are substituted together with them;
        // otherwise those member references would name BCL methods returning the controlled awaitables, which
        // do not exist. The extension methods that produce them redirect to factories for the controlled types.
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.configured.asyncdisposable", BclConfiguredAsyncDisposable, ControlledConfiguredAsyncDisposable);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.configured.asyncenumerable", BclConfiguredAsyncEnumerable, ControlledConfiguredAsyncEnumerable);
        Sub(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.configured.asyncenumerator", BclConfiguredAsyncEnumerator, ControlledConfiguredAsyncEnumerator);
        TaskRule(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.asyncdisposable.configureawait",
            MemberSignature.Method(TaskAsyncEnumerableExtensionsType, "ConfigureAwait", AsyncDisposableType, Boolean),
            Shim(AsyncEnumerableShim, "ConfigureAwait", AsyncDisposableType, Boolean));
        TaskRule(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.asyncenumerable.configureawait",
            MemberSignature.Method(TaskAsyncEnumerableExtensionsType, "ConfigureAwait", AsyncEnumerableT, Boolean),
            Shim(AsyncEnumerableShim, "ConfigureAwait", AsyncEnumerableTDecl, Boolean));
        TaskRule(builder, BuiltInRuleFamily.ValueTaskMachinery, "clockwork.tasks.asyncenumerable.withcancellation",
            MemberSignature.Method(TaskAsyncEnumerableExtensionsType, "WithCancellation", AsyncEnumerableT, CancellationToken),
            Shim(AsyncEnumerableShim, "WithCancellation", AsyncEnumerableTDecl, CancellationToken));

        // ---- ValueTask conversions: AsTask and Preserve of a value task backed by a pending IValueTaskSource register a
        // BCL completion callback with the source, which a source running continuations asynchronously invokes on the
        // thread pool. The shims poll the source through the coordinator instead. The receiver is a value type, so each
        // shim takes it by reference, as the call pushes its address. ----
        TaskRule(builder, BuiltInRuleFamily.ValueTaskConversions, "clockwork.tasks.valuetask.astask",
            MemberSignature.Method(ValueTaskType, "AsTask"), Shim(ValueTaskShim, "AsTask", ValueTaskRef));
        TaskRule(builder, BuiltInRuleFamily.ValueTaskConversions, "clockwork.tasks.valuetask.preserve",
            MemberSignature.Method(ValueTaskType, "Preserve"), Shim(ValueTaskShim, "Preserve", ValueTaskRef));
        TaskRule(builder, BuiltInRuleFamily.ValueTaskConversions, "clockwork.tasks.valuetask.generic.astask",
            MemberSignature.Method(ValueTaskTType, "AsTask"), Shim(ValueTaskShim, "AsTask", ValueTaskTRefDecl));
        TaskRule(builder, BuiltInRuleFamily.ValueTaskConversions, "clockwork.tasks.valuetask.generic.preserve",
            MemberSignature.Method(ValueTaskTType, "Preserve"), Shim(ValueTaskShim, "Preserve", ValueTaskTRefDecl));

        // ---- TaskFactory scheduling: StartNew offloads onto a task scheduler (the thread pool by
        // default). The controlled runtime handles it by queuing the delegate body as a fresh controlled operation on
        // the coordinator (like Task.Run), honouring the factory's/call's cancellation token; the
        // AttachedToParent creation option is rejected at runtime. Task.Factory.StartNew(Func<TResult>)
        // is a generic method (`!!0`); TaskFactory`1's StartNew(Func<TResult>) is a non-generic method
        // over the type parameter (`!0`). The inventory mirrors all 24 .NET 11 / Coyote StartNew signatures:
        // plain, CancellationToken, TaskCreationOptions, full scheduler, and state-carrying forms. ----
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action",
            MemberSignature.Method(TaskFactoryType, "StartNew", Action),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, Action));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action.cancellationtoken",
            MemberSignature.Method(TaskFactoryType, "StartNew", Action, CancellationToken),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, Action, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action.options",
            MemberSignature.Method(TaskFactoryType, "StartNew", Action, TaskCreationOptions),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, Action, TaskCreationOptions));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action.scheduler",
            MemberSignature.Method(TaskFactoryType, "StartNew", Action, CancellationToken, TaskCreationOptions, TaskSchedulerType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, Action, CancellationToken, TaskCreationOptions, TaskSchedulerType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action.state",
            MemberSignature.Method(TaskFactoryType, "StartNew", ActionOfObject, ObjectType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, ActionOfObject, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action.state.cancellationtoken",
            MemberSignature.Method(TaskFactoryType, "StartNew", ActionOfObject, ObjectType, CancellationToken),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, ActionOfObject, ObjectType, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action.state.options",
            MemberSignature.Method(TaskFactoryType, "StartNew", ActionOfObject, ObjectType, TaskCreationOptions),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, ActionOfObject, ObjectType, TaskCreationOptions));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.action.state.scheduler",
            MemberSignature.Method(TaskFactoryType, "StartNew", ActionOfObject, ObjectType, CancellationToken, TaskCreationOptions, TaskSchedulerType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, ActionOfObject, ObjectType, CancellationToken, TaskCreationOptions, TaskSchedulerType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfMethodResult),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfResultDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func.cancellationtoken",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfMethodResult, CancellationToken),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfResultDecl, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func.options",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfMethodResult, TaskCreationOptions),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfResultDecl, TaskCreationOptions));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func.scheduler",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfMethodResult, CancellationToken, TaskCreationOptions, TaskSchedulerType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfResultDecl, CancellationToken, TaskCreationOptions, TaskSchedulerType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func.state",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfObjectAndMethodResult, ObjectType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfObjectAndResultDecl, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func.state.cancellationtoken",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfObjectAndMethodResult, ObjectType, CancellationToken),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfObjectAndResultDecl, ObjectType, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func.state.options",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfObjectAndMethodResult, ObjectType, TaskCreationOptions),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfObjectAndResultDecl, ObjectType, TaskCreationOptions));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.startnew.func.state.scheduler",
            MemberSignature.Method(TaskFactoryType, "StartNew", FuncOfObjectAndMethodResult, ObjectType, CancellationToken, TaskCreationOptions, TaskSchedulerType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryType, FuncOfObjectAndResultDecl, ObjectType, CancellationToken, TaskCreationOptions, TaskSchedulerType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfTypeResult),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfResultDecl));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func.cancellationtoken",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfTypeResult, CancellationToken),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfResultDecl, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func.options",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfTypeResult, TaskCreationOptions),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfResultDecl, TaskCreationOptions));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func.scheduler",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfTypeResult, CancellationToken, TaskCreationOptions, TaskSchedulerType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfResultDecl, CancellationToken, TaskCreationOptions, TaskSchedulerType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func.state",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfObjectAndTypeResult, ObjectType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfObjectAndResultDecl, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func.state.cancellationtoken",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfObjectAndTypeResult, ObjectType, CancellationToken),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfObjectAndResultDecl, ObjectType, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func.state.options",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfObjectAndTypeResult, ObjectType, TaskCreationOptions),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfObjectAndResultDecl, ObjectType, TaskCreationOptions));
        TaskRule(builder, BuiltInRuleFamily.TaskFactory, "clockwork.tasks.factory.generic.startnew.func.state.scheduler",
            MemberSignature.Method(TaskFactoryTType, "StartNew", FuncOfObjectAndTypeResult, ObjectType, CancellationToken, TaskCreationOptions, TaskSchedulerType),
            Shim(TaskFactoryShim, "StartNew", TaskFactoryTOfResultDecl, FuncOfObjectAndResultDecl, ObjectType, CancellationToken, TaskCreationOptions, TaskSchedulerType));

        // ---- TaskFactory multi-task continuations: every TaskFactory and TaskFactory<TResult> ContinueWhenAll and
        // ContinueWhenAny overload -> a genuine BCL continuation of a private gate which the coordinator completes once it
        // observes the antecedents complete, so the BCL registers nothing on them. A TaskFactory<TResult> member binds the
        // shim's TResult from the receiver, then its own TAntecedentResult. ----
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenall.action", TaskFactoryType, TaskFactoryType, "ContinueWhenAll",
            TaskArray, TaskArray, ActionOfTaskArray, ActionOfTaskArray);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenall.antecedent.action", TaskFactoryType, TaskFactoryType, "ContinueWhenAll",
            TaskTArray, AntecedentArrayDecl, ActionOfTaskTArray, ActionOfAntecedentArrayDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenall.func", TaskFactoryType, TaskFactoryType, "ContinueWhenAll",
            TaskArray, TaskArray, FuncOfTaskArrayAndMethodResult, FuncOfTaskArrayAndResultDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenall.antecedent.func", TaskFactoryType, TaskFactoryType, "ContinueWhenAll",
            TaskTArray, AntecedentArrayDecl, FuncOfTaskTArrayAndSecondMethodResult, FuncOfAntecedentArrayAndResultDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenany.action", TaskFactoryType, TaskFactoryType, "ContinueWhenAny",
            TaskArray, TaskArray, ActionOfTask, ActionOfTask);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenany.antecedent.action", TaskFactoryType, TaskFactoryType, "ContinueWhenAny",
            TaskTArray, AntecedentArrayDecl, ActionOfTaskT, ActionOfAntecedentDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenany.func", TaskFactoryType, TaskFactoryType, "ContinueWhenAny",
            TaskArray, TaskArray, FuncOfTaskAndResult, FuncOfTaskAndResultDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.continuewhenany.antecedent.func", TaskFactoryType, TaskFactoryType, "ContinueWhenAny",
            TaskTArray, AntecedentArrayDecl, FuncOfTaskTAndSecondMethodResult, FuncOfAntecedentAndResultDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.generic.continuewhenall.func", TaskFactoryTType, TaskFactoryTOfResultDecl, "ContinueWhenAll",
            TaskArray, TaskArray, FuncOfTaskArrayAndTypeResult, FuncOfTaskArrayAndResultDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.generic.continuewhenall.antecedent.func", TaskFactoryTType, TaskFactoryTOfResultDecl, "ContinueWhenAll",
            TaskTArray, AntecedentArrayDecl, FuncOfTaskTArrayAndTypeResult, FuncOfAntecedentArrayAndResultDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.generic.continuewhenany.func", TaskFactoryTType, TaskFactoryTOfResultDecl, "ContinueWhenAny",
            TaskArray, TaskArray, FuncOfTaskAndTypeResult, FuncOfTaskAndResultDecl);
        MultiTaskContinuationRules(builder, "clockwork.tasks.factory.generic.continuewhenany.antecedent.func", TaskFactoryTType, TaskFactoryTOfResultDecl, "ContinueWhenAny",
            TaskTArray, AntecedentArrayDecl, FuncOfTaskTAndTypeResult, FuncOfAntecedentAndResultDecl);

        // ---- TaskFactory.FromAsync: the task completes when an IAsyncResult does, on a thread of its implementation's
        // choosing, so every overload of both factories is rejected at the call site. ----
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.TaskFactoryFromAsync, RewriteRule.InjectRejection(
            "clockwork.tasks.factory.fromasync",
            new MemberSignature(TaskFactoryType, "FromAsync"),
            Shim(TaskFactoryShim, "RejectFromAsync", String))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.TaskFactoryFromAsync, RewriteRule.InjectRejection(
            "clockwork.tasks.factory.generic.fromasync",
            new MemberSignature(TaskFactoryTType, "FromAsync"),
            Shim(TaskFactoryShim, "RejectFromAsync", String))));

        // ---- Thread: construction and Start/Join are controlled; Sleep/Yield/SpinWait yield cooperatively;
        // the OS-specific priority/apartment/interrupt surface is rejected precisely under simulation. Each
        // controlled thread is a real thread object whose body is queued as a fresh controlled operation, so
        // the logical identity surface (Name, ManagedThreadId, IsBackground) keeps working unchanged. ----
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.environment.currentmanagedthreadid",
            MemberSignature.Method(EnvironmentType, "get_CurrentManagedThreadId"),
            Shim(EnvironmentShim, "GetCurrentManagedThreadId"));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Thread, RewriteRule.RedirectNewObj(
            "clockwork.thread.ctor.threadstart",
            MemberSignature.Constructor(ThreadType, ThreadStart), Shim(ThreadShim, "Create", ThreadStart))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Thread, RewriteRule.RedirectNewObj(
            "clockwork.thread.ctor.threadstart.stacksize",
            MemberSignature.Constructor(ThreadType, ThreadStart, Int32), Shim(ThreadShim, "Create", ThreadStart, Int32))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Thread, RewriteRule.RedirectNewObj(
            "clockwork.thread.ctor.parameterized",
            MemberSignature.Constructor(ThreadType, ParameterizedThreadStart), Shim(ThreadShim, "Create", ParameterizedThreadStart))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Thread, RewriteRule.RedirectNewObj(
            "clockwork.thread.ctor.parameterized.stacksize",
            MemberSignature.Constructor(ThreadType, ParameterizedThreadStart, Int32), Shim(ThreadShim, "Create", ParameterizedThreadStart, Int32))));

        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.start",
            MemberSignature.Method(ThreadType, "Start"), Shim(ThreadShim, "Start", ThreadType));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.start.parameter",
            MemberSignature.Method(ThreadType, "Start", ObjectType), Shim(ThreadShim, "Start", ThreadType, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.unsafestart",
            MemberSignature.Method(ThreadType, "UnsafeStart"), Shim(ThreadShim, "UnsafeStart", ThreadType));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.unsafestart.parameter",
            MemberSignature.Method(ThreadType, "UnsafeStart", ObjectType), Shim(ThreadShim, "UnsafeStart", ThreadType, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.join",
            MemberSignature.Method(ThreadType, "Join"), Shim(ThreadShim, "Join", ThreadType));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.join.milliseconds",
            MemberSignature.Method(ThreadType, "Join", Int32), Shim(ThreadShim, "Join", ThreadType, Int32));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.join.timespan",
            MemberSignature.Method(ThreadType, "Join", TimeSpan), Shim(ThreadShim, "Join", ThreadType, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.sleep.milliseconds",
            MemberSignature.Method(ThreadType, "Sleep", Int32), Shim(ThreadShim, "Sleep", Int32));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.sleep.timespan",
            MemberSignature.Method(ThreadType, "Sleep", TimeSpan), Shim(ThreadShim, "Sleep", TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.spinwait",
            MemberSignature.Method(ThreadType, "SpinWait", Int32), Shim(ThreadShim, "SpinWait", Int32));
        TaskRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.yield",
            MemberSignature.Method(ThreadType, "Yield"), Shim(ThreadShim, "Yield"));

        RejectedRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.set_priority",
            MemberSignature.Method(ThreadType, "set_Priority", ThreadPriority), Shim(ThreadShim, "SetPriority", ThreadType, ThreadPriority));
        RejectedRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.interrupt",
            MemberSignature.Method(ThreadType, "Interrupt"), Shim(ThreadShim, "Interrupt", ThreadType));
        RejectedRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.setapartmentstate",
            MemberSignature.Method(ThreadType, "SetApartmentState", ApartmentState), Shim(ThreadShim, "SetApartmentState", ThreadType, ApartmentState));

        // The obsolete ApartmentState property setter tries to set the apartment state, as TrySetApartmentState does.
        RejectedRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.set_apartmentstate",
            MemberSignature.Method(ThreadType, "set_ApartmentState", ApartmentState), Shim(ThreadShim, "SetApartmentStateProperty", ThreadType, ApartmentState));
        RejectedRule(builder, BuiltInRuleFamily.Thread, "clockwork.thread.trysetapartmentstate",
            MemberSignature.Method(ThreadType, "TrySetApartmentState", ApartmentState), Shim(ThreadShim, "TrySetApartmentState", ThreadType, ApartmentState));

        // ---- ThreadPool: QueueUserWorkItem / UnsafeQueueUserWorkItem queue the callback as a fresh
        // controlled operation on the coordinator. ThreadPool methods are static, so the shim signatures
        // match the target parameters exactly (no receiver prepended). The safe variants flow the caller's
        // ExecutionContext; the unsafe variants do not. The generic overloads are GenericInstanceMethods
        // (`!!0` target, `TState` replacement). UnsafeQueueNativeOverlapped is rejected at the call site. ----
        TaskRule(builder, BuiltInRuleFamily.ThreadPool, "clockwork.threadpool.queue.waitcallback",
            MemberSignature.Method(ThreadPoolType, "QueueUserWorkItem", WaitCallback), Shim(ThreadPoolShim, "QueueUserWorkItem", WaitCallback));
        TaskRule(builder, BuiltInRuleFamily.ThreadPool, "clockwork.threadpool.queue.waitcallback.state",
            MemberSignature.Method(ThreadPoolType, "QueueUserWorkItem", WaitCallback, ObjectType), Shim(ThreadPoolShim, "QueueUserWorkItem", WaitCallback, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.ThreadPool, "clockwork.threadpool.queue.generic",
            MemberSignature.Method(ThreadPoolType, "QueueUserWorkItem", ActionOfTStateVar, TStateVar, Boolean),
            Shim(ThreadPoolShim, "QueueUserWorkItem", ActionOfTStateDecl, TStateDecl, Boolean));
        TaskRule(builder, BuiltInRuleFamily.ThreadPool, "clockwork.threadpool.unsafequeue.waitcallback.state",
            MemberSignature.Method(ThreadPoolType, "UnsafeQueueUserWorkItem", WaitCallback, ObjectType), Shim(ThreadPoolShim, "UnsafeQueueUserWorkItem", WaitCallback, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.ThreadPool, "clockwork.threadpool.unsafequeue.workitem",
            MemberSignature.Method(ThreadPoolType, "UnsafeQueueUserWorkItem", IThreadPoolWorkItem, Boolean), Shim(ThreadPoolShim, "UnsafeQueueUserWorkItem", IThreadPoolWorkItem, Boolean));
        TaskRule(builder, BuiltInRuleFamily.ThreadPool, "clockwork.threadpool.unsafequeue.generic",
            MemberSignature.Method(ThreadPoolType, "UnsafeQueueUserWorkItem", ActionOfTStateVar, TStateVar, Boolean),
            Shim(ThreadPoolShim, "UnsafeQueueUserWorkItem", ActionOfTStateDecl, TStateDecl, Boolean));

        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.ThreadPool, RewriteRule.InjectRejection(
            "clockwork.threadpool.unsafequeuenativeoverlapped",
            MemberSignature.Method(ThreadPoolType, "UnsafeQueueNativeOverlapped", NativeOverlappedPtr),
            Shim(ThreadPoolShim, "RejectNativeOverlapped", String))));

        // ---- Timer types and TimeProvider bridge. Whole-type substitutions ensure constructors,
        // properties, events, and instance methods cannot bypass the controlled timer implementations. ----
        Sub(builder, BuiltInRuleFamily.Timers, "clockwork.timer.threading.type", TimerType, ControlledTimerType);
        Sub(builder, BuiltInRuleFamily.Timers, "clockwork.timer.component.type", TimersTimerType, ControlledComponentTimerType);
        Sub(builder, BuiltInRuleFamily.Timers, "clockwork.timer.periodic.type", PeriodicTimerType, ControlledPeriodicTimerType);
        TaskRule(builder, BuiltInRuleFamily.Timers, "clockwork.timeprovider.system",
            MemberSignature.Method(TimeProvider, "get_System"), Shim(TimeProviderShim, "get_System"));
        TaskRule(builder, BuiltInRuleFamily.Timers, "clockwork.timeprovider.createtimer",
            MemberSignature.Method(TimeProvider, "CreateTimer", TimerCallbackType, ObjectType, TimeSpan, TimeSpan),
            Shim(TimeProviderShim, "CreateTimer", TimeProvider, TimerCallbackType, ObjectType, TimeSpan, TimeSpan));

        // ---- CancellationTokenSource timer construction and lifecycle. The object remains the BCL
        // identity; receiver-first shims own only the virtual timer registration. ----
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.CancellationTimers, RewriteRule.RedirectNewObj(
            "clockwork.cancellationtokensource.ctor.milliseconds",
            MemberSignature.Constructor(CancellationTokenSourceType, Int32),
            Shim(CancellationTokenSourceShim, "Create", Int32))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.CancellationTimers, RewriteRule.RedirectNewObj(
            "clockwork.cancellationtokensource.ctor.timespan",
            MemberSignature.Constructor(CancellationTokenSourceType, TimeSpan),
            Shim(CancellationTokenSourceShim, "Create", TimeSpan))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.CancellationTimers, RewriteRule.RedirectNewObj(
            "clockwork.cancellationtokensource.ctor.timespan.timeprovider",
            MemberSignature.Constructor(CancellationTokenSourceType, TimeSpan, TimeProvider),
            Shim(CancellationTokenSourceShim, "Create", TimeSpan, TimeProvider))));
        TaskRule(builder, BuiltInRuleFamily.CancellationTimers, "clockwork.cancellationtokensource.cancelafter.milliseconds",
            MemberSignature.Method(CancellationTokenSourceType, "CancelAfter", Int32),
            Shim(CancellationTokenSourceShim, "CancelAfter", CancellationTokenSourceType, Int32));
        TaskRule(builder, BuiltInRuleFamily.CancellationTimers, "clockwork.cancellationtokensource.cancelafter.timespan",
            MemberSignature.Method(CancellationTokenSourceType, "CancelAfter", TimeSpan),
            Shim(CancellationTokenSourceShim, "CancelAfter", CancellationTokenSourceType, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.CancellationTimers, "clockwork.cancellationtokensource.cancel",
            MemberSignature.Method(CancellationTokenSourceType, "Cancel"),
            Shim(CancellationTokenSourceShim, "Cancel", CancellationTokenSourceType));
        TaskRule(builder, BuiltInRuleFamily.CancellationTimers, "clockwork.cancellationtokensource.cancel.throw",
            MemberSignature.Method(CancellationTokenSourceType, "Cancel", Boolean),
            Shim(CancellationTokenSourceShim, "Cancel", CancellationTokenSourceType, Boolean));
        TaskRule(builder, BuiltInRuleFamily.CancellationTimers, "clockwork.cancellationtokensource.cancelasync",
            MemberSignature.Method(CancellationTokenSourceType, "CancelAsync"),
            Shim(CancellationTokenSourceShim, "CancelAsync", CancellationTokenSourceType));
        TaskRule(builder, BuiltInRuleFamily.CancellationTimers, "clockwork.cancellationtokensource.tryreset",
            MemberSignature.Method(CancellationTokenSourceType, "TryReset"),
            Shim(CancellationTokenSourceShim, "TryReset", CancellationTokenSourceType));
        TaskRule(builder, BuiltInRuleFamily.CancellationTimers, "clockwork.cancellationtokensource.dispose",
            MemberSignature.Method(CancellationTokenSourceType, "Dispose"),
            Shim(CancellationTokenSourceShim, "Dispose", CancellationTokenSourceType));

        // ---- Registered waits: RegisterWaitForSingleObject / UnsafeRegisterWaitForSingleObject
        // bind a WaitOrTimerCallback to a controlled event, which the coordinator now models. RegisteredWaitHandle
        // is retargeted by whole-type substitution so its locals/fields and the Unregister instance member remap
        // onto the controlled type; the eight static factories (four timeout overloads x safe/unsafe) are redirected
        // to shims that return the controlled handle. The safe family flows ExecutionContext; the unsafe family does
        // not. The static factory shim signatures match the BCL targets exactly (no receiver prepended). ----
        Sub(builder, BuiltInRuleFamily.ThreadPool, "clockwork.threadpool.registeredwaithandle.type",
            RegisteredWaitHandleType, ControlledRegisteredWaitHandleType);

        RegisterWaitRedirect(builder, "clockwork.threadpool.registerwait.uint32", "RegisterWaitForSingleObject", UInt32);
        RegisterWaitRedirect(builder, "clockwork.threadpool.registerwait.int32", "RegisterWaitForSingleObject", Int32);
        RegisterWaitRedirect(builder, "clockwork.threadpool.registerwait.int64", "RegisterWaitForSingleObject", Int64);
        RegisterWaitRedirect(builder, "clockwork.threadpool.registerwait.timespan", "RegisterWaitForSingleObject", TimeSpan);
        RegisterWaitRedirect(builder, "clockwork.threadpool.unsaferegisterwait.uint32", "UnsafeRegisterWaitForSingleObject", UInt32);
        RegisterWaitRedirect(builder, "clockwork.threadpool.unsaferegisterwait.int32", "UnsafeRegisterWaitForSingleObject", Int32);
        RegisterWaitRedirect(builder, "clockwork.threadpool.unsaferegisterwait.int64", "UnsafeRegisterWaitForSingleObject", Int64);
        RegisterWaitRedirect(builder, "clockwork.threadpool.unsaferegisterwait.timespan", "UnsafeRegisterWaitForSingleObject", TimeSpan);

        // ---- Parallel: every loop runs on a deterministic pool of controlled workers on the coordinator, which honours
        // MaxDegreeOfParallelism; the synchronous loops pump the coordinator until the pool completes, and the
        // asynchronous loops (ForEachAsync, ForAsync) return its completion. Parallel is static, so the shim signatures
        // match the target exactly. The generic overloads are GenericInstanceMethods (`!!0` target, `TSource`/`T`
        // replacement). The overloads whose body receives a ParallelLoopState (break/stop and thread-local TLocal) are
        // rejected at the call site. ----
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.invoke",
            MemberSignature.Method(ParallelType, "Invoke", ActionArray), Shim(ParallelShim, "Invoke", ActionArray));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.invoke.options",
            MemberSignature.Method(ParallelType, "Invoke", ParallelOptionsType, ActionArray), Shim(ParallelShim, "Invoke", ParallelOptionsType, ActionArray));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.for.int32",
            MemberSignature.Method(ParallelType, "For", Int32, Int32, ActionOfInt32), Shim(ParallelShim, "For", Int32, Int32, ActionOfInt32));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.for.int32.options",
            MemberSignature.Method(ParallelType, "For", Int32, Int32, ParallelOptionsType, ActionOfInt32), Shim(ParallelShim, "For", Int32, Int32, ParallelOptionsType, ActionOfInt32));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.for.int64",
            MemberSignature.Method(ParallelType, "For", Int64, Int64, ActionOfInt64), Shim(ParallelShim, "For", Int64, Int64, ActionOfInt64));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.for.int64.options",
            MemberSignature.Method(ParallelType, "For", Int64, Int64, ParallelOptionsType, ActionOfInt64), Shim(ParallelShim, "For", Int64, Int64, ParallelOptionsType, ActionOfInt64));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreach",
            MemberSignature.Method(ParallelType, "ForEach", IEnumerableOfTSourceVar, ActionOfTSourceVar),
            Shim(ParallelShim, "ForEach", IEnumerableOfTSourceDecl, ActionOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreach.options",
            MemberSignature.Method(ParallelType, "ForEach", IEnumerableOfTSourceVar, ParallelOptionsType, ActionOfTSourceVar),
            Shim(ParallelShim, "ForEach", IEnumerableOfTSourceDecl, ParallelOptionsType, ActionOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreach.partitioner",
            MemberSignature.Method(ParallelType, "ForEach", PartitionerOfTSourceVar, ActionOfTSourceVar),
            Shim(ParallelShim, "ForEach", PartitionerOfTSourceDecl, ActionOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreach.partitioner.options",
            MemberSignature.Method(ParallelType, "ForEach", PartitionerOfTSourceVar, ParallelOptionsType, ActionOfTSourceVar),
            Shim(ParallelShim, "ForEach", PartitionerOfTSourceDecl, ParallelOptionsType, ActionOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.forasync",
            MemberSignature.Method(ParallelType, "ForAsync", GenericArg0, GenericArg0, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForAsync", GenericTDecl, GenericTDecl, AsyncBodyOfTDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.forasync.cancellationtoken",
            MemberSignature.Method(ParallelType, "ForAsync", GenericArg0, GenericArg0, CancellationToken, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForAsync", GenericTDecl, GenericTDecl, CancellationToken, AsyncBodyOfTDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.forasync.options",
            MemberSignature.Method(ParallelType, "ForAsync", GenericArg0, GenericArg0, ParallelOptionsType, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForAsync", GenericTDecl, GenericTDecl, ParallelOptionsType, AsyncBodyOfTDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreachasync",
            MemberSignature.Method(ParallelType, "ForEachAsync", IEnumerableOfTSourceVar, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForEachAsync", IEnumerableOfTSourceDecl, AsyncBodyOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreachasync.cancellationtoken",
            MemberSignature.Method(ParallelType, "ForEachAsync", IEnumerableOfTSourceVar, CancellationToken, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForEachAsync", IEnumerableOfTSourceDecl, CancellationToken, AsyncBodyOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreachasync.options",
            MemberSignature.Method(ParallelType, "ForEachAsync", IEnumerableOfTSourceVar, ParallelOptionsType, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForEachAsync", IEnumerableOfTSourceDecl, ParallelOptionsType, AsyncBodyOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreachasync.asyncenumerable",
            MemberSignature.Method(ParallelType, "ForEachAsync", IAsyncEnumerableOfTSourceVar, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForEachAsync", IAsyncEnumerableOfTSourceDecl, AsyncBodyOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreachasync.asyncenumerable.cancellationtoken",
            MemberSignature.Method(ParallelType, "ForEachAsync", IAsyncEnumerableOfTSourceVar, CancellationToken, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForEachAsync", IAsyncEnumerableOfTSourceDecl, CancellationToken, AsyncBodyOfTSourceDecl));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.foreachasync.asyncenumerable.options",
            MemberSignature.Method(ParallelType, "ForEachAsync", IAsyncEnumerableOfTSourceVar, ParallelOptionsType, AsyncBodyOfTSourceVar),
            Shim(ParallelShim, "ForEachAsync", IAsyncEnumerableOfTSourceDecl, ParallelOptionsType, AsyncBodyOfTSourceDecl));

        // The simulated processor count, which the program reads as Environment.ProcessorCount and which sizes what the
        // BCL sizes by the processor count: the loops' default degree of parallelism and the range partitioners' range
        // size. These rules share the Parallel family so that one selection decides both the loops and the count they
        // are sized by. Partitioner.Create(IEnumerable) reads the processor count only to size a fill buffer which the
        // BCL never fills, so it stays a BCL call (UnrewrittenMembers).
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.environment.processorcount",
            MemberSignature.Method(EnvironmentType, "get_ProcessorCount"), Shim(EnvironmentShim, "GetProcessorCount"));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.partitioner.create.int32",
            MemberSignature.Method(PartitionerType, "Create", Int32, Int32), Shim(PartitionerShim, "Create", Int32, Int32));
        TaskRule(builder, BuiltInRuleFamily.Parallel, "clockwork.parallel.partitioner.create.int64",
            MemberSignature.Method(PartitionerType, "Create", Int64, Int64), Shim(PartitionerShim, "Create", Int64, Int64));

        ParallelRejection(builder, "clockwork.parallel.for.int32.loopstate", "For", Int32, Int32, ActionOfInt32LoopState);
        ParallelRejection(builder, "clockwork.parallel.for.int32.loopstate.options", "For", Int32, Int32, ParallelOptionsType, ActionOfInt32LoopState);
        ParallelRejection(builder, "clockwork.parallel.for.int64.loopstate", "For", Int64, Int64, ActionOfInt64LoopState);
        ParallelRejection(builder, "clockwork.parallel.for.int64.loopstate.options", "For", Int64, Int64, ParallelOptionsType, ActionOfInt64LoopState);
        ParallelRejection(builder, "clockwork.parallel.foreach.loopstate", "ForEach", IEnumerableOfTSourceVar, ActionOfTSourceLoopStateVar);
        ParallelRejection(builder, "clockwork.parallel.foreach.loopstate.index", "ForEach", IEnumerableOfTSourceVar, ActionOfTSourceLoopStateInt64Var);
        ParallelRejection(builder, "clockwork.parallel.foreach.loopstate.options", "ForEach", IEnumerableOfTSourceVar, ParallelOptionsType, ActionOfTSourceLoopStateVar);
        ParallelRejection(builder, "clockwork.parallel.foreach.loopstate.index.options", "ForEach", IEnumerableOfTSourceVar, ParallelOptionsType, ActionOfTSourceLoopStateInt64Var);
        ParallelRejection(builder, "clockwork.parallel.foreach.partitioner.loopstate", "ForEach", PartitionerOfTSourceVar, ActionOfTSourceLoopStateVar);
        ParallelRejection(builder, "clockwork.parallel.foreach.partitioner.loopstate.options", "ForEach", PartitionerOfTSourceVar, ParallelOptionsType, ActionOfTSourceLoopStateVar);
        ParallelRejection(builder, "clockwork.parallel.foreach.orderablepartitioner.loopstate.index", "ForEach", OrderablePartitionerOfTSourceVar, ActionOfTSourceLoopStateInt64Var);
        ParallelRejection(builder, "clockwork.parallel.foreach.orderablepartitioner.loopstate.index.options", "ForEach", OrderablePartitionerOfTSourceVar, ParallelOptionsType, ActionOfTSourceLoopStateInt64Var);
        ParallelRejection(builder, "clockwork.parallel.for.int32.local", "For", Int32, Int32, ForLocalInit, ForInt32LocalBody, ForLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.for.int32.local.options", "For", Int32, Int32, ParallelOptionsType, ForLocalInit, ForInt32LocalBody, ForLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.for.int64.local", "For", Int64, Int64, ForLocalInit, ForInt64LocalBody, ForLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.for.int64.local.options", "For", Int64, Int64, ParallelOptionsType, ForLocalInit, ForInt64LocalBody, ForLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.local", "ForEach", IEnumerableOfTSourceVar, ForEachLocalInit, ForEachLocalBody, ForEachLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.local.options", "ForEach", IEnumerableOfTSourceVar, ParallelOptionsType, ForEachLocalInit, ForEachLocalBody, ForEachLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.local.index", "ForEach", IEnumerableOfTSourceVar, ForEachLocalInit, ForEachIndexedLocalBody, ForEachLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.local.index.options", "ForEach", IEnumerableOfTSourceVar, ParallelOptionsType, ForEachLocalInit, ForEachIndexedLocalBody, ForEachLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.partitioner.local", "ForEach", PartitionerOfTSourceVar, ForEachLocalInit, ForEachLocalBody, ForEachLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.partitioner.local.options", "ForEach", PartitionerOfTSourceVar, ParallelOptionsType, ForEachLocalInit, ForEachLocalBody, ForEachLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.orderablepartitioner.local.index", "ForEach", OrderablePartitionerOfTSourceVar, ForEachLocalInit, ForEachIndexedLocalBody, ForEachLocalFinally);
        ParallelRejection(builder, "clockwork.parallel.foreach.orderablepartitioner.local.index.options", "ForEach", OrderablePartitionerOfTSourceVar, ParallelOptionsType, ForEachLocalInit, ForEachIndexedLocalBody, ForEachLocalFinally);

        // ---- Channels: the ChannelReader<T>/ChannelWriter<T> members whose BCL implementations are async methods in the
        // channels assembly, whose awaits resume on the thread pool. Each is a virtual member of the open generic type
        // (`!0` target), redirected receiver first to a shim which calls an override as written and otherwise runs the
        // BCL's algorithm through controlled awaits. A base call from an override (a non-virtual `call`) runs the base
        // algorithm, since dispatching it would re-enter the override. ----
        ChannelRule(builder, "clockwork.channels.reader.readallasync", ChannelReaderType, "ReadAllAsync", ChannelReaderShim,
            [CancellationToken], [ChannelReaderTDecl, CancellationToken]);
        ChannelRule(builder, "clockwork.channels.reader.readasync", ChannelReaderType, "ReadAsync", ChannelReaderShim,
            [CancellationToken], [ChannelReaderTDecl, CancellationToken]);
        ChannelRule(builder, "clockwork.channels.writer.writeasync", ChannelWriterType, "WriteAsync", ChannelWriterShim,
            [TypeArg0, CancellationToken], [ChannelWriterTDecl, GenericTDecl, CancellationToken]);

        // ---- PLINQ: the ParallelEnumerable members which create a ParallelQuery. A query runs on TaskScheduler.Default
        // worker tasks and its caller blocks on framework waits, so each call site is rejected; every operator needs a
        // ParallelQuery, which rewritten code then cannot create. ----
        PlinqRejection(builder, "clockwork.plinq.asparallel.generic", "AsParallel", IEnumerableOfTSourceVar);
        PlinqRejection(builder, "clockwork.plinq.asparallel.nongeneric", "AsParallel", NonGenericIEnumerable);
        PlinqRejection(builder, "clockwork.plinq.asparallel.partitioner", "AsParallel", PartitionerOfTSourceVar);
        PlinqRejection(builder, "clockwork.plinq.range", "Range", Int32, Int32);
        PlinqRejection(builder, "clockwork.plinq.repeat", "Repeat", GenericArg0, Int32);
        PlinqRejection(builder, "clockwork.plinq.empty", "Empty");

        // ---- BlockingCollection<T>: the members which add, take, or complete adding. The BCL blocks a taker or a bounded
        // adder on framework semaphores and wait handles; each shim makes the BCL's own zero-timeout attempt and parks
        // the caller on the coordinator until it can succeed. ----
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.add", "Add", [TypeArg0], [GenericTDecl]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.add.token", "Add", [TypeArg0, CancellationToken], [GenericTDecl, CancellationToken]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.tryadd", "TryAdd", [TypeArg0], [GenericTDecl]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.tryadd.timespan", "TryAdd", [TypeArg0, TimeSpan], [GenericTDecl, TimeSpan]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.tryadd.milliseconds", "TryAdd", [TypeArg0, Int32], [GenericTDecl, Int32]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.tryadd.milliseconds.token", "TryAdd", [TypeArg0, Int32, CancellationToken], [GenericTDecl, Int32, CancellationToken]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.take", "Take", [], []);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.take.token", "Take", [CancellationToken], [CancellationToken]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.trytake", "TryTake", [TypeArg0Ref], [GenericTRefDecl]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.trytake.timespan", "TryTake", [TypeArg0Ref, TimeSpan], [GenericTRefDecl, TimeSpan]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.trytake.milliseconds", "TryTake", [TypeArg0Ref, Int32], [GenericTRefDecl, Int32]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.trytake.milliseconds.token", "TryTake", [TypeArg0Ref, Int32, CancellationToken], [GenericTRefDecl, Int32, CancellationToken]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.getconsumingenumerable", "GetConsumingEnumerable", [], []);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.getconsumingenumerable.token", "GetConsumingEnumerable", [CancellationToken], [CancellationToken]);
        BlockingCollectionInstanceRule(builder, "clockwork.blockingcollection.completeadding", "CompleteAdding", [], []);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.addtoany", "AddToAny", TypeArg0);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.addtoany.token", "AddToAny", TypeArg0, CancellationToken);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.tryaddtoany", "TryAddToAny", TypeArg0);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.tryaddtoany.timespan", "TryAddToAny", TypeArg0, TimeSpan);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.tryaddtoany.milliseconds", "TryAddToAny", TypeArg0, Int32);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.tryaddtoany.milliseconds.token", "TryAddToAny", TypeArg0, Int32, CancellationToken);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.takefromany", "TakeFromAny", TypeArg0Ref);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.takefromany.token", "TakeFromAny", TypeArg0Ref, CancellationToken);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.trytakefromany", "TryTakeFromAny", TypeArg0Ref);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.trytakefromany.timespan", "TryTakeFromAny", TypeArg0Ref, TimeSpan);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.trytakefromany.milliseconds", "TryTakeFromAny", TypeArg0Ref, Int32);
        BlockingCollectionStaticRule(builder, "clockwork.blockingcollection.trytakefromany.milliseconds.token", "TryTakeFromAny", TypeArg0Ref, Int32, CancellationToken);

        // ---- Async I/O: the asynchronous members of Stream, TextReader, and TextWriter whose base implementations run the
        // synchronous member on the thread pool, block on a semaphore or task, or await the other members through BCL
        // awaits. A virtual member calls an override as written and otherwise runs the base algorithm on the
        // coordinator; its base calls run the base algorithm. A non-virtual member runs the BCL's algorithm over the
        // controlled virtual members. ----
        AsyncIORule(builder, "clockwork.io.stream.readasync.array", StreamType, StreamShim, "ReadAsync", isVirtual: true, ByteArray, Int32, Int32, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.readasync.memory", StreamType, StreamShim, "ReadAsync", isVirtual: true, MemoryOfByte, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.writeasync.array", StreamType, StreamShim, "WriteAsync", isVirtual: true, ByteArray, Int32, Int32, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.writeasync.memory", StreamType, StreamShim, "WriteAsync", isVirtual: true, ReadOnlyMemoryOfByte, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.flushasync.token", StreamType, StreamShim, "FlushAsync", isVirtual: true, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.copytoasync.buffersize.token", StreamType, StreamShim, "CopyToAsync", isVirtual: true, StreamType, Int32, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.beginread", StreamType, StreamShim, "BeginRead", isVirtual: true, ByteArray, Int32, Int32, AsyncCallbackType, ObjectType);
        AsyncIORule(builder, "clockwork.io.stream.endread", StreamType, StreamShim, "EndRead", isVirtual: true, AsyncResultType);
        AsyncIORule(builder, "clockwork.io.stream.beginwrite", StreamType, StreamShim, "BeginWrite", isVirtual: true, ByteArray, Int32, Int32, AsyncCallbackType, ObjectType);
        AsyncIORule(builder, "clockwork.io.stream.endwrite", StreamType, StreamShim, "EndWrite", isVirtual: true, AsyncResultType);
        AsyncIORule(builder, "clockwork.io.stream.readasync.array.notoken", StreamType, StreamShim, "ReadAsync", isVirtual: false, ByteArray, Int32, Int32);
        AsyncIORule(builder, "clockwork.io.stream.writeasync.array.notoken", StreamType, StreamShim, "WriteAsync", isVirtual: false, ByteArray, Int32, Int32);
        AsyncIORule(builder, "clockwork.io.stream.flushasync", StreamType, StreamShim, "FlushAsync", isVirtual: false);
        AsyncIORule(builder, "clockwork.io.stream.copytoasync", StreamType, StreamShim, "CopyToAsync", isVirtual: false, StreamType);
        AsyncIORule(builder, "clockwork.io.stream.copytoasync.buffersize", StreamType, StreamShim, "CopyToAsync", isVirtual: false, StreamType, Int32);
        AsyncIORule(builder, "clockwork.io.stream.copytoasync.token", StreamType, StreamShim, "CopyToAsync", isVirtual: false, StreamType, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.readexactlyasync.memory", StreamType, StreamShim, "ReadExactlyAsync", isVirtual: false, MemoryOfByte, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.readexactlyasync.array", StreamType, StreamShim, "ReadExactlyAsync", isVirtual: false, ByteArray, Int32, Int32, CancellationToken);
        AsyncIORule(builder, "clockwork.io.stream.readatleastasync", StreamType, StreamShim, "ReadAtLeastAsync", isVirtual: false, MemoryOfByte, Int32, Boolean, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textreader.readlineasync", TextReaderType, TextReaderShim, "ReadLineAsync", isVirtual: true);
        AsyncIORule(builder, "clockwork.io.textreader.readlineasync.token", TextReaderType, TextReaderShim, "ReadLineAsync", isVirtual: true, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textreader.readtoendasync", TextReaderType, TextReaderShim, "ReadToEndAsync", isVirtual: true);
        AsyncIORule(builder, "clockwork.io.textreader.readtoendasync.token", TextReaderType, TextReaderShim, "ReadToEndAsync", isVirtual: true, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textreader.readasync.array", TextReaderType, TextReaderShim, "ReadAsync", isVirtual: true, CharArray, Int32, Int32);
        AsyncIORule(builder, "clockwork.io.textreader.readasync.memory", TextReaderType, TextReaderShim, "ReadAsync", isVirtual: true, MemoryOfChar, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textreader.readblockasync.array", TextReaderType, TextReaderShim, "ReadBlockAsync", isVirtual: true, CharArray, Int32, Int32);
        AsyncIORule(builder, "clockwork.io.textreader.readblockasync.memory", TextReaderType, TextReaderShim, "ReadBlockAsync", isVirtual: true, MemoryOfChar, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.char", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: true, CharType);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.rune", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: true, RuneType);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.string", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: true, String);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.stringbuilder", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: true, StringBuilderType, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.array", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: true, CharArray, Int32, Int32);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.memory", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: true, ReadOnlyMemoryOfChar, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.char", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: true, CharType);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.rune", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: true, RuneType);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.string", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: true, String);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.stringbuilder", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: true, StringBuilderType, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.array", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: true, CharArray, Int32, Int32);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.memory", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: true, ReadOnlyMemoryOfChar, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: true);
        AsyncIORule(builder, "clockwork.io.textwriter.flushasync", TextWriterType, TextWriterShim, "FlushAsync", isVirtual: true);
        AsyncIORule(builder, "clockwork.io.textwriter.flushasync.token", TextWriterType, TextWriterShim, "FlushAsync", isVirtual: true, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.string.token", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: false, String, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writeasync.chararray", TextWriterType, TextWriterShim, "WriteAsync", isVirtual: false, CharArray);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.string.token", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: false, String, CancellationToken);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.chararray", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: false, CharArray);
        AsyncIORule(builder, "clockwork.io.textwriter.writelineasync.token", TextWriterType, TextWriterShim, "WriteLineAsync", isVirtual: false, CancellationToken);

        // ---- Background work: the members which start a BackgroundWorker's DoWork, or a BackgroundService's ExecuteAsync,
        // on the thread pool (Task.Factory.StartNew on TaskScheduler.Default, and the real Task.Run). The work is the
        // application's own, and each type raises or stores it through private state a shim cannot reproduce, so each call
        // site is rejected. ----
        CallSiteRejection(builder, BuiltInRuleFamily.BackgroundWork, "clockwork.backgroundworker.runworkerasync",
            new MemberSignature(BackgroundWorkerType, "RunWorkerAsync"), BackgroundWorkerShim);
        CallSiteRejection(builder, BuiltInRuleFamily.BackgroundWork, "clockwork.backgroundservice.startasync",
            MemberSignature.Method(BackgroundServiceType, "StartAsync", CancellationToken), BackgroundServiceShim);

        // ---- TPL Dataflow: every block constructor (any overload, so a new one is rejected too) and the DataflowBlock helpers
        // which schedule work or wait. A block wires cancellation and completion propagation through continuations on
        // TaskScheduler.Default whatever its options' scheduler, and the helpers time out on real timers, so controlling
        // the options' scheduler would not be faithful; each call site is rejected. ----
        foreach ((string block, string id) in DataflowBlockTypes)
        {
            CallSiteRejection(builder, BuiltInRuleFamily.Dataflow, "clockwork.dataflow." + id + ".ctor",
                new MemberSignature(DataflowNamespace + block, ".ctor"), DataflowShim);
        }

        foreach ((string helper, string id) in DataflowSchedulingHelpers)
        {
            CallSiteRejection(builder, BuiltInRuleFamily.Dataflow, "clockwork.dataflow." + id,
                new MemberSignature(DataflowNamespace + "DataflowBlock", helper), DataflowShim);
        }

        // ---- WebSockets over a stream: the framework's protocol implementation sends keep-alive pings from a real timer on
        // the thread pool and times out its closing handshake in real time, so the factories which run it over a
        // caller's stream are rejected. ----
        CallSiteRejection(builder, BuiltInRuleFamily.WebSockets, "clockwork.websocket.createfromstream",
            new MemberSignature(WebSocketType, "CreateFromStream"), WebSocketShim);
        CallSiteRejection(builder, BuiltInRuleFamily.WebSockets, "clockwork.websocket.createclientwebsocket",
            new MemberSignature(WebSocketType, "CreateClientWebSocket"), WebSocketShim);

        // ---- WebSocketStream factories which may make the stream own its socket: on disposal an owning stream bounds the
        // closing handshake with a real timer, so the shims reject a finite, non-zero close timeout. ----
        TaskRule(builder, BuiltInRuleFamily.WebSockets, "clockwork.websocketstream.create.ownswebsocket",
            MemberSignature.Method(WebSocketStreamType, "Create", WebSocketType, WebSocketMessageType, Boolean),
            Shim(WebSocketStreamShim, "Create", WebSocketType, WebSocketMessageType, Boolean));
        TaskRule(builder, BuiltInRuleFamily.WebSockets, "clockwork.websocketstream.create.closetimeout",
            MemberSignature.Method(WebSocketStreamType, "Create", WebSocketType, WebSocketMessageType, TimeSpan),
            Shim(WebSocketStreamShim, "Create", WebSocketType, WebSocketMessageType, TimeSpan));

        // ---- HttpClient: the BCL bounds every request with HttpClient.Timeout on a real timer, so each member which sends
        // redirects to a shim which rejects a finite timeout and then calls the BCL member. A call through
        // HttpMessageInvoker reaches the client's override, so its shim checks a client receiver too. The virtual members'
        // base calls run the base implementation without dispatch. ----
        foreach ((string member, string[] parameters) in HttpClientSends)
        {
            RewriteRule rule = RewriteRule.RedirectCall(
                HttpRuleId("clockwork.http.httpclient.", member, parameters),
                MemberSignature.Method(HttpClientType, member, parameters),
                Shim(HttpClientShim, member, [HttpClientType, .. parameters]));
            if (parameters is [HttpRequestMessageType, CancellationToken])
            {
                rule = rule with { BaseCallReplacement = Shim(HttpClientShim, "Base" + member, [HttpClientType, .. parameters]) };
            }

            builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.HttpClient, rule));
        }

        foreach (string member in (ReadOnlySpan<string>)["Send", "SendAsync"])
        {
            builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.HttpClient, RewriteRule.RedirectCall(
                HttpRuleId("clockwork.http.httpmessageinvoker.", member, [HttpRequestMessageType, CancellationToken]),
                MemberSignature.Method(HttpMessageInvokerType, member, HttpRequestMessageType, CancellationToken),
                Shim(HttpMessageInvokerShim, member, HttpMessageInvokerType, HttpRequestMessageType, CancellationToken)) with
            {
                BaseCallReplacement = Shim(HttpMessageInvokerShim, "Base" + member, HttpMessageInvokerType, HttpRequestMessageType, CancellationToken),
            }));
        }

        foreach ((string member, string[] parameters) in HttpClientJsonSends)
        {
            TaskRule(builder, BuiltInRuleFamily.HttpClient, HttpRuleId("clockwork.http.json.", member, parameters),
                MemberSignature.Method(HttpClientJsonType, member, parameters),
                Shim(HttpClientJsonShim, member, [.. parameters.Select(static parameter => parameter.Replace(MethodTypeArg0, "TValue", StringComparison.Ordinal))]));
        }

        // ---- Hosting: the BCL's extensions await the host's work in framework code, block the caller on it (Start, Run,
        // WaitForShutdown), or bound a stop with a real timer (StopAsync(IHost, TimeSpan)); the shims await through the
        // coordinator, wait with the controlled wait, and time out in virtual time. ----
        foreach ((string member, string id) in (ReadOnlySpan<(string, string)>)[("Start", "start"), ("Run", "run"), ("WaitForShutdown", "waitforshutdown")])
        {
            TaskRule(builder, BuiltInRuleFamily.Hosting, "clockwork.hosting." + id,
                MemberSignature.Method(HostExtensionsType, member, IHostType), Shim(HostExtensionsShim, member, IHostType));
        }

        foreach ((string member, string id) in (ReadOnlySpan<(string, string)>)[("RunAsync", "runasync"), ("WaitForShutdownAsync", "waitforshutdownasync")])
        {
            TaskRule(builder, BuiltInRuleFamily.Hosting, "clockwork.hosting." + id,
                MemberSignature.Method(HostExtensionsType, member, IHostType, CancellationToken),
                Shim(HostExtensionsShim, member, IHostType, CancellationToken));
        }

        TaskRule(builder, BuiltInRuleFamily.Hosting, "clockwork.hosting.stopasync.timeout",
            MemberSignature.Method(HostExtensionsType, "StopAsync", IHostType, TimeSpan),
            Shim(HostExtensionsShim, "StopAsync", IHostType, TimeSpan));

        // The builder's Start blocks on StartAsync, which builds the host and awaits its start in framework code.
        TaskRule(builder, BuiltInRuleFamily.Hosting, "clockwork.hosting.builder.start",
            MemberSignature.Method(HostBuilderExtensionsType, "Start", IHostBuilderType),
            Shim(HostBuilderExtensionsShim, "Start", IHostBuilderType));
        TaskRule(builder, BuiltInRuleFamily.Hosting, "clockwork.hosting.builder.startasync",
            MemberSignature.Method(HostBuilderExtensionsType, "StartAsync", IHostBuilderType, CancellationToken),
            Shim(HostBuilderExtensionsShim, "StartAsync", IHostBuilderType, CancellationToken));

        // ---- Uncontrolled invocation: process control and abrupt host termination. A
        // rewritten assembly must never launch, kill, signal, wait on, reschedule, or terminate a real OS process out from
        // under the simulation, so each of these call sites is rejected with a precise diagnostic naming
        // the exact API and IL offset (recorded in the manifest as a Rejected transformation). ----
        UncontrolledRejection(builder, "clockwork.process.start.filename", ProcessType, "Start", String);
        UncontrolledRejection(builder, "clockwork.process.start.startinfo", ProcessType, "Start", ProcessStartInfoType);
        UncontrolledRejection(builder, "clockwork.process.start.filename.arguments", ProcessType, "Start", String, String);
        UncontrolledRejection(builder, "clockwork.process.start.filename.argumentlist", ProcessType, "Start", String, IEnumerableOfStringType);
        UncontrolledRejection(builder, "clockwork.process.start.filename.credentials", ProcessType, "Start", String, String, SecureStringType, String);
        UncontrolledRejection(builder, "clockwork.process.start.filename.arguments.credentials", ProcessType, "Start", String, String, String, SecureStringType, String);
        UncontrolledRejection(builder, "clockwork.process.start.instance", ProcessType, "Start");
        UncontrolledRejection(builder, "clockwork.process.kill", ProcessType, "Kill");
        UncontrolledRejection(builder, "clockwork.process.kill.tree", ProcessType, "Kill", Boolean);
        UncontrolledRejection(builder, "clockwork.process.waitforexit", ProcessType, "WaitForExit");
        UncontrolledRejection(builder, "clockwork.process.waitforexit.milliseconds", ProcessType, "WaitForExit", Int32);
        UncontrolledRejection(builder, "clockwork.process.waitforexit.timespan", ProcessType, "WaitForExit", TimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.waitforexitasync", ProcessType, "WaitForExitAsync", CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.process.run.startinfo", ProcessType, "Run", ProcessStartInfoType, NullableTimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.run.filename", ProcessType, "Run", String, IEnumerableOfStringType, Boolean, NullableTimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.runasync.startinfo", ProcessType, "RunAsync", ProcessStartInfoType, CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.process.runasync.filename", ProcessType, "RunAsync", String, IEnumerableOfStringType, Boolean, CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.process.runandcapturetext.startinfo", ProcessType, "RunAndCaptureText", ProcessStartInfoType, NullableTimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.runandcapturetext.filename", ProcessType, "RunAndCaptureText", String, IEnumerableOfStringType, NullableTimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.runandcapturetextasync.startinfo", ProcessType, "RunAndCaptureTextAsync", ProcessStartInfoType, CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.process.runandcapturetextasync.filename", ProcessType, "RunAndCaptureTextAsync", String, IEnumerableOfStringType, CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.process.startandforget.startinfo", ProcessType, "StartAndForget", ProcessStartInfoType);
        UncontrolledRejection(builder, "clockwork.process.startandforget.filename", ProcessType, "StartAndForget", String, IEnumerableOfStringType);
        UncontrolledRejection(builder, "clockwork.process.signal", ProcessType, "Signal", PosixSignalType);
        UncontrolledRejection(builder, "clockwork.process.waitforexitstatus", ProcessType, "WaitForExitStatus");
        UncontrolledRejection(builder, "clockwork.process.trywaitforexitstatus", ProcessType, "TryWaitForExitStatus", TimeSpanType, ProcessExitStatusRef);
        UncontrolledRejection(builder, "clockwork.process.waitforexitstatusasync", ProcessType, "WaitForExitStatusAsync", CancellationTokenType);

        // Reading a process's whole output blocks until the process closes its standard output and error.
        UncontrolledRejection(builder, "clockwork.process.readallbytes", ProcessType, "ReadAllBytes", NullableTimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.readallbytesasync", ProcessType, "ReadAllBytesAsync", CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.process.readalltext", ProcessType, "ReadAllText", NullableTimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.readalltextasync", ProcessType, "ReadAllTextAsync", CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.process.readalllines", ProcessType, "ReadAllLines", NullableTimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.readalllinesasync", ProcessType, "ReadAllLinesAsync", CancellationTokenType);

        // Waiting for a process to go idle, asking it whether it responds, or closing its main window waits on and signals
        // a real process; the event-based exit and output notifications raise their events on threads of the BCL's
        // choosing as the process exits or writes.
        UncontrolledRejection(builder, "clockwork.process.waitforinputidle", ProcessType, "WaitForInputIdle");
        UncontrolledRejection(builder, "clockwork.process.waitforinputidle.milliseconds", ProcessType, "WaitForInputIdle", Int32);
        UncontrolledRejection(builder, "clockwork.process.waitforinputidle.timespan", ProcessType, "WaitForInputIdle", TimeSpanType);
        UncontrolledRejection(builder, "clockwork.process.get_responding", ProcessType, "get_Responding");
        UncontrolledRejection(builder, "clockwork.process.closemainwindow", ProcessType, "CloseMainWindow");
        UncontrolledRejection(builder, "clockwork.process.set_enableraisingevents", ProcessType, "set_EnableRaisingEvents", Boolean);
        UncontrolledRejection(builder, "clockwork.process.beginoutputreadline", ProcessType, "BeginOutputReadLine");
        UncontrolledRejection(builder, "clockwork.process.beginerrorreadline", ProcessType, "BeginErrorReadLine");

        // The scheduling, affinity, and working-set setters of a process and of its OS threads control a real process.
        UncontrolledRejection(builder, "clockwork.process.set_priorityclass", ProcessType, "set_PriorityClass", ProcessPriorityClassType);
        UncontrolledRejection(builder, "clockwork.process.set_priorityboostenabled", ProcessType, "set_PriorityBoostEnabled", Boolean);
        UncontrolledRejection(builder, "clockwork.process.set_processoraffinity", ProcessType, "set_ProcessorAffinity", IntPtrType);
        UncontrolledRejection(builder, "clockwork.process.set_maxworkingset", ProcessType, "set_MaxWorkingSet", IntPtrType);
        UncontrolledRejection(builder, "clockwork.process.set_minworkingset", ProcessType, "set_MinWorkingSet", IntPtrType);
        UncontrolledRejection(builder, "clockwork.processthread.set_idealprocessor", ProcessThreadType, "set_IdealProcessor", Int32);
        UncontrolledRejection(builder, "clockwork.processthread.resetidealprocessor", ProcessThreadType, "ResetIdealProcessor");
        UncontrolledRejection(builder, "clockwork.processthread.set_priorityboostenabled", ProcessThreadType, "set_PriorityBoostEnabled", Boolean);
        UncontrolledRejection(builder, "clockwork.processthread.set_prioritylevel", ProcessThreadType, "set_PriorityLevel", ThreadPriorityLevelType);
        UncontrolledRejection(builder, "clockwork.processthread.set_processoraffinity", ProcessThreadType, "set_ProcessorAffinity", IntPtrType);

        // A process handle starts, signals, resumes, kills, and waits on a process as Process does.
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.start", SafeProcessHandleType, "Start", ProcessStartInfoType);
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.signal", SafeProcessHandleType, "Signal", PosixSignalType);
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.resume", SafeProcessHandleType, "Resume");
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.kill", SafeProcessHandleType, "Kill");
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.waitforexit", SafeProcessHandleType, "WaitForExit");
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.trywaitforexit", SafeProcessHandleType, "TryWaitForExit", TimeSpanType, ProcessExitStatusRef);
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.waitforexitasync", SafeProcessHandleType, "WaitForExitAsync", CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.waitforexitorkillontimeout", SafeProcessHandleType, "WaitForExitOrKillOnTimeout", TimeSpanType);
        UncontrolledRejection(builder, "clockwork.safeprocesshandle.waitforexitorkilloncancellationasync", SafeProcessHandleType, "WaitForExitOrKillOnCancellationAsync", CancellationTokenType);
        UncontrolledRejection(builder, "clockwork.windowsprocessstartarguments.start", WindowsProcessStartArgumentsType, "Start", ProcessStartInfoType, WindowsProcessStarter);
        UncontrolledRejection(builder, "clockwork.unixprocessstartarguments.start", UnixProcessStartArgumentsType, "Start", ProcessStartInfoType, UnixProcessStarter);
        UncontrolledRejection(builder, "clockwork.environment.exit", EnvironmentType, "Exit", Int32);
        UncontrolledRejection(builder, "clockwork.environment.failfast.message", EnvironmentType, "FailFast", String);
        UncontrolledRejection(builder, "clockwork.environment.failfast.exception", EnvironmentType, "FailFast", String, ExceptionType);

        // ---- Monitor: the entire static Monitor surface is redirected to ControlledMonitor,
        // which models ownership/recursion/condition-wait on the cooperative logical thread. Because the C#
        // `lock (object)` statement lowers to Monitor.Enter(obj, ref bool) + finally Monitor.Exit(obj),
        // redirecting these members controls every `lock` automatically - no separate lock rule is needed.
        // Monitor is static, so each shim signature matches the BCL target exactly. ----
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.enter",
            MemberSignature.Method(MonitorType, "Enter", ObjectType), Shim(MonitorShim, "Enter", ObjectType));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.enter.locktaken",
            MemberSignature.Method(MonitorType, "Enter", ObjectType, BooleanRef), Shim(MonitorShim, "Enter", ObjectType, BooleanRef));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.exit",
            MemberSignature.Method(MonitorType, "Exit", ObjectType), Shim(MonitorShim, "Exit", ObjectType));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.isentered",
            MemberSignature.Method(MonitorType, "IsEntered", ObjectType), Shim(MonitorShim, "IsEntered", ObjectType));
        RejectedRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.get_lockcontentioncount",
            MemberSignature.Method(MonitorType, "get_LockContentionCount"), Shim(MonitorShim, "LockContentionCount"));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.tryenter",
            MemberSignature.Method(MonitorType, "TryEnter", ObjectType), Shim(MonitorShim, "TryEnter", ObjectType));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.tryenter.locktaken",
            MemberSignature.Method(MonitorType, "TryEnter", ObjectType, BooleanRef), Shim(MonitorShim, "TryEnter", ObjectType, BooleanRef));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.tryenter.milliseconds",
            MemberSignature.Method(MonitorType, "TryEnter", ObjectType, Int32), Shim(MonitorShim, "TryEnter", ObjectType, Int32));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.tryenter.milliseconds.locktaken",
            MemberSignature.Method(MonitorType, "TryEnter", ObjectType, Int32, BooleanRef), Shim(MonitorShim, "TryEnter", ObjectType, Int32, BooleanRef));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.tryenter.timespan",
            MemberSignature.Method(MonitorType, "TryEnter", ObjectType, TimeSpan), Shim(MonitorShim, "TryEnter", ObjectType, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.tryenter.timespan.locktaken",
            MemberSignature.Method(MonitorType, "TryEnter", ObjectType, TimeSpan, BooleanRef), Shim(MonitorShim, "TryEnter", ObjectType, TimeSpan, BooleanRef));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.wait",
            MemberSignature.Method(MonitorType, "Wait", ObjectType), Shim(MonitorShim, "Wait", ObjectType));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.wait.milliseconds",
            MemberSignature.Method(MonitorType, "Wait", ObjectType, Int32), Shim(MonitorShim, "Wait", ObjectType, Int32));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.wait.milliseconds.exitcontext",
            MemberSignature.Method(MonitorType, "Wait", ObjectType, Int32, Boolean), Shim(MonitorShim, "Wait", ObjectType, Int32, Boolean));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.wait.timespan",
            MemberSignature.Method(MonitorType, "Wait", ObjectType, TimeSpan), Shim(MonitorShim, "Wait", ObjectType, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.wait.timespan.exitcontext",
            MemberSignature.Method(MonitorType, "Wait", ObjectType, TimeSpan, Boolean), Shim(MonitorShim, "Wait", ObjectType, TimeSpan, Boolean));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.pulse",
            MemberSignature.Method(MonitorType, "Pulse", ObjectType), Shim(MonitorShim, "Pulse", ObjectType));
        TaskRule(builder, BuiltInRuleFamily.Monitor, "clockwork.monitor.pulseall",
            MemberSignature.Method(MonitorType, "PulseAll", ObjectType), Shim(MonitorShim, "PulseAll", ObjectType));

        // ---- System.Threading.Lock: type substitution retargets the dedicated lock type and its
        // nested Scope ref struct onto the controlled equivalents. This remaps `new Lock()`, every field/
        // local/parameter typed as Lock or Lock.Scope, and the C# `lock (Lock)` lowering
        // (EnterScope/Scope.Dispose) wholesale, so no per-member call rules are required. ----
        Sub(builder, BuiltInRuleFamily.Lock, "clockwork.lock.type", LockType, ControlledLockType);
        Sub(builder, BuiltInRuleFamily.Lock, "clockwork.lock.scope.type", LockScopeType, ControlledLockScopeType);

        // ---- SemaphoreSlim: SemaphoreSlim is a sealed class, so the controlled object is a real
        // SemaphoreSlim identity handle whose count/waiter state lives in a weak-keyed side table. The two
        // constructors redirect to Create factories; every instance member is receiver-first (the shim
        // prepends the SemaphoreSlim receiver). AvailableWaitHandle is bridged to a controlled manual-reset
        // wait handle whose signalled state tracks whether a permit is available. ----
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Semaphore, RewriteRule.RedirectNewObj(
            "clockwork.semaphoreslim.ctor.initial",
            MemberSignature.Constructor(SemaphoreSlimType, Int32), Shim(SemaphoreSlimShim, "Create", Int32))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Semaphore, RewriteRule.RedirectNewObj(
            "clockwork.semaphoreslim.ctor.initial.max",
            MemberSignature.Constructor(SemaphoreSlimType, Int32, Int32), Shim(SemaphoreSlimShim, "Create", Int32, Int32))));

        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.get_currentcount",
            MemberSignature.Method(SemaphoreSlimType, "get_CurrentCount"), Shim(SemaphoreSlimShim, "CurrentCount", SemaphoreSlimType));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.wait",
            MemberSignature.Method(SemaphoreSlimType, "Wait"), Shim(SemaphoreSlimShim, "Wait", SemaphoreSlimType));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.wait.cancellationtoken",
            MemberSignature.Method(SemaphoreSlimType, "Wait", CancellationToken), Shim(SemaphoreSlimShim, "Wait", SemaphoreSlimType, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.wait.milliseconds",
            MemberSignature.Method(SemaphoreSlimType, "Wait", Int32), Shim(SemaphoreSlimShim, "Wait", SemaphoreSlimType, Int32));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.wait.milliseconds.cancellationtoken",
            MemberSignature.Method(SemaphoreSlimType, "Wait", Int32, CancellationToken), Shim(SemaphoreSlimShim, "Wait", SemaphoreSlimType, Int32, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.wait.timespan",
            MemberSignature.Method(SemaphoreSlimType, "Wait", TimeSpan), Shim(SemaphoreSlimShim, "Wait", SemaphoreSlimType, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.wait.timespan.cancellationtoken",
            MemberSignature.Method(SemaphoreSlimType, "Wait", TimeSpan, CancellationToken), Shim(SemaphoreSlimShim, "Wait", SemaphoreSlimType, TimeSpan, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.waitasync",
            MemberSignature.Method(SemaphoreSlimType, "WaitAsync"), Shim(SemaphoreSlimShim, "WaitAsync", SemaphoreSlimType));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.waitasync.cancellationtoken",
            MemberSignature.Method(SemaphoreSlimType, "WaitAsync", CancellationToken), Shim(SemaphoreSlimShim, "WaitAsync", SemaphoreSlimType, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.waitasync.milliseconds",
            MemberSignature.Method(SemaphoreSlimType, "WaitAsync", Int32), Shim(SemaphoreSlimShim, "WaitAsync", SemaphoreSlimType, Int32));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.waitasync.milliseconds.cancellationtoken",
            MemberSignature.Method(SemaphoreSlimType, "WaitAsync", Int32, CancellationToken), Shim(SemaphoreSlimShim, "WaitAsync", SemaphoreSlimType, Int32, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.waitasync.timespan",
            MemberSignature.Method(SemaphoreSlimType, "WaitAsync", TimeSpan), Shim(SemaphoreSlimShim, "WaitAsync", SemaphoreSlimType, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.waitasync.timespan.cancellationtoken",
            MemberSignature.Method(SemaphoreSlimType, "WaitAsync", TimeSpan, CancellationToken), Shim(SemaphoreSlimShim, "WaitAsync", SemaphoreSlimType, TimeSpan, CancellationToken));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.release",
            MemberSignature.Method(SemaphoreSlimType, "Release"), Shim(SemaphoreSlimShim, "Release", SemaphoreSlimType));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.release.count",
            MemberSignature.Method(SemaphoreSlimType, "Release", Int32), Shim(SemaphoreSlimShim, "Release", SemaphoreSlimType, Int32));
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.dispose",
            MemberSignature.Method(SemaphoreSlimType, "Dispose"), Shim(SemaphoreSlimShim, "Dispose", SemaphoreSlimType));

        // AvailableWaitHandle is bridged to a controlled manual-reset wait handle tracking count > 0.
        TaskRule(builder, BuiltInRuleFamily.Semaphore, "clockwork.semaphoreslim.get_availablewaithandle",
            MemberSignature.Method(SemaphoreSlimType, "get_AvailableWaitHandle"), Shim(SemaphoreSlimShim, "AvailableWaitHandle", SemaphoreSlimType));

        BuildInterlockedEntries(builder);
        BuildVolatileEntries(builder);

        // ---- System.Threading.SpinWait: a value type retargeted by whole-type substitution,
        // exactly like System.Threading.Lock/Scope. This remaps every field/local/parameter typed SpinWait,
        // each `new SpinWait()` / `default`, the instance members (Count, NextSpinWillYield, Reset, both
        // SpinOnce overloads) and the static SpinUntil overloads onto the controlled struct, so no per-member
        // call rules are required. A controlled spin never busy-waits: it yields to the deterministic loop. ----
        Sub(builder, BuiltInRuleFamily.SpinWait, "clockwork.spinwait.type", SpinWaitType, ControlledSpinWaitType);

        BuildWaitHandleEntries(builder);
        BuildModernSynchronizationSynchronizationEntries(builder);

        return builder.ToImmutable();
    }

    // modern synchronization: the remaining modern synchronization surface. ReaderWriterLockSlim and
    // ManualResetEventSlim use BCL instances strictly as identity handles, so constructors are redirected
    // to factories and all declared instance members are receiver-first shims. Mutex and Semaphore use the
    // same identity-handle model; inherited WaitHandle operations are already handled by BuildWaitHandleEntries.
    // SpinLock, Barrier, and CountdownEvent have self-contained controlled types and are substituted wholly.
    private static void BuildModernSynchronizationSynchronizationEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        BuildReaderWriterLockSlimEntries(builder);
        BuildManualResetEventSlimEntries(builder);
        BuildKernelMutexEntries(builder);
        BuildKernelSemaphoreEntries(builder);
        BuildContextEntries(builder);

        Sub(builder, BuiltInRuleFamily.SpinLock, "clockwork.spinlock.type", SpinLockType, ControlledSpinLockType);
        Sub(builder, BuiltInRuleFamily.Barrier, "clockwork.barrier.type", BarrierType, ControlledBarrierType);
        Sub(builder, BuiltInRuleFamily.CountdownEvent, "clockwork.countdownevent.type", CountdownEventType, ControlledCountdownEventType);
    }

    private static void BuildReaderWriterLockSlimEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.ReaderWriterLockSlim, RewriteRule.RedirectNewObj(
            "clockwork.readerwriterlockslim.ctor",
            MemberSignature.Constructor(ReaderWriterLockSlimType),
            Shim(ReaderWriterLockSlimShim, "Create"))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.ReaderWriterLockSlim, RewriteRule.RedirectNewObj(
            "clockwork.readerwriterlockslim.ctor.recursionpolicy",
            MemberSignature.Constructor(ReaderWriterLockSlimType, LockRecursionPolicyType),
            Shim(ReaderWriterLockSlimShim, "Create", LockRecursionPolicyType))));

        ReaderWriterRule("get_recursionpolicy", "get_RecursionPolicy", [], "RecursionPolicy");
        ReaderWriterRule("get_currentreadcount", "get_CurrentReadCount", [], "CurrentReadCount");
        ReaderWriterRule("get_isreadlockheld", "get_IsReadLockHeld", [], "IsReadLockHeld");
        ReaderWriterRule("get_isupgradeablereadlockheld", "get_IsUpgradeableReadLockHeld", [], "IsUpgradeableReadLockHeld");
        ReaderWriterRule("get_iswritelockheld", "get_IsWriteLockHeld", [], "IsWriteLockHeld");
        ReaderWriterRule("get_recursivereadcount", "get_RecursiveReadCount", [], "RecursiveReadCount");
        ReaderWriterRule("get_recursiveupgradecount", "get_RecursiveUpgradeCount", [], "RecursiveUpgradeCount");
        ReaderWriterRule("get_recursivewritecount", "get_RecursiveWriteCount", [], "RecursiveWriteCount");
        ReaderWriterRule("get_waitingreadcount", "get_WaitingReadCount", [], "WaitingReadCount");
        ReaderWriterRule("get_waitingupgradecount", "get_WaitingUpgradeCount", [], "WaitingUpgradeCount");
        ReaderWriterRule("get_waitingwritecount", "get_WaitingWriteCount", [], "WaitingWriteCount");
        ReaderWriterRule("enterreadlock", "EnterReadLock", [], "EnterReadLock");
        ReaderWriterRule("tryenterreadlock.milliseconds", "TryEnterReadLock", [Int32], "TryEnterReadLock", Int32);
        ReaderWriterRule("tryenterreadlock.timespan", "TryEnterReadLock", [TimeSpan], "TryEnterReadLock", TimeSpan);
        ReaderWriterRule("exitreadlock", "ExitReadLock", [], "ExitReadLock");
        ReaderWriterRule("enterupgradeablereadlock", "EnterUpgradeableReadLock", [], "EnterUpgradeableReadLock");
        ReaderWriterRule("tryenterupgradeablereadlock.milliseconds", "TryEnterUpgradeableReadLock", [Int32], "TryEnterUpgradeableReadLock", Int32);
        ReaderWriterRule("tryenterupgradeablereadlock.timespan", "TryEnterUpgradeableReadLock", [TimeSpan], "TryEnterUpgradeableReadLock", TimeSpan);
        ReaderWriterRule("exitupgradeablereadlock", "ExitUpgradeableReadLock", [], "ExitUpgradeableReadLock");
        ReaderWriterRule("enterwritelock", "EnterWriteLock", [], "EnterWriteLock");
        ReaderWriterRule("tryenterwritelock.milliseconds", "TryEnterWriteLock", [Int32], "TryEnterWriteLock", Int32);
        ReaderWriterRule("tryenterwritelock.timespan", "TryEnterWriteLock", [TimeSpan], "TryEnterWriteLock", TimeSpan);
        ReaderWriterRule("exitwritelock", "ExitWriteLock", [], "ExitWriteLock");
        ReaderWriterRule("dispose", "Dispose", [], "Dispose");

        void ReaderWriterRule(string id, string targetMember, string[] targetParameters, string shimMember, params string[] shimParameters) =>
            TaskRule(
                builder,
                BuiltInRuleFamily.ReaderWriterLockSlim,
                "clockwork.readerwriterlockslim." + id,
                MemberSignature.Method(ReaderWriterLockSlimType, targetMember, targetParameters),
                Shim(ReaderWriterLockSlimShim, shimMember, [ReaderWriterLockSlimType, .. shimParameters]));
    }

    private static void BuildManualResetEventSlimEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.ManualResetEventSlim, RewriteRule.RedirectNewObj(
            "clockwork.manualreseteventslim.ctor",
            MemberSignature.Constructor(ManualResetEventSlimType),
            Shim(ManualResetEventSlimShim, "Create"))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.ManualResetEventSlim, RewriteRule.RedirectNewObj(
            "clockwork.manualreseteventslim.ctor.initialstate",
            MemberSignature.Constructor(ManualResetEventSlimType, Boolean),
            Shim(ManualResetEventSlimShim, "Create", Boolean))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.ManualResetEventSlim, RewriteRule.RedirectNewObj(
            "clockwork.manualreseteventslim.ctor.initialstate.spincount",
            MemberSignature.Constructor(ManualResetEventSlimType, Boolean, Int32),
            Shim(ManualResetEventSlimShim, "Create", Boolean, Int32))));

        ManualResetEventSlimRule("get_isset", "get_IsSet", [], "IsSet");
        ManualResetEventSlimRule("get_spincount", "get_SpinCount", [], "SpinCount");
        ManualResetEventSlimRule("get_waithandle", "get_WaitHandle", [], "WaitHandle");
        ManualResetEventSlimRule("set", "Set", [], "Set");
        ManualResetEventSlimRule("reset", "Reset", [], "Reset");
        ManualResetEventSlimRule("wait", "Wait", [], "Wait");
        ManualResetEventSlimRule("wait.cancellationtoken", "Wait", [CancellationToken], "Wait", CancellationToken);
        ManualResetEventSlimRule("wait.milliseconds", "Wait", [Int32], "Wait", Int32);
        ManualResetEventSlimRule("wait.milliseconds.cancellationtoken", "Wait", [Int32, CancellationToken], "Wait", Int32, CancellationToken);
        ManualResetEventSlimRule("wait.timespan", "Wait", [TimeSpan], "Wait", TimeSpan);
        ManualResetEventSlimRule("wait.timespan.cancellationtoken", "Wait", [TimeSpan, CancellationToken], "Wait", TimeSpan, CancellationToken);
        ManualResetEventSlimRule("dispose", "Dispose", [], "Dispose");

        void ManualResetEventSlimRule(
            string id,
            string targetMember,
            string[] targetParameters,
            string shimMember,
            params string[] shimParameters) =>
            TaskRule(
                builder,
                BuiltInRuleFamily.ManualResetEventSlim,
                "clockwork.manualreseteventslim." + id,
                MemberSignature.Method(ManualResetEventSlimType, targetMember, targetParameters),
                Shim(ManualResetEventSlimShim, shimMember, [ManualResetEventSlimType, .. shimParameters]));
    }

    private static void BuildKernelMutexEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Mutex, RewriteRule.RedirectNewObj(
            "clockwork.mutex.ctor", MemberSignature.Constructor(MutexType), Shim(MutexShim, "Create"))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Mutex, RewriteRule.RedirectNewObj(
            "clockwork.mutex.ctor.initiallyowned", MemberSignature.Constructor(MutexType, Boolean), Shim(MutexShim, "Create", Boolean))));
        TaskRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.release",
            MemberSignature.Method(MutexType, "ReleaseMutex"), Shim(MutexShim, "ReleaseMutex", MutexType));

        RejectedNewObjRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.ctor.named",
            MemberSignature.Constructor(MutexType, Boolean, String), Shim(MutexShim, "CreateNamed", Boolean, String));
        RejectedNewObjRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.ctor.named.creatednew",
            MemberSignature.Constructor(MutexType, Boolean, String, BooleanRef), Shim(MutexShim, "CreateNamed", Boolean, String, BooleanRef));
        RejectedNewObjRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.ctor.named.options",
            MemberSignature.Constructor(MutexType, Boolean, String, NamedWaitHandleOptionsType), Shim(MutexShim, "CreateNamed", Boolean, String, NamedWaitHandleOptionsType));
        RejectedNewObjRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.ctor.named.options.creatednew",
            MemberSignature.Constructor(MutexType, Boolean, String, NamedWaitHandleOptionsType, BooleanRef), Shim(MutexShim, "CreateNamed", Boolean, String, NamedWaitHandleOptionsType, BooleanRef));
        RejectedNewObjRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.ctor.name.options",
            MemberSignature.Constructor(MutexType, String, NamedWaitHandleOptionsType), Shim(MutexShim, "CreateNamed", String, NamedWaitHandleOptionsType));
        RejectedRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.openexisting",
            MemberSignature.Method(MutexType, "OpenExisting", String), Shim(MutexShim, "OpenExisting", String));
        RejectedRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.openexisting.options",
            MemberSignature.Method(MutexType, "OpenExisting", String, NamedWaitHandleOptionsType), Shim(MutexShim, "OpenExisting", String, NamedWaitHandleOptionsType));
        RejectedRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.tryopenexisting",
            MemberSignature.Method(MutexType, "TryOpenExisting", String, MutexRef), Shim(MutexShim, "TryOpenExisting", String, MutexRef));
        RejectedRule(builder, BuiltInRuleFamily.Mutex, "clockwork.mutex.tryopenexisting.options",
            MemberSignature.Method(MutexType, "TryOpenExisting", String, NamedWaitHandleOptionsType, MutexRef), Shim(MutexShim, "TryOpenExisting", String, NamedWaitHandleOptionsType, MutexRef));
    }

    private static void BuildKernelSemaphoreEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.KernelSemaphore, RewriteRule.RedirectNewObj(
            "clockwork.semaphore.ctor", MemberSignature.Constructor(KernelSemaphoreType, Int32, Int32), Shim(KernelSemaphoreShim, "Create", Int32, Int32))));
        TaskRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.release",
            MemberSignature.Method(KernelSemaphoreType, "Release"), Shim(KernelSemaphoreShim, "Release", KernelSemaphoreType));
        TaskRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.release.count",
            MemberSignature.Method(KernelSemaphoreType, "Release", Int32), Shim(KernelSemaphoreShim, "Release", KernelSemaphoreType, Int32));

        RejectedNewObjRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.ctor.named",
            MemberSignature.Constructor(KernelSemaphoreType, Int32, Int32, String), Shim(KernelSemaphoreShim, "CreateNamed", Int32, Int32, String));
        RejectedNewObjRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.ctor.named.creatednew",
            MemberSignature.Constructor(KernelSemaphoreType, Int32, Int32, String, BooleanRef), Shim(KernelSemaphoreShim, "CreateNamed", Int32, Int32, String, BooleanRef));
        RejectedNewObjRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.ctor.named.options",
            MemberSignature.Constructor(KernelSemaphoreType, Int32, Int32, String, NamedWaitHandleOptionsType), Shim(KernelSemaphoreShim, "CreateNamed", Int32, Int32, String, NamedWaitHandleOptionsType));
        RejectedNewObjRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.ctor.named.options.creatednew",
            MemberSignature.Constructor(KernelSemaphoreType, Int32, Int32, String, NamedWaitHandleOptionsType, BooleanRef), Shim(KernelSemaphoreShim, "CreateNamed", Int32, Int32, String, NamedWaitHandleOptionsType, BooleanRef));
        RejectedRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.openexisting",
            MemberSignature.Method(KernelSemaphoreType, "OpenExisting", String), Shim(KernelSemaphoreShim, "OpenExisting", String));
        RejectedRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.openexisting.options",
            MemberSignature.Method(KernelSemaphoreType, "OpenExisting", String, NamedWaitHandleOptionsType), Shim(KernelSemaphoreShim, "OpenExisting", String, NamedWaitHandleOptionsType));
        RejectedRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.tryopenexisting",
            MemberSignature.Method(KernelSemaphoreType, "TryOpenExisting", String, KernelSemaphoreRef), Shim(KernelSemaphoreShim, "TryOpenExisting", String, KernelSemaphoreRef));
        RejectedRule(builder, BuiltInRuleFamily.KernelSemaphore, "clockwork.semaphore.tryopenexisting.options",
            MemberSignature.Method(KernelSemaphoreType, "TryOpenExisting", String, NamedWaitHandleOptionsType, KernelSemaphoreRef), Shim(KernelSemaphoreShim, "TryOpenExisting", String, NamedWaitHandleOptionsType, KernelSemaphoreRef));
    }

    private static void BuildContextEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.capture",
            MemberSignature.Method(ExecutionContextType, "Capture"), Shim(ExecutionContextShim, "Capture"));
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.run",
            MemberSignature.Method(ExecutionContextType, "Run", ExecutionContextType, ContextCallbackType, ObjectType),
            Shim(ExecutionContextShim, "Run", ExecutionContextType, ContextCallbackType, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.suppressflow",
            MemberSignature.Method(ExecutionContextType, "SuppressFlow"), Shim(ExecutionContextShim, "SuppressFlow"));
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.restoreflow",
            MemberSignature.Method(ExecutionContextType, "RestoreFlow"), Shim(ExecutionContextShim, "RestoreFlow"));
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.isflowsuppressed",
            MemberSignature.Method(ExecutionContextType, "IsFlowSuppressed"), Shim(ExecutionContextShim, "IsFlowSuppressed"));
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.restore",
            MemberSignature.Method(ExecutionContextType, "Restore", ExecutionContextType), Shim(ExecutionContextShim, "Restore", ExecutionContextType));
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.createcopy",
            MemberSignature.Method(ExecutionContextType, "CreateCopy"), Shim(ExecutionContextShim, "CreateCopy", ExecutionContextType));
        TaskRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.dispose",
            MemberSignature.Method(ExecutionContextType, "Dispose"), Shim(ExecutionContextShim, "Dispose", ExecutionContextType));
        RejectedRule(builder, BuiltInRuleFamily.ExecutionContext, "clockwork.executioncontext.getobjectdata",
            MemberSignature.Method(ExecutionContextType, "GetObjectData", SerializationInfoType, StreamingContextType),
            Shim(ExecutionContextShim, "GetObjectData", ExecutionContextType, SerializationInfoType, StreamingContextType));

        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.get_current",
            MemberSignature.Method(SynchronizationContextType, "get_Current"), Shim(SynchronizationContextShim, "Current"));
        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.set_current",
            MemberSignature.Method(SynchronizationContextType, "SetSynchronizationContext", SynchronizationContextType),
            Shim(SynchronizationContextShim, "SetSynchronizationContext", SynchronizationContextType));
        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.createcopy",
            MemberSignature.Method(SynchronizationContextType, "CreateCopy"), Shim(SynchronizationContextShim, "CreateCopy", SynchronizationContextType));
        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.iswaitnotificationrequired",
            MemberSignature.Method(SynchronizationContextType, "IsWaitNotificationRequired"), Shim(SynchronizationContextShim, "IsWaitNotificationRequired", SynchronizationContextType));
        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.operationstarted",
            MemberSignature.Method(SynchronizationContextType, "OperationStarted"), Shim(SynchronizationContextShim, "OperationStarted", SynchronizationContextType));
        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.operationcompleted",
            MemberSignature.Method(SynchronizationContextType, "OperationCompleted"), Shim(SynchronizationContextShim, "OperationCompleted", SynchronizationContextType));
        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.post",
            MemberSignature.Method(SynchronizationContextType, "Post", SendOrPostCallbackType, ObjectType),
            Shim(SynchronizationContextShim, "Post", SynchronizationContextType, SendOrPostCallbackType, ObjectType));
        TaskRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.send",
            MemberSignature.Method(SynchronizationContextType, "Send", SendOrPostCallbackType, ObjectType),
            Shim(SynchronizationContextShim, "Send", SynchronizationContextType, SendOrPostCallbackType, ObjectType));
        RejectedRule(builder, BuiltInRuleFamily.SynchronizationContext, "clockwork.synchronizationcontext.wait",
            MemberSignature.Method(SynchronizationContextType, "Wait", IntPtrArray, Boolean, Int32),
            Shim(SynchronizationContextShim, "Wait", SynchronizationContextType, IntPtrArray, Boolean, Int32));
    }

    // wait-handle and atomic control: the full .NET 11 System.Threading.Interlocked surface. Every call site is redirected to a
    // shim carrying the identical ref-first signature. Under Clockwork's cooperative single-logical-thread
    // scheduler a read-modify-write is an indivisible step, so each shim delegates to the real primitive,
    // preserving exact atomic return / overflow / reference-write semantics. The generic And<T> / Or<T> /
    // Exchange<T> / CompareExchange<T> overloads use the `!!0`/`!!0&` target -> `T`/`T&` replacement split.
    private static void BuildInterlockedEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        void Rule(string id, string member, string[] target, string[] replacement) =>
            TaskRule(builder, BuiltInRuleFamily.Interlocked, id,
                MemberSignature.Method(InterlockedType, member, target), Shim(InterlockedShim, member, replacement));

        // Increment / Decrement (int, long, uint, ulong).
        Rule("clockwork.interlocked.increment.int32", "Increment", [Int32Ref], [Int32Ref]);
        Rule("clockwork.interlocked.increment.int64", "Increment", [Int64Ref], [Int64Ref]);
        Rule("clockwork.interlocked.increment.uint32", "Increment", [UInt32Ref], [UInt32Ref]);
        Rule("clockwork.interlocked.increment.uint64", "Increment", [UInt64Ref], [UInt64Ref]);
        Rule("clockwork.interlocked.decrement.int32", "Decrement", [Int32Ref], [Int32Ref]);
        Rule("clockwork.interlocked.decrement.int64", "Decrement", [Int64Ref], [Int64Ref]);
        Rule("clockwork.interlocked.decrement.uint32", "Decrement", [UInt32Ref], [UInt32Ref]);
        Rule("clockwork.interlocked.decrement.uint64", "Decrement", [UInt64Ref], [UInt64Ref]);

        // Add (int, long, uint, ulong).
        Rule("clockwork.interlocked.add.int32", "Add", [Int32Ref, Int32], [Int32Ref, Int32]);
        Rule("clockwork.interlocked.add.int64", "Add", [Int64Ref, Int64], [Int64Ref, Int64]);
        Rule("clockwork.interlocked.add.uint32", "Add", [UInt32Ref, UInt32], [UInt32Ref, UInt32]);
        Rule("clockwork.interlocked.add.uint64", "Add", [UInt64Ref, UInt64], [UInt64Ref, UInt64]);

        // And / Or (int, uint, long, ulong, generic value type).
        Rule("clockwork.interlocked.and.int32", "And", [Int32Ref, Int32], [Int32Ref, Int32]);
        Rule("clockwork.interlocked.and.uint32", "And", [UInt32Ref, UInt32], [UInt32Ref, UInt32]);
        Rule("clockwork.interlocked.and.int64", "And", [Int64Ref, Int64], [Int64Ref, Int64]);
        Rule("clockwork.interlocked.and.uint64", "And", [UInt64Ref, UInt64], [UInt64Ref, UInt64]);
        Rule("clockwork.interlocked.and.generic", "And", [GenericArg0Ref, GenericArg0], [GenericTRefDecl, GenericTDecl]);
        Rule("clockwork.interlocked.or.int32", "Or", [Int32Ref, Int32], [Int32Ref, Int32]);
        Rule("clockwork.interlocked.or.uint32", "Or", [UInt32Ref, UInt32], [UInt32Ref, UInt32]);
        Rule("clockwork.interlocked.or.int64", "Or", [Int64Ref, Int64], [Int64Ref, Int64]);
        Rule("clockwork.interlocked.or.uint64", "Or", [UInt64Ref, UInt64], [UInt64Ref, UInt64]);
        Rule("clockwork.interlocked.or.generic", "Or", [GenericArg0Ref, GenericArg0], [GenericTRefDecl, GenericTDecl]);

        // Exchange (every primitive, native-int, floating-point, reference, and generic).
        Rule("clockwork.interlocked.exchange.int32", "Exchange", [Int32Ref, Int32], [Int32Ref, Int32]);
        Rule("clockwork.interlocked.exchange.int64", "Exchange", [Int64Ref, Int64], [Int64Ref, Int64]);
        Rule("clockwork.interlocked.exchange.object", "Exchange", [ObjectRef, ObjectType], [ObjectRef, ObjectType]);
        Rule("clockwork.interlocked.exchange.sbyte", "Exchange", [SByteRef, SByteType], [SByteRef, SByteType]);
        Rule("clockwork.interlocked.exchange.int16", "Exchange", [Int16Ref, Int16Type], [Int16Ref, Int16Type]);
        Rule("clockwork.interlocked.exchange.byte", "Exchange", [ByteRef, ByteType], [ByteRef, ByteType]);
        Rule("clockwork.interlocked.exchange.uint16", "Exchange", [UInt16Ref, UInt16Type], [UInt16Ref, UInt16Type]);
        Rule("clockwork.interlocked.exchange.uint32", "Exchange", [UInt32Ref, UInt32], [UInt32Ref, UInt32]);
        Rule("clockwork.interlocked.exchange.uint64", "Exchange", [UInt64Ref, UInt64], [UInt64Ref, UInt64]);
        Rule("clockwork.interlocked.exchange.single", "Exchange", [SingleRef, SingleType], [SingleRef, SingleType]);
        Rule("clockwork.interlocked.exchange.double", "Exchange", [DoubleRef, DoubleType], [DoubleRef, DoubleType]);
        Rule("clockwork.interlocked.exchange.intptr", "Exchange", [IntPtrRef, IntPtrType], [IntPtrRef, IntPtrType]);
        Rule("clockwork.interlocked.exchange.uintptr", "Exchange", [UIntPtrRef, UIntPtrType], [UIntPtrRef, UIntPtrType]);
        Rule("clockwork.interlocked.exchange.generic", "Exchange", [GenericArg0Ref, GenericArg0], [GenericTRefDecl, GenericTDecl]);

        // CompareExchange (every primitive, native-int, floating-point, reference, and generic).
        Rule("clockwork.interlocked.compareexchange.int32", "CompareExchange", [Int32Ref, Int32, Int32], [Int32Ref, Int32, Int32]);
        Rule("clockwork.interlocked.compareexchange.int64", "CompareExchange", [Int64Ref, Int64, Int64], [Int64Ref, Int64, Int64]);
        Rule("clockwork.interlocked.compareexchange.object", "CompareExchange", [ObjectRef, ObjectType, ObjectType], [ObjectRef, ObjectType, ObjectType]);
        Rule("clockwork.interlocked.compareexchange.sbyte", "CompareExchange", [SByteRef, SByteType, SByteType], [SByteRef, SByteType, SByteType]);
        Rule("clockwork.interlocked.compareexchange.int16", "CompareExchange", [Int16Ref, Int16Type, Int16Type], [Int16Ref, Int16Type, Int16Type]);
        Rule("clockwork.interlocked.compareexchange.byte", "CompareExchange", [ByteRef, ByteType, ByteType], [ByteRef, ByteType, ByteType]);
        Rule("clockwork.interlocked.compareexchange.uint16", "CompareExchange", [UInt16Ref, UInt16Type, UInt16Type], [UInt16Ref, UInt16Type, UInt16Type]);
        Rule("clockwork.interlocked.compareexchange.uint32", "CompareExchange", [UInt32Ref, UInt32, UInt32], [UInt32Ref, UInt32, UInt32]);
        Rule("clockwork.interlocked.compareexchange.uint64", "CompareExchange", [UInt64Ref, UInt64, UInt64], [UInt64Ref, UInt64, UInt64]);
        Rule("clockwork.interlocked.compareexchange.single", "CompareExchange", [SingleRef, SingleType, SingleType], [SingleRef, SingleType, SingleType]);
        Rule("clockwork.interlocked.compareexchange.double", "CompareExchange", [DoubleRef, DoubleType, DoubleType], [DoubleRef, DoubleType, DoubleType]);
        Rule("clockwork.interlocked.compareexchange.intptr", "CompareExchange", [IntPtrRef, IntPtrType, IntPtrType], [IntPtrRef, IntPtrType, IntPtrType]);
        Rule("clockwork.interlocked.compareexchange.uintptr", "CompareExchange", [UIntPtrRef, UIntPtrType, UIntPtrType], [UIntPtrRef, UIntPtrType, UIntPtrType]);
        Rule("clockwork.interlocked.compareexchange.generic", "CompareExchange", [GenericArg0Ref, GenericArg0, GenericArg0], [GenericTRefDecl, GenericTDecl, GenericTDecl]);

        // Read (long, ulong) and the memory barriers.
        Rule("clockwork.interlocked.read.int64", "Read", [Int64Ref], [Int64Ref]);
        Rule("clockwork.interlocked.read.uint64", "Read", [UInt64Ref], [UInt64Ref]);
        Rule("clockwork.interlocked.memorybarrier", "MemoryBarrier", [], []);
        Rule("clockwork.interlocked.memorybarrierprocesswide", "MemoryBarrierProcessWide", [], []);
    }

    // wait-handle and atomic control: the full .NET 11 System.Threading.Volatile surface. Each Read/Write call site is redirected
    // to a shim with the identical ref-first signature; the generic Read<T>/Write<T> overloads use the
    // `!!0`/`!!0&` target -> `T`/`T&` replacement split. Under the cooperative single-logical-thread
    // scheduler a volatile access is an indivisible step, so each shim delegates to the real primitive,
    // preserving the exact value and the acquire (read) / release (write) fence intent.
    private static void BuildVolatileEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        void Rule(string id, string member, string[] target, string[] replacement) =>
            TaskRule(builder, BuiltInRuleFamily.Volatile, id,
                MemberSignature.Method(VolatileType, member, target), Shim(VolatileShim, member, replacement));

        // Read (every primitive, native-int, floating-point, generic reference).
        Rule("clockwork.volatile.read.boolean", "Read", [BooleanRef], [BooleanRef]);
        Rule("clockwork.volatile.read.sbyte", "Read", [SByteRef], [SByteRef]);
        Rule("clockwork.volatile.read.byte", "Read", [ByteRef], [ByteRef]);
        Rule("clockwork.volatile.read.int16", "Read", [Int16Ref], [Int16Ref]);
        Rule("clockwork.volatile.read.uint16", "Read", [UInt16Ref], [UInt16Ref]);
        Rule("clockwork.volatile.read.int32", "Read", [Int32Ref], [Int32Ref]);
        Rule("clockwork.volatile.read.uint32", "Read", [UInt32Ref], [UInt32Ref]);
        Rule("clockwork.volatile.read.int64", "Read", [Int64Ref], [Int64Ref]);
        Rule("clockwork.volatile.read.uint64", "Read", [UInt64Ref], [UInt64Ref]);
        Rule("clockwork.volatile.read.intptr", "Read", [IntPtrRef], [IntPtrRef]);
        Rule("clockwork.volatile.read.uintptr", "Read", [UIntPtrRef], [UIntPtrRef]);
        Rule("clockwork.volatile.read.single", "Read", [SingleRef], [SingleRef]);
        Rule("clockwork.volatile.read.double", "Read", [DoubleRef], [DoubleRef]);
        Rule("clockwork.volatile.read.generic", "Read", [GenericArg0Ref], [GenericTRefDecl]);

        // Write (every primitive, native-int, floating-point, generic reference).
        Rule("clockwork.volatile.write.boolean", "Write", [BooleanRef, Boolean], [BooleanRef, Boolean]);
        Rule("clockwork.volatile.write.sbyte", "Write", [SByteRef, SByteType], [SByteRef, SByteType]);
        Rule("clockwork.volatile.write.byte", "Write", [ByteRef, ByteType], [ByteRef, ByteType]);
        Rule("clockwork.volatile.write.int16", "Write", [Int16Ref, Int16Type], [Int16Ref, Int16Type]);
        Rule("clockwork.volatile.write.uint16", "Write", [UInt16Ref, UInt16Type], [UInt16Ref, UInt16Type]);
        Rule("clockwork.volatile.write.int32", "Write", [Int32Ref, Int32], [Int32Ref, Int32]);
        Rule("clockwork.volatile.write.uint32", "Write", [UInt32Ref, UInt32], [UInt32Ref, UInt32]);
        Rule("clockwork.volatile.write.int64", "Write", [Int64Ref, Int64], [Int64Ref, Int64]);
        Rule("clockwork.volatile.write.uint64", "Write", [UInt64Ref, UInt64], [UInt64Ref, UInt64]);
        Rule("clockwork.volatile.write.intptr", "Write", [IntPtrRef, IntPtrType], [IntPtrRef, IntPtrType]);
        Rule("clockwork.volatile.write.uintptr", "Write", [UIntPtrRef, UIntPtrType], [UIntPtrRef, UIntPtrType]);
        Rule("clockwork.volatile.write.single", "Write", [SingleRef, SingleType], [SingleRef, SingleType]);
        Rule("clockwork.volatile.write.double", "Write", [DoubleRef, DoubleType], [DoubleRef, DoubleType]);
        Rule("clockwork.volatile.write.generic", "Write", [GenericArg0Ref, GenericArg0], [GenericTRefDecl, GenericTDecl]);

        // Acquire / release fences.
        Rule("clockwork.volatile.readbarrier", "ReadBarrier", [], []);
        Rule("clockwork.volatile.writebarrier", "WriteBarrier", [], []);
    }

    // wait-handle and atomic control: the controlled event / wait-handle surface. AutoResetEvent, ManualResetEvent and
    // EventWaitHandle are concrete sealed classes, so the real objects are retained as identity handles and
    // side state lives in a ConditionalWeakTable keyed by that object. Each `new` is redirected to a Create
    // factory; every instance member is a receiver-first static shim (the WaitHandle receiver is prepended).
    // WaitOne / Dispose / Close / Handle / SafeWaitHandle are declared on the WaitHandle base and shimmed on
    // ControlledWaitHandle; Set / Reset are declared on EventWaitHandle and shimmed on
    // ControlledEventWaitHandle. Named / cross-process APIs (named constructors, OpenExisting,
    // TryOpenExisting) and the raw-handle accessors are rejected: a single simulation process cannot model a
    // system-wide kernel object, and a controlled event exposes no native handle.
    private static void BuildWaitHandleEntries(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        // ---- constructors -> Create factories (RedirectNewObj) ----
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.WaitHandle, RewriteRule.RedirectNewObj(
            "clockwork.autoresetevent.ctor",
            MemberSignature.Constructor(AutoResetEventType, Boolean),
            Shim(EventWaitHandleShim, "CreateAutoResetEvent", Boolean))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.WaitHandle, RewriteRule.RedirectNewObj(
            "clockwork.manualresetevent.ctor",
            MemberSignature.Constructor(ManualResetEventType, Boolean),
            Shim(EventWaitHandleShim, "CreateManualResetEvent", Boolean))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.WaitHandle, RewriteRule.RedirectNewObj(
            "clockwork.eventwaithandle.ctor.mode",
            MemberSignature.Constructor(EventWaitHandleType, Boolean, EventResetModeType),
            Shim(EventWaitHandleShim, "CreateEvent", Boolean, EventResetModeType))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.WaitHandle, RewriteRule.RedirectNewObj(
            "clockwork.eventwaithandle.ctor.named",
            MemberSignature.Constructor(EventWaitHandleType, Boolean, EventResetModeType, String),
            Shim(EventWaitHandleShim, "CreateNamedEvent", Boolean, EventResetModeType, String))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.WaitHandle, RewriteRule.RedirectNewObj(
            "clockwork.eventwaithandle.ctor.named.creatednew",
            MemberSignature.Constructor(EventWaitHandleType, Boolean, EventResetModeType, String, BooleanRef),
            Shim(EventWaitHandleShim, "CreateNamedEvent", Boolean, EventResetModeType, String, BooleanRef))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.WaitHandle, RewriteRule.RedirectNewObj(
            "clockwork.eventwaithandle.ctor.named.options",
            MemberSignature.Constructor(EventWaitHandleType, Boolean, EventResetModeType, String, NamedWaitHandleOptionsType),
            Shim(EventWaitHandleShim, "CreateNamedEvent", Boolean, EventResetModeType, String, NamedWaitHandleOptionsType))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.WaitHandle, RewriteRule.RedirectNewObj(
            "clockwork.eventwaithandle.ctor.named.options.creatednew",
            MemberSignature.Constructor(EventWaitHandleType, Boolean, EventResetModeType, String, NamedWaitHandleOptionsType, BooleanRef),
            Shim(EventWaitHandleShim, "CreateNamedEvent", Boolean, EventResetModeType, String, NamedWaitHandleOptionsType, BooleanRef))));

        // ---- WaitHandle.WaitOne overloads -> receiver-first controlled kernel ----
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitone",
            MemberSignature.Method(WaitHandleType, "WaitOne"), Shim(WaitHandleShim, "WaitOne", WaitHandleType));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitone.milliseconds",
            MemberSignature.Method(WaitHandleType, "WaitOne", Int32), Shim(WaitHandleShim, "WaitOne", WaitHandleType, Int32));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitone.timespan",
            MemberSignature.Method(WaitHandleType, "WaitOne", TimeSpan), Shim(WaitHandleShim, "WaitOne", WaitHandleType, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitone.milliseconds.exitcontext",
            MemberSignature.Method(WaitHandleType, "WaitOne", Int32, Boolean), Shim(WaitHandleShim, "WaitOne", WaitHandleType, Int32, Boolean));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitone.timespan.exitcontext",
            MemberSignature.Method(WaitHandleType, "WaitOne", TimeSpan, Boolean), Shim(WaitHandleShim, "WaitOne", WaitHandleType, TimeSpan, Boolean));

        // ---- WaitHandle.WaitAny -> controlled multi-handle kernel (first-signalled, lowest index) ----
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitany",
            MemberSignature.Method(WaitHandleType, "WaitAny", WaitHandleArray), Shim(WaitHandleShim, "WaitAny", WaitHandleArray));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitany.milliseconds",
            MemberSignature.Method(WaitHandleType, "WaitAny", WaitHandleArray, Int32), Shim(WaitHandleShim, "WaitAny", WaitHandleArray, Int32));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitany.timespan",
            MemberSignature.Method(WaitHandleType, "WaitAny", WaitHandleArray, TimeSpan), Shim(WaitHandleShim, "WaitAny", WaitHandleArray, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitany.milliseconds.exitcontext",
            MemberSignature.Method(WaitHandleType, "WaitAny", WaitHandleArray, Int32, Boolean), Shim(WaitHandleShim, "WaitAny", WaitHandleArray, Int32, Boolean));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitany.timespan.exitcontext",
            MemberSignature.Method(WaitHandleType, "WaitAny", WaitHandleArray, TimeSpan, Boolean), Shim(WaitHandleShim, "WaitAny", WaitHandleArray, TimeSpan, Boolean));

        // ---- WaitHandle.WaitAll -> controlled multi-handle kernel (all-signalled, atomic consume) ----
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitall",
            MemberSignature.Method(WaitHandleType, "WaitAll", WaitHandleArray), Shim(WaitHandleShim, "WaitAll", WaitHandleArray));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitall.milliseconds",
            MemberSignature.Method(WaitHandleType, "WaitAll", WaitHandleArray, Int32), Shim(WaitHandleShim, "WaitAll", WaitHandleArray, Int32));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitall.timespan",
            MemberSignature.Method(WaitHandleType, "WaitAll", WaitHandleArray, TimeSpan), Shim(WaitHandleShim, "WaitAll", WaitHandleArray, TimeSpan));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitall.milliseconds.exitcontext",
            MemberSignature.Method(WaitHandleType, "WaitAll", WaitHandleArray, Int32, Boolean), Shim(WaitHandleShim, "WaitAll", WaitHandleArray, Int32, Boolean));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.waitall.timespan.exitcontext",
            MemberSignature.Method(WaitHandleType, "WaitAll", WaitHandleArray, TimeSpan, Boolean), Shim(WaitHandleShim, "WaitAll", WaitHandleArray, TimeSpan, Boolean));

        // ---- WaitHandle.SignalAndWait -> atomic signal-then-wait on the controlled kernel ----
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.signalandwait",
            MemberSignature.Method(WaitHandleType, "SignalAndWait", WaitHandleType, WaitHandleType), Shim(WaitHandleShim, "SignalAndWait", WaitHandleType, WaitHandleType));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.signalandwait.milliseconds.exitcontext",
            MemberSignature.Method(WaitHandleType, "SignalAndWait", WaitHandleType, WaitHandleType, Int32, Boolean), Shim(WaitHandleShim, "SignalAndWait", WaitHandleType, WaitHandleType, Int32, Boolean));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.signalandwait.timespan.exitcontext",
            MemberSignature.Method(WaitHandleType, "SignalAndWait", WaitHandleType, WaitHandleType, TimeSpan, Boolean), Shim(WaitHandleShim, "SignalAndWait", WaitHandleType, WaitHandleType, TimeSpan, Boolean));

        // ---- WaitHandle disposal ----
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.dispose",
            MemberSignature.Method(WaitHandleType, "Dispose"), Shim(WaitHandleShim, "Dispose", WaitHandleType));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.close",
            MemberSignature.Method(WaitHandleType, "Close"), Shim(WaitHandleShim, "Close", WaitHandleType));

        // ---- raw-handle accessors: rejected (a controlled event has no native handle) ----
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.get_handle",
            MemberSignature.Method(WaitHandleType, "get_Handle"), Shim(WaitHandleShim, "GetHandle", WaitHandleType));
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.set_handle",
            MemberSignature.Method(WaitHandleType, "set_Handle", IntPtrType), Shim(WaitHandleShim, "SetHandle", WaitHandleType, IntPtrType));
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.get_safewaithandle",
            MemberSignature.Method(WaitHandleType, "get_SafeWaitHandle"), Shim(WaitHandleShim, "GetSafeWaitHandle", WaitHandleType));
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.waithandle.set_safewaithandle",
            MemberSignature.Method(WaitHandleType, "set_SafeWaitHandle", SafeWaitHandleType), Shim(WaitHandleShim, "SetSafeWaitHandle", WaitHandleType, SafeWaitHandleType));

        // ---- EventWaitHandle.Set / Reset -> receiver-first controlled signalling ----
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.eventwaithandle.set",
            MemberSignature.Method(EventWaitHandleType, "Set"), Shim(EventWaitHandleShim, "Set", EventWaitHandleType));
        TaskRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.eventwaithandle.reset",
            MemberSignature.Method(EventWaitHandleType, "Reset"), Shim(EventWaitHandleShim, "Reset", EventWaitHandleType));

        // ---- named / cross-process open APIs: rejected ----
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.eventwaithandle.openexisting",
            MemberSignature.Method(EventWaitHandleType, "OpenExisting", String), Shim(EventWaitHandleShim, "OpenExisting", String));
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.eventwaithandle.openexisting.options",
            MemberSignature.Method(EventWaitHandleType, "OpenExisting", String, NamedWaitHandleOptionsType), Shim(EventWaitHandleShim, "OpenExisting", String, NamedWaitHandleOptionsType));
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.eventwaithandle.tryopenexisting",
            MemberSignature.Method(EventWaitHandleType, "TryOpenExisting", String, EventWaitHandleRef), Shim(EventWaitHandleShim, "TryOpenExisting", String, EventWaitHandleRef));
        RejectedRule(builder, BuiltInRuleFamily.WaitHandle, "clockwork.eventwaithandle.tryopenexisting.options",
            MemberSignature.Method(EventWaitHandleType, "TryOpenExisting", String, NamedWaitHandleOptionsType, EventWaitHandleRef), Shim(EventWaitHandleShim, "TryOpenExisting", String, NamedWaitHandleOptionsType, EventWaitHandleRef));
    }

    // Rejects a Parallel overload at the call site. The BCL methods return ParallelLoopResult, so
    // InjectRejection is used (it prepends a throwing RejectUnsupported(string) before the original call,
    // keeping the value-returning invocation in place for stack balance).
    private static void ParallelRejection(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string method,
        params string[] parameterTypes)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Parallel, RewriteRule.InjectRejection(
            id,
            MemberSignature.Method(ParallelType, method, parameterTypes),
            Shim(ParallelShim, "RejectUnsupported", String))));
    }

    // Redirects an asynchronous member of Stream, TextReader, or TextWriter to its receiver-first shim. A virtual member's
    // base calls (a non-virtual `call` from an override) redirect to the shim's `Base` form, which runs the base
    // algorithm, since dispatching would re-enter the override.
    private static void AsyncIORule(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string declaringType,
        string shimType,
        string member,
        bool isVirtual,
        params string[] parameterTypes)
    {
        string[] shimParameters = [declaringType, .. parameterTypes];
        RewriteRule rule = RewriteRule.RedirectCall(
            id,
            MemberSignature.Method(declaringType, member, parameterTypes),
            Shim(shimType, member, shimParameters));
        builder.Add(new BuiltInRuleEntry(
            BuiltInRuleFamily.AsyncIO,
            isVirtual ? rule with { BaseCallReplacement = Shim(shimType, "Base" + member, shimParameters) } : rule));
    }

    // Redirects an instance member of BlockingCollection<T> to its receiver-first generic shim.
    private static void BlockingCollectionInstanceRule(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string member,
        string[] targetParameters,
        string[] shimParameters)
    {
        TaskRule(builder, BuiltInRuleFamily.BlockingCollection, id,
            MemberSignature.Method(BlockingCollectionType, member, targetParameters),
            Shim(BlockingCollectionShim, member, [BlockingCollectionTDecl, .. shimParameters]));
    }

    // Redirects a static *Any member of BlockingCollection<T>, whose first two parameters are the collections and the
    // item, to the generic shim of the same shape; the declaring type's argument binds the shim's T.
    private static void BlockingCollectionStaticRule(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string member,
        string item,
        params string[] rest)
    {
        string itemDecl = item == TypeArg0Ref ? GenericTRefDecl : GenericTDecl;
        TaskRule(builder, BuiltInRuleFamily.BlockingCollection, id,
            MemberSignature.Method(BlockingCollectionType, member, [BlockingCollectionArrayVar, item, .. rest]),
            Shim(BlockingCollectionShim, member, [BlockingCollectionArrayDecl, itemDecl, .. rest]));
    }

    // Rejects a PLINQ entry point at the call site. The targets return a ParallelQuery, so InjectRejection prepends the
    // throwing RejectUnsupported(string) and keeps the original call for stack balance.
    // Rejects a call site with a shim's RejectUnsupported(string), which InjectRejection prepends to the original call.
    private static void CallSiteRejection(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        BuiltInRuleFamily family,
        string id,
        MemberSignature target,
        string shimType)
    {
        builder.Add(new BuiltInRuleEntry(family, RewriteRule.InjectRejection(id, target, Shim(shimType, "RejectUnsupported", String))));
    }

    // The overloads of an HttpClient member which takes a string or Uri request URI, then `parameters`, with and without
    // a trailing CancellationToken.
    private static IEnumerable<(string Member, string[] Parameters)> HttpUriSends(string member, string[] parameters)
    {
        foreach (string uri in (string[])[String, UriType])
        {
            yield return (member, [uri, .. parameters]);
            yield return (member, [uri, .. parameters, CancellationToken]);
        }
    }

    // The overloads of HttpClient.Send or SendAsync: a request, with and without a completion option and a token.
    private static IEnumerable<(string Member, string[] Parameters)> HttpRequestSends(string member) =>
    [
        (member, [HttpRequestMessageType]),
        (member, [HttpRequestMessageType, HttpCompletionOptionType]),
        (member, [HttpRequestMessageType, CancellationToken]),
        (member, [HttpRequestMessageType, HttpCompletionOptionType, CancellationToken]),
    ];

    // The overloads of an HttpClientJsonExtensions member which reads a value: by Type with options, a context, or
    // neither, and generically with options, type information, or neither.
    private static IEnumerable<(string Member, string[] Parameters)> HttpJsonReads(string member) =>
        HttpJsonOverloads(member, [[SystemType, JsonOptionsType], [SystemType, JsonContextType], [SystemType], [JsonOptionsType], [JsonTypeInfoOfValue], []]);

    // The overloads of an HttpClientJsonExtensions member which writes a value: with options, type information, or neither.
    private static IEnumerable<(string Member, string[] Parameters)> HttpJsonWrites(string member) =>
        HttpJsonOverloads(member, [[MethodTypeArg0, JsonOptionsType], [MethodTypeArg0, JsonTypeInfoOfValue], [MethodTypeArg0]]);

    // The overloads of an HttpClientJsonExtensions member which enumerates values: with options, type information, or neither.
    private static IEnumerable<(string Member, string[] Parameters)> HttpJsonEnumerations(string member) =>
        HttpJsonOverloads(member, [[JsonOptionsType], [JsonTypeInfoOfValue], []]);

    // Each shape after the client and a string or Uri request URI, with a trailing CancellationToken.
    private static IEnumerable<(string Member, string[] Parameters)> HttpJsonOverloads(string member, string[][] shapes)
    {
        foreach (string uri in (string[])[String, UriType])
        {
            foreach (string[] shape in shapes)
            {
                yield return (member, [HttpClientType, uri, .. shape, CancellationToken]);
            }
        }
    }

    // A rule id naming an HTTP member's overload by its parameters: "clockwork.http.httpclient.getasync.uri.cancellationtoken".
    private static string HttpRuleId(string prefix, string member, string[] parameters) =>
        prefix + member.ToLowerInvariant() + string.Concat(parameters
            .Where(static parameter => parameter != HttpClientType)
            .Select(static parameter => "." + parameter switch
            {
                String => "string",
                UriType => "uri",
                CancellationToken => "cancellationtoken",
                HttpCompletionOptionType => "completionoption",
                HttpContentType => "content",
                HttpRequestMessageType => "request",
                SystemType => "type",
                JsonOptionsType => "options",
                JsonContextType => "context",
                JsonTypeInfoOfValue => "typeinfo",
                MethodTypeArg0 => "value",
                _ => throw new ArgumentOutOfRangeException(nameof(parameters), parameter, "An unexpected HTTP member parameter."),
            }));


    private static void PlinqRejection(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string method,
        params string[] parameterTypes)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Plinq, RewriteRule.InjectRejection(
            id,
            MemberSignature.Method(ParallelEnumerableType, method, parameterTypes),
            Shim(ParallelEnumerableShim, "RejectUnsupported", String))));
    }

    // Rejects an uncontrolled process/termination call at the call site. The targets have varied return
    // types (Process, Boolean, Task, void), so InjectRejection is used uniformly: it prepends a throwing
    // Reject(string) before the original invocation, which therefore never executes at runtime while the
    // IL stack stays balanced. The injected diagnostic names the exact API and the pass records the site
    // as a Rejected transformation in the manifest.
    private static void UncontrolledRejection(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string declaringType,
        string method,
        params string[] parameterTypes)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.UncontrolledInvocation, RewriteRule.InjectRejection(
            id,
            MemberSignature.Method(declaringType, method, parameterTypes),
            Shim(UncontrolledInvocationShim, "Reject", String))));
    }

    private static void RejectedRule(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        BuiltInRuleFamily family,
        string id,
        MemberSignature target,
        RewriteReplacement replacement)
    {
        builder.Add(new BuiltInRuleEntry(family, RewriteRule.RedirectCall(id, target, replacement, SimulationApiPolicy.Rejected)));
    }

    private static void RejectedNewObjRule(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        BuiltInRuleFamily family,
        string id,
        MemberSignature target,
        RewriteReplacement replacement)
    {
        builder.Add(new BuiltInRuleEntry(family, RewriteRule.RedirectNewObj(id, target, replacement, SimulationApiPolicy.Rejected)));
    }

    // Redirects a registered-wait factory overload to its controlled shim. The BCL method is static, so the
    // shim signature matches the target parameters exactly (no receiver prepended); the shim returns the
    // controlled RegisteredWaitHandle, which composes with the whole-type substitution of the receiving local.
    private static void RegisterWaitRedirect(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string method,
        string timeoutType)
    {
        TaskRule(builder, BuiltInRuleFamily.ThreadPool, id,
            MemberSignature.Method(ThreadPoolType, method, WaitHandle, WaitOrTimerCallback, ObjectType, timeoutType, Boolean),
            Shim(ThreadPoolShim, method, WaitHandle, WaitOrTimerCallback, ObjectType, timeoutType, Boolean));
    }

    private static void Sub(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string targetTypeFullName,
        string controlledTypeFullName) =>
        Sub(builder, BuiltInRuleFamily.AsyncMachinery, id, targetTypeFullName, controlledTypeFullName);

    private static void Sub(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        BuiltInRuleFamily family,
        string id,
        string targetTypeFullName,
        string controlledTypeFullName)
    {
        builder.Add(new BuiltInRuleEntry(family, RewriteRule.SubstituteType(
            id, targetTypeFullName, RewriteReplacement.Type(ShimAssemblyName, controlledTypeFullName))));
    }

    // Every Task and Task<TResult> constructor redirects to the matching ControlledTask.Create factory, whose
    // parameters are the constructor's, with TResult for the type parameter.
    private static void AddTaskConstructorRules(ImmutableArray<BuiltInRuleEntry>.Builder builder)
    {
        string[][] taskShapes =
        [
            [Action],
            [Action, CancellationTokenType],
            [Action, TaskCreationOptions],
            [Action, CancellationTokenType, TaskCreationOptions],
            [ActionOfObject, ObjectType],
            [ActionOfObject, ObjectType, CancellationTokenType],
            [ActionOfObject, ObjectType, TaskCreationOptions],
            [ActionOfObject, ObjectType, CancellationTokenType, TaskCreationOptions],
        ];
        foreach (string[] parameters in taskShapes)
        {
            builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.TaskScheduling, RewriteRule.RedirectNewObj(
                "clockwork.tasks.ctor" + ConstructorRuleSuffix(parameters),
                MemberSignature.Constructor(Task, parameters),
                Shim(TaskShim, "Create", parameters))));
        }

        (string Target, string Shim)[][] genericShapes =
        [
            [("System.Func`1<!0>", FuncOfResultDecl)],
            [("System.Func`1<!0>", FuncOfResultDecl), (CancellationTokenType, CancellationTokenType)],
            [("System.Func`1<!0>", FuncOfResultDecl), (TaskCreationOptions, TaskCreationOptions)],
            [("System.Func`1<!0>", FuncOfResultDecl), (CancellationTokenType, CancellationTokenType), (TaskCreationOptions, TaskCreationOptions)],
            [("System.Func`2<System.Object,!0>", "System.Func`2<System.Object,TResult>"), (ObjectType, ObjectType)],
            [("System.Func`2<System.Object,!0>", "System.Func`2<System.Object,TResult>"), (ObjectType, ObjectType), (CancellationTokenType, CancellationTokenType)],
            [("System.Func`2<System.Object,!0>", "System.Func`2<System.Object,TResult>"), (ObjectType, ObjectType), (TaskCreationOptions, TaskCreationOptions)],
            [("System.Func`2<System.Object,!0>", "System.Func`2<System.Object,TResult>"), (ObjectType, ObjectType), (CancellationTokenType, CancellationTokenType), (TaskCreationOptions, TaskCreationOptions)],
        ];
        foreach ((string Target, string Shim)[] parameters in genericShapes)
        {
            string[] targetParameters = [.. parameters.Select(static parameter => parameter.Target)];
            builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.TaskScheduling, RewriteRule.RedirectNewObj(
                "clockwork.tasks.generic.ctor" + ConstructorRuleSuffix(targetParameters),
                MemberSignature.Constructor(TaskTType, targetParameters),
                Shim(TaskShim, "Create", [.. parameters.Select(static parameter => parameter.Shim)]))));
        }
    }

    // A rule-id suffix naming a constructor's shape: ".func", ".action.state.cancellationtoken", and so on.
    private static string ConstructorRuleSuffix(string[] parameters) =>
        string.Concat(parameters.Select(static parameter => parameter switch
        {
            Action or "System.Func`1<!0>" => ".action",
            ActionOfObject or "System.Func`2<System.Object,!0>" => ".action.state",
            ObjectType => string.Empty,
            CancellationTokenType => ".cancellationtoken",
            TaskCreationOptions => ".options",
            _ => throw new ArgumentOutOfRangeException(nameof(parameters), parameter, "An unexpected Task constructor parameter."),
        }));
    private static void TaskRule(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        BuiltInRuleFamily family,
        string id,
        MemberSignature target,
        RewriteReplacement replacement)
    {
        builder.Add(new BuiltInRuleEntry(family, RewriteRule.RedirectCall(id, target, replacement)));
    }

    // Redirects a virtual channel member to a shim which dispatches to an override, and its base calls to the shim's
    // `Base` form, which runs the base algorithm.
    private static void ChannelRule(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string declaringType,
        string member,
        string shimType,
        string[] targetParameters,
        string[] shimParameters)
    {
        builder.Add(new BuiltInRuleEntry(
            BuiltInRuleFamily.Channels,
            RewriteRule.RedirectCall(id, MemberSignature.Method(declaringType, member, targetParameters), Shim(shimType, member, shimParameters))
                with
            {
                BaseCallReplacement = Shim(shimType, "Base" + member, shimParameters),
            }));
    }

    // Adds the five overloads of one ContinueWith shape: the plain form, then the CancellationToken, TaskScheduler,
    // TaskContinuationOptions, and full (CancellationToken, TaskContinuationOptions, TaskScheduler) forms, after the
    // continuation delegate and, for a state-carrying shape, its state.
    private static void ContinueWithRules(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string declaringType,
        string receiver,
        string continuation,
        string shimContinuation,
        bool hasState)
    {
        string[] target = hasState ? [continuation, ObjectType] : [continuation];
        string[] shim = hasState ? [receiver, shimContinuation, ObjectType] : [receiver, shimContinuation];
        (string Suffix, string[] Parameters)[] forms =
        [
            (string.Empty, []),
            (".cancellationtoken", [CancellationToken]),
            (".scheduler", [TaskSchedulerType]),
            (".options", [TaskContinuationOptions]),
            (".cancellationtoken.options.scheduler", [CancellationToken, TaskContinuationOptions, TaskSchedulerType]),
        ];

        foreach ((string suffix, string[] parameters) in forms)
        {
            TaskRule(builder, BuiltInRuleFamily.TaskContinuations, id + suffix,
                MemberSignature.Method(declaringType, "ContinueWith", [.. target, .. parameters]),
                Shim(TaskShim, "ContinueWith", [.. shim, .. parameters]));
        }
    }

    // Adds the four overloads of one ContinueWhenAll or ContinueWhenAny shape: the plain form, then the CancellationToken,
    // TaskContinuationOptions, and full (CancellationToken, TaskContinuationOptions, TaskScheduler) forms, after the
    // antecedents and the continuation delegate. Each redirects to the receiver-first shim of the same name.
    private static void MultiTaskContinuationRules(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string declaringType,
        string receiver,
        string member,
        string tasks,
        string shimTasks,
        string continuation,
        string shimContinuation)
    {
        (string Suffix, string[] Parameters)[] forms =
        [
            (string.Empty, []),
            (".cancellationtoken", [CancellationToken]),
            (".options", [TaskContinuationOptions]),
            (".cancellationtoken.options.scheduler", [CancellationToken, TaskContinuationOptions, TaskSchedulerType]),
        ];

        foreach ((string suffix, string[] parameters) in forms)
        {
            TaskRule(builder, BuiltInRuleFamily.TaskFactoryContinuations, id + suffix,
                MemberSignature.Method(declaringType, member, [tasks, continuation, .. parameters]),
                Shim(TaskFactoryShim, member, [receiver, shimTasks, shimContinuation, .. parameters]));
        }
    }

    // Adds the twelve completion members of one TaskCompletionSource type, each redirected to the receiver-first shim
    // of the same name.
    private static void CompletionSourceRules(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string declaringType,
        string receiver,
        string[] result,
        string[] shimResult,
        string completedTask,
        string shimCompletedTask)
    {
        (string Suffix, string Member, string[] Parameters, string[] ShimParameters)[] members =
        [
            (".setresult", "SetResult", result, shimResult),
            (".trysetresult", "TrySetResult", result, shimResult),
            (".setexception", "SetException", [ExceptionTypeName], [ExceptionTypeName]),
            (".setexception.enumerable", "SetException", [IEnumerableOfException], [IEnumerableOfException]),
            (".trysetexception", "TrySetException", [ExceptionTypeName], [ExceptionTypeName]),
            (".trysetexception.enumerable", "TrySetException", [IEnumerableOfException], [IEnumerableOfException]),
            (".setcanceled", "SetCanceled", [], []),
            (".setcanceled.cancellationtoken", "SetCanceled", [CancellationToken], [CancellationToken]),
            (".trysetcanceled", "TrySetCanceled", [], []),
            (".trysetcanceled.cancellationtoken", "TrySetCanceled", [CancellationToken], [CancellationToken]),
            (".setfromtask", "SetFromTask", [completedTask], [shimCompletedTask]),
            (".trysetfromtask", "TrySetFromTask", [completedTask], [shimCompletedTask]),
        ];

        foreach ((string suffix, string member, string[] parameters, string[] shimParameters) in members)
        {
            TaskRule(builder, BuiltInRuleFamily.TaskCompletionSource, id + suffix,
                MemberSignature.Method(declaringType, member, parameters),
                Shim(TaskCompletionSourceShim, member, [receiver, .. shimParameters]));
        }
    }

    private static ImmutableArray<BuiltInRuleEntry> BuildDeterministicBclEntries()
    {
        var builder = ImmutableArray.CreateBuilder<BuiltInRuleEntry>();

        // ---- Clock family: wall-clock, offset clock, monotonic timestamp, tick counters ----
        Clock(builder, "clockwork.bcl.datetime.now", "System.DateTime", "get_Now", DateTimeShim, "GetNow");
        Clock(builder, "clockwork.bcl.datetime.utcnow", "System.DateTime", "get_UtcNow", DateTimeShim, "GetUtcNow");
        Clock(builder, "clockwork.bcl.datetime.today", "System.DateTime", "get_Today", DateTimeShim, "GetToday");
        Clock(builder, "clockwork.bcl.datetimeoffset.now", "System.DateTimeOffset", "get_Now", DateTimeOffsetShim, "GetNow");
        Clock(builder, "clockwork.bcl.datetimeoffset.utcnow", "System.DateTimeOffset", "get_UtcNow", DateTimeOffsetShim, "GetUtcNow");
        Clock(builder, "clockwork.bcl.stopwatch.gettimestamp", "System.Diagnostics.Stopwatch", "GetTimestamp", StopwatchShim, "GetTimestamp");
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Clock, RewriteRule.RedirectCall(
            "clockwork.bcl.stopwatch.getelapsedtime",
            MemberSignature.Method("System.Diagnostics.Stopwatch", "GetElapsedTime", Int64),
            Shim(StopwatchShim, "GetElapsedTime", Int64))));
        Clock(builder, "clockwork.bcl.environment.tickcount", "System.Environment", "get_TickCount", EnvironmentShim, "GetTickCount");
        Clock(builder, "clockwork.bcl.environment.tickcount64", "System.Environment", "get_TickCount64", EnvironmentShim, "GetTickCount64");

        // ---- Guid family: deterministic identity bytes with preserved RFC variant/version ----
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Identity, RewriteRule.RedirectCall(
            "clockwork.bcl.guid.newguid",
            MemberSignature.Method("System.Guid", "NewGuid"),
            Shim(GuidShim, "NewGuid"))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Identity, RewriteRule.RedirectCall(
            "clockwork.bcl.guid.createversion7",
            MemberSignature.Method("System.Guid", "CreateVersion7"),
            Shim(GuidShim, "CreateVersion7"))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Identity, RewriteRule.RedirectCall(
            "clockwork.bcl.guid.createversion7.timestamp",
            MemberSignature.Method("System.Guid", "CreateVersion7", DateTimeOffset),
            Shim(GuidShim, "CreateVersion7", DateTimeOffset))));

        // ---- Random family: shared instance and both constructors ----
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Random, RewriteRule.RedirectCall(
            "clockwork.bcl.random.shared",
            MemberSignature.Method("System.Random", "get_Shared"),
            Shim(RandomShim, "GetShared"))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Random, RewriteRule.RedirectNewObj(
            "clockwork.bcl.random.ctor.unseeded",
            MemberSignature.Constructor("System.Random"),
            Shim(RandomShim, "CreateUnseeded"))));
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Random, RewriteRule.RedirectNewObj(
            "clockwork.bcl.random.ctor.seeded",
            MemberSignature.Constructor("System.Random", Int32),
            Shim(RandomShim, "CreateSeeded", Int32))));

        // ---- Crypto family: deterministic non-cryptographic shims for static RNG APIs ----
        Crypto(builder, "clockwork.bcl.rng.create", Shim(CryptoShim, "Create"),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "Create"));
        Crypto(builder, "clockwork.bcl.rng.create.named", Shim(CryptoShim, "Create", String),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "Create", String));
        Crypto(builder, "clockwork.bcl.rng.fill", Shim(CryptoShim, "Fill", SpanByte),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "Fill", SpanByte));
        Crypto(builder, "clockwork.bcl.rng.getbytes.count", Shim(CryptoShim, "GetBytes", Int32),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetBytes", Int32));
        Crypto(builder, "clockwork.bcl.rng.getint32.exclusive", Shim(CryptoShim, "GetInt32", Int32),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetInt32", Int32));
        Crypto(builder, "clockwork.bcl.rng.getint32.range", Shim(CryptoShim, "GetInt32", Int32, Int32),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetInt32", Int32, Int32));
        Crypto(builder, "clockwork.bcl.rng.getitems.span", Shim(CryptoShim, "GetItems", ReadOnlySpanTDecl, SpanTDecl),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetItems", ReadOnlySpanGenericArg0, SpanGenericArg0));
        Crypto(builder, "clockwork.bcl.rng.getitems.length", Shim(CryptoShim, "GetItems", ReadOnlySpanTDecl, Int32),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetItems", ReadOnlySpanGenericArg0, Int32));
        Crypto(builder, "clockwork.bcl.rng.gethexstring.span", Shim(CryptoShim, "GetHexString", SpanChar, Boolean),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetHexString", SpanChar, Boolean));
        Crypto(builder, "clockwork.bcl.rng.gethexstring.length", Shim(CryptoShim, "GetHexString", Int32, Boolean),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetHexString", Int32, Boolean));
        Crypto(builder, "clockwork.bcl.rng.getstring", Shim(CryptoShim, "GetString", ReadOnlySpanChar, Int32),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "GetString", ReadOnlySpanChar, Int32));
        Crypto(builder, "clockwork.bcl.rng.shuffle", Shim(CryptoShim, "Shuffle", SpanTDecl),
            MemberSignature.Method("System.Security.Cryptography.RandomNumberGenerator", "Shuffle", SpanGenericArg0));

        return builder.ToImmutable();
    }

    private static void Clock(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        string declaringType,
        string member,
        string shimType,
        string shimMember)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Clock, RewriteRule.RedirectCall(
            id,
            MemberSignature.Method(declaringType, member),
            Shim(shimType, shimMember))));
    }

    private static void Crypto(
        ImmutableArray<BuiltInRuleEntry>.Builder builder,
        string id,
        RewriteReplacement replacement,
        MemberSignature target)
    {
        builder.Add(new BuiltInRuleEntry(BuiltInRuleFamily.Crypto, RewriteRule.RedirectCall(
            id, target, replacement)));
    }

    private static ImmutableArray<BuiltInRuleEntry> ForSupportedRuntime(ImmutableArray<BuiltInRuleEntry> entries) =>
        [.. entries.Select(entry => entry with
        {
            Rule = entry.Rule with { SupportedRuntimes = new RuntimeVersionRange(SupportedRuntime, SupportedRuntime) },
        })];

    // A collection expression always yields a non-default ImmutableArray - even when empty - so the
    // resolver treats the replacement as having an exact parameter constraint and picks the intended
    // overload deterministically (never the metadata-order first match).
    private static RewriteReplacement Shim(string declaringType, string member, params string[] parameterTypes) =>
        new(ShimAssemblyName, declaringType, member, [.. parameterTypes]);

    private readonly record struct BuiltInRuleEntry(BuiltInRuleFamily Family, RewriteRule Rule);
}
