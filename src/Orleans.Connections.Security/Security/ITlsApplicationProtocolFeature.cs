// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Connections.Security;

public interface ITlsApplicationProtocolFeature
{
    ReadOnlyMemory<byte> ApplicationProtocol { get; }
}
