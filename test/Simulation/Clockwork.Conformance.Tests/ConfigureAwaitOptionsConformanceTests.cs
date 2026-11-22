using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for <c>Task.ConfigureAwait</c> and <c>Task&lt;TResult&gt;.ConfigureAwait</c> in
/// rewritten code, with the BCL's own awaiter as the oracle. Each case awaits an antecedent inside a
/// simulation and replays the same <c>ConfigureAwait</c> on a twin antecedent with the BCL awaiter: whether
/// the await completes synchronously is the BCL awaiter's <c>IsCompleted</c> before the twin completes, and
/// its outcome is what the BCL awaiter's <c>GetResult</c> throws or returns afterwards. The twin keeps the
/// oracle from observing the probe's antecedent. Every flag combination and an undefined option is crossed
/// with every antecedent outcome, completed before or after the await, and the probe is staged in both
/// Release and Debug codegen because their async state machines differ.
/// </summary>
public sealed class ConfigureAwaitOptionsConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        namespace Conf { public static class ConfigureAwaitProbe {
            // Each probe logs the await's progress and whether the async method returned to its caller before
            // the await completed.
            public static Task Await(Task antecedent, ConfigureAwaitOptions options, List<string> log) =>
                Returned(AwaitImpl(antecedent, options, log), log);

            public static Task<int> AwaitGeneric(Task<int> antecedent, ConfigureAwaitOptions options, List<string> log) =>
                Returned(AwaitGenericImpl(antecedent, options, log), log);

            public static Task AwaitBool(Task antecedent, bool continueOnCapturedContext, List<string> log) =>
                Returned(AwaitBoolImpl(antecedent, continueOnCapturedContext, log), log);

            public static Task<int> AwaitGenericBool(Task<int> antecedent, bool continueOnCapturedContext, List<string> log) =>
                Returned(AwaitGenericBoolImpl(antecedent, continueOnCapturedContext, log), log);

            private static T Returned<T>(T pending, List<string> log) where T : Task
            {
                log.Add(pending.IsCompleted ? "returned completed" : "returned pending");
                return pending;
            }

            private static async Task AwaitImpl(Task antecedent, ConfigureAwaitOptions options, List<string> log)
            {
                log.Add("awaiting");
                await antecedent.ConfigureAwait(options);
                log.Add("resumed");
            }

            private static async Task<int> AwaitGenericImpl(Task<int> antecedent, ConfigureAwaitOptions options, List<string> log)
            {
                log.Add("awaiting");
                int value = await antecedent.ConfigureAwait(options);
                log.Add("resumed");
                return value;
            }

            private static async Task AwaitBoolImpl(Task antecedent, bool continueOnCapturedContext, List<string> log)
            {
                log.Add("awaiting");
                await antecedent.ConfigureAwait(continueOnCapturedContext);
                log.Add("resumed");
            }

            private static async Task<int> AwaitGenericBoolImpl(Task<int> antecedent, bool continueOnCapturedContext, List<string> log)
            {
                log.Add("awaiting");
                int value = await antecedent.ConfigureAwait(continueOnCapturedContext);
                log.Add("resumed");
                return value;
            }
        } }
        """;

    private const int SucceededValue = 41;

    /// <summary>Every combination of the three defined flags, then the first undefined bit.</summary>
    private static readonly ConfigureAwaitOptions[] AllOptions =
        [.. Enumerable.Range(0, 9).Select(value => (ConfigureAwaitOptions)value)];

    private static readonly TaskStatus[] Outcomes = [TaskStatus.RanToCompletion, TaskStatus.Faulted, TaskStatus.Canceled];

    private static readonly bool[] Booleans = [false, true];

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;

    public ConfigureAwaitOptionsConformanceTests()
    {
        _release = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.ConfigureAwaitRel", "Conf.ConfigureAwaitProbe", Source, optimize: true));
        _debug = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.ConfigureAwaitDbg", "Conf.ConfigureAwaitProbe", Source, optimize: false));
    }

    /// <summary>Optimization, options, the antecedent's outcome, and whether it completes only after the await suspends.</summary>
    public static TheoryData<bool, ConfigureAwaitOptions, TaskStatus, bool> OptionsCases
    {
        get
        {
            var data = new TheoryData<bool, ConfigureAwaitOptions, TaskStatus, bool>();
            foreach (bool optimize in Booleans)
            {
                foreach (ConfigureAwaitOptions options in AllOptions)
                {
                    foreach (TaskStatus outcome in Outcomes)
                    {
                        foreach (bool pending in Booleans)
                        {
                            data.Add(optimize, options, outcome, pending);
                        }
                    }
                }
            }

            return data;
        }
    }

    /// <summary>Optimization, the bool argument, the antecedent's outcome, and whether it completes only after the await suspends.</summary>
    public static TheoryData<bool, bool, TaskStatus, bool> BoolCases
    {
        get
        {
            var data = new TheoryData<bool, bool, TaskStatus, bool>();
            foreach (bool optimize in Booleans)
            {
                foreach (bool continueOnCapturedContext in Booleans)
                {
                    foreach (TaskStatus outcome in Outcomes)
                    {
                        foreach (bool pending in Booleans)
                        {
                            data.Add(optimize, continueOnCapturedContext, outcome, pending);
                        }
                    }
                }
            }

            return data;
        }
    }

    public static TheoryData<bool, ConfigureAwaitOptions> NullCases
    {
        get
        {
            var data = new TheoryData<bool, ConfigureAwaitOptions>();
            foreach (bool optimize in Booleans)
            {
                foreach (ConfigureAwaitOptions options in AllOptions)
                {
                    data.Add(optimize, options);
                }
            }

            return data;
        }
    }

    public static TheoryData<bool, bool> NullBoolCases => new()
    {
        { true, false },
        { true, true },
        { false, false },
        { false, true },
    };

    [Theory]
    [MemberData(nameof(OptionsCases))]
    public void TaskAwaitBehavesAsTheBclAwaiter(bool optimize, ConfigureAwaitOptions options, TaskStatus outcome, bool pending)
    {
        Observation expected = BclTaskAwait(options, outcome, pending);

        Observation actual = AwaitTask("Await", optimize, options, outcome, pending);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(OptionsCases))]
    public void GenericTaskAwaitBehavesAsTheBclAwaiter(bool optimize, ConfigureAwaitOptions options, TaskStatus outcome, bool pending)
    {
        Observation expected = BclGenericTaskAwait(options, outcome, pending);

        Observation actual = AwaitGenericTask("AwaitGeneric", optimize, options, outcome, pending);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(BoolCases))]
    public void TaskBoolOverloadBehavesAsItsOptionsEquivalent(bool optimize, bool continueOnCapturedContext, TaskStatus outcome, bool pending)
    {
        Observation viaOptions = AwaitTask("Await", optimize, Equivalent(continueOnCapturedContext), outcome, pending);

        Observation viaBool = AwaitTask("AwaitBool", optimize, continueOnCapturedContext, outcome, pending);

        Assert.Equal(viaOptions, viaBool);
    }

    [Theory]
    [MemberData(nameof(BoolCases))]
    public void GenericTaskBoolOverloadBehavesAsItsOptionsEquivalent(
        bool optimize,
        bool continueOnCapturedContext,
        TaskStatus outcome,
        bool pending)
    {
        Observation viaOptions = AwaitGenericTask("AwaitGeneric", optimize, Equivalent(continueOnCapturedContext), outcome, pending);

        Observation viaBool = AwaitGenericTask("AwaitGenericBool", optimize, continueOnCapturedContext, outcome, pending);

        Assert.Equal(viaOptions, viaBool);
    }

    [Theory]
    [MemberData(nameof(NullCases))]
    public void NullTaskThrowsAsTheBclDoes(bool optimize, ConfigureAwaitOptions options)
    {
        Observation expected = Rejected(Record.Exception(() => { _ = NullTask().ConfigureAwait(options); }));

        Observation actual = Run("Await", optimize, antecedent: null, options, []);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(NullCases))]
    public void NullGenericTaskThrowsAsTheBclDoes(bool optimize, ConfigureAwaitOptions options)
    {
        Observation expected = Rejected(Record.Exception(() => { _ = NullGenericTask().ConfigureAwait(options); }));

        Observation actual = Run("AwaitGeneric", optimize, antecedent: null, options, []);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(NullBoolCases))]
    public void NullTaskBoolOverloadThrowsAsTheBclDoes(bool optimize, bool continueOnCapturedContext)
    {
        Observation expected = Rejected(Record.Exception(() => { _ = NullTask().ConfigureAwait(continueOnCapturedContext); }));

        Observation actual = Run("AwaitBool", optimize, antecedent: null, continueOnCapturedContext, []);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(NullBoolCases))]
    public void NullGenericTaskBoolOverloadThrowsAsTheBclDoes(bool optimize, bool continueOnCapturedContext)
    {
        Observation expected = Rejected(Record.Exception(() => { _ = NullGenericTask().ConfigureAwait(continueOnCapturedContext); }));

        Observation actual = Run("AwaitGenericBool", optimize, antecedent: null, continueOnCapturedContext, []);

        Assert.Equal(expected, actual);
    }

    /// <summary>Replays the await on the BCL awaiter of a twin <see cref="Task"/> antecedent.</summary>
    private static Observation BclTaskAwait(ConfigureAwaitOptions options, TaskStatus outcome, bool pending)
    {
        var twin = new TaskCompletionSource();
        if (!pending)
        {
            Complete(twin, outcome);
        }

        ConfiguredTaskAwaitable awaitable = default;
        Exception? rejected = Record.Exception(() => { awaitable = twin.Task.ConfigureAwait(options); });
        if (rejected is not null)
        {
            return Rejected(rejected);
        }

        ConfiguredTaskAwaitable.ConfiguredTaskAwaiter awaiter = awaitable.GetAwaiter();
        bool synchronous = awaiter.IsCompleted;
        if (pending)
        {
            Complete(twin, outcome);
        }

        return Awaited(synchronous, Record.Exception(awaiter.GetResult), result: null);
    }

    /// <summary>Replays the await on the BCL awaiter of a twin <see cref="Task{TResult}"/> antecedent.</summary>
    private static Observation BclGenericTaskAwait(ConfigureAwaitOptions options, TaskStatus outcome, bool pending)
    {
        var twin = new TaskCompletionSource<int>();
        if (!pending)
        {
            Complete(twin, outcome);
        }

        ConfiguredTaskAwaitable<int> awaitable = default;
        Exception? rejected = Record.Exception(() => { awaitable = twin.Task.ConfigureAwait(options); });
        if (rejected is not null)
        {
            return Rejected(rejected);
        }

        ConfiguredTaskAwaitable<int>.ConfiguredTaskAwaiter awaiter = awaitable.GetAwaiter();
        bool synchronous = awaiter.IsCompleted;
        if (pending)
        {
            Complete(twin, outcome);
        }

        int? result = null;
        Exception? thrown = Record.Exception(() => { result = awaiter.GetResult(); });
        return Awaited(synchronous, thrown, result);
    }

    /// <summary>The observable effect of a <c>ConfigureAwait</c> call which throws: the async method faults before it awaits.</summary>
    private static Observation Rejected(Exception? exception)
    {
        Assert.NotNull(exception);
        return Awaited(synchronous: true, exception, result: null);
    }

    /// <summary>
    /// The observable effect on the probe of an await whose awaiter reports <paramref name="synchronous"/> from
    /// <c>IsCompleted</c> and then throws <paramref name="thrown"/> or returns <paramref name="result"/>.
    /// </summary>
    private static Observation Awaited(bool synchronous, Exception? thrown, int? result)
    {
        var log = new List<string> { "awaiting" };
        if (!synchronous)
        {
            log.Add("returned pending");
        }

        if (thrown is null)
        {
            log.Add("resumed");
        }

        if (synchronous)
        {
            log.Add("returned completed");
        }

        TaskStatus status = thrown switch
        {
            null => TaskStatus.RanToCompletion,
            OperationCanceledException => TaskStatus.Canceled,
            _ => TaskStatus.Faulted,
        };

        return new Observation(string.Join(", ", log), status, Describe(thrown), result);
    }

    private Observation AwaitTask(string probe, bool optimize, object option, TaskStatus outcome, bool pending)
    {
        var antecedent = new TaskCompletionSource();
        return Run(probe, optimize, antecedent.Task, option, CompletionWork(() => Complete(antecedent, outcome), pending));
    }

    private Observation AwaitGenericTask(string probe, bool optimize, object option, TaskStatus outcome, bool pending)
    {
        var antecedent = new TaskCompletionSource<int>();
        return Run(probe, optimize, antecedent.Task, option, CompletionWork(() => Complete(antecedent, outcome), pending));
    }

    /// <summary>Runs the probe in a simulation, with <paramref name="work"/> running after it as further simulation work.</summary>
    private Observation Run(string probe, bool optimize, Task? antecedent, object option, Action[] work)
    {
        using var host = new SimulationHost(Start);
        var log = new List<string>();

        var task = (Task)host.InvokeWithWork(Method(probe, optimize), [antecedent, option, log], work)!;

        Assert.True(task.IsCompleted);
        int? result = null;
        Exception? thrown = Record.Exception(() =>
        {
            if (task is Task<int> generic)
            {
                result = generic.GetAwaiter().GetResult();
            }
            else
            {
                task.GetAwaiter().GetResult();
            }
        });

        return new Observation(string.Join(", ", log), task.Status, Describe(thrown), result);
    }

    private MethodInfo Method(string name, bool optimize) =>
        (optimize ? _release : _debug).Value.Method(name);

    /// <summary>
    /// Completes the antecedent now (so the await observes a completed task) or returns the completion as
    /// simulation work that runs after the probe has suspended.
    /// </summary>
    private static Action[] CompletionWork(Action complete, bool pending)
    {
        if (pending)
        {
            return [complete];
        }

        complete();
        return [];
    }

    private static void Complete(TaskCompletionSource source, TaskStatus outcome)
    {
        switch (outcome)
        {
            case TaskStatus.RanToCompletion:
                source.SetResult();
                break;
            case TaskStatus.Faulted:
                source.SetException(new InvalidTimeZoneException("boom"));
                break;
            default:
                source.SetCanceled();
                break;
        }
    }

    private static void Complete(TaskCompletionSource<int> source, TaskStatus outcome)
    {
        switch (outcome)
        {
            case TaskStatus.RanToCompletion:
                source.SetResult(SucceededValue);
                break;
            case TaskStatus.Faulted:
                source.SetException(new InvalidTimeZoneException("boom"));
                break;
            default:
                source.SetCanceled();
                break;
        }
    }

    private static ConfigureAwaitOptions Equivalent(bool continueOnCapturedContext) =>
        continueOnCapturedContext ? ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.None;

    private static Task NullTask() => null!;

    private static Task<int> NullGenericTask() => null!;

    private static string? Describe(Exception? exception) =>
        exception is null ? null : $"{exception.GetType().FullName}: {exception.Message}";

    public void Dispose() => _fixture.Dispose();

    /// <summary>What an await observably did: the probe's log, its task's status, and its exception or result.</summary>
    private sealed record Observation(string Log, TaskStatus Status, string? Exception, int? Result);
}
