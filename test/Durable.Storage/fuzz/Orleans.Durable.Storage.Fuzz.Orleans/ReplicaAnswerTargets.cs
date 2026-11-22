using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Serialization;

namespace Orleans.Durable.Storage.Fuzz.Orleans;

/// <summary>
/// The replica node grain's three answers (orleans.md §5.12, §7.2), each decoded from its surrogate as a grain reply carries
/// it, then judged by the caller's reply checks (<see cref="LedgerReplicaCallResults"/>). Orleans' codec reads the input
/// into the surrogate, a framing it owns, so an input it refuses lies outside the target's domain.
/// </summary>
/// <remarks>
/// <para>
/// The oracle restates §7.2's rules apart from the converters: the tags 1 to 8 an answer takes, a refusal's kind among
/// 1 to 4 and its message, a corruption's message, a too-large answer's positive length, and each case's own fields, so
/// a case which carries a field it does not own, DATA bytes on any case but DATA among them, is refused. The converter
/// refuses with <see cref="ReplicaReplyDecodingException"/> exactly the surrogates the rules refuse, releasing the bytes
/// one carried, and otherwise accepts the value the rules give, whose encoding is that same surrogate: each value has
/// exactly one accepted encoding.
/// </para>
/// <para>
/// The caller's reply checks then answer the value as §5.12's table does, or throw the exception it names: a refusal's
/// kind maps to its exception, a corruption to <see cref="LedgerReplicaCorruptException"/> with its slot when one is
/// named, <c>NotThisNode</c> to <see cref="LedgerReplicaUnavailableException"/>, and a reply which fails its check to
/// <see cref="InvalidDataException"/>. Each check runs against fixed call parameters which every case's seed meets.
/// </para>
/// </remarks>
/// <typeparam name="TAnswer">The answer.</typeparam>
/// <typeparam name="TSurrogate">Its surrogate.</typeparam>
internal abstract class ReplicaAnswerTarget<TAnswer, TSurrogate> : FuzzTarget
    where TAnswer : struct
    where TSurrogate : struct
{
    /// <summary>The node the reply checks name.</summary>
    protected static readonly NodeId Callee = new(new Guid("2f6c3b1e-7d4a-4e9b-8a1c-5d0e9f3b2a71"));

    private static readonly Lazy<ServiceProvider> Services = new(static () => new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(ILedgerStoreGrain).Assembly))
        .BuildServiceProvider());

    private static readonly Lazy<Serializer> Codec = new(static () => Services.Value.GetRequiredService<Serializer>());

    public override int MaxLength => 4096;

    public override IEnumerable<byte[]> Seeds(GoldenVectors vectors) =>
        [.. Cases().Select(static value => Codec.Value.SerializeToArray(value)), .. Refused().Select(static surrogate => Codec.Value.SerializeToArray(surrogate))];

    protected override FuzzOutcome Decode(ReadOnlySpan<byte> input)
    {
        TSurrogate surrogate;
        try
        {
            surrogate = Codec.Value.Deserialize<TSurrogate>(input.ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return FuzzOutcome.None;
        }

        var carried = Bytes(surrogate);
        var expected = Expected(surrogate);
        TAnswer value;
        try
        {
            value = FromSurrogate(surrogate);
        }
        catch (ReplicaReplyDecodingException)
        {
            Require(expected is null, "refused a surrogate which §7.2's rules accept.");
            Require(carried.IsEmpty || IsReleased(carried), "refused a surrogate without releasing the bytes it carried.");
            return FuzzOutcome.Rejected;
        }

        Require(expected is not null, $"accepted a surrogate which §7.2's rules refuse, as {value}.");
        Require(value.Equals(expected.Value), $"accepted {value} where §7.2's rules give {expected}.");
        Require(Same(surrogate, ToSurrogate(value)), $"accepted {value}, whose encoding is not the surrogate: each value has one accepted encoding.");
        CheckReply(value);
        return FuzzOutcome.Accepted;
    }

    /// <summary>Returns a value of every case.</summary>
    protected abstract IEnumerable<TAnswer> Cases();

    /// <summary>Returns surrogates the converter refuses.</summary>
    protected abstract IEnumerable<TSurrogate> Refused();

    protected abstract TAnswer FromSurrogate(in TSurrogate surrogate);

    protected abstract TSurrogate ToSurrogate(in TAnswer value);

    /// <summary>Returns the bytes a surrogate carries, which its decoding owns.</summary>
    protected virtual OrleansBuffer Bytes(in TSurrogate surrogate) => default;

    /// <summary>Returns the value §7.2's rules give for <paramref name="surrogate"/>, or null when they refuse it.</summary>
    protected abstract TAnswer? Expected(in TSurrogate surrogate);

    /// <summary>Returns whether two surrogates are one encoding: every field equal, bytes compared by content.</summary>
    protected abstract bool Same(in TSurrogate decoded, in TSurrogate encoded);

    /// <summary>Applies the caller's reply checks to an accepted value, releasing the bytes it holds.</summary>
    protected abstract void CheckReply(in TAnswer value);

    /// <summary>
    /// Returns the value §7.2's rules give for the cases every answer shares: <c>Dropped</c>, <c>Backpressure</c>,
    /// <c>NotThisNode</c>, a refusal of a known kind with its message, and a corruption with its message; or null when the
    /// tag names none of them or its case lacks a field.
    /// </summary>
    protected static object? Shared(byte tag, byte refusal, string? message, long? slot) => tag switch
    {
        3 => ReplicaWriteOutcome.Dropped.Instance,
        4 => ReplicaWriteOutcome.Backpressure.Instance,
        5 => NotThisNode.Instance,
        6 when refusal is >= 1 and <= 4 && message is not null => new ReplicaCallRefused((ReplicaRefusal)refusal, message),
        7 when message is not null => new ReplicaCorrupt(slot, message),
        _ => null,
    };

    /// <summary>
    /// Returns whether case <paramref name="tag"/> sets a shared field it does not own: only a refusal (6) owns a kind, only
    /// a refusal and a corruption (7) own a message, and only a corruption owns a slot.
    /// </summary>
    protected static bool SetsUnownedShared(byte tag, byte refusal, string? message, long? slot) =>
        refusal != 0 && tag != 6 || message is not null && tag is not (6 or 7) || slot is not null && tag != 7;

    /// <summary>
    /// Runs one reply check and requires its outcome to be the one §5.12's table gives for <paramref name="value"/>, a
    /// shared case's exception when it is one, and otherwise <paramref name="returns"/>' judgement of what it returned.
    /// </summary>
    protected void RequireReply<TResult>(object? value, Func<TResult> check, Func<TResult, bool> returns, string what)
    {
        TResult result;
        try
        {
            result = check();
        }
        catch (Exception thrown) when (thrown is not FuzzFindingException)
        {
            Require(Throws(value, thrown), $"the reply check of {what} threw {thrown.GetType().Name}: {thrown.Message}");
            return;
        }

        Require(value is not (NotThisNode or ReplicaCallRefused or ReplicaCorrupt) && returns(result), $"the reply check of {what} returned {result}.");
    }

    /// <summary>
    /// Returns whether a reply check rightly threw <paramref name="thrown"/> for <paramref name="value"/>: the shared
    /// cases' exceptions, and <see cref="InvalidDataException"/> for any other case, whose check failed.
    /// </summary>
    private static bool Throws(object? value, Exception thrown) => value switch
    {
        NotThisNode => thrown is LedgerReplicaUnavailableException,
        ReplicaCallRefused { Kind: ReplicaRefusal.Argument } => thrown.GetType() == typeof(ArgumentException),
        ReplicaCallRefused { Kind: ReplicaRefusal.ArgumentOutOfRange } => thrown is ArgumentOutOfRangeException,
        ReplicaCallRefused { Kind: ReplicaRefusal.UnsupportedVersion } => thrown is NotSupportedException,
        ReplicaCallRefused { Kind: ReplicaRefusal.Stopping } => thrown is LedgerReplicaUnavailableException,
        ReplicaCorrupt corrupt => thrown is LedgerReplicaCorruptException failure
            && failure.Slot?.Value == (corrupt.Slot is >= 0 ? corrupt.Slot : null),
        _ => thrown is InvalidDataException,
    };

    /// <summary>Returns whether an Orleans buffer has been released.</summary>
    protected static bool IsReleased(OrleansBuffer buffer)
    {
        try
        {
            _ = buffer.ToArray();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}

/// <summary>A replica write's answer (<c>orleans-replica-write</c>), checked as a writer write's and as a recovery write's.</summary>
internal sealed class ReplicaWriteAnswerTarget : ReplicaAnswerTarget<RemoteReplicaWriteOutcome, RemoteReplicaWriteOutcomeSurrogate>
{
    private static readonly RemoteReplicaWriteOutcomeConverter Converter = new();

    public override string Name => "orleans-replica-write";

    protected override IEnumerable<RemoteReplicaWriteOutcome> Cases() =>
    [
        ReplicaWriteOutcome.Written.Instance,
        ReplicaWriteOutcome.Sealed.Instance,
        ReplicaWriteOutcome.Dropped.Instance,
        ReplicaWriteOutcome.Backpressure.Instance,
        NotThisNode.Instance,
        new ReplicaCallRefused(ReplicaRefusal.Argument, "an empty batch"),
        new ReplicaCallRefused(ReplicaRefusal.ArgumentOutOfRange, "a slot past the window"),
        new ReplicaCallRefused(ReplicaRefusal.UnsupportedVersion, "version 1"),
        new ReplicaCallRefused(ReplicaRefusal.Stopping, "stopping"),
        new ReplicaCorrupt(7, "a slot which fails its CRC"),
        new ReplicaCorrupt(null, "a replica whose catalog fails its CRC"),
    ];

    protected override IEnumerable<RemoteReplicaWriteOutcomeSurrogate> Refused() =>
    [
        new() { Case = 0 },
        new() { Case = ReplicaReplyTag.TooLarge },
        new() { Case = ReplicaReplyTag.Refused, Refusal = 0, Message = "m" },
        new() { Case = ReplicaReplyTag.Refused, Refusal = 5, Message = "m" },
        new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)ReplicaRefusal.Argument },
        new() { Case = ReplicaReplyTag.Corrupt, Slot = 3 },
        new() { Case = ReplicaReplyTag.First, Message = "m" },
        new() { Case = ReplicaReplyTag.Dropped, Slot = 2 },
        new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)ReplicaRefusal.Argument, Message = "m", Slot = 1 },
        new() { Case = ReplicaReplyTag.Corrupt, Refusal = (byte)ReplicaRefusal.Argument, Message = "m" },
    ];

    protected override RemoteReplicaWriteOutcome FromSurrogate(in RemoteReplicaWriteOutcomeSurrogate surrogate) => Converter.ConvertFromSurrogate(surrogate);

    protected override RemoteReplicaWriteOutcomeSurrogate ToSurrogate(in RemoteReplicaWriteOutcome value) => Converter.ConvertToSurrogate(value);

    protected override RemoteReplicaWriteOutcome? Expected(in RemoteReplicaWriteOutcomeSurrogate surrogate) => surrogate.Case switch
    {
        _ when SetsUnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot) => null,
        1 => ReplicaWriteOutcome.Written.Instance,
        2 => ReplicaWriteOutcome.Sealed.Instance,
        _ => Shared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot) switch
        {
            ReplicaWriteOutcome.Dropped dropped => dropped,
            ReplicaWriteOutcome.Backpressure backpressure => backpressure,
            NotThisNode notThisNode => notThisNode,
            ReplicaCallRefused refused => refused,
            ReplicaCorrupt corrupt => corrupt,
            _ => null,
        },
    };

    protected override bool Same(in RemoteReplicaWriteOutcomeSurrogate decoded, in RemoteReplicaWriteOutcomeSurrogate encoded) =>
        (decoded.Case, decoded.Refusal, decoded.Message, decoded.Slot) == (encoded.Case, encoded.Refusal, encoded.Message, encoded.Slot);

    protected override void CheckReply(in RemoteReplicaWriteOutcome value)
    {
        var answer = value;
        RequireReply(answer.Value, () => LedgerReplicaCallResults.Write(Callee, answer, recovery: false), result => Equals(result.Value, answer.Value), "a writer write");

        // A recovery write is never answered Sealed: the check refuses it as InvalidDataException.
        RequireReply(
            answer.Value is ReplicaWriteOutcome.Sealed ? null : answer.Value,
            () => LedgerReplicaCallResults.Write(Callee, answer, recovery: true),
            result => answer.Value is not ReplicaWriteOutcome.Sealed && Equals(result.Value, answer.Value),
            "a recovery write");
    }
}

/// <summary>
/// A replica read's answer (<c>orleans-replica-read</c>), checked as a whole read which reserved
/// <see cref="Reserved"/> bytes.
/// </summary>
internal sealed class ReplicaReadAnswerTarget : ReplicaAnswerTarget<RemoteReplicaRead, RemoteReplicaReadSurrogate>
{
    /// <summary>The reply the read reserved: DATA up to its length passes the check, and longer DATA does not.</summary>
    private const int Reserved = 8;

    private static readonly RemoteReplicaReadConverter Converter = new();

    public override string Name => "orleans-replica-read";

    protected override IEnumerable<RemoteReplicaRead> Cases() =>
    [
        new RemoteReplicaRead.Data(JournalSeeds.Bytes(1, 2, 3)),
        new RemoteReplicaRead.Data(JournalSeeds.Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9)),
        new RemoteReplicaRead.Data(default),
        new RemoteReplicaRead.TooLarge(32),
        new ReplicaRead.OutsideBatch(5),
        ReplicaRead.Missing.Instance,
        ReplicaWriteOutcome.Dropped.Instance,
        ReplicaWriteOutcome.Backpressure.Instance,
        NotThisNode.Instance,
        new ReplicaCallRefused(ReplicaRefusal.ArgumentOutOfRange, "a range past the value"),
        new ReplicaCorrupt(4, "a slot which fails its CRC"),
    ];

    protected override IEnumerable<RemoteReplicaReadSurrogate> Refused() =>
    [
        new() { Case = 0 },
        new() { Case = 10 },
        new() { Case = ReplicaReplyTag.OutsideBatch, Length = 0 },
        new() { Case = ReplicaReplyTag.OutsideBatch, Length = -1 },
        new() { Case = ReplicaReplyTag.Second, Bytes = JournalSeeds.Bytes(1) },
        new() { Case = ReplicaReplyTag.Refused, Refusal = 6, Message = "m", Bytes = JournalSeeds.Bytes(1, 2) },
        new() { Case = ReplicaReplyTag.TooLarge, Length = 0 },
        new() { Case = ReplicaReplyTag.TooLarge, Length = -1 },
        new() { Case = ReplicaReplyTag.Corrupt },
        new() { Case = ReplicaReplyTag.First, Bytes = JournalSeeds.Bytes(1), Length = 1 },
        new() { Case = ReplicaReplyTag.First, Bytes = JournalSeeds.Bytes(1), Message = "m" },
        new() { Case = ReplicaReplyTag.TooLarge, Length = 9, Slot = 1 },
        new() { Case = ReplicaReplyTag.Second, Length = 4 },
    ];

    protected override RemoteReplicaRead FromSurrogate(in RemoteReplicaReadSurrogate surrogate) => Converter.ConvertFromSurrogate(surrogate);

    protected override RemoteReplicaReadSurrogate ToSurrogate(in RemoteReplicaRead value) => Converter.ConvertToSurrogate(value);

    protected override OrleansBuffer Bytes(in RemoteReplicaReadSurrogate surrogate) => surrogate.Bytes;

    protected override RemoteReplicaRead? Expected(in RemoteReplicaReadSurrogate surrogate) => surrogate.Case switch
    {
        _ when SetsUnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot) => null,
        not (8 or 9) when surrogate.Length != 0 => null,
        1 => new RemoteReplicaRead.Data(surrogate.Bytes),
        _ when !surrogate.Bytes.IsEmpty => null,
        2 => ReplicaRead.Missing.Instance,
        8 when surrogate.Length > 0 => new RemoteReplicaRead.TooLarge(surrogate.Length),
        9 when surrogate.Length > 0 => new ReplicaRead.OutsideBatch(surrogate.Length),
        _ => Shared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot) switch
        {
            ReplicaWriteOutcome.Dropped dropped => dropped,
            ReplicaWriteOutcome.Backpressure backpressure => backpressure,
            NotThisNode notThisNode => notThisNode,
            ReplicaCallRefused refused => refused,
            ReplicaCorrupt corrupt => corrupt,
            _ => null,
        },
    };

    protected override bool Same(in RemoteReplicaReadSurrogate decoded, in RemoteReplicaReadSurrogate encoded) =>
        (decoded.Case, decoded.Length, decoded.Refusal, decoded.Message, decoded.Slot) == (encoded.Case, encoded.Length, encoded.Refusal, encoded.Message, encoded.Slot)
        && decoded.Bytes.ToArray().AsSpan().SequenceEqual(encoded.Bytes.ToArray());

    protected override void CheckReply(in RemoteReplicaRead value)
    {
        var answer = value;
        var data = (answer.Value as RemoteReplicaRead.Data)?.Bytes;
        var sent = data?.ToArray();
        RequireReply(
            answer.Value is RemoteReplicaRead.Data or RemoteReplicaRead.TooLarge or ReplicaRead.OutsideBatch ? null : answer.Value,
            () => LedgerReplicaCallResults.Read(Callee, answer, Reserved, ranged: false),
            result => Returned(answer, sent, result),
            "a whole read");
        Require(data is not { } bytes || bytes.IsEmpty || IsReleased(bytes), "the reply check of a read kept the DATA's received bytes.");
    }

    /// <summary>
    /// Returns whether a read's check rightly returned <paramref name="result"/>: DATA of 1 to <see cref="Reserved"/> bytes,
    /// <paramref name="sent"/>, moved whole into Clog's pool, or the store's other answer unchanged; and releases the
    /// result's batch.
    /// </summary>
    private static bool Returned(RemoteReplicaRead answer, byte[]? sent, ReplicaRead result)
    {
        if (!result.TryGetValue(out global::Orleans.Durable.Storage.Engine.Buffers.ArcBuffer batch))
        {
            return answer.Value is not RemoteReplicaRead.Data && Equals(result.Value, answer.Value);
        }

        using (batch)
        {
            return sent is { Length: >= 1 and <= Reserved } && batch.ToArray().AsSpan().SequenceEqual(sent);
        }
    }
}

/// <summary>
/// A replica's contiguous-tail read's answer (<c>orleans-replica-tail</c>), checked as a call which sent the contiguous
/// tail <see cref="Sent"/>.
/// </summary>
internal sealed class ReplicaTailAnswerTarget : ReplicaAnswerTarget<RemoteReplicaTailRead, RemoteReplicaTailReadSurrogate>
{
    /// <summary>The contiguous tail the call sent: a tail at or above it passes the check, and a lower one does not.</summary>
    private const long Sent = 5;

    private static readonly RemoteReplicaTailReadConverter Converter = new();

    public override string Name => "orleans-replica-tail";

    protected override IEnumerable<RemoteReplicaTailRead> Cases() =>
    [
        new RemoteReplicaTailRead.Tail(Sent, IsSealed: false),
        new RemoteReplicaTailRead.Tail(Sent + 12, IsSealed: true),
        new RemoteReplicaTailRead.Tail(Sent - 1, IsSealed: true),
        ReplicaWriteOutcome.Dropped.Instance,
        ReplicaWriteOutcome.Backpressure.Instance,
        NotThisNode.Instance,
        new ReplicaCallRefused(ReplicaRefusal.Stopping, "stopping"),
        new ReplicaCorrupt(-1, "a seal record which fails its CRC"),
    ];

    protected override IEnumerable<RemoteReplicaTailReadSurrogate> Refused() =>
    [
        new() { Case = 0 },
        new() { Case = ReplicaReplyTag.Second },
        new() { Case = ReplicaReplyTag.TooLarge },
        new() { Case = ReplicaReplyTag.Refused, Refusal = 3 },
        new() { Case = ReplicaReplyTag.Corrupt, Slot = 1 },
        new() { Case = ReplicaReplyTag.First, ContiguousTail = Sent, Message = "m" },
        new() { Case = ReplicaReplyTag.Dropped, ContiguousTail = Sent },
        new() { Case = ReplicaReplyTag.Backpressure, IsSealed = true },
        new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)ReplicaRefusal.Stopping, Message = "m", IsSealed = true },
    ];

    protected override RemoteReplicaTailRead FromSurrogate(in RemoteReplicaTailReadSurrogate surrogate) => Converter.ConvertFromSurrogate(surrogate);

    protected override RemoteReplicaTailReadSurrogate ToSurrogate(in RemoteReplicaTailRead value) => Converter.ConvertToSurrogate(value);

    protected override RemoteReplicaTailRead? Expected(in RemoteReplicaTailReadSurrogate surrogate) => surrogate.Case switch
    {
        _ when SetsUnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot) => null,
        not 1 when surrogate.ContiguousTail != 0 || surrogate.IsSealed => null,
        1 => new RemoteReplicaTailRead.Tail(surrogate.ContiguousTail, surrogate.IsSealed),
        _ => Shared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot) switch
        {
            ReplicaWriteOutcome.Dropped dropped => dropped,
            ReplicaWriteOutcome.Backpressure backpressure => backpressure,
            NotThisNode notThisNode => notThisNode,
            ReplicaCallRefused refused => refused,
            ReplicaCorrupt corrupt => corrupt,
            _ => null,
        },
    };

    protected override bool Same(in RemoteReplicaTailReadSurrogate decoded, in RemoteReplicaTailReadSurrogate encoded) =>
        (decoded.Case, decoded.ContiguousTail, decoded.IsSealed, decoded.Refusal, decoded.Message, decoded.Slot)
            == (encoded.Case, encoded.ContiguousTail, encoded.IsSealed, encoded.Refusal, encoded.Message, encoded.Slot);

    protected override void CheckReply(in RemoteReplicaTailRead value)
    {
        var answer = value;
        RequireReply(
            answer.Value is RemoteReplicaTailRead.Tail ? null : answer.Value,
            () => LedgerReplicaCallResults.Tail(Callee, answer, new Slot(Sent)),
            result => answer.Value switch
            {
                RemoteReplicaTailRead.Tail tail => tail.ContiguousTail >= Sent && result.TryGetValue(out LedgerReplicaTail returned)
                    && returned == new LedgerReplicaTail(new Slot(tail.ContiguousTail), tail.IsSealed),
                _ => Equals(result.Value, answer.Value),
            },
            "a contiguous-tail read");
    }
}
