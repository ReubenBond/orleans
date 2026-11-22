using System.Collections.Concurrent;
using System.Collections.Immutable;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

/// <summary>What a concurrency escape a candidate reaches does.</summary>
internal enum EscapeSink
{
    /// <summary>Queues work to the thread pool (a <c>ThreadPool</c> queueing or registered-wait member).</summary>
    ThreadPool,

    /// <summary>Queues work to <c>TaskScheduler.Default</c> (<c>TaskScheduler.Default</c>, <c>Task.Run</c>, <c>TaskFactory.StartNew</c>, <c>Task.Start</c>).</summary>
    TaskScheduler,

    /// <summary>Starts a physical thread (<c>Thread.Start</c>, <c>Thread.UnsafeStart</c>).</summary>
    Thread,

    /// <summary>Creates a real timer (<c>System.Threading.Timer</c>, <c>TimerQueueTimer</c>).</summary>
    Timer,

    /// <summary>Is an async method or async iterator (a compiled state machine, or runtime async), whose awaits resume through BCL awaiters or the runtime.</summary>
    AsyncMethod,
}

/// <summary>A public or protected framework member that reaches a concurrency escape.</summary>
/// <param name="Assembly">The simple name of the implementation assembly that declares the member.</param>
/// <param name="Member">The member, from the implementation assembly.</param>
/// <param name="Depth">The number of calls from the member to the method that contains the escape (0 when the member does).</param>
/// <param name="Sink">The kind of escape at the end of the shortest path.</param>
/// <param name="Path">The shortest call path, from the member to the method that contains the escape.</param>
internal sealed record EscapeCandidate(string Assembly, MethodDefinition Member, int Depth, EscapeSink Sink, ImmutableArray<string> Path)
{
    /// <summary>Gets the member qualified by its declaring type, as <see cref="ReferencePack.QualifiedKey"/> names it.</summary>
    public string QualifiedKey => ReferencePack.QualifiedKey(Member);
}

/// <summary>
/// Scans every managed implementation assembly of the shared framework the tests run on for public members (and protected members
/// of unsealed types) that reach a concurrency escape: thread-pool queueing, <c>TaskScheduler.Default</c> and the members
/// that schedule onto it, <c>Thread.Start</c>, a real timer, or a compiler-generated async state machine. Reference
/// assemblies have no method bodies, so the scan reads the implementation; it follows <c>call</c>, <c>callvirt</c>, and
/// <c>newobj</c> to their declared target within one assembly (not into overrides, delegates, or other assemblies), up to
/// <see cref="ConcurrencyEscapeAudit.MaxDepth"/> calls. Each assembly is read and scanned once per test run, in parallel, and the result is
/// ordered by qualified key, so it is deterministic.
/// </summary>
internal static class ConcurrencyEscapeScanner
{
    private const MethodImplAttributes RuntimeAsync = (MethodImplAttributes)System.Reflection.MethodImplAttributes.Async;

    private static readonly ConcurrentDictionary<string, Lazy<ImmutableArray<EscapeCandidate>>> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the directory of the shared framework scanned: the one the tests run on, or the directory named by
    /// <c>ORLEANS_SIMULATION_ESCAPE_FRAMEWORK_DIRECTORY</c>, which checks another operating system's implementation assemblies
    /// (an extracted <c>Microsoft.NETCore.App.Runtime.&lt;rid&gt;</c> pack) against the same reference pack.
    /// </summary>
    public static string FrameworkDirectory { get; } =
        Environment.GetEnvironmentVariable("ORLEANS_SIMULATION_ESCAPE_FRAMEWORK_DIRECTORY") is { Length: > 0 } directory
            ? directory
            : Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    /// <summary>Gets the simple names of the managed assemblies in <see cref="FrameworkDirectory"/>, in ordinal order.</summary>
    public static ImmutableArray<string> AssemblyNames { get; } =
    [
        .. Directory.GetFiles(FrameworkDirectory, "*.dll")
            .Where(IsManaged)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Returns the candidates of every managed assembly of the shared framework, ordered by qualified key.</summary>
    public static ImmutableArray<EscapeCandidate> ScanAll()
    {
        var results = new ImmutableArray<EscapeCandidate>[AssemblyNames.Length];
        Parallel.For(0, AssemblyNames.Length, index => results[index] = Scan(AssemblyNames[index]));
        return [.. results.SelectMany(result => result).OrderBy(candidate => candidate.QualifiedKey, StringComparer.Ordinal)];
    }

    /// <summary>Returns the candidates of one implementation assembly, read and scanned once per test run.</summary>
    public static ImmutableArray<EscapeCandidate> Scan(string assemblyName) =>
        Cache.GetOrAdd(assemblyName, name => new Lazy<ImmutableArray<EscapeCandidate>>(() => ScanCore(name))).Value;

    private static ImmutableArray<EscapeCandidate> ScanCore(string assemblyName)
    {
        // Each assembly has its own resolver and module, so the parallel scans share no Cecil state. A call within the
        // module resolves without loading another assembly.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(FrameworkDirectory);
        ModuleDefinition module = ModuleDefinition.ReadModule(
            Path.Combine(FrameworkDirectory, assemblyName + ".dll"),
            new ReaderParameters { AssemblyResolver = resolver, InMemory = true });

        var callers = new Dictionary<MethodDefinition, List<(MethodDefinition Caller, int Cost)>>();
        var sinks = new Dictionary<MethodDefinition, EscapeSink>();
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods)
            {
                // A method compiled with runtime async suspends and resumes through the runtime's continuations.
                if ((method.ImplAttributes & RuntimeAsync) != 0)
                {
                    sinks[method] = EscapeSink.AsyncMethod;
                }

                if (!method.HasBody)
                {
                    continue;
                }

                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) || instruction.Operand is not MethodReference callee)
                    {
                        continue;
                    }

                    if (CallSink(callee) is EscapeSink sink)
                    {
                        sinks.TryAdd(method, sink);
                    }

                    if (InModule(callee, module) is MethodDefinition target && target != method)
                    {
                        if (!callers.TryGetValue(target, out List<(MethodDefinition, int)>? list))
                        {
                            callers[target] = list = [];
                        }

                        list.Add((method, CallCost(method, target)));
                    }
                }
            }
        }

        // A 0-1 breadth-first search from the escapes along reversed call edges finds each method's shortest path to
        // one. Methods and their callers are visited in metadata order, so the path recorded for a candidate is
        // deterministic.
        var next = new Dictionary<MethodDefinition, MethodDefinition?>();
        var depth = new Dictionary<MethodDefinition, int>();
        var sinkOf = new Dictionary<MethodDefinition, EscapeSink>();
        var pending = new LinkedList<MethodDefinition>();
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (sinks.TryGetValue(method, out EscapeSink sink))
                {
                    next[method] = null;
                    depth[method] = 0;
                    sinkOf[method] = sink;
                    pending.AddLast(method);
                }
            }
        }

        while (pending.First is { } first)
        {
            MethodDefinition method = first.Value;
            pending.RemoveFirst();
            if (!callers.TryGetValue(method, out List<(MethodDefinition Caller, int Cost)>? list))
            {
                continue;
            }

            foreach ((MethodDefinition caller, int cost) in list)
            {
                int distance = depth[method] + cost;
                if (distance > ConcurrencyEscapeAudit.MaxDepth || (depth.TryGetValue(caller, out int known) && known <= distance))
                {
                    continue;
                }

                depth[caller] = distance;
                next[caller] = method;
                sinkOf[caller] = sinkOf[method];
                if (cost == 0)
                {
                    pending.AddFirst(caller);
                }
                else
                {
                    pending.AddLast(caller);
                }
            }
        }

        var candidates = ImmutableArray.CreateBuilder<EscapeCandidate>();
        foreach (TypeDefinition type in module.GetTypes())
        {
            if (!ReferencePack.IsVisible(type) || type.BaseType?.FullName == "System.MulticastDelegate")
            {
                continue;
            }

            foreach (MethodDefinition method in type.Methods)
            {
                if (!IsCallable(method) || !depth.TryGetValue(method, out int distance))
                {
                    continue;
                }

                var path = ImmutableArray.CreateBuilder<string>();
                for (MethodDefinition? step = method; step is not null; step = next[step])
                {
                    path.Add(step.FullName);
                }

                candidates.Add(new EscapeCandidate(assemblyName, method, distance, sinkOf[method], path.ToImmutable()));
            }
        }

        return candidates.ToImmutable();
    }

    // The shared framework directory also holds the runtime's native libraries, which carry no metadata.
    private static bool IsManaged(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
        return reader.HasMetadata;
    }
    // A call to another overload of the caller, or to a local function or lambda the compiler generated from a method's
    // body, continues the caller's own work, so it does not count toward the depth; any other call does.
    private static int CallCost(MethodDefinition caller, MethodDefinition callee) =>
        (callee.DeclaringType == caller.DeclaringType && callee.Name == caller.Name) || callee.Name.StartsWith('<') ? 0 : 1;

    // A public member, or a protected member a derived type outside the assembly can call.
    private static bool IsCallable(MethodDefinition method) =>
        method.IsPublic || ((method.IsFamily || method.IsFamilyOrAssembly) && !method.DeclaringType.IsSealed);

    private static EscapeSink? CallSink(MethodReference callee)
    {
        TypeReference declaringType = callee.DeclaringType is GenericInstanceType generic ? generic.ElementType : callee.DeclaringType;
        string name = callee.Name;
        return declaringType.FullName switch
        {
            "System.Threading.ThreadPool" when name.Contains("Queue", StringComparison.Ordinal) || name.Contains("RegisterWait", StringComparison.Ordinal) => EscapeSink.ThreadPool,
            "System.Threading.Tasks.TaskScheduler" when name == "get_Default" => EscapeSink.TaskScheduler,
            "System.Threading.Tasks.Task" when name is "Run" or "Start" => EscapeSink.TaskScheduler,
            "System.Threading.Tasks.TaskFactory" or "System.Threading.Tasks.TaskFactory`1" when name == "StartNew" => EscapeSink.TaskScheduler,
            "System.Threading.Thread" when name is "Start" or "UnsafeStart" => EscapeSink.Thread,
            "System.Threading.Timer" or "System.Threading.TimerQueueTimer" when name == ".ctor" => EscapeSink.Timer,

            // The shared framework is trimmed without its state-machine attributes, so an async method is recognised by
            // the builder its kickoff starts, and an async iterator by the builder its state machine creates.
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder" or "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1"
                or "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder" or "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1"
                or "System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder" or "System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1"
                or "System.Runtime.CompilerServices.AsyncVoidMethodBuilder" when name == "Start" => EscapeSink.AsyncMethod,
            "System.Runtime.CompilerServices.AsyncIteratorMethodBuilder" when name == "Create" => EscapeSink.AsyncMethod,
            _ => null,
        };
    }

    // The definition of a call target the module declares, without loading another assembly.
    private static MethodDefinition? InModule(MethodReference callee, ModuleDefinition module)
    {
        MethodReference element = callee is GenericInstanceMethod instance ? instance.ElementMethod : callee;
        if (element is MethodDefinition definition)
        {
            return definition.Module == module ? definition : null;
        }

        TypeReference declaringType = element.DeclaringType is GenericInstanceType generic ? generic.ElementType : element.DeclaringType;
        return declaringType is TypeDefinition local && local.Module == module ? element.Resolve() : null;
    }
}
