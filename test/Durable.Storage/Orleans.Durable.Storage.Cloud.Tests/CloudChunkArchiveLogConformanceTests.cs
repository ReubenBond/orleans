using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Azure;
using Google;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Archive logs' chunk layout (archive-logs.md §7.4) over each cloud object store on its emulator: the Accordant
/// <see cref="IArchiveLogStore"/> spec, under its chunk-layout option, over <see cref="ChunkArchiveLogStore"/> on S3 through
/// LocalStack, on Cloud Storage through its testbench and fake-gcs-server, and on Azure through Azurite, in sequences, in
/// concurrent groups, and through the transport's transient faults; and the bases' page contract. Each case's store lies
/// under a prefix of its own.
/// </summary>
public sealed class CloudChunkArchiveLogConformanceTests(CloudEmulators emulators) : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    [InlineData(Emulator.FakeGcsServer)]
    [InlineData(Emulator.Azurite)]
    public async Task TheChunkLayoutConformsInSequences(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var kit = Kit(_ => new Target(emulator, endpoint, faultRate: 0, seed: 0), ArchiveLogStoreSpec.Create(unknownOutcomes: false, retriesMeetLandings: false, interleavedWrites: false, chunkLayout: true));
        await kit.SampleAsync(kit.Sequences(20).WithReplays(), iterations: 30, threads: 4);
    }

    [Theory]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    [InlineData(Emulator.Azurite)]
    public async Task TheChunkLayoutConformsInConcurrentGroups(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var kit = Kit(_ => new Target(emulator, endpoint, faultRate: 0, seed: 0), ArchiveLogStoreSpec.Create(unknownOutcomes: true, staleReads: true, retriesMeetLandings: false, interleavedWrites: true, chunkLayout: true));
        await kit.SampleAsync(kit.ConcurrentSequences(8).WithReplays(), iterations: 20, threads: 4);
    }

    [Theory]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    [InlineData(Emulator.Azurite)]
    public async Task TheChunkLayoutConformsThroughTransientFaults(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var faults = 0;
        var kit = Kit(seed => new Target(emulator, endpoint, faultRate: 0.2, seed, injected => Interlocked.Add(ref faults, injected)), ArchiveLogStoreSpec.Create(unknownOutcomes: true, staleReads: true, chunkLayout: true));
        await kit.SampleAsync(kit.Sequences(16).WithReplays(), iterations: 20, threads: 4);
        await kit.SampleAsync(kit.ConcurrentSequences(8).WithReplays(), iterations: 10, threads: 4);
        Assert.True(faults > 0, "The transport injected no fault.");
    }

    [Theory]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    [InlineData(Emulator.Azurite)]
    public async Task TheBasesListingMeetsItsPageContract(Emulator emulator)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        await ArchiveLogPageContract.CheckAsync(new ChunkArchiveLogStore(store.Store) { ListPageSize = 2 }, Token);
    }

    private ConformanceKit<ArchiveLogStoreState, Target> Kit(Func<int, Target> create, Microsoft.Accordant.Spec<ArchiveLogStoreState> spec) =>
        new ConformanceKit<ArchiveLogStoreState, Target>(
                spec,
                static () => new ArchiveLogStoreState(),
                seed => Task.FromResult(create(seed)),
                ArchiveLogStoreSpec.IsCallerAllowed,
                Token,
                scope: _leaks.InScopeAsync,
                conflicts: ArchiveLogStoreSpec.Conflicts)
            .ArchiveLogStoreOperations(static (target, request) => target.RunAsync(request));

    /// <summary>
    /// One case's chunk store over a cloud object store under a prefix of its own: a call which an injected fault failed,
    /// or which met S3's conflict between concurrent conditional writes, answers nothing; a read which a write overlapped
    /// and which answers <c>Stale</c> reports it as racing.
    /// </summary>
    internal sealed class Target : IAsyncDisposable
    {
        private readonly CloudStore _store;
        private readonly ArchiveLogStoreExecutor _executor;
        private readonly Action<int>? _injected;
        private int _writing;
        private int _writes;

        public Target(Emulator emulator, Uri endpoint, double faultRate, int seed, Action<int>? injected = null)
        {
            _store = new CloudStore(emulator, endpoint);
            _executor = new ArchiveLogStoreExecutor(new ChunkArchiveLogStore(_store.Store));
            _injected = injected;
            if (faultRate > 0)
            {
                _store.Transport.FailRandomly(seed, faultRate);
            }
        }

        public async Task<LogOutcome> RunAsync(object request)
        {
            var write = request is LogAppend or LogOpen or LogDeleteBase;
            var (writing, writes) = (Volatile.Read(ref _writing), Volatile.Read(ref _writes));
            if (write)
            {
                _ = Interlocked.Increment(ref _writing);
                _ = Interlocked.Increment(ref _writes);
            }

            var faults = TransportDouble.TrackCall();
            try
            {
                var outcome = await _executor.RunAsync(request, Token);
                var overlapped = writing > 0 || Volatile.Read(ref _writes) != writes;
                return outcome.Kind == LogOutcomeKind.Stale && overlapped ? new LogOutcome(LogOutcomeKind.StaleRacing) : outcome;
            }
            catch (Exception exception) when (exception is IOException and not EndOfStreamException || IsConditionalConflict(exception) || faults.Value > 0 && IsFaultFailure(exception))
            {
                return LogOutcome.Unknown;
            }
            finally
            {
                if (write)
                {
                    _ = Interlocked.Decrement(ref _writing);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _injected?.Invoke(_store.Transport.Injected);
            return _store.DisposeAsync();
        }

        private static bool IsConditionalConflict(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is AmazonS3Exception { StatusCode: HttpStatusCode.Conflict, ErrorCode: "ConditionalRequestConflict" })
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsFaultFailure(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                switch (current)
                {
                    case TransportDoubleException:
                    case HttpRequestException:
                    case RequestFailedException { Status: 503 }:
                    case AmazonServiceException { StatusCode: HttpStatusCode.ServiceUnavailable }:
                    case GoogleApiException { HttpStatusCode: HttpStatusCode.ServiceUnavailable }:
                        return true;
                    default:
                        break;
                }
            }

            return false;
        }
    }
}
