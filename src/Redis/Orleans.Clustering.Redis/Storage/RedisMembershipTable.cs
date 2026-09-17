using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using StackExchange.Redis;
using Orleans.Configuration;
using Newtonsoft.Json;
using System.Linq;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Clustering.Redis
{
    internal class RedisMembershipTable : IMembershipTable, IDisposable, IAsyncDisposable
    {
        private const string TableVersionKey = "Version";
        private static readonly TableVersion DefaultTableVersion = new TableVersion(0, "0");
        private readonly RedisClusteringOptions _redisOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly JsonSerializerSettings _jsonSerializerSettings;
        private readonly RedisKey _clusterKey;
        private readonly SemaphoreSlim _initializationLock = new(1, 1);
        private IConnectionMultiplexer _muxer = null!;
        private IDatabase _db = null!;
        private bool _muxerIsShared;

        public RedisMembershipTable(IOptions<RedisClusteringOptions> redisOptions, IOptions<ClusterOptions> clusterOptions)
        {
            _redisOptions = redisOptions.Value;
            _clusterOptions = clusterOptions.Value;
            _clusterKey = _redisOptions.CreateRedisKey(_clusterOptions);
            _jsonSerializerSettings = JsonSettings.JsonSerializerSettings;
        }

        public bool IsInitialized { get; private set; }

        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(clusterId, _clusterOptions.ClusterId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Cluster id {clusterId} does not match RedisMembershipTable value of '{_clusterOptions.ClusterId}'.",
                    nameof(clusterId));
            }

            await AwaitAsync(_db.KeyDeleteAsync(_clusterKey), cancellationToken);
        }

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            await _initializationLock.WaitAsync(cancellationToken);
            try
            {
                await InitializeMembershipTableCoreAsync(tryInitTableVersion, cancellationToken);
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        private async Task InitializeMembershipTableCoreAsync(bool tryInitTableVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_redisOptions.EntryExpiry is not null)
            {
                throw new OrleansConfigurationException("Redis membership entries cannot expire: expiration can remove live members and reset the table version. Use membership cleanup or explicitly delete the cluster's entries instead.");
            }

            var muxer = _muxer;
            var isShared = _muxerIsShared;
            var isNewConnection = muxer is null;
            if (muxer is null)
            {
                var creation = _redisOptions.CreateMultiplexer(_redisOptions);
                try
                {
                    (muxer, isShared) = await AwaitAsync(creation, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The tokenless factory can still return an owned connection after the caller stops waiting.
                    DisposeAbandonedMultiplexerAsync(creation).Ignore();
                    throw;
                }
            }

            var initialized = false;
            try
            {
                var db = isNewConnection ? muxer.GetDatabase() : _db;
                if (tryInitTableVersion)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AwaitAsync(db.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(DefaultTableVersion), When.NotExists), cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                    await AwaitAsync(db.KeyPersistAsync(_clusterKey), cancellationToken);
                }

                if (isNewConnection)
                {
                    _muxer = muxer;
                    _muxerIsShared = isShared;
                    _db = db;
                }

                IsInitialized = true;
                initialized = true;
            }
            finally
            {
                if (!initialized && isNewConnection && !isShared)
                {
                    await muxer.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task DisposeAbandonedMultiplexerAsync(Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> creation)
        {
            var (muxer, isShared) = await creation.ConfigureAwait(false);
            if (!isShared)
            {
                await muxer.DisposeAsync().ConfigureAwait(false);
            }
        }

        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            return await UpsertRowInternal(entry, tableVersion, allowInsertOnly: true, cancellationToken);
        }

        private async Task<bool> UpsertRowInternal(MembershipEntry entry, TableVersion tableVersion, bool allowInsertOnly, CancellationToken cancellationToken)
        {
            var rowKey = entry.SiloAddress.ToString();
            var updatedEntry = Deserialize(Serialize(entry));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tx = _db.CreateTransaction();
                var versionCondition = tx.AddCondition(Condition.HashEqual(_clusterKey, TableVersionKey, tableVersion.VersionEtag));
                if (allowInsertOnly)
                {
                    tx.AddCondition(Condition.HashNotExists(_clusterKey, rowKey));
                }
                else
                {
                    var current = await AwaitAsync(_db.HashGetAsync(_clusterKey, rowKey), cancellationToken);
                    if (!current.HasValue)
                    {
                        return false;
                    }

                    var existingEntry = Deserialize(current.ToString());
                    updatedEntry.IAmAliveTime = new DateTime(Math.Max(entry.IAmAliveTime.Ticks, existingEntry.IAmAliveTime.Ticks), DateTimeKind.Utc);
                    tx.AddCondition(Condition.HashEqual(_clusterKey, rowKey, current));
                }

                tx.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(tableVersion)).Ignore();
                tx.HashSetAsync(_clusterKey, rowKey, Serialize(updatedEntry)).Ignore();
                cancellationToken.ThrowIfCancellationRequested();
                if (await AwaitAsync(tx.ExecuteAsync(), cancellationToken))
                {
                    return true;
                }

                if (allowInsertOnly || !versionCondition.WasSatisfied)
                {
                    return false;
                }

                // A heartbeat changed the row without changing the table version. Merge it and retry.
            }
        }

        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var all = await AwaitAsync(_db.HashGetAllAsync(_clusterKey), cancellationToken);
            var tableVersionRow = all.SingleOrDefault(h => TableVersionKey.Equals(h.Name, StringComparison.Ordinal));
            TableVersion tableVersion = GetTableVersionFromRow(tableVersionRow.Value);

            var data = all.Where(x => !TableVersionKey.Equals(x.Name, StringComparison.Ordinal) && x.Value.HasValue)
                .Select(x => Tuple.Create(Deserialize(x.Value!), tableVersion.VersionEtag))
                .ToList();
            return new MembershipTableData(data, tableVersion);
        }

        private static TableVersion GetTableVersionFromRow(RedisValue tableVersionRow)
        {
            if (TryGetValueString(tableVersionRow, out var value))
            {
                return DeserializeVersion(value);
            }

            return DefaultTableVersion;
        }

        private static bool TryGetValueString(RedisValue key, [NotNullWhen(true)] out string? value)
        {
            if (key.HasValue)
            {
                value = key.ToString();
                return true;
            }

            value = null;
            return false;
        }

        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

        public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tx = _db.CreateTransaction();
            var tableVersionRowTask = tx.HashGetAsync(_clusterKey, TableVersionKey);
            tableVersionRowTask.Ignore();
            var entryRowTask = tx.HashGetAsync(_clusterKey, key.ToString());
            entryRowTask.Ignore();
            cancellationToken.ThrowIfCancellationRequested();
            if (!await AwaitAsync(tx.ExecuteAsync(), cancellationToken))
            {
                throw new RedisClusteringException($"Unexpected transaction failure while reading key {key}");
            }

            TableVersion tableVersion = GetTableVersionFromRow(await AwaitAsync(tableVersionRowTask, cancellationToken));
            var entryRow = await AwaitAsync(entryRowTask, cancellationToken);
            if (TryGetValueString(entryRow, out var entryValueString))
            {
                var entry = Deserialize(entryValueString);
                return new MembershipTableData(Tuple.Create(entry, tableVersion.VersionEtag), tableVersion);
            }
            else
            {
                return new MembershipTableData(tableVersion);
            }
        }

        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            var key = entry.SiloAddress.ToString();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await AwaitAsync(_db.HashGetAsync(_clusterKey, key), cancellationToken);
                if (!current.HasValue)
                {
                    return;
                }

                var existingEntry = Deserialize(current.ToString());
                if (existingEntry.IAmAliveTime >= entry.IAmAliveTime)
                {
                    return;
                }

                existingEntry.IAmAliveTime = entry.IAmAliveTime;
                var tx = _db.CreateTransaction();
                tx.AddCondition(Condition.HashEqual(_clusterKey, key, current));
                tx.HashSetAsync(_clusterKey, key, Serialize(existingEntry)).Ignore();
                cancellationToken.ThrowIfCancellationRequested();
                if (await AwaitAsync(tx.ExecuteAsync(), cancellationToken))
                {
                    return;
                }
            }
        }

        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reads use the table version as the row etag, so both must describe the same view.
            return string.Equals(etag, tableVersion.VersionEtag, StringComparison.Ordinal)
                && await UpsertRowInternal(entry, tableVersion, allowInsertOnly: false, cancellationToken);
        }

        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = await AwaitAsync(_db.HashGetAllAsync(_clusterKey), cancellationToken);
                var version = GetTableVersionFromRow(rows.SingleOrDefault(row => row.Name == TableVersionKey).Value);
                HashEntry? candidate = null;
                foreach (var row in rows)
                {
                    if (row.Name == TableVersionKey)
                    {
                        continue;
                    }

                    var entry = Deserialize(row.Value.ToString());
                    if (entry.Status == SiloStatus.Dead
                        && Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks) < beforeDate.UtcDateTime.Ticks
                        && entry.SuspectTimes?.Any(vote => vote.Item2 >= beforeDate.UtcDateTime) != true)
                    {
                        candidate = row;
                        break;
                    }
                }

                if (candidate is not { } selected)
                {
                    return;
                }

                var next = version.Next();
                var tx = _db.CreateTransaction();
                tx.AddCondition(Condition.HashEqual(_clusterKey, TableVersionKey, version.VersionEtag));
                tx.AddCondition(Condition.HashEqual(_clusterKey, selected.Name, selected.Value));
                tx.HashDeleteAsync(_clusterKey, selected.Name).Ignore();
                tx.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(next)).Ignore();
                cancellationToken.ThrowIfCancellationRequested();
                await AwaitAsync(tx.ExecuteAsync(), cancellationToken);
            }
        }

        // StackExchange.Redis does not accept cancellation tokens. Observe terminal faults if a caller abandons its wait.
        private static async Task<T> AwaitAsync<T>(Task<T> operation, CancellationToken cancellationToken)
        {
            operation.Ignore();
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _initializationLock.Dispose();
            var muxer = _muxer;
            if (muxer is null)
            {
                return;
            }

            var muxerIsShared = _muxerIsShared;
            _muxer = null!;
            _db = null!;
            _muxerIsShared = false;
            IsInitialized = false;

            if (!muxerIsShared)
            {
                muxer.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _initializationLock.Dispose();
            var muxer = _muxer;
            if (muxer is null)
            {
                return;
            }

            var muxerIsShared = _muxerIsShared;
            _muxer = null!;
            _db = null!;
            _muxerIsShared = false;
            IsInitialized = false;

            if (!muxerIsShared)
            {
                await muxer.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static string SerializeVersion(TableVersion tableVersion) => tableVersion.Version.ToString(CultureInfo.InvariantCulture);

        private static TableVersion DeserializeVersion(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
            {
                return DefaultTableVersion;
            }

            var version = int.Parse(versionString);
            return new TableVersion(version, versionString);
        }

        private string Serialize(MembershipEntry value)
        {
            return JsonConvert.SerializeObject(value, _jsonSerializerSettings);
        }

        private MembershipEntry Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<MembershipEntry>(json, _jsonSerializerSettings)!;
        }
    }
}
