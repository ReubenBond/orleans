using System.Collections.Immutable;
using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.Testing.Simulation.Analyzers;

/// <summary>
/// The direct task and synchronization API surface whose shipped Clockwork rules require the
/// containing assembly to be instrumented. Entries are member-level because overload precision
/// remains the rewriter's responsibility; the analyzer's purpose is to make required instrumentation
/// visible at the source call site.
/// </summary>
public static class InstrumentedApiInventory
{
    private const string Wildcard = "*";

    private static readonly ImmutableHashSet<string> Members =
    [
        Api("System.Threading.Tasks.Task", "WhenAll"),
        Api("System.Threading.Tasks.Task", "WhenAny"),
        Api("System.Threading.Tasks.Task", "WhenEach"),
        Api("System.Threading.Tasks.Task", "Wait"),
        Api("System.Threading.Tasks.Task", "WaitAll"),
        Api("System.Threading.Tasks.Task", "WaitAny"),
        Api("System.Threading.Tasks.Task", "ContinueWith"),
        Api("System.Threading.Tasks.Task", "Delay"),
        Api("System.Threading.Tasks.Task", "WaitAsync"),
        Api("System.Threading.Tasks.Task", "Run"),
        Api("System.Threading.Tasks.Task", "Start"),
        Api("System.Threading.Tasks.Task", "RunSynchronously"),
        Api("System.Threading.Tasks.Task", "Yield"),
        Api("System.Threading.Tasks.Task`1", "Result"),
        Api("System.Threading.Tasks.Task", ".ctor"),
        Api("System.Threading.Tasks.Task`1", ".ctor"),
        Api("System.Threading.Tasks.Task", "CreationOptions"),
        Api("System.Threading.Tasks.Task`1", "ContinueWith"),
        Api("System.Threading.Tasks.Task`1", "WaitAsync"),
        Api("System.Threading.Tasks.TaskExtensions", "Unwrap"),
        Api("System.Threading.Tasks.TaskAsyncEnumerableExtensions", "ToBlockingEnumerable"),
        Api("System.Threading.Tasks.TaskCompletionSource", "SetResult"),
        Api("System.Threading.Tasks.TaskCompletionSource", "TrySetResult"),
        Api("System.Threading.Tasks.TaskCompletionSource", "SetException"),
        Api("System.Threading.Tasks.TaskCompletionSource", "TrySetException"),
        Api("System.Threading.Tasks.TaskCompletionSource", "SetCanceled"),
        Api("System.Threading.Tasks.TaskCompletionSource", "TrySetCanceled"),
        Api("System.Threading.Tasks.TaskCompletionSource", "SetFromTask"),
        Api("System.Threading.Tasks.TaskCompletionSource", "TrySetFromTask"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "SetResult"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "TrySetResult"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "SetException"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "TrySetException"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "SetCanceled"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "TrySetCanceled"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "SetFromTask"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", "TrySetFromTask"),
        Api("System.Threading.Tasks.TaskCompletionSource", ".ctor"),
        Api("System.Threading.Tasks.TaskCompletionSource`1", ".ctor"),
        Api("System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1", "SetResult"),
        Api("System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1", "SetException"),
        Api("System.Threading.Tasks.ValueTask", "AsTask"),
        Api("System.Threading.Tasks.ValueTask", "Preserve"),
        Api("System.Threading.Tasks.ValueTask`1", "AsTask"),
        Api("System.Threading.Tasks.ValueTask`1", "Preserve"),
        Api("System.Threading.Tasks.ValueTask`1", "Result"),
        Api("System.Threading.Tasks.TaskFactory", "StartNew"),
        Api("System.Threading.Tasks.TaskFactory`1", "StartNew"),
        Api("System.Threading.Tasks.TaskFactory", "ContinueWhenAll"),
        Api("System.Threading.Tasks.TaskFactory", "ContinueWhenAny"),
        Api("System.Threading.Tasks.TaskFactory", "FromAsync"),
        Api("System.Threading.Tasks.TaskFactory`1", "ContinueWhenAll"),
        Api("System.Threading.Tasks.TaskFactory`1", "ContinueWhenAny"),
        Api("System.Threading.Tasks.TaskFactory`1", "FromAsync"),
        Api("System.Environment", "CurrentManagedThreadId"),
        Api("System.Threading.Thread", ".ctor"),
        Api("System.Threading.Thread", "Start"),
        Api("System.Threading.Thread", "UnsafeStart"),
        Api("System.Threading.Thread", "Join"),
        Api("System.Threading.Thread", "Sleep"),
        Api("System.Threading.Thread", "SpinWait"),
        Api("System.Threading.Thread", "Yield"),
        Api("System.Threading.Thread", "Priority"),
        Api("System.Threading.Thread", "Interrupt"),
        Api("System.Threading.Thread", "SetApartmentState"),
        Api("System.Threading.Thread", "TrySetApartmentState"),
        Api("System.Threading.Thread", "ApartmentState"),
        Api("System.Threading.ThreadPool", "QueueUserWorkItem"),
        Api("System.Threading.ThreadPool", "UnsafeQueueUserWorkItem"),
        Api("System.Threading.ThreadPool", "UnsafeQueueNativeOverlapped"),
        Api("System.Threading.ThreadPool", "RegisterWaitForSingleObject"),
        Api("System.Threading.ThreadPool", "UnsafeRegisterWaitForSingleObject"),
        Api("System.Threading.RegisteredWaitHandle", Wildcard),
        Api("System.Threading.Timer", Wildcard),
        Api("System.Timers.Timer", Wildcard),
        Api("System.Threading.PeriodicTimer", Wildcard),
        Api("System.TimeProvider", "System"),
        Api("System.TimeProvider", "CreateTimer"),
        Api("System.Threading.CancellationTokenSource", ".ctor"),
        Api("System.Threading.CancellationTokenSource", "CancelAfter"),
        Api("System.Threading.CancellationTokenSource", "Cancel"),
        Api("System.Threading.CancellationTokenSource", "CancelAsync"),
        Api("System.Threading.CancellationTokenSource", "TryReset"),
        Api("System.Threading.CancellationTokenSource", "Dispose"),
        Api("System.Threading.Tasks.Parallel", "Invoke"),
        Api("System.Threading.Tasks.Parallel", "For"),
        Api("System.Threading.Tasks.Parallel", "ForEach"),
        Api("System.Threading.Tasks.Parallel", "ForAsync"),
        Api("System.Threading.Tasks.Parallel", "ForEachAsync"),
        Api("System.Environment", "ProcessorCount"),
        Api("System.Collections.Concurrent.Partitioner", "Create"),
        Api("System.Threading.Channels.ChannelReader`1", "ReadAllAsync"),
        Api("System.Threading.Channels.ChannelReader`1", "ReadAsync"),
        Api("System.Threading.Channels.ChannelWriter`1", "WriteAsync"),
        Api("System.Linq.ParallelEnumerable", "AsParallel"),
        Api("System.Linq.ParallelEnumerable", "Range"),
        Api("System.Linq.ParallelEnumerable", "Repeat"),
        Api("System.Linq.ParallelEnumerable", "Empty"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "Add"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "TryAdd"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "Take"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "TryTake"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "GetConsumingEnumerable"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "CompleteAdding"),
        Api("System.ComponentModel.BackgroundWorker", "RunWorkerAsync"),
        Api("Microsoft.Extensions.Hosting.BackgroundService", "StartAsync"),
        Api("System.Threading.Tasks.Dataflow.ActionBlock`1", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.BatchBlock`1", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.BatchedJoinBlock`2", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.BatchedJoinBlock`3", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.BroadcastBlock`1", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.BufferBlock`1", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.JoinBlock`2", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.JoinBlock`3", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.TransformBlock`2", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.TransformManyBlock`2", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.WriteOnceBlock`1", ".ctor"),
        Api("System.Threading.Tasks.Dataflow.DataflowBlock", "Choose"),
        Api("System.Threading.Tasks.Dataflow.DataflowBlock", "Receive"),
        Api("System.Threading.Tasks.Dataflow.DataflowBlock", "ReceiveAsync"),
        Api("System.Threading.Tasks.Dataflow.DataflowBlock", "ReceiveAllAsync"),
        Api("System.Threading.Tasks.Dataflow.DataflowBlock", "SendAsync"),
        Api("System.Threading.Tasks.Dataflow.DataflowBlock", "OutputAvailableAsync"),
        Api("System.Net.WebSockets.WebSocket", "CreateFromStream"),
        Api("System.Net.WebSockets.WebSocket", "CreateClientWebSocket"),
        Api("System.Net.WebSockets.WebSocketStream", "Create"),
        Api("System.Net.Http.HttpClient", "DeleteAsync"),
        Api("System.Net.Http.HttpClient", "GetAsync"),
        Api("System.Net.Http.HttpClient", "GetByteArrayAsync"),
        Api("System.Net.Http.HttpClient", "GetStreamAsync"),
        Api("System.Net.Http.HttpClient", "GetStringAsync"),
        Api("System.Net.Http.HttpClient", "PatchAsync"),
        Api("System.Net.Http.HttpClient", "PostAsync"),
        Api("System.Net.Http.HttpClient", "PutAsync"),
        Api("System.Net.Http.HttpClient", "Send"),
        Api("System.Net.Http.HttpClient", "SendAsync"),
        Api("System.Net.Http.HttpMessageInvoker", "Send"),
        Api("System.Net.Http.HttpMessageInvoker", "SendAsync"),
        Api("System.Net.Http.Json.HttpClientJsonExtensions", Wildcard),
        Api("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions", Wildcard),
        Api("Microsoft.Extensions.Hosting.HostingAbstractionsHostBuilderExtensions", Wildcard),
        Api("System.Collections.Concurrent.BlockingCollection`1", "AddToAny"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "TryAddToAny"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "TakeFromAny"),
        Api("System.Collections.Concurrent.BlockingCollection`1", "TryTakeFromAny"),
        Api("System.IO.Stream", "ReadAsync"),
        Api("System.IO.Stream", "WriteAsync"),
        Api("System.IO.Stream", "FlushAsync"),
        Api("System.IO.Stream", "CopyToAsync"),
        Api("System.IO.Stream", "BeginRead"),
        Api("System.IO.Stream", "EndRead"),
        Api("System.IO.Stream", "BeginWrite"),
        Api("System.IO.Stream", "EndWrite"),
        Api("System.IO.Stream", "ReadExactlyAsync"),
        Api("System.IO.Stream", "ReadAtLeastAsync"),
        Api("System.IO.TextReader", "ReadLineAsync"),
        Api("System.IO.TextReader", "ReadToEndAsync"),
        Api("System.IO.TextReader", "ReadAsync"),
        Api("System.IO.TextReader", "ReadBlockAsync"),
        Api("System.IO.TextWriter", "WriteAsync"),
        Api("System.IO.TextWriter", "WriteLineAsync"),
        Api("System.IO.TextWriter", "FlushAsync"),
        Api("System.Threading.Monitor", "Enter"),
        Api("System.Threading.Monitor", "Exit"),
        Api("System.Threading.Monitor", "IsEntered"),
        Api("System.Threading.Monitor", "TryEnter"),
        Api("System.Threading.Monitor", "Wait"),
        Api("System.Threading.Monitor", "Pulse"),
        Api("System.Threading.Monitor", "PulseAll"),
        Api("System.Threading.Monitor", "LockContentionCount"),
        Api("System.Threading.Lock", Wildcard),
        Api("System.Threading.Lock+Scope", Wildcard),
        Api("System.Threading.SemaphoreSlim", ".ctor"),
        Api("System.Threading.SemaphoreSlim", "CurrentCount"),
        Api("System.Threading.SemaphoreSlim", "AvailableWaitHandle"),
        Api("System.Threading.SemaphoreSlim", "Wait"),
        Api("System.Threading.SemaphoreSlim", "WaitAsync"),
        Api("System.Threading.SemaphoreSlim", "Release"),
        Api("System.Threading.SemaphoreSlim", "Dispose"),
        Api("System.Threading.ReaderWriterLockSlim", Wildcard),
        Api("System.Threading.ManualResetEventSlim", Wildcard),
        Api("System.Threading.Mutex", ".ctor"),
        Api("System.Threading.Mutex", "ReleaseMutex"),
        Api("System.Threading.Mutex", "OpenExisting"),
        Api("System.Threading.Mutex", "TryOpenExisting"),
        Api("System.Threading.Semaphore", ".ctor"),
        Api("System.Threading.Semaphore", "Release"),
        Api("System.Threading.Semaphore", "OpenExisting"),
        Api("System.Threading.Semaphore", "TryOpenExisting"),
        Api("System.Threading.SpinLock", Wildcard),
        Api("System.Threading.ExecutionContext", "Capture"),
        Api("System.Threading.ExecutionContext", "Run"),
        Api("System.Threading.ExecutionContext", "SuppressFlow"),
        Api("System.Threading.ExecutionContext", "RestoreFlow"),
        Api("System.Threading.ExecutionContext", "IsFlowSuppressed"),
        Api("System.Threading.ExecutionContext", "Restore"),
        Api("System.Threading.ExecutionContext", "CreateCopy"),
        Api("System.Threading.ExecutionContext", "Dispose"),
        Api("System.Threading.ExecutionContext", "GetObjectData"),
        Api("System.Threading.SynchronizationContext", "Current"),
        Api("System.Threading.SynchronizationContext", "SetSynchronizationContext"),
        Api("System.Threading.SynchronizationContext", "CreateCopy"),
        Api("System.Threading.SynchronizationContext", "IsWaitNotificationRequired"),
        Api("System.Threading.SynchronizationContext", "OperationStarted"),
        Api("System.Threading.SynchronizationContext", "OperationCompleted"),
        Api("System.Threading.SynchronizationContext", "Post"),
        Api("System.Threading.SynchronizationContext", "Send"),
        Api("System.Threading.SynchronizationContext", "Wait"),
        Api("System.Threading.Barrier", Wildcard),
        Api("System.Threading.CountdownEvent", Wildcard),
    ];

    // Members which need instrumentation only for their base implementation, an async method whose awaits resume on the
    // thread pool: the shim calls a receiver's override as written.
    private static readonly ImmutableHashSet<string> BaseImplementationMembers =
    [
        Api("System.Threading.Channels.ChannelReader`1", "ReadAllAsync"),
        Api("System.Threading.Channels.ChannelReader`1", "ReadAsync"),
        Api("System.Threading.Channels.ChannelWriter`1", "WriteAsync"),
    ];

    /// <summary>Gets the metadata names of all framework types represented by the inventory.</summary>
    public static ImmutableArray<string> TypeMetadataNames { get; } =
        [.. Members.Select(static entry => entry.Substring(0, entry.IndexOf("::", StringComparison.Ordinal)))
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(static name => name, System.StringComparer.Ordinal)];

    /// <summary>Returns whether the named framework member requires shipped Clockwork instrumentation.</summary>
    public static bool Contains(string typeMetadataName, string memberName) =>
        Members.Contains(Api(typeMetadataName, memberName))
        || Members.Contains(Api(typeMetadataName, Wildcard));

    /// <summary>Returns whether the exact invocation shape is covered by shipped instrumentation.</summary>
    public static bool ContainsInvocation(string typeMetadataName, IMethodSymbol method)
    {
        if (!Contains(typeMetadataName, method.Name))
        {
            return false;
        }

        if (typeMetadataName is "System.Threading.Tasks.TaskFactory" or "System.Threading.Tasks.TaskFactory`1")
        {
            return method.Name != "StartNew"
                || (method.Parameters.Length <= 3
                    && !method.Parameters.Any(parameter =>
                        parameter.Type.ToDisplayString() == "System.Threading.Tasks.TaskScheduler"));
        }

        // Only the completion-source constructors which take creation options are rewritten.
        if (typeMetadataName is "System.Threading.Tasks.TaskCompletionSource" or "System.Threading.Tasks.TaskCompletionSource`1"
            && method.MethodKind == MethodKind.Constructor)
        {
            return method.Parameters.Any(static parameter =>
                parameter.Type.ToDisplayString() == "System.Threading.Tasks.TaskCreationOptions");
        }

        // Only the range factories which size their ranges by the processor count are rewritten.
        if (typeMetadataName == "System.Collections.Concurrent.Partitioner")
        {
            return method.Parameters.Length == 2
                && method.Parameters.All(static parameter =>
                    parameter.Type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64);
        }

        return true;
    }

    /// <summary>
    /// Returns whether a call to a member which needs instrumentation only for its base implementation provably runs an
    /// override: a virtual call whose receiver's static type, or one of its bases below the member's declaring type,
    /// overrides the member. A base call (<c>base.ReadAsync()</c>) is not virtual and runs the base implementation; a
    /// receiver typed as the declaring type (a built-in channel's <c>Reader</c>, say) may run either.
    /// </summary>
    /// <param name="typeMetadataName">The metadata name of the member's declaring type.</param>
    /// <param name="method">The invoked member.</param>
    /// <param name="isVirtual">Whether the invocation dispatches virtually.</param>
    /// <param name="receiverType">The static type of the receiver.</param>
    /// <returns>Whether the call runs an override, which the shim calls as written.</returns>
    public static bool DispatchesToOverride(
        string typeMetadataName,
        IMethodSymbol method,
        bool isVirtual,
        ITypeSymbol? receiverType)
    {
        if (!isVirtual || !BaseImplementationMembers.Contains(Api(typeMetadataName, method.Name)))
        {
            return false;
        }

        for (INamedTypeSymbol? type = receiverType as INamedTypeSymbol;
            type is not null && !SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, method.ContainingType.OriginalDefinition);
            type = type.BaseType)
        {
            foreach (IMethodSymbol member in type.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                for (IMethodSymbol? overridden = member.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                {
                    if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, method.OriginalDefinition))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Returns whether all operations over the named framework type are instrumented.</summary>
    public static bool ContainsType(string typeMetadataName) =>
        Members.Contains(Api(typeMetadataName, Wildcard));

    private static string Api(string typeMetadataName, string memberName) =>
        typeMetadataName + "::" + memberName;
}
