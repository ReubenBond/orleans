using GPSTracker.Common;
using Orleans;

namespace GPSTracker;

public interface IRemoteLocationHub : IGrainObserver
{
    ValueTask BroadcastUpdates(VelocityBatch messages);
}
