// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Services;

/// <summary>
/// Base interface for grain service clients.
/// </summary>
/// <typeparam name="TGrainService">The grain service interface type.</typeparam>
public interface IGrainServiceClient<TGrainService>
    where TGrainService : IGrainService
{
}