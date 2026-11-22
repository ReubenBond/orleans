using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.S3.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// The fold oracle (<see cref="LedgerStorageSpec"/>, LL-I1 to LL-I9) over <see cref="ObjectLedgerStorage"/> backed by
/// <see cref="S3ObjectStore"/> in directory-bucket mode (<c>--x-s3</c>) on <see cref="FakeS3DirectoryBucketService"/>,
/// whose listings are unordered (object-ledgers.md §3.1, §9, implementation plan row 7.2b).
/// </summary>
public sealed class S3DirectoryBucketLedgerConformanceTests : IDisposable
{
    private const int Window = 2;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact(Timeout = 120_000)]
    public async Task EverySequenceToASmallDepthConforms()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static seed => CreateSystem(seed, FakeS3ListingOrder.Reversed, maxPageSize: 2, retainEmptyDirectories: true));
        var spec = LedgerStorageSpec.Create();
        var inputs = new Microsoft.Accordant.InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LedgerOutcome>(operation).With(request, request.ToString()));

        Add(LedgerStorageSpec.Append, new LedgerAppend(1));
        Add(LedgerStorageSpec.Append, new LedgerAppend(2));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(3));
        Add(LedgerStorageSpec.AppendUnanswered, new LedgerAppendUnanswered(5, Landed: false));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(0));
        Add(LedgerStorageSpec.Read, new LedgerSlotRead(1));
        Add(LedgerStorageSpec.Check, new LedgerCheck());
        Add(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: false));
        Add(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true));
        Add(LedgerStorageSpec.Observe, LedgerObserve.Instance);
        Add(LedgerStorageSpec.Drop, new LedgerDrop(Every: false));
        Add(LedgerStorageSpec.Drop, new LedgerDrop(Every: true));

        var sequences = spec.GenerateTests(new LedgerState(), inputs, kit.GenerationOptions(maxDepth: 5));
        Assert.NotEmpty(sequences);
        await kit.RunAsync(sequences);
    }

    [Fact(Timeout = 120_000)]
    public async Task RandomSequencesConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static seed => CreateSystem(seed, FakeS3ListingOrder.Shuffled, maxPageSize: 2, retainEmptyDirectories: true));
        await kit.SampleAsync(kit.Sequences(25), iterations: 60, threads: 4);
    }

    [Fact(Timeout = 120_000)]
    public async Task RandomSequencesConformOverAFaultyStore()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static seed => CreateSystem(seed, FakeS3ListingOrder.Shuffled, maxPageSize: 2, retainEmptyDirectories: true, faultRate: 0.1));
        await kit.SampleAsync(kit.Sequences(25), iterations: 40, threads: 4);
    }

    [Fact(Timeout = 120_000)]
    public async Task RandomSequencesConformThroughHttpTransportFaults()
    {
        var injected = 0;
        var kit = Kit(
            TestContext.Current.CancellationToken,
            seed => CreateSystem(
                seed,
                FakeS3ListingOrder.Shuffled,
                maxPageSize: 2,
                retainEmptyDirectories: true,
                httpFaultRate: 0.12,
                onDisposed: count => Interlocked.Add(ref injected, count)));
        await kit.SampleAsync(kit.Sequences(20), iterations: 30, threads: 4);
        Assert.True(injected > 0, "Expected HTTP-layer TransportDouble faults to be injected.");
    }

    [Fact(Timeout = 120_000)]
    public async Task ConcurrentGroupsWithFencesAndDropsConform()
    {
        var kit = Kit(TestContext.Current.CancellationToken, static seed => CreateSystem(seed, FakeS3ListingOrder.Shuffled, maxPageSize: 2, retainEmptyDirectories: true));
        var concurrent = Gen.Frequency(
            (4, LedgerConformanceOperations.Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
            (2, LedgerConformanceOperations.Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))),
            (1, LedgerConformanceOperations.RangeReads.Select(static request => new ConformanceCall(LedgerStorageSpec.ReadRange, request))),
            (1, LedgerConformanceOperations.Slots.Select(static from => new ConformanceCall(LedgerStorageSpec.Scan, new LedgerScan(from)))),
            (1, Gen.Const(new ConformanceCall(LedgerStorageSpec.Check, new LedgerCheck(Elsewhere: true)))),
            (2, Gen.Const(new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(Elsewhere: true)))),
            (1, Gen.Bool.Select(static every => new ConformanceCall(LedgerStorageSpec.Drop, new LedgerDrop(every)))));
        var rounds = Gen.Frequency(
            (3, LedgerConformanceOperations.Calls.Select(static call => new ConformanceRound([call]))),
            (2, concurrent.Array[2, 3].Select(static calls => new ConformanceRound(calls))));
        await kit.SampleAsync(
            Gen.Select(rounds.Array[1, 15], Gen.Int[0, int.MaxValue], static (r, seed) => new ConformanceCase(r, Seed: seed)),
            iterations: 60,
            threads: 4);
    }

    private ConformanceKit<LedgerState, ObjectLedgerSystem> Kit(CancellationToken cancellationToken, Func<int, ObjectLedgerSystem> create) =>
        new ConformanceKit<LedgerState, ObjectLedgerSystem>(
                LedgerStorageSpec.Create(),
                static () => new LedgerState(),
                seed => Task.FromResult(create(seed)),
                LedgerStorageSpec.IsCallerAllowed,
                cancellationToken,
                _leaks.InScopeAsync)
            .WithLedgerOperations();

    private static ObjectLedgerSystem CreateSystem(
        int seed,
        FakeS3ListingOrder order,
        int maxPageSize,
        bool retainEmptyDirectories,
        double faultRate = 0,
        double httpFaultRate = 0,
        Action<int>? onDisposed = null)
    {
        var fake = new FakeS3DirectoryBucketService(
            listingOrder: order,
            seed: seed,
            maxPageSize: maxPageSize,
            retainEmptyDirectories: retainEmptyDirectories,
            duplicateAcrossPages: true);
        TransportDouble? transport = null;
        AmazonS3Client? client = null;
        OwnedResources? owned = null;
        try
        {
            HttpMessageHandler handler = fake;
            if (httpFaultRate > 0)
            {
                transport = new TransportDouble { InnerHandler = fake };
                transport.FailRandomly(seed, httpFaultRate);
                handler = transport;
            }

            client = CloudClients.S3Client(StubResponses.Unserved, handler, disableS3ExpressSessionAuth: true);
            IObjectStore store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName, prefix: "ledgers-dir/");
            if (httpFaultRate > 0)
            {
                store = new TransportFaultNormalizingStore(store);
            }

            if (faultRate > 0)
            {
                store = new FaultInjectingObjectStore(store, seed, faultRate, maxUnknownOutcomes: 6);
            }

            owned = new OwnedResources(client, transport, fake, onDisposed);
            client = null;
            transport = null;
            fake = null;
            var system = ObjectLedgerSystem.Over(store, Window, owned);
            owned = null;
            return system;
        }
        finally
        {
            owned?.Dispose();
            client?.Dispose();
            transport?.Dispose();
            fake?.Dispose();
        }
    }

    private sealed class OwnedResources(
        AmazonS3Client client,
        TransportDouble? transport,
        FakeS3DirectoryBucketService fake,
        Action<int>? onDisposed) : IDisposable, IAsyncDisposable
    {
        public void Dispose()
        {
            if (transport is not null)
            {
                onDisposed?.Invoke(transport.Injected);
            }

            client.Dispose();
            transport?.Dispose();
            fake.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Normalizes exhausted HTTP-layer <see cref="TransportDouble"/> faults into <see cref="IOException"/> so
    /// <see cref="ObjectLedgerSystem"/> retries read/check/sweep calls uniformly while letting <see cref="AmazonS3Client"/>
    /// retry at the HTTP layer first.
    /// </summary>
    private sealed class TransportFaultNormalizingStore(IObjectStore inner) : IObjectStore
    {
        public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.CreateAsync(name, content, crc32c, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientTransportFault(exception))
            {
                throw new IOException("Exhausted HTTP transport retries.", exception);
            }
        }

        public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(name, offset, length, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientTransportFault(exception))
            {
                throw new IOException("Exhausted HTTP transport retries.", exception);
            }
        }

        public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.GetPropertiesAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientTransportFault(exception))
            {
                throw new IOException("Exhausted HTTP transport retries.", exception);
            }
        }

        public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var enumerator = inner.ListAsync(prefix, startAfter, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsTransientTransportFault(exception))
                    {
                        throw new IOException("Exhausted HTTP transport retries.", exception);
                    }

                    if (!moved)
                    {
                        yield break;
                    }

                    yield return enumerator.Current;
                }
            }
        }

        public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientTransportFault(exception))
            {
                throw new IOException("Exhausted HTTP transport retries.", exception);
            }
        }

        public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadVersionedAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientTransportFault(exception))
            {
                throw new IOException("Exhausted HTTP transport retries.", exception);
            }
        }

        public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransientTransportFault(exception))
            {
                throw new IOException("Exhausted HTTP transport retries.", exception);
            }
        }

        private static bool IsTransientTransportFault(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is TransportDoubleException or AmazonServiceException { StatusCode: HttpStatusCode.ServiceUnavailable })
                {
                    return true;
                }
            }

            return false;
        }
    }
}
