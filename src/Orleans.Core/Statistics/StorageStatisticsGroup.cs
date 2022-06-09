using System;

namespace Orleans.Runtime
{
    internal class StorageStatisticsGroup
    {
        internal static CounterStatistic StorageReadTotal;
        internal static CounterStatistic StorageWriteTotal;
        internal static CounterStatistic StorageActivateTotal;
        internal static CounterStatistic StorageClearTotal;
        internal static CounterStatistic StorageReadErrors;
        internal static CounterStatistic StorageWriteErrors;
        internal static CounterStatistic StorageActivateErrors;
        internal static CounterStatistic StorageClearErrors;
        internal static AverageTimeSpanStatistic StorageReadLatency;
        internal static AverageTimeSpanStatistic StorageWriteLatency;
        internal static AverageTimeSpanStatistic StorageClearLatency;

        internal static void Init()
        {
            StorageReadTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_READ_TOTAL);
            StorageWriteTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_WRITE_TOTAL);
            StorageActivateTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_ACTIVATE_TOTAL);
            StorageReadErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_READ_ERRORS);
            StorageWriteErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_WRITE_ERRORS);
            StorageActivateErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_ACTIVATE_ERRORS);
            StorageReadLatency = AverageTimeSpanStatistic.FindOrCreate(StatisticNames.STORAGE_READ_LATENCY);
            StorageWriteLatency = AverageTimeSpanStatistic.FindOrCreate(StatisticNames.STORAGE_WRITE_LATENCY);
            StorageClearTotal = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_CLEAR_TOTAL);
            StorageClearErrors = CounterStatistic.FindOrCreate(StatisticNames.STORAGE_CLEAR_ERRORS);
            StorageClearLatency = AverageTimeSpanStatistic.FindOrCreate(StatisticNames.STORAGE_CLEAR_LATENCY);
        }

        internal static void OnStorageRead(string stateName, GrainId grain, TimeSpan latency)
        {
            StorageReadTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageReadLatency.AddSample(latency);
            }
        }

        internal static void OnStorageWrite(string stateName, GrainId grain, TimeSpan latency)
        {
            StorageWriteTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageWriteLatency.AddSample(latency);
            }
        }
        
        internal static void OnStorageActivate(string stateName, TimeSpan latency)
        {
            StorageActivateTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageReadLatency.AddSample(latency);
            }
        }

        internal static void OnStorageReadError(string stateName, GrainId grain)
        {
            StorageReadErrors.Increment();
        }
        
        internal static void OnStorageWriteError(string stateName, GrainId grain)
        {
            StorageWriteErrors.Increment();
        }

        internal static void OnStorageActivateError(string grainType)
        {
            StorageActivateErrors.Increment();
        }

        internal static void OnStorageDelete(string grainType, GrainId grain, TimeSpan latency)
        {
            StorageClearTotal.Increment();
            if (latency > TimeSpan.Zero)
            {
                StorageClearLatency.AddSample(latency);
            }
        }

        internal static void OnStorageDeleteError(string grainType, GrainId grain)
        {
            StorageClearErrors.Increment();
        }
    }
}
