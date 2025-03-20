// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime;

internal class HistogramBucketAggregator
{
    private long _value = 0;
    private readonly KeyValuePair<string, object>[] _tags;
    public long Bound { get; }

    public HistogramBucketAggregator(KeyValuePair<string, object>[] tags, long bound, KeyValuePair<string, object> label)
    {
        _tags = tags.Concat(new[] { label }).ToArray();
        Bound = bound;
    }

    public ReadOnlySpan<KeyValuePair<string, object>> Tags => _tags;

    public long Value => _value;

    public void Add(long measurement) => Interlocked.Add(ref _value, measurement);

    public Measurement<long> Collect()
    {
        return new Measurement<long>(_value, _tags);
    }
}
