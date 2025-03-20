// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;

namespace Orleans.Transactions.Abstractions;

public interface ITransactionManagerExtension : IGrainExtension
{
    [AlwaysInterleave]
    [Transaction(TransactionOption.Suppress)]
    Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalParticipants);

    [AlwaysInterleave]
    [Transaction(TransactionOption.Suppress)]
    [OneWay]
    Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status);

    [AlwaysInterleave]
    [Transaction(TransactionOption.Suppress)]
    [OneWay]
    Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource);
}
