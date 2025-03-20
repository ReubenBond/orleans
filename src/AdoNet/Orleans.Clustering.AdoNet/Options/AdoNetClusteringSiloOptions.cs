// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration;

/// <summary>
/// Options for ADO.NET clustering
/// </summary>
public class AdoNetClusteringSiloOptions
{
    /// <summary>
    /// Connection string for AdoNet Storage
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }

    /// <summary>
    /// The invariant name of the connector for membership's database.
    /// </summary>
    public string Invariant { get; set; }
}
