using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface ITestGrain : IGrainWithIntegerKey
    {
        // duplicate to verify identity
        Task<long> GetKey();

        // separate label that can be set
        Task<string> GetLabel();

        Task SetLabel(string label);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetActivationId();

        Task<ITestGrain> GetGrainReference();

        Task<Tuple<string, string>> TestRequestContext();

        Task<IGrain[]> GetMultipleGrainInterfaces_Array();

        Task<List<IGrain>> GetMultipleGrainInterfaces_List();

        Task StartTimer();

        Task DoLongAction(TimeSpan timespan, string str);
    }

    public interface IGuidTestGrain : IGrainWithGuidKey
    {
        // duplicate to verify identity
        Task<Guid> GetKey();

        // separate label that can be set
        Task<string> GetLabel();

        Task SetLabel(string label);

        Task<string> GetRuntimeInstanceId();

        Task<string> GetActivationId();
    }

    public interface IOneWayGrain : IGrainWithGuidKey
    {
        [OneWay]
        Task Notify(ISimpleGrainObserver observer);

        [OneWay]
        Task ThrowsOneWay();

        Task<bool> NotifyOtherGrain(IOneWayGrain otherGrain, ISimpleGrainObserver observer);

        Task<IOneWayGrain> GetOtherGrain();

        [AlwaysInterleave]
        Task<bool> NotifyOtherGrain();

        Task<int> GetCount();

        Task Deactivate();

        Task<string> GetActivationId();

        Task<SiloAddress> GetSiloAddress();
        Task<SiloAddress> GetPrimaryForGrain();
    }

    public interface ICanBeOneWayGrain : IGrainWithGuidKey
    {
        Task Notify(ISimpleGrainObserver observer);

        Task Throws();

        Task<int> GetCount();
    }
}
