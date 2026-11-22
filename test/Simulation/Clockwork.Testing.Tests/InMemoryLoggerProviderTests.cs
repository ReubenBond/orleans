using System.Reflection;
using System.Text;
using Orleans.Testing.Simulation.Testing;
using Microsoft.Extensions.Logging;

namespace Orleans.Testing.Simulation.Testing.Tests;

public sealed class InMemoryLoggerProviderTests
{
    [Fact]
    public void LogEntryPreservesConstructorValuesAndValueSemantics()
    {
        var timestamp = new DateTimeOffset(2026, 7, 28, 12, 34, 56, TimeSpan.Zero);
        var eventId = new EventId(42, "meaningful-event");
        var exception = new InvalidOperationException("known failure");
        var entry = new LogEntry(
            timestamp,
            LogLevel.Warning,
            "Clockwork.Tests.Category",
            eventId,
            "captured message",
            exception);

        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Equal("Clockwork.Tests.Category", entry.Category);
        Assert.Equal(eventId, entry.EventId);
        Assert.Equal("captured message", entry.Message);
        Assert.Same(exception, entry.Exception);

        LogEntry copy = entry with { };
        LogEntry changed = entry with { Message = "different message" };

        Assert.Equal(entry, copy);
        Assert.Equal(entry.GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(entry, changed);
    }

    [Fact]
    public void BufferLogCapturesFormattedStateMetadataAndException()
    {
        var timestamp = new DateTimeOffset(2026, 7, 28, 13, 14, 15, TimeSpan.Zero);
        var buffer = new InMemoryLogBuffer(new MutableTimeProvider(timestamp));
        var state = new LogState("job-17", 3);
        var exception = new InvalidOperationException("operation failed");
        var eventId = new EventId(17, "operation");
        LogState? receivedState = null;
        Exception? receivedException = null;

        buffer.Log(
            LogLevel.Error,
            eventId,
            state,
            exception,
            (value, error) =>
            {
                receivedState = value;
                receivedException = error;
                return $"{value.Name} failed after {value.Attempts} attempts";
            },
            "Clockwork.Worker");

        LogEntry entry = Assert.Single(buffer.AllEntries);
        Assert.Same(state, receivedState);
        Assert.Same(exception, receivedException);
        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Equal(eventId, entry.EventId);
        Assert.Equal("Clockwork.Worker", entry.Category);
        Assert.Equal("job-17 failed after 3 attempts", entry.Message);
        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public void BufferLogUsesTimeProviderAtEachLogCall()
    {
        var firstTimestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var secondTimestamp = new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(firstTimestamp);
        var buffer = new InMemoryLogBuffer(timeProvider);

        AddEntry(buffer, LogLevel.Information, "first");
        timeProvider.UtcNow = secondTimestamp;
        AddEntry(buffer, LogLevel.Warning, "second");

        LogEntry[] entries = [.. buffer.AllEntries];
        Assert.Equal(2, entries.Length);
        Assert.Equal(firstTimestamp, entries[0].Timestamp);
        Assert.Equal("first", entries[0].Message);
        Assert.Equal(secondTimestamp, entries[1].Timestamp);
        Assert.Equal("second", entries[1].Message);
    }

    [Fact]
    public void BufferLogWithNullFormatterThrowsWithoutEnqueuing()
    {
        var buffer = new InMemoryLogBuffer();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => buffer.Log(
                LogLevel.Information,
                new EventId(1),
                "state",
                exception: null,
                formatter: null!,
                category: "category"));

        Assert.Equal("formatter", exception.ParamName);
        Assert.Empty(buffer.AllEntries);
    }

    [Fact]
    public void BufferLogPropagatesFormatterFailureWithoutEnqueuing()
    {
        var buffer = new InMemoryLogBuffer();
        var formatterFailure = new FormatException("formatter failed");

        FormatException thrown = Assert.Throws<FormatException>(
            () => buffer.Log(
                LogLevel.Warning,
                new EventId(2),
                "state",
                exception: null,
                (_, _) => throw formatterFailure,
                "category"));

        Assert.Same(formatterFailure, thrown);
        Assert.Empty(buffer.AllEntries);
    }

    [Fact]
    public void GetEntriesIncludesThresholdAndHigherLevelsInInsertionOrder()
    {
        var buffer = new InMemoryLogBuffer();
        AddEntry(buffer, LogLevel.Debug, "below");
        AddEntry(buffer, LogLevel.Warning, "at threshold");
        AddEntry(buffer, LogLevel.Information, "also below");
        AddEntry(buffer, LogLevel.Error, "above");
        AddEntry(buffer, LogLevel.Critical, "highest");

        LogEntry[] entries = [.. buffer.GetEntries(LogLevel.Warning)];

        Assert.Equal(
            [LogLevel.Warning, LogLevel.Error, LogLevel.Critical],
            entries.Select(entry => entry.LogLevel));
        Assert.Equal(
            ["at threshold", "above", "highest"],
            entries.Select(entry => entry.Message));
    }

    [Fact]
    public void AllEntriesIsASnapshotAndClearOnlyAffectsTheLiveBuffer()
    {
        var buffer = new InMemoryLogBuffer();
        AddEntry(buffer, LogLevel.Information, "first");
        IReadOnlyList<LogEntry> snapshot = buffer.AllEntries;

        AddEntry(buffer, LogLevel.Warning, "second");
        Assert.Single(snapshot);
        Assert.Equal("first", snapshot[0].Message);
        Assert.Equal(2, buffer.AllEntries.Count);

        buffer.Clear();

        Assert.Empty(buffer.AllEntries);
        LogEntry retained = Assert.Single(snapshot);
        Assert.Equal(LogLevel.Information, retained.LogLevel);
        Assert.Equal("first", retained.Message);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRCE")]
    [InlineData(LogLevel.Debug, "DBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "FAIL")]
    [InlineData(LogLevel.Critical, "CRIT")]
    [InlineData(LogLevel.None, "NONE")]
    public void FormatEntriesUsesExpectedLevelAbbreviation(LogLevel level, string abbreviation)
    {
        var buffer = new InMemoryLogBuffer();
        AddEntry(buffer, level, "level message");

        string content = buffer.FormatAllEntries();

        Assert.Contains($"\t{abbreviation}\t", content, StringComparison.Ordinal);
        Assert.Contains("level message", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatAllEntriesContainsStableMetadataAndErrorMarker()
    {
        var timestamp = new DateTimeOffset(2026, 7, 28, 21, 22, 23, 456, TimeSpan.Zero);
        var buffer = new InMemoryLogBuffer(new MutableTimeProvider(timestamp));
        AddEntry(buffer, LogLevel.Information, "ordinary message", eventId: new EventId(72), category: "Stable.Category");
        AddEntry(buffer, LogLevel.Error, "failed message", eventId: new EventId(73), category: "Stable.Category");

        string content = buffer.FormatAllEntries();
        string[] lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string informationLine = Assert.Single(lines, line => line.Contains("ordinary message", StringComparison.Ordinal));
        string errorLine = Assert.Single(lines, line => line.Contains("failed message", StringComparison.Ordinal));

        Assert.Contains("[2026-07-28 21:22:23.456 ", informationLine, StringComparison.Ordinal);
        Assert.Contains("\tINFO\t72\tStable.Category]", informationLine, StringComparison.Ordinal);
        Assert.DoesNotContain("!!!!!!!!!!", informationLine, StringComparison.Ordinal);
        Assert.Contains("[2026-07-28 21:22:23.456 ", errorLine, StringComparison.Ordinal);
        Assert.Contains("\tFAIL\t73\tStable.Category]", errorLine, StringComparison.Ordinal);
        Assert.Contains("\t!!!!!!!!!! failed message", errorLine, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatEntriesFiltersAtTheRequestedThresholdAndPreservesOrder()
    {
        var buffer = new InMemoryLogBuffer();
        AddEntry(buffer, LogLevel.Information, "excluded information");
        AddEntry(buffer, LogLevel.Error, "first retained");
        AddEntry(buffer, LogLevel.Warning, "threshold retained");
        AddEntry(buffer, LogLevel.Critical, "last retained");

        string content = buffer.FormatEntries(LogLevel.Warning);

        Assert.DoesNotContain("excluded information", content, StringComparison.Ordinal);
        int firstIndex = content.IndexOf("first retained", StringComparison.Ordinal);
        int thresholdIndex = content.IndexOf("threshold retained", StringComparison.Ordinal);
        int lastIndex = content.IndexOf("last retained", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0);
        Assert.True(thresholdIndex > firstIndex);
        Assert.True(lastIndex > thresholdIndex);
    }

    [Fact]
    public void FormattedExceptionIncludesOuterInnerAndAggregateDetails()
    {
        var buffer = new InMemoryLogBuffer();
        var ordinary = new InvalidOperationException(
            "outer failure",
            new ArgumentException("inner failure"));
        var firstAggregateInner = new FormatException("first aggregate failure");
        var secondAggregateInner = new NotSupportedException("second aggregate failure");
        var aggregate = new AggregateException(
            "aggregate failure",
            firstAggregateInner,
            secondAggregateInner);

        AddEntry(buffer, LogLevel.Error, "ordinary exception", ordinary);
        AddEntry(buffer, LogLevel.Critical, "aggregate exception", aggregate);

        Assert.Same(ordinary, buffer.AllEntries[0].Exception);
        Assert.Same(aggregate, buffer.AllEntries[1].Exception);
        string content = buffer.FormatAllEntries();
        Assert.Contains("Exc level 0: System.InvalidOperationException: outer failure", content, StringComparison.Ordinal);
        Assert.Contains("Exc level 1: System.ArgumentException: inner failure", content, StringComparison.Ordinal);
        Assert.Contains("Exc level 0: System.AggregateException: aggregate failure", content, StringComparison.Ordinal);
        Assert.Contains("Exc level 1: System.FormatException: first aggregate failure", content, StringComparison.Ordinal);
        Assert.Contains("Exc level 1: System.NotSupportedException: second aggregate failure", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattedReflectionTypeLoadExceptionIncludesLoaderExceptionDetails()
    {
        var buffer = new InMemoryLogBuffer();
        var firstLoaderException = new TypeLoadException("missing first type");
        var secondLoaderException = new FileNotFoundException("missing dependency");
        var exception = new ReflectionTypeLoadException(
            [typeof(string), null],
            [firstLoaderException, secondLoaderException],
            "types could not be loaded");

        AddEntry(buffer, LogLevel.Error, "reflection load failed", exception);

        Assert.Same(exception, Assert.Single(buffer.AllEntries).Exception);
        string content = buffer.FormatAllEntries();
        Assert.Contains(
            "Exc level 0: System.Reflection.ReflectionTypeLoadException: types could not be loaded",
            content,
            StringComparison.Ordinal);
        Assert.Contains("Exc level 1: System.TypeLoadException: missing first type", content, StringComparison.Ordinal);
        Assert.Contains("Exc level 1: System.IO.FileNotFoundException: missing dependency", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatWithSizeReportsExactUtf8ByteCounts()
    {
        var buffer = new InMemoryLogBuffer();
        AddEntry(buffer, LogLevel.Information, "excluded café");
        AddEntry(buffer, LogLevel.Warning, "retained 東京");
        AddEntry(buffer, LogLevel.Error, "retained 🚀");

        (string allContent, long allSize) = buffer.FormatAllEntriesWithSize();
        (string filteredContent, long filteredSize) = buffer.FormatEntriesWithSize(LogLevel.Warning);

        Assert.Equal(Encoding.UTF8.GetByteCount(allContent), allSize);
        Assert.Equal(Encoding.UTF8.GetByteCount(filteredContent), filteredSize);
        Assert.Contains("excluded café", allContent, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded café", filteredContent, StringComparison.Ordinal);
        Assert.Contains("retained 東京", filteredContent, StringComparison.Ordinal);
        Assert.Contains("retained 🚀", filteredContent, StringComparison.Ordinal);
        Assert.True(filteredSize < allSize);
    }

    [Fact]
    public void ApproximateSizeIsZeroWhenEmptyAndGrowsForContentAndExceptions()
    {
        var buffer = new InMemoryLogBuffer();

        Assert.Equal(0, buffer.ApproximateSizeBytes);

        AddEntry(buffer, LogLevel.Information, "short content");
        long messageSize = buffer.ApproximateSizeBytes;

        AddEntry(
            buffer,
            LogLevel.Error,
            "a substantially longer failure message",
            new InvalidOperationException("failure details"));
        long exceptionSize = buffer.ApproximateSizeBytes;

        Assert.True(messageSize > 0);
        Assert.True(exceptionSize > messageSize);
    }

    private static void AddEntry(
        InMemoryLogBuffer buffer,
        LogLevel level,
        string message,
        Exception? exception = null,
        EventId eventId = default,
        string category = "Test.Category") =>
        buffer.Log(level, eventId, message, exception, static (state, _) => state, category);

    private sealed record LogState(string Name, int Attempts);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [Fact]
    public void LoggingTypesHaveExpectedAssemblyNamespaceAndVisibility()
    {
        Type[] publicTypes = [typeof(InMemoryLogBuffer), typeof(InMemoryLoggerProvider), typeof(LogEntry)];
        Assembly assembly = typeof(InMemoryLoggerProvider).Assembly;

        Assert.Equal("Orleans.Testing.Simulation.Testing", assembly.GetName().Name);
        Assert.All(
            publicTypes,
            type =>
            {
                Assert.Same(assembly, type.Assembly);
                Assert.Equal("Orleans.Testing.Simulation.Testing", type.Namespace);
                Assert.True(type.IsPublic);
                Assert.Contains(type, assembly.ExportedTypes);
            });
        Assert.True(typeof(LogEntry).IsValueType);
        Assert.True(typeof(ILoggerProvider).IsAssignableFrom(typeof(InMemoryLoggerProvider)));

        Type? loggerType = assembly.GetType("Orleans.Testing.Simulation.Testing.InMemoryLogger");
        Assert.NotNull(loggerType);
        Assert.True(loggerType.IsNotPublic);
        Assert.DoesNotContain(loggerType, assembly.ExportedTypes);

        Assembly runtimeAssembly = typeof(Orleans.Testing.Simulation.SimulationCluster).Assembly;
        Assert.Null(runtimeAssembly.GetType("Orleans.Testing.Simulation.InMemoryLogBuffer"));
        Assert.Null(runtimeAssembly.GetType("Orleans.Testing.Simulation.InMemoryLoggerProvider"));
        Assert.Null(runtimeAssembly.GetType("Orleans.Testing.Simulation.InMemoryLogger"));
        Assert.Null(runtimeAssembly.GetType("Orleans.Testing.Simulation.LogEntry"));
    }

    [Fact]
    public void ProviderLoggersShareBufferAndRetainCategoryAndInsertionOrder()
    {
        using var provider = new InMemoryLoggerProvider();
        ILogger firstLogger = provider.CreateLogger("First.Category");
        ILogger secondLogger = provider.CreateLogger("Second.Category");

        LogMessage(firstLogger, LogLevel.Information, new EventId(11, "first"), "first message");
        LogMessage(secondLogger, LogLevel.Warning, new EventId(22, "second"), "second message");
        LogMessage(firstLogger, LogLevel.Error, new EventId(33, "third"), "third message");

        LogEntry[] entries = [.. provider.Buffer.AllEntries];
        Assert.Equal(3, entries.Length);
        Assert.Equal(
            ["First.Category", "Second.Category", "First.Category"],
            entries.Select(entry => entry.Category));
        Assert.Equal(
            ["first message", "second message", "third message"],
            entries.Select(entry => entry.Message));
        Assert.Equal([11, 22, 33], entries.Select(entry => entry.EventId.Id));
        Assert.Equal(
            [LogLevel.Information, LogLevel.Warning, LogLevel.Error],
            entries.Select(entry => entry.LogLevel));
    }

    [Fact]
    public void LoggerLogPassesStateAndExceptionToFormatterAndCapturesResult()
    {
        using var provider = new InMemoryLoggerProvider();
        ILogger logger = provider.CreateLogger("Generic.Category");
        var state = new LogState("job-42", 5);
        var exception = new InvalidOperationException("generic failure");
        var eventId = new EventId(42, "generic-event");
        LogState? receivedState = null;
        Exception? receivedException = null;

        logger.Log(
            LogLevel.Critical,
            eventId,
            state,
            exception,
            (value, error) =>
            {
                receivedState = value;
                receivedException = error;
                return $"{value.Name} stopped after {value.Attempts} attempts";
            });

        LogEntry entry = Assert.Single(provider.Buffer.AllEntries);
        Assert.Same(state, receivedState);
        Assert.Same(exception, receivedException);
        Assert.Equal(LogLevel.Critical, entry.LogLevel);
        Assert.Equal(eventId, entry.EventId);
        Assert.Equal("Generic.Category", entry.Category);
        Assert.Equal("job-42 stopped after 5 attempts", entry.Message);
        Assert.Same(exception, entry.Exception);
    }

    [Theory]
    [InlineData(LogLevel.Trace, true)]
    [InlineData(LogLevel.Debug, true)]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.None, false)]
    public void LoggerIsEnabledForOrdinaryLevelsButNotNone(LogLevel level, bool expected)
    {
        using var provider = new InMemoryLoggerProvider();
        ILogger logger = provider.CreateLogger("Enabled.Category");

        Assert.Equal(expected, logger.IsEnabled(level));
    }

    [Fact]
    public void LoggerDoesNotInvokeFormatterOrEnqueueForNone()
    {
        using var provider = new InMemoryLoggerProvider();
        ILogger logger = provider.CreateLogger("None.Category");
        var formatterInvoked = false;
        var ignoredException = new InvalidOperationException("ignored exception");

        logger.Log(
            LogLevel.None,
            new EventId(99, "none"),
            "ignored state",
            ignoredException,
            (state, _) =>
            {
                formatterInvoked = true;
                return state;
            });

        Assert.False(formatterInvoked);
        Assert.Empty(provider.Buffer.AllEntries);
    }

    [Fact]
    public void BeginScopeReturnsDisposableAndScopeIsCurrentlyANoOp()
    {
        using var provider = new InMemoryLoggerProvider();
        ILogger logger = provider.CreateLogger("Scope.Category");

        IDisposable? scope = logger.BeginScope("scope-value");
        Assert.NotNull(scope);
        using (scope)
        {
            LogMessage(logger, LogLevel.Information, default, "inside message");
        }

        scope.Dispose();
        LogMessage(logger, LogLevel.Information, default, "after message");

        LogEntry[] entries = [.. provider.Buffer.AllEntries];
        Assert.Equal(2, entries.Length);
        Assert.Equal(["inside message", "after message"], entries.Select(entry => entry.Message));
        Assert.All(entries, entry => Assert.Equal("Scope.Category", entry.Category));
        Assert.DoesNotContain("scope-value", provider.Buffer.FormatAllEntries(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDisposeClearsBufferAndIsIdempotent()
    {
        var provider = new InMemoryLoggerProvider();
        ILogger logger = provider.CreateLogger("Disposal.Category");
        InMemoryLogBuffer buffer = provider.Buffer;
        LogMessage(logger, LogLevel.Information, default, "retained until disposal");
        Assert.Single(buffer.AllEntries);

        provider.Dispose();

        Assert.Empty(buffer.AllEntries);
        AddEntry(buffer, LogLevel.Warning, "added after first disposal");
        provider.Dispose();
        LogEntry retained = Assert.Single(buffer.AllEntries);
        Assert.Equal("added after first disposal", retained.Message);
        Assert.Equal(LogLevel.Warning, retained.LogLevel);
    }

    private static void LogMessage(ILogger logger, LogLevel level, EventId eventId, string message) =>
        logger.Log(level, eventId, message, exception: null, static (state, _) => state);

    [Fact]
    public void ProviderForwardsTimeProviderAndLoggersCaptureCurrentUtcTime()
    {
        var firstTimestamp = new DateTimeOffset(2026, 7, 28, 18, 50, 57, TimeSpan.Zero);
        var secondTimestamp = new DateTimeOffset(2026, 7, 28, 18, 51, 58, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(firstTimestamp);
        using var provider = new InMemoryLoggerProvider(timeProvider);
        ILogger firstLogger = provider.CreateLogger("First.Timed.Category");
        ILogger secondLogger = provider.CreateLogger("Second.Timed.Category");

        LogMessage(firstLogger, LogLevel.Information, new EventId(101), "first timed message");
        timeProvider.UtcNow = secondTimestamp;
        LogMessage(secondLogger, LogLevel.Warning, new EventId(102), "second timed message");

        LogEntry[] entries = [.. provider.Buffer.AllEntries];
        Assert.Equal(2, entries.Length);
        Assert.Equal(firstTimestamp, entries[0].Timestamp);
        Assert.Equal("First.Timed.Category", entries[0].Category);
        Assert.Equal("first timed message", entries[0].Message);
        Assert.Equal(secondTimestamp, entries[1].Timestamp);
        Assert.Equal("Second.Timed.Category", entries[1].Category);
        Assert.Equal("second timed message", entries[1].Message);
    }

    [Fact]
    public void EmptyBufferFormattingAndSizeApisReturnEmptyAndZero()
    {
        var buffer = new InMemoryLogBuffer();

        string allContent = buffer.FormatAllEntries();
        string filteredContent = buffer.FormatEntries(LogLevel.Warning);
        (string allSizedContent, long allSize) = buffer.FormatAllEntriesWithSize();
        (string filteredSizedContent, long filteredSize) = buffer.FormatEntriesWithSize(LogLevel.Error);

        Assert.Equal(string.Empty, allContent);
        Assert.Equal(string.Empty, filteredContent);
        Assert.Equal(string.Empty, allSizedContent);
        Assert.Equal(0, allSize);
        Assert.Equal(string.Empty, filteredSizedContent);
        Assert.Equal(0, filteredSize);
        Assert.Equal(0, buffer.ApproximateSizeBytes);
        Assert.Empty(buffer.AllEntries);
    }

    [Fact]
    public void FormattedThrownExceptionIncludesItsExactCapturedStackTrace()
    {
        static void ThrowCapturedException() => throw new InvalidOperationException("captured stack failure");

        var buffer = new InMemoryLogBuffer();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(ThrowCapturedException);
        string stackTrace = exception.StackTrace!;
        Assert.NotEmpty(stackTrace);

        AddEntry(buffer, LogLevel.Error, "stack trace message", exception);

        string content = buffer.FormatAllEntries();
        Assert.Same(exception, Assert.Single(buffer.AllEntries).Exception);
        Assert.Contains(
            $"Exc level 0: System.InvalidOperationException: captured stack failure{Environment.NewLine}{stackTrace}",
            content,
            StringComparison.Ordinal);
        Assert.Contains("stack trace message", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattedReflectionTypeLoadExceptionHandlesEmptyAndNullLoaderExceptions()
    {
        var buffer = new InMemoryLogBuffer();
        var withoutLoaders = new ReflectionTypeLoadException([], [], "no loader details");
        AddEntry(buffer, LogLevel.Error, "empty loaders", withoutLoaders);

        string emptyLoaderContent = buffer.FormatAllEntries();
        Assert.Contains(
            "Exc level 0: System.Reflection.ReflectionTypeLoadException: no loader details",
            emptyLoaderContent,
            StringComparison.Ordinal);
        Assert.Contains("No LoaderExceptions found", emptyLoaderContent, StringComparison.Ordinal);

        buffer.Clear();
        var realLoaderException = new TypeLoadException("real loader failure");
        var withNullLoader = new ReflectionTypeLoadException(
            [typeof(string), null],
            [null, realLoaderException],
            "mixed loader details");
        AddEntry(buffer, LogLevel.Critical, "mixed loaders", withNullLoader);

        string mixedLoaderContent = buffer.FormatAllEntries();
        Assert.DoesNotContain("No LoaderExceptions found", mixedLoaderContent, StringComparison.Ordinal);
        Assert.Contains(
            "Exc level 0: System.Reflection.ReflectionTypeLoadException: mixed loader details",
            mixedLoaderContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Exc level 1: System.TypeLoadException: real loader failure",
            mixedLoaderContent,
            StringComparison.Ordinal);
        Assert.Equal(1, mixedLoaderContent.Split("Exc level 1:", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ErrorMarkerIsAppliedOnlyToErrorAndNotCritical()
    {
        var buffer = new InMemoryLogBuffer();
        AddEntry(buffer, LogLevel.Warning, "warning marker check");
        AddEntry(buffer, LogLevel.Error, "error marker check");
        AddEntry(buffer, LogLevel.Critical, "critical marker check");

        string content = buffer.FormatAllEntries();
        string[] lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string warningLine = Assert.Single(lines, line => line.Contains("warning marker check", StringComparison.Ordinal));
        string errorLine = Assert.Single(lines, line => line.Contains("error marker check", StringComparison.Ordinal));
        string criticalLine = Assert.Single(lines, line => line.Contains("critical marker check", StringComparison.Ordinal));

        Assert.DoesNotContain("!!!!!!!!!!", warningLine, StringComparison.Ordinal);
        Assert.Contains("\t!!!!!!!!!! error marker check", errorLine, StringComparison.Ordinal);
        Assert.DoesNotContain("!!!!!!!!!!", criticalLine, StringComparison.Ordinal);
        Assert.Equal(1, content.Split("!!!!!!!!!!", StringSplitOptions.None).Length - 1);
    }
}
