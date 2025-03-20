// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface IClientAddressableTestClientObject : IGrainObserver
{
    Task<string> OnHappyPath(string message);
    Task OnSadPath(string message);
    Task<int> OnSerialStress(int n);
    Task<int> OnParallelStress(int n);
}
