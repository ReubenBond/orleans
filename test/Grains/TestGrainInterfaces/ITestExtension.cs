// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface ITestExtension : IGrainExtension
{
    Task<string> CheckExtension_1();

    Task<string> CheckExtension_2();
}

public interface IGenericTestExtension<T> : IGrainExtension
{
    Task<T> CheckExtension_1();

    Task<string> CheckExtension_2();
}

public interface ISimpleExtension : IGrainExtension
{
    Task<string> CheckExtension_1();
}

public interface IAutoExtension : IGrainExtension
{
    Task<string> CheckExtension();
}