// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans;

/// <summary>
/// A <see cref="ILifecycleObservable"/> marker type for client lifecycles.
/// </summary>
public interface IClusterClientLifecycle : ILifecycleObservable
{
}
