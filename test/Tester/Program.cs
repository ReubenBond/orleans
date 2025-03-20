// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.TestingHost;

namespace Tester;

public static class Program 
{
    public static async Task Main(string[] args) => await StandaloneSiloHost.Main(args);
}
