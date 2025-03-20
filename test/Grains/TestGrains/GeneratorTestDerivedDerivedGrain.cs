// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class GeneratorTestDerivedDerivedGrain : GeneratorTestDerivedGrain2, IGeneratorTestDerivedDerivedGrain
    {
        public Task<string> StringNConcat(string[] strArray)
        {
            string strAll = string.Empty;
            foreach(string str in strArray)
                strAll = string.Concat(strAll, str);

            return Task.FromResult(strAll);
        }

        public Task<string> StringReplace(ReplaceArguments strs)
        {
            myGrainString = myGrainString.Replace(strs.OldString, strs.NewString);
            return Task.FromResult(myGrainString);
        }
    }
}