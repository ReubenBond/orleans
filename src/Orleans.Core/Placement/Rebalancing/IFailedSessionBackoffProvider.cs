// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Internal;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Determines how long to wait between successive rebalancing sessions, if an aprior session has failed.
/// </summary>
/// <remarks>
/// A session is considered "failed" if n-consecutive number of cycles yielded no significant improvement
/// to the cluster's entropy.
/// </remarks>
public interface IFailedSessionBackoffProvider : IBackoffProvider { }