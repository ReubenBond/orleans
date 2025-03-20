// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Providers;

internal enum ProviderErrorCode
{
    ProvidersBase = 200000,

    MemoryStreamProviderBase                    = ProvidersBase + 400,
    MemoryStreamProviderBase_QueueMessageBatchAsync = MemoryStreamProviderBase + 1,
    MemoryStreamProviderBase_GetQueueMessagesAsync = MemoryStreamProviderBase + 2,
}
