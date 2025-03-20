// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration;

public class ZooKeeperGatewayListProviderOptions
{
    /// <summary>
    /// Connection string for ZooKeeper storage
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }
}
