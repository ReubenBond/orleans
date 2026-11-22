using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Fast, emulator-free checks on the live-account fidelity harnesses: skip-reason parity with <c>test/Durable.Storage/eng/validate.ps1</c>,
/// bucket-name normalization and validation, and stale run-prefix timestamp detection.
/// </summary>
public sealed class LiveAccountHarnessTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Every live-account harness's <c>SkipReason</c> appears verbatim in <c>test/Durable.Storage/eng/validate.ps1</c>'s allowlist so an
    /// emulator run with unset <c>CLOG_*_LIVE</c> variables never drifts out of sync.
    /// </summary>
    [Fact]
    public void ValidateScriptContainsEveryLiveAccountSkipReasonVerbatim()
    {
        var validatePath = Path.Combine(FindStorageRoot(), "eng", "validate.ps1");
        var script = File.ReadAllText(validatePath);

        string[] reasons =
        [
            AzureLiveAccount.SkipReason,
            S3LiveAccount.SkipReason,
            S3ExpressLiveAccount.SkipReason,
            GcsLiveAccount.SkipReason,
        ];

        foreach (var reason in reasons)
        {
            var psSingleQuoted = $"'{reason.Replace("'", "''", StringComparison.Ordinal)}'";
            Assert.Contains(psSingleQuoted, script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null, "s3://")]
    [InlineData("", "s3://")]
    [InlineData("   \t  ", "s3://")]
    [InlineData("s3://", "s3://")]
    [InlineData("s3:///", "s3://")]
    [InlineData("GS:///", "gs://")]
    public void NormalizeBucketNameReturnsNullForUnsetOrEmptyValues(string? raw, string scheme)
    {
        Assert.Null(LiveBucketAccount.NormalizeBucketName(raw, scheme, "CLOG_TEST_LIVE"));
    }

    [Theory]
    [InlineData("my-bucket", "s3://", "my-bucket")]
    [InlineData("  s3://my-bucket/  ", "s3://", "my-bucket")]
    [InlineData("S3://my-bucket--use1-az4--x-s3///", "s3://", "my-bucket--use1-az4--x-s3")]
    [InlineData("gs://my-gcs-bucket/", "gs://", "my-gcs-bucket")]
    public void NormalizeBucketNameStripsSchemeWhitespaceAndTrailingSlashes(string raw, string scheme, string expected)
    {
        Assert.Equal(expected, LiveBucketAccount.NormalizeBucketName(raw, scheme, "CLOG_TEST_LIVE"));
    }

    [Theory]
    [InlineData("s3://my-bucket/nested/path", "s3://")]
    [InlineData("my bucket", "s3://")]
    [InlineData("gs://bucket/sub", "gs://")]
    [InlineData("bucket\tname", "gs://")]
    public void NormalizeBucketNameRejectsPathSegmentsAndInternalWhitespace(string raw, string scheme)
    {
        _ = Assert.Throws<InvalidOperationException>(() => LiveBucketAccount.NormalizeBucketName(raw, scheme, "CLOG_TEST_LIVE"));
    }

    [Fact]
    public void IsStaleRunPrefixDistinguishesOldRecentAndMalformedPrefixes()
    {
        var now = new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero);
        const string providerPrefix = "clog-fidelity-s3/";

        Assert.True(LiveBucketAccount.IsStaleRunPrefix("clog-fidelity-s3/20260415T115959Z-abcdef/", providerPrefix, now));
        Assert.False(LiveBucketAccount.IsStaleRunPrefix("clog-fidelity-s3/20260417T115959Z-abcdef/", providerPrefix, now));
        Assert.False(LiveBucketAccount.IsStaleRunPrefix("clog-fidelity-s3/not-a-timestamp/", providerPrefix, now));
        Assert.False(LiveBucketAccount.IsStaleRunPrefix("other-prefix/20260415T115959Z-abcdef/", providerPrefix, now));
    }

    [Fact]
    public async Task OncePerProcessSweepSurvivesFirstCallerCancellationAndRetriesAfterFailure()
    {
        var sweep = new LiveBucketAccount.OncePerProcessSweep();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;

        // An already-canceled caller fast-fails without starting a sweep.
        using (var alreadyCanceled = new CancellationTokenSource())
        {
            await alreadyCanceled.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.RunOnceAsync(
                ct =>
                {
                    _ = Interlocked.Increment(ref invocations);
                    return Task.CompletedTask;
                },
                alreadyCanceled.Token));
        }

        Assert.Equal(0, Volatile.Read(ref invocations));

        using var firstCts = new CancellationTokenSource();
        var first = sweep.RunOnceAsync(
            async ct =>
            {
                _ = Interlocked.Increment(ref invocations);
                _ = started.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
            firstCts.Token);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var second = sweep.RunOnceAsync(
            ct =>
            {
                _ = Interlocked.Increment(ref invocations);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await firstCts.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        _ = release.TrySetResult();
        await second;
        Assert.Equal(1, Volatile.Read(ref invocations));

        // A faulted sweep is discarded so a later caller retries.
        var retryingSweep = new LiveBucketAccount.OncePerProcessSweep();
        var attempts = 0;
        _ = await Assert.ThrowsAsync<IOException>(() => retryingSweep.RunOnceAsync(
            ct =>
            {
                _ = Interlocked.Increment(ref attempts);
                return Task.FromException(new IOException("transient failure"));
            },
            TestContext.Current.CancellationToken));

        await retryingSweep.RunOnceAsync(
            ct =>
            {
                _ = Interlocked.Increment(ref attempts);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    private static string FindStorageRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Orleans.slnx")))
            {
                return Path.Combine(current.FullName, "test", "Durable.Storage");
            }

            current = current.Parent;
        }

        throw new InvalidOperationException($"Could not locate Orleans.slnx above '{AppContext.BaseDirectory}'.");
    }
}
