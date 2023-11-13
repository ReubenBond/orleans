using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStreamingHistoryGrain")]
    public interface IStreamingHistoryGrain : IGrainWithStringKey
    {
        [Alias("BecomeConsumer")]
        Task BecomeConsumer(StreamId streamId, string provider, string filterData = null);

        [Alias("StopBeingConsumer")]
        Task StopBeingConsumer();

        [Alias("GetReceivedItems")]
        Task<List<int>> GetReceivedItems();
    }
}