// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestExtensions;

public abstract class OrleansTestingBase
{
    public static long GetRandomGrainId() => Random.Shared.Next();
}