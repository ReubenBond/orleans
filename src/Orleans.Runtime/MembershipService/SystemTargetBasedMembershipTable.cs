#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Serialization;

namespace Orleans.Runtime.MembershipService
{
    internal class SystemTargetBasedMembershipTable : IMembershipTable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;
        private IMembershipTableSystemTarget? _tableGrain;

        public SystemTargetBasedMembershipTable(IServiceProvider serviceProvider, ILogger<SystemTargetBasedMembershipTable> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            _tableGrain = await GetMembershipTable();
        }

        private async Task<IMembershipTableSystemTarget> GetMembershipTable()
        {
            var options = _serviceProvider.GetRequiredService<IOptions<DevelopmentClusterMembershipOptions>>().Value;
            if (options.PrimarySiloEndpoint == null)
            {
                throw new OrleansConfigurationException(
                    $"{nameof(DevelopmentClusterMembershipOptions)}.{nameof(options.PrimarySiloEndpoint)} must be set when using development clustering.");
            }

            var siloDetails = _serviceProvider.GetRequiredService<ILocalSiloDetails>();
            bool isPrimarySilo = siloDetails.SiloAddress.Endpoint.Equals(options.PrimarySiloEndpoint);
            if (isPrimarySilo)
            {
                _logger.LogInformation((int)ErrorCode.MembershipFactory1, "Creating in-memory membership table");
                var catalog = _serviceProvider.GetRequiredService<Catalog>();
                catalog.RegisterSystemTarget(ActivatorUtilities.CreateInstance<MembershipTableSystemTarget>(_serviceProvider));
            }

            var grainFactory = _serviceProvider.GetRequiredService<IInternalGrainFactory>();
            var primarySiloAddress = SiloAddress.New(options.PrimarySiloEndpoint, 0);
            var membershipTableGrain = grainFactory.GetSystemTarget<IMembershipTableSystemTarget>(Constants.SystemMembershipTableType, primarySiloAddress);
            if (isPrimarySilo)
            {
                await WaitForTableGrainToInit(membershipTableGrain);
            }

            return membershipTableGrain;
        }

        private async Task WaitForTableGrainToInit(IMembershipTableSystemTarget membershipTableSystemTarget)
        {
            var timespan = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5);

            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await membershipTableSystemTarget.ReadAll().WithTimeout(timespan, $"MembershipGrain trying to read all content of the membership table, failed due to timeout {timespan}");
                    _logger.LogInformation((int)ErrorCode.MembershipTableGrainInit2, "Connected to membership table provider.");
                    return;
                }
                catch (Exception exc)
                {
                    var baseException = exc.GetBaseException();
                    if (baseException is TimeoutException or OrleansException)
                    {
                        _logger.LogInformation(
                            (int)ErrorCode.MembershipTableGrainInit3,
                            "Waiting for membership table provider to initialize. Going to sleep for {Duration} and re-try to reconnect.",
                            timespan);
                    }
                    else
                    {
                        _logger.LogInformation((int)ErrorCode.MembershipTableGrainInit4, "Membership table provider failed to initialize. Giving up.");
                        throw;
                    }
                }

                await Task.Delay(timespan);
            }
        }

        public Task DeleteMembershipTableEntries(string clusterId) => _tableGrain!.DeleteMembershipTableEntries(clusterId);

        public async Task<MembershipTableData> ReadRow(SiloAddress key) => await _tableGrain!.ReadRow(key);

        public async Task<MembershipTableData> ReadAll() => await _tableGrain!.ReadAll();

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => _tableGrain!.InsertRow(entry, tableVersion);

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => _tableGrain!.UpdateRow(entry, etag, tableVersion);

        public Task UpdateIAmAlive(MembershipEntry entry) => _tableGrain!.UpdateIAmAlive(entry);

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => _tableGrain!.CleanupDefunctSiloEntries(beforeDate);
    }

    [Reentrant]
    internal class MembershipTableSystemTarget : SystemTarget, IMembershipTableSystemTarget
    {
        private readonly InMemoryMembershipTable _table;
        private readonly ILogger _logger;

        public MembershipTableSystemTarget(
            ILocalSiloDetails localSiloDetails,
            ILoggerFactory loggerFactory,
            DeepCopier deepCopier)
            : base(CreateId(localSiloDetails), localSiloDetails.SiloAddress, loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<MembershipTableSystemTarget>();
            _table = new InMemoryMembershipTable(deepCopier);
            _logger.LogInformation((int)ErrorCode.MembershipGrainBasedTable1, "GrainBasedMembershipTable Activated.");
        }

        private static SystemTargetGrainId CreateId(ILocalSiloDetails localSiloDetails) => SystemTargetGrainId.Create(Constants.SystemMembershipTableType, SiloAddress.New(localSiloDetails.SiloAddress.Endpoint, 0));

        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            _logger.LogInformation("InitializeMembershipTable {TryInitTableVersion}.", tryInitTableVersion);
            return Task.CompletedTask;
        }

        public Task DeleteMembershipTableEntries(string clusterId)
        {
            _logger.LogInformation("DeleteMembershipTableEntries {ClusterId}", clusterId);
            _table.Reset();
            return Task.CompletedTask;
        }

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            return Task.FromResult(_table.Read(key));
        }

        public Task<MembershipTableData> ReadAll()
        {
            var t = _table.ReadAll();
            return Task.FromResult(t);
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("InsertRow entry = {Entry}, table version = {Version}", entry.ToString(), tableVersion);
            bool result = _table.Insert(entry, tableVersion);
            if (result == false)
                _logger.LogInformation(
                    (int)ErrorCode.MembershipGrainBasedTable2,
                    "Insert of {Entry} and table version {Version} failed. Table now is {Table}",
                    entry.ToString(),
                    tableVersion,
                    _table.ReadAll());

            return Task.FromResult(result);
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("UpdateRow entry = {Entry}, etag = {ETag}, table version = {Version}", entry.ToString(), etag, tableVersion);
            bool result = _table.Update(entry, etag, tableVersion);
            if (result == false)
                _logger.LogInformation(
                    (int)ErrorCode.MembershipGrainBasedTable3,
                    "Update of {Entry}, eTag {ETag}, table version {Version} failed. Table now is {Table}",
                    entry.ToString(),
                    etag,
                    tableVersion,
                    _table.ReadAll());

            return Task.FromResult(result);
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("UpdateIAmAlive entry = {Entry}", entry.ToString());
            _table.UpdateIAmAlive(entry);
            return Task.CompletedTask;
        }

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            _table.CleanupDefunctSiloEntries(beforeDate);
            return Task.CompletedTask;
        }
    }
}