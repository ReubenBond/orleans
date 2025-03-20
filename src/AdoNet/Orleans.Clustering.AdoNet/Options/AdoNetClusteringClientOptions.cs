// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration;

public class AdoNetClusteringClientOptions
{
    /// <summary>
    /// Connection string for Sql
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }

    /// <summary>
    /// The invariant name of the connector for gatewayProvider's database.
    /// </summary>
    public string Invariant { get; set; }
}
