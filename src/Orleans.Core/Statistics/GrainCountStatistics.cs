// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime;

/// <summary>
/// Centralized statistics on per-grain-type activation counts.
/// </summary>
internal class GrainCountStatistics
{
    public IEnumerable<KeyValuePair<string, long>> GetSimpleGrainStatistics()
    {
        return GrainMetricsListener
            .GrainCounts
            .Select(s => new KeyValuePair<string, long>(s.Key, s.Value))
            .Where(p => p.Value > 0);
    }
}
