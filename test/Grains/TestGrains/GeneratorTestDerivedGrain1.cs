// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class GeneratorTestDerivedGrain1 : GeneratorTestGrain, IGeneratorTestDerivedGrain1
{
    public Task<byte[]> ByteAppend(byte[] data)
    {
        byte[] tmp = new byte[myGrainBytes.Length + data.Length];
        myGrainBytes.CopyTo(tmp, 0);
        data.CopyTo(tmp, myGrainBytes.Length);
        myGrainBytes = tmp;
        //RaiseStateUpdateEvent();
        return Task.FromResult(myGrainBytes);
    }
}