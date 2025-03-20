// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface IObserverGrain : IGrainWithIntegerKey
{
    Task SetTarget(ISimpleObserverableGrain target);
    Task Subscribe(ISimpleGrainObserver observer);
}
