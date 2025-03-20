// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions.Abstractions;

public interface ITransactionAgentStatistics
{
    void TrackTransactionStarted();
    long TransactionsStarted { get; }

    void TrackTransactionSucceeded();
    long TransactionsSucceeded { get; }

    void TrackTransactionFailed();
    long TransactionsFailed { get; }

    void TrackTransactionThrottled();
    long TransactionsThrottled { get; }
}
