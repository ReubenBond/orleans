// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.FSharpInterfaces;

namespace UnitTests.GrainInterfaces;

// uncomment the following interface definition to reproduce #1349

public interface IGeneratorTestDerivedFromFSharpInterfaceInExternalAssemblyGrain : IGrainWithGuidKey, IFSharpBaseInterface
{
}
