using System.Collections.Concurrent;
using System.Globalization;
using Xunit;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Shared bucket-name validation, timestamped run-prefix isolation, stale-prefix sweep gating, and bounded-parallel
/// cleanup helpers for the live-account S3, S3 Express One Zone, and Google Cloud Storage harnesses.
/// </summary>
internal static class LiveBucketAccount
{
    private const int DeleteParallelism = 8;
    private const string TimestampFormat = "yyyyMMddTHHmmssZ";
    private static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Normalizes a bucket environment variable value: returns <see langword="null"/> when unset, whitespace-only, or a
    /// bare scheme prefix (for example <c>s3:///</c> or <c>gs:///</c>); fails closed with
    /// <see cref="InvalidOperationException"/> when the remaining value contains whitespace or path separators.
    /// </summary>
    internal static string? NormalizeBucketName(string? raw, string schemePrefix, string envVarName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[schemePrefix.Length..];
        }

        trimmed = trimmed.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal) || trimmed.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Invalid {envVarName} value '{raw}': expected a bare bucket name without whitespace or path segments.");
        }

        return trimmed;
    }

    /// <summary>
    /// Formats a unique, timestamped run prefix under <paramref name="providerPrefix"/> so concurrent and repeated live
    /// runs never collide and stale runs older than <see cref="StaleAge"/> can be swept.
    /// </summary>
    internal static string CreateRunPrefix(string providerPrefix) =>
        $"{providerPrefix}{TimeProvider.System.GetUtcNow().ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}/";

    /// <summary>
    /// Returns whether <paramref name="runPrefix"/> is a timestamped prefix under <paramref name="providerPrefix"/>
    /// created more than <see cref="StaleAge"/> before <paramref name="now"/>.
    /// </summary>
    internal static bool IsStaleRunPrefix(string runPrefix, string providerPrefix, DateTimeOffset now)
    {
        if (!runPrefix.StartsWith(providerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var segment = runPrefix.AsSpan(providerPrefix.Length);
        var dash = segment.IndexOf('-');
        if (dash != TimestampFormat.Length)
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
                   segment[..dash],
                   TimestampFormat,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var created)
               && now - created > StaleAge;
    }

    /// <summary>
    /// Attempts to delete every key in <paramref name="keys"/> with bounded concurrency, collecting all per-key failures
    /// instead of aborting on the first so cleanup removes as much as possible.
    /// </summary>
    internal static async Task DeleteAllAsync(
        IReadOnlyList<string> keys,
        Func<string, CancellationToken, Task> deleteOne,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var failures = new ConcurrentQueue<Exception>();
        await Parallel.ForEachAsync(
            keys,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = DeleteParallelism,
                CancellationToken = cancellationToken,
            },
            async (key, ct) =>
            {
                try
                {
                    await deleteOne(key, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Enqueue(ex);
                }
            });

        if (!failures.IsEmpty)
        {
            throw new AggregateException($"Failed to delete {failures.Count} live test object(s).", failures);
        }
    }

    /// <summary>
    /// Completes disposal without letting an <see cref="TransportDouble.OpenResponses"/> assertion mask a cleanup
    /// exception or an already-failed test.
    /// </summary>
    internal static void FinalizeDisposal(Exception? cleanupFailure, int openResponses)
    {
        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }

        if (TestContext.Current.TestState?.Result != TestResult.Failed)
        {
            Assert.Equal(0, openResponses);
        }
    }

    /// <summary>
    /// Runs a best-effort stale-prefix sweep at most once per process; a faulted or canceled attempt is discarded so a
    /// later test retries. The sweep delegate is invoked outside the gate and owns its own client/transport lifetime so
    /// a cancelling waiter never disposes resources under an in-flight sweep.
    /// </summary>
    internal sealed class OncePerProcessSweep
    {
        private readonly Lock _gate = new();
        private Task? _sweep;

        internal Task RunOnceAsync(Func<CancellationToken, Task> sweepAsync, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            TaskCompletionSource? starter = null;
            Task shared;
            lock (_gate)
            {
                if (_sweep is { IsFaulted: true } or { IsCanceled: true })
                {
                    _sweep = null;
                }

                if (_sweep is null)
                {
                    starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _sweep = starter.Task;
                }

                shared = _sweep;
            }

            if (starter is not null)
            {
                _ = starter.Task.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _ = RunStarterAsync(starter, sweepAsync);
            }

            return shared.WaitAsync(cancellationToken);
        }

        private static async Task RunStarterAsync(
            TaskCompletionSource starter,
            Func<CancellationToken, Task> sweepAsync)
        {
            try
            {
                await sweepAsync(CancellationToken.None).ConfigureAwait(false);
                _ = starter.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                _ = starter.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                _ = starter.TrySetException(ex);
            }
        }
    }
}
