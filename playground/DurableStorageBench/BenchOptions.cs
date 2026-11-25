using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Durable.Storage.Bench;

public enum BenchMode
{
    Clog,
    Baseline,
}

public sealed class BenchOptions
{
    public string Mode { get; set; } = "Clog";

    public string ClusterId { get; set; } = "durable-storage-bench";

    public string ServiceId { get; set; } = "durable-storage-bench";

    public string? SiloName { get; set; }

    public int SiloPort { get; set; } = 11111;

    public int GatewayPort { get; set; } = 30000;

    public int HttpPort { get; set; } = 8080;

    public string? AdvertisedIPAddress { get; set; }

    public long RocksDbWriteBufferBytes { get; set; } = 256L << 20;

    public BenchStorageOptions Storage { get; set; } = new();

    public BenchMode ParsedMode => Mode.Trim().ToLowerInvariant() switch
    {
        "clog" or "durablestorage" or "durable-storage" => BenchMode.Clog,
        "baseline" or "azureblob" or "azure-blob" => BenchMode.Baseline,
        _ => throw new InvalidOperationException($"Unsupported Bench:Mode '{Mode}'. Expected 'Clog' or 'Baseline'."),
    };
}

public sealed class BenchStorageOptions
{
    public string? BlobEndpoint { get; set; }

    public string? TableEndpoint { get; set; }

    public string? ConnectionString { get; set; }

    public string? ClusteringTableName { get; set; }

    public string LsmContainer { get; set; } = "lsm";

    public string JobsContainer { get; set; } = "jobs";

    public string BaselineJournalsContainer { get; set; } = "journals";

    public string? ResultsBlobEndpoint { get; set; }

    public string? ResultsConnectionString { get; set; }

    public string ResultsContainer { get; set; } = "results";

    public bool CreateContainersIfNotExists { get; set; } = true;
}

public sealed class CasPaxosBenchOptions
{
    public string? VoterHosts { get; set; }

    public string ConsensusDir { get; set; } = "/mnt/nvme/consensus";

    public int? Ordinal { get; set; }

    public int? ProposerId { get; set; }

    public int DnsResolveTimeoutSeconds { get; set; } = 180;
}

public sealed record ResolvedVoter(int Index, string Id, string HostSpec, IPEndPoint Endpoint, string DatabasePath);

public sealed record BenchRuntimeInfo(
    BenchMode Mode,
    string ClusterId,
    string ServiceId,
    string SiloName,
    int HttpPort,
    string WalStorageType,
    string LsmContainer,
    string JobsContainer,
    string BaselineJournalsContainer,
    int? Ordinal,
    IReadOnlyList<string> ResolvedVoters);

internal static class BenchBootstrapHelpers
{
    public static int? InferOrdinal(int? configuredOrdinal, int? durableStorageOrdinal, string? podName)
    {
        if (configuredOrdinal is { } c)
        {
            return c;
        }

        if (durableStorageOrdinal is { } d)
        {
            return d;
        }

        if (!string.IsNullOrWhiteSpace(podName))
        {
            var dash = podName.LastIndexOf('-');
            if (dash >= 0 && dash < podName.Length - 1
                && int.TryParse(podName.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static async Task<List<ResolvedVoter>> ResolveVotersAsync(
        CasPaxosBenchOptions options,
        int defaultSiloPort,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.VoterHosts))
        {
            throw new InvalidOperationException(
                "CasPaxosBench:VoterHosts must be configured in Clog mode (e.g. 'silo-0.silo-headless.default.svc.cluster.local:11111,...').");
        }

        var entries = options.VoterHosts
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            throw new InvalidOperationException("CasPaxosBench:VoterHosts contains no entries.");
        }

        var consensusDir = Path.GetFullPath(options.ConsensusDir);
        Directory.CreateDirectory(consensusDir);

        var timeout = TimeSpan.FromSeconds(Math.Max(5, options.DnsResolveTimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var resolved = new IPEndPoint?[entries.Length];
        var attempt = 0;

        while (true)
        {
            attempt++;
            var pending = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                if (resolved[i] is not null)
                {
                    continue;
                }

                var ep = await TryResolveEndpointAsync(entries[i], defaultSiloPort, cancellationToken).ConfigureAwait(false);
                if (ep is not null)
                {
                    resolved[i] = ep;
                    Console.WriteLine($"[CasPaxosBootstrap] Resolved voter-{i} '{entries[i]}' -> {ep}");
                }
                else
                {
                    pending++;
                }
            }

            if (pending == 0)
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var unresolved = entries.Where((_, idx) => resolved[idx] is null);
                throw new TimeoutException(
                    $"Timed out after {timeout.TotalSeconds:F0}s waiting for CasPaxos voter DNS resolution: {string.Join(", ", unresolved)}");
            }

            if (attempt == 1 || attempt % 5 == 0)
            {
                var unresolved = entries.Where((_, idx) => resolved[idx] is null);
                Console.WriteLine($"[CasPaxosBootstrap] Attempt {attempt}: waiting for DNS resolution of {string.Join(", ", unresolved)}...");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        var result = new List<ResolvedVoter>(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var id = $"voter-{i}";
            var dbPath = Path.Combine(consensusDir, $"voter-{i}.db");
            result.Add(new ResolvedVoter(i, id, entries[i], resolved[i]!, dbPath));
        }

        return result;
    }

    public static async Task<IPAddress> ResolveAdvertisedIpAsync(
        BenchOptions options,
        IReadOnlyList<ResolvedVoter>? resolvedVoters,
        int? ordinal,
        CancellationToken cancellationToken)
    {
        var explicitIp = options.AdvertisedIPAddress ?? Environment.GetEnvironmentVariable("POD_IP");
        if (!string.IsNullOrWhiteSpace(explicitIp) && IPAddress.TryParse(explicitIp.Trim(), out var parsed))
        {
            return parsed;
        }

        if (resolvedVoters is not null && ordinal is { } ord && ord >= 0 && ord < resolvedVoters.Count)
        {
            return resolvedVoters[ord].Endpoint.Address;
        }

        try
        {
            var hostAddresses = await Dns.GetHostAddressesAsync(Dns.GetHostName(), cancellationToken).ConfigureAwait(false);
            var ipv4 = hostAddresses.FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (ipv4 is not null)
            {
                return ipv4;
            }
        }
        catch
        {
            // Fall through to loopback.
        }

        return IPAddress.Loopback;
    }

    public static string SanitizeAzureTableName(string? explicitName, string clusterId)
    {
        var raw = !string.IsNullOrWhiteSpace(explicitName) ? explicitName.Trim() : clusterId.Trim();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        var sanitized = sb.ToString();
        if (sanitized.Length == 0 || !char.IsAsciiLetter(sanitized[0]))
        {
            sanitized = "Orleans" + sanitized;
        }

        if (sanitized.Length < 3)
        {
            sanitized = sanitized.PadRight(3, '0');
        }

        if (sanitized.Length > 63)
        {
            sanitized = sanitized[..63];
        }

        return sanitized;
    }

    public static string SanitizeArchivePrefix(string clusterId)
    {
        var sb = new StringBuilder(16);
        foreach (var ch in clusterId.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
            {
                sb.Append(ch);
                if (sb.Length == 16)
                {
                    break;
                }
            }
        }

        var prefix = sb.ToString();
        if (string.IsNullOrEmpty(prefix) || prefix is "default" or "dev")
        {
            prefix = "bench";
        }

        return prefix;
    }

    public static Guid DeterministicClusterGuid(string clusterId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"orleans-durable-storage-bench:{clusterId}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static async Task<IPEndPoint?> TryResolveEndpointAsync(string spec, int defaultPort, CancellationToken cancellationToken)
    {
        if (IPEndPoint.TryParse(spec, out var parsed) && parsed.Port > 0)
        {
            return parsed;
        }

        var host = spec;
        var port = defaultPort;
        var lastColon = spec.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(spec.AsSpan(lastColon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort))
        {
            host = spec[..lastColon];
            port = parsedPort;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return new IPEndPoint(ip, port);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            var ipv4 = addresses.FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork);
            return ipv4 is not null ? new IPEndPoint(ipv4, port) : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
