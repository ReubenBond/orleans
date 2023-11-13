using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    [Alias("Orleans.ISiloControl")]
    internal interface ISiloControl : ISystemTarget, IVersionManager
    {
        [Alias("Ping")]
        Task Ping(string message);

        [Alias("ForceGarbageCollection")]
        Task ForceGarbageCollection();
        [Alias("ForceActivationCollection")]
        Task ForceActivationCollection(TimeSpan ageLimit);
        [Alias("ForceRuntimeStatisticsCollection")]
        Task ForceRuntimeStatisticsCollection();

        [Alias("GetRuntimeStatistics")]
        Task<SiloRuntimeStatistics> GetRuntimeStatistics();
        [Alias("GetGrainStatistics")]
        Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics();
        [Alias("GetDetailedGrainStatistics")]
        Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[] types = null);
        [Alias("GetSimpleGrainStatistics")]
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics();
        [Alias("GetDetailedGrainReport")]
        Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId);

        [Alias("GetActivationCount")]
        Task<int> GetActivationCount();

        [Alias("SendControlCommandToProvider")]
        Task<object> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg);
        [Alias("GetActiveGrains")]
        Task<List<GrainId>> GetActiveGrains(GrainType grainType);
    }
}
