// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans;

/// <summary>
/// The internal-facing client interface.
/// </summary>
internal interface IInternalClusterClient : IClusterClient, IInternalGrainFactory
{
}