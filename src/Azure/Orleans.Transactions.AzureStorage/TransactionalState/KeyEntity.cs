// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure;
using Azure.Data.Tables;

namespace Orleans.Transactions.AzureStorage;

internal class KeyEntity : ITableEntity
{
    public const string RK = "k";

    public KeyEntity()
    {
        this.RowKey = RK;
    }

    public long CommittedSequenceId { get; set; }
    public string Metadata { get; set; }
    public string PartitionKey { get; set; }
    public string RowKey { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
