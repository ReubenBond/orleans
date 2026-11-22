using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Orleans.Testing.Simulation.Instrumentation.Manifest;
using Orleans.Testing.Simulation.Instrumentation.Rewriting;
using Orleans.Testing.Simulation.Instrumentation.Rules;
using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;
using Orleans.Testing.Simulation.Runtime.Policy;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Golden;

/// <summary>
/// Exhaustive end-to-end coverage for the modern synchronization inventory. The direct fixture invokes
/// every receiver-first or factory rule, while the closure fixture proves whole-type substitutions also
/// retarget compiler-generated captures and <c>Action&lt;Barrier&gt;</c>.
/// </summary>
public sealed class ModernSynchronizationRuleGoldenTests
{
    private const string DirectFixture = """
        using System;
        using System.Runtime.Serialization;
        using System.Threading;

        namespace Fx
        {
            public static class ModernSynchronizationUser
            {
                public static ReaderWriterLockSlim ReaderWriterDefault() => new ReaderWriterLockSlim();
                public static ReaderWriterLockSlim ReaderWriterRecursive() => new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                public static int ReaderWriterProperties(ReaderWriterLockSlim value) =>
                    value.CurrentReadCount + value.RecursiveReadCount + value.RecursiveUpgradeCount + value.RecursiveWriteCount +
                    value.WaitingReadCount + value.WaitingUpgradeCount + value.WaitingWriteCount +
                    (value.IsReadLockHeld ? 1 : 0) + (value.IsUpgradeableReadLockHeld ? 1 : 0) +
                    (value.IsWriteLockHeld ? 1 : 0) + (int)value.RecursionPolicy;
                public static void ReaderWriterMembers(ReaderWriterLockSlim value)
                {
                    value.EnterReadLock(); _ = value.TryEnterReadLock(0); _ = value.TryEnterReadLock(TimeSpan.Zero); value.ExitReadLock();
                    value.EnterUpgradeableReadLock(); _ = value.TryEnterUpgradeableReadLock(0); _ = value.TryEnterUpgradeableReadLock(TimeSpan.Zero); value.ExitUpgradeableReadLock();
                    value.EnterWriteLock(); _ = value.TryEnterWriteLock(0); _ = value.TryEnterWriteLock(TimeSpan.Zero); value.ExitWriteLock();
                    value.Dispose();
                }

                public static ManualResetEventSlim EventDefault() => new ManualResetEventSlim();
                public static ManualResetEventSlim EventInitial() => new ManualResetEventSlim(true);
                public static ManualResetEventSlim EventSpin() => new ManualResetEventSlim(false, 1);
                public static int EventProperties(ManualResetEventSlim value) => (value.IsSet ? 1 : 0) + value.SpinCount + (value.WaitHandle is null ? 0 : 1);
                public static void EventMembers(ManualResetEventSlim value, CancellationToken token)
                {
                    value.Set(); value.Reset(); value.Wait(); value.Wait(token);
                    _ = value.Wait(0); _ = value.Wait(0, token); _ = value.Wait(TimeSpan.Zero); _ = value.Wait(TimeSpan.Zero, token);
                    value.Dispose();
                }

                public static Mutex MutexDefault() => new Mutex();
                public static Mutex MutexInitial() => new Mutex(false);
                public static void MutexNamed()
                {
                    _ = new Mutex(false, "named");
                    _ = new Mutex(false, "named", out bool first);
                    _ = new Mutex(false, "named", default(NamedWaitHandleOptions));
                    _ = new Mutex(false, "named", default(NamedWaitHandleOptions), out bool second);
                    _ = new Mutex("named", default(NamedWaitHandleOptions));
                    _ = Mutex.OpenExisting("named");
                    _ = Mutex.OpenExisting("named", default(NamedWaitHandleOptions));
                    _ = Mutex.TryOpenExisting("named", out Mutex opened);
                    _ = Mutex.TryOpenExisting("named", default(NamedWaitHandleOptions), out Mutex openedWithOptions);
                }
                public static void MutexRelease(Mutex value) => value.ReleaseMutex();

                public static Semaphore SemaphoreDefault() => new Semaphore(1, 2);
                public static void SemaphoreNamed()
                {
                    _ = new Semaphore(1, 2, "named");
                    _ = new Semaphore(1, 2, "named", out bool first);
                    _ = new Semaphore(1, 2, "named", default(NamedWaitHandleOptions));
                    _ = new Semaphore(1, 2, "named", default(NamedWaitHandleOptions), out bool second);
                    _ = Semaphore.OpenExisting("named");
                    _ = Semaphore.OpenExisting("named", default(NamedWaitHandleOptions));
                    _ = Semaphore.TryOpenExisting("named", out Semaphore opened);
                    _ = Semaphore.TryOpenExisting("named", default(NamedWaitHandleOptions), out Semaphore openedWithOptions);
                }
                public static int SemaphoreRelease(Semaphore value) => value.Release() + value.Release(1);

                public static void ExecutionContextMembers(ExecutionContext value, SerializationInfo info, StreamingContext streaming)
                {
                    _ = ExecutionContext.Capture();
                    ExecutionContext.Run(value, _ => { }, null);
                    _ = ExecutionContext.SuppressFlow();
                    ExecutionContext.RestoreFlow();
                    _ = ExecutionContext.IsFlowSuppressed();
                    ExecutionContext.Restore(value);
                    _ = value.CreateCopy();
                    value.Dispose();
                    value.GetObjectData(info, streaming);
                }

                public static void SynchronizationContextMembers(SynchronizationContext value)
                {
                    _ = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(value);
                    _ = value.CreateCopy();
                    _ = value.IsWaitNotificationRequired();
                    value.OperationStarted();
                    value.OperationCompleted();
                    value.Post(_ => { }, null);
                    value.Send(_ => { }, null);
                    _ = value.Wait(Array.Empty<IntPtr>(), false, 0);
                }

                public static void SpinLockMembers(ref SpinLock value)
                {
                    _ = value.IsHeld; _ = value.IsHeldByCurrentThread; _ = value.IsThreadOwnerTrackingEnabled;
                    bool taken = false; value.Enter(ref taken); value.Exit(); value.Exit(false);
                    taken = false; value.TryEnter(ref taken); taken = false; value.TryEnter(0, ref taken);
                    taken = false; value.TryEnter(TimeSpan.Zero, ref taken);
                }
                public static SpinLock SpinLockConstructor() => new SpinLock(true);

                public static Barrier BarrierDefault() => new Barrier(1);
                public static Barrier BarrierCallback() => new Barrier(1, barrier => BarrierCallbackBody(barrier));
                public static void BarrierCallbackBody(Barrier barrier) => _ = barrier.CurrentPhaseNumber;
                public static void BarrierMembers(Barrier value, CancellationToken token)
                {
                    _ = value.CurrentPhaseNumber; _ = value.ParticipantCount; _ = value.ParticipantsRemaining;
                    _ = value.AddParticipant(); _ = value.AddParticipants(1); value.RemoveParticipant(); value.RemoveParticipants(1);
                    value.SignalAndWait(); value.SignalAndWait(token); _ = value.SignalAndWait(0); _ = value.SignalAndWait(0, token);
                    _ = value.SignalAndWait(TimeSpan.Zero); _ = value.SignalAndWait(TimeSpan.Zero, token); value.Dispose();
                }

                public static CountdownEvent CountdownDefault() => new CountdownEvent(1);
                public static int CountdownProperties(CountdownEvent value) =>
                    value.CurrentCount + value.InitialCount + (value.IsSet ? 1 : 0) + (value.WaitHandle is null ? 0 : 1);
                public static void CountdownMembers(CountdownEvent value, CancellationToken token)
                {
                    value.AddCount(); value.AddCount(1); _ = value.TryAddCount(); _ = value.TryAddCount(1);
                    _ = value.Signal(); _ = value.Signal(1); value.Reset(); value.Reset(1);
                    value.Wait(); value.Wait(token); _ = value.Wait(0); _ = value.Wait(0, token);
                    _ = value.Wait(TimeSpan.Zero); _ = value.Wait(TimeSpan.Zero, token); value.Dispose();
                }
            }
        }
        """;

    private const string ClosureFixture = """
        using System;
        using System.Threading;

        namespace Fx
        {
            public static class Captures
            {
                public static Action Capture(Barrier barrier, CountdownEvent countdown) =>
                    () => { _ = barrier.CurrentPhaseNumber; countdown.Signal(); };

                public static Barrier CreateWithCallback() =>
                    new Barrier(1, barrier => { _ = barrier.ParticipantCount; });
            }
        }
        """;

    private const string CrossAssemblyApiFixture = """
        using System.Threading;

        namespace Fx.Contracts
        {
            public static class SynchronizationApi
            {
                public static Barrier ReturnBarrier(Barrier barrier, CountdownEvent countdown, SpinLock spinLock) => barrier;
                public static CountdownEvent ReturnCountdown(Barrier barrier, CountdownEvent countdown, SpinLock spinLock) => countdown;
                public static SpinLock ReturnSpinLock(Barrier barrier, CountdownEvent countdown, SpinLock spinLock) => spinLock;
            }
        }
        """;

    private const string CrossAssemblyConsumerFixture = """
        using System.Threading;
        using Fx.Contracts;

        namespace Fx.Consumer
        {
            public static class SynchronizationConsumer
            {
                public static Barrier CallBarrier(Barrier barrier, CountdownEvent countdown, SpinLock spinLock) =>
                    SynchronizationApi.ReturnBarrier(barrier, countdown, spinLock);

                public static CountdownEvent CallCountdown(Barrier barrier, CountdownEvent countdown, SpinLock spinLock) =>
                    SynchronizationApi.ReturnCountdown(barrier, countdown, spinLock);

                public static SpinLock CallSpinLock(Barrier barrier, CountdownEvent countdown, SpinLock spinLock) =>
                    SynchronizationApi.ReturnSpinLock(barrier, countdown, spinLock);
            }
        }
        """;

    private static string RuntimeAssemblyPath =>
        typeof(Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier).Assembly.Location;

    [Fact]
    public void EveryModernSynchronizationDirectRuleRewritesToTheControlledRuntimeAndManifest()
    {
        using var context = RewriteTestContext.Create();
        RewriteResult result = Rewrite(context, "Fx.ModernSynchronization", DirectFixture);

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ModernSynchronization.rewritten.dll"));

        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledReaderWriterLockSlim"));
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledManualResetEventSlim"));
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledMutex"));
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledSemaphore"));
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledExecutionContext"));
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, "ControlledSynchronizationContext"));
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "ReaderWriterLockSlim::.ctor"));
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "ManualResetEventSlim::.ctor"));
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "Threading.Mutex::.ctor"));
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "Threading.Semaphore::.ctor"));
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "System.Threading.ExecutionContext::Capture"));
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, "System.Threading.SynchronizationContext::Wait"));

        AssertWholeTypeWasRetargeted(module, "System.Threading.SpinLock", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledSpinLock");
        AssertWholeTypeWasRetargeted(module, "System.Threading.Barrier", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier");
        AssertWholeTypeWasRetargeted(module, "System.Threading.CountdownEvent", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledCountdownEvent");

        BuiltInRuleFamily[] modernSynchronizationFamilies =
        [
            BuiltInRuleFamily.ReaderWriterLockSlim,
            BuiltInRuleFamily.ManualResetEventSlim,
            BuiltInRuleFamily.Mutex,
            BuiltInRuleFamily.KernelSemaphore,
            BuiltInRuleFamily.SpinLock,
            BuiltInRuleFamily.ExecutionContext,
            BuiltInRuleFamily.SynchronizationContext,
            BuiltInRuleFamily.Barrier,
            BuiltInRuleFamily.CountdownEvent,
        ];
        ImmutableHashSet<string> transformed = result.Manifest.Transformations
            .Select(transformation => transformation.RuleId)
            .ToImmutableHashSet(StringComparer.Ordinal);
        foreach ((BuiltInRuleFamily _, Orleans.Testing.Simulation.Instrumentation.Rules.RewriteRule rule) in BuiltInRuleSets.ControlledTasksInventory
            .Where(entry => modernSynchronizationFamilies.Contains(entry.Family)))
        {
            Assert.Contains(rule.Id, transformed);
            if (rule.Target.MemberName is not null)
            {
                Assert.False(CecilInspect.AnyMethodCallsContaining(
                    module,
                    rule.Target.DeclaringTypeFullName + "::" + rule.Target.MemberName),
                    $"The original target for '{rule.Id}' remained in the rewritten fixture.");
            }
        }

        Assert.Contains(result.Manifest.Transformations, transformation =>
            transformation.RuleId == "clockwork.mutex.openexisting" &&
            transformation.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(result.Manifest.Transformations, transformation =>
            transformation.RuleId == "clockwork.executioncontext.getobjectdata" &&
            transformation.Policy == SimulationApiPolicy.Rejected);
        Assert.Contains(result.Manifest.Transformations, transformation =>
            transformation.RuleId == "clockwork.synchronizationcontext.wait" &&
            transformation.Policy == SimulationApiPolicy.Rejected);
    }

    [Fact]
    public void WholeTypeSubstitutionsRetargetClosureFieldsAndBarrierCallbackGenericArguments()
    {
        using var context = RewriteTestContext.Create();
        Rewrite(context, "Fx.ModernSynchronizationClosures", ClosureFixture);

        using ModuleDefinition module = context.LoadModule(
            Path.Combine(context.Directory, "Fx.ModernSynchronizationClosures.rewritten.dll"));
        AssertWholeTypeWasRetargeted(module, "System.Threading.Barrier", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledBarrier");
        AssertWholeTypeWasRetargeted(module, "System.Threading.CountdownEvent", "Orleans.Testing.Simulation.Shims.System.Threading.ControlledCountdownEvent");
        Assert.DoesNotContain(
            module.GetTypes().SelectMany(type => type.Fields),
            field => field.FieldType.FullName.Contains("System.Threading.Barrier", StringComparison.Ordinal)
                || field.FieldType.FullName.Contains("System.Threading.CountdownEvent", StringComparison.Ordinal));
    }

    [Fact]
    public void CrossAssemblyCallsRebuildSignaturesWhenOnlyParameterAndReturnTypesAreSubstituted()
    {
        using var context = RewriteTestContext.Create();
        string apiPath = FixtureCompiler.Compile(
            "Fx.SignatureApi",
            CrossAssemblyApiFixture,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize: false);
        string consumerPath = FixtureCompiler.Compile(
            "Fx.SignatureConsumer",
            CrossAssemblyConsumerFixture,
            context.Directory,
            FixtureSymbols.PortableFile,
            optimize: false,
            additionalReferencePaths: [apiPath]);
        string rewrittenApiPath = Path.Combine(context.Directory, "Fx.SignatureApi.rewritten.dll");
        string rewrittenConsumerPath = Path.Combine(context.Directory, "Fx.SignatureConsumer.rewritten.dll");
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],

            // Both assemblies are rewritten, as the closure runner would declare.
            RewrittenAssemblyNames = ["Fx.SignatureApi", "Fx.SignatureConsumer"],
        };
        RewriteRuleSet rules = BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies);

        RewriteEngine.Rewrite(new RewriteRequest(apiPath, rewrittenApiPath, rules, options)).EnsureSuccess();
        RewriteEngine.Rewrite(new RewriteRequest(consumerPath, rewrittenConsumerPath, rules, options)).EnsureSuccess();

        using ModuleDefinition consumer = context.LoadModule(rewrittenConsumerPath);
        MethodReference[] apiCalls = consumer.GetType("Fx.Consumer.SynchronizationConsumer")!.Methods
            .SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Where(method => method.DeclaringType.FullName == "Fx.Contracts.SynchronizationApi")
            .ToArray();
        Assert.Equal(3, apiCalls.Length);
        Assert.All(apiCalls, call =>
        {
            Assert.False(call.ReturnType.FullName.StartsWith("System.Threading.", StringComparison.Ordinal));
            Assert.All(call.Parameters, parameter =>
                Assert.False(parameter.ParameterType.FullName.StartsWith("System.Threading.", StringComparison.Ordinal)));
        });

        File.Copy(rewrittenApiPath, apiPath, overwrite: true);
        var loadContext = new DirectoryLoadContext(context.Directory);
        try
        {
            Assembly consumerAssembly = loadContext.LoadFromAssemblyPath(rewrittenConsumerPath);
            Type consumerType = consumerAssembly.GetType("Fx.Consumer.SynchronizationConsumer", throwOnError: true)!;
            foreach (string methodName in new[] { "CallBarrier", "CallCountdown", "CallSpinLock" })
            {
                MethodInfo method = consumerType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
                Type spinLockType = method.GetParameters()[2].ParameterType;
                object spinLock = Activator.CreateInstance(spinLockType)!;

                // If MapMethod retains the original BCL signature, this invocation fails with
                // MissingMethodException before the trivial API method can return.
                object? result = method.Invoke(null, [null, null, spinLock]);
                if (methodName == "CallSpinLock")
                {
                    Assert.NotNull(result);
                }
                else
                {
                    Assert.Null(result);
                }
            }
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static RewriteResult Rewrite(RewriteTestContext context, string assemblyName, string fixture)
    {
        string fixturePath = context.CompileFixture(assemblyName, fixture);
        string outputPath = Path.Combine(context.Directory, assemblyName + ".rewritten.dll");
        var options = new RewriteOptions
        {
            TargetRuntime = RewriteTestContext.TargetRuntime,
            ReplacementAssemblyPaths = [RuntimeAssemblyPath],
            ReferenceSearchDirectories = [context.Directory, Path.GetDirectoryName(RuntimeAssemblyPath)!],
        };

        RewriteResult result = RewriteEngine.Rewrite(new RewriteRequest(
            fixturePath,
            outputPath,
            BuiltInRuleSets.BuildControlledTasks(BuiltInRuleSets.AllFamilies),
            options));
        result.EnsureSuccess();
        return result;
    }

    private static void AssertWholeTypeWasRetargeted(ModuleDefinition module, string original, string controlled)
    {
        Assert.True(CecilInspect.AnyMethodCallsContaining(module, controlled));
        Assert.False(CecilInspect.AnyMethodCallsContaining(module, original));
    }

    private sealed class DirectoryLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _directory = directory;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            string candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
