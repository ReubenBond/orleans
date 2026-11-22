using System.Collections.Immutable;

namespace Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

/// <summary>A framework member which reaches a concurrency escape and needs no rule, with the reason.</summary>
/// <param name="DeclaringTypeFullName">The Cecil full name of the declaring type.</param>
/// <param name="MemberKey">The member as a call site names it, in the form <see cref="UnrewrittenMember.MemberKey"/> uses.</param>
/// <param name="Reason">Why the member needs no rule.</param>
internal readonly record struct AuditedEscape(string DeclaringTypeFullName, string MemberKey, EscapeReason Reason)
{
    /// <summary>Gets the member qualified by its declaring type: <c>DeclaringType::MemberKey</c>.</summary>
    public string QualifiedKey => DeclaringTypeFullName + "::" + MemberKey;
}

/// <summary>A shared-framework assembly whose members reach concurrency escapes and need no rule, with the reason.</summary>
/// <param name="Name">The simple name of the implementation assembly.</param>
/// <param name="Reason">Why its members need no rule.</param>
/// <param name="Behavior">What its members do under simulation, which the reason summarizes.</param>
internal readonly record struct AuditedAssembly(string Name, EscapeReason Reason, string Behavior);

/// <summary>
/// The classified result of the concurrency-escape audit. A test scans every managed implementation assembly of the
/// shared framework the tests run on for the public members (and protected members of unsealed types) of the reference
/// pack that reach thread-pool queueing, <c>TaskScheduler.Default</c> or the members which schedule onto it
/// (<c>Task.Run</c>, <c>Task.Start</c>, <c>TaskFactory.StartNew</c>), <c>Thread.Start</c>, a real timer, or an async
/// method, within <see cref="MaxDepth"/> calls inside one assembly. Every such member must be controlled or rejected by a
/// built-in rule, listed in <see cref="UnrewrittenMembers"/>, listed in <see cref="All"/> with the reason it needs no rule,
/// or declared by an assembly of <see cref="Assemblies"/>, whose reason covers every member a rule does not classify. The
/// implementation assemblies differ by operating system, so the lists hold every platform's candidates. The test fails on
/// a member a new runtime adds, and on an entry the reference pack or the shared framework no longer has, or that a rule
/// now classifies; the members an assembly's reason covers are kept in a snapshot, so one a runtime adds to such an
/// assembly fails until it is reviewed. <see cref="RuleInventoryDocument"/> publishes the result as the concurrency status table.
/// </summary>
internal static class ConcurrencyEscapeAudit
{
    /// <summary>
    /// The longest chain of calls from a member to an escape that the scan follows. A call to another overload of the
    /// caller, or to a local function or lambda compiled from its body, does not count. Four calls reach the escapes
    /// behind the framework's layered stream and socket overloads; a fifth starts reaching the BCL's blocking waits,
    /// which only read the current scheduler.
    /// </summary>
    public const int MaxDepth = 4;

    /// <summary>
    /// Gets the assemblies whose every candidate a rule does not classify needs no rule for one reason: they perform OS
    /// I/O, await what they are given through the BCL's dispatch, or raise an application's callbacks on threads of
    /// their own. A member-level entry in <see cref="All"/> overrides an assembly's reason for that member. CoreLib and the
    /// concurrency libraries are classified member by member, since rules control most of their candidates.
    /// </summary>
    public static ImmutableArray<AuditedAssembly> Assemblies { get; } =
    [
        new("Microsoft.Extensions.Caching.Abstractions", EscapeReason.FrameworkAwait,
            "`HybridCache` and the `IDistributedCache` extensions await the cache and the factory they are given."),
        new("Microsoft.Extensions.Hosting.Abstractions", EscapeReason.FrameworkAwait,
            "`BackgroundService.StopAsync` awaits the service's work. The `IHost` and `IHostBuilder` extensions, which await the host's work " +
            "in framework code, block on it, or bound a stop with a real timer, are controlled (`Hosting` family), and " +
            "`BackgroundService.StartAsync`, which starts the service's work with the real `Task.Run`, is rejected " +
            "(`BackgroundWork` family)."),
        new("Microsoft.Extensions.Options", EscapeReason.FrameworkAwait,
            "Asynchronous options validation awaits the validators it is given."),
        new("System.ComponentModel.Annotations", EscapeReason.FrameworkAwait,
            "Asynchronous validation awaits the validation attributes it is given."),
        new("System.Data.Common", EscapeReason.FrameworkAwait,
            "`DbDataSource` awaits the provider's connections and commands."),
        new("System.Formats.Tar", EscapeReason.FrameworkAwait,
            "`TarReader` and `TarWriter` await the stream they are given; `TarFile` also reads and writes the file system."),
        new("System.IO.Compression", EscapeReason.FrameworkAwait,
            "The compression streams and `ZipArchive` await the stream they wrap."),
        new("System.IO.Compression.Brotli", EscapeReason.FrameworkAwait,
            "`BrotliStream` awaits the stream it wraps."),
        new("System.IO.Compression.ZipFile", EscapeReason.OsResource,
            "`ZipFile` and its extensions read and write archives and entries on the file system."),
        new("System.IO.FileSystem.Watcher", EscapeReason.AffinityBackstop,
            "`FileSystemWatcher` raises its events as the OS reports changes, on a thread of its own (Linux, macOS) or " +
            "the thread pool (Windows)."),
        new("System.IO.Pipelines", EscapeReason.FrameworkAwait,
            "`PipeReader` and `PipeWriter` copy to and from the stream or pipe they are given, awaiting it."),
        new("System.IO.Pipes", EscapeReason.OsResource,
            "Named and anonymous pipes are OS handles; `NamedPipeClientStream.ConnectAsync` connects on the thread pool."),
        new("System.Linq.AsyncEnumerable", EscapeReason.FrameworkAwait,
            "The operators await the sources and the asynchronous selectors and predicates they are given."),
        new("System.Memory", EscapeReason.FrameworkAwait,
            "The stream over a `ReadOnlySequence<byte>` awaits the destination it copies to."),
        new("System.Net.Http", EscapeReason.FrameworkAwait,
            "The content types await the streams they are given and the handlers await the handler they wrap; the default " +
            "handler performs network I/O. `HttpClient`'s sending members, which bound each request with " +
            "`HttpClient.Timeout` on a real timer, are controlled (`HttpClient` family). `MessageProcessingHandler` is " +
            "listed by member."),
        new("System.Net.Http.Json", EscapeReason.FrameworkAwait,
            "The `HttpContent` extensions await the content they are given. The `HttpClient` extensions, which bound the " +
            "request and the reading of its response with `HttpClient.Timeout` on a real timer, are controlled " +
            "(`HttpClient` family)."),
        new("System.Net.HttpListener", EscapeReason.OsResource,
            "`HttpListener` serves requests from the OS's HTTP stack or its own sockets."),
        new("System.Net.Mail", EscapeReason.OsResource,
            "`SmtpClient` sends over a network connection and raises `SendCompleted` through the captured " +
            "`SynchronizationContext` or on the thread pool."),
        new("System.Net.NameResolution", EscapeReason.OsResource,
            "`Dns` resolves names through the OS resolver on the thread pool."),
        new("System.Net.NetworkInformation", EscapeReason.AffinityBackstop,
            "`NetworkChange` raises its events as the OS reports address and availability changes, on a thread of the " +
            "BCL's choosing."),
        new("System.Net.Ping", EscapeReason.OsResource,
            "`Ping` sends ICMP echo requests through the OS and raises `PingCompleted` through the captured " +
            "`SynchronizationContext` or on the thread pool."),
        new("System.Net.Quic", EscapeReason.OsResource,
            "QUIC connections, listeners, and streams run over the OS's QUIC implementation."),
        new("System.Net.Requests", EscapeReason.OsResource,
            "`WebRequest` and its HTTP, FTP, and file implementations perform network and file I/O."),
        new("System.Net.Security", EscapeReason.FrameworkAwait,
            "`SslStream` and `NegotiateStream` await the stream they wrap; their handshakes use the OS's security " +
            "libraries."),
        new("System.Net.ServerSentEvents", EscapeReason.FrameworkAwait,
            "`SseParser` and `SseFormatter` await the stream and the items they are given."),
        new("System.Net.Sockets", EscapeReason.OsResource,
            "Sockets, `TcpClient`, `TcpListener`, and `UdpClient` are OS sockets, whose operations complete on the OS's " +
            "I/O threads or the thread pool."),
        new("System.Net.WebClient", EscapeReason.OsResource,
            "`WebClient` performs network I/O and raises its completion events through the captured " +
            "`SynchronizationContext` or on the thread pool."),
        new("System.Net.WebSockets", EscapeReason.FrameworkAwait,
            "`WebSocketStream` and the `WebSocket` base members await the `WebSocket` they are given. The " +
            "`WebSocketStream.Create` overloads which make a stream own its socket, whose disposal bounds the close with a " +
            "real timer, are controlled, and the factories which run the framework's protocol over a caller's stream are " +
            "rejected (`WebSockets` family)."),
        new("System.Net.WebSockets.Client", EscapeReason.OsResource,
            "`ClientWebSocket` connects over the network."),
        new("System.Private.Xml", EscapeReason.FrameworkAwait,
            "The asynchronous `XmlReader` and `XmlWriter` members await the stream, reader, or writer they wrap; " +
            "`XmlUrlResolver` opens files and network resources."),
        new("System.Private.Xml.Linq", EscapeReason.FrameworkAwait,
            "`XDocument`, `XElement`, and `XNode` load and save through an asynchronous `XmlReader` or `XmlWriter`."),
        new("System.Security.Cryptography", EscapeReason.FrameworkAwait,
            "`CryptoStream` and the asynchronous hashing members await the stream they are given."),
        new("System.Text.Json", EscapeReason.FrameworkAwait,
            "The serializer, `JsonDocument`, `JsonNode`, and `Utf8JsonWriter` await the stream or asynchronous sequence " +
            "they are given."),
        new("System.Text.RegularExpressions", EscapeReason.WaitsForFrameworkWork,
            "Parsing a deeply nested pattern continues on a fresh stack on the thread pool while the constructor waits."),
        new("System.Transactions.Local", EscapeReason.AffinityBackstop,
            "A transaction's timeout runs on a real timer, and when it expires the abort and the enlistments' " +
            "notifications run on the thread pool; distributed transactions use the OS's transaction coordinator."),
    ];
    /// <summary>Gets every listed member, grouped by declaring type and then by reason.</summary>
    public static ImmutableArray<AuditedEscape> All { get; } = Build();

    private static ImmutableArray<AuditedEscape> Build()
    {
        var members = ImmutableArray.CreateBuilder<AuditedEscape>();
        void Add(string type, EscapeReason reason, params string[] memberKeys)
        {
            foreach (string memberKey in memberKeys)
            {
                members.Add(new AuditedEscape(type, memberKey, reason));
            }
        }

        Add("System.CodeDom.Compiler.IndentedTextWriter", EscapeReason.FrameworkAwait,
            "FlushAsync()",
            "FlushAsync(System.Threading.CancellationToken)",
            "OutputTabsAsync()",
            "WriteAsync(System.Char)",
            "WriteAsync(System.Char[],System.Int32,System.Int32)",
            "WriteAsync(System.ReadOnlyMemory`1<System.Char>,System.Threading.CancellationToken)",
            "WriteAsync(System.String)",
            "WriteAsync(System.Text.StringBuilder,System.Threading.CancellationToken)",
            "WriteLineAsync()",
            "WriteLineAsync(System.Char)",
            "WriteLineAsync(System.Char[],System.Int32,System.Int32)",
            "WriteLineAsync(System.ReadOnlyMemory`1<System.Char>,System.Threading.CancellationToken)",
            "WriteLineAsync(System.String)",
            "WriteLineAsync(System.Text.StringBuilder,System.Threading.CancellationToken)",
            "WriteLineNoTabsAsync(System.String)");

        Add("System.Diagnostics.Tracing.EventListener", EscapeReason.AffinityBackstop,
            "DisableEvents(System.Diagnostics.Tracing.EventSource)",
            "Dispose()",
            "EnableEvents(System.Diagnostics.Tracing.EventSource,System.Diagnostics.Tracing.EventLevel)",
            "EnableEvents(System.Diagnostics.Tracing.EventSource,System.Diagnostics.Tracing.EventLevel,System.Diagnostics.Tracing.EventKeywords)",
            "EnableEvents(System.Diagnostics.Tracing.EventSource,System.Diagnostics.Tracing.EventLevel,System.Diagnostics.Tracing.EventKeywords,System.Collections.Generic.IDictionary`2<System.String,System.String>)");

        Add("System.IO.BinaryWriter", EscapeReason.FrameworkAwait,
            "DisposeAsync()");

        Add("System.IO.BufferedStream", EscapeReason.FrameworkAwait,
            "BeginRead(System.Byte[],System.Int32,System.Int32,System.AsyncCallback,System.Object)",
            "BeginWrite(System.Byte[],System.Int32,System.Int32,System.AsyncCallback,System.Object)",
            "CopyToAsync(System.IO.Stream,System.Int32,System.Threading.CancellationToken)",
            "DisposeAsync()",
            "FlushAsync(System.Threading.CancellationToken)",
            "ReadAsync(System.Byte[],System.Int32,System.Int32,System.Threading.CancellationToken)",
            "ReadAsync(System.Memory`1<System.Byte>,System.Threading.CancellationToken)",
            "WriteAsync(System.Byte[],System.Int32,System.Int32,System.Threading.CancellationToken)",
            "WriteAsync(System.ReadOnlyMemory`1<System.Byte>,System.Threading.CancellationToken)");

        Add("System.IO.File", EscapeReason.OsResource,
            "AppendAllBytesAsync(System.String,System.Byte[],System.Threading.CancellationToken)",
            "AppendAllBytesAsync(System.String,System.ReadOnlyMemory`1<System.Byte>,System.Threading.CancellationToken)",
            "AppendAllLinesAsync(System.String,System.Collections.Generic.IEnumerable`1<System.String>,System.Text.Encoding,System.Threading.CancellationToken)",
            "AppendAllLinesAsync(System.String,System.Collections.Generic.IEnumerable`1<System.String>,System.Threading.CancellationToken)",
            "AppendAllTextAsync(System.String,System.ReadOnlyMemory`1<System.Char>,System.Text.Encoding,System.Threading.CancellationToken)",
            "AppendAllTextAsync(System.String,System.ReadOnlyMemory`1<System.Char>,System.Threading.CancellationToken)",
            "AppendAllTextAsync(System.String,System.String,System.Text.Encoding,System.Threading.CancellationToken)",
            "AppendAllTextAsync(System.String,System.String,System.Threading.CancellationToken)",
            "ReadAllBytesAsync(System.String,System.Threading.CancellationToken)",
            "ReadAllLinesAsync(System.String,System.Text.Encoding,System.Threading.CancellationToken)",
            "ReadAllLinesAsync(System.String,System.Threading.CancellationToken)",
            "ReadAllTextAsync(System.String,System.Text.Encoding,System.Threading.CancellationToken)",
            "ReadAllTextAsync(System.String,System.Threading.CancellationToken)",
            "ReadLinesAsync(System.String,System.Text.Encoding,System.Threading.CancellationToken)",
            "ReadLinesAsync(System.String,System.Threading.CancellationToken)",
            "WriteAllBytesAsync(System.String,System.Byte[],System.Threading.CancellationToken)",
            "WriteAllBytesAsync(System.String,System.ReadOnlyMemory`1<System.Byte>,System.Threading.CancellationToken)",
            "WriteAllLinesAsync(System.String,System.Collections.Generic.IEnumerable`1<System.String>,System.Text.Encoding,System.Threading.CancellationToken)",
            "WriteAllLinesAsync(System.String,System.Collections.Generic.IEnumerable`1<System.String>,System.Threading.CancellationToken)",
            "WriteAllTextAsync(System.String,System.ReadOnlyMemory`1<System.Char>,System.Text.Encoding,System.Threading.CancellationToken)",
            "WriteAllTextAsync(System.String,System.ReadOnlyMemory`1<System.Char>,System.Threading.CancellationToken)",
            "WriteAllTextAsync(System.String,System.String,System.Text.Encoding,System.Threading.CancellationToken)",
            "WriteAllTextAsync(System.String,System.String,System.Threading.CancellationToken)");

        Add("System.IO.FileStream", EscapeReason.OsResource,
            "BeginRead(System.Byte[],System.Int32,System.Int32,System.AsyncCallback,System.Object)",
            "BeginWrite(System.Byte[],System.Int32,System.Int32,System.AsyncCallback,System.Object)",
            "CopyToAsync(System.IO.Stream,System.Int32,System.Threading.CancellationToken)",
            "DisposeAsync()",
            "FlushAsync(System.Threading.CancellationToken)",
            "ReadAsync(System.Byte[],System.Int32,System.Int32,System.Threading.CancellationToken)",
            "ReadAsync(System.Memory`1<System.Byte>,System.Threading.CancellationToken)",
            "WriteAsync(System.Byte[],System.Int32,System.Int32,System.Threading.CancellationToken)",
            "WriteAsync(System.ReadOnlyMemory`1<System.Byte>,System.Threading.CancellationToken)");

        Add("System.IO.MemoryStream", EscapeReason.FrameworkAwait,
            "CopyToAsync(System.IO.Stream,System.Int32,System.Threading.CancellationToken)");

        Add("System.IO.RandomAccess", EscapeReason.OsResource,
            "ReadAsync(Microsoft.Win32.SafeHandles.SafeFileHandle,System.Collections.Generic.IReadOnlyList`1<System.Memory`1<System.Byte>>,System.Int64,System.Threading.CancellationToken)",
            "ReadAsync(Microsoft.Win32.SafeHandles.SafeFileHandle,System.Memory`1<System.Byte>,System.Int64,System.Threading.CancellationToken)",
            "WriteAsync(Microsoft.Win32.SafeHandles.SafeFileHandle,System.Collections.Generic.IReadOnlyList`1<System.ReadOnlyMemory`1<System.Byte>>,System.Int64,System.Threading.CancellationToken)",
            "WriteAsync(Microsoft.Win32.SafeHandles.SafeFileHandle,System.ReadOnlyMemory`1<System.Byte>,System.Int64,System.Threading.CancellationToken)");

        Add("System.IO.ReadOnlyMemoryStream", EscapeReason.FrameworkAwait,
            "CopyToAsync(System.IO.Stream,System.Int32,System.Threading.CancellationToken)");


        Add("System.IO.StreamReader", EscapeReason.FrameworkAwait,
            "ReadAsync(System.Char[],System.Int32,System.Int32)",
            "ReadAsync(System.Memory`1<System.Char>,System.Threading.CancellationToken)",
            "ReadBlockAsync(System.Char[],System.Int32,System.Int32)",
            "ReadBlockAsync(System.Memory`1<System.Char>,System.Threading.CancellationToken)",
            "ReadLineAsync()",
            "ReadLineAsync(System.Threading.CancellationToken)",
            "ReadToEndAsync()",
            "ReadToEndAsync(System.Threading.CancellationToken)");

        Add("System.IO.StreamWriter", EscapeReason.FrameworkAwait,
            "DisposeAsync()",
            "FlushAsync()",
            "FlushAsync(System.Threading.CancellationToken)",
            "WriteAsync(System.Char)",
            "WriteAsync(System.Char[],System.Int32,System.Int32)",
            "WriteAsync(System.ReadOnlyMemory`1<System.Char>,System.Threading.CancellationToken)",
            "WriteAsync(System.String)",
            "WriteLineAsync()",
            "WriteLineAsync(System.Char)",
            "WriteLineAsync(System.Char[],System.Int32,System.Int32)",
            "WriteLineAsync(System.ReadOnlyMemory`1<System.Char>,System.Threading.CancellationToken)",
            "WriteLineAsync(System.String)");

        Add("System.IO.StringWriter", EscapeReason.CompletesOnCaller,
            "WriteAsync(System.Text.StringBuilder,System.Threading.CancellationToken)",
            "WriteLineAsync(System.Text.StringBuilder,System.Threading.CancellationToken)");



        Add("System.IO.WritableMemoryStream", EscapeReason.FrameworkAwait,
            "CopyToAsync(System.IO.Stream,System.Int32,System.Threading.CancellationToken)");

        Add("System.Net.Http.MessageProcessingHandler", EscapeReason.AffinityBackstop,
            "SendAsync(System.Net.Http.HttpRequestMessage,System.Threading.CancellationToken)");

        Add("System.Progress`1", EscapeReason.AffinityBackstop,
            "OnReport(!0)");

        Add("System.Runtime.CompilerServices.AsyncIteratorMethodBuilder", EscapeReason.UnhandledException,
            "AwaitOnCompleted``2(!!0&,!!1&)",
            "AwaitUnsafeOnCompleted``2(!!0&,!!1&)");

        Add("System.Runtime.CompilerServices.AsyncVoidMethodBuilder", EscapeReason.UnhandledException,
            "AwaitOnCompleted``2(!!0&,!!1&)",
            "AwaitUnsafeOnCompleted``2(!!0&,!!1&)",
            "SetException(System.Exception)",
            "SetResult()");

        Add("System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder", EscapeReason.ValueTaskSource,
            "SetException(System.Exception)",
            "SetResult()");

        Add("System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder", EscapeReason.UnhandledException,
            "AwaitOnCompleted``2(!!0&,!!1&)",
            "AwaitUnsafeOnCompleted``2(!!0&,!!1&)");

        Add("System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1", EscapeReason.ValueTaskSource,
            "SetException(System.Exception)",
            "SetResult(!0)");

        Add("System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1", EscapeReason.UnhandledException,
            "AwaitOnCompleted``2(!!0&,!!1&)",
            "AwaitUnsafeOnCompleted``2(!!0&,!!1&)");

        Add("System.Threading.CancellationTokenRegistration", EscapeReason.FrameworkAwait,
            "DisposeAsync()");

        Add("System.Threading.Tasks.ConcurrentExclusiveSchedulerPair", EscapeReason.CustomScheduler,
            ".ctor()",
            "Complete()");

        Add("System.Threading.Tasks.Dataflow.DataflowBlockOptions", EscapeReason.SchedulerValue,
            ".ctor()");

        Add("System.Threading.Tasks.Dataflow.ExecutionDataflowBlockOptions", EscapeReason.SchedulerValue,
            ".ctor()");

        Add("System.Threading.Tasks.Dataflow.GroupingDataflowBlockOptions", EscapeReason.SchedulerValue,
            ".ctor()");

        Add("System.Threading.Tasks.ParallelOptions", EscapeReason.SchedulerValue,
            ".ctor()");

        Add("System.Threading.Tasks.TaskScheduler", EscapeReason.SchedulerValue,
            "get_Current()");

        Add("System.Threading.Tasks.TaskScheduler", EscapeReason.CustomScheduler,
            "TryExecuteTask(System.Threading.Tasks.Task)");

        Add("System.Threading.Tasks.TaskToAsyncResult", EscapeReason.FrameworkAwait,
            "Begin(System.Threading.Tasks.Task,System.AsyncCallback,System.Object)");

        return members.ToImmutable();
    }
}
