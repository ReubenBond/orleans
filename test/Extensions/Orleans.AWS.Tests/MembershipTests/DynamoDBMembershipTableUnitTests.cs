using System.Globalization;
using System.Net;
using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [TestSuite("BVT")]
    [TestProvider("DynamoDB")]
    [TestArea("Membership")]
    public class DynamoDBMembershipTableUnitTests
    {
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
            var table = new DynamoDBMembershipTable(
                NullLoggerFactory.Instance,
                Options.Create(new DynamoDBClusteringOptions()),
                Options.Create(new ClusterOptions { ClusterId = "cluster" }));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            var token = cancellation.Token;
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var entry = new MembershipEntry { SiloAddress = silo };
            var version = new TableVersion(1, "etag");

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
            {
                "Initialize" => table.InitializeMembershipTableAsync(true, token),
                "Delete" => table.DeleteMembershipTableEntriesAsync("cluster", token),
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DeletesUseExpectedBackendApiAndForwardCancellation(bool cleanup)
        {
            using var client = new BatchDeleteClient();
            var table = CreateTable(client);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            var pending = DeleteEntries(table, cleanup, cancellation.Token);
            try
            {
                if (cleanup)
                {
                    Assert.Empty(client.Requests);
                    Assert.Equal(52, Assert.Single(client.TransactionRequests).TransactItems.Count);
                    var deletes = client.TransactionRequests.SelectMany(request => request.TransactItems).Where(item => item.Delete is not null).ToArray();
                    Assert.Equal(51, deletes.Length);
                    for (var index = 0; index < deletes.Length; index++)
                    {
                        var item = deletes[index];
                        Assert.Null(item.Put);
                        Assert.Null(item.Update);
                        Assert.Null(item.ConditionCheck);
                        var delete = Assert.IsType<Delete>(item.Delete);
                        Assert.Equal("membership", delete.TableName);
                        Assert.Equal("cluster", delete.Key[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S);
                        Assert.Equal($"silo-{index}", delete.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                        Assert.Equal(
                            $"{SiloInstanceRecord.STATUS_PROPERTY_NAME} = :dead AND {SiloInstanceRecord.ETAG_PROPERTY_NAME} = :etag AND {SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME} = :heartbeat",
                            delete.ConditionExpression);
                        Assert.Equal(((int)SiloStatus.Dead).ToString(CultureInfo.InvariantCulture), delete.ExpressionAttributeValues[":dead"].N);
                        Assert.Equal((index + 1).ToString(CultureInfo.InvariantCulture), delete.ExpressionAttributeValues[":etag"].N);
                        Assert.Equal("2026-01-01 00:00:00.000 GMT", delete.ExpressionAttributeValues[":heartbeat"].S);
                    }
                }
                else
                {
                    Assert.Empty(client.TransactionRequests);
                    Assert.Equal(new[] { 25, 25, 2 }, client.Requests.Select(request => request.RequestItems["membership"].Count));
                }

                Assert.Equal(cleanup ? 1 : 3, client.Tokens.Count);
                Assert.All(client.Tokens, token => Assert.Equal(cancellation.Token, token));
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                await pending;
            }

            if (cleanup)
            {
                Assert.Equal(1, client.Version);
                Assert.Equal(1, client.VersionEtag);
                Assert.Empty(client.Records);
                Assert.Equal([(1, 0)], client.Commits);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchCancellationWaitsForAlreadyStartedDeletes(bool cleanup)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var client = new BatchDeleteClient { OnFirstDeleteRequest = cancellation.Cancel };
            var table = CreateTable(client);

            var pending = DeleteEntries(table, cleanup, cancellation.Token);
            try
            {
                if (cleanup)
                {
                    Assert.Single(client.TransactionRequests);
                    Assert.Empty(client.Requests);
                }
                else
                {
                    Assert.Single(client.Requests);
                    Assert.Empty(client.TransactionRequests);
                }

                Assert.Equal(cancellation.Token, Assert.Single(client.Tokens));
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }
        }

        [Fact]
        public async Task CleanupConditionalConflictsPreserveConditionalDeletion()
        {
            using var client = new BatchDeleteClient
            {
                TransactionFailure = new TransactionCanceledException("conditional conflict")
                {
                    CancellationReasons = [new CancellationReason { Code = "ConditionalCheckFailed" }]
                }
            };
            client.OnFirstDeleteRequest = () => client.Records["silo-0"].IAmAliveTime = "2026-01-03 00:00:00.000 GMT";
            client.CompleteAll();
            var table = CreateTable(client);

            await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);

            Assert.Empty(client.Requests);
            Assert.Equal(new[] { 52, 51 }, client.TransactionRequests.Select(request => request.TransactItems.Count));
            Assert.Equal(1, client.Version);
            Assert.Equal(1, client.VersionEtag);
            Assert.Equal("silo-0", Assert.Single(client.Records).Key);
            Assert.Equal([(1, 1)], client.Commits);
            Assert.Equal(3, client.QueryCount);

            await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);
            Assert.Equal(2, client.TransactionRequests.Count);
            Assert.Equal(1, client.Version);
        }

        [Fact]
        public async Task CleanupStorageFailuresPropagate()
        {
            var failure = new AmazonDynamoDBException("Storage unavailable.");
            using var client = new BatchDeleteClient { TransactionFailure = failure };
            var table = CreateTable(client);

            var exception = await Assert.ThrowsAsync<AmazonDynamoDBException>(
                () => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken));

            Assert.Same(failure, exception);
            Assert.Empty(client.Requests);
            Assert.Single(client.TransactionRequests);
            Assert.Equal(0, client.Version);
            Assert.Equal(51, client.Records.Count);
            Assert.Empty(client.Commits);
        }

        [Fact]
        public async Task CleanupBatchesReserveVersionSlotAndAdvanceOncePerCommit()
        {
            using var client = new BatchDeleteClient { RowCount = 201 };
            client.CompleteAll();
            var table = CreateTable(client);

            await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { 100, 100, 4 }, client.TransactionRequests.Select(request => request.TransactItems.Count));
            Assert.Equal([(1, 102), (2, 3), (3, 0)], client.Commits);
            Assert.Equal(3, client.Version);
            Assert.Equal(3, client.VersionEtag);
            Assert.Empty(client.Records);
            Assert.Empty(client.Requests);
        }

        [Theory]
        [InlineData(nameof(SiloInstanceRecord.StartTime))]
        [InlineData(nameof(SiloInstanceRecord.IAmAliveTime))]
        [InlineData(nameof(SiloInstanceRecord.SuspectingTimes))]
        public async Task CleanupUsesExclusiveTickPrecisionCutoff(string timestampField)
        {
            var cutoff = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
            using var client = new BatchDeleteClient { RowCount = 1 };
            var row = client.Records["silo-0"];
            const string timestamp = "2026-01-03 00:00:00.000 GMT";
            switch (timestampField)
            {
                case nameof(SiloInstanceRecord.StartTime): row.StartTime = timestamp; break;
                case nameof(SiloInstanceRecord.IAmAliveTime): row.IAmAliveTime = timestamp; break;
                case nameof(SiloInstanceRecord.SuspectingTimes): row.SuspectingTimes = timestamp; break;
            }
            client.CompleteAll();
            var table = CreateTable(client);

            await table.CleanupDefunctSiloEntriesAsync(cutoff, TestContext.Current.CancellationToken);
            Assert.Empty(client.TransactionRequests);
            Assert.Equal(0, client.Version);
            Assert.Single(client.Records);
            await table.CleanupDefunctSiloEntriesAsync(cutoff.AddTicks(1), TestContext.Current.CancellationToken);

            Assert.Single(client.TransactionRequests);
            Assert.Empty(client.Records);
            Assert.Equal(1, client.Version);
            Assert.Equal(1, client.VersionEtag);
            Assert.Equal([(1, 0)], client.Commits);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CleanupAtMaximumVersionOnlyFailsWhenRowsWouldBeDeleted(bool hasRows)
        {
            using var client = new BatchDeleteClient
            {
                RowCount = hasRows ? 1 : 0,
                Version = int.MaxValue,
                VersionEtag = int.MaxValue
            };
            var table = CreateTable(client);

            if (hasRows)
            {
                await Assert.ThrowsAsync<OverflowException>(() => DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken));
            }
            else
            {
                await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);
            }

            Assert.Empty(client.TransactionRequests);
            Assert.Empty(client.Requests);
            Assert.Equal(hasRows ? 1 : 0, client.Records.Count);
            Assert.Equal(int.MaxValue, client.Version);
            Assert.Equal(int.MaxValue, client.VersionEtag);
        }

        private static Task DeleteEntries(DynamoDBMembershipTable table, bool cleanup, CancellationToken cancellationToken) =>
            cleanup
                ? table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), cancellationToken)
                : table.DeleteMembershipTableEntriesAsync("cluster", cancellationToken);

        [Fact]
        public async Task HeartbeatAfterCompactionPreservesAbsenceAndVersion()
        {
            using var client = new HeartbeatClient();
            var table = CreateTable(client);

            await table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken);

            Assert.Single(client.Updates);
            Assert.Contains("attribute_exists(DeploymentId)", client.Updates[0].ConditionExpression);
            Assert.Contains("attribute_exists(SiloIdentity)", client.Updates[0].ConditionExpression);
            Assert.Equal(new[] { "127.0.0.1-11111-1", SiloInstanceRecord.TABLE_VERSION_ROW },
                client.Reads.Select(request => request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S));
            Assert.Equal(7, client.Version.MembershipVersion);
            Assert.Equal(7, client.Version.ETag);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task HeartbeatInfrastructureFailuresRemainVisible(bool missingTable)
        {
            var failure = missingTable
                ? new ResourceNotFoundException("Missing membership table.")
                : new AmazonDynamoDBException("Access denied.");
            using var client = new HeartbeatClient { Failure = failure };
            var table = CreateTable(client);

            var exception = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(() => table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken));

            Assert.Same(failure, exception);
            Assert.Single(client.Updates);
            Assert.Empty(client.Reads);
        }

        [Fact]
        public async Task HeartbeatMissingMembershipHistoryFailsClosed()
        {
            using var client = new HeartbeatClient { VersionExists = false };
            var table = CreateTable(client);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken));

            Assert.Single(client.Updates);
            Assert.Equal(2, client.Reads.Count);
        }

        private static DynamoDBMembershipTable CreateTable(AmazonDynamoDBClient client)
        {
            var table = new DynamoDBMembershipTable(
                NullLoggerFactory.Instance,
                Options.Create(new DynamoDBClusteringOptions { TableName = "membership" }),
                Options.Create(new ClusterOptions { ClusterId = "cluster" }));
            var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
            var clientField = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<IDisposable>(clientField.GetValue(storage)).Dispose();
            clientField.SetValue(storage, client);
            typeof(DynamoDBMembershipTable).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, storage);
            return table;
        }

        private sealed class HeartbeatClient() : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
        {
            public AmazonDynamoDBException? Failure { get; init; }
            public bool VersionExists { get; init; } = true;
            public List<UpdateItemRequest> Updates { get; } = [];
            public List<GetItemRequest> Reads { get; } = [];
            public SiloInstanceRecord Version { get; } = new()
            {
                DeploymentId = "cluster",
                SiloIdentity = SiloInstanceRecord.TABLE_VERSION_ROW,
                MembershipVersion = 7,
                ETag = 7
            };

            public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(TestContext.Current.CancellationToken, cancellationToken);
                Updates.Add(request);
                return Task.FromException<UpdateItemResponse>(Failure ?? new ConditionalCheckFailedException("Silo row absent."));
            }

            public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(TestContext.Current.CancellationToken, cancellationToken);
                Assert.True(request.ConsistentRead);
                Reads.Add(request);
                return Task.FromResult(VersionExists && request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S == SiloInstanceRecord.TABLE_VERSION_ROW
                    ? new GetItemResponse { Item = Version.GetFields(includeKeys: true) }
                    : new GetItemResponse());
            }
        }

        private sealed class BatchDeleteClient() : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
        {
            private readonly List<TaskCompletionSource<BatchWriteItemResponse>> _completions = [];
            private readonly List<(TransactWriteItemsRequest Request, TaskCompletionSource<TransactWriteItemsResponse> Completion)> _transactionCompletions = [];
            private Dictionary<string, SiloInstanceRecord>? _records;
            private bool _released;

            public Action? OnFirstDeleteRequest { get; set; }
            public Exception? TransactionFailure { get; set; }
            public int RowCount { get; init; } = 51;
            public int Version { get; set; }
            public int VersionEtag { get; set; }
            public int QueryCount { get; private set; }
            public List<(int Version, int Rows)> Commits { get; } = [];
            public Dictionary<string, SiloInstanceRecord> Records => _records ??= Enumerable.Range(0, RowCount).Select(i => new SiloInstanceRecord
            {
                DeploymentId = "cluster",
                SiloIdentity = $"silo-{i}",
                Status = (int)SiloStatus.Dead,
                IAmAliveTime = "2026-01-01 00:00:00.000 GMT",
                ETag = i + 1
            }).ToDictionary(record => record.SiloIdentity);
            public List<BatchWriteItemRequest> Requests { get; } = [];
            public List<TransactWriteItemsRequest> TransactionRequests { get; } = [];
            public List<CancellationToken> Tokens { get; } = [];

            public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QueryCount++;
                var version = new SiloInstanceRecord
                {
                    DeploymentId = "cluster",
                    SiloIdentity = SiloInstanceRecord.TABLE_VERSION_ROW,
                    MembershipVersion = Version,
                    ETag = VersionEtag
                };
                return Task.FromResult(new QueryResponse
                {
                    Items = Records.Values.Append(version).Select(record => record.GetFields(includeKeys: true)).ToList(),
                    LastEvaluatedKey = [],
                });
            }

            public override Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                Tokens.Add(cancellationToken);
                var completion = new TaskCompletionSource<BatchWriteItemResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _completions.Add(completion);
                if (_released)
                {
                    completion.SetResult(new BatchWriteItemResponse());
                }

                if (Tokens.Count == 1)
                {
                    OnFirstDeleteRequest?.Invoke();
                }

                return completion.Task;
            }

            public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
                TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
            {
                TransactionRequests.Add(request);
                Tokens.Add(cancellationToken);
                if (Tokens.Count == 1)
                {
                    OnFirstDeleteRequest?.Invoke();
                }

                if (TransactionFailure is { } failure)
                {
                    TransactionFailure = null;
                    return Task.FromException<TransactWriteItemsResponse>(failure);
                }

                var completion = new TaskCompletionSource<TransactWriteItemsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_released)
                {
                    ApplyTransaction(request);
                    completion.SetResult(new TransactWriteItemsResponse());
                }
                else
                {
                    _transactionCompletions.Add((request, completion));
                }

                return completion.Task;
            }

            public void CompleteAll()
            {
                _released = true;
                foreach (var completion in _completions.ToArray())
                {
                    completion.TrySetResult(new BatchWriteItemResponse());
                }

                foreach (var (request, completion) in _transactionCompletions.ToArray())
                {
                    ApplyTransaction(request);
                    completion.TrySetResult(new TransactWriteItemsResponse());
                }
                _transactionCompletions.Clear();
            }

            private void ApplyTransaction(TransactWriteItemsRequest request)
            {
                var update = Assert.IsType<Update>(Assert.Single(request.TransactItems, item => item.Update is not null).Update);
                Assert.Equal("membership", update.TableName);
                Assert.Equal(SiloInstanceRecord.TABLE_VERSION_ROW, update.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                Assert.Equal($"{SiloInstanceRecord.ETAG_PROPERTY_NAME} = :currentETag", update.ConditionExpression);
                Assert.Equal(VersionEtag.ToString(CultureInfo.InvariantCulture), update.ExpressionAttributeValues[":currentETag"].N);
                var nextVersion = int.Parse(update.ExpressionAttributeValues[$":{SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME}"].N, CultureInfo.InvariantCulture);
                var nextEtag = int.Parse(update.ExpressionAttributeValues[$":{SiloInstanceRecord.ETAG_PROPERTY_NAME}"].N, CultureInfo.InvariantCulture);
                Assert.Equal(checked(Version + 1), nextVersion);
                Assert.Equal(checked(VersionEtag + 1), nextEtag);
                var deletes = request.TransactItems.Where(item => item.Delete is not null).Select(item => Assert.IsType<Delete>(item.Delete)).ToArray();
                Assert.NotEmpty(deletes);
                Assert.Equal(deletes.Length + 1, request.TransactItems.Count);
                foreach (var delete in deletes)
                {
                    var record = Records[delete.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S];
                    Assert.Equal(record.ETag.ToString(CultureInfo.InvariantCulture), delete.ExpressionAttributeValues[":etag"].N);
                    Assert.Equal(record.IAmAliveTime, delete.ExpressionAttributeValues[":heartbeat"].S);
                    Assert.Equal(((int)SiloStatus.Dead).ToString(CultureInfo.InvariantCulture), delete.ExpressionAttributeValues[":dead"].N);
                }

                foreach (var delete in deletes)
                {
                    Records.Remove(delete.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                }
                Version = nextVersion;
                VersionEtag = nextEtag;
                Commits.Add((Version, Records.Count));
            }
        }

        [Fact]
        public void SiloIsDefunct_ParsesPersistedTimestampUsingInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var record = new SiloInstanceRecord
                {
                    IAmAliveTime = "2026-09-03 20:00:00.000 GMT",
                    Status = (int)SiloStatus.Dead
                };
                var cutoff = new DateTimeOffset(2026, 9, 3, 21, 0, 0, TimeSpan.Zero);

                Assert.True(DynamoDBMembershipTable.SiloIsDefunct(record, cutoff));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }
    }
}
