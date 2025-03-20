// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime;

internal record struct TagList(
    string Name1,
    object Value1,
    string Name2 = default,
    object Value2 = default,
    string Name3 = default,
    object Value3 = default,
    string Name4 = default,
    object Value4 = default);
