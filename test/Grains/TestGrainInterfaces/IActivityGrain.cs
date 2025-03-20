// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IActivityGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
    }

    [GenerateSerializer]
    public class ActivityData
    {
        [Id(0)]
        public string Id { get; set; }

        [Id(1)]
        public string TraceState { get; set; }

        [Id(2)]
        public List<KeyValuePair<string, string>> Baggage { get; set; }
    }
}
