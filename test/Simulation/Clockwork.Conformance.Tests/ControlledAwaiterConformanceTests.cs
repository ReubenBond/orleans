using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// Conformance of the controlled task and value-task awaiters outside the compiler's await pattern: a
/// rethrown exception's stack trace matches the one the BCL awaiter produces, with no Clockwork frame; a
/// null task throws the BCL's exception, after the simulation guard; and a synchronous
/// <c>GetAwaiter().GetResult()</c> on a task which later simulation work completes pumps the simulation
/// instead of blocking its only logical thread.
/// </summary>
public sealed class ControlledAwaiterConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // An optimizing JIT may inline the BCL's ConfigureAwait into its caller, removing that frame from the BCL's
    // trace depending on the caller's tier. The probe disables JIT optimization so the BCL trace has the frames
    // of a non-inlined call in every run, as the controlled awaitable produces.
    private const string Source = """
        using System.Diagnostics;
        using System.Threading.Tasks;
        [assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations)]
        namespace Conf { public static class AwaiterProbe {
            public static async Task<int> Fail()
            {
                await Task.CompletedTask;
                throw new System.InvalidTimeZoneException("boom");
            }

            // ---- each await form over a faulted task, and ConfigureAwait calls which the BCL rejects ----
            public static async Task AwaitTask() { await (Task)Fail(); }
            public static async Task AwaitGenericTask() { _ = await Fail(); }
            public static async Task AwaitConfiguredTask() { await ((Task)Fail()).ConfigureAwait(false); }
            public static async Task AwaitConfiguredTaskOptions() { await ((Task)Fail()).ConfigureAwait(ConfigureAwaitOptions.None); }
            public static async Task AwaitConfiguredGenericTask() { _ = await Fail().ConfigureAwait(false); }
            public static async Task AwaitConfiguredGenericTaskOptions() { _ = await Fail().ConfigureAwait(ConfigureAwaitOptions.None); }
            public static async Task AwaitValueTask() { await new ValueTask(Fail()); }
            public static async Task AwaitGenericValueTask() { _ = await new ValueTask<int>(Fail()); }
            public static async Task AwaitConfiguredValueTask() { await new ValueTask(Fail()).ConfigureAwait(false); }
            public static async Task AwaitConfiguredGenericValueTask() { _ = await new ValueTask<int>(Fail()).ConfigureAwait(false); }
            public static async Task RejectTaskOptions() { await Task.CompletedTask.ConfigureAwait((ConfigureAwaitOptions)8); }
            public static async Task RejectGenericTaskOptions() { _ = await Task.FromResult(1).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }

            // ---- null receivers, awaited and as synchronous awaiter-source calls ----
            public static async Task AwaitNullTask() { Task task = null; await task; }
            public static async Task AwaitNullGenericTask() { Task<int> task = null; _ = await task; }
            public static async Task AwaitNullConfiguredTask() { Task task = null; await task.ConfigureAwait(false); }
            public static async Task AwaitNullConfiguredGenericTaskOptions() { Task<int> task = null; _ = await task.ConfigureAwait(ConfigureAwaitOptions.None); }
            public static void GetNullTaskAwaiter() { Task task = null; _ = task.GetAwaiter(); }
            public static void GetNullGenericTaskAwaiter() { Task<int> task = null; _ = task.GetAwaiter(); }
            public static void ConfigureNullTask() { Task task = null; _ = task.ConfigureAwait(false); }
            public static void ConfigureNullGenericTaskOptions() { Task<int> task = null; _ = task.ConfigureAwait(ConfigureAwaitOptions.None); }

            // ---- synchronous GetResult on an antecedent which later simulation work completes ----
            public static object GetTaskResult(Task<int> antecedent)
            {
                ((Task)antecedent).GetAwaiter().GetResult();
                return antecedent.Status;
            }

            public static object GetGenericTaskResult(Task<int> antecedent) => antecedent.GetAwaiter().GetResult();

            public static object GetConfiguredTaskResult(Task<int> antecedent)
            {
                ((Task)antecedent).ConfigureAwait(false).GetAwaiter().GetResult();
                return antecedent.Status;
            }

            public static object GetSuppressedTaskResult(Task<int> antecedent)
            {
                ((Task)antecedent).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
                return antecedent.Status;
            }

            public static object GetConfiguredGenericTaskResult(Task<int> antecedent) =>
                antecedent.ConfigureAwait(ConfigureAwaitOptions.ForceYielding).GetAwaiter().GetResult();

            public static object GetValueTaskResult(Task<int> antecedent)
            {
                new ValueTask(antecedent).GetAwaiter().GetResult();
                return antecedent.Status;
            }

            public static object GetGenericValueTaskResult(Task<int> antecedent) =>
                new ValueTask<int>(antecedent).GetAwaiter().GetResult();

            public static object GetConfiguredValueTaskResult(Task<int> antecedent)
            {
                new ValueTask(antecedent).ConfigureAwait(false).GetAwaiter().GetResult();
                return antecedent.Status;
            }

            public static object GetConfiguredGenericValueTaskResult(Task<int> antecedent) =>
                new ValueTask<int>(antecedent).ConfigureAwait(false).GetAwaiter().GetResult();
        } }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bclRelease;
    private readonly Lazy<UninstrumentedProbe> _bclDebug;

    public ControlledAwaiterConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.AwaiterRel", "Conf.AwaiterProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.AwaiterDbg", "Conf.AwaiterProbe", Source, optimize: false));
        _bclRelease = new Lazy<UninstrumentedProbe>(() =>
            _fixture.CompileUninstrumented("Conf.AwaiterBclRel", "Conf.AwaiterProbe", Source, optimize: true));
        _bclDebug = new Lazy<UninstrumentedProbe>(() =>
            _fixture.CompileUninstrumented("Conf.AwaiterBclDbg", "Conf.AwaiterProbe", Source, optimize: false));
    }

    public static TheoryData<bool, string> FaultingAwaits => WithOptimizations(
        "AwaitTask",
        "AwaitGenericTask",
        "AwaitConfiguredTask",
        "AwaitConfiguredTaskOptions",
        "AwaitConfiguredGenericTask",
        "AwaitConfiguredGenericTaskOptions",
        "AwaitValueTask",
        "AwaitGenericValueTask",
        "AwaitConfiguredValueTask",
        "AwaitConfiguredGenericValueTask",
        "RejectTaskOptions",
        "RejectGenericTaskOptions");

    public static TheoryData<bool, string> NullAwaits => WithOptimizations(
        "AwaitNullTask",
        "AwaitNullGenericTask",
        "AwaitNullConfiguredTask",
        "AwaitNullConfiguredGenericTaskOptions");

    /// <summary>A synchronous awaiter-source call on a null task, and the controlled API its shim guards.</summary>
    public static TheoryData<bool, string, string> NullAwaiterSources
    {
        get
        {
            var data = new TheoryData<bool, string, string>();
            foreach (bool optimize in new[] { true, false })
            {
                data.Add(optimize, "GetNullTaskAwaiter", "System.Threading.Tasks.Task.GetAwaiter");
                data.Add(optimize, "GetNullGenericTaskAwaiter", "System.Threading.Tasks.Task`1.GetAwaiter");
                data.Add(optimize, "ConfigureNullTask", "System.Threading.Tasks.Task.ConfigureAwait");
                data.Add(optimize, "ConfigureNullGenericTaskOptions", "System.Threading.Tasks.Task`1.ConfigureAwait");
            }

            return data;
        }
    }

    /// <summary>The probe, whether the antecedent faults, and the probe's expected return value.</summary>
    public static TheoryData<string, bool, string> SynchronousGetResults => new()
    {
        { "GetTaskResult", false, nameof(TaskStatus.RanToCompletion) },
        { "GetGenericTaskResult", false, "41" },
        { "GetConfiguredTaskResult", false, nameof(TaskStatus.RanToCompletion) },
        { "GetSuppressedTaskResult", true, nameof(TaskStatus.Faulted) },
        { "GetConfiguredGenericTaskResult", false, "41" },
        { "GetValueTaskResult", false, nameof(TaskStatus.RanToCompletion) },
        { "GetGenericValueTaskResult", false, "41" },
        { "GetConfiguredValueTaskResult", false, nameof(TaskStatus.RanToCompletion) },
        { "GetConfiguredGenericValueTaskResult", false, "41" },
    };

    [Theory]
    [MemberData(nameof(FaultingAwaits))]
    public void RethrownExceptionStackTraceMatchesTheBclAwaiter(bool optimize, string probe)
    {
        var bclTask = (Task)Bcl(optimize).Method(probe).Invoke(null, null)!;
        string expected = FaultStackTrace(bclTask);
        using var host = new SimulationHost(Start);

        var task = (Task)host.Invoke(Staged(optimize).Method(probe))!;

        string actual = FaultStackTrace(task);
        Assert.Contains("Conf.AwaiterProbe", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("Clockwork", actual, StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(NullAwaits))]
    public void AwaitingANullTaskFaultsAsTheBclDoes(bool optimize, string probe)
    {
        var bclTask = (Task)Bcl(optimize).Method(probe).Invoke(null, null)!;
        Fault expected = Fault.Of(Assert.IsType<NullReferenceException>(SingleFault(bclTask)));
        using var host = new SimulationHost(Start);

        var task = (Task)host.Invoke(Staged(optimize).Method(probe))!;

        Fault actual = Fault.Of(SingleFault(task));
        Assert.DoesNotContain("Clockwork", actual.StackTrace, StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(NullAwaiterSources))]
    public void NullTaskThrowsAsTheBclDoesAfterTheSimulationGuard(bool optimize, string probe, string apiName)
    {
        var invocation = Assert.Throws<TargetInvocationException>(() => Bcl(optimize).Method(probe).Invoke(null, null));
        var expected = Assert.IsType<NullReferenceException>(invocation.InnerException);
        MethodInfo staged = Staged(optimize).Method(probe);

        // Outside a simulation the guard runs before the task is dereferenced.
        Assert.Equal(apiName, SimulationNotActiveExceptionAssert.Throws(staged).ApiName);

        using var host = new SimulationHost(Start);
        Exception actual = Assert.ThrowsAny<Exception>(() => host.Invoke(staged));

        // SimulationHost rethrows the probe's exception, which replaces its stack trace, so the awaited
        // null-task cases compare stack traces instead.
        Assert.Equal((expected.GetType(), expected.Message), (actual.GetType(), actual.Message));
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(SynchronousGetResults))]
    public async Task SynchronousGetResultPumpsTheSimulationUntilTheTaskCompletes(string probe, bool faults, string expected)
    {
        MethodInfo method = _release.Value.Method(probe);

        // A blocking GetResult would hang this thread forever, so it runs off the test thread and the
        // theory's timeout reports the hang.
        object? result = await Task.Run(
            () =>
            {
                using var host = new SimulationHost(Start);
                var antecedent = new TaskCompletionSource<int>();
                return host.InvokeWithWork(
                    method,
                    [antecedent.Task],
                    () =>
                    {
                        if (faults)
                        {
                            antecedent.SetException(new InvalidTimeZoneException("boom"));
                        }
                        else
                        {
                            antecedent.SetResult(41);
                        }
                    });
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, Convert.ToString(result, CultureInfo.InvariantCulture));
    }

    private static string FaultStackTrace(Task task) => SingleFault(task).StackTrace ?? string.Empty;

    private static Exception SingleFault(Task task)
    {
        Assert.Equal(TaskStatus.Faulted, task.Status);
        return Assert.Single(task.Exception!.InnerExceptions);
    }

    private static TheoryData<bool, string> WithOptimizations(params string[] probes)
    {
        var data = new TheoryData<bool, string>();
        foreach (bool optimize in new[] { true, false })
        {
            foreach (string probe in probes)
            {
                data.Add(optimize, probe);
            }
        }

        return data;
    }

    private StagedProbe Staged(bool optimize) => (optimize ? _release : _debug).Value;

    private UninstrumentedProbe Bcl(bool optimize) => (optimize ? _bclRelease : _bclDebug).Value;

    public void Dispose() => _fixture.Dispose();

    /// <summary>An exception's observable identity: its type, message, and stack trace.</summary>
    private sealed record Fault(string? Type, string Message, string StackTrace)
    {
        public static Fault Of(Exception exception) =>
            new(exception.GetType().FullName, exception.Message, exception.StackTrace ?? string.Empty);
    }
}
