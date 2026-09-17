using System.Net;
using System.Reflection;
using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Orleans.Clustering.Firestore.Tests;

[TestSuite("BVT")]
[TestProvider("GoogleCloud")]
[TestArea("Membership")]
[TestCategory("BVT")]
public sealed class FirestoreMembershipHeartbeatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatAfterCompactionPreservesAbsenceAndVersion(bool deletionRacesCommit)
    {
        var client = new HeartbeatClient { DeletionRacesCommit = deletionRacesCommit };
        var table = CreateTable(client);

        await table.UpdateIAmAliveAsync(Entry(), TestContext.Current.CancellationToken);

        Assert.Equal(deletionRacesCommit ? 2 : 1, client.Transactions);
        Assert.Equal(deletionRacesCommit ? 3 : 2, client.Reads.Count);
        Assert.EndsWith($"/{Utils.SanitizeId("cluster")}", client.Reads[^1].Documents.Single());
        Assert.Equal(deletionRacesCommit ? 1 : 0, client.AttemptedWrites.Count);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(7, client.Version);
    }

    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task HeartbeatInfrastructureFailuresRemainVisible(StatusCode status)
    {
        var failure = new RpcException(new Status(status, "infrastructure-failure"));
        var client = new HeartbeatClient { Failure = failure };
        var table = CreateTable(client);

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => table.UpdateIAmAliveAsync(Entry(), TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Empty(client.AttemptedWrites);
        Assert.Empty(client.CommittedWrites);
    }

    [Fact]
    public async Task HeartbeatMissingMembershipHistoryFailsClosed()
    {
        var client = new HeartbeatClient { VersionExists = false };
        var table = CreateTable(client);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => table.UpdateIAmAliveAsync(Entry(), TestContext.Current.CancellationToken));

        Assert.Equal(2, client.Reads.Count);
        Assert.Empty(client.AttemptedWrites);
        Assert.Empty(client.CommittedWrites);
    }

    private static MembershipEntry Entry() => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
        IAmAliveTime = DateTime.UnixEpoch.AddHours(1)
    };

    private static FirestoreMembershipTable CreateTable(HeartbeatClient client)
    {
        var options = new FirestoreOptions { ProjectId = "test-project", EmulatorHost = "127.0.0.1:1" };
        var storage = new FirestoreDataManager("Cluster", Utils.SanitizeId("cluster"), options, NullLogger<FirestoreDataManager>.Instance);
        typeof(FirestoreDataManager).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(storage, FirestoreDb.Create(options.ProjectId, client));
        var table = new FirestoreMembershipTable(
            NullLoggerFactory.Instance, Options.Create(options), Options.Create(new ClusterOptions { ClusterId = "cluster" }));
        typeof(FirestoreMembershipTable).GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, storage);
        return table;
    }

    private sealed class HeartbeatClient : FirestoreClient
    {
        public override FirestoreSettings Settings => FirestoreSettings.GetDefault();
        public bool DeletionRacesCommit { get; init; }
        public bool VersionExists { get; init; } = true;
        public RpcException? Failure { get; init; }
        public int Transactions { get; private set; }
        public int Version { get; } = 7;
        public List<BatchGetDocumentsRequest> Reads { get; } = [];
        public List<Write> AttemptedWrites { get; } = [];
        public List<Write> CommittedWrites { get; } = [];

        public override Task<BeginTransactionResponse> BeginTransactionAsync(BeginTransactionRequest request, CallSettings? callSettings = null)
        {
            Transactions++;
            return Task.FromResult(new BeginTransactionResponse { Transaction = ByteString.CopyFromUtf8($"transaction-{Transactions}") });
        }

        public override BatchGetDocumentsStream BatchGetDocuments(BatchGetDocumentsRequest request, CallSettings? callSettings = null)
        {
            Reads.Add(request);
            if (Failure is { } failure)
            {
                throw failure;
            }

            var name = Assert.Single(request.Documents);
            var version = name.EndsWith($"/{Utils.SanitizeId("cluster")}", StringComparison.Ordinal);
            var found = version ? VersionExists : DeletionRacesCommit && Transactions == 1;
            var response = new BatchGetDocumentsResponse { ReadTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddHours(2)) };
            if (found)
            {
                response.Found = new Document
                {
                    Name = name,
                    CreateTime = Timestamp.FromDateTime(DateTime.UnixEpoch),
                    UpdateTime = Timestamp.FromDateTime(DateTime.UnixEpoch)
                };
                if (version)
                {
                    response.Found.Fields[nameof(ClusterVersionEntity.MembershipVersion)] = new Value { IntegerValue = Version };
                }
                else
                {
                    response.Found.Fields[nameof(SiloInstanceEntity.IAmAliveTime)] = new Value { TimestampValue = Timestamp.FromDateTime(DateTime.UnixEpoch) };
                }
            }
            else
            {
                response.Missing = name;
            }

            return new ResponseStream(response);
        }

        public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? callSettings = null)
        {
            AttemptedWrites.AddRange(request.Writes);
            if (DeletionRacesCommit && Transactions == 1)
            {
                Assert.Single(request.Writes);
                return Task.FromException<CommitResponse>(new RpcException(new Status(StatusCode.Aborted, "Silo removed by cleanup.")));
            }

            Assert.Empty(request.Writes);
            CommittedWrites.AddRange(request.Writes);
            return Task.FromResult(new CommitResponse { CommitTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddHours(2)) });
        }

        public override Task RollbackAsync(RollbackRequest request, CallSettings? callSettings = null) => Task.CompletedTask;
    }

    private sealed class ResponseStream(BatchGetDocumentsResponse response) : FirestoreClient.BatchGetDocumentsStream
    {
        public override AsyncServerStreamingCall<BatchGetDocumentsResponse> GrpcCall { get; } = new(
            new ResponseReader(response), Task.FromResult(new global::Grpc.Core.Metadata()), () => Status.DefaultSuccess, () => new global::Grpc.Core.Metadata(), () => { });
    }

    private sealed class ResponseReader(BatchGetDocumentsResponse response) : IAsyncStreamReader<BatchGetDocumentsResponse>
    {
        private bool _read;
        public BatchGetDocumentsResponse Current => response;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = !_read;
            _read = true;
            return Task.FromResult(result);
        }
    }
}
