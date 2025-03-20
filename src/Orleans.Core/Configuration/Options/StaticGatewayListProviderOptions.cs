// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration;

/// <summary>
/// Options for configuring a static list of gateways.
/// </summary>
/// <remarks>>
/// See <see cref="ClientBuilderExtensions.UseStaticClustering(IClientBuilder, System.Net.IPEndPoint[])"/> for more information.
/// </remarks>
public class StaticGatewayListProviderOptions
{
    /// <summary>
    /// Gets or sets the list of gateway addresses.
    /// </summary>
    public List<Uri> Gateways { get; set; } = new List<Uri>();
}
