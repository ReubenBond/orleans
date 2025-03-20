// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Repartitioning;

namespace UnitTests.ActivationRepartitioningTests;

/// <summary>
/// Ignores client messages to make testing easier
/// </summary>
internal sealed class TestMessageFilter(GrainMigratabilityChecker checker) : IRepartitionerMessageFilter
{
    private readonly RepartitionerMessageFilter _messageFilter = new(checker);

    public bool IsAcceptable(Message message, out bool isSenderMigratable, out bool isTargetMigratable) =>
        _messageFilter.IsAcceptable(message, out isSenderMigratable, out isTargetMigratable) &&
        !message.SendingGrain.IsClient() && !message.TargetGrain.IsClient();
}