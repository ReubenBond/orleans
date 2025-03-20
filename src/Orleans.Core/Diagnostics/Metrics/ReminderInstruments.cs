// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class ReminderInstruments
{
    public static Histogram<double> TardinessSeconds = Instruments.Meter.CreateHistogram<double>(InstrumentNames.REMINDERS_TARDINESS, "seconds");
    public static ObservableGauge<int> ActiveReminders;
    public static void RegisterActiveRemindersObserve(Func<int> observeValue)
    {
        ActiveReminders = Instruments.Meter.CreateObservableGauge(InstrumentNames.REMINDERS_NUMBER_ACTIVE_REMINDERS, observeValue);
    }

    public static Counter<int> TicksDelivered = Instruments.Meter.CreateCounter<int>(InstrumentNames.REMINDERS_COUNTERS_TICKS_DELIVERED);

}
