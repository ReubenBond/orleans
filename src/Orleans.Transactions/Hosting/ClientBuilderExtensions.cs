// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Hosting;

public static class ClientBuilderExtensions
{
    public static IClientBuilder UseTransactions(this IClientBuilder builder)
        => builder.ConfigureServices(services => services.UseTransactionsWithClient());
}