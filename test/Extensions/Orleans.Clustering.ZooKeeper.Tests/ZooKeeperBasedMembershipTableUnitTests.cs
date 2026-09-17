using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    [TestSuite("BVT")]
    [TestProvider("ZooKeeper")]
    [TestArea("Membership")]
    public sealed class ZooKeeperBasedMembershipTableUnitTests
    {
        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperBasedMembershipTable(
                    null!,
                    CreateMembershipTableOptions(),
                    CreateClusterOptions()));

            Assert.Equal("logger", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullMembershipTableOptions_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperBasedMembershipTable(
                    NullLogger<ZooKeeperBasedMembershipTable>.Instance,
                    null!,
                    CreateClusterOptions()));

            Assert.Equal("membershipTableOptions", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullClusterOptions_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperBasedMembershipTable(
                    NullLogger<ZooKeeperBasedMembershipTable>.Instance,
                    CreateMembershipTableOptions(),
                    null!));

            Assert.Equal("clusterOptions", exception.ParamName);
        }

        [Fact]
        public void InsertRow_NullEntry_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.InsertRowAsync(null!, CreateTableVersion(), TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void InsertRow_NullTableVersion_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.InsertRowAsync(CreateMembershipEntry(), null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("tableVersion", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void InsertRow_NullEntryAndTableVersion_ThrowsForEntryFirst()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.InsertRowAsync(null!, null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullEntry_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(null!, "17", CreateTableVersion(), TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullTableVersion_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(CreateMembershipEntry(), "17", null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("tableVersion", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullEtag_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(CreateMembershipEntry(), null!, CreateTableVersion(), TestContext.Current.CancellationToken);
            });

            Assert.Equal("etag", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullEntryAndTableVersion_ThrowsForEntryFirst()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(null!, "17", null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateIAmAlive_NullEntry_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateIAmAliveAsync(null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UpdateIAmAlive_CompactedHeartbeat_CompletesWithoutCreatingRowsOrChangingVersion(bool duringWrite)
        {
            var entry = CreateMembershipEntry();
            entry.IAmAliveTime = DateTime.UnixEpoch.AddDays(1);
            var path = "/" + entry.SiloAddress.ToParsableString() + "/IAmAlive";
            var readCalls = 0;
            var writeCalls = 0;
            var nodes = new Dictionary<string, byte[]>
            {
                ["/"] = ZooKeeperBasedMembershipTable.Serialize(42)
            };
            if (duringWrite)
            {
                nodes[path] = ZooKeeperBasedMembershipTable.Serialize(DateTime.UnixEpoch);
            }

            var result = await ZooKeeperBasedMembershipTable.UpdateIAmAliveCoreAsync(
                entry,
                actualPath =>
                {
                    readCalls++;
                    Assert.Equal(path, actualPath);
                    return nodes.TryGetValue(actualPath, out var stored)
                        ? Task.FromResult(CreateHeartbeatResult(ZooKeeperBasedMembershipTable.Deserialize<DateTime>(stored), 7))
                        : Task.FromException<DataResult>(new KeeperException.NoNodeException(path));
                },
                (actualPath, data, version) =>
                {
                    writeCalls++;
                    Assert.Equal(path, actualPath);
                    Assert.Equal(7, version);
                    Assert.Equal(entry.IAmAliveTime, ZooKeeperBasedMembershipTable.Deserialize<DateTime>(data));
                    nodes.Remove(path);
                    nodes["/"] = ZooKeeperBasedMembershipTable.Serialize(43);
                    return Task.FromException<Stat>(new KeeperException.NoNodeException(path));
                },
                TestContext.Current.CancellationToken);

            Assert.True(result);
            var remaining = Assert.Single(nodes);
            Assert.Equal("/", remaining.Key);
            Assert.Equal(duringWrite ? 43 : 42, ZooKeeperBasedMembershipTable.Deserialize<int>(remaining.Value));
            Assert.Equal(1, readCalls);
            Assert.Equal(duringWrite ? 1 : 0, writeCalls);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task UpdateIAmAlive_InfrastructureFailure_PropagatesSameException(bool duringWrite, bool permissions)
        {
            var entry = CreateMembershipEntry();
            entry.IAmAliveTime = DateTime.UnixEpoch.AddDays(1);
            var path = "/" + entry.SiloAddress.ToParsableString() + "/IAmAlive";
            Exception failure = permissions
                ? new KeeperException.NoAuthException()
                : new KeeperException.ConnectionLossException();
            var readCalls = 0;
            var writeCalls = 0;

            var actual = await Record.ExceptionAsync(() => ZooKeeperBasedMembershipTable.UpdateIAmAliveCoreAsync(
                entry,
                actualPath =>
                {
                    readCalls++;
                    Assert.Equal(path, actualPath);
                    return duringWrite
                        ? Task.FromResult(CreateHeartbeatResult(DateTime.UnixEpoch, 7))
                        : Task.FromException<DataResult>(failure);
                },
                (actualPath, _, version) =>
                {
                    writeCalls++;
                    Assert.Equal(path, actualPath);
                    Assert.Equal(7, version);
                    return Task.FromException<Stat>(failure);
                },
                TestContext.Current.CancellationToken));

            Assert.Same(failure, actual);
            Assert.Equal(1, readCalls);
            Assert.Equal(duringWrite ? 1 : 0, writeCalls);
        }

        [Fact]
        public async Task UpdateIAmAlive_BadVersionThenCompaction_CompletesWithoutRetryingMissingNode()
        {
            var entry = CreateMembershipEntry();
            entry.IAmAliveTime = DateTime.UnixEpoch.AddDays(1);
            var path = "/" + entry.SiloAddress.ToParsableString() + "/IAmAlive";
            var reads = 0;
            var writes = 0;

            Assert.True(await ZooKeeperBasedMembershipTable.UpdateIAmAliveCoreAsync(
                entry,
                _ => ++reads == 1
                    ? Task.FromResult(CreateHeartbeatResult(DateTime.UnixEpoch, 7))
                    : Task.FromException<DataResult>(new KeeperException.NoNodeException(path)),
                (_, _, _) =>
                {
                    writes++;
                    return Task.FromException<Stat>(new KeeperException.BadVersionException(path));
                },
                TestContext.Current.CancellationToken));

            Assert.Equal(2, reads);
            Assert.Equal(1, writes);
        }

        [Theory]
        [InlineData(nameof(IMembershipTable.InitializeMembershipTableAsync))]
        [InlineData(nameof(IMembershipTable.DeleteMembershipTableEntriesAsync))]
        [InlineData(nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync))]
        [InlineData(nameof(IMembershipTable.ReadRowAsync))]
        [InlineData(nameof(IMembershipTable.ReadAllAsync))]
        [InlineData(nameof(IMembershipTable.InsertRowAsync))]
        [InlineData(nameof(IMembershipTable.UpdateRowAsync))]
        [InlineData(nameof(IMembershipTable.UpdateIAmAliveAsync))]
        public async Task MembershipOperations_PreCanceledToken_DoesNotCreateClient(string operation)
        {
            var sut = CreateSut("127.0.0.1:invalid-port");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            var cancellationToken = cancellation.Token;
            Func<Task> invoke = operation switch
            {
                nameof(IMembershipTable.InitializeMembershipTableAsync) => () => sut.InitializeMembershipTableAsync(true, cancellationToken),
                nameof(IMembershipTable.DeleteMembershipTableEntriesAsync) => () => sut.DeleteMembershipTableEntriesAsync("cluster-a", cancellationToken),
                nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync) => () => sut.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch, cancellationToken),
                nameof(IMembershipTable.ReadRowAsync) => () => sut.ReadRowAsync(CreateSiloAddress(), cancellationToken),
                nameof(IMembershipTable.ReadAllAsync) => () => sut.ReadAllAsync(cancellationToken),
                nameof(IMembershipTable.InsertRowAsync) => () => sut.InsertRowAsync(CreateMembershipEntry(), CreateTableVersion(), cancellationToken),
                nameof(IMembershipTable.UpdateRowAsync) => () => sut.UpdateRowAsync(CreateMembershipEntry(), "17", CreateTableVersion(), cancellationToken),
                nameof(IMembershipTable.UpdateIAmAliveAsync) => () => sut.UpdateIAmAliveAsync(CreateMembershipEntry(), cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(invoke);

            Assert.Equal(cancellationToken, exception.CancellationToken);
        }

        [Theory]
        [InlineData("localhost:2181", "cluster-a", "localhost:2181", "/cluster-a", "localhost:2181/cluster-a")]
        [InlineData("localhost:2181/", "/cluster-a", "localhost:2181/", "//cluster-a", "localhost:2181///cluster-a")]
        public void Constructor_ValidOptions_PreservesLiteralConnectionAndClusterPathComposition(
            string connectionString,
            string clusterId,
            string expectedRootConnectionString,
            string expectedClusterPath,
            string expectedDeploymentConnectionString)
        {
            var sut = CreateSut(connectionString, clusterId);

            Assert.Equal(expectedRootConnectionString, GetPrivateField<string>(sut, "rootConnectionString"));
            Assert.Equal(expectedClusterPath, GetPrivateField<string>(sut, "clusterPath"));
            Assert.Equal(expectedDeploymentConnectionString, GetPrivateField<string>(sut, "deploymentConnectionString"));
        }

        [Fact]
        public void ConvertToRowPath_ValidAddress_PrefixesParsableAddressWithSlash()
        {
            var address = CreateSiloAddress();

            var result = InvokePrivatePathMethod("ConvertToRowPath", address);

            Assert.Equal("/127.0.0.1:11111@12345", result);
            Assert.EndsWith(address.ToParsableString(), result, StringComparison.Ordinal);
        }

        [Fact]
        public void ConvertToRowIAmAlivePath_ValidAddress_AppendsIAmAliveSegment()
        {
            var address = CreateSiloAddress();

            var result = InvokePrivatePathMethod("ConvertToRowIAmAlivePath", address);

            Assert.Equal("/127.0.0.1:11111@12345/IAmAlive", result);
            Assert.Equal(InvokePrivatePathMethod("ConvertToRowPath", address) + "/IAmAlive", result);
        }

        private static DataResult CreateHeartbeatResult(DateTime time, int version) =>
            Assert.IsType<DataResult>(Activator.CreateInstance(
                typeof(DataResult),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [ZooKeeperBasedMembershipTable.Serialize(time), new Stat(0, 0, 0, 0, version, 0, 0, 0, 0, 0, 0)],
                culture: null));

        private static ZooKeeperBasedMembershipTable CreateSut(
            string connectionString = "sentinel.invalid:2181",
            string clusterId = "cluster-a") =>
            new(
                NullLogger<ZooKeeperBasedMembershipTable>.Instance,
                CreateMembershipTableOptions(connectionString),
                CreateClusterOptions(clusterId));

        private static IOptions<ZooKeeperClusteringSiloOptions> CreateMembershipTableOptions(
            string connectionString = "sentinel.invalid:2181") =>
            Options.Create(new ZooKeeperClusteringSiloOptions { ConnectionString = connectionString });

        private static IOptions<ClusterOptions> CreateClusterOptions(string clusterId = "cluster-a") =>
            Options.Create(new ClusterOptions { ClusterId = clusterId });

        private static MembershipEntry CreateMembershipEntry() =>
            new()
            {
                SiloAddress = CreateSiloAddress(),
                HostName = "host-a",
                SiloName = "silo-a",
                Status = SiloStatus.Active,
                ProxyPort = 30000
            };

        private static SiloAddress CreateSiloAddress() =>
            SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 12345);

        private static TableVersion CreateTableVersion() => new(18, "17");

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<T>(field.GetValue(instance));
        }

        private static string InvokePrivatePathMethod(string methodName, SiloAddress address)
        {
            var method = typeof(ZooKeeperBasedMembershipTable).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsType<string>(method.Invoke(null, [address]));
        }
    }
}
