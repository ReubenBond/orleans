using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Placement.Rebalancing;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal interface IActivationRebalancerReportReceiver
{
    void ReceiveReport(RebalancingReport report);
}

// The rebalancer is the only writer and every report fully supersedes its predecessor.
internal sealed class ActivationRebalancerReportDisseminationNamespace(
    IActivationRebalancerReportReceiver receiver,
    IOptions<ActivationRebalancerOptions> options,
    TimeProvider timeProvider,
    Serializer serializer) : IDisseminationNamespace
{
    private const string ReportKey = "report";
    private readonly object _lock = new();
    private DisseminationValue _value;
    private long _version;

    public DisseminationNamespace Name => DisseminationNamespaceNames.RebalancingReport;

    public DisseminationNamespaceOptions Options => options.Value.ReportDissemination;

    public IEnumerable<DigestEntry> Digests
    {
        get
        {
            long version;
            lock (_lock)
            {
                version = _version;
            }

            if (version > 0)
            {
                yield return new DigestEntry(ReportKey, version);
            }
        }
    }

    public async ValueTask<bool> PublishAsync(
        IDisseminationService disseminationService,
        RebalancingReport report,
        CancellationToken cancellationToken)
    {
        long version;
        lock (_lock)
        {
            if (_version == long.MaxValue)
            {
                throw new InvalidOperationException("The activation rebalancer report version is exhausted.");
            }

            version = Math.Max(_version + 1, timeProvider.GetUtcNow().Ticks);
            if (version <= 0)
            {
                version = 1;
            }

            _version = version;
            _value = new DisseminationValue(
                ReportKey,
                fromVersion: 0,
                toVersion: version,
                serializer.SerializeToArray(report));
        }

        receiver.ReceiveReport(report);
        return await disseminationService.Publish(this, ReportKey, version, cancellationToken);
    }

    public long GetVersion(DisseminationKey key)
    {
        if (!IsReportKey(key))
        {
            return 0;
        }

        lock (_lock)
        {
            return _version;
        }
    }

    public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request)
    {
        if (!IsReportKey(request.Key))
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        DisseminationValue value;
        long version;
        lock (_lock)
        {
            value = _value;
            version = _version;
        }

        if (version == 0)
        {
            return DisseminationRepairResult.Unavailable(version: 0);
        }

        if (request.ToVersion is { } targetVersion && targetVersion != version)
        {
            return DisseminationRepairResult.Unavailable(version);
        }

        if (request.FromVersion is { } peerVersion && peerVersion >= version)
        {
            return DisseminationRepairResult.Current(version);
        }

        if (request.MaxItemCount <= 0
            || value.Payload.Length > request.MaxPayloadBytes
            || value.Payload.Length > request.MaxBatchBytes)
        {
            return DisseminationRepairResult.InsufficientCapacity(version);
        }

        return DisseminationRepairResult.Produced(version, [value]);
    }

    public ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (!IsReportKey(value.Key) || value.FromVersion != 0 || value.ToVersion <= 0)
        {
            return ValueTask.FromResult(DisseminationApplyResult.Rejected);
        }

        var report = serializer.Deserialize<RebalancingReport>(value.Payload);
        DisseminationApplyResult result;
        lock (_lock)
        {
            if (value.ToVersion < _version)
            {
                result = DisseminationApplyResult.Obsolete;
            }
            else if (value.ToVersion == _version)
            {
                result = DisseminationApplyResult.Duplicate;
            }
            else
            {
                _version = value.ToVersion;
                _value = value;
                result = DisseminationApplyResult.Applied;
            }
        }

        if (result == DisseminationApplyResult.Applied)
        {
            receiver.ReceiveReport(report);
        }

        return ValueTask.FromResult(result);
    }

    private static bool IsReportKey(DisseminationKey key) =>
        key.Value is string value && string.Equals(value, ReportKey, StringComparison.Ordinal);
}
