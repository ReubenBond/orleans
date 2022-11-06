using GPSTracker.Common;
using Orleans;

namespace GPSTracker.GrainInterface;

public interface IDeviceGrain : IGrainWithIntegerKey
{
    ValueTask ProcessMessage(DeviceMessage message);
}
