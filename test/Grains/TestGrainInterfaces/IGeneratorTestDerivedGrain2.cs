// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IGeneratorTestDerivedGrain2 : IGeneratorTestGrain
    {
        Task<string> StringConcat(string str1, string str2, string str3);
    }
}