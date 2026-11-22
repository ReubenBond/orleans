using Orleans.Testing.Simulation.Instrumentation.Rules.BuiltIn;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Threading;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Conformance.Tests;

public sealed class ModernSynchronizationConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;

        namespace Conf
        {
            public static class ModernSynchronizationProbe
            {
                private static SpinLock _fieldLock = new SpinLock(true);
                private static int _posted;

                public static int ReaderWriter()
                {
                    using var value = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
                    value.EnterReadLock();
                    var reads = value.CurrentReadCount + value.RecursiveReadCount;
                    value.ExitReadLock();
                    value.EnterUpgradeableReadLock();
                    value.EnterWriteLock();
                    var held = value.IsUpgradeableReadLockHeld && value.IsWriteLockHeld;
                    value.ExitWriteLock();
                    value.ExitUpgradeableReadLock();
                    return reads + (held ? 1 : 0) +
                        (value.TryEnterReadLock(TimeSpan.Zero) ? 1 : 0);
                }

                public static int ManualResetEvent()
                {
                    using var value = new ManualResetEventSlim(false, 2047);
                    var bridge = value.WaitHandle;
                    var timedOut = !value.Wait(0) && !value.Wait(TimeSpan.Zero, CancellationToken.None);
                    value.Set();
                    value.Wait();
                    value.Wait(CancellationToken.None);
                    var signaled = value.Wait(0, CancellationToken.None) && bridge.WaitOne(0);
                    value.Reset();
                    return (timedOut ? 1 : 0) + (signaled ? 1 : 0) + value.SpinCount;
                }

                public static int MutexAndSemaphore()
                {
                    using var mutex = new Mutex(false, null);
                    using var semaphore = new Semaphore(1, 2, null);
                    using var allFirst = new Semaphore(1, 1, null);
                    using var allSecond = new Semaphore(1, 1, null);
                    WaitHandle mutexHandle = mutex;
                    WaitHandle semaphoreHandle = semaphore;
                    var mutexTaken = mutexHandle.WaitOne(0);
                    mutex.ReleaseMutex();
                    var any = WaitHandle.WaitAny(new[] { semaphoreHandle, mutexHandle }, 0);
                    var all = WaitHandle.WaitAll(new WaitHandle[] { allFirst, allSecond }, 0);
                    return (mutexTaken ? 1 : 0) + any + (all ? 10 : 0) + semaphore.Release();
                }

                public static bool SpinLock()
                {
                    var local = new SpinLock(true);
                    var taken = false;
                    local.Enter(ref taken);
                    var localHeld = taken && local.IsHeldByCurrentThread;
                    local.Exit();
                    taken = false;
                    _fieldLock.Enter(ref taken);
                    var fieldHeld = taken && _fieldLock.IsHeldByCurrentThread;
                    _fieldLock.Exit(false);
                    return localHeld && fieldHeld && !_fieldLock.IsHeld;
                }

                public static int Contexts()
                {
                    var local = new AsyncLocal<int> { Value = 3 };
                    var capture = ExecutionContext.Capture();
                    local.Value = 8;
                    var flowed = 0;
                    ExecutionContext.Run(capture, _ => flowed = local.Value, null);
                    ExecutionContext.SuppressFlow();
                    var suppressed = ExecutionContext.Capture() is null;
                    ExecutionContext.RestoreFlow();

                    var context = new SynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(context);
                    var current = SynchronizationContext.Current == context;
                    context.Send(_ => _posted += 1, null);
                    context.Post(_ => _posted += 10, null);
                    SynchronizationContext.SetSynchronizationContext(null);
                    return flowed + (suppressed ? 1 : 0) + (current ? 1 : 0) + _posted;
                }

                public static int Posted() => _posted;

                public static int BarrierAndCountdown()
                {
                    var phases = 0;
                    using var barrier = new Barrier(1, _ => phases++);
                    var barrierResult = barrier.SignalAndWait(0);
                    using var countdown = new CountdownEvent(1);
                    var bridge = countdown.WaitHandle;
                    var zero = !countdown.Wait(0);
                    var set = countdown.Signal();
                    countdown.Wait(CancellationToken.None);
                    return phases + (barrierResult ? 1 : 0) + (zero ? 1 : 0) + (set ? 1 : 0) +
                        (bridge.WaitOne(0) ? 1 : 0);
                }

                public static void NamedMutex() => _ = new Mutex(false, "clockwork-conformance-named");
                public static void NamedSemaphore() => _ = new Semaphore(0, 1, "clockwork-conformance-named");
                public static void RawSynchronizationContextWait() =>
                    new SynchronizationContext().Wait(new[] { IntPtr.Zero }, false, 0);
            }
        }
        """;

    private const string ThirdPartyClosureSource = """
        using System;
        using System.Threading;

        namespace ThirdParty
        {
            public static class ModernPrimitiveDependency
            {
                public static int InvokeClosure()
                {
                    var signal = new ManualResetEventSlim(false);
                    Action action = () =>
                    {
                        signal.Set();
                        if (!signal.Wait(0))
                        {
                            throw new InvalidOperationException("controlled signal was not observed");
                        }
                    };

                    action();
                    return signal.IsSet ? 1 : 0;
                }
            }
        }
        """;

    private readonly RewriteFixture _fixture = new();

    public static TheoryData<bool> Optimizations => new() { false, true };

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void RewrittenModernSynchronizationFamiliesExecuteInsideSimulation(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        Assert.Equal(4, (int)host.Invoke(probe.Method("ReaderWriter"))!);
        Assert.Equal(2049, (int)host.Invoke(probe.Method("ManualResetEvent"))!);
        Assert.Equal(11, (int)host.Invoke(probe.Method("MutexAndSemaphore"))!);
        Assert.True((bool)host.Invoke(probe.Method("SpinLock"))!);
        Assert.Equal(6, (int)host.Invoke(probe.Method("Contexts"))!);
        Assert.Equal(11, (int)host.Invoke(probe.Method("Posted"))!);
        Assert.Equal(5, (int)host.Invoke(probe.Method("BarrierAndCountdown"))!);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void RewrittenUnsupportedModernSynchronizationApisAreRejectedByTheSimulation(bool optimize)
    {
        StagedProbe probe = Stage(optimize);
        using var host = new SimulationHost(Start);

        Assert.Throws<SimulationApiException>(() => host.Invoke(probe.Method("NamedMutex")));
        Assert.Throws<SimulationApiException>(() => host.Invoke(probe.Method("NamedSemaphore")));
        Assert.Throws<SimulationApiException>(
            () => host.Invoke(probe.Method("RawSynchronizationContextWait")));
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void RewrittenThirdPartyClosureUsesModernPrimitiveInsideSimulation(bool optimize)
    {
        StagedProbe dependency = _fixture.StageControlledTasks(
            $"ThirdParty.ModernPrimitive.{(optimize ? "Release" : "Debug")}",
            "ThirdParty.ModernPrimitiveDependency",
            ThirdPartyClosureSource,
            optimize,
            [BuiltInRuleFamily.ManualResetEventSlim]);
        using var host = new SimulationHost(Start);

        Assert.Equal(1, (int)host.Invoke(dependency.Method("InvokeClosure"))!);
        Assert.Contains(
            dependency.Result.Manifest.Transformations,
            transformation => transformation.RuleId == "clockwork.manualreseteventslim.set");
        using ModuleDefinition module = ModuleDefinition.ReadModule(dependency.StagedDll);
        TypeDefinition closure = Assert.Single(
            module.GetTypes(),
            type => type.Name.Contains("DisplayClass", StringComparison.Ordinal));
        Assert.Contains(
            closure.Methods.SelectMany(method => method.HasBody ? method.Body.Instructions : []),
            instruction => instruction.Operand is MethodReference reference
                && reference.DeclaringType.FullName == "Orleans.Testing.Simulation.Shims.System.Threading.ControlledManualResetEventSlim"
                && reference.Name == "Set");
    }

    private StagedProbe Stage(bool optimize) =>
        _fixture.StageControlledTasks(
            $"Conf.ModernSynchronization.{(optimize ? "Release" : "Debug")}",
            "Conf.ModernSynchronizationProbe",
            Source,
            optimize,
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
                BuiltInRuleFamily.WaitHandle,
            ]);

    public void Dispose() => _fixture.Dispose();
}
