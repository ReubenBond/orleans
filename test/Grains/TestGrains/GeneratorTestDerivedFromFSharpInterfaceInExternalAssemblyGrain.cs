// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

// uncomment the following class to verify correct code generation for #1349
// (do so once code generation succeeds)
// NOTE: also uncomment the corresponding test in Tester/GeneratorGrainTests.cs

public class GeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain : Grain, IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain
{
    public Task<int> Echo(int x)
    {
        return Task.FromResult(x);
    }

    public Task<Tuple<string, int>> MultipleParameterEcho(string s, int x)
    {
        return Task.FromResult(new Tuple<string,int>(s,x));
    }
}
