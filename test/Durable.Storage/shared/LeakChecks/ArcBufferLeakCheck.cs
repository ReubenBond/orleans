using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Establishes that a test returned every <see cref="ArcBuffer"/> page it rented: every page rented from the pool, or
/// wrapped around an array, under the check was released by its last owner (BUF-I1, BUF-I2;
/// docs/spec/verification.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// The check is the <see cref="ArcBufferPageAccounting"/> current in the execution context which starts it and in the
/// work that context starts, and it counts each page's return from whichever thread releases it. A test class starts
/// it in a field initializer, which xUnit runs in the test's execution context, and disposes it after the components
/// the test owns:
/// <code>
/// private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
/// public void Dispose() => _leaks.Dispose();
/// </code>
/// Work which does not inherit the test's execution context, such as a server's requests, enters the check through
/// <see cref="Run{T}"/>. A property test checks each case in its own nested check, so a leak fails the case which
/// caused it, which is shrunk and reports its seed:
/// <code>
/// Gen.Int.Sample(value => _leaks.InScope(() => CheckCase(value)));
/// </code>
/// </para>
/// <para>
/// A failing check reports how many pages are outstanding; with <c>CLOG_TEST_LEAK_TRACES=1</c> it also reports where
/// each was rented, grouped by call site.
/// </para>
/// </remarks>
internal sealed class ArcBufferLeakCheck : ArcBufferPageAccounting, IDisposable
{
    internal const string TraceVariable = "CLOG_TEST_LEAK_TRACES";

    // The call sites a failure reports, the most frequent first.
    private const int ReportedSites = 10;

    private static readonly bool TracesFromEnvironment = Environment.GetEnvironmentVariable(TraceVariable) is "1" or "true";

    private readonly ArcBufferLeakCheck? _parent;
    private readonly ArcBufferPageAccounting? _previous;
    private readonly bool _assertsAtDisposal;
    private readonly ConcurrentDictionary<ArcBufferPage, int>? _rentals;
    private int _rented;
    private int _returned;
    private bool _disposed;

    private ArcBufferLeakCheck(ArcBufferLeakCheck? parent, bool tracesRentals, bool assertsAtDisposal)
    {
        _parent = parent;
        _assertsAtDisposal = assertsAtDisposal;
        _rentals = tracesRentals ? new() : null;
        _previous = Current;
        Current = this;
    }

    /// <summary>
    /// Gets a value indicating whether the check records where each outstanding page was rented.
    /// </summary>
    internal bool TracesRentals => _rentals is not null;

    /// <summary>
    /// Gets the number of pages rented under the check.
    /// </summary>
    internal int Rented => Volatile.Read(ref _rented);

    /// <summary>
    /// Gets the number of pages rented under the check which have not returned.
    /// </summary>
    internal int Outstanding => Rented - Volatile.Read(ref _returned);

    /// <summary>
    /// Starts counting the pages rented in the current execution context and in the work it starts, until the check is
    /// disposed, which restores the accounting it replaced.
    /// </summary>
    /// <param name="tracesRentals">Whether to record where pages are rented; by default, <c>CLOG_TEST_LEAK_TRACES</c>.</param>
    internal static ArcBufferLeakCheck Start(bool? tracesRentals = null) =>
        new(parent: null, tracesRentals ?? TracesFromEnvironment, assertsAtDisposal: true);

    /// <summary>
    /// Starts counting the pages rented in the current execution context and in the work it starts, for a caller which
    /// reports leaks itself through <see cref="DescribeLeaks"/>: <c>SeededSimulation</c> starts one in a scenario's
    /// context and runs it as a teardown check. Disposal restores the accounting it replaced without asserting.
    /// </summary>
    internal static ArcBufferLeakCheck StartUnasserted() =>
        new(parent: null, TracesFromEnvironment, assertsAtDisposal: false);

    /// <summary>
    /// Runs <paramref name="body"/> with <paramref name="accounting"/> counting the pages it rents and the work it starts,
    /// for work which does not inherit the execution context of the test which owns the accounting.
    /// </summary>
    internal static T Run<T>(ArcBufferPageAccounting accounting, Func<T> body)
    {
        var previous = Current;
        Current = accounting;
        try
        {
            return body();
        }
        finally
        {
            Current = previous;
        }
    }

    /// <summary>
    /// Starts a nested check, current until it is disposed, which counts the pages rented in its scope; this check
    /// counts them too. Disposing the scope restores this check without asserting, so a failure inside the scope is
    /// reported as itself; the scope asserts through <see cref="AssertNoLeaks"/>.
    /// </summary>
    internal ArcBufferLeakCheck Scope() => new(this, TracesRentals, assertsAtDisposal: false);

    /// <summary>
    /// Runs one case of a property in its own <see cref="Scope"/> and, once the case has returned and released what it
    /// owns, fails unless every page rented in the scope has returned. A case which throws reports its own failure.
    /// </summary>
    internal void InScope(Action body)
    {
        var scope = Scope();
        try
        {
            body();
        }
        finally
        {
            scope.Dispose();
        }

        scope.AssertNoLeaks();
    }

    /// <inheritdoc cref="InScope"/>
    internal async Task InScopeAsync(Func<Task> body)
    {
        var scope = Scope();
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            scope.Dispose();
        }

        scope.AssertNoLeaks();
    }

    /// <summary>
    /// Fails unless every page rented under the check has returned.
    /// </summary>
    internal void AssertNoLeaks()
    {
        if (DescribeLeaks() is { } leaks)
        {
            Assert.Fail(leaks);
        }
    }

    /// <summary>
    /// Describes the pages rented under the check which have not returned, or returns <see langword="null"/> when every
    /// page has.
    /// </summary>
    internal string? DescribeLeaks()
    {
        var outstanding = Outstanding;
        if (outstanding == 0)
        {
            return null;
        }

        var message = new StringBuilder(string.Create(
            CultureInfo.InvariantCulture,
            $"{outstanding} of the {Rented} ArcBuffer pages rented under the test did not return (BUF-I1, BUF-I2)."));
        if (_rentals is not { } rentals)
        {
            _ = message.Append(CultureInfo.InvariantCulture, $" Set {TraceVariable}=1 to report where each was rented.");
        }
        else
        {
            var sites = rentals.Values.CountBy(static site => site).OrderByDescending(static site => site.Value).ToList();
            foreach (var (site, count) in sites.Take(ReportedSites))
            {
                _ = message.AppendLine()
                    .Append(CultureInfo.InvariantCulture, $"{count} rented at:")
                    .AppendLine()
                    .Append(PageRentalSites.Describe(site));
            }

            if (sites.Count > ReportedSites)
            {
                _ = message.AppendLine().Append(CultureInfo.InvariantCulture, $"... and {sites.Count - ReportedSites} more call sites.");
            }
        }

        return message.ToString();
    }

    /// <summary>
    /// Restores the accounting the check replaced. A check from <see cref="Start"/> then asserts that every page rented
    /// under it has returned, unless the test has already failed: a failure leaves whatever its exception interrupted,
    /// and the test reports that failure instead.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Current = _previous;
        if (!_assertsAtDisposal)
        {
            return;
        }

        if (TestContext.Current.TestState?.Result == TestResult.Failed)
        {
            TestDiagnostics.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Leak check skipped after the test failed: {Outstanding} of {Rented} ArcBuffer pages are outstanding."));
            return;
        }

        AssertNoLeaks();
    }

    internal override void OnRented(ArcBufferPage page) => Record(page, _rentals is null ? PageRentalSites.Unrecorded : PageRentalSites.Capture());

    internal override void OnReturned(ArcBufferPage page)
    {
        _ = _rentals?.TryRemove(page, out _);
        _ = Interlocked.Increment(ref _returned);
        _parent?.OnReturned(page);
    }

    private void Record(ArcBufferPage page, int site)
    {
        _ = Interlocked.Increment(ref _rented);
        _ = _rentals?.TryAdd(page, site);
        _parent?.Record(page, site);
    }
}
