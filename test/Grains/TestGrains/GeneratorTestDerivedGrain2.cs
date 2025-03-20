// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GeneratorTestDerivedGrain2 : GeneratorTestGrain, IGeneratorTestDerivedGrain2
    {
        public Task<string> StringConcat(string str1, string str2, string str3)
        {
            return Task.FromResult((string.Concat(str1, str2, str3)));
        }
    }
}