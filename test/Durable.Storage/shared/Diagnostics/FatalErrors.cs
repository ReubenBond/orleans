using System.Reflection;
using System.Text;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Testing;
using Xunit.Sdk;
using Xunit.v3;

[assembly: FatalErrors.TestCheckAttribute]
[assembly: AssemblyFixture(typeof(FatalErrors.AssemblyCheck))]

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Records every report to <see cref="FatalErrorHandler"/> in a test host, a failed local storage operation or a defect,
/// in place of the process stop, so that the test or seeded simulation which raised it takes it or fails
/// (conventions.md §14, verification.md §7.3).
/// </summary>
/// <remarks>
/// <para>
/// The test host's builder hook sets the interceptor once. A report is recorded with a sequence number and the test whose
/// execution context raised it, which flows into the code a seeded simulation rewrites, so parallel tests never see each
/// other's reports. The reporting code then rethrows the failure, which <see cref="FatalErrorHandler.IsReported"/> names as
/// reported, and the storage double which injected a storage failure stops, as the process does, so nothing the process
/// does afterwards reaches the device. Within a test, a storage failure's path names the failed node: every node root is
/// unique within a test.
/// </para>
/// <para>
/// A test which injects a failure takes its reports with <see cref="TakeReportedBy"/>, and a seeded simulation's driver
/// takes them after each step and fails the run on any it did not inject; <see cref="TestCheckAttribute"/> fails a test
/// which left a report untaken, and <see cref="AssemblyCheck"/> one reported outside every test. A call through a crashed process's
/// view of storage (<see cref="CrashedProcessException"/>) is not recorded: a stopped process reports nothing.
/// </para>
/// </remarks>
internal static class FatalErrors
{
    private static readonly Action<FatalError> Interceptor = Record;
    private static readonly Lock Gate = new();
    private static readonly List<ReportedFatalError> Records = [];
    private static long _reportedCount;
    private static bool _closed;

    /// <summary>
    /// Gets the number of reports in this process: an owner which starts now takes those numbered from this
    /// count.
    /// </summary>
    internal static long ReportedCount
    {
        get
        {
            lock (Gate)
            {
                return _reportedCount;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the recorder intercepts reports.
    /// </summary>
    internal static bool IsInstalled => ReferenceEquals(FatalErrorHandler.Interceptor, Interceptor);

    /// <summary>
    /// Intercepts every report in this process; the test host's builder hook calls it.
    /// </summary>
    internal static void Install() => FatalErrorHandler.Interceptor = Interceptor;

    /// <summary>
    /// Takes the reports which <paramref name="test"/>, or code outside every test when it is <see langword="null"/>,
    /// raised since the first <paramref name="since"/> reports.
    /// </summary>
    internal static List<ReportedFatalError> TakeReportedBy(ITest? test, long since = 0)
    {
        var taken = new List<ReportedFatalError>();
        lock (Gate)
        {
            _ = Records.RemoveAll(record =>
            {
                if (!RaisedBy(record, test, since))
                {
                    return false;
                }

                taken.Add(record);
                return true;
            });
        }

        return taken;
    }

    /// <summary>
    /// Takes the reports the current test raised since the first <paramref name="since"/> reports.
    /// </summary>
    internal static List<ReportedFatalError> TakeMine(long since = 0) => TakeReportedBy(TestContext.Current.Test, since);

    private static bool RaisedBy(ReportedFatalError record, ITest? test, long since) =>
        record.Sequence >= since && string.Equals(record.TestId, test?.UniqueID, StringComparison.Ordinal);

    /// <summary>
    /// Returns whether <paramref name="exception"/> carries a <see cref="CrashedProcessException"/>, as itself, an inner
    /// exception, or an aggregate's member: a report of it came from a process which already crashed.
    /// </summary>
    internal static bool FromCrashedProcess(Exception exception) =>
        exception is CrashedProcessException
        || (exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(FromCrashedProcess)
            : exception.InnerException is { } inner && FromCrashedProcess(inner));

    private static void Record(FatalError error)
    {
        if (FromCrashedProcess(error.Exception))
        {
            return;
        }

        var test = TestContext.Current.Test;
        ReportedFatalError record;
        bool closed;
        lock (Gate)
        {
            record = new ReportedFatalError(error, test?.TestDisplayName, test?.UniqueID, _reportedCount++);
            closed = _closed;
            if (!closed)
            {
                Records.Add(record);
            }
        }

        if (closed)
        {
            // No owner remains to take it, and a test host which exits cleanly would hide it.
            Environment.FailFast($"A fatal error was reported after the test assembly's check: {error.Describe()}");
        }

        TestDiagnostics.WriteLine("Intercepted: " + error.Describe());
    }

    /// <summary>
    /// Fails a test which left a report untaken once its body has run: outside a test, that report would have
    /// stopped the process.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class TestCheckAttribute : BeforeAfterTestAttribute
    {
        public override void After(MethodInfo methodUnderTest, IXunitTest test)
        {
            if (TakeReportedBy(test) is { Count: > 0 } reported)
            {
                Assert.Fail(Describe(
                    reported.Count == 1
                        ? "1 fatal error was reported in this test and not taken; it would have stopped the process:"
                        : $"{reported.Count} fatal errors were reported in this test and not taken; they would have stopped the process:",
                    reported));
            }
        }
    }

    /// <summary>
    /// Fails the test assembly at its cleanup when the recorder no longer intercepts reports, or with a report still
    /// recorded, and ends recording.
    /// </summary>
    internal sealed class AssemblyCheck : IDisposable
    {
        public void Dispose()
        {
            var installed = IsInstalled;
            List<ReportedFatalError> leftover;
            lock (Gate)
            {
                _closed = true;
                leftover = [.. Records];
                Records.Clear();
            }

            if (!installed)
            {
                Assert.Fail("The fatal error recorder no longer intercepts reports, so a report may have stopped the process.");
            }

            if (leftover.Count > 0)
            {
                Assert.Fail(Describe("Fatal errors were reported outside every test, or after the check of the test which raised them:", leftover));
            }
        }
    }

    private static string Describe(string heading, IEnumerable<ReportedFatalError> reported)
    {
        var report = new StringBuilder(heading);
        foreach (var record in reported)
        {
            _ = report.AppendLine().Append(record.TestName is { } name ? $"In {name}: " : "Outside every test: ").Append(record.Failure.Describe());
        }

        return report.ToString();
    }
}

/// <summary>
/// A report to <see cref="FatalErrorHandler"/> in a test host, with the test whose execution context raised it.
/// </summary>
/// <param name="Failure">The report: a failed local storage operation or a defect.</param>
/// <param name="TestName">The display name of the test which raised it, or <see langword="null"/> outside every test.</param>
/// <param name="TestId">The unique identity of the test which raised it.</param>
/// <param name="Sequence">The report's number among those in the process.</param>
internal sealed record ReportedFatalError(FatalError Failure, string? TestName, string? TestId, long Sequence)
{
    /// <summary>
    /// Gets the reporting site.
    /// </summary>
    public FatalErrorSite Site => Failure.Site;

    /// <summary>
    /// Gets the path a failed storage operation named, or <see langword="null"/> for a defect.
    /// </summary>
    public string? Path => Failure.Path;
}
