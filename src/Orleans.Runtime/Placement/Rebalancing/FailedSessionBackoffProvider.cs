// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

internal sealed class FailedSessionBackoffProvider(IOptions<ActivationRebalancerOptions> options)
    : FixedBackoff(options.Value.SessionCyclePeriod), IFailedSessionBackoffProvider;