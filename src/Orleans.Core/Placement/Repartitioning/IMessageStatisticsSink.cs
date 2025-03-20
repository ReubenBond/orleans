// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
namespace Orleans.Placement.Repartitioning;

internal interface IMessageStatisticsSink
{
    Action<Message>? GetMessageObserver();
}

internal sealed class NoOpMessageStatisticsSink : IMessageStatisticsSink
{
    public Action<Message>? GetMessageObserver() => null;
}