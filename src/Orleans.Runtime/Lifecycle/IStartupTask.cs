// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime;

/// <summary>
/// Defines an action to be taken after silo startup.
/// </summary>
public interface IStartupTask
{
    /// <summary>
    /// Called after the silo has started.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token which is canceled when the method must abort.</param>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    Task Execute(CancellationToken cancellationToken);
}
