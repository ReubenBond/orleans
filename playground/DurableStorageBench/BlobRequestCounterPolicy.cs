using System.Collections.Concurrent;
using Azure.Core;
using Azure.Core.Pipeline;

namespace Orleans.Durable.Storage.Bench;

/// <summary>
/// <see cref="HttpPipelinePolicy"/> attached at <see cref="HttpPipelinePosition.PerRetry"/> to track exact
/// ground-truth HTTP request counts to Azure Blob Storage broken down by logical container and HTTP method.
/// </summary>
public sealed class BlobRequestCounterPolicy : HttpPipelinePolicy
{
    private readonly Dictionary<string, string> _containerAliases;
    private readonly ConcurrentDictionary<(string Container, string Method), CounterEntry> _counts = new();
    private long _totalRequests;
    private long _failedRequests;

    public BlobRequestCounterPolicy(
        string lsmContainer,
        string jobsContainer,
        string baselineJournalsContainer)
    {
        _containerAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(lsmContainer))
        {
            _containerAliases[lsmContainer] = "lsm";
        }

        if (!string.IsNullOrWhiteSpace(jobsContainer))
        {
            _containerAliases[jobsContainer] = "jobs";
        }

        if (!string.IsNullOrWhiteSpace(baselineJournalsContainer))
        {
            _containerAliases[baselineJournalsContainer] = "journals";
        }
    }

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var (container, method) = Classify(message.Request);
        Increment(container, method);
        try
        {
            ProcessNext(message, pipeline);
            if (message.HasResponse && message.Response.Status >= 400)
            {
                Interlocked.Increment(ref _failedRequests);
            }
        }
        catch
        {
            Interlocked.Increment(ref _failedRequests);
            throw;
        }
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var (container, method) = Classify(message.Request);
        Increment(container, method);
        try
        {
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
            if (message.HasResponse && message.Response.Status >= 400)
            {
                Interlocked.Increment(ref _failedRequests);
            }
        }
        catch
        {
            Interlocked.Increment(ref _failedRequests);
            throw;
        }
    }

    public BlobCounterSnapshot Snapshot()
    {
        var byContainer = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        var byMethod = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;

        foreach (var ((container, method), entry) in _counts)
        {
            var count = Volatile.Read(ref entry.Count);
            if (count == 0)
            {
                continue;
            }

            if (!byContainer.TryGetValue(container, out var methods))
            {
                methods = new Dictionary<string, long>(StringComparer.Ordinal);
                byContainer[container] = methods;
            }

            methods[method] = methods.GetValueOrDefault(method) + count;
            byMethod[method] = byMethod.GetValueOrDefault(method) + count;
            total += count;
        }

        return new BlobCounterSnapshot(
            Total: total,
            Failed: Interlocked.Read(ref _failedRequests),
            ByMethod: byMethod,
            ByContainer: byContainer);
    }

    private void Increment(string container, string method)
    {
        Interlocked.Increment(ref _totalRequests);
        var entry = _counts.GetOrAdd((container, method), static _ => new CounterEntry());
        Interlocked.Increment(ref entry.Count);
    }

    private (string Container, string Method) Classify(Azure.Core.Request request)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var rawPath = request.Uri.Path;
        var container = ExtractContainer(rawPath);
        if (_containerAliases.TryGetValue(container, out var alias))
        {
            container = alias;
        }

        return (container, method);
    }

    private static string ExtractContainer(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "_service";
        }

        var span = path.AsSpan().TrimStart('/');
        if (span.IsEmpty)
        {
            return "_service";
        }

        var slash = span.IndexOf('/');
        var first = slash >= 0 ? span[..slash] : span;

        // Azurite paths are /devstoreaccount1/<container>/...
        if (first.Equals("devstoreaccount1", StringComparison.OrdinalIgnoreCase) && slash >= 0)
        {
            var rest = span[(slash + 1)..];
            if (rest.IsEmpty)
            {
                return "_service";
            }

            var nextSlash = rest.IndexOf('/');
            return (nextSlash >= 0 ? rest[..nextSlash] : rest).ToString();
        }

        return first.ToString();
    }

    private sealed class CounterEntry
    {
        public long Count;
    }
}

[GenerateSerializer]
[Immutable]
public sealed record BlobCounterSnapshot(
    [property: Id(0)] long Total,
    [property: Id(1)] long Failed,
    [property: Id(2)] Dictionary<string, long> ByMethod,
    [property: Id(3)] Dictionary<string, Dictionary<string, long>> ByContainer)
{
    public static BlobCounterSnapshot Empty { get; } = new(0, 0, [], []);

    public long GetCount(string container, string method)
    {
        if (ByContainer.TryGetValue(container, out var methods)
            && methods.TryGetValue(method, out var count))
        {
            return count;
        }

        return 0;
    }

    public static BlobCounterSnapshot Merge(IEnumerable<BlobCounterSnapshot> snapshots)
    {
        var byContainer = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        var byMethod = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        long failed = 0;

        foreach (var snapshot in snapshots)
        {
            total += snapshot.Total;
            failed += snapshot.Failed;
            foreach (var (method, count) in snapshot.ByMethod)
            {
                byMethod[method] = byMethod.GetValueOrDefault(method) + count;
            }

            foreach (var (container, methods) in snapshot.ByContainer)
            {
                if (!byContainer.TryGetValue(container, out var target))
                {
                    target = new Dictionary<string, long>(StringComparer.Ordinal);
                    byContainer[container] = target;
                }

                foreach (var (method, count) in methods)
                {
                    target[method] = target.GetValueOrDefault(method) + count;
                }
            }
        }

        return new BlobCounterSnapshot(total, failed, byMethod, byContainer);
    }

    public static BlobCounterSnapshot Diff(BlobCounterSnapshot before, BlobCounterSnapshot after)
    {
        var byContainer = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        var byMethod = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;

        foreach (var (container, afterMethods) in after.ByContainer)
        {
            before.ByContainer.TryGetValue(container, out var beforeMethods);
            Dictionary<string, long>? deltaMethods = null;

            foreach (var (method, afterCount) in afterMethods)
            {
                var beforeCount = beforeMethods is not null ? beforeMethods.GetValueOrDefault(method) : 0;
                var delta = Math.Max(0, afterCount - beforeCount);
                if (delta == 0)
                {
                    continue;
                }

                deltaMethods ??= new Dictionary<string, long>(StringComparer.Ordinal);
                deltaMethods[method] = delta;
                byMethod[method] = byMethod.GetValueOrDefault(method) + delta;
                total += delta;
            }

            if (deltaMethods is not null)
            {
                byContainer[container] = deltaMethods;
            }
        }

        return new BlobCounterSnapshot(
            Total: total,
            Failed: Math.Max(0, after.Failed - before.Failed),
            ByMethod: byMethod,
            ByContainer: byContainer);
    }
}
