// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime;

/// <summary>
/// The placement strategy used by system targets.
/// </summary>
[GenerateSerializer, Immutable, SuppressReferenceTracking]
public sealed class SystemTargetPlacementStrategy : PlacementStrategy
{
    public static SystemTargetPlacementStrategy Instance { get; } = new();

    public override bool IsUsingGrainDirectory => false;
}
