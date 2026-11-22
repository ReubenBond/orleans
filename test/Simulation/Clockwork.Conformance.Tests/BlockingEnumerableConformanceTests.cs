using System.Reflection;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for <c>TaskAsyncEnumerableExtensions.ToBlockingEnumerable</c> in rewritten code. The BCL
/// blocks the calling thread until each <c>MoveNextAsync</c> and the final <c>DisposeAsync</c> complete, which inside a
/// simulation blocks the one logical thread that would complete them. The controlled enumerable pumps the simulation
/// instead. Every probe runs both rewritten under simulation and uninstrumented against the real BCL, and the two must
/// observe the same iteration, cancellation, disposal, and exception sequence.
/// </summary>
public sealed class BlockingEnumerableConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class BlockingEnumerableProbe {
            public static string Enumerate(int count, int throwAt, int breakAfter, bool delay)
            {
                var log = new List<string>();
                try
                {
                    foreach (int item in Numbers(log, count, throwAt, delay).ToBlockingEnumerable())
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

            // The token reaches the iterator through GetAsyncEnumerator, as WithCancellation's does.
            public static string Cancel(int cancelAfter)
            {
                var log = new List<string>();
                using var source = new CancellationTokenSource();
                try
                {
                    foreach (int item in Numbers(log, 4, throwAt: -1, delay: false).ToBlockingEnumerable(source.Token))
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

            // Enumeration is deferred: neither creating the enumerable nor an enumerator that never moves starts it.
            public static string Deferred()
            {
                var log = new List<string>();
                IEnumerable<int> blocking = Numbers(log, 3, throwAt: -1, delay: false).ToBlockingEnumerable();
                log.Add("created");
                using (IEnumerator<int> unused = blocking.GetEnumerator())
                {
                    log.Add("enumerator");
                }

                log.Add("sum " + blocking.Sum());
                log.Add("first " + blocking.First());
                return string.Join("|", log);
            }

            // A hand-written enumerator whose MoveNextAsync or DisposeAsync throws synchronously or faults.
            public static string Sequence(string moveFailure, string disposeFailure)
            {
                var log = new List<string>();
                try
                {
                    foreach (int item in new Faulting(log, moveFailure, disposeFailure).ToBlockingEnumerable())
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
                bool delay,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                log.Add("start cancelable=" + cancellationToken.CanBeCanceled);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (delay)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(5));
                        }
                        else
                        {
                            await Task.Yield();
                        }

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
                    await Task.Yield();
                    log.Add("iterator finally");
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

    private const string ProbeType = "Conf.BlockingEnumerableProbe";

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _release;
    private readonly Lazy<StagedProbe> _debug;
    private readonly Lazy<UninstrumentedProbe> _bclRelease;
    private readonly Lazy<UninstrumentedProbe> _bclDebug;

    public BlockingEnumerableConformanceTests()
    {
        _release = new(() => _fixture.StageControlledTasks("Conf.BlockingEnumerableRel", ProbeType, Source, optimize: true));
        _debug = new(() => _fixture.StageControlledTasks("Conf.BlockingEnumerableDbg", ProbeType, Source, optimize: false));
        _bclRelease = new(() => _fixture.CompileUninstrumented("Conf.BlockingEnumerableBclRel", ProbeType, Source, optimize: true));
        _bclDebug = new(() => _fixture.CompileUninstrumented("Conf.BlockingEnumerableBclDbg", ProbeType, Source, optimize: false));
    }

    public static TheoryData<bool, int, int, int, bool> EnumerateCases
    {
        get
        {
            var data = new TheoryData<bool, int, int, int, bool>();
            foreach (bool optimize in new[] { true, false })
            {
                data.Add(optimize, 3, -1, -1, false);
                data.Add(optimize, 0, -1, -1, false);
                data.Add(optimize, 3, 1, -1, false);
                data.Add(optimize, 3, -1, 0, false);
                data.Add(optimize, 3, -1, -1, true);
            }

            return data;
        }
    }

    public static TheoryData<bool, int> CancelCases => new() { { true, 1 }, { true, -1 }, { false, 1 }, { false, -1 } };

    public static TheoryData<bool, string, string> SequenceCases
    {
        get
        {
            var data = new TheoryData<bool, string, string>();
            foreach (bool optimize in new[] { true, false })
            {
                data.Add(optimize, "none", "none");
                data.Add(optimize, "sync", "none");
                data.Add(optimize, "fault", "none");
                data.Add(optimize, "none", "sync");
                data.Add(optimize, "none", "fault");
                data.Add(optimize, "fault", "fault");
            }

            return data;
        }
    }

    public static TheoryData<bool> Optimizations => new() { true, false };

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(EnumerateCases))]
    public async Task EnumeratesBreaksAndRethrowsAsTheBclDoes(bool optimize, int count, int throwAt, int breakAfter, bool delay)
    {
        string actual = await AssertMatchesBcl(TestContext.Current.CancellationToken, optimize, "Enumerate", count, throwAt, breakAfter, delay);

        // The source enumerator is disposed on completion, early break, and fault alike.
        Assert.Contains("iterator finally", actual, StringComparison.Ordinal);
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(CancelCases))]
    public async Task TokenReachesTheIteratorAsTheBclDoes(bool optimize, int cancelAfter)
    {
        string actual = await AssertMatchesBcl(TestContext.Current.CancellationToken, optimize, "Cancel", cancelAfter);

        Assert.StartsWith("start cancelable=True|", actual, StringComparison.Ordinal);
        if (cancelAfter >= 0)
        {
            Assert.EndsWith("|iterator finally|caught OperationCanceledException token=True", actual, StringComparison.Ordinal);
        }
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(Optimizations))]
    public async Task EnumerationIsDeferredAsTheBclDoes(bool optimize)
    {
        string actual = await AssertMatchesBcl(TestContext.Current.CancellationToken, optimize, "Deferred");

        Assert.StartsWith("created|enumerator|start cancelable=False|", actual, StringComparison.Ordinal);
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(SequenceCases))]
    public async Task MoveNextAndDisposeExceptionsPropagateAsTheBclDoes(bool optimize, string moveFailure, string disposeFailure)
    {
        string actual = await AssertMatchesBcl(TestContext.Current.CancellationToken, optimize, "Sequence", moveFailure, disposeFailure);

        Assert.Contains("dispose", actual, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Optimizations))]
    public void ToBlockingEnumerableIsControlled(bool optimize)
    {
        StagedProbe probe = optimize ? _release.Value : _debug.Value;

        Assert.Contains(
            probe.Result.Manifest.Transformations,
            transformation => transformation.RuleId == "clockwork.tasks.asyncenumerable.toblockingenumerable");
    }

    /// <summary>
    /// Runs the probe uninstrumented against the real BCL and rewritten under simulation, and requires the same
    /// observable sequence from both. A blocking wait inside the simulation would hang the calling thread forever, so
    /// the simulation runs off the test thread and the theory's timeout reports the hang.
    /// </summary>
    private async Task<string> AssertMatchesBcl(CancellationToken cancellationToken, bool optimize, string name, params object[] args)
    {
        UninstrumentedProbe bcl = optimize ? _bclRelease.Value : _bclDebug.Value;
        string expected = (string)bcl.Method(name).Invoke(null, args)!;
        MethodInfo staged = (optimize ? _release : _debug).Value.Method(name);

        string actual = await Task.Run(
            () =>
            {
                using var host = new SimulationHost(Start);
                return (string)host.Invoke(staged, args)!;
            },
            cancellationToken);

        Assert.Equal(expected, actual);
        return actual;
    }

    public void Dispose() => _fixture.Dispose();
}
