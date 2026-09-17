using System.Collections.Concurrent;
using System.Net;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.Cassandra;
using Orleans.Clustering.TestKit;
using Xunit;

namespace Tester.Cassandra.Clustering;

public sealed partial class CassandraClusteringTableTests
{
    private const int ConformancePageSize = 32;
    private readonly ConcurrentBag<(IMembershipTable Table, string ClusterId, ServiceProvider Services)> _legacyMembershipHandles = [];
    private Task<(Cluster Cluster, ISession Session)>? _conformanceSession;

    protected override int ConformanceConcurrencyRowCount => ConformancePageSize + 1;

    protected override MembershipTableTestFixture CreateConformanceFixture()
        => new("Cassandra", CreateConformanceHandleAsync);

    private async ValueTask<MembershipTableTestHandle> CreateConformanceHandleAsync(
        string serviceId,
        string clusterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, session) = await (_conformanceSession ??= CreateConformanceSessionAsync(cancellationToken));
        var services = CreateMembershipServices(serviceId, clusterId, () => Task.FromResult(session), cassandraTtl: false);
        var table = services.GetRequiredService<CassandraClusteringTable>();
        return new MembershipTableTestHandle(table, services.DisposeAsync);
    }

    private async Task<(Cluster Cluster, ISession Session)> CreateConformanceSessionAsync(CancellationToken cancellationToken)
    {
        var container = await _cassandraContainer.RunImage(cancellationToken);
        var cluster = Cluster.Builder()
            .WithDefaultKeyspace("orleans")
            .AddContactPoints(new IPEndPoint(IPAddress.Loopback, container.exposedPort))
            .WithQueryOptions(new QueryOptions().SetPageSize(ConformancePageSize))
            .Build();
        try
        {
            var session = await cluster.ConnectAsync("orleans");
            cancellationToken.ThrowIfCancellationRequested();
            return (cluster, session);
        }
        catch
        {
            cluster.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var (table, clusterId, services) in _legacyMembershipHandles)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await table.DeleteMembershipTableEntriesAsync(clusterId, cleanup.Token).WaitAsync(cleanup.Token);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await services.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (_conformanceSession is { IsCompletedSuccessfully: true } session)
        {
            try
            {
                (await session).Cluster.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Cassandra membership fixture cleanup failed.", failures);
        }
    }

    [Fact]
    public async Task MembershipTable_CassandraRows_UseTtlZeroAndVersionedCleanup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = $"Service_{Guid.NewGuid():N}";
        var clusterId = $"Cluster_{Guid.NewGuid():N}";
        var (table, _) = await CreateNewMembershipTableAsync(serviceId, clusterId, cancellationToken, cassandraTtl: false);
        var entries = Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.None).Select(status =>
        {
            var entry = CreateMembershipEntryForTest();
            entry.Status = status;
            entry.SuspectTimes = [Tuple.Create(entry.SiloAddress, entry.IAmAliveTime)];
            return entry;
        }).ToArray();

        foreach (var entry in entries)
        {
            var before = await table.ReadAllAsync(cancellationToken);
            Assert.True(await table.InsertRowAsync(entry, before.Version.Next(), cancellationToken));
        }

        var committed = await table.ReadAllAsync(cancellationToken);
        var session = await CreateSession(cancellationToken);
        var rows = await session.ExecuteAsync(new SimpleStatement(
            """
            SELECT status, version, TTL(version) AS version_ttl,
                TTL(silo_name) AS name_ttl, TTL(host_name) AS host_ttl,
                TTL(status) AS status_ttl, TTL(proxy_port) AS proxy_ttl,
                TTL(start_time) AS start_ttl, TTL(i_am_alive_time) AS heartbeat_ttl,
                TTL(suspect_times) AS suspects_ttl
            FROM membership WHERE partition_key = ?
            """,
            $"{serviceId}-{clusterId}"));
        var observed = rows.ToArray();
        Assert.Equal(entries.Length, observed.Length);
        Assert.Equal(entries.Length, committed.Version.Version);
        Assert.Equal(
            Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.None).Order(),
            observed.Select(row => (SiloStatus)(int)row["status"]).Order());
        foreach (var row in observed)
        {
            Assert.Equal(committed.Version.Version, (int)row["version"]);
            Assert.Null(row["version_ttl"]);
            foreach (var column in new[] { "name_ttl", "host_ttl", "status_ttl", "proxy_ttl", "start_ttl", "heartbeat_ttl", "suspects_ttl" })
            {
                Assert.Null(row[column]);
            }
        }

        var cutoff = entries.Max(entry => entry.IAmAliveTime > entry.StartTime ? entry.IAmAliveTime : entry.StartTime).AddSeconds(1);
        await table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(cutoff), cancellationToken);
        var after = await table.ReadAllAsync(cancellationToken);
        Assert.Equal(committed.Version.Version + 1, after.Version.Version);
        Assert.NotEmpty(after.Version.VersionEtag);
        Assert.NotEqual(committed.Version.VersionEtag, after.Version.VersionEtag);
        Assert.Equal(
            entries.Where(entry => entry.Status != SiloStatus.Dead).Select(entry => entry.SiloAddress).Order(),
            after.Members.Select(row => row.Item1.SiloAddress).Order());
    }
}
