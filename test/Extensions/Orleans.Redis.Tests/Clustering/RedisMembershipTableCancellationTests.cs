using System.Net;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NSubstitute;
using Orleans.Clustering.Redis;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Clustering;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestArea("Membership")]
[TestCategory("BVT")]
public sealed class RedisMembershipTableCancellationTests
{
    [Fact]
    public async Task Initialize_ConfiguredEntryExpiry_RejectsBeforeCreatingConnection()
    {
        var factoryCalls = 0;
        using var table = new RedisMembershipTable(
            Options.Create(new RedisClusteringOptions
            {
                EntryExpiry = TimeSpan.FromHours(1),
                CreateMultiplexer = _ =>
                {
                    factoryCalls++;
                    throw new InvalidOperationException("The connection factory must not be called.");
                }
            }),
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" }));

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(
            () => table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken));

        Assert.Contains("expire", exception.Message);
        Assert.Equal(0, factoryCalls);
        Assert.False(table.IsInitialized);
    }

    [Theory]
    [InlineData("Initialize")]
    [InlineData("Delete")]
    [InlineData("ReadAll")]
    [InlineData("ReadRow")]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    [InlineData("Cleanup")]
    public async Task Operations_PreCanceled_DoNotAccessBackend(string operation)
    {
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            throw new InvalidOperationException("The connection factory must not be called.");
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var token = cancellation.Token;
        var entry = new MembershipEntry { SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1) };
        var version = new TableVersion(1, "1");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "Initialize" => table.InitializeMembershipTableAsync(true, token),
            "Delete" => table.DeleteMembershipTableEntriesAsync("cluster", token),
            "ReadAll" => table.ReadAllAsync(token),
            "ReadRow" => table.ReadRowAsync(entry.SiloAddress, token),
            "Insert" => table.InsertRowAsync(entry, version, token),
            "Update" => table.UpdateRowAsync(entry, "0", version, token),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Equal(token, exception.CancellationToken);
        Assert.Equal(0, factoryCalls);
        Assert.False(table.IsInitialized);
    }

    [Fact]
    public async Task Initialize_CanceledDuringFactory_DisposesLateOwnedConnection()
    {
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.DisposeAsync().Returns(_ =>
        {
            disposed.SetResult();
            return ValueTask.CompletedTask;
        });
        using var table = CreateTable(_ => creation.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(initialization.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initialization.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(creation.Task.IsCompleted);
        Assert.False(table.IsInitialized);
        table.Dispose();

        creation.SetResult((muxer, false));
        await disposed.Task.WaitAsync(TestContext.Current.CancellationToken);

        await muxer.Received(1).DisposeAsync();
        muxer.DidNotReceive().GetDatabase(Arg.Any<int>(), Arg.Any<object>());
        Assert.False(table.IsInitialized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_CanceledDuringTableVersion_DoesNotPersistOrPublishConnection(bool isShared)
    {
        var versionWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Substitute.For<IDatabase>();
        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
            .Returns(versionWrite.Task);
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        muxer.DisposeAsync().Returns(ValueTask.CompletedTask);
        using var table = CreateTable(_ => Task.FromResult((muxer, isShared)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(initialization.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initialization.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(table.IsInitialized);
        Assert.False(versionWrite.Task.IsCompleted);
        await muxer.Received(isShared ? 0 : 1).DisposeAsync();
        await database.DidNotReceive().KeyPersistAsync(Arg.Any<RedisKey>());
        versionWrite.SetResult(true);
    }

    [Fact]
    public async Task Initialize_CompletedStorageWork_PublishesConnectionAfterCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var database = Substitute.For<IDatabase>();
        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
            .Returns(Task.FromResult(true));
        database.KeyPersistAsync(Arg.Any<RedisKey>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        });
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        using var table = CreateTable(_ => Task.FromResult((muxer, true)));

        await table.InitializeMembershipTableAsync(true, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(table.IsInitialized);
        await muxer.DidNotReceive().DisposeAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Initialize_Repeated_ReusesConnectionAndBootstrapsAfterDelete(bool isShared, bool disposeAsync)
    {
        var backend = new InitializationBackend();
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return Task.FromResult((backend.Multiplexer, isShared));
        });
        var token = TestContext.Current.CancellationToken;

        await table.InitializeMembershipTableAsync(false, token);
        Assert.Empty(backend.Rows);
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
        backend.Rows["Version"] = "17";
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(17, (await table.ReadAllAsync(token)).Version.Version);

        await table.DeleteMembershipTableEntriesAsync("cluster", token);
        Assert.Empty(backend.Rows);
        await table.InitializeMembershipTableAsync(true, token);

        Assert.True(table.IsInitialized);
        Assert.Equal("Version", Assert.Single(backend.Rows).Key.ToString());
        Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
        Assert.Equal(0, (await table.ReadAllAsync(token)).Version.Version);
        Assert.Equal(1, factoryCalls);
        backend.Multiplexer.Received(1).GetDatabase(Arg.Any<int>(), Arg.Any<object>());
        await backend.Database.Received(3).HashSetAsync(Arg.Any<RedisKey>(), "Version", "0", When.NotExists);
        await backend.Database.Received(3).KeyPersistAsync(Arg.Any<RedisKey>());
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.DidNotReceive().DisposeAsync();

        if (disposeAsync)
        {
            await table.DisposeAsync();
        }
        else
        {
            table.Dispose();
        }

        Assert.False(table.IsInitialized);
        table.Dispose();
        backend.Multiplexer.Received(!isShared && !disposeAsync ? 1 : 0).Dispose();
        await backend.Multiplexer.Received(!isShared && disposeAsync ? 1 : 0).DisposeAsync();
    }

    [Fact]
    public async Task Initialize_OverlappingCalls_CreateOneConnectionAndBootstrapEachCall()
    {
        var backend = new InitializationBackend();
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return creation.Task;
        });
        var token = TestContext.Current.CancellationToken;
        var first = table.InitializeMembershipTableAsync(true, token);
        var second = table.InitializeMembershipTableAsync(true, token);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, factoryCalls);
        creation.SetResult((backend.Multiplexer, false));
        await Task.WhenAll(first, second).WaitAsync(token);

        Assert.Equal(1, factoryCalls);
        await backend.Database.Received(2).HashSetAsync(Arg.Any<RedisKey>(), "Version", "0", When.NotExists);
        await backend.Database.Received(2).KeyPersistAsync(Arg.Any<RedisKey>());
        Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
    }

    [Fact]
    public async Task Initialize_CanceledWhileWaiting_DoesNotCreateConnectionOrBootstrap()
    {
        var backend = new InitializationBackend();
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return creation.Task;
        });
        var token = TestContext.Current.CancellationToken;
        var first = table.InitializeMembershipTableAsync(true, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var waiting = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, factoryCalls);
        creation.SetResult((backend.Multiplexer, false));
        await first.WaitAsync(token);

        Assert.Equal(1, factoryCalls);
        Assert.True(table.IsInitialized);
        await backend.Database.Received(1).HashSetAsync(Arg.Any<RedisKey>(), "Version", "0", When.NotExists);
    }

    [Fact]
    public async Task Initialize_RepeatedCancellation_RetainsOwnedConnectionForRetry()
    {
        var backend = new InitializationBackend();
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return Task.FromResult((backend.Multiplexer, false));
        });
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var versionWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
            .Returns(versionWrite.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var repeated = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(repeated.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repeated.WaitAsync(token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(table.IsInitialized);
        Assert.Equal(1, factoryCalls);
        Assert.False(versionWrite.Task.IsCompleted);
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.DidNotReceive().DisposeAsync();
        await backend.Database.Received(1).KeyPersistAsync(Arg.Any<RedisKey>());

        versionWrite.SetResult(false);
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(1, factoryCalls);
        await backend.Database.Received(2).KeyPersistAsync(Arg.Any<RedisKey>());
        Assert.Equal(0, (await table.ReadAllAsync(token)).Version.Version);
    }

    [Fact]
    public async Task Initialize_RepeatedFailure_RetainsOwnedConnectionForRetry()
    {
        var backend = new InitializationBackend();
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return Task.FromResult((backend.Multiplexer, false));
        });
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var failure = new InvalidOperationException("Bootstrap failed.");
        backend.Database.KeyPersistAsync(Arg.Any<RedisKey>())
            .Returns(Task.FromException<bool>(failure), Task.FromResult(true));

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.InitializeMembershipTableAsync(true, token)));
        Assert.True(table.IsInitialized);
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.DidNotReceive().DisposeAsync();

        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(1, factoryCalls);
        await backend.Database.Received(3).KeyPersistAsync(Arg.Any<RedisKey>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_MaxVersion_PreservesTableWithoutStartingTransaction(bool eligible)
    {
        var backend = new InitializationBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, false)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(false, token);
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
            Status = eligible ? SiloStatus.Dead : SiloStatus.Active,
            StartTime = time,
            IAmAliveTime = time
        };
        RedisValue row = JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings);
        var key = entry.SiloAddress.ToString();
        backend.Rows["Version"] = "2147483647";
        backend.Rows[key] = row;

        if (eligible)
        {
            await Assert.ThrowsAsync<OverflowException>(() => table.CleanupDefunctSiloEntriesAsync(time.AddDays(1), token));
        }
        else
        {
            await table.CleanupDefunctSiloEntriesAsync(time.AddDays(1), token);
        }

        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"2147483647", backend.Rows["Version"]);
        Assert.Equal(row, backend.Rows[key]);
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Fact]
    public async Task UpdateIAmAlive_CompactedRow_CompletesWithoutCreatingRowOrChangingVersion()
    {
        var backend = new InitializationBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, false)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(false, token);
        backend.Rows["Version"] = "42";
        backend.Database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(RedisValue.Null);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
            IAmAliveTime = DateTime.UnixEpoch.AddDays(1)
        };

        await table.UpdateIAmAliveAsync(entry, token);

        Assert.Equal((RedisValue)"42", Assert.Single(backend.Rows).Value);
        Assert.False(backend.Rows.ContainsKey(entry.SiloAddress.ToString()));
        await backend.Database.Received(1).HashGetAsync(Arg.Any<RedisKey>(), entry.SiloAddress.ToString());
        backend.Database.DidNotReceive().CreateTransaction();
        await backend.Database.DidNotReceive().HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>());
    }

    [Fact]
    public async Task UpdateIAmAlive_CompactionWinsConditionalWrite_CompletesWithoutResurrection()
    {
        var backend = new InitializationBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, false)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(false, token);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
            Status = SiloStatus.Dead,
            IAmAliveTime = DateTime.UnixEpoch
        };
        var key = entry.SiloAddress.ToString();
        RedisValue captured = JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings);
        backend.Rows["Version"] = "42";
        backend.Rows[key] = captured;
        backend.Database.HashGetAsync(Arg.Any<RedisKey>(), key).Returns(captured, RedisValue.Null);
        var transaction = Substitute.For<ITransaction>();
        backend.Database.CreateTransaction().Returns(transaction);
        transaction.ExecuteAsync().Returns(_ =>
        {
            backend.Rows.Remove(key);
            backend.Rows["Version"] = "43";
            return Task.FromResult(false);
        });
        entry.IAmAliveTime = entry.IAmAliveTime.AddDays(1);

        await table.UpdateIAmAliveAsync(entry, token);

        Assert.Equal((RedisValue)"43", Assert.Single(backend.Rows).Value);
        Assert.False(backend.Rows.ContainsKey(key));
        await backend.Database.Received(2).HashGetAsync(Arg.Any<RedisKey>(), key);
        backend.Database.Received(1).CreateTransaction();
        var clusterKey = RedisClusteringOptions.DefaultCreateRedisKey(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" });
        transaction.Received(1).AddCondition(Arg.Is<Condition>(
            condition => condition.ToString() == Condition.HashEqual(clusterKey, key, captured).ToString()));
        await transaction.Received(1).HashSetAsync(Arg.Any<RedisKey>(), key, Arg.Any<RedisValue>());
        await transaction.DidNotReceive().HashSetAsync(Arg.Any<RedisKey>(), "Version", Arg.Any<RedisValue>());
        await transaction.Received(1).ExecuteAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdateIAmAlive_InfrastructureFailure_PropagatesSameException(bool duringWrite, bool permissions)
    {
        var backend = new InitializationBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, false)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(false, token);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
            Status = SiloStatus.Dead,
            IAmAliveTime = DateTime.UnixEpoch
        };
        var key = entry.SiloAddress.ToString();
        RedisValue captured = JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings);
        backend.Rows["Version"] = "42";
        backend.Rows[key] = captured;
        Exception failure = permissions
            ? new RedisServerException("NOPERM this user has no permissions to access the key")
            : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection unavailable.");
        var transaction = Substitute.For<ITransaction>();
        backend.Database.CreateTransaction().Returns(transaction);
        backend.Database.HashGetAsync(Arg.Any<RedisKey>(), key).Returns(duringWrite
            ? Task.FromResult(captured)
            : Task.FromException<RedisValue>(failure));
        transaction.ExecuteAsync().Returns(Task.FromException<bool>(failure));
        entry.IAmAliveTime = entry.IAmAliveTime.AddDays(1);

        var actual = await Record.ExceptionAsync(() => table.UpdateIAmAliveAsync(entry, token));

        Assert.Same(failure, actual);
        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"42", backend.Rows["Version"]);
        Assert.Equal(captured, backend.Rows[key]);
        await backend.Database.Received(1).HashGetAsync(Arg.Any<RedisKey>(), key);
        backend.Database.Received(duringWrite ? 1 : 0).CreateTransaction();
        await transaction.Received(duringWrite ? 1 : 0).ExecuteAsync();
        await transaction.DidNotReceive().HashSetAsync(Arg.Any<RedisKey>(), "Version", Arg.Any<RedisValue>());
    }

    [Fact]
    public async Task ReadRow_CanceledDuringTransaction_DoesNotReturnSuccess()
    {
        var execution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = Substitute.For<ITransaction>();
        transaction.ExecuteAsync().Returns(execution.Task);
        var database = Substitute.For<IDatabase>();
        database.CreateTransaction().Returns(transaction);
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        using var table = CreateTable(_ => Task.FromResult((muxer, true)));
        await table.InitializeMembershipTableAsync(false, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var read = table.ReadRowAsync(SiloAddress.New(IPAddress.Loopback, 11111, 1), cancellation.Token);
        Assert.False(read.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => read.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(execution.Task.IsCompleted);
        _ = transaction.Received(1).ExecuteAsync();
        execution.SetResult(true);
    }

    private static RedisMembershipTable CreateTable(Func<RedisClusteringOptions, Task<(IConnectionMultiplexer, bool)>> factory) =>
        new(
            Options.Create(new RedisClusteringOptions
            {
                CreateMultiplexer = factory
            }),
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" }));

    private sealed class InitializationBackend
    {
        public Dictionary<RedisValue, RedisValue> Rows { get; } = [];
        public IDatabase Database { get; } = Substitute.For<IDatabase>();
        public IConnectionMultiplexer Multiplexer { get; } = Substitute.For<IConnectionMultiplexer>();

        public InitializationBackend()
        {
            Database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
                .Returns(call => Task.FromResult(Rows.TryAdd(call.ArgAt<RedisValue>(1), call.ArgAt<RedisValue>(2))));
            Database.KeyPersistAsync(Arg.Any<RedisKey>()).Returns(Task.FromResult(true));
            Database.KeyDeleteAsync(Arg.Any<RedisKey>()).Returns(_ =>
            {
                var existed = Rows.Count > 0;
                Rows.Clear();
                return Task.FromResult(existed);
            });
            Database.HashGetAllAsync(Arg.Any<RedisKey>())
                .Returns(_ => Task.FromResult(Rows.Select(pair => new HashEntry(pair.Key, pair.Value)).ToArray()));
            Multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Database);
            Multiplexer.DisposeAsync().Returns(ValueTask.CompletedTask);
        }
    }
}
