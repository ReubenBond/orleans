using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Serialization;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The replica transport's grain messages (orleans.md §5.12, §7.2, §9, conventions.md §15): every case of the three
/// remote replica answers crosses through its surrogate, whose wire encoding of every case a snapshot pins, as do the
/// requests; an unknown case tag is refused with <see cref="InvalidDataException"/>; every case and request crosses
/// unchanged; and serializing a read's DATA reply releases the host admission it holds (OR-I17).
/// </summary>
[NoPageLeakCheck("Grain messages carry Orleans buffers and no Clog pages (OR-I18).")]
public sealed class ReplicaMessageTests : IDisposable
{
    private static readonly RemoteReplicaWriteOutcome[] WriteCases =
    [
        ReplicaWriteOutcome.Written.Instance,
        ReplicaWriteOutcome.Written.FromGrant(TimeSpan.FromSeconds(3)),
        ReplicaWriteOutcome.Sealed.Instance,
        ReplicaWriteOutcome.Dropped.Instance,
        ReplicaWriteOutcome.Backpressure.Instance,
        NotThisNode.Instance,
        new ReplicaCallRefused(ReplicaRefusal.Argument, "An empty batch."),
        new ReplicaCallRefused(ReplicaRefusal.Stopping, "Stopping."),
        new ReplicaCorrupt(null, "A damaged value."),
    ];

    private static RemoteReplicaRead[] CreateReadCases() =>
    [
        WireBytes.Data(1, 2, 3),
        WireBytes.Data(),
        new RemoteReplicaRead.TooLarge(1 << 20),
        new ReplicaRead.OutsideBatch(5),
        ReplicaRead.Missing.Instance,
        ReplicaWriteOutcome.Dropped.Instance,
        ReplicaWriteOutcome.Backpressure.Instance,
        NotThisNode.Instance,
        new ReplicaCallRefused(ReplicaRefusal.ArgumentOutOfRange, "The slot is out of range."),
        new ReplicaCorrupt(4, "A damaged value."),
    ];

    private static readonly RemoteReplicaTailRead[] TailCases =
    [
        new RemoteReplicaTailRead.Tail(7, IsSealed: true),
        new RemoteReplicaTailRead.Tail(7, IsSealed: true, TimeSpan.FromSeconds(2).Ticks),
        new RemoteReplicaTailRead.Tail(0, IsSealed: false),
        ReplicaWriteOutcome.Dropped.Instance,
        ReplicaWriteOutcome.Backpressure.Instance,
        NotThisNode.Instance,
        new ReplicaCallRefused(ReplicaRefusal.UnsupportedVersion, "Version 2."),
        new ReplicaCorrupt(null, "A damaged value."),
    ];

    private static readonly RemoteReplicaHeartbeatRead[] HeartbeatCases =
    [
        new RemoteReplicaHeartbeatRead.Reply(TimeSpan.FromSeconds(3).Ticks, [(byte)ReplicaHeartbeatStatus.Granted, (byte)ReplicaHeartbeatStatus.Sealed, (byte)ReplicaHeartbeatStatus.Dropped, (byte)ReplicaHeartbeatStatus.Absent]),
        new RemoteReplicaHeartbeatRead.Reply(0, []),
        ReplicaWriteOutcome.Backpressure.Instance,
        NotThisNode.Instance,
        new ReplicaCallRefused(ReplicaRefusal.UnsupportedVersion, "Version 2."),
    ];

    private static readonly LedgerReplicaHeartbeatRequest Heartbeat = new()
    {
        Version = LedgerReplicaProtocol.Version,
        Ledgers = [9, 10],
    };

    private static LedgerReplicaWriteRequest CreateWrite() => new()
    {
        Version = LedgerReplicaProtocol.Version,
        Ledger = 9,
        WindowSize = 4,
        Slot = 3,
        ContiguousTail = 2,
        Recovery = true,
        Payload = WireBytes.Arc(5, 6),
    };

    private static readonly LedgerReplicaReadRequest Read = new()
    {
        Version = LedgerReplicaProtocol.Version,
        Ledger = 9,
        WindowSize = 4,
        Slot = 3,
        ContiguousTail = 2,
        Offset = 1,
        Length = 1,
        MaxReplyBytes = 1,
    };

    private static readonly LedgerReplicaTailRequest Tail = new()
    {
        Version = LedgerReplicaProtocol.Version,
        Ledger = 9,
        WindowSize = 4,
        ContiguousTail = 2,
        Seal = true,
    };

    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(ILedgerStoreGrain).Assembly))
        .BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public Task EveryAnswerCaseAndRequestHasAPinnedWireEncoding()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var readCases = CreateReadCases();
        var write = CreateWrite();
        return Verify(new
        {
            Writes = WriteCases.Select(outcome => new { Case = outcome.Value!.GetType().Name, Outcome = outcome.ToString(), Wire = Wire(serializer, outcome) }),
            Reads = readCases.Select(read => new { Case = read.Value!.GetType().Name, Wire = Wire(serializer, read) }),
            Tails = TailCases.Select(read => new { Case = read.Value!.GetType().Name, Read = read.ToString(), Wire = Wire(serializer, read) }),
            Heartbeats = HeartbeatCases.Select(read => new { Case = read.Value!.GetType().Name, Read = read.ToString(), Wire = Wire(serializer, read) }),
            Requests = new
            {
                Write = Wire(serializer, write),
                Read = Wire(serializer, Read),
                Tail = Wire(serializer, Tail),
                Heartbeat = Wire(serializer, Heartbeat),
            },
        });
    }

    [Fact]
    public void EveryAnswerCaseAndRequestCrossesUnchanged()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        foreach (var outcome in WriteCases)
        {
            Assert.Equal(outcome, serializer.Deserialize<RemoteReplicaWriteOutcome>(serializer.SerializeToArray(outcome)));
        }

        foreach (var read in CreateReadCases())
        {
            var expectedBytes = (read.Value as RemoteReplicaRead.Data)?.Bytes.ToArray();
            var decoded = serializer.Deserialize<RemoteReplicaRead>(serializer.SerializeToArray(read));
            if (expectedBytes is not null)
            {
                var decodedData = Assert.IsType<RemoteReplicaRead.Data>(decoded.Value);
                Assert.Equal(expectedBytes, decodedData.Bytes.ToArray());
                decodedData.Bytes.Dispose();
            }
            else
            {
                Assert.Equal(read, decoded);
            }
        }

        foreach (var read in TailCases)
        {
            Assert.Equal(read, serializer.Deserialize<RemoteReplicaTailRead>(serializer.SerializeToArray(read)));
        }

        foreach (var read in HeartbeatCases)
        {
            Assert.Equal(read, serializer.Deserialize<RemoteReplicaHeartbeatRead>(serializer.SerializeToArray(read)));
        }

        var sentWrite = CreateWrite();
        var expectedPayload = sentWrite.Payload.ToArray();
        var write = serializer.Deserialize<LedgerReplicaWriteRequest>(serializer.SerializeToArray(sentWrite));
        Assert.Equal((sentWrite.Version, sentWrite.Ledger, sentWrite.WindowSize, sentWrite.Slot, sentWrite.ContiguousTail, sentWrite.Recovery), (write.Version, write.Ledger, write.WindowSize, write.Slot, write.ContiguousTail, write.Recovery));
        Assert.Equal(expectedPayload, write.Payload.ToArray());
        write.Payload.Dispose();
        Assert.Equal(Read, serializer.Deserialize<LedgerReplicaReadRequest>(serializer.SerializeToArray(Read)));
        Assert.Equal(Tail, serializer.Deserialize<LedgerReplicaTailRequest>(serializer.SerializeToArray(Tail)));
        var heartbeat = serializer.Deserialize<LedgerReplicaHeartbeatRequest>(serializer.SerializeToArray(Heartbeat));
        Assert.Equal(Heartbeat.Version, heartbeat.Version);
        Assert.Equal(Heartbeat.Ledgers, heartbeat.Ledgers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(255)]
    public void AnUnknownWriteCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaWriteOutcomeConverter().ConvertFromSurrogate(new RemoteReplicaWriteOutcomeSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(255)]
    public void AnUnknownReadCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(255)]
    public void AnUnknownTailCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaTailReadConverter().ConvertFromSurrogate(new RemoteReplicaTailReadSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(255)]
    public void AnUnknownHeartbeatCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaHeartbeatReadConverter().ConvertFromSurrogate(new RemoteReplicaHeartbeatReadSurrogate { Case = tag }));

    /// <summary>
    /// A payload and a DATA reply of 40 KiB, which span Orleans' 16 KiB pages, cross unchanged, read back from an Orleans
    /// buffer as a receiving silo reads them.
    /// </summary>
    [Fact]
    public void AMultiPagePayloadAndDataCrossUnchanged()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var bytes = new byte[40 << 10];
        new Random(40).NextBytes(bytes);

        var sent = CreateWrite() with { Payload = WireBytes.Arc(bytes) };
        using var request = WireBytes.Of(serializer.SerializeToArray(sent));
        Assert.True(sent.Payload.IsConsumed);
        var write = serializer.Deserialize<LedgerReplicaWriteRequest>(request);
        Assert.Equal(bytes, write.Payload.ToArray());
        write.Payload.Dispose();

        var data = WireBytes.Data(bytes);
        using var reply = WireBytes.Of(serializer.SerializeToArray<RemoteReplicaRead>(data));
        Assert.True(data.Bytes.IsConsumed);
        var read = Assert.IsType<RemoteReplicaRead.Data>(serializer.Deserialize<RemoteReplicaRead>(reply).Value);
        Assert.Equal(bytes, read.Bytes.ToArray());
        read.Bytes.Dispose();
    }

    /// <summary>
    /// A read reply's case other than DATA carries no bytes: one which carries some is refused, and the bytes it decoded are
    /// released, as they are when a case is refused for another field.
    /// </summary>
    [Fact]
    public void AReadCaseOtherThanDataReleasesAndRefusesTheBytesItCarries()
    {
        var missing = WireBytes.Arc(1);
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = ReplicaReplyTag.Second, Bytes = missing }));
        Assert.True(WireBytes.IsReleased(missing));

        var tooLarge = WireBytes.Arc(1, 2);
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = ReplicaReplyTag.TooLarge, Bytes = tooLarge }));
        Assert.True(WireBytes.IsReleased(tooLarge));
    }

    /// <summary>
    /// OR-I15: a version-1 reply's other cases encode their absent DATA as a null reference, which a version-2 caller cannot
    /// decode; it reports the reply as <see cref="InvalidDataException"/>, so silos of the two versions never form one
    /// cluster unnoticed.
    /// </summary>
    [Fact]
    public void AVersionOneReplyIsRefusedAsInvalidData()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var failure = Assert.ThrowsAny<Exception>(() => serializer.Deserialize<RemoteReplicaRead>(Convert.FromHexString("200005c10101010101c101c101e0")));

        _ = Assert.IsType<InvalidDataException>(LedgerReplicaCallResults.Failure(new NodeId(Guid.NewGuid()), failure, CancellationToken.None));
    }

    /// <summary>A case which lacks a field it carries is refused: a refusal of no known kind or without a message, a corruption without a message, and TooLarge or OutsideBatch of no positive length.</summary>
    [Fact]
    public void ACaseWhichLacksAFieldIsRefused()
    {
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = ReplicaReplyTag.TooLarge }));
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = ReplicaReplyTag.OutsideBatch }));
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaWriteOutcomeConverter().ConvertFromSurrogate(new RemoteReplicaWriteOutcomeSurrogate { Case = ReplicaReplyTag.Refused, Refusal = 9, Message = "?" }));
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaTailReadConverter().ConvertFromSurrogate(new RemoteReplicaTailReadSurrogate { Case = ReplicaReplyTag.Refused, Refusal = 1 }));
        _ = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaWriteOutcomeConverter().ConvertFromSurrogate(new RemoteReplicaWriteOutcomeSurrogate { Case = ReplicaReplyTag.Corrupt }));
    }

    /// <summary>
    /// Orleans §7.2: each value has exactly one accepted encoding, so a case which carries a field it does not own is
    /// refused, naming the field, whatever value the field holds.
    /// </summary>
    [Fact]
    public void ACaseWhichCarriesAFieldItDoesNotOwnIsRefused()
    {
        AssertRefused(() => new RemoteReplicaWriteOutcomeConverter().ConvertFromSurrogate(new RemoteReplicaWriteOutcomeSurrogate { Case = ReplicaReplyTag.First, Message = "m" }), "Message");
        AssertRefused(() => new RemoteReplicaWriteOutcomeConverter().ConvertFromSurrogate(new RemoteReplicaWriteOutcomeSurrogate { Case = ReplicaReplyTag.Second, Refusal = 1 }), "Refusal");
        AssertRefused(() => new RemoteReplicaWriteOutcomeConverter().ConvertFromSurrogate(new RemoteReplicaWriteOutcomeSurrogate { Case = ReplicaReplyTag.Refused, Refusal = 1, Message = "m", Slot = 0 }), "Slot");
        AssertRefused(() => new RemoteReplicaWriteOutcomeConverter().ConvertFromSurrogate(new RemoteReplicaWriteOutcomeSurrogate { Case = ReplicaReplyTag.Corrupt, Refusal = 2, Message = "m" }), "Refusal");
        AssertRefused(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = ReplicaReplyTag.Dropped, Length = 4 }), "Length");
        AssertRefused(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = ReplicaReplyTag.TooLarge, Length = 9, Slot = 1 }), "Slot");
        AssertRefused(() => new RemoteReplicaTailReadConverter().ConvertFromSurrogate(new RemoteReplicaTailReadSurrogate { Case = ReplicaReplyTag.Backpressure, IsSealed = true }), "IsSealed");
        AssertRefused(() => new RemoteReplicaTailReadConverter().ConvertFromSurrogate(new RemoteReplicaTailReadSurrogate { Case = ReplicaReplyTag.NotThisNode, ContiguousTail = 3 }), "ContiguousTail");
        AssertRefused(() => new RemoteReplicaTailReadConverter().ConvertFromSurrogate(new RemoteReplicaTailReadSurrogate { Case = ReplicaReplyTag.First, ContiguousTail = 3, Message = "m" }), "Message");

        static void AssertRefused(Action convert, string field) =>
            Assert.Contains($"field {field}", Assert.Throws<ReplicaReplyDecodingException>(convert).Message, StringComparison.Ordinal);
    }

    /// <summary>A DATA reply which carries a field DATA does not own is refused, and the bytes it decoded are released.</summary>
    [Fact]
    public void ADataReplyWhichCarriesAnotherFieldReleasesAndRefusesItsBytes()
    {
        var data = WireBytes.Arc(1, 2, 3);
        var refused = Assert.Throws<ReplicaReplyDecodingException>(() => new RemoteReplicaReadConverter().ConvertFromSurrogate(new RemoteReplicaReadSurrogate { Case = ReplicaReplyTag.First, Bytes = data, Length = 3 }));
        Assert.Contains("field Length", refused.Message, StringComparison.Ordinal);
        Assert.True(WireBytes.IsReleased(data));
    }

    /// <summary>
    /// OR-I17: a read's DATA reply holds its host admission until Orleans first copies it, which releases it, or, dropped
    /// unserialized, until its deadline, and not a tick before.
    /// </summary>
    [Fact]
    public void SerializingADataReplyReleasesItsHostAdmissionAndItsDeadlineReleasesAnUnserializedOne()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var time = new FakeTimeProvider();
        using var host = new LedgerReplicaHost(new LocalNode(), TestReplicaLimits.Of(deadline: TimeSpan.FromSeconds(30)), time);
        var serialized = host.TryAdmit(10)!;
        var dropped = host.TryAdmit(10)!;
        serialized.HoldUntil(time, host.ReplyDeadline);
        dropped.HoldUntil(time, host.ReplyDeadline);
        Assert.Equal((2, 20L), (host.Calls, host.Bytes));

        _ = serializer.SerializeToArray<RemoteReplicaRead>(WireBytes.Data(1) with { Admission = serialized });
        Assert.Equal((1, 10L), (host.Calls, host.Bytes));

        time.Advance(host.ReplyDeadline - TimeSpan.FromTicks(1));
        Assert.Equal((1, 10L), (host.Calls, host.Bytes));
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal((0, 0L), (host.Calls, host.Bytes));
    }

    private static string Wire<T>(Serializer serializer, T value) => Convert.ToHexStringLower(serializer.SerializeToArray(value));
}
