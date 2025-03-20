// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Tester.AzureUtils.Streaming;

public static class AzureQueueUtilities
{
    public static List<string> GenerateQueueNames(string queueNamePrefix, int queueCount)
    {
        return Enumerable.Range(0, queueCount).Select(num => $"{queueNamePrefix}-{num}").ToList();
    }
}
