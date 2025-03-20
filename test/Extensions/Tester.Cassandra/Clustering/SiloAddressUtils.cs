// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;

namespace Tester.Cassandra.Clustering;

public static class SiloAddressUtils
{
    private static readonly IPEndPoint s_localEndpoint = new(IPAddress.Loopback, 0);

    public static SiloAddress NewLocalSiloAddress(int gen)
    {
        return SiloAddress.New(s_localEndpoint, gen);
    }
}