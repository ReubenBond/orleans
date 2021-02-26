using System;
using System.Collections.Generic;

using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Snapshot of current runtime statistics for a silo
    /// </summary>
    [Serializable]
    [Hagar.GenerateSerializer]
    public class SiloRuntimeStatistics
    {
        /// <summary>
        /// Total number of activations in a silo.
        /// </summary>
        [Hagar.Id(1)]
        public int ActivationCount { get; internal set; }

        /// <summary>
        /// Number of activations in a silo that have been recently used.
        /// </summary>
        [Hagar.Id(2)]
        public int RecentlyUsedActivationCount { get; internal set; }

        /// <summary>
        /// The size of the sending queue.
        /// </summary>
        [Hagar.Id(3)]
        public int SendQueueLength { get; internal set; }

        /// <summary>
        /// The size of the receiving queue.
        /// </summary>
        [Hagar.Id(4)]
        public int ReceiveQueueLength { get; internal set; }

        /// <summary>
        /// The CPU utilization.
        /// </summary>
        [Hagar.Id(5)]
        public float? CpuUsage { get; internal set; }

        /// <summary>
        /// The amount of memory available in the silo [bytes].
        /// </summary>
        [Hagar.Id(6)]
        public float? AvailableMemory { get; internal set; }

        /// <summary>
        /// The used memory size.
        /// </summary>
        [Hagar.Id(7)]
        public long? MemoryUsage { get; internal set; }

        /// <summary>
        /// The total physical memory available [bytes].
        /// </summary>
        [Hagar.Id(8)]
        public long? TotalPhysicalMemory { get; internal set; }

        /// <summary>
        /// Is this silo overloaded.
        /// </summary>
        [Hagar.Id(9)]
        public bool IsOverloaded { get; internal set; }

        /// <summary>
        /// The number of clients currently connected to that silo.
        /// </summary>
        [Hagar.Id(10)]
        public long ClientCount { get; internal set; }

        [Hagar.Id(11)]
        public long ReceivedMessages { get; internal set; }

        [Hagar.Id(12)]
        public long SentMessages { get; internal set; }


        /// <summary>
        /// The DateTime when this statistics was created.
        /// </summary>
        [Hagar.Id(13)]
        public DateTime DateTime { get; private set; }

        internal SiloRuntimeStatistics() { }

        internal SiloRuntimeStatistics(
            IMessageCenter messageCenter,
            int activationCount,
            int recentlyUsedActivationCount,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            DateTime dateTime)
        {
            ActivationCount = activationCount;
            RecentlyUsedActivationCount = recentlyUsedActivationCount;
            SendQueueLength = messageCenter.SendQueueLength;
            CpuUsage = hostEnvironmentStatistics.CpuUsage;
            AvailableMemory = hostEnvironmentStatistics.AvailableMemory;
            MemoryUsage = appEnvironmentStatistics.MemoryUsage;
            IsOverloaded = loadSheddingOptions.Value.LoadSheddingEnabled && this.CpuUsage > loadSheddingOptions.Value.LoadSheddingLimit;
            ClientCount = MessagingStatisticsGroup.ConnectedClientCount.GetCurrentValue();
            TotalPhysicalMemory = hostEnvironmentStatistics.TotalPhysicalMemory;
            ReceivedMessages = MessagingStatisticsGroup.MessagesReceived.GetCurrentValue();
            SentMessages = MessagingStatisticsGroup.MessagesSentTotal.GetCurrentValue();
            DateTime = dateTime;
        }

        public override string ToString()
        {
            return
                "SiloRuntimeStatistics: "
                + $"ActivationCount={ActivationCount} " 
                + $"RecentlyUsedActivationCount={RecentlyUsedActivationCount} "
                + $"SendQueueLength={SendQueueLength} "
                + $"CpuUsage={CpuUsage} "
                + $"AvailableMemory={AvailableMemory} "
                + $"MemoryUsage={MemoryUsage} "
                + $"IsOverloaded={IsOverloaded} "
                + $"ClientCount={ClientCount} "
                + $"TotalPhysicalMemory={TotalPhysicalMemory} "
                + $"DateTime={DateTime}";
        }
    }

    /// <summary>
    /// Snapshot of current statistics for a given grain type.
    /// </summary>
    [Serializable]
    [Hagar.GenerateSerializer]
    internal class GrainStatistic
    {
        /// <summary>
        /// The type of the grain for this GrainStatistic.
        /// </summary>
        [Hagar.Id(1)]
        public string GrainType { get; set; }

        /// <summary>
        /// Number of grains of a this type.
        /// </summary>
        [Hagar.Id(2)]
        public int GrainCount { get; set; }

        /// <summary>
        /// Number of activation of a grain of this type.
        /// </summary>
        [Hagar.Id(3)]
        public int ActivationCount { get; set; }

        /// <summary>
        /// Number of silos that have activations of this grain type.
        /// </summary>
        [Hagar.Id(4)]
        public int SiloCount { get; set; }

        /// <summary>
        /// Returns the string representation of this GrainStatistic.
        /// </summary>
        public override string ToString()
        {
            return string.Format("GrainStatistic: GrainType={0} NumSilos={1} NumGrains={2} NumActivations={3} ", GrainType, SiloCount, GrainCount, ActivationCount);
        }
    }

    /// <summary>
    /// Simple snapshot of current statistics for a given grain type on a given silo.
    /// </summary>
    [Serializable]
    [Hagar.GenerateSerializer]
    public class SimpleGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this SimpleGrainStatistic.
        /// </summary>
        [Hagar.Id(1)]
        public string GrainType { get; set; }

        /// <summary>
        /// The silo address for this SimpleGrainStatistic.
        /// </summary>
        [Hagar.Id(2)]
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// The number of activations of this grain type on this given silo.
        /// </summary>
        [Hagar.Id(3)]
        public int ActivationCount { get; set; }

        /// <summary>
        /// Returns the string representation of this SimpleGrainStatistic.
        /// </summary>
        public override string ToString()
        {
            return string.Format("SimpleGrainStatistic: GrainType={0} Silo={1} NumActivations={2} ", GrainType, SiloAddress, ActivationCount);
        }
    }

    [Serializable]
    [Hagar.GenerateSerializer]
    public class DetailedGrainStatistic
    {
        /// <summary>
        /// The type of the grain for this DetailedGrainStatistic.
        /// </summary>
        [Hagar.Id(1)]
        public string GrainType { get; set; }

        /// <summary>
        /// The silo address for this DetailedGrainStatistic.
        /// </summary>
        [Hagar.Id(2)]
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// Unique Id for the grain.
        /// </summary>
        [Hagar.Id(3)]
        public GrainId GrainId { get; set; }

        /// <summary>
        /// The grains Category
        /// </summary>
        [Hagar.Id(4)]
        public string Category { get; set; }
    }

    [Serializable]
    [Hagar.GenerateSerializer]
    internal class DetailedGrainReport
    {
        [Hagar.Id(1)]
        public GrainId Grain { get; set; }

        /// <summary>silo on which these statistics come from</summary>
        [Hagar.Id(2)]
        public SiloAddress SiloAddress { get; set; }

        /// <summary>silo on which these statistics come from</summary>
        [Hagar.Id(3)]
        public string SiloName { get; set; }

        /// <summary>activation addresses in the local directory cache</summary>
        [Hagar.Id(4)]
        public List<ActivationAddress> LocalCacheActivationAddresses { get; set; }

        /// <summary>activation addresses in the local directory.</summary>
        [Hagar.Id(5)]
        public List<ActivationAddress> LocalDirectoryActivationAddresses { get; set; }

        /// <summary>primary silo for this grain</summary>
        [Hagar.Id(6)]
        public SiloAddress PrimaryForGrain { get; set; }

        /// <summary>the name of the class that implements this grain.</summary>
        [Hagar.Id(7)]
        public string GrainClassTypeName { get; set; }

        /// <summary>activations on this silo</summary>
        [Hagar.Id(8)]
        public List<string> LocalActivations { get; set; }

        public override string ToString()
        {
            return string.Format(Environment.NewLine 
                + "**DetailedGrainReport for grain {0} from silo {1} SiloAddress={2}" + Environment.NewLine 
                + "   LocalCacheActivationAddresses={3}" + Environment.NewLine
                + "   LocalDirectoryActivationAddresses={4}"  + Environment.NewLine
                + "   PrimaryForGrain={5}" + Environment.NewLine 
                + "   GrainClassTypeName={6}" + Environment.NewLine
                + "   LocalActivations:" + Environment.NewLine
                + "{7}." + Environment.NewLine,
                    Grain.ToString(),                                   // {0}
                    SiloName,                                                   // {1}
                    SiloAddress.ToLongString(),                                 // {2}
                    Utils.EnumerableToString(LocalCacheActivationAddresses),    // {3}
                    Utils.EnumerableToString(LocalDirectoryActivationAddresses),// {4}
                    PrimaryForGrain,                                            // {5}
                    GrainClassTypeName,                                         // {6}
                    Utils.EnumerableToString(LocalActivations,                  // {7}
                        str => string.Format("      {0}", str), "\n"));
        }
    }
}
