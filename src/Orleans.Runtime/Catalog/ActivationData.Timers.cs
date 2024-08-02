#nullable enable

namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    void IGrainTimerRegistry.OnTimerCreated(IGrainTimer timer)
    {
        lock (this)
        {
            Timers ??= [];
            Timers.Add(timer);
        }
    }

    void IGrainTimerRegistry.OnTimerDisposed(IGrainTimer timer)
    {
        lock (this) // need to lock since dispose can be called on finalizer thread, outside grain context (not single threaded).
        {
            if (Timers is null)
            {
                return;
            }

            Timers.Remove(timer);
        }
    }

    private void DisposeTimers()
    {
        lock (this)
        {
            if (Timers is null)
            {
                return;
            }

            // Need to set Timers to null since OnTimerDisposed mutates the timers set if it is not null.
            var timers = Timers;
            Timers = null;

            // Dispose all timers.
            foreach (var timer in timers)
            {
                timer.Dispose();
            }
        }
    }
}
