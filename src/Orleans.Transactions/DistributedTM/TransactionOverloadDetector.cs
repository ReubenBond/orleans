using System;
using Microsoft.Extensions.Options;
using Orleans.Internal.Trasactions;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

public interface ITransactionOverloadDetector
{
    bool IsOverloaded();
}

/// <summary>
/// Options for load shedding based on transaction rate 
/// </summary>
public class TransactionRateLoadSheddingOptions
{
    /// <summary>
    /// whether to turn on transaction load shedding. Default to false;
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Default load shedding limit
    /// </summary>
    public const double DEFAULT_LIMIT = 700;

    /// <summary>
    /// Load shedding limit for transaction
    /// </summary>
    public double Limit { get; set; } = DEFAULT_LIMIT;
}

internal sealed class TransactionOverloadDetector : ITransactionOverloadDetector
{
    private static readonly TimeSpan MetricsCheckPeriod = TimeSpan.FromSeconds(15);
    private readonly ITransactionAgentStatistics _statistics;
    private readonly TransactionRateLoadSheddingOptions _options;
    private readonly PeriodicAction _monitor;
    private ITransactionAgentStatistics _lastStatistics;
    private double _transactionStartedPerSecond;
    private DateTime _lastCheckTime;

    public TransactionOverloadDetector(ITransactionAgentStatistics statistics, IOptions<TransactionRateLoadSheddingOptions> options)
    {
        _statistics = statistics;
        _options = options.Value;
        _monitor = new PeriodicAction(MetricsCheckPeriod, RecordStatistics);
        _lastStatistics = TransactionAgentStatistics.Copy(statistics);
        _lastCheckTime = DateTime.UtcNow;
    }

    private void RecordStatistics()
    {
        var current = TransactionAgentStatistics.Copy(_statistics);
        var now = DateTime.UtcNow;

        _transactionStartedPerSecond = CalculateTps(_lastStatistics.TransactionsStarted, _lastCheckTime, current.TransactionsStarted, now);
        _lastStatistics = current;
        _lastCheckTime = now;
    }

    public bool IsOverloaded()
    {
        if (!_options.Enabled)
            return false;

        var now = DateTime.UtcNow;
        _monitor.TryAction(now);
        var txPerSecondCurrently = CalculateTps(_lastStatistics.TransactionsStarted, _lastCheckTime, _statistics.TransactionsStarted, now);
        //decaying utilization for tx per second
        var aggregatedTxPerSecond = (_transactionStartedPerSecond + 2.0 * txPerSecondCurrently) / 3.0;
            
        return aggregatedTxPerSecond > _options.Limit;
    }

    private static double CalculateTps(long startCounter, DateTime startTimeUtc, long currentCounter, DateTime curentTimeUtc)
    {
        var deltaTime = curentTimeUtc - startTimeUtc;
        var deltaCounter = currentCounter - startCounter;
        return (deltaTime.TotalMilliseconds < 1000)
            ? deltaCounter
            : deltaCounter * 1000.0 / deltaTime.TotalMilliseconds;
    }
}