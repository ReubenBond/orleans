using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// A grain directory partition implementation used during rolling upgrades from
    /// <see cref="LocalGrainDirectory"/> (DHT-based) to <see cref="DistributedGrainDirectory"/> (Virtual Synchrony-based).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class provides local storage for DHT-based directory operations during migration.
    /// New silos configured to use <see cref="DistributedGrainDirectory"/> still participate in the DHT ring
    /// so that old silos can forward requests to them.
    /// </para>
    /// <para>
    /// The <see cref="DistributedGrainDirectory"/> discovers grain registrations through its normal recovery
    /// mechanism by querying silos for their local activations via <see cref="ActivationDirectory"/>.
    /// This avoids the need for synchronous-to-async bridging during registration.
    /// </para>
    /// <para>
    /// This allows a seamless rolling upgrade where:
    /// <list type="bullet">
    /// <item>Old silos use <see cref="LocalGrainDirectory"/> with <see cref="LocalGrainDirectoryPartition"/></item>
    /// <item>New silos use <see cref="LocalGrainDirectory"/> with <see cref="DelegatingGrainDirectoryPartition"/>
    /// which stores locally for DHT compatibility</item>
    /// </list>
    /// </para>
    /// </remarks>
    internal sealed partial class DelegatingGrainDirectoryPartition : ILocalGrainDirectoryPartition
    {
        private readonly ISiloStatusOracle _siloStatusOracle;
        private readonly ILogger<DelegatingGrainDirectoryPartition> _logger;
        
        /// <summary>
        /// Local storage for grain registrations, ensuring DHT-based lookups work correctly during migration.
        /// This mirrors the storage pattern of <see cref="LocalGrainDirectoryPartition"/>.
        /// </summary>
        private readonly Dictionary<GrainId, GrainInfo> _partitionData = new();
        private readonly object _lock = new();

        public DelegatingGrainDirectoryPartition(
            ISiloStatusOracle siloStatusOracle,
            ILoggerFactory loggerFactory)
        {
            _siloStatusOracle = siloStatusOracle ?? throw new ArgumentNullException(nameof(siloStatusOracle));
            _logger = loggerFactory.CreateLogger<DelegatingGrainDirectoryPartition>();
        }

        /// <inheritdoc />
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _partitionData.Count;
                }
            }
        }

        /// <inheritdoc />
        public AddressAndTag AddSingleActivation(GrainAddress address, GrainAddress? previousAddress)
        {
            LogDebugAddSingleActivation(address, previousAddress);

            if (!IsValidSilo(address.SiloAddress))
            {
                var siloStatus = _siloStatusOracle.GetApproximateSiloStatus(address.SiloAddress);
                throw new OrleansException($"Trying to register {address.GrainId} on invalid silo: {address.SiloAddress}. Known status: {siloStatus}");
            }

            GrainAddress resultAddress;
            int versionTag;
            lock (_lock)
            {
                if (!_partitionData.TryGetValue(address.GrainId, out var info))
                {
                    info = new GrainInfo();
                    _partitionData[address.GrainId] = info;
                }

                // Use the existing GrainInfo.TryAddSingleActivation logic
                resultAddress = info.TryAddSingleActivation(address, previousAddress);
                versionTag = info.VersionTag;
            }

            // Note: We don't replicate to DistributedGrainDirectory here.
            // The DistributedGrainDirectory discovers registrations through its recovery mechanism
            // by querying silos for their local activations via ActivationDirectory.

            return new AddressAndTag(resultAddress, versionTag);
        }

        /// <inheritdoc />
        public void RemoveActivation(GrainId grain, ActivationId activation, UnregistrationCause cause = UnregistrationCause.Force)
        {
            LogTraceRemoveActivation(grain, activation, cause);

            lock (_lock)
            {
                if (_partitionData.TryGetValue(grain, out var info))
                {
                    // Force removal since we don't have lazy deregistration delay in this context
                    if (info.RemoveActivation(activation, UnregistrationCause.Force, TimeSpan.Zero, out var wasRemoved) && wasRemoved)
                    {
                        // Remove the entry if no activation remains
                        if (info.Activation is null)
                        {
                            _partitionData.Remove(grain);
                        }
                    }
                }
            }
        }

        /// <inheritdoc />
        public void RemoveGrain(GrainId grain)
        {
            LogTraceRemoveGrain(grain);

            lock (_lock)
            {
                _partitionData.Remove(grain);
            }
        }

        /// <inheritdoc />
        public AddressAndTag LookUpActivation(GrainId grain)
        {
            lock (_lock)
            {
                if (_partitionData.TryGetValue(grain, out var info) && info.Activation is { } activation)
                {
                    if (IsValidSilo(activation.SiloAddress))
                    {
                        LogDebugLookupFound(grain, activation);
                        return new AddressAndTag(activation, info.VersionTag);
                    }
                    else
                    {
                        // Registration is on a dead silo, remove it
                        _partitionData.Remove(grain);
                    }
                }
            }

            LogDebugLookupNotFound(grain);
            return new AddressAndTag(null, 0);
        }

        /// <inheritdoc />
        public int GetGrainETag(GrainId grain)
        {
            lock (_lock)
            {
                if (_partitionData.TryGetValue(grain, out var info))
                {
                    return info.VersionTag;
                }
            }
            return GrainInfo.NO_ETAG;
        }

        /// <inheritdoc />
        public List<KeyValuePair<GrainId, GrainInfo>> GetItems()
        {
            lock (_lock)
            {
                return new List<KeyValuePair<GrainId, GrainInfo>>(_partitionData);
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            lock (_lock)
            {
                _partitionData.Clear();
            }
            LogDebugClearCalled();
        }

        /// <inheritdoc />
        public List<GrainAddress> Split(Predicate<GrainId> predicate)
        {
            var result = new List<GrainAddress>();
            lock (_lock)
            {
                var keysToRemove = new List<GrainId>();
                foreach (var kvp in _partitionData)
                {
                    if (predicate(kvp.Key) && kvp.Value.Activation is { } activation)
                    {
                        result.Add(activation);
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    _partitionData.Remove(key);
                }
            }
            LogDebugSplitReturning(result.Count);
            return result;
        }

        private bool IsValidSilo(SiloAddress? silo) => silo is not null && _siloStatusOracle.IsFunctionalDirectory(silo);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "DelegatingGrainDirectoryPartition.AddSingleActivation: address={Address}, previousAddress={PreviousAddress}"
        )]
        private partial void LogDebugAddSingleActivation(GrainAddress address, GrainAddress? previousAddress);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "DelegatingGrainDirectoryPartition.AddSingleActivation: address={Address}, previousAddress={PreviousAddress}"
        )]
        private partial void LogTraceAddSingleActivation(GrainAddress address, GrainAddress? previousAddress);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "DelegatingGrainDirectoryPartition.RemoveActivation: grain={GrainId}, activation={ActivationId}, cause={Cause}"
        )]
        private partial void LogTraceRemoveActivation(GrainId grainId, ActivationId activationId, UnregistrationCause cause);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "DelegatingGrainDirectoryPartition.RemoveGrain: grain={GrainId}"
        )]
        private partial void LogTraceRemoveGrain(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "DelegatingGrainDirectoryPartition.Clear called"
        )]
        private partial void LogDebugClearCalled();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "DelegatingGrainDirectoryPartition.Split returning {Count} entries"
        )]
        private partial void LogDebugSplitReturning(int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "DelegatingGrainDirectoryPartition.LookUpActivation: grain={GrainId}, found={Address}"
        )]
        private partial void LogDebugLookupFound(GrainId grainId, GrainAddress address);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "DelegatingGrainDirectoryPartition.LookUpActivation: grain={GrainId}, not found"
        )]
        private partial void LogDebugLookupNotFound(GrainId grainId);
    }
}
