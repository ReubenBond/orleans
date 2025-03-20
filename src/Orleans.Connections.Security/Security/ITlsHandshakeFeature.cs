// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Authentication;

namespace Orleans.Connections.Security
{
    public interface ITlsHandshakeFeature
    {
        SslProtocols Protocol { get; }

        CipherAlgorithmType CipherAlgorithm { get; }

        int CipherStrength { get; }

        HashAlgorithmType HashAlgorithm { get; }

        int HashStrength { get; }

        ExchangeAlgorithmType KeyExchangeAlgorithm { get; }

        int KeyExchangeStrength { get; }
    }
}
