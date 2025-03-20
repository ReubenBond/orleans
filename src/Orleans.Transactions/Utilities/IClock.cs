// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions;

/// <summary>
/// System clock abstraction
/// </summary>
public interface IClock
{
    /// <summary>
    /// Current time in utc
    /// </summary>
    /// <returns></returns>
    DateTime UtcNow();
}
