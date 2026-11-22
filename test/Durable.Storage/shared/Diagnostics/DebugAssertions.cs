using System.Diagnostics;
using System.Reflection;
using System.Text;
using Orleans.Durable.Storage.Testing;
using Xunit.Sdk;
using Xunit.v3;

[assembly: DebugAssertions.TestCheckAttribute]
[assembly: AssemblyFixture(typeof(DebugAssertions.AssemblyCheck))]

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Records every failed <c>Debug.Assert</c>, <c>Debug.Fail</c>, and <c>Trace.Assert</c> in a test host, so that the
/// seeded simulation, test, or test assembly which raised it fails (docs/spec/verification.md §11.2; the design's
/// rationale is in §14).
/// </summary>
/// <remarks>
/// The test host's builder hook installs a trace listener ahead of every other. A failed assertion throws
/// <see cref="DebugAssertionException"/> at its site, and the store records it with a sequence number and the test whose
/// execution context raised it. An owner takes the records its test raised since the owner started: a seeded simulation
/// while it runs, then <see cref="TestCheckAttribute"/> once the test's body has run, then <see cref="AssemblyCheck"/> at
/// the test assembly's cleanup.
/// </remarks>
internal static class DebugAssertions
{
    private static readonly Listener Hook = new();
    private static readonly Lock Gate = new();
    private static readonly List<DebugAssertionException> Records = [];
    private static bool _closed;

    /// <summary>
    /// Gets the number of assertions which have failed in this process: an owner which starts now takes those numbered
    /// from this count.
    /// </summary>
    internal static long RaisedCount
    {
        get
        {
            lock (Gate)
            {
                return field;
            }
        }

        private set;
    }

    /// <summary>
    /// Gets a value indicating whether the hook is the first trace listener, so that it handles every failed assertion.
    /// </summary>
    internal static bool IsInstalled => Trace.Listeners.Count > 0 && ReferenceEquals(Trace.Listeners[0], Hook);

    /// <summary>
    /// Installs the hook ahead of every other trace listener, once; the test host's builder hook calls it.
    /// </summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (!Trace.Listeners.Contains(Hook))
            {
                Trace.Listeners.Insert(0, Hook);
            }
        }
    }

    /// <summary>
    /// Returns the first recorded assertion which <paramref name="test"/> raised since the first <paramref name="since"/>
    /// assertions, leaving it recorded.
    /// </summary>
    internal static DebugAssertionException? FirstRaisedBy(ITest? test, long since)
    {
        lock (Gate)
        {
            return Records.Find(record => RaisedBy(record, test, since));
        }
    }

    /// <summary>
    /// Takes the recorded assertions which <paramref name="test"/>, or code outside every test when it is
    /// <see langword="null"/>, raised since the first <paramref name="since"/> assertions.
    /// </summary>
    internal static List<DebugAssertionException> TakeRaisedBy(ITest? test, long since = 0) => Take(record => RaisedBy(record, test, since));

    private static bool RaisedBy(DebugAssertionException record, ITest? test, long since) =>
        record.Sequence >= since && string.Equals(record.TestId, test?.UniqueID, StringComparison.Ordinal);

    private static List<DebugAssertionException> Take(Predicate<DebugAssertionException> match)
    {
        lock (Gate)
        {
            var taken = Records.FindAll(match);
            _ = Records.RemoveAll(match);
            return taken;
        }
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// Fails a test which raised a failed assertion, once the test's body has run, whether or not the code under test
    /// caught its exception.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class TestCheckAttribute : BeforeAfterTestAttribute
    {
        public override void After(MethodInfo methodUnderTest, IXunitTest test)
        {
            if (TakeRaisedBy(test) is { Count: > 0 } recorded)
            {
                throw new RecordedDebugAssertionsException(
                    recorded.Count == 1
                        ? "1 debug assertion was recorded in this test, whether or not it propagated."
                        : Invariant($"{recorded.Count} debug assertions were recorded in this test, whether or not they propagated."),
                    recorded);
            }
        }
    }

    /// <summary>
    /// Fails the test assembly at its cleanup when the hook is no longer the first trace listener, or with every failed
    /// assertion still recorded, and ends recording.
    /// </summary>
    internal sealed class AssemblyCheck : IDisposable
    {
        public void Dispose()
        {
            var installed = IsInstalled;
            var leftover = Close();
            if (!installed || leftover.Count > 0)
            {
                Assert.Fail(Describe(installed, leftover));
            }
        }

        /// <summary>
        /// Describes why the assembly check fails.
        /// </summary>
        internal static string Describe(bool installed, IReadOnlyList<DebugAssertionException> leftover)
        {
            var report = new StringBuilder();
            if (!installed)
            {
                _ = report.Append("The debug assertion hook is no longer the first trace listener, so a failed assertion may have gone unrecorded.");
            }

            if (leftover.Count > 0)
            {
                _ = report.Append(report.Length > 0 ? " " : string.Empty)
                    .Append(leftover.Count == 1
                        ? "1 debug assertion failed outside every test, or after the check of the test which raised it:"
                        : Invariant($"{leftover.Count} debug assertions failed outside every test, or after the check of the tests which raised them:"));
                foreach (var failure in leftover)
                {
                    _ = report.AppendLine().Append(failure.TestName is { } name ? Invariant($"In {name}: ") : "Outside every test: ").Append(failure);
                }
            }

            return report.ToString();
        }

        /// <summary>
        /// Takes every recorded assertion and ends recording: an assertion which fails afterwards ends the process.
        /// </summary>
        private static List<DebugAssertionException> Close()
        {
            lock (Gate)
            {
                _closed = true;
                var taken = new List<DebugAssertionException>(Records);
                Records.Clear();
                return taken;
            }
        }
    }

    private sealed class Listener : TraceListener
    {
        public Listener()
            : base("Clog debug assertions")
        {
        }

        public override void Fail(string? message) => throw RecordFailure(message, detailMessage: null);

        public override void Fail(string? message, string? detailMessage) => throw RecordFailure(message, detailMessage);

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
        }

        private static DebugAssertionException RecordFailure(string? message, string? detailMessage)
        {
            var (site, siteKey) = Site();
            return Add(Describe(message, detailMessage), site, siteKey);
        }

        /// <summary>
        /// Records a failed assertion with the current test, and returns its exception.
        /// </summary>
        private static DebugAssertionException Add(string message, string site, string siteKey)
        {
            var test = TestContext.Current.Test;
            DebugAssertionException failure;
            bool closed;
            lock (Gate)
            {
                failure = new DebugAssertionException(message, site, siteKey, test?.TestDisplayName, test?.UniqueID, RaisedCount++);
                closed = _closed;
                if (!closed)
                {
                    Records.Add(failure);
                }
            }

            if (closed)
            {
                // No owner remains to report it, and a test host which exits cleanly would hide it.
                Environment.FailFast(Invariant($"A debug assertion failed after the test assembly's check: {failure}"));
            }

            TestDiagnostics.WriteLine(failure.ToString());
            return failure;
        }

        private static string Describe(string? message, string? detailMessage) => (message, detailMessage) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => Invariant($"Debug assertion failed: {message} {detailMessage}"),
            ({ Length: > 0 }, _) => Invariant($"Debug assertion failed: {message}"),
            (_, { Length: > 0 }) => Invariant($"Debug assertion failed: {detailMessage}"),
            _ => "Debug assertion failed.",
        };

        /// <summary>
        /// Returns the stack of the assertion's site, beginning with the code which asserted, below this listener and
        /// the trace machinery, and the site's key: its first frame's method and IL offset.
        /// </summary>
        private static (string Site, string Key) Site()
        {
            var frames = new StackTrace(fNeedFileInfo: true).GetFrames();
            var index = Array.FindIndex(frames, static frame => frame.GetMethod()?.DeclaringType is { } type
                && type != typeof(Listener)
                && !string.Equals(type.Namespace, "System.Diagnostics", StringComparison.Ordinal));
            if (index < 0)
            {
                return (new StackTrace(frames).ToString(), "unknown site");
            }

            var method = frames[index].GetMethod()!;
            return (
                new StackTrace(frames[index..]).ToString(),
                Invariant($"{method.DeclaringType!.FullName}.{method.Name}+IL_{frames[index].GetILOffset():x4}"));
        }
    }
}

/// <summary>
/// A failed <c>Debug.Assert</c>, <c>Debug.Fail</c>, or <c>Trace.Assert</c> in a test host, thrown at its site
/// (docs/spec/verification.md §11.2).
/// </summary>
public sealed class DebugAssertionException : Exception
{
    private readonly string? _site;

    public DebugAssertionException()
    {
    }

    public DebugAssertionException(string message)
        : base(message)
    {
    }

    public DebugAssertionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal DebugAssertionException(string message, string site, string siteKey, string? testName, string? testId, long sequence, Exception? context = null)
        : base(message, context)
    {
        _site = site;
        SiteKey = siteKey;
        TestName = testName;
        TestId = testId;
        Sequence = sequence;
    }

    /// <summary>
    /// Gets the display name of the test whose execution context raised the assertion, or <see langword="null"/> when no
    /// test's did.
    /// </summary>
    public string? TestName { get; }

    /// <summary>
    /// Gets the assertion's site as the first frame's method and IL offset, which tells apart sites whose messages are
    /// the same.
    /// </summary>
    public string? SiteKey { get; }

    /// <summary>
    /// Gets the stack of the assertion's site, wherever the exception was later rethrown or caught.
    /// </summary>
    public override string? StackTrace => _site ?? base.StackTrace;

    /// <summary>
    /// Gets the unique identity of the test which raised the assertion.
    /// </summary>
    internal string? TestId { get; }

    /// <summary>
    /// Gets the assertion's number among those which failed in the process.
    /// </summary>
    internal long Sequence { get; }

    /// <summary>
    /// Returns this assertion carrying <paramref name="context"/>, the failure which followed it, as its inner exception;
    /// the assertion itself when there is none or it is that failure.
    /// </summary>
    internal DebugAssertionException WithContext(Exception? context) =>
        context is null || ReferenceEquals(context, this)
            ? this
            : new DebugAssertionException(Message, _site ?? string.Empty, SiteKey ?? string.Empty, TestName, TestId, Sequence, context);
}

/// <summary>
/// Fails a test with the debug assertions recorded in it, whether or not they propagated, so that one which also failed
/// the test directly reads as a repeat (docs/spec/verification.md §11.2).
/// </summary>
public sealed class RecordedDebugAssertionsException : AggregateException
{
    public RecordedDebugAssertionsException()
    {
    }

    public RecordedDebugAssertionsException(string message)
        : base(message)
    {
    }

    public RecordedDebugAssertionsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal RecordedDebugAssertionsException(string message, IEnumerable<DebugAssertionException> recorded)
        : base(message, recorded)
    {
    }
}
