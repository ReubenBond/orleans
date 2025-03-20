// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.FSharpGrains;
using UnitTests.FSharpInterfaces;

[assembly: GenerateCodeForDeclaringAssembly(typeof(Generic1ArgumentGrain<>))]

namespace UnitTests.GrainInterfaces
{
    public interface IFSharpParametersGrain<T,U> : IGrainWithGuidKey, IFSharpParameters<T>
    {
    }
}
