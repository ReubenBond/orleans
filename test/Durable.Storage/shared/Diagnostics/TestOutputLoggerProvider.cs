using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Routes <c>Microsoft.Extensions.Logging</c> output from code under test into the output of the test which created
/// the provider, so a failing test's report carries its traces (docs/spec/verification.md).
/// </summary>
/// <remarks>
/// The provider captures the creating test's output, so entries logged on other threads still reach that test.
/// Entries logged after the test finishes are dropped. The provider is also an <see cref="ILoggerFactory"/>, so code
/// which takes a factory or an <see cref="ILogger{TCategoryName}"/> can use it directly:
/// <c>new TestOutputLoggerProvider().CreateLogger&lt;T&gt;()</c>.
/// </remarks>
internal sealed class TestOutputLoggerProvider(LogLevel minimumLevel = LogLevel.Trace) : ILoggerProvider, ILoggerFactory
{
    private readonly LogLevel _minimumLevel = minimumLevel;
    private readonly ITestOutputHelper? _output = TestContext.Current.TestOutputHelper;
    private readonly long _started = Stopwatch.GetTimestamp();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("The test output logger factory has exactly one provider.");

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{Stopwatch.GetElapsedTime(_started).TotalSeconds:0.000000}s] {level} {category}: {message}");
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        try
        {
            _output?.WriteLine(line);
        }
        catch (InvalidOperationException)
        {
            // The test has finished, and its report is already written.
        }
    }

    private sealed class Logger(TestOutputLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
