// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Xunit.Abstractions;

public static class TestOutputHelperExtensions
{
    public static void WriteLine(this ITestOutputHelper output, object value)
    {
        output.WriteLine(value.ToString());
    }
}
