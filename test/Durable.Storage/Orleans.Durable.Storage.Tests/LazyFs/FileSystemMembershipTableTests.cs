using System.Net;
using Orleans.Durable.Storage.FileSystem.MetaStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests.LazyFs;

/// <summary>
/// Unit tests for <see cref="FileSystemMembershipTable"/>, verifying ETag / table-version CAS and that
/// <see cref="FileSystemMembershipTable.UpdateIAmAlive"/> preserves table and row ETags as Orleans requires.
/// </summary>
public sealed class FileSystemMembershipTableTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clog-fs-membership-" + Guid.NewGuid().ToString("N"));

    public FileSystemMembershipTableTests() => _ = Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        _leaks.Dispose();
    }

    [Fact]
    public async Task MembershipTableSupportsInsertUpdateConflictDetectionAndIAmAlive()
    {
        var metaStore = new FileSystemMetaStore(_root, TimeProvider.System);
        var cluster = Guid.NewGuid();
        var table = new FileSystemMembershipTable(metaStore, cluster);

        await table.InitializeMembershipTable(tryInitTableVersion: true);
        var empty = await table.ReadAll();
        Assert.Empty(empty.Members);
        Assert.Equal(0, empty.Version.Version);

        var silo1 = SiloAddress.New(IPAddress.Loopback, 11111, generation: 1);
        var silo2 = SiloAddress.New(IPAddress.Loopback, 11112, generation: 1);
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;

        var entry1 = new MembershipEntry
        {
            SiloAddress = silo1,
            Status = SiloStatus.Joining,
            HostName = "localhost",
            SiloName = "silo-0",
            ProxyPort = 30000,
            StartTime = now,
            IAmAliveTime = now,
        };
        var inserted1 = await table.InsertRow(entry1, empty.Version.Next());
        Assert.True(inserted1);

        // Stale TableVersion must be rejected.
        var staleInsert = await table.InsertRow(
            new MembershipEntry
            {
                SiloAddress = silo2,
                Status = SiloStatus.Joining,
                HostName = "localhost",
                SiloName = "silo-1",
                ProxyPort = 30001,
                StartTime = now,
                IAmAliveTime = now,
            },
            empty.Version.Next());
        Assert.False(staleInsert);

        var afterInsert = await table.ReadAll();
        Assert.Single(afterInsert.Members);
        Assert.Equal(1, afterInsert.Version.Version);

        // UpdateIAmAlive must update IAmAliveTime without invalidating TableVersion or row ETag.
        var later = now.AddSeconds(10);
        await table.UpdateIAmAlive(new MembershipEntry { SiloAddress = silo1, IAmAliveTime = later });

        var rowAfterHeartbeat = await table.ReadRow(silo1);
        var (hbEntry, hbEtag) = Assert.Single(rowAfterHeartbeat.Members);
        Assert.Equal(later, hbEntry.IAmAliveTime);
        Assert.Equal(afterInsert.Version.VersionEtag, rowAfterHeartbeat.Version.VersionEtag);
        Assert.Equal(afterInsert.Members[0].Item2, hbEtag);

        // Using the pre-heartbeat TableVersion and row ETag to promote silo1 to Active must still succeed.
        var activeEntry = new MembershipEntry
        {
            SiloAddress = silo1,
            Status = SiloStatus.Active,
            HostName = "localhost",
            SiloName = "silo-0",
            ProxyPort = 30000,
            StartTime = now,
            IAmAliveTime = later,
        };
        Assert.True(await table.UpdateRow(activeEntry, afterInsert.Members[0].Item2, afterInsert.Version.Next()));

        // Wrong row ETag must fail.
        var afterActive = await table.ReadAll();
        Assert.False(await table.UpdateRow(activeEntry, "wrong-etag", afterActive.Version.Next()));

        // Mark dead and clean up defunct entries.
        var deadEntry = new MembershipEntry
        {
            SiloAddress = silo1,
            Status = SiloStatus.Dead,
            HostName = "localhost",
            SiloName = "silo-0",
            ProxyPort = 30000,
            StartTime = now,
            IAmAliveTime = now.AddMinutes(-5),
        };
        Assert.True(await table.UpdateRow(deadEntry, afterActive.Members[0].Item2, afterActive.Version.Next()));

        await table.CleanupDefunctSiloEntries(TimeProvider.System.GetUtcNow().AddMinutes(-1));
        var afterCleanup = await table.ReadAll();
        Assert.Empty(afterCleanup.Members);
    }
}
