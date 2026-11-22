#define DEBUG

// Debug's calls compile only where DEBUG is defined, so this file defines it: its tests fail assertions through Debug
// itself in every configuration, the Release tests' among them.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Xunit.v3;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// The debug assertion hook's guarantees: a failed assertion throws at its site with its message, and each record fails
/// its seeded run, its test, or its test assembly, whether or not the code under test caught its exception
/// (test/Durable.Storage/docs/spec/verification.md §11.2).
/// </summary>
public sealed class DebugAssertionsTests
{
    private const string Caught = "caught by the code under test";

    /// <summary>
    /// A failed <c>Debug.Assert</c> throws at its site with its message and detail, names its test, and keys its site by
    /// the asserting method and IL offset.
    /// </summary>
    [Fact]
    public void AFailedAssertionThrowsAtItsSiteWithItsMessage()
    {
        var failure = Assert.Throws<DebugAssertionException>(static () => AssertEmpty([1], "the message", "the detail"));

        Assert.Equal("Debug assertion failed: the message the detail", failure.Message);
        Assert.Equal(TestContext.Current.Test!.TestDisplayName, failure.TestName);
        Assert.Contains(nameof(AssertEmpty), failure.StackTrace!.Split(Environment.NewLine)[0], StringComparison.Ordinal);
        Assert.StartsWith($"{typeof(DebugAssertionsTests).FullName}.{nameof(AssertEmpty)}+IL_", failure.SiteKey, StringComparison.Ordinal);
        Assert.Same(failure, Assert.Single(TakeMine()));
    }

    /// <summary>
    /// Sites which share a message have different keys, and one site keeps its key, so minimization tells them apart.
    /// </summary>
    [Fact]
    public void SitesWhichShareAMessageHaveDifferentKeys()
    {
        var first = Assert.Throws<DebugAssertionException>(static () => FailHere(Caught));
        var again = Assert.Throws<DebugAssertionException>(static () => FailHere(Caught));
        var other = Assert.Throws<DebugAssertionException>(static () => FailThere(Caught));

        Assert.Equal(first.SiteKey, again.SiteKey);
        Assert.NotEqual(first.SiteKey, other.SiteKey);
        Assert.Equal(first.Message, other.Message);
        Assert.Equal(3, TakeMine().Count);
    }

    /// <summary>
    /// An assertion which propagates fails its test directly, and the test's check reports it again as recorded, in a
    /// wrapper which says so; the check leaves nothing recorded.
    /// </summary>
    [Fact]
    public void AnUncaughtAssertionFailsItsTestAndIsReportedAgainAsRecorded()
    {
        var failure = Assert.Throws<DebugAssertionException>(static () => FailHere("not caught"));

        var recorded = Assert.Throws<RecordedDebugAssertionsException>(CheckThisTest);

        Assert.StartsWith("1 debug assertion was recorded in this test, whether or not it propagated.", recorded.Message, StringComparison.Ordinal);
        Assert.Same(failure, Assert.Single(recorded.InnerExceptions));
        Assert.Empty(TakeMine());
    }

    /// <summary>
    /// <c>Debug.Fail</c> and <c>Trace.Assert</c> reach the hook as <c>Debug.Assert</c> does, and the test's check reports
    /// every assertion the test raised.
    /// </summary>
    [Fact]
    public void DebugFailAndTraceAssertAreRecordedToo()
    {
        Assert.Equal("Debug assertion failed: failed", Assert.Throws<DebugAssertionException>(static () => Debug.Fail("failed")).Message);
        Assert.Equal("Debug assertion failed: false", Assert.Throws<DebugAssertionException>(static () => Trace.Assert(false)).Message);

        var recorded = Assert.Throws<RecordedDebugAssertionsException>(CheckThisTest);

        Assert.StartsWith("2 debug assertions were recorded in this test, whether or not they propagated.", recorded.Message, StringComparison.Ordinal);
        Assert.Equal(2, recorded.InnerExceptions.Count);
        Assert.Empty(TakeMine());
    }

    /// <summary>
    /// An assertion which the code under test catches, in work the test started, fails the test once its body has run,
    /// with its message and its site.
    /// </summary>
    [Fact]
    public async Task ACaughtAssertionFailsItsTestOnceItsBodyHasRun()
    {
        Assert.True(await Task.Run(static () => CatchAnAssertion(Caught), TestContext.Current.CancellationToken));

        var recorded = Assert.Throws<RecordedDebugAssertionsException>(CheckThisTest);

        var failure = Assert.IsType<DebugAssertionException>(Assert.Single(recorded.InnerExceptions));
        Assert.Equal($"Debug assertion failed: {Caught}", failure.Message);
        Assert.Contains(nameof(FailHere), failure.StackTrace!.Split(Environment.NewLine)[0], StringComparison.Ordinal);
        Assert.Empty(TakeMine());
    }

    /// <summary>
    /// A seeded run whose code under test catches an assertion fails with it, and reports its seed and history.
    /// </summary>
    [Fact]
    public async Task ASeededRunWhoseCodeCatchesAnAssertionFailsWithItsSeed()
    {
        var failure = await Assert.ThrowsAsync<DebugAssertionException>(static () => SeededSimulation.RunAsync(5, static run =>
        {
            run.Record("asserting");
            Assert.True(CatchAnAssertion(Caught));
            return Task.CompletedTask;
        }));

        Assert.Equal($"Debug assertion failed: {Caught}", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal("asserting", Assert.Single(await ReadHistoryAsync(5)));
        Assert.Empty(TakeMine());
    }

    /// <summary>
    /// A seeded run whose assertion propagates fails with that assertion alone.
    /// </summary>
    [Fact]
    public async Task ASeededRunWhoseAssertionPropagatesFailsWithItOnce()
    {
        Exception? raised = null;
        var failure = await Assert.ThrowsAsync<DebugAssertionException>(() => SeededSimulation.RunAsync(6, run =>
        {
            run.Record("asserting");
            raised = Record.Exception(static () => FailHere("not caught"));
            throw raised!;
        }));

        Assert.Same(raised, failure);
        Assert.Null(failure.InnerException);
        Assert.Equal("asserting", Assert.Single(await ReadHistoryAsync(6)));
        Assert.Empty(TakeMine());
    }

    /// <summary>
    /// A seeded run whose code catches an assertion and then fails differently fails with the assertion, which carries
    /// the later failure as its inner exception.
    /// </summary>
    [Fact]
    public async Task ACaughtAssertionTakesPrecedenceOverTheFailureWhichFollowsIt()
    {
        var failure = await Assert.ThrowsAsync<DebugAssertionException>(static () => SeededSimulation.RunAsync(7, static run =>
        {
            run.Record("asserting");
            Assert.True(CatchAnAssertion(Caught));
            throw new InvalidDataException("The scenario observed a wrong answer.");
        }));

        Assert.Equal($"Debug assertion failed: {Caught}", failure.Message);
        Assert.Equal("The scenario observed a wrong answer.", Assert.IsType<InvalidDataException>(failure.InnerException).Message);
        Assert.Equal("asserting", Assert.Single(await ReadHistoryAsync(7)));
        Assert.Empty(TakeMine());
    }

    /// <summary>
    /// An assertion raised outside every test's execution context is no test's, and the assembly check reports it.
    /// </summary>
    [Fact]
    public async Task AnAssertionOutsideEveryTestIsLeftForTheAssemblyCheck()
    {
        var since = DebugAssertions.RaisedCount;
        Task<bool> caught;
        using (ExecutionContext.SuppressFlow())
        {
            caught = Task.Run(static () => CatchAnAssertion(Caught));
        }

        Assert.True(await caught);
        Assert.Empty(TakeMine());

        // Only this test's own record: the assembly check proper runs at the assembly's cleanup.
        var outside = DebugAssertions.TakeRaisedBy(test: null, since);
        Assert.Null(Assert.Single(outside).TestName);
        var report = DebugAssertions.AssemblyCheck.Describe(installed: true, outside);
        Assert.StartsWith("1 debug assertion failed outside every test, or after the check of the test which raised it:", report, StringComparison.Ordinal);
        Assert.Contains($"Outside every test: {typeof(DebugAssertionException).FullName}: Debug assertion failed: {Caught}", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hook is the first trace listener and installs once; the assembly check fails when it is no longer first.
    /// </summary>
    [Fact]
    public void TheHookIsTheFirstListenerAndInstallsOnce()
    {
        var listeners = Trace.Listeners.Count;

        DebugAssertions.Install();

        Assert.True(DebugAssertions.IsInstalled);
        Assert.Equal(listeners, Trace.Listeners.Count);
        Assert.StartsWith(
            "The debug assertion hook is no longer the first trace listener",
            DebugAssertions.AssemblyCheck.Describe(installed: false, []),
            StringComparison.Ordinal);
    }

    private static List<DebugAssertionException> TakeMine() => DebugAssertions.TakeRaisedBy(TestContext.Current.Test);

    private static void CheckThisTest()
    {
        var test = Assert.IsAssignableFrom<IXunitTest>(TestContext.Current.Test);
        new DebugAssertions.TestCheckAttribute().After(test.TestMethod.Method, test);
    }

    private static async Task<string[]> ReadHistoryAsync(int seed) =>
        await File.ReadAllLinesAsync(
            TestDiagnostics.PathFor(TestContext.Current.Test!.TestDisplayName, string.Create(CultureInfo.InvariantCulture, $".seed-{seed}.history.log")),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Fails an assertion and catches its exception, as code under test which counts a failure toward an outcome does.
    /// </summary>
    /// <returns>Whether the assertion's exception was caught.</returns>
    private static bool CatchAnAssertion(string message)
    {
        try
        {
            FailHere(message);
            return false;
        }
        catch (DebugAssertionException)
        {
            return true;
        }
    }

    // Each helper is never inlined, so it is the first frame of its assertion's site.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertEmpty(int[] values, string message, string detailMessage) => Debug.Assert(values.Length == 0, message, detailMessage);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailHere(string message) => Debug.Fail(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailThere(string message) => Debug.Fail(message);
}
