// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;

namespace Orleans.Runtime;

internal interface IDeploymentLoadPublisher : ISystemTarget
{
    [OneWay]
    Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats);
}
