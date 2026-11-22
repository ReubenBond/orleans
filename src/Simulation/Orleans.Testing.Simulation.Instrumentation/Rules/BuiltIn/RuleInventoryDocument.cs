using System.Collections.Immutable;
using System.Text;
using Orleans.Testing.Simulation.Runtime.Policy;

namespace Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;

/// <summary>
/// Renders the built-in rule inventories (<see cref="BuiltInRuleSets.DeterministicBclInventory"/> and
/// <see cref="BuiltInRuleSets.ControlledTasksInventory"/>), the members of their targeted types that stay BCL calls
/// (<see cref="UnrewrittenMembers"/>), and the concurrency status table with the audited framework members
/// (<see cref="ConcurrencyEscapeAudit"/>) as stable Markdown. The output is committed to <c>docs/rule-inventory.md</c> and
/// verified against this renderer by a test, so the published inventory can never silently drift from the shipped rule
/// set. The rendering is deterministic: families and rules follow their canonical declared order, and listed members
/// are grouped by declaring type in ordinal order, then by reason.
/// </summary>
public static class RuleInventoryDocument
{
    /// <summary>
    /// Gets the families whose rules control or reject a concurrency entry point: the members which start, schedule,
    /// await, or wait for work. The concurrency status table has a row for each, and a test checks that every family
    /// whose rules classify a member the concurrency-escape audit finds is among them.
    /// </summary>
    internal static ImmutableArray<BuiltInRuleFamily> ConcurrencyFamilies { get; } =
    [
        BuiltInRuleFamily.TaskScheduling,
        BuiltInRuleFamily.TaskFactory,
        BuiltInRuleFamily.TaskContinuations,
        BuiltInRuleFamily.TaskFactoryContinuations,
        BuiltInRuleFamily.TaskFactoryFromAsync,
        BuiltInRuleFamily.TaskCompletionSource,
        BuiltInRuleFamily.TaskCombinators,
        BuiltInRuleFamily.TaskSynchronization,
        BuiltInRuleFamily.TaskTime,
        BuiltInRuleFamily.AsyncMachinery,
        BuiltInRuleFamily.ValueTaskMachinery,
        BuiltInRuleFamily.ValueTaskConversions,
        BuiltInRuleFamily.Thread,
        BuiltInRuleFamily.ThreadPool,
        BuiltInRuleFamily.Timers,
        BuiltInRuleFamily.CancellationTimers,
        BuiltInRuleFamily.Parallel,
        BuiltInRuleFamily.Plinq,
        BuiltInRuleFamily.Channels,
        BuiltInRuleFamily.BlockingCollection,
        BuiltInRuleFamily.AsyncIO,
        BuiltInRuleFamily.BackgroundWork,
        BuiltInRuleFamily.Dataflow,
        BuiltInRuleFamily.WebSockets,
        BuiltInRuleFamily.HttpClient,
        BuiltInRuleFamily.Hosting,
        BuiltInRuleFamily.Semaphore,
        BuiltInRuleFamily.SynchronizationContext,
        BuiltInRuleFamily.UncontrolledInvocation,
    ];

    /// <summary>Renders the full inventory document.</summary>
    /// <returns>The Markdown text, using <c>\n</c> line endings and a trailing newline.</returns>
    public static string Render()
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line("# Built-in rewrite rule inventory");
        Line();
        Line("<!-- Generated from Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn.RuleInventoryDocument.Render().");
        Line("     Do not edit by hand; a test verifies this file matches the shipped rule set. -->");
        Line();
        Line(
            "This is the exact, exhaustive surface the built-in rule sets redirect. Every other API is " +
            "**not** rewritten. Instrumented closure binaries are simulation/test artifacts: every Controlled " +
            "entry point requires an active Clockwork simulation, and an active simulation with no registered " +
            "runtime service fails explicitly rather than use real time, randomness, or an uncontrolled task. " +
            "Uninstrumented production binaries retain ordinary BCL behavior.");
        Line();

        RenderSet(
            sb,
            "Deterministic BCL rule set",
            BuiltInRuleSets.DeterministicBclId,
            BuiltInRuleSets.DeterministicBclVersion,
            BuiltInRuleSets.DeterministicBclInventory);

        RenderSet(
            sb,
            "Controlled task rule set",
            BuiltInRuleSets.ControlledTasksId,
            BuiltInRuleSets.ControlledTasksVersion,
            BuiltInRuleSets.ControlledTasksInventory);

        Line("# Race exploration instrumentation inventory");
        Line();
        Line("This inventory is enabled only when instrumentation mode is `RaceExploration`; `Controlled` mode injects none of these calls.");
        Line();
        Line("| Surface | Instrumentation | Tracked identity |");
        Line("| --- | --- | --- |");
        Line("| `ldfld` / `stfld` on reference types | Read/write scheduling point | Weak object identity + field member |");
        Line("| `ldsfld` / `stsfld` | Read/write scheduling point | Static field member |");
        Line("| volatile field access | Schedule-only point before the `volatile.` prefix | Not race-tracked |");
        Line("| `ldelem.*` / `stelem.*` vector arrays | Read/write scheduling point | Weak array identity + element index |");
        Line("| field-address, indirect/object, and `ldelema` access | Schedule-only point | Not race-tracked |");
        Line("| `brtrue` / `brfalse` | Control-flow scheduling point | n/a |");
        Line("| `List<T>`, `Dictionary<TKey,TValue>`, `HashSet<T>` direct concrete members | Read/write/iteration point after the original call | Weak collection identity |");
        Line("| `ConcurrentBag<T>`, `ConcurrentDictionary<TKey,TValue>`, `ConcurrentQueue<T>`, `ConcurrentStack<T>` direct concrete members | Interleaving point after the original call | Not reported as a race |");
        Line();
        Line("Limits: constructors and property accessors are excluded; generated `MoveNext` methods are visited, with generated value-type state fields schedule-only. Multidimensional arrays, interface-typed and tail-prefixed collection calls, reflection/dynamic dispatch, spans, unmanaged memory, and arbitrary pointer offsets are not assigned tracked locations.");
        Line();

        RenderUnrewrittenMembers(sb);

        RenderConcurrencyStatus(sb);

        Line("# Documented holes (not rewritten in these rule sets)");
        Line();
        Line("These nondeterministic or entropy-drawing surfaces are intentionally **not** covered and");
        Line("remain real BCL calls even under simulation:");
        Line();
        string[] holeReasons = [.. Enum.GetValues<UnrewrittenReason>().Where(IsDocumentedHole).Select(reason => Code(reason.ToString()))];
        Line($"- Every member listed above with the {string.Join(", ", holeReasons[..^1])}, or {holeReasons[^1]} reason.");
        Line("- A delegate created from a member, such as `Func<bool> responds = process.CloseMainWindow`, calls the BCL member: the rules rewrite `call`, `callvirt`, and `newobj` sites, and a delegate is created with `ldftn` or `ldvirtftn`.");
        Line("- Blocking `ValueTask<T>.Result` reads: the accessor is not rewritten, so it would block the logical thread on an incomplete value task. `await`, or `GetAwaiter().GetResult()` through the controlled awaiter (which pumps until the value task completes), is the supported controlled path.");
        Line("- Continuations registered by uninstrumented code (an assembly excluded from rewriting, or the framework itself) follow the BCL's dispatch, so a source which runs its continuations asynchronously runs them on the thread pool.");
        Line("- The framework members listed under [Concurrency entry points: status](#concurrency-entry-points-status) with a reason, and any work a BCL call starts on another thread (the table's last row).");
        Line("- Named/cross-process synchronization (named `EventWaitHandle`/`Mutex`/`Semaphore` and their `OpenExisting`/`TryOpenExisting` APIs): a single-process simulation cannot model kernel-object sharing, so these are rejected.");
        Line("- Custom `TimeProvider` implementations are rejected by timer-consuming controlled APIs unless Clockwork explicitly recognizes them. `System.Timers.Timer` rejects non-null `SynchronizingObject` and designer `Site` integration because those paths can marshal callbacks to uncontrolled UI or native threads. `Timer.Dispose(WaitHandle)` accepts controlled event handles only.");
        Line();
        Line("Determinism is claimed **only** for the exact rules tabulated above.");

        return sb.ToString();
    }

    private static void RenderSet(
        StringBuilder sb,
        string title,
        string id,
        string version,
        ImmutableArray<(BuiltInRuleFamily Family, RewriteRule Rule)> inventory)
    {
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line($"# {title}");
        Line();
        Line($"Rule set id: {Code(id)}");
        Line($"Version: {Code(version)}");
        Line($"Supported runtime: {Code(BuiltInRuleSets.SupportedRuntime.ToString())}");
        Line($"Shim assembly: {Code(BuiltInRuleSets.ShimAssemblyName)}");
        Line();

        foreach (BuiltInRuleFamily family in BuiltInRuleSets.AllFamilies)
        {
            ImmutableArray<RewriteRule> rules =
                [.. inventory.Where(e => e.Family == family).Select(e => e.Rule)];
            if (rules.IsEmpty)
            {
                continue;
            }

            Line($"## {family} family");
            Line();
            Line($"Policy: **{DescribePolicy(rules)}**. {DescribeFamily(family)}");
            Line();
            Line("| Rule id | BCL target | Shim | Policy |");
            Line("| --- | --- | --- | --- |");
            foreach (RewriteRule rule in rules)
            {
                string target = rule.Operation == RewriteOperationKind.RedirectNewObj
                    ? "new " + rule.Target.DeclaringTypeFullName + ParamSuffix(rule.Target.ParameterTypeFullNames)
                    : Display(rule.Target);
                Line($"| {Code(rule.Id)} | {Code(target)} | {Code(Display(rule.Replacement))} | {rule.Policy} |");
            }

            Line();
        }
    }

    private static string DescribePolicy(ImmutableArray<RewriteRule> rules) =>
        rules.All(r => r.Policy == SimulationApiPolicy.Rejected) ? "Rejected" : "Controlled";

    private static void RenderUnrewrittenMembers(StringBuilder sb)
    {
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line("# Members left as BCL calls");
        Line();
        Line(
            "Every public and protected member of a type the built-in rules target is controlled or rejected by a rule " +
            "above, or is listed below with the reason a call to it stays a BCL call. A type is targeted when a rule " +
            "names or substitutes it, or when a member's signature mentions a class, struct, or interface of " +
            "`System.Diagnostics.Process.dll`. A test classifies each targeted type against the reference assemblies of " +
            "the supported runtime, so a member a new runtime adds fails that test until it is classified. Snapshots of " +
            "the reference pack's assemblies and of the public types in the namespaces of the targeted types fail when a " +
            "runtime adds or removes one.");
        Line();
        Line("| Reason | Behavior | Documented hole |");
        Line("| --- | --- | --- |");
        foreach (UnrewrittenReason reason in Enum.GetValues<UnrewrittenReason>())
        {
            Line($"| {Code(reason.ToString())} | {DescribeReason(reason)} | {(IsDocumentedHole(reason) ? "Yes" : "No")} |");
        }

        Line();
        foreach (IGrouping<string, UnrewrittenMember> typeMembers in UnrewrittenMembers.All
            .GroupBy(member => member.DeclaringTypeFullName)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Line($"## {Code(typeMembers.Key)}");
            Line();
            foreach (IGrouping<UnrewrittenReason, UnrewrittenMember> reasonMembers in typeMembers
                .GroupBy(member => member.Reason)
                .OrderBy(group => group.Key))
            {
                Line($"- {Code(reasonMembers.Key.ToString())}: " + string.Join(", ", reasonMembers.Select(member => Code(member.MemberKey))));
            }

            Line();
        }
    }

    // The concurrency status table has a row for each concurrency family, for each reason a member which schedules, blocks,
    // or awaits stays a BCL call, and for each reason a member the concurrency-escape audit finds needs no rule, then a row
    // for everything else. The audited members follow, grouped by declaring type.
    private static void RenderConcurrencyStatus(StringBuilder sb)
    {
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line("# Concurrency entry points: status");
        Line();
        Line(
            "A test scans every managed implementation assembly of the shared framework the tests run on for the public " +
            "members, and protected members of unsealed types, of the reference pack which reach a concurrency escape: " +
            "thread-pool queueing, `TaskScheduler.Default` or a member which schedules onto it (`Task.Run`, `Task.Start`, " +
            "`TaskFactory.StartNew`), `Thread.Start`, a real timer, or an async method. It follows calls within one " +
            $"assembly, up to {ConcurrencyEscapeAudit.MaxDepth} deep, not counting a call to another overload or to a local " +
            "function or lambda compiled from the caller's body. Every member it finds is controlled or rejected by a rule, " +
            "listed under [Members left as BCL calls](#members-left-as-bcl-calls), listed under " +
            "[Audited framework members](#audited-framework-members) with the reason it needs no rule, or declared by an " +
            "assembly listed under [Audited framework assemblies](#audited-framework-assemblies), whose reason covers its " +
            "members. The implementation assemblies differ by operating system, so the lists hold the candidates of " +
            "Windows, Linux, and macOS. A runtime which adds or removes such a member fails the test until it is classified; " +
            "the members classified by their assembly are kept in a snapshot, so one a runtime adds to such an assembly " +
            "fails until it is reviewed against the assembly's reason.");
        Line();
        Line("| Entry points | Status under simulation |");
        Line("| --- | --- |");

        ImmutableArray<(BuiltInRuleFamily Family, RewriteRule Rule)> inventory =
            [.. BuiltInRuleSets.DeterministicBclInventory, .. BuiltInRuleSets.ControlledTasksInventory];
        foreach (BuiltInRuleFamily family in ConcurrencyFamilies)
        {
            ImmutableArray<RewriteRule> rules = [.. inventory.Where(entry => entry.Family == family).Select(entry => entry.Rule)];
            string[] names = [.. rules.Select(EntryPoint).Distinct(StringComparer.Ordinal)];
            string[] rejected =
            [
                .. names
                    .Select(name => (Name: name, Policies: rules.Where(rule => EntryPoint(rule) == name).Select(rule => rule.Policy).ToArray()))
                    .Where(entry => entry.Policies.Contains(SimulationApiPolicy.Rejected))
                    .Select(entry => entry.Policies.All(policy => policy == SimulationApiPolicy.Rejected)
                        ? Code(entry.Name)
                        : Code(entry.Name) + " (some overloads)"),
            ];
            string status = rejected.Length == 0 ? "Controlled"
                : rejected.Length == names.Length && rules.All(rule => rule.Policy == SimulationApiPolicy.Rejected) ? "Rejected"
                : "Controlled; rejected: " + string.Join(", ", rejected);
            Line($"| {string.Join(", ", names.Select(Code))} | {status} ([`{family}` family](#{family.ToString().ToLowerInvariant()}-family)). |");
        }

        foreach (UnrewrittenReason reason in (ReadOnlySpan<UnrewrittenReason>)[UnrewrittenReason.Schedules, UnrewrittenReason.Blocks, UnrewrittenReason.Channel, UnrewrittenReason.RuntimeAsync])
        {
            string[] names =
            [
                .. UnrewrittenMembers.All
                    .Where(member => member.Reason == reason)
                    .Select(member => EntryPoint(member.DeclaringTypeFullName, member.MemberKey[..member.MemberKey.IndexOf('(', StringComparison.Ordinal)]))
                    .Distinct(StringComparer.Ordinal),
            ];
            Line($"| {string.Join(", ", names.Select(Code))} | BCL call ({Code(reason.ToString())}): {DescribeReason(reason)} |");
        }

        foreach (EscapeReason reason in Enum.GetValues<EscapeReason>())
        {
            string[] types =
            [
                .. ConcurrencyEscapeAudit.All
                    .Where(member => member.Reason == reason)
                    .Select(member => ShortTypeName(member.DeclaringTypeFullName))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(Code),
            ];
            string[] assemblies =
            [
                .. ConcurrencyEscapeAudit.Assemblies
                    .Where(assembly => assembly.Reason == reason)
                    .Select(assembly => Code(assembly.Name)),
            ];
            var entries = new List<string>();
            if (types.Length > 0)
            {
                entries.Add($"{string.Join(", ", types)} ([members](#audited-framework-members))");
            }

            if (assemblies.Length > 0)
            {
                entries.Add($"the assemblies {string.Join(", ", assemblies)} ([assemblies](#audited-framework-assemblies))");
            }

            if (entries.Count > 0)
            {
                Line($"| {string.Join("; ", entries)} | {Code(reason.ToString())}: {DescribeEscape(reason)} |");
            }
        }

        Line(
            "| Continuations which uninstrumented code (an assembly excluded from rewriting, or the framework) registers on " +
            "a task | Follow the BCL's dispatch ([documented holes](#documented-holes-not-rewritten-in-these-rule-sets)). |");
        Line(
            "| Everything else | Runs as a BCL call. Work it starts on another thread fails the run at its first call to a " +
            "controlled API, which throws `SimulationThreadAffinityException` ([thread affinity](compatibility.md#thread-affinity)); " +
            "work which only computes or changes shared state runs unchecked, racing the logical thread. |");
        Line();

        Line("## Audited framework assemblies");
        Line();
        Line("| Assembly | Reason | Behavior under simulation |");
        Line("| --- | --- | --- |");
        foreach (AuditedAssembly assembly in ConcurrencyEscapeAudit.Assemblies.OrderBy(assembly => assembly.Name, StringComparer.Ordinal))
        {
            Line($"| {Code(assembly.Name)} | {Code(assembly.Reason.ToString())} | {assembly.Behavior} |");
        }

        Line();
        Line("## Audited framework members");
        Line();
        foreach (IGrouping<string, AuditedEscape> typeMembers in ConcurrencyEscapeAudit.All
            .GroupBy(member => member.DeclaringTypeFullName)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Line($"### {Code(typeMembers.Key)}");
            Line();
            foreach (IGrouping<EscapeReason, AuditedEscape> reasonMembers in typeMembers
                .GroupBy(member => member.Reason)
                .OrderBy(group => group.Key))
            {
                Line($"- {Code(reasonMembers.Key.ToString())}: " + string.Join(", ", reasonMembers.Select(member => Code(member.MemberKey))));
            }

            Line();
        }
    }

    private static string DescribeEscape(EscapeReason reason) => reason switch
    {
        EscapeReason.FrameworkAwait =>
            "A framework async method or adapter. It runs on the caller until it awaits; its continuations follow the BCL's " +
            "dispatch. A bare continuation runs inline where Clockwork completes the awaited operation " +
            "([Completions from rewritten code](compatibility.md#completions-from-rewritten-code)), and one which " +
            "captured a lane's context is posted to that lane. It runs on the thread pool when the operation runs its " +
            "continuations asynchronously, or when uninstrumented code completes it where a context or scheduler of the " +
            "simulation is current; the thread-affinity check catches its first controlled call there.",
        EscapeReason.OsResource =>
            "Performs I/O on an OS file, socket, or network connection, which completes on the operating system's I/O " +
            "threads or the thread pool. A simulation performs I/O through the system under test's own abstractions, " +
            "which it substitutes.",
        EscapeReason.SchedulerValue =>
            "Reads or stores `TaskScheduler.Default` or the current scheduler without queueing work to it. The controlled " +
            "APIs which take a scheduler accept `TaskScheduler.Default` and run the work on the coordinator.",
        EscapeReason.CustomScheduler =>
            "Belongs to a task scheduler other than the coordinator's, which queues to the thread pool. Every controlled " +
            "API which takes a scheduler rejects it, so controlled work never reaches it.",
        EscapeReason.ValueTaskSource =>
            "Completes, or registers a continuation on, an `IValueTaskSource`, whose continuations can run on the thread " +
            "pool. Controlled awaits poll the source's status and register no continuation, so such a continuation comes " +
            "only from uninstrumented code.",
        EscapeReason.UnhandledException =>
            "Rethrows an exception on the thread pool when it escapes an `async void` method or an awaiter's `OnCompleted` " +
            "throws, which ends the process, as in .NET.",
        EscapeReason.AffinityBackstop =>
            "Runs a callback of the caller's on a thread of the BCL's choosing. The thread-affinity check fails the run at " +
            "the callback's first controlled call; a callback which only computes or changes shared state runs unchecked.",
        EscapeReason.CompletesOnCaller =>
            "Completes on the caller: the receiver's overrides of the members it awaits complete synchronously.",
        EscapeReason.WaitsForFrameworkWork =>
            "Runs framework code, and none of the caller's, on another thread while the calling thread blocks until it " +
            "finishes, so the call returns the BCL's result on the logical thread with no scheduling point.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    // The API a rule targets, as source code names it: Type.Member, new Type, or Type for a substituted type.
    private static string EntryPoint(RewriteRule rule) =>
        rule.Operation == RewriteOperationKind.SubstituteType
            ? ShortTypeName(rule.Target.DeclaringTypeFullName)
            : EntryPoint(rule.Target.DeclaringTypeFullName, rule.Operation == RewriteOperationKind.RedirectNewObj ? ".ctor" : rule.Target.MemberName);

    private static string EntryPoint(string declaringTypeFullName, string? memberName)
    {
        string type = ShortTypeName(declaringTypeFullName);
        if (memberName is null)
        {
            return type;
        }

        int arity = memberName.IndexOf("``", StringComparison.Ordinal);
        string name = arity < 0 ? memberName : memberName[..arity];
        if (name == ".ctor")
        {
            return "new " + type;
        }

        foreach (string accessor in (ReadOnlySpan<string>)["get_", "set_", "add_", "remove_"])
        {
            if (name.StartsWith(accessor, StringComparison.Ordinal))
            {
                return type + "." + name[accessor.Length..];
            }
        }

        return type + "." + name;
    }

    // A type as source code names it: no namespace, a dot before a nested type, and <T> (or <T1,T2>) for its arity.
    private static string ShortTypeName(string fullName)
    {
        string[] segments = fullName.Split('/');
        segments[0] = segments[0][(segments[0].LastIndexOf('.') + 1)..];
        for (int i = 0; i < segments.Length; i++)
        {
            int tick = segments[i].IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                int arity = int.Parse(segments[i][(tick + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                segments[i] = segments[i][..tick] + (arity == 1 ? "<T>" : "<" + string.Join(",", Enumerable.Range(1, arity).Select(n => "T" + n)) + ">");
            }
        }

        return string.Join(".", segments);
    }

    // A documented hole is a member whose BCL behavior can differ between runs or hosts, or escape the coordinator.
    private static bool IsDocumentedHole(UnrewrittenReason reason) => reason switch
    {
        UnrewrittenReason.Value or UnrewrittenReason.Dispatch or UnrewrittenReason.Fence or UnrewrittenReason.RuntimeAsync
            or UnrewrittenReason.AlwaysThrows or UnrewrittenReason.SubstitutedTypeProtected or UnrewrittenReason.Channel
            or UnrewrittenReason.SynchronousIO => false,
        UnrewrittenReason.CultureOrTimeZone or UnrewrittenReason.HostState or UnrewrittenReason.Process
            or UnrewrittenReason.RealClock or UnrewrittenReason.ProcessWideId or UnrewrittenReason.CurrentThread
            or UnrewrittenReason.IdentityHandle or UnrewrittenReason.ConstructorCall or UnrewrittenReason.Blocks
            or UnrewrittenReason.Schedules or UnrewrittenReason.ParallelQuery or UnrewrittenReason.FrameworkWrapper
            or UnrewrittenReason.DataflowBlock or UnrewrittenReason.BackgroundWork or UnrewrittenReason.WebSocket => true,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    private static string DescribeReason(UnrewrittenReason reason) => reason switch
    {
        UnrewrittenReason.Value =>
            "Computes its result from the receiver and its arguments, or creates, configures, or releases the receiver.",
        UnrewrittenReason.CultureOrTimeZone =>
            "Uses or sets the current culture, or converts local time with the host's time zone, so its result follows " +
            "the host's configuration.",
        UnrewrittenReason.HostState =>
            "Reads or changes state of the host process or machine (environment variables, paths, identity, privileges, " +
            "and processor, memory, and thread-pool statistics), so a simulation observes the host's values.",
        UnrewrittenReason.Process =>
            "Looks up, configures, reads, or releases an OS process or its handle, so a simulation observes the live " +
            "process. The members that start, signal, wait on, reschedule, or terminate a process are rejected " +
            "(`UncontrolledInvocation` family).",
        UnrewrittenReason.Dispatch =>
            "Runs on the receiver. `new Random()`, `Random.Shared`, `RandomNumberGenerator.Create`, and " +
            "`TimeProvider.System` return instances that draw from the simulation's seeded streams and virtual clock.",
        UnrewrittenReason.RealClock =>
            "Reads the host's monotonic clock. `Stopwatch.GetTimestamp()` and `Stopwatch.GetElapsedTime(long)` are " +
            "controlled.",
        UnrewrittenReason.ProcessWideId =>
            "Returns an identifier drawn from a process-wide counter, so its value depends on the tasks and threads the " +
            "process created before.",
        UnrewrittenReason.CurrentThread =>
            "Reads or changes the calling thread's state (its `Thread` object, thread-local data slots, principal, and " +
            "affinity hints), which belongs to the physical thread that runs every logical thread or flows with the " +
            "`ExecutionContext` as in the BCL.",
        UnrewrittenReason.IdentityHandle =>
            "Reads the BCL `Thread` object that a controlled thread keeps as its identity. That object never runs, so a " +
            "controlled thread reports itself unstarted.",
        UnrewrittenReason.ConstructorCall =>
            "A rule rewrites `new` of the type, but a constructor `call` to this one (a derived type's constructor " +
            "chaining to it, or a value type constructed in place) runs the BCL constructor, so the instance keeps BCL " +
            "state, such as an unseeded generator, a real timer, or a named kernel object.",
        UnrewrittenReason.Fence =>
            "Performs one memory access or fence, which the cooperative scheduler runs as an indivisible step. Race " +
            "exploration does not track it.",
        UnrewrittenReason.Blocks =>
            "Blocks the calling physical thread as in the BCL, which stalls the simulation until it returns. Use `await` " +
            "or a controlled overload.",
        UnrewrittenReason.Schedules =>
            "Runs its work through a BCL task scheduler, the thread pool, or a new physical thread rather than the " +
            "simulation's coordinator.",
        UnrewrittenReason.RuntimeAsync =>
            "Runtime-async intrinsic, which suspends its caller through the runtime's own continuation machinery. " +
            "Rewriting a method compiled with runtime async fails with `CWR0013`.",
        UnrewrittenReason.AlwaysThrows =>
            "Throws on .NET 11: `PlatformNotSupportedException`, or `InvalidOperationException` for " +
            "`GetCompressedStack` and `SetCompressedStack`.",
        UnrewrittenReason.SubstitutedTypeProtected =>
            "Protected member of a substituted type, which only a derived type can call. Rewriting a type derived from " +
            "a substituted type fails.",
        UnrewrittenReason.Channel =>
            "Reads or changes the channel's state on the calling thread, or returns a waiter which a later change to that " +
            "state completes. Controlled code makes those changes on the logical thread and awaits the waiter through a " +
            "controlled awaiter, so the member keeps its BCL behavior.",
        UnrewrittenReason.ParallelQuery =>
            "Operates on a PLINQ `ParallelQuery<T>`, which only the rejected `AsParallel`, `Range`, `Repeat`, and `Empty` " +
            "create (`Plinq` family). A query that uninstrumented code creates and passes to rewritten code runs on worker " +
            "tasks of `TaskScheduler.Default`, as in the BCL.",
        UnrewrittenReason.SynchronousIO =>
            "Reads, writes, flushes, or disposes on the calling thread, which is the logical thread: a synchronous I/O " +
            "member, or `DisposeAsync`, whose base implementation calls `Dispose`. The asynchronous members whose base " +
            "implementations run these on the thread pool are controlled (`AsyncIO` family).",
        UnrewrittenReason.FrameworkWrapper =>
            "Returns a framework wrapper over the given instances (`Stream.Synchronized`, `TextReader.Synchronized`, " +
            "`TextWriter.Synchronized`, `TextWriter.CreateBroadcasting`). The wrapper's own members are classified with its " +
            "type, but its calls to a wrapped instance are framework calls, so they reach that instance's base " +
            "asynchronous members as in the BCL: `Stream.Synchronized` reads and writes through the wrapped stream's " +
            "`BeginRead`/`BeginWrite`, and a broadcasting writer awaits each writer's asynchronous members.",
        UnrewrittenReason.DataflowBlock =>
            "Operates on a TPL Dataflow block, which rewritten code cannot create: every block constructor is rejected " +
            "(`Dataflow` family). A block which uninstrumented code creates and passes to rewritten code runs its work on " +
            "its options' `TaskScheduler` and its completion propagation on `TaskScheduler.Default`, as in the BCL.",
        UnrewrittenReason.BackgroundWork =>
            "Configures a `BackgroundWorker` or `BackgroundService`, raises its events on the caller, or observes or stops " +
            "work which only the rejected `RunWorkerAsync` and `StartAsync` begin (`BackgroundWork` family). Work which " +
            "a call through `IHostedService` starts, as a host makes, runs on the thread pool, where the thread-affinity " +
            "check catches its first controlled call.",
        UnrewrittenReason.WebSocket =>
            "Runs on the receiving `WebSocket`, or on the `WebSocket` a `WebSocketStream` wraps: an application's own " +
            "implementation, a `ClientWebSocket` over an OS connection, or one uninstrumented code creates. The " +
            "framework's implementation over a caller's stream is created only by the rejected `CreateFromStream` and " +
            "`CreateClientWebSocket` (`WebSockets` family).",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    // A Markdown code span whose delimiter is longer than any backtick run in the text, as Cecil names carry backticks
    // (Task`1, GetItems``1).
    private static string Code(string text)
    {
        int longestRun = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
        }

        if (longestRun == 0)
        {
            return "`" + text + "`";
        }

        string fence = new('`', longestRun + 1);
        return fence + " " + text + " " + fence;
    }

    private static string DescribeFamily(BuiltInRuleFamily family) => family switch
    {
        BuiltInRuleFamily.Clock =>
            "Wall-clock, offset-clock, monotonic timestamp, and tick-counter reads dispatch to the node's " +
            "simulated clock. Local-time APIs honour the configured simulation time zone; tick counters wrap " +
            "with correct `int`/`long` semantics.",
        BuiltInRuleFamily.Identity =>
            "GUIDs draw deterministic bytes while preserving RFC 4122 variant and version. `CreateVersion7` " +
            "encodes the simulated UTC millisecond timestamp in the first 48 bits; repeated calls at the same " +
            "simulated instant share that timestamp (no monotonicity guarantee beyond the BCL contract).",
        BuiltInRuleFamily.Random =>
            "`Random.Shared` and unseeded `new Random()` become per-node deterministic streams isolated from " +
            "the scheduler/network/Buggify seed domains; explicitly seeded `new Random(int)` preserves the " +
            "caller's seed exactly. Stable seed derivation uses `SimulationStableHash`. Every instance member, " +
            "including the generic `NextInteger<T>` and `NextBinaryFloat<T>`, draws from the instance's stream.",
        BuiltInRuleFamily.Crypto =>
            "Static random APIs are redirected to `ControlledRandomNumberGenerator` and draw from a " +
            "deterministic, per-node non-cryptographic stream. Uninstrumented production binaries retain " +
            "ordinary BCL behavior.",
        BuiltInRuleFamily.TaskCombinators =>
            "`Task.WhenAll`/`WhenAny` (the non-generic `Task[]`, `IEnumerable<Task>`, .NET 9+ params " +
            "`ReadOnlySpan<Task>`, and two-argument overloads, plus their generic `Task<TResult>` " +
            "counterparts) and the `TaskExtensions.Unwrap` extension methods redirect to controlled " +
            "combinators which register nothing on their antecedents. A result whose antecedents have already " +
            "completed is computed by the BCL synchronously; otherwise the coordinator polls the antecedents and " +
            "a controlled operation completes the result, with the BCL's argument validation, result, fault " +
            "aggregation, and cancellation. `WhenAny` returns the first antecedent, in argument order, among those " +
            "observed complete; the non-generic `WhenAll` aggregates faults in the order its antecedents are " +
            "observed complete, with antecedents observed in the same scheduling step in argument order. " +
            "`Task.WhenEach` (array, span, and enumerable, generic and non-generic) yields the tasks in the order the " +
            "coordinator observes them complete, from the call on, through an enumerator whose `MoveNextAsync` a " +
            "controlled operation completes, with the BCL's validation, single enumeration, and cancellation.",
        BuiltInRuleFamily.TaskSynchronization =>
            "Every `Task.Wait`, `Task.WaitAll`, and `Task.WaitAny` overload redirects to a controlled wait that pumps " +
            "the deterministic loop rather than blocking a physical thread, with timeouts that elapse in virtual time and the " +
            "BCL's results and exceptions; a never-satisfiable wait surfaces " +
            "as a precise deadlock diagnostic instead of hanging the scheduler. " +
            "`TaskAsyncEnumerableExtensions.ToBlockingEnumerable` redirects to an enumerable that pumps the loop " +
            "until each `MoveNextAsync` and the final `DisposeAsync` complete, with the BCL's token, early-exit " +
            "disposal, and exception semantics.",
        BuiltInRuleFamily.TaskContinuations =>
            "All 40 .NET 11 `Task.ContinueWith` and `Task<T>.ContinueWith` overloads (with and without state, a " +
            "`CancellationToken`, `TaskContinuationOptions`, and a `TaskScheduler`) redirect. The result is a genuine " +
            "BCL continuation task of a private gate, so its status, options, state, cancellation (including " +
            "`LazyCancellation`), `NotOn*`/`OnlyOn*` filtering, and exceptions are the BCL's. The coordinator observes " +
            "the antecedent complete and a controlled operation completes the gate, running the continuation inline on " +
            "the logical thread (`ExecuteSynchronously` is a hint). The overloads without a scheduler use " +
            "`TaskScheduler.Default`; an explicit `TaskScheduler.Default` or the simulation's own `SimulationTaskScheduler` " +
            "is accepted. `LongRunning`, `AttachedToParent`, and any other scheduler cannot be preserved and are rejected.",
        BuiltInRuleFamily.TaskCompletionSource =>
            "Rewritten code keeps the real `TaskCompletionSource` and `TaskCompletionSource<TResult>`, with their real " +
            "creation options. The completion members (`SetResult`, `SetException`, `SetCanceled`, `SetFromTask`, and " +
            "their `TrySet*` forms) redirect to shims which complete the source through the same BCL member and " +
            "publish the race-detection release edge an await, wait, combinator, or continuation acquires. Outside a " +
            "simulation (in a finalizer, or a callback from uninstrumented code) the shims call the BCL member alone. " +
            "The constructors which take creation options redirect to factories which reject `AttachedToParent` inside " +
            "a simulation, as the `Task` constructors do: Clockwork's controlled tasks do not model parent and child tasks.",
        BuiltInRuleFamily.TaskTime =>
            "`Task.Delay` and `Task.WaitAsync` use controlled virtual deadlines, preserve cancellation and " +
            "terminal task state, and never consume wall-clock time.",
        BuiltInRuleFamily.TaskScheduling =>
            "`Task.Run` (all `Action`/`Func<TResult>`/`Func<Task>`/`Func<Task<TResult>>` overloads, with and " +
            "without a `CancellationToken`) redirects thread-pool scheduling into controlled operations. " +
            "Each overload redirects to a controlled equivalent that schedules the delegate as a controlled " +
            "operation on the simulation coordinator, preserving cancellation and unwrap semantics. `Task.Start` and " +
            "`Task.RunSynchronously` run a task constructed with `new Task(...)` on the coordinator's `TaskScheduler`, " +
            "with the BCL's validation and status transitions; a custom scheduler is rejected.",
        BuiltInRuleFamily.AsyncMachinery =>
            "The compiler-generated builder and awaiter types of an `async` state machine " +
            "(`AsyncTaskMethodBuilder`, `TaskAwaiter`, `ConfiguredTaskAwaitable`/`YieldAwaitable` and their " +
            "awaiters, generic and non-generic) are substituted onto Clockwork's controlled equivalents by " +
            "the member-aware pass, and `Task.Yield()` redirects to the controlled yield. Every awaited " +
            "continuation is scheduled through the simulation coordinator instead of the thread pool, and " +
            "`ConfigureAwait(false)` stays controlled. `AsyncHelpers.HandleAsyncEntryPoint`, through which an async " +
            "`Main` returns, waits for its task through the controlled awaiter. A method compiled with runtime async has " +
            "no state machine and resumes through the runtime's own continuation machinery, so rewriting one fails " +
            "with `CWR0013`; the runtime-async `AsyncHelpers.Await` intrinsics taking a configured task awaitable are " +
            "rejected.",
        BuiltInRuleFamily.ValueTaskMachinery =>
            "The compiler-generated builder and awaiter types of an `async ValueTask`/`async ValueTask<T>` " +
            "state machine (`AsyncValueTaskMethodBuilder`, `ValueTaskAwaiter`, `ConfiguredValueTaskAwaitable` " +
            "and their awaiters, generic and non-generic) are substituted onto Clockwork's controlled " +
            "equivalents by the member-aware pass, so every awaited value-task continuation is scheduled " +
            "through the simulation coordinator. `ConfigureAwait(false)` stays controlled. The configured async " +
            "disposable and enumerable that `await using` and `await foreach` consume " +
            "(`ConfiguredAsyncDisposable`, `ConfiguredCancelableAsyncEnumerable<T>` and its `Enumerator`) are " +
            "substituted with them, and the `TaskAsyncEnumerableExtensions.ConfigureAwait`/`WithCancellation` " +
            "extensions redirect to factories for the controlled types, so iteration and dispose awaits resume " +
            "through the coordinator and the configured token reaches `GetAsyncEnumerator` as in the BCL. " +
            "The runtime-async `AsyncHelpers.Await` intrinsics taking a configured value-task awaitable are " +
            "rejected. Synchronous blocking on a value task is not rewritten " +
            "(a value task may be consumed only once); `await` is the supported controlled path.",
        BuiltInRuleFamily.ValueTaskConversions =>
            "`ValueTask.AsTask`, `ValueTask<T>.AsTask`, `ValueTask.Preserve`, and `ValueTask<T>.Preserve` redirect. A " +
            "value task backed by a pending `IValueTaskSource` is polled through the source's status by the " +
            "coordinator, with no completion callback registered on the source, and a controlled operation consumes " +
            "the completed source once through the BCL's own conversion. Any other value task is converted by the " +
            "BCL itself, synchronously.",
        BuiltInRuleFamily.TaskFactory =>
            "All 24 .NET 11 `TaskFactory.StartNew` and `TaskFactory<T>.StartNew` overloads are classified, " +
            "including state-carrying delegates and the full cancellation/options/scheduler forms. Each " +
            "redirects to a controlled equivalent that schedules the delegate as a fresh logical strand while " +
            "preserving state, cancellation, and results. Non-default schedulers and creation options whose " +
            "semantics cannot be preserved are rejected precisely.",
        BuiltInRuleFamily.TaskFactoryContinuations =>
            "All 48 .NET 11 `TaskFactory` and `TaskFactory<T>` `ContinueWhenAll` and `ContinueWhenAny` overloads " +
            "redirect. The result is a genuine BCL continuation task of a private gate, with the BCL's argument " +
            "validation, creation options, state (the continuation delegate), and cancellation, including a task canceled " +
            "at creation for a canceled token. The coordinator observes the antecedents complete and a controlled operation " +
            "completes the gate, running the continuation inline on the logical thread with a defensive copy of the " +
            "antecedents, or with the first antecedent, in argument order, among those observed complete. The overloads " +
            "without a scheduler use the factory's or `TaskScheduler.Default`; `LongRunning`, `AttachedToParent`, and " +
            "schedulers other than `TaskScheduler.Default` and the simulation's own are rejected, as for `ContinueWith`.",
        BuiltInRuleFamily.TaskFactoryFromAsync =>
            "Every `TaskFactory.FromAsync` and `TaskFactory<T>.FromAsync` overload is rejected at the call site: the task " +
            "completes when an `IAsyncResult` does, on a thread of its implementation's choosing, outside Clockwork's " +
            "control. Await a task-returning API instead.",
        BuiltInRuleFamily.Thread =>
            "`Thread` construction (`ThreadStart`/`ParameterizedThreadStart`, with and without a stack size), " +
            "`Start`, `UnsafeStart` (which does not flow the `ExecutionContext`), `Join` (all overloads), `Sleep`, `Yield`, " +
            "and `SpinWait` redirect to a controlled thread " +
            "that maps each thread to a controlled operation on the simulation coordinator; `Join`/`Sleep` " +
            "yield the logical thread via the deterministic loop rather than blocking a physical thread or " +
            "consuming real time. OS-specific priority, apartment-state, and `Interrupt` operations cannot be " +
            "modelled faithfully and are rejected with a precise diagnostic.",
        BuiltInRuleFamily.ThreadPool =>
            "`ThreadPool.QueueUserWorkItem` (the `WaitCallback`, `WaitCallback`+state, and generic " +
            "`Action<TState>`+state+preferLocal forms) and `UnsafeQueueUserWorkItem` (the `WaitCallback`+state, " +
            "`IThreadPoolWorkItem`, and generic forms) queue the callback as a controlled operation on the " +
            "simulation coordinator; the safe variants flow `ExecutionContext` while the unsafe variants do " +
            "not, matching the BCL. The registered-wait APIs (`RegisterWaitForSingleObject`/" +
            "`UnsafeRegisterWaitForSingleObject`, across the `uint`/`int`/`long`/`TimeSpan` timeout overloads) " +
            "run as passive, event-driven controlled waits on the target handle's modelled signalled state: " +
            "the callback fires with `timedOut: false` on a signal (an auto-reset handle consumes exactly one) " +
            "or `timedOut: true` on the virtual-time deadline, honouring `executeOnlyOnce`, re-arming " +
            "otherwise, and flowing `ExecutionContext` for the safe family only; the returned " +
            "`RegisteredWaitHandle` is substituted with the controlled handle so `Unregister` stops the wait " +
            "and signals its completion event. `UnsafeQueueNativeOverlapped` depends on native I/O and is " +
            "rejected with a precise diagnostic.",
        BuiltInRuleFamily.Timers =>
            "`System.Threading.Timer`, `System.Timers.Timer`, and `PeriodicTimer` are substituted with " +
            "controlled virtual-time implementations. `TimeProvider.System` and `CreateTimer` bridge to " +
            "the same scheduler; unsupported provider and designer marshaling paths reject precisely.",
        BuiltInRuleFamily.CancellationTimers =>
            "`CancellationTokenSource` timed constructors and `CancelAfter` use resettable virtual deadlines. " +
            "Manual cancellation, reset, and disposal remove stale registrations before they can fire.",
        BuiltInRuleFamily.Parallel =>
            "`Parallel.ForEachAsync`, `Parallel.ForAsync`, `Parallel.Invoke`, `Parallel.For`, and `Parallel.ForEach` run on " +
            "a deterministic pool of controlled workers on the simulation coordinator, and the overloads whose body " +
            "receives a `ParallelLoopState` are rejected. `Environment.ProcessorCount` and the range factories " +
            "`Partitioner.Create(fromInclusive, toExclusive)` use the simulation's processor count, which sizes the loops " +
            "without a limit. [Parallel loops](compatibility.md#parallel-loops) describes their behavior.",
        BuiltInRuleFamily.Channels =>
            "`ChannelReader<T>.ReadAllAsync` and the base `ChannelReader<T>.ReadAsync` and `ChannelWriter<T>.WriteAsync`, " +
            "which the BCL implements as async methods in the channels assembly whose awaits resume on the thread pool, " +
            "call a reader's or writer's override as written and otherwise run the BCL's algorithm with controlled " +
            "awaits on the coordinator. The members left as BCL calls complete through the channel's own state, which " +
            "controlled code changes on the logical thread.",
        BuiltInRuleFamily.Plinq =>
            "Every `ParallelEnumerable.AsParallel` overload, and `ParallelEnumerable.Range`, `Repeat`, and `Empty`, are " +
            "rejected at the call site with a precise diagnostic: PLINQ runs a query on worker tasks of " +
            "`TaskScheduler.Default` and blocks the calling thread on framework waits until they finish. These members are " +
            "the only public way to create a `ParallelQuery<T>`, so the operators over one stay BCL calls.",
        BuiltInRuleFamily.BlockingCollection =>
            "The `BlockingCollection<T>` members which add, take, or complete adding (`Add`, `TryAdd`, `Take`, `TryTake`, " +
            "`GetConsumingEnumerable`, `CompleteAdding`, and the static `AddToAny`, `TryAddToAny`, `TakeFromAny`, and " +
            "`TryTakeFromAny`) redirect to controlled shims over the real collection. Each makes the BCL's own " +
            "zero-timeout attempt, which validates, adds, or takes as the BCL does, and when that would have waited on the " +
            "framework's semaphores, parks the caller on the coordinator until the collection can satisfy it, adding is " +
            "completed (a waiting adder then throws and a waiting taker returns `false`, as in the BCL), the token is " +
            "cancelled, or the timeout elapses in virtual time. A wait which nothing can satisfy is reported as a deadlock. " +
            "Adds and `CompleteAdding` publish a race-detection edge which takes acquire.",
        BuiltInRuleFamily.AsyncIO =>
            "The asynchronous members of `Stream`, `TextReader`, and `TextWriter` whose base implementations run the " +
            "synchronous member on the thread pool (`ReadAsync`, `WriteAsync`, `FlushAsync`, `ReadLineAsync`, " +
            "`ReadToEndAsync`, `ReadBlockAsync`, `WriteLineAsync`), block the caller on a semaphore or task (`Stream." +
            "BeginRead`/`EndRead`/`BeginWrite`/`EndWrite`), or await the other members through BCL awaits " +
            "(`Stream.CopyToAsync`) call an override as written and otherwise run the BCL's base algorithm on the " +
            "coordinator: each synchronous read, write, or flush runs as a fresh controlled operation concurrent with " +
            "the caller, a stream's base reads and writes are serialized in FIFO order as the BCL's semaphore serializes " +
            "them, and every await is controlled. An override's base call runs the base algorithm. The non-virtual " +
            "members (`Stream.ReadExactlyAsync`, `ReadAtLeastAsync`, and the overloads which forward to a virtual member) " +
            "run the BCL's algorithm over the controlled virtual members. `DisposeAsync` and the synchronous members run " +
            "on the calling thread and stay BCL calls.",
        BuiltInRuleFamily.BackgroundWork =>
            "`BackgroundWorker.RunWorkerAsync` and `BackgroundService.StartAsync` are rejected at the call site with a " +
            "precise diagnostic: they start the application's `DoWork` handler or `ExecuteAsync` on the thread pool " +
            "(`Task.Factory.StartNew` on `TaskScheduler.Default`, and the real `Task.Run`), and each type raises or stores " +
            "that work through private state, so no shim can run it on the coordinator with the BCL's semantics. A call " +
            "through `IHostedService`, as a host makes, names the interface rather than `BackgroundService.StartAsync`, so it " +
            "stays a BCL call.",
        BuiltInRuleFamily.Dataflow =>
            "Every TPL Dataflow block constructor, and the `DataflowBlock` helpers which schedule work or wait (`Choose`, " +
            "`Receive`, `ReceiveAsync`, `ReceiveAllAsync`, `SendAsync`, `OutputAvailableAsync`), are rejected at the call " +
            "site with a precise diagnostic. A block runs its delegates on its options' `TaskScheduler`, but wires " +
            "cancellation and completion propagation through continuations on `TaskScheduler.Default` whatever that " +
            "scheduler is, and the helpers time out on real timers, so a scheduler substitution would not keep its work " +
            "on the coordinator. Use `System.Threading.Channels` with `Task.Run` or `Parallel.ForEachAsync` instead.",
        BuiltInRuleFamily.WebSockets =>
            "`WebSocket.CreateFromStream` and `WebSocket.CreateClientWebSocket` are rejected at the call site with a " +
            "precise diagnostic: the framework's WebSocket protocol over a caller's stream sends keep-alive pings from a " +
            "real timer on the thread pool and times out its closing handshake in real time. The `WebSocketStream.Create` " +
            "overloads which may make the stream own its socket are controlled: a stream which owns its socket bounds the " +
            "close on disposal with a real timer, so they throw `SimulationApiException` for a finite, non-zero close " +
            "timeout (an owning stream's default is 16 seconds), and otherwise return the BCL's stream.",
        BuiltInRuleFamily.HttpClient =>
            "Every `HttpClient` member which sends a request, `HttpMessageInvoker.Send` and `SendAsync`, and every " +
            "`HttpClientJsonExtensions` member redirect to shims which throw `SimulationApiException` when the client's " +
            "`Timeout` is finite, as it is by default (100 seconds), and otherwise call the BCL member: the BCL bounds each " +
            "request, and the JSON extensions the reading of its response, with the timeout on a real timer. Set " +
            "`Timeout` to `Timeout.InfiniteTimeSpan` and bound a request with a token which a virtual delay cancels. The " +
            "virtual `Send` and `SendAsync` with a request and a token check the timeout only where they run " +
            "`HttpClient`'s implementation, whether called through `HttpClient` or `HttpMessageInvoker`: an override is " +
            "called as written, and its base call runs the base implementation, checked.",
        BuiltInRuleFamily.Hosting =>
            "Every `HostingAbstractionsHostExtensions` and `HostingAbstractionsHostBuilderExtensions` member is controlled. " +
            "The BCL's members await the host's work in " +
            "framework code, whose continuations leave the logical thread where the simulation's synchronization context " +
            "is current; block the caller on it (`Start`, `Run`, `WaitForShutdown`, and the builder's `Start`); or bound a stop with a real timer " +
            "(`StopAsync(IHost, TimeSpan)`). The shims run the BCL's steps in the same order, awaiting through the " +
            "coordinator, waiting with the controlled wait, which pumps the coordinator so the host's work runs while the " +
            "caller waits, and stopping with a token which a virtual timer cancels once the timeout elapses in simulated time.",
        BuiltInRuleFamily.Monitor =>
            "The complete .NET 11 `Monitor` surface is classified: synchronization and C# `lock (object)` " +
            "lowering are controlled with deterministic virtual-time deadlines, while the process-wide " +
            "`LockContentionCount` metric is rejected because it has no per-simulation meaning.",
        BuiltInRuleFamily.Lock =>
            "The .NET 9+ `System.Threading.Lock` type and nested `Scope` are substituted onto controlled " +
            "equivalents, covering the dedicated C# lock lowering in Debug and Release builds.",
        BuiltInRuleFamily.Semaphore =>
            "The .NET 11 `SemaphoreSlim` constructors, counts, waits, releases, and disposal are controlled; " +
            "`AvailableWaitHandle` returns a controlled manual-reset bridge whose signal tracks whether the " +
            "permit count is positive and which composes with the controlled wait-handle surface.",
        BuiltInRuleFamily.UncontrolledInvocation =>
            "Process control and abrupt-termination APIs (`Process.Start`/`Run`/`RunAsync`/`RunAndCaptureText`/" +
            "`RunAndCaptureTextAsync`/`StartAndForget`, the `Start` instance/`Kill`/`Signal` members, the " +
            "`WaitForExit`/`WaitForExitAsync`/`WaitForExitStatus`/`TryWaitForExitStatus`/`WaitForExitStatusAsync` " +
            "waits, the `ReadAllBytes`/`ReadAllText`/`ReadAllLines` reads (and their async forms) that wait for the " +
            "process to close its output, the `WaitForInputIdle` waits, the `Responding` getter, `CloseMainWindow`, the `EnableRaisingEvents` " +
            "setter and `BeginOutputReadLine`/`BeginErrorReadLine` (which raise events on threads of the BCL's choosing as " +
            "the process exits or writes), the `Process` priority, processor-affinity, and working-set setters and the " +
            "`ProcessThread` scheduling setters, the `SafeProcessHandle` `Start`/`Signal`/`Resume`/`Kill` and wait members, the " +
            "`WindowsProcessStartArguments`/`UnixProcessStartArguments` `Start` launchers, and `Environment.Exit`/" +
            "`FailFast`) cannot be modelled inside a single " +
            "simulated process at all. A throwing guard is injected before each call site so a rewritten assembly can " +
            "never launch, signal, wait on, reschedule, or terminate a real OS process; unlike the controlled shims the " +
            "rejection is unconditional (it fires whether or not a simulation is active).",
        BuiltInRuleFamily.Interlocked =>
            "The full .NET 11 `Interlocked` surface - `Increment`/`Decrement`/`Add` (`int`/`long`/`uint`/`ulong`), " +
            "`And`/`Or` (`int`/`long`/`uint`/`ulong` and the generic integer or enum overload), `Exchange`/" +
            "`CompareExchange` (every primitive, native-int, floating-point, reference, and generic overload), " +
            "`Read` (`long`/`ulong`), and the memory barriers - " +
            "redirects each call site to a shim with the identical `ref`-first signature. Clockwork's " +
            "cooperative single-logical-thread scheduler makes every read-modify-write an indivisible step " +
            "(never split, never interleaved mid-operation), so the shim delegates to the real primitive and " +
            "preserves exact atomic return, overflow, and reference-write semantics under the active " +
            "simulation. The exploration policy injects no mid-operation scheduling point; the single " +
            "delegation site is the race-exploration access-tracking attachment point.",
        BuiltInRuleFamily.Volatile =>
            "The full .NET 11 `Volatile` surface - `Read`/`Write` (every primitive, native-int, " +
            "floating-point, and generic reference overload) and the `ReadBarrier`/`WriteBarrier` fences - " +
            "redirects each call site to a shim with the identical `ref`-first signature. Under the " +
            "cooperative single-logical-thread scheduler a volatile access is an indivisible step, so the " +
            "shim delegates to the real primitive and preserves the exact value read/written together with " +
            "the acquire (read) / release (write) fence intent. The single delegation site is the " +
            "race-exploration access-tracking attachment point.",
        BuiltInRuleFamily.SpinWait =>
            "`System.Threading.SpinWait` is a value type retargeted by whole-type substitution (like " +
            "`System.Threading.Lock`): every local/field/parameter typed `SpinWait`, each `new SpinWait()`/" +
            "`default`, the instance members (`Count`, `NextSpinWillYield`, `Reset`, both `SpinOnce` " +
            "overloads) and the static `SpinUntil` overloads remap onto the controlled struct. Inside a " +
            "simulation a spin never burns CPU or consumes real time: `SpinOnce` is a cooperative no-op that " +
            "only advances the observable spin count, and `SpinUntil` pumps the deterministic loop until its " +
            "predicate holds (a never-satisfiable predicate surfaces as the loop-model deadlock diagnostic). " +
            "The finite `SpinUntil` overloads use a first-winner virtual-time deadline.",
        BuiltInRuleFamily.WaitHandle =>
            "The controlled event / wait-handle surface - `AutoResetEvent`, `ManualResetEvent`, " +
            "`EventWaitHandle`, and the shared `WaitHandle` operations. Each concrete event is a sealed BCL " +
            "class, so the real object is retained as an identity handle while its signaled state and a " +
            "deterministic FIFO waiter set live in a side table; every `new` redirects to a `Create` factory " +
            "and each instance member is a receiver-first shim. `WaitOne` (all five overloads) pumps the " +
            "deterministic loop until the event is signaled - a never-satisfiable wait surfaces as the " +
            "loop-model deadlock diagnostic rather than hanging - and `Set`/`Reset` model exact reset-mode " +
            "semantics: an auto-reset `Set` wakes and consumes exactly one eligible waiter (or leaves the " +
            "event signaled until the next `WaitOne` consumes it), while a manual-reset `Set` releases every " +
            "waiter and stays signaled until `Reset`. The static multi-handle operations `WaitAny` (returns " +
            "the lowest-index signaled handle) and `WaitAll` (waits until every handle is simultaneously " +
            "signaled, then consumes them atomically so an auto-reset handle is never partially consumed) " +
            "register across all handles with no lost signals, validating null/empty/over-64 arrays and - " +
            "for `WaitAll` - duplicate handles; `SignalAndWait` atomically signals the first handle then " +
            "waits on the second. Finite timeouts use a first-winner virtual-time deadline (zero polls, " +
            "infinite never times out); `Dispose`/`Close` mark the modelled state disposed. Named / " +
            "cross-process APIs (named constructors, `OpenExisting`, `TryOpenExisting`) and the raw " +
            "native-handle accessors (`Handle`, `SafeWaitHandle`) cannot be modelled in a single simulated " +
            "process and are rejected with a precise diagnostic.",
        BuiltInRuleFamily.ReaderWriterLockSlim =>
            "Every public .NET 11 `ReaderWriterLockSlim` constructor, property, enter/try-enter/exit overload, " +
            "and `Dispose` member redirects to receiver-first controlled shims. The real BCL instance is only " +
            "an identity key; logical-strand ownership, recursion, wait queues, and deadlines are modelled " +
            "without blocking a physical thread.",
        BuiltInRuleFamily.ManualResetEventSlim =>
            "Every public .NET 11 `ManualResetEventSlim` constructor, property, set/reset/wait overload, and " +
            "`Dispose` redirects to receiver-first controlled shims. Signal state, waiters, cancellation, " +
            "deadlines, and the exposed wait-handle bridge are modelled in side state.",
        BuiltInRuleFamily.Mutex =>
            "Unnamed `Mutex` construction and `ReleaseMutex` are controlled through the wait-handle kernel. " +
            "Named constructors (including null-name forms that the shim conditionally treats as unnamed) and " +
            "`OpenExisting`/`TryOpenExisting` are classified Rejected because a non-null name is cross-process " +
            "kernel state. Ownership and recursion are logical-strand state; owner exit without `ReleaseMutex` " +
            "leaves the mutex owned so a later indefinite wait reports the controlled deadlock diagnostic " +
            "rather than simulating `AbandonedMutexException`.",
        BuiltInRuleFamily.KernelSemaphore =>
            "The unnamed kernel `Semaphore` constructor and both `Release` overloads are controlled through " +
            "the wait-handle kernel. Named constructors and `OpenExisting`/`TryOpenExisting` are Rejected " +
            "because cross-process semaphore state cannot be represented by one simulation.",
        BuiltInRuleFamily.SpinLock =>
            "`SpinLock` is wholly substituted with `ControlledSpinLock`, preserving its value-type surface " +
            "while replacing CPU spinning with deterministic scheduler pumping.",
        BuiltInRuleFamily.ExecutionContext =>
            "`ExecutionContext` capture, run, flow-control, copy, and disposal members redirect to controlled " +
            "shims. The legacy `GetObjectData` serialization surface is Rejected before it can invoke BCL " +
            "serialization behavior.",
        BuiltInRuleFamily.SynchronizationContext =>
            "`SynchronizationContext` ambient-context and callback-dispatch members redirect to controlled " +
            "shims. `Post` queues through the coordinator and `Send` runs on the current logical strand; custom " +
            "context dispatch is not invoked. Its raw native-handle `Wait` member is Rejected before it can " +
            "block a physical thread.",
        BuiltInRuleFamily.Barrier =>
            "`Barrier` is wholly substituted with `ControlledBarrier`, including generic occurrences such as " +
            "`Action<Barrier>`, so participant state and post-phase callbacks remain under simulation.",
        BuiltInRuleFamily.CountdownEvent =>
            "`CountdownEvent` is wholly substituted with `ControlledCountdownEvent`, so all count updates, " +
            "waits, bridge handles, and disposal run under the deterministic scheduler.",
        _ => string.Empty,
    };

    private static string ParamSuffix(ImmutableArray<string> parameters) =>
        parameters.IsDefault ? "(*)" : "(" + string.Join(",", parameters) + ")";

    // ToCanonicalString is the signature-hashing encoding; the inventory shows the readable member form.
    private static string Display(MemberSignature signature) =>
        signature.MemberName is null
            ? signature.DeclaringTypeFullName
            : signature.DeclaringTypeFullName + "::" + signature.MemberName + ParamSuffix(signature.ParameterTypeFullNames);

    private static string Display(RewriteReplacement replacement) =>
        replacement.AssemblyName + "!" + (replacement.MemberName is null
            ? replacement.DeclaringTypeFullName
            : replacement.DeclaringTypeFullName + "::" + replacement.MemberName + ParamSuffix(replacement.ParameterTypeFullNames));
}
