using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Consul;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Runtime.Membership
{
    /// <summary>
    /// A Membership Table implementation using Consul 0.6.0  https://consul.io/
    /// </summary>
    public partial class ConsulBasedMembershipTable : IMembershipTable
    {
        private static readonly TableVersion NotFoundTableVersion = new TableVersion(0, "0");
        private static readonly QueryOptions ConsistentRead = new() { Consistency = ConsistencyMode.Consistent };
        private readonly ILogger _logger;
        private readonly IConsulClient _consulClient;
        private readonly ConsulClusteringOptions clusteringSiloTableOptions;
        private readonly string clusterId;
        private readonly string? kvRootFolder;
        private readonly string versionKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsulBasedMembershipTable"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="membershipTableOptions">The Consul clustering options.</param>
        /// <param name="clusterOptions">The cluster identity options.</param>
        public ConsulBasedMembershipTable(
            ILogger<ConsulBasedMembershipTable> logger,
            IOptions<ConsulClusteringOptions> membershipTableOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            this.clusterId = clusterOptions.Value.ClusterId;
            this.kvRootFolder = membershipTableOptions.Value.KvRootFolder;
            this._logger = logger;
            this.clusteringSiloTableOptions = membershipTableOptions.Value;
            this._consulClient = this.clusteringSiloTableOptions.CreateClient();
            versionKey = ConsulSiloRegistrationAssembler.FormatVersionKey(clusterId, kvRootFolder);
        }

        /// <summary>
        /// Initializes the Consul based membership table.
        /// </summary>
        /// <param name="tryInitTableVersion">Whether to create the initial table version if it does not exist.</param>
        /// <returns></returns>
        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tryInitTableVersion)
            {
                await _consulClient.KV.Txn(
                    new List<KVTxnOp> { GetVersionRowUpdate(NotFoundTableVersion) }, cancellationToken);
            }
        }

        /// <inheritdoc />
        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress siloAddress) => ReadRowAsync(siloAddress, CancellationToken.None);

        /// <inheritdoc />
        public async Task<MembershipTableData> ReadRowAsync(SiloAddress siloAddress, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (siloRegistration, tableVersion) = await GetConsulSiloRegistration(siloAddress, cancellationToken);

            return AssembleMembershipTableData(tableVersion, siloRegistration);
        }

        /// <inheritdoc />
        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        /// <inheritdoc />
        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            return ReadAllAsync(this._consulClient, this.clusterId, this.kvRootFolder, this._logger, this.versionKey, cancellationToken);
        }

        /// <summary>
        /// Reads all membership entries for a cluster from Consul.
        /// </summary>
        /// <param name="consulClient">The Consul client.</param>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="kvRootFolder">The optional root folder containing Orleans keys.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="versionKey">The key containing the membership table version, or <see langword="null"/> to use the cluster's default version key.</param>
        /// <returns>The cluster membership entries and table version.</returns>
        [Obsolete("Use ReadAllAsync instead.")]
        public static Task<MembershipTableData> ReadAll(IConsulClient consulClient, string clusterId, string? kvRootFolder, ILogger logger, string? versionKey) =>
            ReadAllAsync(consulClient, clusterId, kvRootFolder, logger, versionKey, CancellationToken.None);

        /// <summary>
        /// Reads all membership entries for a cluster from Consul.
        /// </summary>
        /// <param name="consulClient">The Consul client.</param>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="kvRootFolder">The optional root folder containing Orleans keys.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="versionKey">The key containing the membership table version, or <see langword="null"/> to use the cluster's default version key.</param>
        /// <param name="cancellationToken">A token which cancels the operation.</param>
        /// <returns>The cluster membership entries and table version.</returns>
        public static async Task<MembershipTableData> ReadAllAsync(IConsulClient consulClient, string clusterId, string? kvRootFolder, ILogger logger, string? versionKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deploymentKVAddresses = await consulClient.KV.List(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(clusterId, kvRootFolder) + "/", ConsistentRead, cancellationToken);
            if (deploymentKVAddresses.Response == null)
            {
                LogDebugCouldNotFindSiloRegistrations(logger, clusterId);
                return new MembershipTableData(NotFoundTableVersion);
            }

            var allSiloRegistrations =
                deploymentKVAddresses.Response
                .Where(siloKV => !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.OrdinalIgnoreCase)
                        && !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.VersionSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(siloKV =>
                {
                    var iAmAliveKV = deploymentKVAddresses.Response.SingleOrDefault(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKV.Key), StringComparison.OrdinalIgnoreCase));
                    return ConsulSiloRegistrationAssembler.FromKVPairs(clusterId, siloKV, iAmAliveKV);
                }).ToArray();

            var tableVersion = GetTableVersion(versionKey ?? ConsulSiloRegistrationAssembler.FormatVersionKey(clusterId, kvRootFolder), deploymentKVAddresses);

            return AssembleMembershipTableData(tableVersion, allSiloRegistrations);
        }

        /// <inheritdoc />
        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                //Use "0" as the eTag then Consul KV CAS will treat the operation as an insert and return false if the KV already exiats.
                var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, "0");
                var insertKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration, this.kvRootFolder);
                var rowInsert = new KVTxnOp(insertKV.Key, KVTxnVerb.CAS) { Index = siloRegistration.LastIndex, Value = insertKV.Value };
                var versionUpdate = this.GetVersionRowUpdate(tableVersion);
                var heartbeat = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(this.clusterId, this.kvRootFolder, entry.SiloAddress, entry.IAmAliveTime);
                var heartbeatInsert = new KVTxnOp(heartbeat.Key, KVTxnVerb.CAS) { Index = 0, Value = heartbeat.Value };

                var responses = await _consulClient.KV.Txn(new List<KVTxnOp> { rowInsert, versionUpdate, heartbeatInsert }, cancellationToken);
                if (!responses.Response.Success)
                {
                    LogDebugConsulMembershipProviderFailedToInsertRow(entry.SiloAddress);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInformationConsulMembershipProviderFailedToInsertRegistration(ex, entry.SiloAddress);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            //Update Silo Liveness
            try
            {
                while (true)
                {
                    var (existing, currentVersion) = await GetConsulSiloRegistration(entry.SiloAddress, cancellationToken);
                    if (existing is null || existing.LastIndex != ulong.Parse(etag, CultureInfo.InvariantCulture)
                        || currentVersion.VersionEtag != tableVersion.VersionEtag)
                    {
                        LogDebugConsulMembershipProviderFailedCASCheck(entry.SiloAddress);
                        return false;
                    }

                    var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, etag);
                    var updateKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration, this.kvRootFolder);
                    var heartbeatKey = ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(updateKV.Key);
                    var currentHeartbeat = (await _consulClient.KV.Get(heartbeatKey, ConsistentRead, cancellationToken)).Response;
                    var heartbeatTime = currentHeartbeat is null
                        ? existing.IAmAliveTime
                        : JsonConvert.DeserializeObject<DateTime>(Encoding.UTF8.GetString(currentHeartbeat.Value));
                    var heartbeat = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(clusterId, kvRootFolder, entry.SiloAddress,
                        new DateTime(Math.Max(entry.IAmAliveTime.Ticks, heartbeatTime.Ticks), DateTimeKind.Utc));
                    var operations = new List<KVTxnOp>
                    {
                        new(updateKV.Key, KVTxnVerb.CAS) { Index = siloRegistration.LastIndex, Value = updateKV.Value },
                        GetVersionRowUpdate(tableVersion),
                        new(heartbeat.Key, KVTxnVerb.CAS) { Index = currentHeartbeat?.ModifyIndex ?? 0, Value = heartbeat.Value }
                    };

                    if ((await _consulClient.KV.Txn(operations, cancellationToken)).Response.Success)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInformationConsulMembershipProviderFailedToUpdateRegistration(ex, entry.SiloAddress);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        /// <inheritdoc />
        public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var iAmAliveKV = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(this.clusterId, this.kvRootFolder, entry.SiloAddress, entry.IAmAliveTime);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = (await _consulClient.KV.Get(iAmAliveKV.Key, ConsistentRead, cancellationToken)).Response;
                if (current is not null && JsonConvert.DeserializeObject<DateTime>(Encoding.UTF8.GetString(current.Value)) >= entry.IAmAliveTime)
                {
                    return;
                }

                iAmAliveKV.ModifyIndex = current?.ModifyIndex ?? 0;
                if ((await _consulClient.KV.CAS(iAmAliveKV, cancellationToken)).Response)
                {
                    return;
                }
            }
        }

        /// <inheritdoc />
        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        /// <inheritdoc />
        public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _consulClient.KV.DeleteTree(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(clusterId, this.kvRootFolder) + "/", cancellationToken);
        }

        private static TableVersion GetTableVersion(string? versionKey, QueryResult<KVPair[]> entries)
        {
            TableVersion tableVersion;
            var tableVersionEntry = entries?.Response?.FirstOrDefault(kv => kv.Key.Equals(versionKey ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (tableVersionEntry != null)
            {
                var versionNumber = int.Parse(Encoding.UTF8.GetString(tableVersionEntry.Value), CultureInfo.InvariantCulture);
                tableVersion = new TableVersion(versionNumber, tableVersionEntry.ModifyIndex.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                tableVersion = NotFoundTableVersion;
            }

            return tableVersion;
        }

        private KVTxnOp GetVersionRowUpdate(TableVersion version)
        {
            var index = ulong.Parse(version.VersionEtag, CultureInfo.InvariantCulture);
            var versionBytes = Encoding.UTF8.GetBytes(version.Version.ToString(CultureInfo.InvariantCulture));
            return new KVTxnOp(this.versionKey, KVTxnVerb.CAS) { Index = index, Value = versionBytes };
        }

        private async Task<(ConsulSiloRegistration?, TableVersion)> GetConsulSiloRegistration(SiloAddress siloAddress, CancellationToken cancellationToken)
        {
            var deploymentKey = ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(this.clusterId, this.kvRootFolder);
            var siloKey = ConsulSiloRegistrationAssembler.FormatDeploymentSiloKey(this.clusterId, this.kvRootFolder, siloAddress);
            var entries = await _consulClient.KV.List(deploymentKey + "/", ConsistentRead, cancellationToken);
            if (entries.Response == null) return (null, NotFoundTableVersion);

            var siloKV = entries.Response.SingleOrDefault(KV => KV.Key.Equals(siloKey, StringComparison.OrdinalIgnoreCase));
            var iAmAliveKV = entries.Response.SingleOrDefault(KV => KV.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKey), StringComparison.OrdinalIgnoreCase));
            var tableVersion = GetTableVersion(versionKey: versionKey, entries: entries);

            var siloRegistration = siloKV is null ? null : ConsulSiloRegistrationAssembler.FromKVPairs(this.clusterId, siloKV, iAmAliveKV);

            return (siloRegistration, tableVersion);
        }

        private static MembershipTableData AssembleMembershipTableData(TableVersion tableVersion, params ConsulSiloRegistration?[] silos)
        {
            var membershipEntries = silos
                .OfType<ConsulSiloRegistration>()
                .Select(silo => ConsulSiloRegistrationAssembler.ToMembershipEntry(silo))
                .ToList();

            return new MembershipTableData(membershipEntries, tableVersion);
        }

        /// <inheritdoc />
        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        /// <inheritdoc />
        public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var allKVs = await _consulClient.KV.List(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(this.clusterId, this.kvRootFolder) + "/", ConsistentRead, cancellationToken);
                if (allKVs.Response == null)
                {
                    LogDebugCouldNotFindSiloRegistrationsForCleanup(this.clusterId);
                    return;
                }

                var selected = allKVs.Response
                    .Where(siloKV => !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.OrdinalIgnoreCase)
                        && !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.VersionSuffix, StringComparison.OrdinalIgnoreCase))
                    .Select(siloKV =>
                    {
                        var heartbeat = allKVs.Response.SingleOrDefault(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKV.Key), StringComparison.OrdinalIgnoreCase));
                        return new
                        {
                            RegistrationKey = siloKV.Key,
                            Heartbeat = heartbeat,
                            Registration = ConsulSiloRegistrationAssembler.FromKVPairs(clusterId, siloKV, heartbeat)
                        };
                    })
                    .FirstOrDefault(entry => entry.Registration.Status == SiloStatus.Dead
                        && Math.Max(entry.Registration.IAmAliveTime.Ticks, entry.Registration.StartTime.Ticks) < beforeDate.UtcDateTime.Ticks
                        && entry.Registration.SuspectingSilos?.Any(vote => vote.Time >= beforeDate.UtcDateTime) != true
                        && entry.Heartbeat is not null);

                if (selected is null || selected.Heartbeat is not { } selectedHeartbeat)
                {
                    return;
                }

                var next = GetTableVersion(versionKey, allKVs).Next();
                await _consulClient.KV.Txn(new List<KVTxnOp>
                {
                    GetVersionRowUpdate(next),
                    new(selected.RegistrationKey, KVTxnVerb.DeleteCAS) { Index = selected.Registration.LastIndex },
                    new(selectedHeartbeat.Key, KVTxnVerb.DeleteCAS) { Index = selectedHeartbeat.ModifyIndex }
                }, cancellationToken);
            }
        }

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "Could not find any silo registrations for deployment {ClusterId}."
        )]
        private static partial void LogDebugCouldNotFindSiloRegistrations(ILogger logger, string clusterId);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "ConsulMembershipProvider failed to insert the row {SiloAddress}."
        )]
        private partial void LogDebugConsulMembershipProviderFailedToInsertRow(SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Information,
            Message = "ConsulMembershipProvider failed to insert registration for silo {SiloAddress}"
        )]
        private partial void LogInformationConsulMembershipProviderFailedToInsertRegistration(Exception ex, SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "ConsulMembershipProvider failed the CAS check when updating the registration for silo {SiloAddress}."
        )]
        private partial void LogDebugConsulMembershipProviderFailedCASCheck(SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Information,
            Message = "ConsulMembershipProvider failed to update the registration for silo {SiloAddress}"
        )]
        private partial void LogInformationConsulMembershipProviderFailedToUpdateRegistration(Exception ex, SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "Could not find any silo registrations for deployment {ClusterId}."
        )]
        private partial void LogDebugCouldNotFindSiloRegistrationsForCleanup(string clusterId);
    }
}
