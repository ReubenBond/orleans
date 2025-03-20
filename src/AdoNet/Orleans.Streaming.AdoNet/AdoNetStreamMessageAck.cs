// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Represents an acknowledgement from storage that a message was enqueued.
/// This is used to surface any generated identifiers for testing.
/// </summary>
internal record AdoNetStreamMessageAck(
    string ServiceId,
    string ProviderId,
    string QueueId,
    long MessageId)
{
    public AdoNetStreamMessageAck() : this("", "", "", 0)
    {
    }
}