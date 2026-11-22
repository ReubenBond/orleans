using System.Buffers;
using System.Diagnostics;
using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

public sealed class CasPaxosMetaStoreMappingTests : IDisposable
{
    // Native-valid examples, not a Clog implementation of the upstream version format.
    private const string FirstVersion = "AAAAAQAAAAE=";
    private const string NextVersion = "AAAAAgAAAAE=";
    private const string Key = "0123456789abcdef0123456789abcdef/l/orders";
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public async Task Read_AbsentReturnsNullAndForwardsKeyAndCancellation()
    {
        using var service = new RegisterServiceStub
        {
            OnRead = static (_, _) => ValueTask.FromResult(new RegisterValue(null, null)),
        };
        var store = new CasPaxosMetaStore(service, Key);

        Assert.Null(await store.ReadAsync(TestContext.Current.CancellationToken));

        var call = Assert.Single(service.Reads);
        Assert.Equal(Key, call.Key);
        Assert.Equal(TestContext.Current.CancellationToken, call.CancellationToken);
        Assert.Empty(service.Writes);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("binary")]
    public async Task Read_PresentPreservesValueAndCanonicalVersion(string caseId)
    {
        byte[] value = caseId == "empty" ? [] : [0, 128, 255, 1];
        using var service = new RegisterServiceStub
        {
            OnRead = (_, _) => ValueTask.FromResult(new RegisterValue(new ByteString(value), Version(FirstVersion))),
        };

        using var entry = Assert.IsType<MetaStoreEntry>(
            await new CasPaxosMetaStore(service, Key).ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(FirstVersion, entry.Version.Value);
        Assert.Equal(value, entry.Value.ToArray());
        Assert.Single(service.Reads);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("binary")]
    public async Task Version_RoundTripsNativeBytesWithoutLoss(string caseId)
    {
        byte[] bytes = caseId == "first"
            ? Convert.FromBase64String(FirstVersion)
            : [0, 128, 254, 255, 1, 0, 255, 128];
        var nativeVersion = new ByteString(bytes);
        Assert.True(RegisterVersion.IsValid(nativeVersion));
        using var service = new RegisterServiceStub
        {
            OnRead = (_, _) => ValueTask.FromResult(new RegisterValue(ByteString.Empty, nativeVersion)),
            OnWrite = static (_, _, value, _) => Applied(value, NextVersion),
        };
        var store = new CasPaxosMetaStore(service, Key);
        using var entry = Assert.IsType<MetaStoreEntry>(await store.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(Convert.ToBase64String(bytes), entry.Version.Value);
        Assert.Equal(NextVersion, (await store.WriteAsync(entry.Version, [17], TestContext.Current.CancellationToken)).AppliedVersion().Value);

        var call = Assert.Single(service.Writes);
        Assert.Equal(bytes, Assert.IsType<ByteString>(call.ExpectedVersion).ToArray());
        Assert.Equal(new byte[] { 17 }, call.Value.ToArray());
        Assert.Equal(Key, call.Key);
        Assert.Equal(TestContext.Current.CancellationToken, call.CancellationToken);
    }

    [Fact]
    public async Task Write_NullVersionIsCreateAndEmptyValueIsPreserved()
    {
        using var service = new RegisterServiceStub
        {
            OnWrite = static (_, _, value, _) => Applied(value, FirstVersion),
        };

        Assert.True((await new CasPaxosMetaStore(service, Key).WriteAsync(
            null,
            ReadOnlySequence<byte>.Empty,
            TestContext.Current.CancellationToken)).IsApplied());

        var call = Assert.Single(service.Writes);
        Assert.Null(call.ExpectedVersion);
        Assert.Empty(call.Value.ToArray());
        Assert.Empty(service.Reads);
    }

    [Theory]
    [InlineData("foreign-text")]
    [InlineData("invalid-alphabet")]
    [InlineData("whitespace")]
    [InlineData("noncanonical-padding")]
    [InlineData("short-native")]
    [InlineData("invalid-native")]
    public async Task Write_InvalidVersionIsAMismatchWithoutIo(string caseId)
    {
        var token = InvalidVersion(caseId);
        using var service = new RegisterServiceStub();

        Assert.False((await new CasPaxosMetaStore(service, Key).WriteAsync(
            new MetaStoreVersion(token),
            "unchanged"u8.ToArray(),
            TestContext.Current.CancellationToken)).IsApplied());

        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task Write_NonNullForeignTokenDoesNotCreateMissingRegister()
    {
        using var service = new RegisterServiceStub
        {
            OnRead = static (_, _) => ValueTask.FromResult(new RegisterValue(null, null)),
        };

        await ConditionalRegisterContract.NonNullVersionDoesNotCreateMissingRegister(
            new CasPaxosMetaStore(service, Key));

        Assert.Empty(service.Writes);
        Assert.Single(service.Reads);
    }

    /// <summary>
    /// An applied write answers the version the protocol reports as the register's current one; a mismatch answers no
    /// version, whatever the protocol reports.
    /// </summary>
    [Theory]
    [InlineData(CompareExchangeStatus.Applied)]
    [InlineData(CompareExchangeStatus.Mismatch)]
    public async Task Write_MapsDefiniteOutcomeWithoutReplay(CompareExchangeStatus status)
    {
        using var service = new RegisterServiceStub
        {
            OnWrite = (_, _, value, _) => ValueTask.FromResult(
                new CompareExchangeResult(status, new RegisterValue(value, Version(NextVersion)))),
        };
        RegisterWriteOutcome expected = status == CompareExchangeStatus.Applied
            ? new RegisterWriteOutcome.Applied(new MetaStoreVersion(NextVersion))
            : RegisterWriteOutcome.Mismatch.Instance;

        Assert.Equal(expected, await new CasPaxosMetaStore(service, Key).WriteAsync(
            new MetaStoreVersion(FirstVersion),
            "value"u8.ToArray(),
            TestContext.Current.CancellationToken));

        Assert.Equal(Version(FirstVersion), Assert.Single(service.Writes).ExpectedVersion);
        Assert.Empty(service.Reads);
    }

    [Theory]
    [InlineData("unobserved", false)]
    [InlineData("applied", false)]
    [InlineData("superseded", false)]
    [InlineData("unobserved", true)]
    [InlineData("applied", true)]
    [InlineData("superseded", true)]
    public async Task Write_UnknownStaysUnknownRegardlessOfLaterObservation(string observation, bool withCause)
    {
        var cause = withCause ? new TimeoutException("Reply lost.") : null;
        var current = observation switch
        {
            "unobserved" => null,
            "applied" => new RegisterValue(new ByteString("attempt"u8), Version(NextVersion)),
            "superseded" => new RegisterValue(new ByteString("winner"u8), Version(NextVersion)),
            _ => throw new ArgumentOutOfRangeException(nameof(observation)),
        };
        using var service = new RegisterServiceStub
        {
            OnWrite = (_, _, _, _) => ValueTask.FromResult(
                new CompareExchangeResult(CompareExchangeStatus.Unknown, current, cause)),
        };

        var error = await Assert.ThrowsAsync<IOException>(async () =>
            await new CasPaxosMetaStore(service, Key).WriteAsync(
                new MetaStoreVersion(FirstVersion),
                "attempt"u8.ToArray(),
                TestContext.Current.CancellationToken));

        Assert.Same(cause, error.InnerException);
        Assert.Contains("unknown outcome", error.Message, StringComparison.Ordinal);
        Assert.Single(service.Writes);
        Assert.Empty(service.Reads);
    }

    [Fact]
    public async Task Write_InvalidServiceOutcomeFailsInsteadOfAcknowledgingOrReportingConflict()
    {
        using var service = new RegisterServiceStub
        {
            OnWrite = static (_, _, _, _) => ValueTask.FromResult(
                new CompareExchangeResult((CompareExchangeStatus)42, null)),
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new CasPaxosMetaStore(service, Key).WriteAsync(
                null,
                "value"u8.ToArray(),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("invalid conditional-write outcome", error.Message, StringComparison.Ordinal);
        Assert.Single(service.Writes);
        Assert.Empty(service.Reads);
    }

    /// <summary>
    /// An applied write whose answer carries no present register version is a malformed answer, which leaves the write's
    /// outcome unknown rather than returning a version the register may not hold.
    /// </summary>
    [Fact]
    public async Task Write_AppliedWithoutTheRegistersVersionFails()
    {
        using var service = new RegisterServiceStub
        {
            OnWrite = static (_, _, _, _) => ValueTask.FromResult(new CompareExchangeResult(CompareExchangeStatus.Applied, null)),
        };

        _ = await Assert.ThrowsAsync<IOException>(
            () => new CasPaxosMetaStore(service, Key).WriteAsync(null, "value"u8.ToArray(), TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task ServiceFailureIsNotTranslatedToAbsenceOrMismatch(string operation)
    {
        var failure = new RegisterUnavailableException();
        using var service = new RegisterServiceStub
        {
            OnRead = (_, _) => ValueTask.FromException<RegisterValue>(failure),
            OnWrite = (_, _, _, _) => ValueTask.FromException<CompareExchangeResult>(failure),
        };
        var store = new CasPaxosMetaStore(service, Key);

        var observed = await Assert.ThrowsAsync<RegisterUnavailableException>(async () =>
        {
            if (operation == "read")
            {
                _ = await store.ReadAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                _ = (await store.WriteAsync(null, "value"u8.ToArray(), TestContext.Current.CancellationToken)).IsApplied();
            }
        });

        Assert.Same(failure, observed);
        Assert.Equal(operation == "read" ? 1 : 0, service.Reads.Count);
        Assert.Equal(operation == "write" ? 1 : 0, service.Writes.Count);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("malformed")]
    public async Task PreCancellationThrowsBeforeIo(string operation)
    {
        using var service = new RegisterServiceStub();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var store = new CasPaxosMetaStore(service, Key);
        var version = operation switch
        {
            "read" or "create" => null,
            "replace" => new MetaStoreVersion(FirstVersion),
            "malformed" => new MetaStoreVersion("not-present"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (operation == "read")
            {
                _ = await store.ReadAsync(cancellation.Token);
                return;
            }

            _ = (await store.WriteAsync(version, ReadOnlySequence<byte>.Empty, cancellation.Token)).IsApplied();
        });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(service.Reads);
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task CancellationAfterDispatchDoesNotReplayUnknownWrite()
    {
        var reply = new TaskCompletionSource<CompareExchangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new RegisterServiceStub
        {
            OnWrite = (_, _, _, _) => new ValueTask<CompareExchangeResult>(reply.Task),
        };
        using var cancellation = new CancellationTokenSource();
        var pending = new CasPaxosMetaStore(service, Key).WriteAsync(
            null,
            "may-have-landed"u8.ToArray(),
            cancellation.Token).AsTask();
        Assert.Equal(cancellation.Token, Assert.Single(service.Writes).CancellationToken);

        await cancellation.CancelAsync();
        reply.SetResult(new CompareExchangeResult(
            CompareExchangeStatus.Unknown,
            new RegisterValue(new ByteString("may-have-landed"u8), Version(FirstVersion))));
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(service.Writes);
        Assert.Empty(service.Reads);
    }

    [Fact]
    public async Task ValuesAreDetachedFromCallerBuffersAndSubsequentReads()
    {
        ByteString? stored = null;
        var version = FirstVersion;
        using var service = new RegisterServiceStub
        {
            OnWrite = (_, _, value, _) =>
            {
                stored = value;
                version = version == FirstVersion ? NextVersion : FirstVersion;
                return Applied(value, version);
            },
            OnRead = (_, _) => ValueTask.FromResult(new RegisterValue(stored, Version(version))),
        };

        await ConditionalRegisterContract.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues(
            new CasPaxosMetaStore(service, Key));

        Assert.Equal([1, 2, 3], service.Writes[0].Value.ToArray());
        Assert.Equal(3, service.Writes.Count);
        Assert.Equal(3, service.Reads.Count);
    }

    /// <summary>
    /// The service answers a write with a mismatch only after its caller's token is cancelled: the adapter throws rather
    /// than answering <see cref="RegisterWriteOutcome.Mismatch"/> (metastore.md §6).
    /// </summary>
    [Fact]
    public async Task AConflictAnsweredAfterCancellationThrows()
    {
        ByteString? stored = null;
        WriteHold? conflict = null;
        using var service = new RegisterServiceStub
        {
            OnRead = (_, _) => ValueTask.FromResult(stored is null ? new RegisterValue(null, null) : new RegisterValue(stored, Revision(1))),
            OnWrite = (_, _, value, cancellationToken) => Interlocked.Exchange(ref conflict, null) is { } held
                ? MismatchAfterCancellationAsync(held, cancellationToken)
                : ValueTask.FromResult(Store(value)),
        };

        await ConditionalRegisterContract.AConflictAnsweredAfterCancellationThrows(
            new CasPaxosMetaStore(service, Key),
            armed => conflict = armed);

        CompareExchangeResult Store(ByteString value)
        {
            stored = value;
            return new CompareExchangeResult(CompareExchangeStatus.Applied, new RegisterValue(value, Revision(1)));
        }

        async ValueTask<CompareExchangeResult> MismatchAfterCancellationAsync(WriteHold held, CancellationToken cancellationToken)
        {
            try
            {
                await held.ConflictAfterCancellationAsync(new InvalidOperationException("answered below"), cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The protocol answers a mismatch rather than throwing.
            }

            return new CompareExchangeResult(CompareExchangeStatus.Mismatch, new RegisterValue(stored, Revision(1)));
        }
    }

    /// <summary>
    /// The service holds the adapter's request, its value already copied into the protocol's bytes, while the caller's
    /// wait is cancelled and its pages are released and reused; the request then lands exactly the value written.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AWriteCancelledMidCallLandsItsOwnCopy(int pages)
    {
        ByteString? stored = null;
        var revision = 0;
        WriteHold? hold = null;
        using var service = new RegisterServiceStub
        {
            OnRead = (_, _) => ValueTask.FromResult(stored is null ? new RegisterValue(null, null) : new RegisterValue(stored, Revision(revision))),
            OnWrite = (_, _, value, cancellationToken) => Interlocked.Exchange(ref hold, null) is { } held
                ? HoldAsync(held, value, cancellationToken)
                : ValueTask.FromResult(Store(value)),
        };

        await ConditionalRegisterContract.AWriteCancelledMidCallLandsItsOwnCopy(
            new CasPaxosMetaStore(service, Key),
            armed => hold = armed,
            pages,
            lands: true);

        CompareExchangeResult Store(ByteString value)
        {
            stored = value;
            return new CompareExchangeResult(CompareExchangeStatus.Applied, new RegisterValue(value, Revision(++revision)));
        }

        async ValueTask<CompareExchangeResult> HoldAsync(WriteHold held, ByteString value, CancellationToken cancellationToken)
        {
            _ = held.LandAsync(() => _ = Store(value));
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            throw new UnreachableException();
        }
    }

    [Fact]
    public async Task AdapterDoesNotImposeGrpcDefaultPayloadLimit()
    {
        var input = new byte[4 * 1024 * 1024 + 1];
        input[0] = 17;
        input[^1] = 255;
        using var service = new RegisterServiceStub
        {
            OnWrite = static (_, _, value, _) => Applied(value, FirstVersion),
        };

        Assert.True((await new CasPaxosMetaStore(service, Key).WriteAsync(
            null,
            input,
            TestContext.Current.CancellationToken)).IsApplied());

        Assert.Equal(input, Assert.Single(service.Writes).Value.ToArray());
    }

    public void Dispose() => _leaks.Dispose();

    private static ByteString Version(string value) => new(Convert.FromBase64String(value));

    /// <summary>
    /// Returns a native-valid version of <paramref name="revision"/>, shaped as <see cref="FirstVersion"/> is.
    /// </summary>
    private static ByteString Revision(int revision) => new([0, 0, 0, checked((byte)revision), 0, 0, 0, 1]);

    private static ValueTask<CompareExchangeResult> Applied(ByteString value, string version) =>
        ValueTask.FromResult(new CompareExchangeResult(CompareExchangeStatus.Applied, new RegisterValue(value, Version(version))));

    private static string InvalidVersion(string caseId) => caseId switch
    {
        "foreign-text" => "not-present",
        "invalid-alphabet" => "!!!!",
        "whitespace" => FirstVersion + "\n",

        // The decoder accepts the same bytes with nonzero unused pad bits; the public encoding is canonical.
        "noncanonical-padding" => "AAAAAQAAAAF=",
        "short-native" => "AQ==",
        "invalid-native" => "AAAAAAAAAAA=",
        _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
    };
}
