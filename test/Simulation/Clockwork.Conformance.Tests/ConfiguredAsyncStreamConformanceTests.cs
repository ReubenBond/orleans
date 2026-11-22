using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for <c>await using (x.ConfigureAwait(bool))</c> and
/// <c>await foreach (... in source.ConfigureAwait(bool))</c>, <c>.WithCancellation(token)</c>, and their chained
/// forms in rewritten code. The controlled-task rule set substitutes the configured async disposable and
/// enumerable, so their dispose and iteration awaits resume through the coordinator. Every probe runs both
/// rewritten under simulation and uninstrumented against the real BCL, and the two must observe the same
/// iteration, disposal, cancellation, and exception sequence. The probes are staged in both Release and Debug
/// codegen because their async state machines differ.
/// </summary>
public sealed class ConfiguredAsyncStreamConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class AsyncStreamProbe {
            // ---- await using ----
            public static async Task<string> Using(bool continueOnCapturedContext, bool throwInBody, bool throwOnDispose)
            {
                var log = new List<string>();
                try
                {
                    await using (new Resource(log, throwOnDispose).ConfigureAwait(continueOnCapturedContext))
                    {
                        log.Add("body");
                        if (throwInBody)
                        {
                            throw new FormatException("body");
                        }
                    }

                    log.Add("after");
                }
                catch (Exception exception)
                {
                    log.Add("caught " + exception.GetType().Name + " " + exception.Message);
                }

                return string.Join("|", log);
            }

            public static async Task<string> UsingDeclaration(bool continueOnCapturedContext)
            {
                var log = new List<string>();
                await UsingDeclarationCore(log, continueOnCapturedContext);
                log.Add("after");
                return string.Join("|", log);
            }

            private static async Task UsingDeclarationCore(List<string> log, bool continueOnCapturedContext)
            {
                await using ConfiguredAsyncDisposable resource = new Resource(log, throwOnDispose: false).ConfigureAwait(continueOnCapturedContext);
                log.Add("body");
            }

            // ---- await foreach ----
            public static async Task<string> Foreach(bool continueOnCapturedContext, int count, int throwAt, int breakAfter)
            {
                var log = new List<string>();
                try
                {
                    await foreach (int item in Numbers(log, count, throwAt).ConfigureAwait(continueOnCapturedContext))
                    {
                        log.Add("item " + item);
                        if (item == breakAfter)
                        {
                            break;
                        }
                    }

                    log.Add("after");
                }
                catch (Exception exception)
                {
                    log.Add("caught " + exception.GetType().Name + " " + exception.Message);
                }

                return string.Join("|", log);
            }

            // Shape 0: WithCancellation; 1: WithCancellation().ConfigureAwait(); 2: ConfigureAwait().WithCancellation().
            public static async Task<string> Cancel(int shape, bool continueOnCapturedContext, int cancelAfter)
            {
                var log = new List<string>();
                using var source = new CancellationTokenSource();
                IAsyncEnumerable<int> numbers = Numbers(log, 4, throwAt: -1);
                try
                {
                    ConfiguredCancelableAsyncEnumerable<int> configured = shape switch
                    {
                        0 => numbers.WithCancellation(source.Token),
                        1 => numbers.WithCancellation(source.Token).ConfigureAwait(continueOnCapturedContext),
                        _ => numbers.ConfigureAwait(continueOnCapturedContext).WithCancellation(source.Token),
                    };

                    await foreach (int item in configured)
                    {
                        log.Add("item " + item);
                        if (item == cancelAfter)
                        {
                            source.Cancel();
                        }
                    }

                    log.Add("after");
                }
                catch (OperationCanceledException exception)
                {
                    log.Add("caught " + exception.GetType().Name + " token=" + (exception.CancellationToken == source.Token));
                }

                return string.Join("|", log);
            }

            // Both the iterator's [EnumeratorCancellation] argument and WithCancellation supply a token, so the
            // iterator observes a linked token which either source cancels.
            public static async Task<string> CancelLinked(bool cancelEnumeratorToken)
            {
                var log = new List<string>();
                using var argument = new CancellationTokenSource();
                using var enumerator = new CancellationTokenSource();
                try
                {
                    await foreach (int item in Numbers(log, 4, throwAt: -1, argument.Token).WithCancellation(enumerator.Token).ConfigureAwait(false))
                    {
                        log.Add("item " + item);
                        (cancelEnumeratorToken ? enumerator : argument).Cancel();
                    }
                }
                catch (OperationCanceledException exception)
                {
                    log.Add("caught " + exception.GetType().Name
                        + " argument=" + (exception.CancellationToken == argument.Token)
                        + " enumerator=" + (exception.CancellationToken == enumerator.Token));
                }

                return string.Join("|", log);
            }

            // A hand-written sequence whose MoveNextAsync or DisposeAsync throws synchronously or faults.
            public static async Task<string> Sequence(bool continueOnCapturedContext, string moveFailure, string disposeFailure)
            {
                var log = new List<string>();
                try
                {
                    await foreach (int item in new Faulting(log, moveFailure, disposeFailure).ConfigureAwait(continueOnCapturedContext))
                    {
                        log.Add("item " + item);
                    }

                    log.Add("after");
                }
                catch (Exception exception)
                {
                    log.Add("caught " + exception.GetType().Name + " " + exception.Message);
                }

                return string.Join("|", log);
            }

            private static async IAsyncEnumerable<int> Numbers(
                List<string> log,
                int count,
                int throwAt,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                log.Add("start cancelable=" + cancellationToken.CanBeCanceled);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        await Task.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (i == throwAt)
                        {
                            throw new InvalidTimeZoneException("move " + i);
                        }

                        log.Add("yield " + i);
                        yield return i;
                    }
                }
                finally
                {
                    log.Add("iterator finally");
                }
            }

            private sealed class Resource(List<string> log, bool throwOnDispose) : IAsyncDisposable
            {
                public async ValueTask DisposeAsync()
                {
                    log.Add("disposing");
                    await Task.Yield();
                    log.Add("disposed");
                    if (throwOnDispose)
                    {
                        throw new InvalidTimeZoneException("dispose");
                    }
                }
            }

            private sealed class Faulting(List<string> log, string moveFailure, string disposeFailure) : IAsyncEnumerable<int>
            {
                public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken) =>
                    new Enumerator(log, moveFailure, disposeFailure);

                private sealed class Enumerator(List<string> log, string moveFailure, string disposeFailure) : IAsyncEnumerator<int>
                {
                    private int _current = -1;

                    public int Current => _current;

                    public ValueTask<bool> MoveNextAsync()
                    {
                        if (_current == 0 && moveFailure == "sync")
                        {
                            throw new InvalidTimeZoneException("sync move");
                        }

                        return MoveNextCore();
                    }

                    public ValueTask DisposeAsync()
                    {
                        log.Add("dispose");
                        if (disposeFailure == "sync")
                        {
                            throw new FormatException("sync dispose");
                        }

                        return DisposeCore();
                    }

                    private async ValueTask<bool> MoveNextCore()
                    {
                        await Task.Yield();
                        if (_current == 0 && moveFailure == "fault")
                        {
                            throw new InvalidTimeZoneException("faulted move");
                        }

                        _current++;
                        return _current < 2;
                    }

                    private async ValueTask DisposeCore()
                    {
                        await Task.Yield();
                        if (disposeFailure == "fault")
                        {
                            throw new FormatException("faulted dispose");
                        }
                    }
                }
            }
        } }
        """;

    private const string ProbeType = "Conf.AsyncStreamProbe";

    private static readonly string[] ConfiguredRules =
    [
        "clockwork.tasks.configured.asyncdisposable",
        "clockwork.tasks.configured.asyncenumerable",
        "clockwork.tasks.configured.asyncenumerator",
        "clockwork.tasks.asyncdisposable.configureawait",
        "clockwork.tasks.asyncenumerable.configureawait",
        "clockwork.tasks.asyncenumerable.withcancellation",
    ];

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bclRelease;
    private readonly Lazy<UninstrumentedProbe> _bclDebug;

    public ConfiguredAsyncStreamConformanceTests()
    {
        _release = new(() => _fixture.StageControlledTasks("Conf.AsyncStreamRel", ProbeType, Source, optimize: true));
        _debug = new(() => _fixture.StageControlledTasks("Conf.AsyncStreamDbg", ProbeType, Source, optimize: false));
        _bclRelease = new(() => _fixture.CompileUninstrumented("Conf.AsyncStreamBclRel", ProbeType, Source, optimize: true));
        _bclDebug = new(() => _fixture.CompileUninstrumented("Conf.AsyncStreamBclDbg", ProbeType, Source, optimize: false));
    }

    public static TheoryData<bool, bool, bool, bool> UsingCases => Cross(
        [true, false], [false, true], [false, true], [false, true]);

    public static TheoryData<bool, bool> ContextCases => Cross([true, false], [false, true]);

    public static TheoryData<bool, bool, int, int, int> ForeachCases
    {
        get
        {
            var data = new TheoryData<bool, bool, int, int, int>();
            foreach (bool optimize in new[] { true, false })
            {
                foreach (bool continueOnCapturedContext in new[] { false, true })
                {
                    data.Add(optimize, continueOnCapturedContext, 3, -1, -1);
                    data.Add(optimize, continueOnCapturedContext, 0, -1, -1);
                    data.Add(optimize, continueOnCapturedContext, 3, 1, -1);
                    data.Add(optimize, continueOnCapturedContext, 3, -1, 0);
                }
            }

            return data;
        }
    }

    public static TheoryData<bool, int, bool, int> CancelCases
    {
        get
        {
            var data = new TheoryData<bool, int, bool, int>();
            foreach (bool optimize in new[] { true, false })
            {
                foreach (int shape in new[] { 0, 1, 2 })
                {
                    data.Add(optimize, shape, false, 1);
                    data.Add(optimize, shape, true, 1);
                    data.Add(optimize, shape, false, -1);
                }
            }

            return data;
        }
    }

    public static TheoryData<bool, bool, string, string> SequenceCases
    {
        get
        {
            var data = new TheoryData<bool, bool, string, string>();
            foreach (bool optimize in new[] { true, false })
            {
                foreach (bool continueOnCapturedContext in new[] { false, true })
                {
                    data.Add(optimize, continueOnCapturedContext, "none", "none");
                    data.Add(optimize, continueOnCapturedContext, "sync", "none");
                    data.Add(optimize, continueOnCapturedContext, "fault", "none");
                    data.Add(optimize, continueOnCapturedContext, "none", "sync");
                    data.Add(optimize, continueOnCapturedContext, "none", "fault");
                    data.Add(optimize, continueOnCapturedContext, "fault", "fault");
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UsingCases))]
    public async Task AwaitUsingDisposesThroughTheCoordinatorAsTheBclDoes(
        bool optimize, bool continueOnCapturedContext, bool throwInBody, bool throwOnDispose)
    {
        string actual = await AssertMatchesBcl(optimize, "Using", continueOnCapturedContext, throwInBody, throwOnDispose);

        Assert.Contains("disposing|disposed", actual, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ContextCases))]
    public async Task AwaitUsingDeclarationDisposesAsTheBclDoes(bool optimize, bool continueOnCapturedContext)
    {
        string actual = await AssertMatchesBcl(optimize, "UsingDeclaration", continueOnCapturedContext);

        Assert.Equal("body|disposing|disposed|after", actual);
    }

    [Theory]
    [MemberData(nameof(ForeachCases))]
    public async Task AwaitForeachIteratesBreaksAndRethrowsAsTheBclDoes(
        bool optimize, bool continueOnCapturedContext, int count, int throwAt, int breakAfter)
    {
        string actual = await AssertMatchesBcl(optimize, "Foreach", continueOnCapturedContext, count, throwAt, breakAfter);

        // The enumerator is disposed on completion, early break, and fault alike.
        Assert.Contains("iterator finally", actual, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CancelCases))]
    public async Task WithCancellationFlowsTheTokenAsTheBclDoes(
        bool optimize, int shape, bool continueOnCapturedContext, int cancelAfter)
    {
        string actual = await AssertMatchesBcl(optimize, "Cancel", shape, continueOnCapturedContext, cancelAfter);

        Assert.StartsWith("start cancelable=True|", actual, StringComparison.Ordinal);
        if (cancelAfter >= 0)
        {
            Assert.EndsWith("|iterator finally|caught OperationCanceledException token=True", actual, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(ContextCases))]
    public async Task LinkedEnumeratorCancellationMatchesTheBcl(bool optimize, bool cancelEnumeratorToken)
    {
        string actual = await AssertMatchesBcl(optimize, "CancelLinked", cancelEnumeratorToken);

        Assert.Contains("caught OperationCanceledException", actual, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SequenceCases))]
    public async Task MoveNextAndDisposeExceptionsPropagateAsTheBclDoes(
        bool optimize, bool continueOnCapturedContext, string moveFailure, string disposeFailure)
    {
        string actual = await AssertMatchesBcl(optimize, "Sequence", continueOnCapturedContext, moveFailure, disposeFailure);

        Assert.Contains("dispose", actual, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void ConfiguredAsyncStreamsAreControlled(bool optimize)
    {
        StagedProbe probe = optimize ? _release.Value : _debug.Value;
        string[] rules = probe.Result.Manifest.Transformations
            .Select(transformation => transformation.RuleId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.All(ConfiguredRules, rule => Assert.Contains(rule, rules));
    }

    public static TheoryData<bool> Optimizations => new() { true, false };

    /// <summary>
    /// Runs the probe uninstrumented against the real BCL and rewritten under simulation, and requires the same
    /// observable sequence from both.
    /// </summary>
    private async Task<string> AssertMatchesBcl(bool optimize, string name, params object[] args)
    {
        UninstrumentedProbe bcl = optimize ? _bclRelease.Value : _bclDebug.Value;
        string expected = await (Task<string>)bcl.Method(name).Invoke(null, args)!;
        using var host = new SimulationHost(Start);

        var task = (Task)host.Invoke((optimize ? _release : _debug).Value.Method(name), args)!;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
        string actual = (string)task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal(expected, actual);
        return actual;
    }

    private static TheoryData<bool, bool> Cross(bool[] first, bool[] second)
    {
        var data = new TheoryData<bool, bool>();
        foreach (bool a in first)
        {
            foreach (bool b in second)
            {
                data.Add(a, b);
            }
        }

        return data;
    }

    private static TheoryData<bool, bool, bool, bool> Cross(bool[] first, bool[] second, bool[] third, bool[] fourth)
    {
        var data = new TheoryData<bool, bool, bool, bool>();
        foreach (bool a in first)
        {
            foreach (bool b in second)
            {
                foreach (bool c in third)
                {
                    foreach (bool d in fourth)
                    {
                        data.Add(a, b, c, d);
                    }
                }
            }
        }

        return data;
    }

    public void Dispose() => _fixture.Dispose();
}
