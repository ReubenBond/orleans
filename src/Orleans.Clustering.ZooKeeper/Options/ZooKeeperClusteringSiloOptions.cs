// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration;

/// <summary>
/// Option to configure ZooKeeperMembership
/// </summary>
public class ZooKeeperClusteringSiloOptions
{
    /// <summary>
    /// Connection string for ZooKeeper Storage
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }
}
