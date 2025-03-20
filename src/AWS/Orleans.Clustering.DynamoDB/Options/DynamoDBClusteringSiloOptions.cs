// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration;

public class DynamoDBClusteringSiloOptions
{
    /// <summary>
    /// Connection string for DynamoDB Storage
    /// </summary>
    [RedactConnectionString]
    public string ConnectionString { get; set; }
}
