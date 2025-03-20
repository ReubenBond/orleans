// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime;

/// <summary>
/// Marker interface for grain extensions, used by internal runtime extension endpoints.
/// </summary>
[GenerateMethodSerializers(typeof(GrainReference), isExtension: true)]
public interface IGrainExtension : IAddressable
{
}
