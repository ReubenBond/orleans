// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;

namespace Orleans.Connections.Security;

internal static class OrleansApplicationProtocol
{
    public static readonly SslApplicationProtocol Orleans1 = new SslApplicationProtocol("Orleans1");
}
