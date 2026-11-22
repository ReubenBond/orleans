using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orleans.Testing.Simulation;

/// <summary>
/// Result of checking whether a message can be delivered.
/// </summary>
public enum DeliveryStatus
{
    /// <summary>Message can be delivered normally.</summary>
    Success,

    /// <summary>Message was randomly dropped (transient failure, should retry).</summary>
    Dropped,

    /// <summary>Message blocked by network partition (persistent failure).</summary>
    Partitioned,
}

/// <summary>
/// Represents the latency parameters for a network link.
/// </summary>
/// <param name="BaseDelay">The base (minimum) message delay.</param>
/// <param name="MaxJitter">The maximum additional random delay.</param>
public readonly record struct LinkLatency(TimeSpan BaseDelay, TimeSpan MaxJitter);

/// <summary>
/// Represents a directional per-link latency override.
/// </summary>
/// <param name="SourceAddress">The sending node's network address.</param>
/// <param name="TargetAddress">The receiving node's network address.</param>
/// <param name="Latency">The configured latency parameters.</param>
public readonly record struct LinkLatencyOverride(string SourceAddress, string TargetAddress, LinkLatency Latency);

/// <summary>
/// Simulation network for in-memory transport between nodes.
/// Provides hooks for injecting network faults like delays, partitions, and message loss.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public class SimulationNetwork
{
    private readonly Func<IReadOnlyList<SimulationNode>> _getNodes;
    private readonly SimulationRandom _random;
    private readonly ConcurrentDictionary<string, HashSet<string>> _partitions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target), LinkLatency> _linkLatencies = [];
    private readonly Lock _lock = new();
    private ILogger _logger;
    private TimeSpan _baseMessageDelay = TimeSpan.Zero;
    private TimeSpan _maxJitter = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets the base message delay for all messages.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative, or its sum with <see cref="MaxJitter"/> exceeds <see cref="TimeSpan.MaxValue"/>.
    /// </exception>
    public TimeSpan BaseMessageDelay
    {
        get => _baseMessageDelay;
        set
        {
            ValidateDelay(value, _maxJitter, nameof(BaseMessageDelay));
            _baseMessageDelay = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum additional random delay for messages.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative, or its sum with <see cref="BaseMessageDelay"/> exceeds <see cref="TimeSpan.MaxValue"/>.
    /// </exception>
    public TimeSpan MaxJitter
    {
        get => _maxJitter;
        set
        {
            ValidateDelay(value, _baseMessageDelay, nameof(MaxJitter));
            _maxJitter = value;
        }
    }

    /// <summary>
    /// Gets or sets the probability of a message being dropped (0.0 to 1.0).
    /// </summary>
    public double MessageDropRate { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationNetwork"/> class.
    /// Creates a new simulation network.
    /// </summary>
    /// <param name="getNodes">Function to get the list of nodes in the simulation.</param>
    /// <param name="random">The deterministic random number generator.</param>
    public SimulationNetwork(Func<IReadOnlyList<SimulationNode>> getNodes, SimulationRandom random)
    {
        ArgumentNullException.ThrowIfNull(getNodes);
        ArgumentNullException.ThrowIfNull(random);
        _getNodes = getNodes;
        _random = random;
        _logger = NullLogger.Instance;
    }

    /// <summary>
    /// Sets the logger for network operations.
    /// </summary>
    public void SetLogger(ILogger logger) => _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Creates a network partition between two nodes (unidirectional).
    /// Messages from sourceAddress will not reach targetAddress.
    /// </summary>
    public void CreatePartition(string sourceAddress, string targetAddress)
    {
        lock (_lock)
        {
            var blocked = _partitions.GetOrAdd(sourceAddress, _ => []);
            blocked.Add(targetAddress);
        }

        OnPartitionCreated(sourceAddress, targetAddress);
    }

    /// <summary>
    /// Creates a bidirectional network partition between two nodes.
    /// </summary>
    public void CreateBidirectionalPartition(string node1, string node2)
    {
        OnBidirectionalPartitionCreating(node1, node2);
        CreatePartition(node1, node2);
        CreatePartition(node2, node1);
    }

    /// <summary>
    /// Removes a network partition between two nodes (unidirectional).
    /// </summary>
    public void HealPartition(string sourceAddress, string targetAddress)
    {
        lock (_lock)
        {
            if (_partitions.TryGetValue(sourceAddress, out var blocked))
            {
                blocked.Remove(targetAddress);
            }
        }

        OnPartitionHealed(sourceAddress, targetAddress);
    }

    /// <summary>
    /// Removes a bidirectional network partition between two nodes.
    /// </summary>
    public void HealBidirectionalPartition(string node1, string node2)
    {
        OnBidirectionalPartitionHealing(node1, node2);
        HealPartition(node1, node2);
        HealPartition(node2, node1);
    }

    /// <summary>
    /// Removes all network partitions.
    /// </summary>
    public void HealAllPartitions()
    {
        int count;
        lock (_lock)
        {
            count = _partitions.Count;
            _partitions.Clear();
        }

        OnAllPartitionsHealed(count);
    }

    /// <summary>
    /// Isolates a node from all other nodes (bidirectional).
    /// </summary>
    public void IsolateNode(string nodeAddress)
    {
        OnNodeIsolating(nodeAddress);
        foreach (var node in _getNodes())
        {
            var addr = node.NetworkAddress;
            if (!string.Equals(addr, nodeAddress, StringComparison.Ordinal))
            {
                CreateBidirectionalPartition(nodeAddress, addr);
            }
        }
    }

    /// <summary>
    /// Removes isolation from a node.
    /// </summary>
    public void ReconnectNode(string nodeAddress)
    {
        OnNodeReconnecting(nodeAddress);
        foreach (var node in _getNodes())
        {
            var addr = node.NetworkAddress;
            if (!string.Equals(addr, nodeAddress, StringComparison.Ordinal))
            {
                HealBidirectionalPartition(nodeAddress, addr);
            }
        }
    }

    /// <summary>
    /// Checks if a node is isolated (has partitions with all other nodes).
    /// </summary>
    public bool IsNodeIsolated(string nodeAddress)
    {
        lock (_lock)
        {
            if (!_partitions.TryGetValue(nodeAddress, out var blocked))
            {
                return false;
            }

            // Check if node is partitioned from all other nodes
            foreach (var node in _getNodes())
            {
                var addr = node.NetworkAddress;
                if (!string.Equals(addr, nodeAddress, StringComparison.Ordinal) && !blocked.Contains(addr))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Checks if a message can be delivered from source to target.
    /// Returns a status indicating success or reason for failure.
    /// </summary>
    public DeliveryStatus CheckDelivery(string sourceAddress, string targetAddress)
    {
        // Self-messages (loopback) are always delivered reliably.
        // In real networks, loopback communication doesn't go through the network.
        if (string.Equals(sourceAddress, targetAddress, StringComparison.Ordinal))
        {
            return DeliveryStatus.Success;
        }

        // Check for network partition first (persistent)
        lock (_lock)
        {
            if (_partitions.TryGetValue(sourceAddress, out var blocked) && blocked.Contains(targetAddress))
            {
                OnMessageBlockedByPartition(sourceAddress, targetAddress);
                return DeliveryStatus.Partitioned;
            }
        }

        // Check for random message drop (transient)
        if (MessageDropRate > 0 && _random.Chance(MessageDropRate))
        {
            OnMessageDroppedRandom(sourceAddress, targetAddress);
            return DeliveryStatus.Dropped;
        }

        return DeliveryStatus.Success;
    }

    /// <summary>
    /// Checks if a message can be delivered from source to target.
    /// This is a convenience method that returns true only if delivery would succeed.
    /// </summary>
    public bool CanDeliver(string sourceAddress, string targetAddress) => CheckDelivery(sourceAddress, targetAddress) == DeliveryStatus.Success;

    /// <summary>
    /// Gets the simulated delay for a message.
    /// </summary>
    public TimeSpan GetMessageDelay()
    {
        return GetMessageDelay(_baseMessageDelay, _maxJitter);
    }

    /// <summary>
    /// Gets the simulated delay for a message between specific nodes.
    /// Uses a directional per-link override when configured and otherwise uses the global delay.
    /// Loopback messages always have zero delay.
    /// </summary>
    /// <param name="sourceAddress">The sending node's network address.</param>
    /// <param name="targetAddress">The receiving node's network address.</param>
    public TimeSpan GetMessageDelay(string sourceAddress, string targetAddress)
    {
        if (string.Equals(sourceAddress, targetAddress, StringComparison.Ordinal))
        {
            return TimeSpan.Zero;
        }

        LinkLatency latency;
        lock (_lock)
        {
            if (!_linkLatencies.TryGetValue((sourceAddress, targetAddress), out latency))
            {
                latency = new LinkLatency(_baseMessageDelay, _maxJitter);
            }
        }

        return GetMessageDelay(latency.BaseDelay, latency.MaxJitter);
    }

    /// <summary>
    /// Sets a directional per-link latency override.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A delay is negative, or their sum exceeds <see cref="TimeSpan.MaxValue"/>.
    /// </exception>
    public void SetLinkLatency(string sourceAddress, string targetAddress, TimeSpan baseDelay, TimeSpan maxJitter)
    {
        ValidateDelayPair(baseDelay, maxJitter, nameof(baseDelay), nameof(maxJitter));

        lock (_lock)
        {
            _linkLatencies[(sourceAddress, targetAddress)] = new LinkLatency(baseDelay, maxJitter);
        }

        OnLinkLatencySet(sourceAddress, targetAddress, baseDelay, maxJitter);
    }

    /// <summary>
    /// Sets the same per-link latency override in both directions.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A delay is negative, or their sum exceeds <see cref="TimeSpan.MaxValue"/>.
    /// </exception>
    public void SetBidirectionalLinkLatency(string node1, string node2, TimeSpan baseDelay, TimeSpan maxJitter)
    {
        ValidateDelayPair(baseDelay, maxJitter, nameof(baseDelay), nameof(maxJitter));

        lock (_lock)
        {
            var latency = new LinkLatency(baseDelay, maxJitter);
            _linkLatencies[(node1, node2)] = latency;
            _linkLatencies[(node2, node1)] = latency;
        }

        OnLinkLatencySet(node1, node2, baseDelay, maxJitter);
        OnLinkLatencySet(node2, node1, baseDelay, maxJitter);
    }

    /// <summary>
    /// Gets the directional per-link latency override, if configured.
    /// </summary>
    /// <returns>The override, or <see langword="null"/> when the link uses the global delay.</returns>
    public LinkLatency? GetLinkLatency(string sourceAddress, string targetAddress)
    {
        lock (_lock)
        {
            return _linkLatencies.TryGetValue((sourceAddress, targetAddress), out var latency) ? latency : null;
        }
    }

    /// <summary>
    /// Removes a directional per-link latency override.
    /// </summary>
    /// <returns><see langword="true"/> when an override was removed; otherwise, <see langword="false"/>.</returns>
    public bool ClearLinkLatency(string sourceAddress, string targetAddress)
    {
        bool removed;
        lock (_lock)
        {
            removed = _linkLatencies.Remove((sourceAddress, targetAddress));
        }

        if (removed)
        {
            OnLinkLatencyCleared(sourceAddress, targetAddress);
        }

        return removed;
    }

    /// <summary>
    /// Removes all per-link latency overrides.
    /// </summary>
    public void ClearAllLinkLatencies()
    {
        int count;
        lock (_lock)
        {
            count = _linkLatencies.Count;
            _linkLatencies.Clear();
        }

        OnAllLinkLatenciesCleared(count);
    }

    /// <summary>
    /// Captures all per-link latency overrides ordered by source and then target address using ordinal comparison.
    /// </summary>
    public IReadOnlyList<LinkLatencyOverride> GetAllLinkLatencies()
    {
        LinkLatencyOverride[] result;
        lock (_lock)
        {
            result = new LinkLatencyOverride[_linkLatencies.Count];
            var index = 0;
            foreach (var ((source, target), latency) in _linkLatencies)
            {
                result[index++] = new LinkLatencyOverride(source, target, latency);
            }
        }

        Array.Sort(result, static (left, right) =>
        {
            var sourceComparison = StringComparer.Ordinal.Compare(left.SourceAddress, right.SourceAddress);
            return sourceComparison != 0
                ? sourceComparison
                : StringComparer.Ordinal.Compare(left.TargetAddress, right.TargetAddress);
        });
        return Array.AsReadOnly(result);
    }

    private TimeSpan GetMessageDelay(TimeSpan baseDelay, TimeSpan maxJitter)
    {
        if (maxJitter == TimeSpan.Zero)
        {
            return baseDelay;
        }

        var jitter = _random.NextTimeSpan(maxJitter);
        return TimeSpan.FromTicks(checked(baseDelay.Ticks + jitter.Ticks));
    }

#pragma warning disable CA1848 // Use the LoggerMessage delegates - these are virtual hooks, override for high-performance logging
#pragma warning disable CA1873 // Logging message template - these are virtual hooks, override for high-performance logging

    /// <summary>Called when a unidirectional partition is created.</summary>
    protected virtual void OnPartitionCreated(string source, string target) => _logger.LogDebug("Partition created: {Source} -> {Target}", source, target);

    /// <summary>Called when a bidirectional partition is about to be created.</summary>
    protected virtual void OnBidirectionalPartitionCreating(string node1, string node2) => _logger.LogDebug("Creating bidirectional partition: {Node1} <-> {Node2}", node1, node2);

    /// <summary>Called when a unidirectional partition is healed.</summary>
    protected virtual void OnPartitionHealed(string source, string target) => _logger.LogDebug("Partition healed: {Source} -> {Target}", source, target);

    /// <summary>Called when a bidirectional partition is about to be healed.</summary>
    protected virtual void OnBidirectionalPartitionHealing(string node1, string node2) => _logger.LogDebug("Healing bidirectional partition: {Node1} <-> {Node2}", node1, node2);

    /// <summary>Called when all partitions are healed.</summary>
    protected virtual void OnAllPartitionsHealed(int count) => _logger.LogDebug("All partitions healed: {Count} partitions", count);

    /// <summary>Called when a node is about to be isolated.</summary>
    protected virtual void OnNodeIsolating(string nodeAddress) => _logger.LogDebug("Isolating node: {Node}", nodeAddress);

    /// <summary>Called when a node is about to be reconnected.</summary>
    protected virtual void OnNodeReconnecting(string nodeAddress) => _logger.LogDebug("Reconnecting node: {Node}", nodeAddress);

    /// <summary>Called when a message is blocked by a partition.</summary>
    protected virtual void OnMessageBlockedByPartition(string source, string target) => _logger.LogTrace("Message blocked by partition: {Source} -> {Target}", source, target);

    /// <summary>Called when a message is randomly dropped.</summary>
    protected virtual void OnMessageDroppedRandom(string source, string target) => _logger.LogTrace("Message randomly dropped: {Source} -> {Target}", source, target);

    /// <summary>Called when a per-link latency override is set.</summary>
    protected virtual void OnLinkLatencySet(string source, string target, TimeSpan baseDelay, TimeSpan maxJitter) =>
        _logger.LogDebug(
            "Link latency set: {Source} -> {Target} (base={BaseDelay}, jitter={MaxJitter})",
            source,
            target,
            baseDelay,
            maxJitter);

    /// <summary>Called when a per-link latency override is cleared.</summary>
    protected virtual void OnLinkLatencyCleared(string source, string target) =>
        _logger.LogDebug("Link latency cleared: {Source} -> {Target}", source, target);

    /// <summary>Called when all per-link latency overrides are cleared.</summary>
    protected virtual void OnAllLinkLatenciesCleared(int count) =>
        _logger.LogDebug("All link latencies cleared: {Count} overrides", count);

#pragma warning restore CA1873, CA1848

    private string DebuggerDisplay
    {
        get
        {
            int linkLatencyCount;
            lock (_lock)
            {
                linkLatencyCount = _linkLatencies.Count;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"SimulationNetwork(Partitions={_partitions.Count}, LinkLatencies={linkLatencyCount}, DropRate={MessageDropRate:P0})");
        }
    }

    private static void ValidateDelay(TimeSpan value, TimeSpan otherDelay, string propertyName)
    {
        ValidateNonnegativeDelay(value, propertyName);

        if (value.Ticks > TimeSpan.MaxValue.Ticks - otherDelay.Ticks)
        {
            ThrowCombinedDelayOutOfRange(propertyName, value);
        }
    }

    private static void ValidateDelayPair(
        TimeSpan baseDelay,
        TimeSpan maxJitter,
        string baseDelayParameterName,
        string maxJitterParameterName)
    {
        ValidateNonnegativeDelay(baseDelay, baseDelayParameterName);
        ValidateNonnegativeDelay(maxJitter, maxJitterParameterName);
        if (baseDelay.Ticks > TimeSpan.MaxValue.Ticks - maxJitter.Ticks)
        {
            ThrowCombinedDelayOutOfRange(baseDelayParameterName, baseDelay);
        }
    }

    private static void ValidateNonnegativeDelay(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Delay values must be nonnegative.");
        }
    }

    private static void ThrowCombinedDelayOutOfRange(string parameterName, TimeSpan value)
    {
        throw new ArgumentOutOfRangeException(
            parameterName,
            value,
            $"{nameof(BaseMessageDelay)} and {nameof(MaxJitter)} must not exceed {nameof(TimeSpan)}.{nameof(TimeSpan.MaxValue)} when combined.");
    }
}
