using System.Collections.Frozen;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using static Orleans.Durable.Storage.Architecture.Tests.Namespaces;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>A Clockwork-incompatible construct found in an assembly: the outermost type and the offending construct.</summary>
internal readonly record struct ClockworkIncompatibility(string Type, string Construct);

/// <summary>
/// Pins verification.md §7.1: every assembly in a Clockwork simulation's rewrite closure follows Clockwork's
/// compatibility rules — it awaits a <see cref="ValueTask"/> rather than blocking on it, runs on the default
/// <see cref="TaskScheduler"/>, synchronizes within its process through unnamed primitives, creates no task with
/// <c>TaskFactory.FromAsync</c>, calls no other API Clockwork rejects at the call site, and compiles no method
/// with runtime async — and no Clog build file enables the <c>runtime-async</c> compiler feature.
/// </summary>
public sealed class ClockworkCompatibilityTests
{
    private const string FixtureNamespace = "Orleans.Durable.Storage.Architecture.Tests.SimulationFixtures.ClockworkCompatibility";

    private const string AsyncStateMachineInterface = "System.Runtime.CompilerServices.IAsyncStateMachine";

    private const string TaskSchedulerType = "System.Threading.Tasks.TaskScheduler";

    /// <summary><see cref="MethodImplAttributes"/> flag emitted for a <c>runtime-async</c> method (ECMA-335 0x2000).</summary>
    private const MethodImplAttributes RuntimeAsyncFlag = (MethodImplAttributes)0x2000;

    /// <summary>
    /// ValueTask blocking members forbidden everywhere, including inside an <c>IAsyncStateMachine</c>: both the BCL
    /// getter (for unrewritten assemblies and fixtures) and Clockwork's rewritten shim.
    /// </summary>
    private static readonly FrozenSet<ReferencedMember> BlockingValueTaskMembers = new ReferencedMember[]
    {
        new("System.Threading.Tasks.ValueTask`1", "get_Result"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledValueTask", "Result"),
    }.ToFrozenSet();

    /// <summary>
    /// ValueTask awaiter <c>GetResult</c> members allowed only inside a compiler-generated <c>IAsyncStateMachine</c>,
    /// where <c>await</c> checks <c>IsCompleted</c> and suspends asynchronously before calling <c>GetResult</c>.
    /// </summary>
    private static readonly FrozenSet<ReferencedMember> ValueTaskAwaiterGetResultMembers = new ReferencedMember[]
    {
        new("System.Runtime.CompilerServices.ValueTaskAwaiter", "GetResult"),
        new("System.Runtime.CompilerServices.ValueTaskAwaiter`1", "GetResult"),
        new("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter", "GetResult"),
        new("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter", "GetResult"),
        new("Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter", "GetResult"),
        new("Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1", "GetResult"),
        new("Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredValueTaskAwaiter", "GetResult"),
        new("Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredValueTaskAwaiter`1", "GetResult"),
    }.ToFrozenSet();

    /// <summary>Cross-process wait-handle types whose constructors taking a name or <c>NamedWaitHandleOptions</c> are forbidden.</summary>
    private static readonly FrozenSet<string> NamedWaitHandleTypes = new[]
    {
        "System.Threading.Mutex",
        "System.Threading.Semaphore",
        "System.Threading.EventWaitHandle",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Named wait-handle, <c>TaskFactory.FromAsync</c>, <c>runtime-async</c>, and other Clockwork call-site
    /// rejected members, in both their BCL and Clockwork-rewritten shim forms.
    /// </summary>
    private static readonly FrozenSet<ReferencedMember> RejectedMembers = new ReferencedMember[]
    {
        // TaskFactory.FromAsync (BCL + Clockwork call-site rejection).
        new("System.Threading.Tasks.TaskFactory", "FromAsync"),
        new("System.Threading.Tasks.TaskFactory`1", "FromAsync"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledTaskFactory", "RejectFromAsync"),

        // Named cross-process synchronization primitives (BCL OpenExisting/TryOpenExisting + Clockwork shims).
        new("System.Threading.Mutex", "OpenExisting"),
        new("System.Threading.Mutex", "TryOpenExisting"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledMutex", "CreateNamed"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledMutex", "OpenExisting"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledMutex", "TryOpenExisting"),
        new("System.Threading.Semaphore", "OpenExisting"),
        new("System.Threading.Semaphore", "TryOpenExisting"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledSemaphore", "CreateNamed"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledSemaphore", "OpenExisting"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledSemaphore", "TryOpenExisting"),
        new("System.Threading.EventWaitHandle", "OpenExisting"),
        new("System.Threading.EventWaitHandle", "TryOpenExisting"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledEventWaitHandle", "CreateNamedEvent"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledEventWaitHandle", "OpenExisting"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledEventWaitHandle", "TryOpenExisting"),

        // Runtime-async AsyncHelpers.Await intrinsics (BCL + Clockwork shim).
        new("System.Runtime.CompilerServices.AsyncHelpers", "Await"),
        new("Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledAsyncHelpers", "Await"),

        // Other APIs Clockwork rejects at the call site with SimulationApiException.
        new("Orleans.Testing.Simulation.Runtime.UncontrolledInvocationGuard", "Reject"),
        new("System.Threading.WaitHandle", "get_Handle"),
        new("System.Threading.WaitHandle", "set_Handle"),
        new("System.Threading.WaitHandle", "get_SafeWaitHandle"),
        new("System.Threading.WaitHandle", "set_SafeWaitHandle"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledWaitHandle", "GetHandle"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledWaitHandle", "SetHandle"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledWaitHandle", "GetSafeWaitHandle"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledWaitHandle", "SetSafeWaitHandle"),
        new("System.Threading.SynchronizationContext", "Wait"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledSynchronizationContext", "Wait"),
        new("System.Threading.ExecutionContext", "GetObjectData"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledExecutionContext", "GetObjectData"),
        new("System.Threading.Thread", "set_Priority"),
        new("System.Threading.Thread", "Interrupt"),
        new("System.Threading.Thread", "SetApartmentState"),
        new("System.Threading.Thread", "set_ApartmentState"),
        new("System.Threading.Thread", "TrySetApartmentState"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread", "SetPriority"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread", "Interrupt"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread", "SetApartmentState"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread", "SetApartmentStateProperty"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread", "TrySetApartmentState"),
        new("System.Threading.Monitor", "get_LockContentionCount"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledMonitor", "LockContentionCount"),
        new("System.Threading.ThreadPool", "UnsafeQueueNativeOverlapped"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.ControlledThreadPool", "RejectUnsupported"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledParallel", "RejectUnsupported"),
        new("Orleans.Testing.Simulation.Shims.System.Linq.ControlledParallelEnumerable", "RejectUnsupported"),
        new("Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledBackgroundWork", "RejectUnsupported"),
    }.ToFrozenSet();

    /// <summary>
    /// Third-party package types in the rewritten closure whose incompatibilities are reviewed as unreachable under
    /// Clog's simulations, keyed by (assembly, outermost type, offending construct). Clog assemblies carry no entries
    /// here.
    /// </summary>
    private static readonly FrozenDictionary<(string Assembly, string Type, string Construct), string> ReviewedPackageIncompatibilities =
        new Dictionary<(string, string, string), string>
        {
            [("Apache.Arrow", "Apache.Arrow.C.CArrowArrayStreamExporter", "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledValueTask.Result")] =
                "C Data Interface stream export, never invoked by Clog's simulations",
            [("Azure.Core", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter.GetResult")] =
                "Azure SDK EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Core", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1.GetResult")] =
                "Azure SDK EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Core", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredValueTaskAwaiter`1.GetResult")] =
                "Azure SDK WithCancellationValueTaskAwaiter forwarding wrapper",
            [("Azure.Data.Tables", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter.GetResult")] =
                "Azure SDK shared-source EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Data.Tables", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1.GetResult")] =
                "Azure SDK shared-source EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Data.Tables", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredValueTaskAwaiter`1.GetResult")] =
                "Azure SDK shared-source WithCancellationValueTaskAwaiter forwarding wrapper",
            [("Azure.Storage.Blobs", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter.GetResult")] =
                "Azure SDK shared-source EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Storage.Blobs", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1.GetResult")] =
                "Azure SDK shared-source EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Storage.Blobs", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredValueTaskAwaiter`1.GetResult")] =
                "Azure SDK shared-source WithCancellationValueTaskAwaiter forwarding wrapper",
            [("Azure.Storage.Common", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter.GetResult")] =
                "Azure SDK shared-source EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Storage.Common", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1.GetResult")] =
                "Azure SDK shared-source EnsureCompleted synchronous wrapper; Clog calls only async Azure SDK paths",
            [("Azure.Storage.Common", "Azure.Core.Pipeline.TaskExtensions", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledConfiguredValueTaskAwaiter`1.GetResult")] =
                "Azure SDK shared-source WithCancellationValueTaskAwaiter forwarding wrapper",
            [("Azure.Core", "Azure.Identity.MsalManagedIdentityClient", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1.GetResult")] =
                "Azure Managed Identity credential, never constructed in Clog's simulations",
            [("Azure.Core", "Azure.Identity.ProcessService", "Orleans.Testing.Simulation.Runtime.UncontrolledInvocationGuard.Reject")] =
                "Azure CLI credential process wrapper, never constructed in Clog's simulations",
            [("Microsoft.Identity.Client", "Microsoft.Identity.Client.ManagedIdentity.V2.InterprocessLock", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledMutex.CreateNamed")] =
                "MSAL cross-process managed identity cache lock, never invoked in Clog's simulations",
            [("Microsoft.Identity.Client", "Microsoft.Identity.Client.Platforms.Features.DesktopOs.StaTaskScheduler", "base System.Threading.Tasks.TaskScheduler")] =
                "MSAL interactive desktop STA scheduler, never invoked in Clog's simulations",
            [("Microsoft.Identity.Client", "Microsoft.Identity.Client.Platforms.Features.DesktopOs.StaTaskScheduler", "System.Threading.Tasks.TaskScheduler..ctor")] =
                "MSAL interactive desktop STA scheduler, never invoked in Clog's simulations",
            [("Microsoft.Identity.Client", "Microsoft.Identity.Client.Platforms.Features.DesktopOs.StaTaskScheduler", "System.Threading.Tasks.TaskScheduler.TryExecuteTask")] =
                "MSAL interactive desktop STA scheduler, never invoked in Clog's simulations",
            [("Microsoft.Identity.Client", "Microsoft.Identity.Client.Platforms.Features.DesktopOs.StaTaskScheduler", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledThread.SetApartmentState")] =
                "MSAL interactive desktop STA thread, never invoked in Clog's simulations",
            [("Microsoft.Identity.Client", "Microsoft.Identity.Client.Platforms.netstandard.NetCorePlatformProxy", "Orleans.Testing.Simulation.Runtime.UncontrolledInvocationGuard.Reject")] =
                "MSAL OS browser launch for interactive login, never invoked in Clog's simulations",
            [("Microsoft.Identity.Client", "Microsoft.Identity.Client.SystemWebViewOptions", "Orleans.Testing.Simulation.Runtime.UncontrolledInvocationGuard.Reject")] =
                "MSAL OS browser launch for interactive login, never invoked in Clog's simulations",
            [("Polly.Core", "Polly.CircuitBreaker.CircuitStateController`1", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1.GetResult")] =
                "Polly circuit breaker strategy, not used by Clog",
            [("Polly.Core", "Polly.Utils.Pipeline.BridgeComponent`1", "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledValueTask.Result")] =
                "Polly synchronous Execute bridge; Clog invokes ExecuteAsync only",
            [("Polly.Core", "Polly.Utils.TaskHelper", "Orleans.Testing.Simulation.Shims.System.Threading.Tasks.ControlledValueTask.Result")] =
                "Polly synchronous execution helper; Clog invokes ExecuteAsync only",
            [("Polly.Core", "Polly.Utils.TaskHelper", "Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices.ControlledValueTaskAwaiter`1.GetResult")] =
                "Polly synchronous execution helper; Clog invokes ExecuteAsync only",
        }.ToFrozenDictionary();

    [Fact]
    public void RewrittenAssembliesFollowClockworkCompatibilityRules()
    {
        Assert.All(
            ReviewedPackageIncompatibilities.Keys,
            static entry => Assert.False(
                entry.Assembly.StartsWith("Orleans.Durable.Storage.", StringComparison.Ordinal),
                $"Clog assembly {entry.Assembly} may not appear in ReviewedPackageIncompatibilities."));

        var references = SimulationClosureTests.ProjectReferences(
            Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Architecture.Tests", "Orleans.Durable.Storage.Architecture.Tests.csproj"));
        var rewritten = new SortedSet<string>(StringComparer.Ordinal);
        var matchedReviews = new HashSet<(string Assembly, string Type, string Construct)>();
        var violations = new List<string>();

        foreach (var project in SimulationClosureTests.SimulationProjects())
        {
            Assert.True(references.Contains(Path.GetFullPath(project)), $"Orleans.Durable.Storage.Architecture.Tests does not reference {project}.");
            var (manifest, directory) = SimulationClosureTests.ClockworkOutput(project);
            foreach (var assembly in SimulationClosureTests.RewrittenAssemblies(manifest))
            {
                var name = Path.GetFileNameWithoutExtension(assembly);
                rewritten.Add(name);
                var assemblyPath = Path.Combine(directory, assembly);
                foreach (var finding in FindIncompatibilities(assemblyPath, static _ => true))
                {
                    var key = (name, finding.Type, finding.Construct);
                    if (ReviewedPackageIncompatibilities.ContainsKey(key))
                    {
                        matchedReviews.Add(key);
                        continue;
                    }

                    violations.Add($"{Path.GetFileNameWithoutExtension(project)}: {name}: {finding.Type} -> {finding.Construct}");
                }
            }
        }

        // The closure holds Clog's engine, the simulation kit, and third-party packages, so an empty result is not vacuous.
        Assert.Contains("Orleans.Durable.Storage.Engine", rewritten);
        Assert.Contains("Orleans.Durable.Storage.Simulation", rewritten);
        Assert.Contains(rewritten, static name => !name.StartsWith("Orleans.Durable.Storage.", StringComparison.Ordinal));
        Assert.True(violations.Count == 0, "Unreviewed Clockwork incompatibilities:" + Environment.NewLine + string.Join(Environment.NewLine, violations));

        var stale = ReviewedPackageIncompatibilities.Keys
            .Where(entry => !matchedReviews.Contains(entry))
            .Select(static entry => $"{entry.Assembly}: {entry.Type} -> {entry.Construct}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(stale.Length == 0, "Stale ReviewedPackageIncompatibilities entries:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void NoBuildFileEnablesRuntimeAsync()
    {
        var root = Repository.FindRoot();
        var files = SimulationClosureTests.BuildFiles(root).ToArray();
        Assert.NotEmpty(files);
        var violations = files
            .Where(static file => EnablesRuntimeAsync(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(root, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void TheRuntimeAsyncCheckFlagsAProjectWhichEnablesIt()
    {
        Assert.True(EnablesRuntimeAsync("<Project><PropertyGroup><Features>strict;runtime-async=on</Features></PropertyGroup></Project>"));
        Assert.True(EnablesRuntimeAsync("<Project><PropertyGroup><Features>RUNTIME-ASYNC</Features></PropertyGroup></Project>"));
        Assert.False(EnablesRuntimeAsync("<Project><PropertyGroup><Features>strict</Features></PropertyGroup></Project>"));
    }

    /// <summary>
    /// The check reports each planted Clockwork-incompatible construct in the fixtures assembly and nothing from
    /// <c>FixtureCompatible</c>.
    /// </summary>
    [Fact]
    public void TheCompatibilityCheckFindsEachPlantedViolationAndNothingElse()
    {
        ClockworkIncompatibility[] expected =
        [
            new(FixtureNamespace + ".FixtureCustomTaskScheduler", "System.Threading.Tasks.TaskScheduler..ctor"),
            new(FixtureNamespace + ".FixtureCustomTaskScheduler", "System.Threading.Tasks.TaskScheduler.TryExecuteTask"),
            new(FixtureNamespace + ".FixtureCustomTaskScheduler", "base System.Threading.Tasks.TaskScheduler"),
            new(FixtureNamespace + ".FixtureFromAsync", "System.Threading.Tasks.TaskFactory.FromAsync"),
            new(FixtureNamespace + ".FixtureFromAsync", "System.Threading.Tasks.TaskFactory`1.FromAsync"),
            new(FixtureNamespace + ".FixtureNamedPrimitives", "System.Threading.EventWaitHandle..ctor"),
            new(FixtureNamespace + ".FixtureNamedPrimitives", "System.Threading.EventWaitHandle.OpenExisting"),
            new(FixtureNamespace + ".FixtureNamedPrimitives", "System.Threading.Mutex..ctor"),
            new(FixtureNamespace + ".FixtureNamedPrimitives", "System.Threading.Mutex.OpenExisting"),
            new(FixtureNamespace + ".FixtureNamedPrimitives", "System.Threading.Semaphore..ctor"),
            new(FixtureNamespace + ".FixtureNamedPrimitives", "System.Threading.Semaphore.TryOpenExisting"),
            new(FixtureNamespace + ".FixtureNonDefaultScheduler", "System.Threading.Tasks.TaskFactory..ctor"),
            new(FixtureNamespace + ".FixtureNonDefaultScheduler", "System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext"),
            new(FixtureNamespace + ".FixtureNonDefaultScheduler", "System.Threading.Tasks.TaskScheduler.get_Current"),
            new(FixtureNamespace + ".FixtureRejectedMembers", "System.Threading.Monitor.get_LockContentionCount"),
            new(FixtureNamespace + ".FixtureRejectedMembers", "System.Threading.Thread.Interrupt"),
            new(FixtureNamespace + ".FixtureRejectedMembers", "System.Threading.WaitHandle.get_SafeWaitHandle"),
            new(FixtureNamespace + ".FixtureSchedulerPair", "System.Threading.Tasks.ConcurrentExclusiveSchedulerPair"),
            new(FixtureNamespace + ".FixtureValueTaskGetResult", "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter.GetResult"),
            new(FixtureNamespace + ".FixtureValueTaskGetResult", "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter.GetResult"),
            new(FixtureNamespace + ".FixtureValueTaskGetResult", "System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult"),
            new(FixtureNamespace + ".FixtureValueTaskGetResult", "System.Runtime.CompilerServices.ValueTaskAwaiter`1.GetResult"),
            new(FixtureNamespace + ".FixtureValueTaskResult", "System.Threading.Tasks.ValueTask`1.get_Result"),
        ];

        Assert.Equal(
            expected,
            FindIncompatibilities(typeof(ClockworkCompatibilityTests).Assembly.Location, static ns => IsWithin(ns, FixtureNamespace)));
    }

    private static bool EnablesRuntimeAsync(string xml) =>
        XDocument.Parse(xml)
            .Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "Features", StringComparison.OrdinalIgnoreCase))
            .Any(static element => element.Value.Contains("runtime-async", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every Clockwork-incompatible construct referenced or declared by the types of <paramref name="assemblyPath"/>
    /// selected by <paramref name="isScanned"/>, grouped by outermost type.
    /// </summary>
    private static List<ClockworkIncompatibility> FindIncompatibilities(string assemblyPath, Func<string, bool> isScanned)
    {
        var findings = new HashSet<ClockworkIncompatibility>();
        foreach (var type in MetadataReferences.Scan(assemblyPath, isScanned))
        {
            var outer = type.FullName.Split('+')[0];
            var isAsyncStateMachine = type.Types.Any(
                static reference => reference.Site == ReferenceSite.Interface && reference.FullName == AsyncStateMachineInterface);

            foreach (var reference in type.Types)
            {
                if (reference.Site == ReferenceSite.BaseType && reference.FullName == TaskSchedulerType)
                {
                    findings.Add(new(outer, "base " + TaskSchedulerType));
                }
                else if (reference.FullName == "System.Threading.Tasks.ConcurrentExclusiveSchedulerPair")
                {
                    findings.Add(new(outer, reference.FullName));
                }
            }

            foreach (var member in type.Members)
            {
                if (BlockingValueTaskMembers.Contains(member)
                    || !isAsyncStateMachine && ValueTaskAwaiterGetResultMembers.Contains(member)
                    || RejectedMembers.Contains(member)
                    || member.DeclaringType == TaskSchedulerType && member.Name != "get_Default")
                {
                    findings.Add(new(outer, member.ToString()));
                }
            }

            foreach (var use in type.MemberUses)
            {
                if (use.Name != ".ctor")
                {
                    continue;
                }

                if (NamedWaitHandleTypes.Contains(use.DeclaringType)
                    && (use.Signature.Contains("String", StringComparison.Ordinal)
                        || use.Signature.Contains("NamedWaitHandleOptions", StringComparison.Ordinal)))
                {
                    findings.Add(new(outer, $"{use.DeclaringType}.{use.Name}"));
                }
                else if (use.DeclaringType is "System.Threading.Tasks.TaskFactory" or "System.Threading.Tasks.TaskFactory`1"
                    && use.Signature.Contains(TaskSchedulerType, StringComparison.Ordinal))
                {
                    findings.Add(new(outer, $"{use.DeclaringType}.{use.Name}"));
                }
            }
        }

        foreach (var runtimeAsync in FindRuntimeAsyncMethods(assemblyPath, isScanned))
        {
            findings.Add(runtimeAsync);
        }

        return [.. findings.OrderBy(static finding => finding.Type, StringComparer.Ordinal).ThenBy(static finding => finding.Construct, StringComparer.Ordinal)];
    }

    private static IEnumerable<ClockworkIncompatibility> FindRuntimeAsyncMethods(string assemblyPath, Func<string, bool> isScanned)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var (ns, outer) = OutermostType(metadata, handle);
            if (!isScanned(ns))
            {
                continue;
            }

            var type = metadata.GetTypeDefinition(handle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if ((method.ImplAttributes & RuntimeAsyncFlag) != 0)
                {
                    yield return new(outer, $"runtime-async {metadata.GetString(method.Name)}");
                }
            }
        }
    }

    private static (string Namespace, string OutermostFullName) OutermostType(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        while (!type.GetDeclaringType().IsNil)
        {
            handle = type.GetDeclaringType();
            type = metadata.GetTypeDefinition(handle);
        }

        var ns = metadata.GetString(type.Namespace);
        var name = metadata.GetString(type.Name);
        return (ns, string.IsNullOrEmpty(ns) ? name : ns + "." + name);
    }
}
