// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestExtensions;

namespace Tester.Cosmos;

public class CosmosTestUtils
{
    public static void CheckCosmosStorage()
    {
        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountEndpoint)
            || string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountKey))
        {
            throw new SkipException();
        }
    }
}
