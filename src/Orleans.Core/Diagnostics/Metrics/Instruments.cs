// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

public static class Instruments
{
    public static readonly Meter Meter = new("Microsoft.Orleans");
}
