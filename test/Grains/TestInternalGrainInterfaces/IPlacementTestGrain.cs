// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    using System.Threading.Tasks;

    using Orleans;
    using Orleans.Runtime;

    internal interface IDefaultPlacementGrain : IGrainWithIntegerKey
    {
        Task<PlacementStrategy> GetDefaultPlacement();
    }
}
