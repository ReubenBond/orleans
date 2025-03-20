// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime;

/// <summary>
/// Marker interface for addressable endpoints, such as grains, observers, and other system-internal addressable endpoints
/// </summary>
[GenerateMethodSerializers(typeof(GrainReference))]
public interface IAddressable
{
}
