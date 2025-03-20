// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace BenchmarkGrainInterfaces.Ping;

public interface ITreeGrain : IGrainWithIntegerCompoundKey
{
    public ValueTask Ping();
}

