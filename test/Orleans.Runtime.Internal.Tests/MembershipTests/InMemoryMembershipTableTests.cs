using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for the in-memory membership table used by development clustering.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("BVT"), TestCategory("Membership")]
    public partial class InMemoryMembershipTableTests : IDisposable
    {
        private readonly InMemoryMembershipTable table;

        public InMemoryMembershipTableTests()
        {
            var services = new ServiceCollection();
            services.AddSerializer();
            var serviceProvider = _services = services.BuildServiceProvider();
            var deepCopier = serviceProvider.GetRequiredService<DeepCopier>();
            table = new InMemoryMembershipTable(deepCopier);
        }

        [Fact]
        public void CleanupDefunctSiloEntries_RemovesOnlyDeadOldEntries()
        {
            var tableVersion = table.ReadTableVersion();

            // Add old dead entry (should be removed)
            var deadEntry = CreateEntry(SiloStatus.Dead, daysOld: 10);
            Assert.True(table.Insert(deadEntry, tableVersion.Next()));
            tableVersion = table.ReadTableVersion();

            // Non-dead entries remain part of the versioned membership view.
            var joiningEntry = CreateEntry(SiloStatus.Joining, daysOld: 10);
            Assert.True(table.Insert(joiningEntry, tableVersion.Next()));
            tableVersion = table.ReadTableVersion();

            // Add old active entry (should NOT be removed)
            var activeEntry = CreateEntry(SiloStatus.Active, daysOld: 10);
            Assert.True(table.Insert(activeEntry, tableVersion.Next()));
            tableVersion = table.ReadTableVersion();

            // Add new entry with current timestamp (should NOT be removed regardless of status)
            var newEntry = CreateEntry(SiloStatus.Dead, daysOld: 0);
            Assert.True(table.Insert(newEntry, tableVersion.Next()));
            var beforeCleanup = table.ReadTableVersion();

            var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
            table.CleanupDefunctSiloEntries(cutoff);

            var data = table.ReadAll();
            Assert.Equal(beforeCleanup.Version + 1, data.Version.Version);
            Assert.NotEqual(beforeCleanup.VersionEtag, data.Version.VersionEtag);
            Assert.Equal(3, data.Members.Count);
            Assert.Contains(data.Members, m => m.Item1.SiloAddress.Equals(activeEntry.SiloAddress));
            Assert.Contains(data.Members, m => m.Item1.SiloAddress.Equals(newEntry.SiloAddress));
            Assert.DoesNotContain(data.Members, m => m.Item1.SiloAddress.Equals(deadEntry.SiloAddress));
            Assert.Contains(data.Members, m => m.Item1.SiloAddress.Equals(joiningEntry.SiloAddress));
        }

        [Fact]
        public void CleanupDefunctSiloEntries_PreservesEveryNonDeadStatus()
        {
            foreach (var status in Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.None))
            {
                var entry = CreateEntry(status, daysOld: 10);
                Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
            }

            var beforeCleanup = table.ReadTableVersion();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
            table.CleanupDefunctSiloEntries(cutoff);

            var data = table.ReadAll();
            Assert.Equal(beforeCleanup.Version + 1, data.Version.Version);
            Assert.NotEqual(beforeCleanup.VersionEtag, data.Version.VersionEtag);
            Assert.Equal(
                Enum.GetValues<SiloStatus>().Where(status => status is not SiloStatus.None and not SiloStatus.Dead).Order(),
                data.Members.Select(row => row.Item1.Status).Order());
            table.CleanupDefunctSiloEntries(cutoff);
            Assert.Equal(data.Version, table.ReadTableVersion());
        }

        [Fact]
        public void CleanupDefunctSiloEntries_PreservesActiveEntries()
        {
            var tableVersion = table.ReadTableVersion();
            var activeEntry = CreateEntry(SiloStatus.Active, daysOld: 30);
            Assert.True(table.Insert(activeEntry, tableVersion.Next()));
            var beforeCleanup = table.ReadAll();

            var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
            table.CleanupDefunctSiloEntries(cutoff);

            var data = table.ReadAll();
            Assert.Single(data.Members);
            Assert.Equal(beforeCleanup.Version, data.Version);
            Assert.Equal(Assert.Single(beforeCleanup.Members).Item2, Assert.Single(data.Members).Item2);
            Assert.Equal(activeEntry.SiloAddress, data.Members[0].Item1.SiloAddress);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Writes_CopyMutableEntryAndSuspectTimes(bool update)
        {
            var entry = CreateEntry(SiloStatus.Joining, daysOld: 10);
            var suspector = SiloAddress.FromParsableString("127.0.0.1:20000@1");
            entry.AddSuspector(suspector, DateTime.UnixEpoch);
            Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
            if (update)
            {
                var current = table.Read(entry.SiloAddress);
                entry.Status = SiloStatus.Active;
                Assert.True(table.Update(entry, Assert.Single(current.Members).Item2, current.Version.Next()));
            }

            var expectedStatus = entry.Status;
            var expectedHeartbeat = entry.IAmAliveTime;
            var version = table.ReadTableVersion();
            var etag = Assert.Single(table.Read(entry.SiloAddress).Members).Item2;
            entry.Status = SiloStatus.Dead;
            entry.IAmAliveTime = expectedHeartbeat.AddDays(1);
            entry.SuspectTimes!.Clear();

            var after = table.Read(entry.SiloAddress);
            var stored = Assert.Single(after.Members);
            Assert.NotSame(entry, stored.Item1);
            Assert.Equal(version, after.Version);
            Assert.Equal(etag, stored.Item2);
            Assert.Equal(expectedStatus, stored.Item1.Status);
            Assert.Equal(expectedHeartbeat, stored.Item1.IAmAliveTime);
            Assert.Equal(Tuple.Create(suspector, DateTime.UnixEpoch), Assert.Single(stored.Item1.SuspectTimes!));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reads_ReturnIndependentMutableSnapshots(bool readAll)
        {
            var entry = CreateEntry(SiloStatus.Joining, daysOld: 10);
            var suspector = SiloAddress.FromParsableString("127.0.0.1:20000@1");
            entry.AddSuspector(suspector, DateTime.UnixEpoch);
            Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
            var snapshot = readAll ? table.ReadAll() : table.Read(entry.SiloAddress);
            var snapshotRow = Assert.Single(snapshot.Members);
            var heartbeat = entry.Copy();
            heartbeat.IAmAliveTime = entry.IAmAliveTime.AddHours(1);
            table.UpdateIAmAlive(heartbeat);

            Assert.Equal(entry.IAmAliveTime, snapshotRow.Item1.IAmAliveTime);
            snapshotRow.Item1.Status = SiloStatus.Dead;
            snapshotRow.Item1.IAmAliveTime = DateTime.UnixEpoch;
            snapshotRow.Item1.SuspectTimes!.Clear();

            var after = table.Read(entry.SiloAddress);
            var stored = Assert.Single(after.Members).Item1;
            Assert.Equal(snapshot.Version, after.Version);
            Assert.Equal(SiloStatus.Joining, stored.Status);
            Assert.Equal(heartbeat.IAmAliveTime, stored.IAmAliveTime);
            Assert.Equal(Tuple.Create(suspector, DateTime.UnixEpoch), Assert.Single(stored.SuspectTimes!));
        }

        [Fact]
        public void Updates_PreserveMaximumHeartbeatAcrossDelayedHeartbeatAndStatusChange()
        {
            var entry = CreateEntry(SiloStatus.Joining, daysOld: 10);
            var originalHeartbeat = entry.IAmAliveTime;
            Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
            var initialVersion = table.ReadTableVersion();
            var heartbeat = entry.Copy();
            heartbeat.IAmAliveTime = entry.IAmAliveTime.AddHours(2);
            var maximum = heartbeat.IAmAliveTime;
            table.UpdateIAmAlive(heartbeat);
            heartbeat.IAmAliveTime = entry.IAmAliveTime.AddHours(1);
            table.UpdateIAmAlive(heartbeat);
            var beforeUpdate = table.Read(entry.SiloAddress);
            Assert.Equal(initialVersion, beforeUpdate.Version);
            Assert.Equal(maximum, Assert.Single(beforeUpdate.Members).Item1.IAmAliveTime);

            entry.Status = SiloStatus.Active;
            Assert.True(table.Update(entry, Assert.Single(beforeUpdate.Members).Item2, beforeUpdate.Version.Next()));

            var after = table.Read(entry.SiloAddress);
            var stored = Assert.Single(after.Members).Item1;
            Assert.Equal(beforeUpdate.Version.Version + 1, after.Version.Version);
            Assert.Equal(SiloStatus.Active, stored.Status);
            Assert.Equal(maximum, stored.IAmAliveTime);
            Assert.Equal(originalHeartbeat, entry.IAmAliveTime);
        }

        [Fact]
        public void CleanupDefunctSiloEntries_PreservesRecentlyDeclaredDeadWithStaleHeartbeat()
        {
            var entry = CreateEntry(SiloStatus.Active, daysOld: 10);
            entry.StartTime = entry.IAmAliveTime = DateTime.UnixEpoch;
            Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
            var beforeUpdate = table.Read(entry.SiloAddress);
            entry.Status = SiloStatus.Dead;
            entry.AddSuspector(SiloAddress.FromParsableString("127.0.0.1:20000@1"), DateTime.UnixEpoch.AddDays(10));
            Assert.True(table.Update(entry, Assert.Single(beforeUpdate.Members).Item2, beforeUpdate.Version.Next()));
            var beforeCleanup = table.Read(entry.SiloAddress);

            table.CleanupDefunctSiloEntries(DateTimeOffset.UnixEpoch.AddDays(5));

            var after = table.Read(entry.SiloAddress);
            var stored = Assert.Single(after.Members);
            Assert.Equal(beforeCleanup.Version, after.Version);
            Assert.Equal(Assert.Single(beforeCleanup.Members).Item2, stored.Item2);
            Assert.Equal(SiloStatus.Dead, stored.Item1.Status);
            Assert.Equal(DateTime.UnixEpoch, stored.Item1.IAmAliveTime);
            Assert.Equal(DateTime.UnixEpoch.AddDays(10), Assert.Single(stored.Item1.SuspectTimes!).Item2);
        }

        private static int _portCounter = 10000;

        private static MembershipEntry CreateEntry(SiloStatus status, int daysOld)
        {
            var port = Interlocked.Increment(ref _portCounter);
            var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), 0);
            var now = DateTime.UtcNow.AddDays(-daysOld);
            return new MembershipEntry
            {
                SiloAddress = siloAddress,
                HostName = "localhost",
                SiloName = $"TestSilo-{port}",
                Status = status,
                StartTime = now,
                IAmAliveTime = now,
            };
        }
    }
}
