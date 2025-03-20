// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface IFirstGrain : IGrainWithGuidKey
{
    Task Start(Guid guid1, Guid guid2);
}

public interface ISecondGrain : IGrainWithGuidKey
{
    Task SecondGrainMethod(Guid guid);
}

public interface IThirdGrain : IGrainWithStringKey
{
    Task ThirdGrainMethod(Guid userId);
}
