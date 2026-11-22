using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Azure;
using CsCheck;
using Google;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// Checks each cloud <see cref="IObjectStore"/> against <see cref="ObjectStoreSpec"/> on its emulator through the
/// conformance kit (verification.md §3.1, §4.1, §10): random sequences and concurrent groups, objects across buffer pages,
/// and sequences and groups whose requests the transport fails before they land or whose answers it loses after, which
/// the SDK's retries meet and the spec judges with unknown outcomes; and the shared contract's examples.
/// </summary>
/// <remarks>
/// Each case runs on a store under a prefix of its own. Concurrent groups run without listings, as for the file-system
/// store: a service's listing concurrent with creates and deletes may name what those make as hints, which a listing
/// judged at one point does not allow (archive §3). A call answers nothing only when the transport faulted one of its own
/// requests and the call failed as such a fault leaves it; any other failure fails the case.
/// </remarks>
public sealed class CloudObjectStoreConformanceTests(CloudEmulators emulators) : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Examples => ObjectStoreContract.Examples;

    /// <summary>
    /// Gets the examples LocalStack runs: every one but a refused replace's, after which LocalStack serves the object it
    /// replaced with its length and no bytes, which
    /// <see cref="CloudObjectStoreFaultTests.AWriteWhoseCrc32CIsWrongIsRefusedAndLeavesTheObjectAsItWas"/> pins
    /// (test/Durable.Storage/docs/spec/verification.md §13).
    /// </summary>
    public static TheoryData<string> S3Examples =>
        [.. ObjectStoreContract.Examples.Select(static row => row.Data).Where(static example => example != nameof(ObjectStoreContract.AWrongCrc32CIsRefusedAndLeavesTheObjectAsItWas))];

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task TheStoreConformsInSequences(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var kit = Kit(_ => Target(emulator, endpoint), unknownOutcomes: false, appends: emulator is Emulator.Azurite);
        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 4);
    }

    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task TheStoreConformsInConcurrentGroups(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var tally = new OutcomeTally();
        var kit = Kit(_ => Target(emulator, endpoint, tally, faultRate: 0), unknownOutcomes: true, listings: false, appends: emulator is Emulator.Azurite);
        await kit.SampleAsync(kit.ConcurrentSequences(8), iterations: 20, threads: 4);
        tally.AssertMostlyDefinite();
    }

    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    public async Task TheStoreConformsThroughTransientFaults(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var tally = new OutcomeTally();
        var kit = Kit(seed => Target(emulator, endpoint, tally, faultRate: 0.2, seed), unknownOutcomes: true, appends: emulator is Emulator.Azurite);
        await kit.SampleAsync(kit.Sequences(20), iterations: 30, threads: 4);
        tally.AssertMostlyDefinite();
    }

    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.GcsTestbench)]
    public async Task TheStoreConformsInConcurrentGroupsThroughTransientFaults(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var tally = new OutcomeTally();
        var kit = Kit(seed => Target(emulator, endpoint, tally, faultRate: 0.2, seed), unknownOutcomes: true, listings: false, appends: emulator is Emulator.Azurite);
        await kit.SampleAsync(kit.ConcurrentSequences(8), iterations: 20, threads: 4);
        tally.AssertMostlyDefinite();
    }

    /// <summary>
    /// Objects of more than 4 MiB, which span several buffer pages, written and read whole and in ranges.
    /// </summary>
    [Theory]
    [InlineData(Emulator.Azurite)]
    [InlineData(Emulator.LocalStack)]
    [InlineData(Emulator.FakeGcsServer)]
    public async Task TheStoreServesObjectsAcrossBufferPages(Emulator emulator)
    {
        var endpoint = emulators.Require(emulator);
        var values = Gen.OneOfConst((byte)1, ObjectStoreSpec.LargeValue, (byte)(ObjectStoreSpec.LargeValue + 1));
        var lengths = Gen.OneOf(Gen.Int[1, 256], Gen.Const(ObjectStoreSpec.LargeLength));
        var kit = Kit(_ => Target(emulator, endpoint), unknownOutcomes: false, values: values, lengths: lengths);
        await kit.SampleAsync(kit.Sequences(8), iterations: 6, threads: 2);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public Task TheAzureStoreKeepsTheContract(string example) => KeepsTheContractAsync(Emulator.Azurite, example);

    [Theory]
    [MemberData(nameof(S3Examples))]
    public Task TheS3StoreKeepsTheContract(string example) => KeepsTheContractAsync(Emulator.LocalStack, example);

    [Theory]
    [MemberData(nameof(Examples))]
    public Task TheCloudStorageStoreKeepsTheContract(string example) => KeepsTheContractAsync(Emulator.FakeGcsServer, example);

    private async Task KeepsTheContractAsync(Emulator emulator, string example)
    {
        await using var store = new CloudStore(emulator, emulators.Require(emulator));
        // The emulators serve plain HTTP, over which S3's and Cloud Storage's checksum refusals are unknown outcomes.
        await ObjectStoreContract.RunAsync(example, store.Store, refusesOverPlainHttp: emulator is not Emulator.Azurite);
    }

    private static Task<ObjectStoreTarget> Target(Emulator emulator, Uri endpoint, OutcomeTally? tally = null, double faultRate = 0, int seed = 0) =>
        Task.FromResult(new ObjectStoreTarget(emulator, endpoint, tally, faultRate, seed));

    private ConformanceKit<ObjectStoreState, ObjectStoreTarget> Kit(
        Func<int, Task<ObjectStoreTarget>> create,
        bool unknownOutcomes,
        bool listings = true,
        Gen<byte>? values = null,
        Gen<int>? lengths = null,
        bool appends = false) =>
        new ConformanceKit<ObjectStoreState, ObjectStoreTarget>(
                ObjectStoreSpec.Create(unknownOutcomes),
                static () => new ObjectStoreState(),
                create,
                ObjectStoreSpec.IsCallerAllowed,
                Token,
                _leaks.InScopeAsync,
                ObjectStoreSpec.Conflicts)
            .ObjectStoreOperations(static (target, request) => target.RunAsync(request), listings, values, lengths, appends);

    /// <summary>
    /// The calls of a run which answered and those whose outcome a fault left unknown.
    /// </summary>
    private sealed class OutcomeTally
    {
        private int _definite;
        private int _unknown;

        public void Count(bool definite) => _ = definite ? Interlocked.Increment(ref _definite) : Interlocked.Increment(ref _unknown);

        /// <summary>
        /// Asserts that most calls answered, so the run judged the stores' answers rather than passing on unknown outcomes.
        /// </summary>
        /// <remarks>
        /// The bound, fewer than a third of the calls unknown, follows from the fault rate and the stores' repetitions. The
        /// transport faults each request with probability 0.2, and a call's outcome is unknown only when a fault meets a
        /// request no layer repeats. Azure.Core and the AWS SDK repeat every request. The Cloud Storage store repeats every
        /// operation but an upload, whose session and content requests nothing repeats, so at most 1 − 0.8² = 0.36 of its
        /// uploads end unknown, and 0.2⁴ of its other calls, whose four attempts all faulted. Creates and replaces take 6 of
        /// the kit's 13 weights in a concurrent group (6 of 14 with listings), so at most about 0.17 of a run's calls end
        /// unknown, and with a run's 300 or so calls the bound lies more than six standard deviations above that. Appends
        /// run only on Azure, whose client repeats them. While the
        /// store repeated no read or delete, about 0.29 did, within two deviations of the bound, and runs failed (#290).
        /// </remarks>
        public void AssertMostlyDefinite() =>
            Assert.True(_definite > 2 * _unknown, $"{_definite} calls answered and {_unknown} left their outcomes unknown.");
    }

    /// <summary>
    /// One case's store, with the executor of its calls. With a tally, a call answers nothing only when the store reports
    /// that it could not confirm a write, or when a fault the transport injected into one of its own requests left it
    /// failing as such a fault does.
    /// </summary>
    private sealed class ObjectStoreTarget : IAsyncDisposable
    {
        private readonly CloudStore _store;
        private readonly ObjectStoreExecutor _executor;
        private readonly OutcomeTally? _tally;

        public ObjectStoreTarget(Emulator emulator, Uri endpoint, OutcomeTally? tally, double faultRate, int seed)
        {
            _store = new CloudStore(emulator, endpoint);
            _executor = new ObjectStoreExecutor(_store.Store, _store.Appendable);
            _tally = tally;
            if (faultRate > 0)
            {
                _store.Transport.FailRandomly(seed, faultRate);
            }
        }

        public async Task<ObjectOutcome> RunAsync(object request)
        {
            var faults = TransportDouble.TrackCall();
            try
            {
                var outcome = await _executor.RunAsync(request, Token);
                _tally?.Count(definite: true);
                return outcome;
            }
            catch (Exception exception) when (_tally is not null && (exception is IOException and not EndOfStreamException || IsConditionalConflict(exception) || faults.Value > 0 && IsFaultFailure(exception)))
            {
                _tally.Count(definite: false);
                return ObjectOutcome.Unknown;
            }
        }

        public ValueTask DisposeAsync() => _store.DisposeAsync();

        /// <summary>
        /// Returns whether <paramref name="exception"/> is S3's refusal of a conditional write which raced another of the
        /// same key (409 <c>ConditionalRequestConflict</c>), which the store throws, leaving its outcome unknown with or
        /// without a fault, so the concurrent groups run unchanged against Amazon S3.
        /// </summary>
        private static bool IsConditionalConflict(Exception exception) =>
            exception is AmazonS3Exception { StatusCode: HttpStatusCode.Conflict, ErrorCode: "ConditionalRequestConflict" };

        /// <summary>
        /// Returns whether <paramref name="exception"/> is how a call fails when a fault left its outcome unknown: the
        /// transport's lost answer, or the 503 it answered for a request the service never saw. A store's own
        /// <see cref="IOException"/>, its report that it could not confirm a write, as when a concurrent delete removes a
        /// Cloud Storage upload's object before its read-back, leaves the outcome unknown with or without a fault.
        /// </summary>
        private static bool IsFaultFailure(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                switch (current)
                {
                    case TransportDoubleException:
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
