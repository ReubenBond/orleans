using System.Net;
using System.Reflection;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Extensions;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Storage;
using Xunit;

namespace Tester.AzureUtils;

[TestCategory("AzureStorage"), TestCategory("Storage"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public class AzureMembershipPaginationTests
{
    private const string ClusterId = "membership-pagination-tests";
    private const string TableName = "MembershipPaginationTests";

    [Theory]
    [InlineData("Initialize")]
    [InlineData("Delete")]
    [InlineData("Cleanup")]
    [InlineData("ReadRow")]
    [InlineData("ReadAll")]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    public async Task CanceledOperationsDoNotAccessStorage(string operation)
    {
        var table = new AzureBasedMembershipTable(
            NullLoggerFactory.Instance,
            Options.Create(new AzureStorageClusteringOptions()),
            Options.Create(new ClusterOptions { ClusterId = ClusterId }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var token = cancellation.Token;
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
        var entry = new MembershipEntry { SiloAddress = silo };
        var version = new TableVersion(1, "etag");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "Initialize" => table.InitializeMembershipTableAsync(true, token),
            "Delete" => table.DeleteMembershipTableEntriesAsync(ClusterId, token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
            "ReadRow" => table.ReadRowAsync(silo, token),
            "ReadAll" => table.ReadAllAsync(token),
            "Insert" => table.InsertRowAsync(entry, version, token),
            "Update" => table.UpdateRowAsync(entry, "etag", version, token),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Equal(token, exception.CancellationToken);
    }

    [Fact]
    public async Task OnePageReadUsesOneQuery()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(false, Version(1, "v1"), Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task StablePaginatedReadUsesOneQuery()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(["silo-1", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task TornPaginatedReadRetries()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 1, "before-1"),
            Silo("silo-1", "s1"),
            Version(2, "legacy-2"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 2, "after-2")),
            before: Version(1, "legacy-1"), after: Version(2, "legacy-2"));
        storage.AddQuery(FencedQuery(2, Silo("silo-2", "s2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(["silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task LegacyVersionAheadRetriesTornReadDuringRollingUpgrade()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 9, "before-9"),
            Silo("silo-1", "stale"),
            Version(11, "legacy-11"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 10, "after-10")),
            before: Version(10, "legacy-10"), after: Version(11, "legacy-11"));
        storage.AddQuery(Query(true, Silo("silo-1", "current"), Version(11, "legacy-11")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal("legacy-11", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task MultipleWritesDuringReadAreDetected()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 1, "before-1"),
            Silo("silo-1", "stale"),
            Version(3, "legacy-3"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 3, "after-3")),
            before: Version(1, "legacy-1"), after: Version(3, "legacy-3"));
        storage.AddQuery(FencedQuery(3, Silo("silo-1", "current")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task PerpetualChurnFailsAfterBoundWithClusterContext()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        for (var attempt = 0; attempt < OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts; attempt++)
        {
            storage.AddQuery(Query(
                true,
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, attempt, $"before-{attempt}"),
                Silo($"silo-{attempt}", $"s{attempt}"),
                Version(attempt + 1, $"legacy-{attempt + 1}"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, attempt + 1, $"after-{attempt + 1}")),
                before: Version(attempt, $"legacy-{attempt}"), after: Version(attempt + 1, $"legacy-{attempt + 1}"));
        }

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Unable to read a consistent membership snapshot for cluster '{ClusterId}' from table '{TableName}' after {OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts} attempts.",
            exception.Message);
        Assert.Equal(OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts, storage.QueryCount);
        Assert.Equal(2 * OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts, storage.VersionReadCount);
    }

    [Fact]
    public async Task MissingBoundaryRowsUseLegacyVersionFence()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("v1", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionFenceRetriesChangedVersionWithoutBoundaryRows(bool paginated)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(paginated, Silo("silo-1", "stale"), Version(2, "v2")),
            before: Version(1, "v1"), after: Version(2, "v2"));
        storage.AddQuery(Query(paginated, Silo("silo-1", "current"), Version(2, "v2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal("v2", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task StableFenceWithDifferentQueriedVersionRetries()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Silo("silo-1", "stale"), Version(1, "v1")),
            before: Version(2, "v2"), after: Version(2, "v2"));
        storage.AddQuery(Query(true, Silo("silo-1", "current"), Version(2, "v2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal("v2", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task MatchingBoundaryRowsDoNotHideConcurrentLegacyWrite()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 7, "before-7"),
            Silo("silo-1", "stale"),
            Version(9, "v9"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 7, "after-7")),
            before: Version(8, "v8"), after: Version(9, "v9"));
        storage.AddQuery(Query(true, Silo("silo-1", "current"), Silo("silo-2", "inserted"), Version(9, "v9")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(["silo-1", "silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(["current", "inserted", "v9"], result.Select(entry => entry.ETag));
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task VersionFenceAllowsDirtyIAmAliveEtagUpdate()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "heartbeat-2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("heartbeat-2", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task CancellationTokenFlowsThroughPaginatedRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")));

        await CreateManager(storage).FindAllSiloEntries(cancellation.Token);

        Assert.Equal(3, storage.CancellationTokens.Count);
        Assert.All(storage.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task CancellationBetweenQueryAndClosingFenceRejectsUnverifiedSnapshot()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(() =>
        {
            cancellation.Cancel();
            return Query(
                true,
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 1, "before"),
                Version(2, "version"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 2, "after"));
        }, Version(1, "version-1"), Version(2, "version"));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager(storage).FindAllSiloEntries(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(1, storage.VersionReadCount);
    }

    [Fact]
    public async Task CancellationStopsMembershipSnapshotRetries()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(2), before: Version(1, "legacy-1"), after: Version(2, "legacy-2"));
        storage.OnVersionRead = count =>
        {
            if (count == 2) cancellation.Cancel();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager(storage).FindAllSiloEntries(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task CompletedMembershipRead_PreservesSnapshotAfterCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(2, Silo("silo-2", "s2")));
        storage.OnVersionRead = count =>
        {
            if (count == 2) cancellation.Cancel();
        };

        var result = await CreateManager(storage).FindAllSiloEntries(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(["silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task CanceledMembershipReadDoesNotQueryStorage()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var storage = new ScriptedMembershipTableReadStorage();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager(storage).FindAllSiloEntries(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, storage.QueryCount);
        Assert.Equal(0, storage.VersionReadCount);
    }

    [Fact]
    public async Task CleanupAtomicallyAdvancesVersionForEachBoundedDeletionBatch()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        var rows = Enumerable.Range(0, 195).Select(index => DeadSilo($"silo-{index:D3}", $"s{index}")).ToArray();
        storage.AddQuery(FencedQuery(0, rows));
        storage.AddQuery(FencedQuery(1, rows.Skip(97).ToArray()));
        storage.AddQuery(FencedQuery(2, rows.Skip(194).ToArray()));
        storage.AddQuery(FencedQuery(3));
        var batches = new List<TableTransactionAction[]>();
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(TestContext.Current.CancellationToken, call.Arg<CancellationToken>());
                batches.Add(call.Arg<IEnumerable<TableTransactionAction>>().ToArray());
                return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>([], Substitute.For<Response>()));
            });

        await CreateManager(storage, client).CleanupDefunctSiloEntries(
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 100, 100, 4 }, batches.Select(batch => batch.Length));
        for (var index = 0; index < batches.Count; index++)
        {
            var version = Assert.IsType<SiloInstanceTableEntry>(batches[index][0].Entity);
            Assert.Equal(TableTransactionActionType.UpdateReplace, batches[index][0].ActionType);
            Assert.Equal(SiloInstanceTableEntry.TABLE_VERSION_ROW, version.RowKey);
            Assert.Equal((index + 1).ToString(), version.MembershipVersion);
            Assert.Equal($"legacy-{index}", batches[index][0].ETag.ToString());
            Assert.Equal(
                [SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX],
                batches[index].Skip(1).Take(2).Select(action => action.Entity.RowKey));
            Assert.All(batches[index].Skip(1).Take(2), action =>
            {
                Assert.Equal(TableTransactionActionType.UpsertReplace, action.ActionType);
                Assert.Equal(version.MembershipVersion, Assert.IsType<SiloInstanceTableEntry>(action.Entity).MembershipVersion);
            });
        }

        var deletes = batches.SelectMany(batch => batch.Skip(3)).ToArray();
        Assert.Equal(rows.Select(row => row.Entity.RowKey), deletes.Select(action => action.Entity.RowKey));
        Assert.Equal(rows.Select(row => row.ETag), deletes.Select(action => action.ETag.ToString()));
        Assert.All(deletes, action => Assert.Equal(TableTransactionActionType.Delete, action.ActionType));
        Assert.Equal(4, storage.QueryCount);
        Assert.Equal(8, storage.VersionReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupConflictReselectsHeartbeatAndVoteRecency(bool refreshVote)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(0, DeadSilo("silo-1", "old")));
        var refreshed = DeadSilo("silo-1", "refreshed");
        if (refreshVote)
        {
            refreshed.Entity.SuspectingTimes = "2026-01-03 00:00:00.000 GMT";
        }
        else
        {
            refreshed.Entity.IAmAliveTime = "2026-01-03 00:00:00.000 GMT";
        }
        storage.AddQuery(FencedQuery(0, refreshed));
        var batches = new List<TableTransactionAction[]>();
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                batches.Add(call.Arg<IEnumerable<TableTransactionAction>>().ToArray());
                return Task.FromException<Response<IReadOnlyList<Response>>>(new RequestFailedException(412, "Changed row etag."));
            });

        await CreateManager(storage, client).CleanupDefunctSiloEntries(
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        var batch = Assert.Single(batches);
        Assert.Equal(4, batch.Length);
        Assert.Equal("legacy-0", batch[0].ETag.ToString());
        Assert.Equal("old", batch[3].ETag.ToString());
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatAfterCompactionPreservesAbsenceAndVersion(bool deletionRacesMerge)
    {
        var client = CreateHeartbeatClient();
        var current = DeadSilo("silo-1", "s1").Entity;
        current.ETag = new ETag("s1");
        _ = client.GetEntityIfExistsAsync<SiloInstanceTableEntry>(
            ClusterId, "silo-1", null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(deletionRacesMerge
                ? Response.FromValue(current, Substitute.For<Response>())
                : new MissingSiloResponse());
        var version = Version(7, "v7").Entity;
        _ = client.GetEntityAsync<SiloInstanceTableEntry>(
            ClusterId, SiloInstanceTableEntry.TABLE_VERSION_ROW, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromResult(Response.FromValue(version, Substitute.For<Response>())));
        _ = client.UpdateEntityAsync(
            Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response>(new RequestFailedException(404, "Retired silo.")));
        var manager = CreateManager(new ScriptedMembershipTableReadStorage(), client);
        var heartbeat = Silo("silo-1", "unused").Entity;
        heartbeat.IAmAliveTime = "2026-01-02 00:00:00.000 GMT";

        Assert.Null(await manager.MergeTableEntryAsync(heartbeat, TestContext.Current.CancellationToken));

        Assert.Equal("7", version.MembershipVersion);
        Assert.Equal(deletionRacesMerge ? 3 : 2, client.ReceivedCalls().Count());
        Assert.DoesNotContain(client.ReceivedCalls(), call => call.GetMethodInfo().Name is "AddEntityAsync" or "UpsertEntityAsync" or "SubmitTransactionAsync");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task HeartbeatInfrastructureFailuresRemainVisible(int status)
    {
        var client = CreateHeartbeatClient();
        var failure = new RequestFailedException(status, "infrastructure-failure");
        _ = client.GetEntityIfExistsAsync<SiloInstanceTableEntry>(
            string.Empty, string.Empty, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromException<NullableResponse<SiloInstanceTableEntry>>(failure));
        _ = client.GetEntityAsync<SiloInstanceTableEntry>(
            string.Empty, string.Empty, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromException<Response<SiloInstanceTableEntry>>(failure));
        var manager = CreateManager(new ScriptedMembershipTableReadStorage(), client);
        var heartbeat = Silo("silo-1", "unused").Entity;
        heartbeat.IAmAliveTime = "2026-01-02 00:00:00.000 GMT";

        var exception = await Assert.ThrowsAsync<RequestFailedException>(
            () => manager.MergeTableEntryAsync(heartbeat, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.All(client.ReceivedCalls(), call => Assert.Contains(call.GetMethodInfo().Name, new[] { "GetEntityIfExistsAsync", "GetEntityAsync" }));
    }

    [Theory]
    [InlineData(nameof(SiloInstanceTableEntry.StartTime))]
    [InlineData(nameof(SiloInstanceTableEntry.IAmAliveTime))]
    [InlineData(nameof(SiloInstanceTableEntry.SuspectingTimes))]
    public async Task CleanupUsesExclusiveTickPrecisionCutoff(string timestampField)
    {
        var cutoff = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        var row = DeadSilo("silo-1", "s1");
        const string timestamp = "2026-01-03 00:00:00.000 GMT";
        switch (timestampField)
        {
            case nameof(SiloInstanceTableEntry.StartTime): row.Entity.StartTime = timestamp; break;
            case nameof(SiloInstanceTableEntry.IAmAliveTime): row.Entity.IAmAliveTime = timestamp; break;
            case nameof(SiloInstanceTableEntry.SuspectingTimes): row.Entity.SuspectingTimes = timestamp; break;
        }
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(0, row));
        storage.AddQuery(FencedQuery(0, row));
        storage.AddQuery(FencedQuery(1));
        var batches = new List<TableTransactionAction[]>();
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                batches.Add(call.Arg<IEnumerable<TableTransactionAction>>().ToArray());
                return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>([], Substitute.For<Response>()));
            });
        var manager = CreateManager(storage, client);

        await manager.CleanupDefunctSiloEntries(cutoff, TestContext.Current.CancellationToken);
        Assert.Empty(batches);
        await manager.CleanupDefunctSiloEntries(cutoff.AddTicks(1), TestContext.Current.CancellationToken);

        var batch = Assert.Single(batches);
        Assert.Equal("1", Assert.IsType<SiloInstanceTableEntry>(batch[0].Entity).MembershipVersion);
        Assert.Equal("legacy-0", batch[0].ETag.ToString());
        Assert.Equal(TableTransactionActionType.Delete, batch[3].ActionType);
        Assert.Equal("s1", batch[3].ETag.ToString());
        Assert.Equal(3, storage.QueryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAtMaximumVersionOnlyFailsWhenRowsWouldBeDeleted(bool hasRows)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(hasRows
            ? FencedQuery(int.MaxValue, DeadSilo("silo-1", "s1"))
            : FencedQuery(int.MaxValue));
        var client = Substitute.For<TableClient>();
        var manager = CreateManager(storage, client);
        if (hasRows)
        {
            await Assert.ThrowsAsync<OverflowException>(() => manager.CleanupDefunctSiloEntries(
                DateTimeOffset.MaxValue, TestContext.Current.CancellationToken));
        }
        else
        {
            await manager.CleanupDefunctSiloEntries(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken);
        }

        Assert.DoesNotContain(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(TableClient.SubmitTransactionAsync));
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void BoundaryVersionRowsSortAroundMembershipRows(string address)
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(address), 11111), 1);
        var rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

        Assert.True(string.CompareOrdinal(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, rowKey) < 0);
        Assert.True(string.CompareOrdinal(rowKey, SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX) < 0);
    }

    private static OrleansSiloInstanceManager CreateManager(IMembershipTableReadStorage storage)
        => new(
            ClusterId,
            NullLoggerFactory.Instance,
            new AzureStorageClusteringOptions { TableName = TableName },
            storage);

    private static OrleansSiloInstanceManager CreateManager(IMembershipTableReadStorage storage, TableClient client)
    {
        var manager = CreateManager(storage);
        var dataManager = Assert.IsType<AzureTableDataManager<SiloInstanceTableEntry>>(
            typeof(OrleansSiloInstanceManager).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));
        typeof(AzureTableDataManager<SiloInstanceTableEntry>).GetProperty(nameof(AzureTableDataManager<SiloInstanceTableEntry>.Table))!
            .SetValue(dataManager, client);
        return manager;
    }

    private static MembershipTableQueryResult FencedQuery(
        int version,
        params (SiloInstanceTableEntry Entity, string ETag)[] entries)
        => Query(
            true,
            [
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, version, $"before-{version}"),
                .. entries,
                Version(version, $"legacy-{version}"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, version, $"after-{version}")
            ]);

    private static MembershipTableQueryResult Query(
        bool isPaginated,
        params (SiloInstanceTableEntry Entity, string ETag)[] entries)
        => new([.. entries], isPaginated);

    private static (SiloInstanceTableEntry Entity, string ETag) Version(int version, string etag)
        => BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW, version, etag);

    private static (SiloInstanceTableEntry Entity, string ETag) BoundaryVersion(
        string rowKey,
        int version,
        string etag)
        => (new()
        {
            PartitionKey = ClusterId,
            RowKey = rowKey,
            DeploymentId = ClusterId,
            MembershipVersion = version.ToString(),
        }, etag);

    private static (SiloInstanceTableEntry Entity, string ETag) Silo(string rowKey, string etag)
        => (new()
        {
            PartitionKey = ClusterId,
            RowKey = rowKey,
            DeploymentId = ClusterId,
        }, etag);

    private static (SiloInstanceTableEntry Entity, string ETag) DeadSilo(string rowKey, string etag)
    {
        var row = Silo(rowKey, etag);
        row.Entity.Status = nameof(SiloStatus.Dead);
        row.Entity.StartTime = "2026-01-01 00:00:00.000 GMT";
        row.Entity.IAmAliveTime = row.Entity.StartTime;
        return row;
    }

    private sealed class MissingSiloResponse : NullableResponse<SiloInstanceTableEntry>
    {
        public override bool HasValue => false;
        public override SiloInstanceTableEntry Value => throw new InvalidOperationException("The silo row is absent.");
        public override Response GetRawResponse() => Substitute.For<Response>();
    }

    private static TableClient CreateHeartbeatClient()
    {
        var client = Substitute.For<TableClient>();
        client.ReturnsForAll<Task<NullableResponse<SiloInstanceTableEntry>>>(
            Task.FromException<NullableResponse<SiloInstanceTableEntry>>(new InvalidOperationException("Unexpected silo read.")));
        client.ReturnsForAll<Task<Response<SiloInstanceTableEntry>>>(
            Task.FromException<Response<SiloInstanceTableEntry>>(new InvalidOperationException("Unexpected version read.")));
        return client;
    }

    private sealed class ScriptedMembershipTableReadStorage : IMembershipTableReadStorage
    {
        private readonly Queue<Func<MembershipTableQueryResult>> queries = new();
        private readonly Queue<(SiloInstanceTableEntry Entity, string ETag)> versions = new();
        private readonly Queue<string> operations = new();
        private readonly List<CancellationToken> cancellationTokens = new();

        public int QueryCount { get; private set; }
        public int VersionReadCount { get; private set; }
        public Action<int>? OnVersionRead { get; set; }

        public IReadOnlyList<CancellationToken> CancellationTokens => cancellationTokens;

        public void AddQuery(
            MembershipTableQueryResult result,
            (SiloInstanceTableEntry Entity, string ETag)? before = null,
            (SiloInstanceTableEntry Entity, string ETag)? after = null)
        {
            var version = result.Entries.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW);
            AddQuery(() => result, before ?? version, after ?? version);
        }

        public void AddQuery(
            Func<MembershipTableQueryResult> query,
            (SiloInstanceTableEntry Entity, string ETag) before,
            (SiloInstanceTableEntry Entity, string ETag) after)
        {
            operations.Enqueue("Version");
            operations.Enqueue("Query");
            operations.Enqueue("Version");
            queries.Enqueue(query);
            versions.Enqueue(before);
            versions.Enqueue(after);
        }

        public Task<(SiloInstanceTableEntry? Entity, string? ETag)> ReadTableVersionAsync(
            string partitionKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ClusterId, partitionKey);
            Assert.Equal("Version", operations.Dequeue());
            cancellationTokens.Add(cancellationToken);
            VersionReadCount++;
            var result = versions.Dequeue();
            OnVersionRead?.Invoke(VersionReadCount);
            return Task.FromResult<(SiloInstanceTableEntry? Entity, string? ETag)>(result);
        }

        public Task<MembershipTableQueryResult> ReadAllTableEntriesForPartitionAsync(
            string partitionKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ClusterId, partitionKey);
            Assert.Equal("Query", operations.Dequeue());
            cancellationTokens.Add(cancellationToken);
            QueryCount++;
            return Task.FromResult(queries.Dequeue()());
        }
    }
}
