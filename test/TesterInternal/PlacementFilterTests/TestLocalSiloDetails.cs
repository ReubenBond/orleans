// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.PlacementFilterTests;

internal class TestLocalSiloDetails : ILocalSiloDetails
{
    public TestLocalSiloDetails(string name, string clusterId, string dnsHostName, SiloAddress siloAddress, SiloAddress gatewayAddress)
    {
        Name = name;
        ClusterId = clusterId;
        DnsHostName = dnsHostName;
        SiloAddress = siloAddress;
        GatewayAddress = gatewayAddress;
    }

    public string Name { get; }
    public string ClusterId { get; }
    public string DnsHostName { get; }
    public SiloAddress SiloAddress { get; }
    public SiloAddress GatewayAddress { get; }
}