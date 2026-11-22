using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime.Policy;
using Orleans.Testing.Simulation.Runtime.Shims;
using Mono.Cecil;
using GenericParameterAttributes = System.Reflection.GenericParameterAttributes;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// Conformance for the controlled APIs' thread affinity: while a simulation is active, every controlled rewrite target
/// requires the simulation's logical thread. A thread which carries the simulation's ambient context through code
/// Clockwork did not rewrite - the real <c>Task.Run</c> in <c>BackgroundService.StartAsync</c>, say - is rejected with a
/// <see cref="SimulationThreadAffinityException"/> naming the API, and the thread driving the simulation surfaces the
/// violation too.
/// </summary>
public sealed class ThreadAffinityConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan EscapedWorkTimeout = TimeSpan.FromSeconds(30);

    private const string HostedServiceSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Hosting;

        namespace Conf {
        public sealed class Worker : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken) => await Task.Delay(1, stoppingToken);
        }

        public static class HostedServiceProbe
        {
            public static Task Start()
            {
                var worker = new Worker();
                _ = worker.StartAsync(CancellationToken.None);
                return worker.ExecuteTask!;
            }
        } }
        """;

    private readonly RewriteFixture _fixture = new();

    /// <summary>
    /// Every target of the built-in controlled rules - each redirected method, and each public member and virtual
    /// override of a substituted type - is invoked with default arguments on a thread which carries the simulation's
    /// ambient context, and must reject it, naming the rule's API, before reading those arguments. The entry points come
    /// from the shipped rule inventories, so a new rule is covered when it is added.
    /// </summary>
    [Fact]
    public async Task EveryControlledEntryPointRejectsAThreadOutsideTheSimulation()
    {
        EntryPoint[] entryPoints = ControlledEntryPoints();
        var cluster = new SimulationCluster(seed: 1, Start);
        ExecutionContext ambient = CaptureSimulationContext(cluster);

        List<string> failures = InvokeEach(ambient, entryPoints, static (entryPoint, exception) =>
            exception is SimulationThreadAffinityException affinity && entryPoint.Names(affinity.ApiName));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        var surfaced = Assert.Throws<SimulationThreadAffinityException>(
            () => cluster.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.True(entryPoints[0].Names(surfaced.ApiName), surfaced.ApiName);
        await cluster.DisposeAsync();
    }

    /// <summary>
    /// Outside any simulation, the same entry points reject the call before reading their arguments, except the
    /// <c>TaskCompletionSource</c> constructors, which construct the BCL source, its completions and
    /// <c>Task.CreationOptions</c>, which pass through to the BCL member and fail on the null receiver, and
    /// <c>ManualResetValueTaskSourceCore</c>'s completions, which pass through to the BCL member: <c>SetResult</c> completes
    /// the default core passed by reference, and <c>SetException</c> rejects the null exception.
    /// </summary>
    [Fact]
    public void EveryControlledEntryPointRejectsACallOutsideASimulation()
    {
        EntryPoint[] entryPoints = ControlledEntryPoints();

        List<string> failures = InvokeEach(ambient: null, entryPoints, static (entryPoint, exception) =>
            entryPoint.Method.DeclaringType == typeof(ControlledTaskCompletionSource) && entryPoint.Method.Name.StartsWith("Create", StringComparison.Ordinal)
                ? exception is null
                : entryPoint.Method.DeclaringType == typeof(Orleans.Testing.Simulation.Shims.System.Threading.Tasks.Sources.ControlledManualResetValueTaskSourceCore)
                    ? entryPoint.Method.Name == nameof(System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<int>.SetException)
                        ? exception is ArgumentNullException
                        : exception is null
                : entryPoint.Method.DeclaringType == typeof(ControlledTaskCompletionSource)
                    || entryPoint.Method == typeof(ControlledTask).GetMethod(nameof(ControlledTask.CreationOptions))
                    ? exception is NullReferenceException
                    : exception is SimulationNotActiveException notActive && entryPoint.Names(notActive.ApiName));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The affinity backstop: <c>BackgroundService</c> loads from the shared framework, uninstrumented, and its
    /// <c>StartAsync</c> starts the rewritten <c>ExecuteAsync</c> through the real <c>Task.Run</c>. A direct call to it is
    /// rejected (<c>clockwork.backgroundservice.startasync</c>), so the probe is staged without that rule, as a call through
    /// <c>IHostedService</c> reaches it. The escaped thread fails at its first controlled call, and although nothing
    /// observes the faulted task, the driver surfaces the violation.
    /// </summary>
    [Fact]
    public async Task BackgroundServiceExecuteAsyncEscapingToTheThreadPoolFailsTheRun()
    {
        StagedProbe probe = _fixture.StageControlledTasksWithout(
            "Conf.ThreadAffinity.HostedService",
            "Conf.HostedServiceProbe",
            HostedServiceSource,
            rule => rule.Id == "clockwork.backgroundservice.startasync");
        var cluster = new SimulationCluster(seed: 1, Start);
        Task? executeTask = null;
        cluster.SchedulerLane.Enqueue(() => executeTask = (Task)probe.Method("Start").Invoke(null, null)!);

        // The escaped thread races this drive, so the violation surfaces from whichever drive follows its report.
        Exception? during = Record.Exception(() => cluster.RunUntilIdle(TestContext.Current.CancellationToken));
        Assert.NotNull(executeTask);
        Assert.True(SpinWait.SpinUntil(() => executeTask.IsCompleted, EscapedWorkTimeout));
        Exception? after = Record.Exception(() => cluster.RunUntilIdle(TestContext.Current.CancellationToken));

        var escaped = Assert.IsType<SimulationThreadAffinityException>(executeTask.Exception?.InnerException);
        Assert.Equal("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", escaped.ApiName);
        Assert.NotEqual(Environment.CurrentManagedThreadId, escaped.ManagedThreadId);
        var surfaced = Assert.Single(new[] { during, after }.OfType<Exception>());
        Assert.Equal(escaped.ApiName, Assert.IsType<SimulationThreadAffinityException>(surfaced).ApiName);
        await cluster.DisposeAsync();
    }

    public void Dispose() => _fixture.Dispose();

    private static ExecutionContext CaptureSimulationContext(SimulationCluster cluster)
    {
        ExecutionContext? captured = null;
        cluster.SchedulerLane.Enqueue(() => captured = ExecutionContext.Capture());
        cluster.RunUntilIdle(TestContext.Current.CancellationToken);
        return captured ?? throw new InvalidOperationException("The simulation work did not capture its context.");
    }

    // Invokes each entry point on a new thread, under the ambient context when one is given, and lists those whose
    // exception the oracle rejects.
    private static List<string> InvokeEach(
        ExecutionContext? ambient,
        EntryPoint[] entryPoints,
        Func<EntryPoint, Exception?, bool> oracle)
    {
        var failures = new List<string>();
        EntryPoint? current = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (ambient is null)
                {
                    InvokeAll();
                }
                else
                {
                    ExecutionContext.Run(ambient, static state => ((Action)state!)(), (Action)InvokeAll);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(thread.Join(EscapedWorkTimeout), $"{Volatile.Read(ref current)} blocked its thread.");
        Assert.Null(failure);
        return failures;

        void InvokeAll()
        {
            foreach (EntryPoint entryPoint in entryPoints)
            {
                Volatile.Write(ref current, entryPoint);
                Exception? exception = Record.Exception(() => InvokeWithDefaultArguments(entryPoint.Method));
                if (!oracle(entryPoint, exception))
                {
                    failures.Add($"{entryPoint}: {exception?.GetType().Name ?? "returned"} {(exception as SimulationThreadAffinityException)?.ApiName ?? (exception as SimulationNotActiveException)?.ApiName}");
                }
            }
        }
    }

    private static EntryPoint[] ControlledEntryPoints()
    {
        Module shims = typeof(SimulationRuntimeDispatch).Module;
        using ModuleDefinition module = ModuleDefinition.ReadModule(shims.Assembly.Location);
        var entryPoints = new Dictionary<int, EntryPoint>();
        var empty = new List<string>();
        foreach ((_, RewriteRule rule) in BuiltInRuleSets.DeterministicBclInventory.Concat(BuiltInRuleSets.ControlledTasksInventory))
        {
            // Rejected APIs throw wherever they run; injected scheduling points are not controlled APIs.
            if (rule.Policy != SimulationApiPolicy.Controlled
                || rule.Operation is RewriteOperationKind.InjectRejection or RewriteOperationKind.InjectSchedulingPoint)
            {
                continue;
            }

            TypeDefinition type = module.GetType(rule.Replacement.DeclaringTypeFullName)
                ?? throw new InvalidOperationException($"Shim type '{rule.Replacement.DeclaringTypeFullName}' is missing.");
            MethodDefinition[] targets = rule.Operation == RewriteOperationKind.SubstituteType
                ? [.. type.Methods.Where(static method => method.IsPublic || method.IsVirtual)]
                : [.. type.Methods.Where(method => method.IsStatic
                    && method.IsPublic
                    && method.Name == rule.Replacement.MemberName
                    && ParametersMatch(method, rule.Replacement.ParameterTypeFullNames))];
            if (targets.Length == 0)
            {
                empty.Add(rule.Id);
            }

            string api = rule.Target.DeclaringTypeFullName.Replace('/', '.');
            foreach (MethodDefinition target in targets)
            {
                int token = target.MetadataToken.ToInt32();
                entryPoints.TryAdd(token, new EntryPoint(Close(shims.ResolveMethod(token)!), api));
            }
        }

        Assert.True(empty.Count == 0, $"Rules without a shim target: {string.Join(", ", empty)}");
        return [.. entryPoints.Values];
    }

    private static bool ParametersMatch(MethodDefinition method, ImmutableArray<string> parameterTypeFullNames) =>
        parameterTypeFullNames.IsDefault
        || method.Parameters.Select(static parameter => parameter.ParameterType.FullName).SequenceEqual(parameterTypeFullNames);

    private static MethodBase Close(MethodBase method)
    {
        if (method.DeclaringType is { IsGenericTypeDefinition: true } open)
        {
            Type closed = open.MakeGenericType([.. open.GetGenericArguments().Select(ChooseTypeArgument)]);
            method = MethodBase.GetMethodFromHandle(method.MethodHandle, closed.TypeHandle)!;
        }

        return method is MethodInfo { IsGenericMethodDefinition: true } generic
            ? generic.MakeGenericMethod([.. generic.GetGenericArguments().Select(ChooseTypeArgument)])
            : method;
    }

    private static Type ChooseTypeArgument(Type parameter)
    {
        Type[] candidates = [typeof(int), typeof(object), typeof(StateMachineStub), typeof(AwaiterStub)];
        return candidates.FirstOrDefault(candidate => Satisfies(candidate, parameter))
            ?? throw new InvalidOperationException($"No stub type satisfies the constraints of '{parameter}'.");
    }

    private static bool Satisfies(Type candidate, Type parameter)
    {
        GenericParameterAttributes attributes = parameter.GenericParameterAttributes;
        if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && candidate.IsValueType)
        {
            return false;
        }

        if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint) && !candidate.IsValueType)
        {
            return false;
        }

        return parameter.GetGenericParameterConstraints()
            .All(constraint => constraint.ContainsGenericParameters || constraint.IsAssignableFrom(candidate));
    }

    // Emits the call, so by-reference and by-ref-like parameters (spans, say) receive default values as well.
    private static void InvokeWithDefaultArguments(MethodBase method)
    {
        Type declaring = method.DeclaringType!;
        var invoker = new DynamicMethod(
            "Invoke" + method.Name,
            typeof(void),
            [typeof(object)],
            typeof(ThreadAffinityConformanceTests).Module,
            skipVisibility: true);
        ILGenerator il = invoker.GetILGenerator();
        if (method is MethodInfo { IsStatic: false })
        {
            if (declaring.IsValueType)
            {
                EmitDefault(il, declaring, byReference: true);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, declaring);
            }
        }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            Type type = parameter.ParameterType;
            if (type == typeof(bool) && parameter.Name == "disposing")
            {
                // Dispose(false) is the finalizer's path, which runs outside any simulation.
                il.Emit(OpCodes.Ldc_I4_1);
                continue;
            }

            EmitDefault(il, type.IsByRef ? type.GetElementType()! : type, type.IsByRef);
        }

        if (method is ConstructorInfo constructor)
        {
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Pop);
        }
        else
        {
            var target = (MethodInfo)method;
            il.Emit(OpCodes.Call, target);
            if (target.ReturnType != typeof(void))
            {
                il.Emit(OpCodes.Pop);
            }
        }

        il.Emit(OpCodes.Ret);
        object? receiver = method is MethodInfo { IsStatic: false } && !declaring.IsValueType
            ? RuntimeHelpers.GetUninitializedObject(declaring)
            : null;
        invoker.CreateDelegate<Action<object?>>()(receiver);
    }

    private static void EmitDefault(ILGenerator il, Type type, bool byReference)
    {
        LocalBuilder local = il.DeclareLocal(type);
        il.Emit(OpCodes.Ldloca, local);
        il.Emit(OpCodes.Initobj, type);
        il.Emit(byReference ? OpCodes.Ldloca : OpCodes.Ldloc, local);
    }

    // An entry point's API name begins with its rule's target type, without generic arity, except that an awaiter or
    // awaitable is named after the API which produced it.
    private sealed record EntryPoint(MethodBase Method, string TargetType)
    {
        private static readonly Dictionary<string, string> AwaitedApis = new(StringComparer.Ordinal)
        {
            ["System.Runtime.CompilerServices.TaskAwaiter"] = "System.Threading.Tasks.Task.GetAwaiter",
            ["System.Runtime.CompilerServices.ConfiguredTaskAwaitable"] = "System.Threading.Tasks.Task.ConfigureAwait",
            ["System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter"] = "System.Threading.Tasks.Task.ConfigureAwait",
            ["System.Runtime.CompilerServices.YieldAwaitable"] = "System.Threading.Tasks.Task.Yield",
            ["System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter"] = "System.Threading.Tasks.Task.Yield",
            ["System.Runtime.CompilerServices.ValueTaskAwaiter"] = "System.Threading.Tasks.ValueTask.GetAwaiter",
            ["System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable"] = "System.Threading.Tasks.ValueTask.ConfigureAwait",
            ["System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter"] = "System.Threading.Tasks.ValueTask.ConfigureAwait",
        };

        public bool Names(string apiName)
        {
            string target = WithoutArity(TargetType);
            return WithoutArity(apiName).StartsWith(AwaitedApis.GetValueOrDefault(target, target), StringComparison.Ordinal);
        }

        public override string ToString() => $"{Method.DeclaringType}::{Method}";

        private static string WithoutArity(string name) => Regex.Replace(name, "`[0-9]+", string.Empty);
    }

    private struct StateMachineStub : IAsyncStateMachine
    {
        public readonly void MoveNext()
        {
        }

        public readonly void SetStateMachine(IAsyncStateMachine stateMachine)
        {
        }
    }

    private readonly struct AwaiterStub : ICriticalNotifyCompletion
    {
        public void OnCompleted(Action continuation)
        {
        }

        public void UnsafeOnCompleted(Action continuation)
        {
        }
    }
}
