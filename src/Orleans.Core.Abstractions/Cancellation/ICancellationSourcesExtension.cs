// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;

namespace Orleans.Runtime;

/// <summary>
/// Extension used by the grain cancellation runtime to propagate cancellation notifications to grains.
/// </summary>
internal interface ICancellationSourcesExtension : IGrainExtension
{
    /// <summary>
    /// Indicates that a cancellation token has been canceled.
    /// </summary>
    /// <param name="tokenId">
    /// The token id.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> representing the operation.
    /// </returns>
    [AlwaysInterleave]
    Task CancelRemoteToken(Guid tokenId);
}