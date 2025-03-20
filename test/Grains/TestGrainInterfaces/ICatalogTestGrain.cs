// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface ICatalogTestGrain : IGrainWithIntegerKey
{
    Task Initialize();
    Task BlastCallNewGrains(int nGrains, long startingKey, int nCallsToEach);
}
