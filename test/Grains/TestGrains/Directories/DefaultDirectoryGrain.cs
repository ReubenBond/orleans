// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces.Directories;

namespace UnitTests.Grains.Directories;

[GrainType(DIRECTORY)]
public class DefaultDirectoryGrain : Grain, IDefaultDirectoryGrain
{
    private int counter = 0;

    public const string DIRECTORY = "Default";

    public Task<int> Ping() => Task.FromResult(++this.counter);

    public Task Reset()
    {
        counter = 0;
        return Task.CompletedTask;
    }

    public Task<string> GetRuntimeInstanceId()
    {
        return Task.FromResult(this.RuntimeIdentity);
    }

    public Task<int> ProxyPing(ICommonDirectoryGrain grain)
    {
        return grain.Ping();
    }
}
