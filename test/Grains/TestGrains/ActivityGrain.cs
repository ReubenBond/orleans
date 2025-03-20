// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class ActivityGrain : IActivityGrain
{
    public Task<ActivityData> GetActivityId()
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return Task.FromResult(default(ActivityData));
        }

        var result = new ActivityData()
        {
            Id = activity.Id,
            TraceState = activity.TraceStateString,
            Baggage = activity.Baggage.ToList(),
        };

        return Task.FromResult(result);
    }
}
