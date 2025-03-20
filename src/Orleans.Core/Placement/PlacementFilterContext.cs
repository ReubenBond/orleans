// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Runtime;

#nullable enable
namespace Orleans.Placement;

public readonly record struct PlacementFilterContext(GrainType GrainType, GrainInterfaceType InterfaceType, ushort InterfaceVersion);