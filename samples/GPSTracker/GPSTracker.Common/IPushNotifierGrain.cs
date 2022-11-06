using GPSTracker.Common;
using Orleans;

namespace GPSTracker.GrainInterface;

public interface IPushNotifierGrain : IGrainWithIntegerKey
{
    ValueTask SendMessage(VelocityMessage message);
}
