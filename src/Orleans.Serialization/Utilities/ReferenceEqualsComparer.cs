// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Utilities;

internal sealed class ReferenceEqualsComparer : IEqualityComparer<object>, IEqualityComparer
{
    public static ReferenceEqualsComparer Default { get; } = new();

    public new bool Equals(object x, object y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => obj is null ? 0 : RuntimeHelpers.GetHashCode(obj);
}