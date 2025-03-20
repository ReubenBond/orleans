// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Messaging;
using Orleans.Configuration;
using Microsoft.Extensions.Options;

namespace Orleans.Clustering.Redis;

internal sealed class RedisGatewayListProvider(RedisMembershipTable table, IOptions<GatewayOptions> options) : IGatewayListProvider
{
    private readonly RedisMembershipTable _table = table;
    private readonly GatewayOptions _gatewayOptions = options.Value;

    public TimeSpan MaxStaleness => _gatewayOptions.GatewayListRefreshPeriod;

    public bool IsUpdatable => true;

    public async Task<IList<Uri>> GetGateways()
    {
        if (!_table.IsInitialized)
        {
            await _table.InitializeMembershipTable(true);
        }

        var all = await _table.ReadAll();
        var result = all.Members
           .Where(x => x.Item1.Status == SiloStatus.Active && x.Item1.ProxyPort != 0)
           .Select(x =>
            {
                var entry = x.Item1;
                return SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.ProxyPort, entry.SiloAddress.Generation).ToGatewayUri();
            }).ToList();
        return result;
    }

    public async Task InitializeGatewayListProvider()
    {
        await _table.InitializeMembershipTable(true);
    }
}