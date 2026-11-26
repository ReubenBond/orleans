using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>
/// The case tags of the remote replica answers' surrogates (orleans.md §7.2, conventions.md §15). A case the answers share
/// has one tag in each: <see cref="ReplicaWriteOutcome.Dropped"/> is 3 wherever it appears.
/// </summary>
internal static class ReplicaReplyTag
{
    /// <summary>A write's <c>Written</c>, a read's <c>Data</c>, and a contiguous-tail read's <c>Tail</c>.</summary>
    public const byte First = 1;

    /// <summary>A write's <c>Sealed</c> and a read's <c>Missing</c>; a contiguous-tail read has none.</summary>
    public const byte Second = 2;

    /// <summary><see cref="ReplicaWriteOutcome.Dropped"/>.</summary>
    public const byte Dropped = 3;

    /// <summary><see cref="ReplicaWriteOutcome.Backpressure"/>.</summary>
    public const byte Backpressure = 4;

    /// <summary><see cref="NotThisNode"/>.</summary>
    public const byte NotThisNode = 5;

    /// <summary><see cref="ReplicaCallRefused"/>.</summary>
    public const byte Refused = 6;

    /// <summary><see cref="ReplicaCorrupt"/>.</summary>
    public const byte Corrupt = 7;

    /// <summary>A read's <see cref="RemoteReplicaRead.TooLarge"/>.</summary>
    public const byte TooLarge = 8;

    /// <summary>A byte-range read's <see cref="ReplicaRead.OutsideBatch"/>.</summary>
    public const byte OutsideBatch = 9;

    /// <summary>Returns the refusal a surrogate carries, refusing an unknown kind or a missing message.</summary>
    public static ReplicaCallRefused Refusal(byte kind, string? message) =>
        Enum.IsDefined((ReplicaRefusal)kind) && message is not null
            ? new ReplicaCallRefused((ReplicaRefusal)kind, message)
            : throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A replica call's refusal names kind {kind} and {(message is null ? "no" : "a")} message."));

    /// <summary>Returns the corruption a surrogate carries, refusing a missing message.</summary>
    public static ReplicaCorrupt Corruption(long? slot, string? message) =>
        message is not null ? new ReplicaCorrupt(slot, message) : throw new ReplicaReplyDecodingException("A replica's corruption answer carries no message.");

    /// <summary>Returns the exception for an unknown tag of the answer <paramref name="answer"/>, which takes <paramref name="tags"/>.</summary>
    public static ReplicaReplyDecodingException Unknown(string answer, byte tag, params byte[] tags) =>
        new(string.Create(CultureInfo.InvariantCulture, $"A {answer}'s case tag is one of {string.Join(", ", tags)}, not {tag}."));

    /// <summary>
    /// Throws when the case <paramref name="tag"/> of the answer <paramref name="answer"/> carries <paramref name="field"/>, a
    /// field it does not own, so each value has exactly one accepted encoding (orleans.md §7.2).
    /// </summary>
    public static void ThrowIfCarries(string answer, byte tag, string? field)
    {
        if (field is not null)
        {
            throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A {answer}'s case {tag} carries the field {field}, which it does not own."));
        }
    }

    /// <summary>
    /// Returns the first of the shared fields a case sets which it does not own, its owned fields given: a refusal's kind,
    /// a refusal's or corruption's message, and a corruption's slot; or null when it sets none.
    /// </summary>
    public static string? Unowned(byte refusal, string? message, long? slot, bool ownsRefusal, bool ownsMessage, bool ownsSlot) =>
        !ownsRefusal && refusal != 0 ? "Refusal"
        : !ownsMessage && message is not null ? "Message"
        : !ownsSlot && slot is not null ? "Slot"
        : null;

    /// <summary>
    /// Returns the first shared field <paramref name="tag"/>'s case sets which it does not own: a refusal owns its kind and
    /// message, a corruption its message and slot, and every other case none of them.
    /// </summary>
    public static string? UnownedShared(byte tag, byte refusal, string? message, long? slot) =>
        Unowned(refusal, message, slot, ownsRefusal: tag == Refused, ownsMessage: tag is Refused or Corrupt, ownsSlot: tag == Corrupt);
}

/// <summary>
/// A reply of a remote replica call failed to decode at the caller: an unknown case tag, or a case without the fields it
/// carries (conventions.md §15). It is the caller's own finding, which no callee sends, so however Orleans wraps it the
/// caller reports it as an <see cref="InvalidDataException"/> (orleans.md §5.12).
/// </summary>
internal sealed class ReplicaReplyDecodingException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ReplicaReplyDecodingException"/> class.</summary>
    public ReplicaReplyDecodingException()
        : base("A replica call's reply failed to decode.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ReplicaReplyDecodingException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ReplicaReplyDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ReplicaReplyDecodingException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ReplicaReplyDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A <see cref="RemoteReplicaWriteOutcome"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct RemoteReplicaWriteOutcomeSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="ReplicaReplyTag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a refusal's kind; 0 for the other cases.</summary>
    [Id(1)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's or corruption's message.</summary>
    [Id(2)]
    public string? Message { get; init; }

    /// <summary>Gets or initializes a corruption's slot, when the store named one.</summary>
    [Id(3)]
    public long? Slot { get; init; }

    /// <summary>Gets or initializes a <see cref="ReplicaWriteOutcome.Written"/> answer's lease grant in ticks; 0 for the other cases.</summary>
    [Id(4)]
    public long LeaseGrantTicks { get; init; }
}

/// <summary>
/// Converts a <see cref="RemoteReplicaWriteOutcome"/> to its surrogate and back, refusing an unknown tag and a case which
/// carries a field it does not own.
/// </summary>
[RegisterConverter]
internal sealed class RemoteReplicaWriteOutcomeConverter : IConverter<RemoteReplicaWriteOutcome, RemoteReplicaWriteOutcomeSurrogate>
{
    private const string Answer = "remote replica write outcome";

    /// <inheritdoc/>
    /// <exception cref="ReplicaReplyDecodingException">The tag names no case, or its case lacks a field or carries one it does not own.</exception>
    public RemoteReplicaWriteOutcome ConvertFromSurrogate(in RemoteReplicaWriteOutcomeSurrogate surrogate)
    {
        var unowned = surrogate.Case != ReplicaReplyTag.First && surrogate.LeaseGrantTicks != 0 ? "LeaseGrantTicks"
            : ReplicaReplyTag.UnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot);
        ReplicaReplyTag.ThrowIfCarries(Answer, surrogate.Case, unowned);
        return surrogate.Case switch
        {
            ReplicaReplyTag.First when surrogate.LeaseGrantTicks < 0 => throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A written outcome's lease grant ticks cannot be negative: {surrogate.LeaseGrantTicks}.")),
            ReplicaReplyTag.First when surrogate.LeaseGrantTicks == 0 => ReplicaWriteOutcome.Written.Instance,
            ReplicaReplyTag.First => new ReplicaWriteOutcome.Written(TimeSpan.FromTicks(surrogate.LeaseGrantTicks)),
            ReplicaReplyTag.Second => ReplicaWriteOutcome.Sealed.Instance,
            ReplicaReplyTag.Dropped => ReplicaWriteOutcome.Dropped.Instance,
            ReplicaReplyTag.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
            ReplicaReplyTag.NotThisNode => NotThisNode.Instance,
            ReplicaReplyTag.Refused => ReplicaReplyTag.Refusal(surrogate.Refusal, surrogate.Message),
            ReplicaReplyTag.Corrupt => ReplicaReplyTag.Corruption(surrogate.Slot, surrogate.Message),
            _ => throw ReplicaReplyTag.Unknown(Answer, surrogate.Case, 1, 2, 3, 4, 5, 6, 7),
        };
    }

    /// <inheritdoc/>
    public RemoteReplicaWriteOutcomeSurrogate ConvertToSurrogate(in RemoteReplicaWriteOutcome value) => value switch
    {
        ReplicaWriteOutcome.Written written => new() { Case = ReplicaReplyTag.First, LeaseGrantTicks = written.LeaseGrant.Ticks },
        ReplicaWriteOutcome.Sealed => new() { Case = ReplicaReplyTag.Second },
        ReplicaWriteOutcome.Dropped => new() { Case = ReplicaReplyTag.Dropped },
        ReplicaWriteOutcome.Backpressure => new() { Case = ReplicaReplyTag.Backpressure },
        NotThisNode => new() { Case = ReplicaReplyTag.NotThisNode },
        ReplicaCallRefused refused => new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        ReplicaCorrupt corrupt => new() { Case = ReplicaReplyTag.Corrupt, Slot = corrupt.Slot, Message = corrupt.Message },
        null => throw new UnreachableException(),
    };
}

/// <summary>A <see cref="RemoteReplicaRead"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct RemoteReplicaReadSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="ReplicaReplyTag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the DATA's bytes; empty for the other cases.</summary>
    [Id(1)]
    public WireArcBuffer Bytes { get; init; }

    /// <summary>Gets or initializes a <see cref="RemoteReplicaRead.TooLarge"/> or <see cref="ReplicaRead.OutsideBatch"/> answer's batch length; 0 for the other cases.</summary>
    [Id(2)]
    public int Length { get; init; }

    /// <summary>Gets or initializes a refusal's kind; 0 for the other cases.</summary>
    [Id(3)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's or corruption's message.</summary>
    [Id(4)]
    public string? Message { get; init; }

    /// <summary>Gets or initializes a corruption's slot, when the store named one.</summary>
    [Id(5)]
    public long? Slot { get; init; }
}

/// <summary>
/// Converts a <see cref="RemoteReplicaRead"/> to its surrogate and back, refusing an unknown tag and a case which carries a
/// field it does not own. Converting a reply to its surrogate, which Orleans' first copy of the response does as the grain
/// method returns, releases the read's host admission (OR-I17). The decoded DATA passes to the caller with its answer
/// (OR-I18); any other case, and a refused DATA, keeps no bytes, so the converter releases what it decoded for one.
/// </summary>
[RegisterConverter]
internal sealed class RemoteReplicaReadConverter : IConverter<RemoteReplicaRead, RemoteReplicaReadSurrogate>
{
    private const string Answer = "remote replica read";

    /// <inheritdoc/>
    /// <exception cref="ReplicaReplyDecodingException">The tag names no case, its case lacks a field, or it carries one it does not own: DATA bytes on any case but DATA among them.</exception>
    public RemoteReplicaRead ConvertFromSurrogate(in RemoteReplicaReadSurrogate surrogate)
    {
        var bytes = surrogate.Bytes ?? WireArcBuffer.Empty;
        var kept = false;
        try
        {
            var unowned = surrogate.Case != ReplicaReplyTag.First && !bytes.IsEmpty ? "Bytes"
                : surrogate.Case is not (ReplicaReplyTag.TooLarge or ReplicaReplyTag.OutsideBatch) && surrogate.Length != 0 ? "Length"
                : ReplicaReplyTag.UnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot);
            ReplicaReplyTag.ThrowIfCarries(Answer, surrogate.Case, unowned);
            if (surrogate.Case == ReplicaReplyTag.First)
            {
                kept = true;
                return new RemoteReplicaRead.Data(bytes);
            }

            return WithoutData(surrogate);
        }
        finally
        {
            if (!kept)
            {
                bytes.Dispose();
            }
        }
    }

    private static RemoteReplicaRead WithoutData(in RemoteReplicaReadSurrogate surrogate) => surrogate.Case switch
    {
        ReplicaReplyTag.Second => ReplicaRead.Missing.Instance,
        ReplicaReplyTag.Dropped => ReplicaWriteOutcome.Dropped.Instance,
        ReplicaReplyTag.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
        ReplicaReplyTag.NotThisNode => NotThisNode.Instance,
        ReplicaReplyTag.Refused => ReplicaReplyTag.Refusal(surrogate.Refusal, surrogate.Message),
        ReplicaReplyTag.Corrupt => ReplicaReplyTag.Corruption(surrogate.Slot, surrogate.Message),
        ReplicaReplyTag.TooLarge when surrogate.Length > 0 => new RemoteReplicaRead.TooLarge(surrogate.Length),
        ReplicaReplyTag.TooLarge => throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A too-large answer names a positive length, not {surrogate.Length}.")),
        ReplicaReplyTag.OutsideBatch when surrogate.Length > 0 => new ReplicaRead.OutsideBatch(surrogate.Length),
        ReplicaReplyTag.OutsideBatch => throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"An outside-the-batch answer names a positive length, not {surrogate.Length}.")),
        _ => throw ReplicaReplyTag.Unknown(Answer, surrogate.Case, 1, 2, 3, 4, 5, 6, 7, 8, 9),
    };

    /// <inheritdoc/>
    public RemoteReplicaReadSurrogate ConvertToSurrogate(in RemoteReplicaRead value) => value switch
    {
        RemoteReplicaRead.Data data => Serialized(data),
        RemoteReplicaRead.TooLarge tooLarge => new() { Case = ReplicaReplyTag.TooLarge, Length = tooLarge.Length },
        ReplicaRead.OutsideBatch outside => new() { Case = ReplicaReplyTag.OutsideBatch, Length = outside.Length },
        ReplicaRead.Missing => new() { Case = ReplicaReplyTag.Second },
        ReplicaWriteOutcome.Dropped => new() { Case = ReplicaReplyTag.Dropped },
        ReplicaWriteOutcome.Backpressure => new() { Case = ReplicaReplyTag.Backpressure },
        NotThisNode => new() { Case = ReplicaReplyTag.NotThisNode },
        ReplicaCallRefused refused => new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        ReplicaCorrupt corrupt => new() { Case = ReplicaReplyTag.Corrupt, Slot = corrupt.Slot, Message = corrupt.Message },
        null => throw new UnreachableException(),
    };

    private static RemoteReplicaReadSurrogate Serialized(RemoteReplicaRead.Data data)
    {
        // Release the host's call/byte admission on first conversion while keeping the payload's deadline safety net
        // armed until WireArcBufferCodec.WriteField or a local caller consumes it (OR-I17, OR-I18).
        data.Admission?.ReleaseAdmission();
        return new() { Case = ReplicaReplyTag.First, Bytes = data.Bytes ?? WireArcBuffer.Empty };
    }
}

/// <summary>A <see cref="RemoteReplicaRangeRead"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct RemoteReplicaRangeReadSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="ReplicaReplyTag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the first slot of a <see cref="RemoteReplicaRangeRead.Batch"/>; 0 for other cases.</summary>
    [Id(1)]
    public long FirstSlot { get; init; }

    /// <summary>Gets or initializes the slot count of a <see cref="RemoteReplicaRangeRead.Batch"/>; 0 for other cases.</summary>
    [Id(2)]
    public int Count { get; init; }

    /// <summary>Gets or initializes the packed slot bytes; empty for other cases or empty batches.</summary>
    [Id(3)]
    public WireArcBuffer Bytes { get; init; }

    /// <summary>Gets or initializes a <see cref="RemoteReplicaRangeRead.TooLarge"/> answer's batch length; 0 for other cases.</summary>
    [Id(4)]
    public int Length { get; init; }

    /// <summary>Gets or initializes a refusal's kind; 0 for other cases.</summary>
    [Id(5)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's or corruption's message.</summary>
    [Id(6)]
    public string? Message { get; init; }

    /// <summary>Gets or initializes a corruption's slot, when the store named one.</summary>
    [Id(7)]
    public long? Slot { get; init; }
}

/// <summary>
/// Converts a <see cref="RemoteReplicaRangeRead"/> to its surrogate and back, releasing host admission on outbound
/// conversion and releasing decoded <see cref="OrleansBuffer"/> bytes on every non-Batch or error path (OR-I17, OR-I18).
/// </summary>
[RegisterConverter]
internal sealed class RemoteReplicaRangeReadConverter : IConverter<RemoteReplicaRangeRead, RemoteReplicaRangeReadSurrogate>
{
    private const string Answer = "remote replica range read";

    /// <inheritdoc/>
    public RemoteReplicaRangeRead ConvertFromSurrogate(in RemoteReplicaRangeReadSurrogate surrogate)
    {
        var bytes = surrogate.Bytes ?? WireArcBuffer.Empty;
        var kept = false;
        try
        {
            var unowned = surrogate.Case != ReplicaReplyTag.First && (!bytes.IsEmpty || surrogate.FirstSlot != 0 || surrogate.Count != 0) ? "Batch"
                : surrogate.Case != ReplicaReplyTag.TooLarge && surrogate.Length != 0 ? "Length"
                : ReplicaReplyTag.UnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot);
            ReplicaReplyTag.ThrowIfCarries(Answer, surrogate.Case, unowned);
            if (surrogate.Case == ReplicaReplyTag.First)
            {
                if (surrogate.FirstSlot < 0 || surrogate.Count < 0)
                {
                    throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A range read batch has invalid FirstSlot {surrogate.FirstSlot} or Count {surrogate.Count}."));
                }

                if (surrogate.Count == 0 && !bytes.IsEmpty)
                {
                    throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"An empty range read batch carried {bytes.Length} unexpected bytes."));
                }

                kept = true;
                return new RemoteReplicaRangeRead.Batch(surrogate.FirstSlot, surrogate.Count, bytes);
            }

            return surrogate.Case switch
            {
                ReplicaReplyTag.Dropped => ReplicaWriteOutcome.Dropped.Instance,
                ReplicaReplyTag.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
                ReplicaReplyTag.NotThisNode => NotThisNode.Instance,
                ReplicaReplyTag.Refused => ReplicaReplyTag.Refusal(surrogate.Refusal, surrogate.Message),
                ReplicaReplyTag.Corrupt => ReplicaReplyTag.Corruption(surrogate.Slot, surrogate.Message),
                ReplicaReplyTag.TooLarge when surrogate.Length > 0 => new RemoteReplicaRangeRead.TooLarge(surrogate.Length),
                ReplicaReplyTag.TooLarge => throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A too-large answer names a positive length, not {surrogate.Length}.")),
                _ => throw ReplicaReplyTag.Unknown(Answer, surrogate.Case, 1, 3, 4, 5, 6, 7, 8),
            };
        }
        finally
        {
            if (!kept)
            {
                bytes.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public RemoteReplicaRangeReadSurrogate ConvertToSurrogate(in RemoteReplicaRangeRead value) => value switch
    {
        RemoteReplicaRangeRead.Batch batch => Serialized(batch),
        RemoteReplicaRangeRead.TooLarge tooLarge => new() { Case = ReplicaReplyTag.TooLarge, Length = tooLarge.Length },
        ReplicaWriteOutcome.Dropped => new() { Case = ReplicaReplyTag.Dropped },
        ReplicaWriteOutcome.Backpressure => new() { Case = ReplicaReplyTag.Backpressure },
        NotThisNode => new() { Case = ReplicaReplyTag.NotThisNode },
        ReplicaCallRefused refused => new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        ReplicaCorrupt corrupt => new() { Case = ReplicaReplyTag.Corrupt, Slot = corrupt.Slot, Message = corrupt.Message },
        null => throw new UnreachableException(),
    };

    private static RemoteReplicaRangeReadSurrogate Serialized(RemoteReplicaRangeRead.Batch batch)
    {
        batch.Admission?.ReleaseAdmission();
        return new() { Case = ReplicaReplyTag.First, FirstSlot = batch.FirstSlot, Count = batch.Count, Bytes = batch.Bytes ?? WireArcBuffer.Empty };
    }
}

/// <summary>A <see cref="RemoteReplicaSlotsRead"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct RemoteReplicaSlotsReadSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="ReplicaReplyTag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the slot count of a <see cref="RemoteReplicaSlotsRead.Batch"/>; 0 for other cases.</summary>
    [Id(1)]
    public int Count { get; init; }

    /// <summary>Gets or initializes the packed slot bytes; empty for other cases or empty batches.</summary>
    [Id(2)]
    public WireArcBuffer Bytes { get; init; }

    /// <summary>Gets or initializes a refusal's kind; 0 for other cases.</summary>
    [Id(3)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's or corruption's message.</summary>
    [Id(4)]
    public string? Message { get; init; }

    /// <summary>Gets or initializes a corruption's slot, when the store named one.</summary>
    [Id(5)]
    public long? Slot { get; init; }
}

/// <summary>
/// Converts a <see cref="RemoteReplicaSlotsRead"/> to its surrogate and back, releasing host admission on outbound
/// conversion and releasing decoded <see cref="WireArcBuffer"/> bytes on every non-Batch or error path (OR-I17, OR-I18).
/// </summary>
[RegisterConverter]
internal sealed class RemoteReplicaSlotsReadConverter : IConverter<RemoteReplicaSlotsRead, RemoteReplicaSlotsReadSurrogate>
{
    private const string Answer = "remote replica slots read";

    /// <inheritdoc/>
    public RemoteReplicaSlotsRead ConvertFromSurrogate(in RemoteReplicaSlotsReadSurrogate surrogate)
    {
        var bytes = surrogate.Bytes ?? WireArcBuffer.Empty;
        var kept = false;
        try
        {
            var unowned = surrogate.Case != ReplicaReplyTag.First && (!bytes.IsEmpty || surrogate.Count != 0) ? "Batch"
                : ReplicaReplyTag.UnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot);
            ReplicaReplyTag.ThrowIfCarries(Answer, surrogate.Case, unowned);
            if (surrogate.Case == ReplicaReplyTag.First)
            {
                if (surrogate.Count < 0)
                {
                    throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A slots read batch has invalid Count {surrogate.Count}."));
                }

                if (surrogate.Count == 0 && !bytes.IsEmpty)
                {
                    throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"An empty slots read batch carried {bytes.Length} unexpected bytes."));
                }

                kept = true;
                return new RemoteReplicaSlotsRead.Batch(surrogate.Count, bytes);
            }

            return surrogate.Case switch
            {
                ReplicaReplyTag.Dropped => ReplicaWriteOutcome.Dropped.Instance,
                ReplicaReplyTag.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
                ReplicaReplyTag.NotThisNode => NotThisNode.Instance,
                ReplicaReplyTag.Refused => ReplicaReplyTag.Refusal(surrogate.Refusal, surrogate.Message),
                ReplicaReplyTag.Corrupt => ReplicaReplyTag.Corruption(surrogate.Slot, surrogate.Message),
                _ => throw ReplicaReplyTag.Unknown(Answer, surrogate.Case, 1, 3, 4, 5, 6, 7),
            };
        }
        finally
        {
            if (!kept)
            {
                bytes.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public RemoteReplicaSlotsReadSurrogate ConvertToSurrogate(in RemoteReplicaSlotsRead value) => value switch
    {
        RemoteReplicaSlotsRead.Batch batch => Serialized(batch),
        ReplicaWriteOutcome.Dropped => new() { Case = ReplicaReplyTag.Dropped },
        ReplicaWriteOutcome.Backpressure => new() { Case = ReplicaReplyTag.Backpressure },
        NotThisNode => new() { Case = ReplicaReplyTag.NotThisNode },
        ReplicaCallRefused refused => new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        ReplicaCorrupt corrupt => new() { Case = ReplicaReplyTag.Corrupt, Slot = corrupt.Slot, Message = corrupt.Message },
        null => throw new UnreachableException(),
    };

    private static RemoteReplicaSlotsReadSurrogate Serialized(RemoteReplicaSlotsRead.Batch batch)
    {
        batch.Admission?.ReleaseAdmission();
        return new() { Case = ReplicaReplyTag.First, Count = batch.Count, Bytes = batch.Bytes ?? WireArcBuffer.Empty };
    }
}

/// <summary>A <see cref="RemoteReplicaTailRead"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct RemoteReplicaTailReadSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="ReplicaReplyTag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a tail's contiguous tail; 0 for the other cases.</summary>
    [Id(1)]
    public long ContiguousTail { get; init; }

    /// <summary>Gets or initializes whether a tail's replica is sealed; false for the other cases.</summary>
    [Id(2)]
    public bool IsSealed { get; init; }

    /// <summary>Gets or initializes a refusal's kind; 0 for the other cases.</summary>
    [Id(3)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's or corruption's message.</summary>
    [Id(4)]
    public string? Message { get; init; }

    /// <summary>Gets or initializes a corruption's slot, when the store named one.</summary>
    [Id(5)]
    public long? Slot { get; init; }

    /// <summary>Gets or initializes a sealed tail's remaining read-lease duration in ticks; 0 for the other cases.</summary>
    [Id(6)]
    public long LeaseRemainingTicks { get; init; }
}

/// <summary>
/// Converts a <see cref="RemoteReplicaTailRead"/> to its surrogate and back, refusing an unknown tag and a case which carries
/// a field it does not own.
/// </summary>
[RegisterConverter]
internal sealed class RemoteReplicaTailReadConverter : IConverter<RemoteReplicaTailRead, RemoteReplicaTailReadSurrogate>
{
    private const string Answer = "remote replica tail read";

    /// <inheritdoc/>
    /// <exception cref="ReplicaReplyDecodingException">The tag names no case, or its case lacks a field or carries one it does not own.</exception>
    public RemoteReplicaTailRead ConvertFromSurrogate(in RemoteReplicaTailReadSurrogate surrogate)
    {
        var unowned = surrogate.Case != ReplicaReplyTag.First && surrogate.ContiguousTail != 0 ? "ContiguousTail"
            : surrogate.Case != ReplicaReplyTag.First && surrogate.IsSealed ? "IsSealed"
            : surrogate.Case != ReplicaReplyTag.First && surrogate.LeaseRemainingTicks != 0 ? "LeaseRemainingTicks"
            : ReplicaReplyTag.UnownedShared(surrogate.Case, surrogate.Refusal, surrogate.Message, surrogate.Slot);
        ReplicaReplyTag.ThrowIfCarries(Answer, surrogate.Case, unowned);
        return surrogate.Case switch
        {
            ReplicaReplyTag.First when surrogate.LeaseRemainingTicks < 0 => throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A tail's lease remaining ticks cannot be negative: {surrogate.LeaseRemainingTicks}.")),
            ReplicaReplyTag.First => new RemoteReplicaTailRead.Tail(surrogate.ContiguousTail, surrogate.IsSealed, surrogate.LeaseRemainingTicks),
            ReplicaReplyTag.Dropped => ReplicaWriteOutcome.Dropped.Instance,
            ReplicaReplyTag.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
            ReplicaReplyTag.NotThisNode => NotThisNode.Instance,
            ReplicaReplyTag.Refused => ReplicaReplyTag.Refusal(surrogate.Refusal, surrogate.Message),
            ReplicaReplyTag.Corrupt => ReplicaReplyTag.Corruption(surrogate.Slot, surrogate.Message),
            _ => throw ReplicaReplyTag.Unknown(Answer, surrogate.Case, 1, 3, 4, 5, 6, 7),
        };
    }

    /// <inheritdoc/>
    public RemoteReplicaTailReadSurrogate ConvertToSurrogate(in RemoteReplicaTailRead value) => value switch
    {
        RemoteReplicaTailRead.Tail tail => new() { Case = ReplicaReplyTag.First, ContiguousTail = tail.ContiguousTail, IsSealed = tail.IsSealed, LeaseRemainingTicks = tail.LeaseRemainingTicks },
        ReplicaWriteOutcome.Dropped => new() { Case = ReplicaReplyTag.Dropped },
        ReplicaWriteOutcome.Backpressure => new() { Case = ReplicaReplyTag.Backpressure },
        NotThisNode => new() { Case = ReplicaReplyTag.NotThisNode },
        ReplicaCallRefused refused => new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        ReplicaCorrupt corrupt => new() { Case = ReplicaReplyTag.Corrupt, Slot = corrupt.Slot, Message = corrupt.Message },
        null => throw new UnreachableException(),
    };
}

/// <summary>A <see cref="RemoteReplicaHeartbeatRead"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct RemoteReplicaHeartbeatReadSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="ReplicaReplyTag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a reply's lease grant in ticks; 0 for the other cases.</summary>
    [Id(1)]
    public long LeaseGrantTicks { get; init; }

    /// <summary>Gets or initializes a reply's per-ledger status bytes; null for the other cases.</summary>
    [Id(2)]
    public byte[]? Outcomes { get; init; }

    /// <summary>Gets or initializes a refusal's kind; 0 for the other cases.</summary>
    [Id(3)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's message.</summary>
    [Id(4)]
    public string? Message { get; init; }
}

/// <summary>
/// Converts a <see cref="RemoteReplicaHeartbeatRead"/> to its surrogate and back, refusing an unknown tag, an unknown
/// heartbeat status byte, and a case which carries a field it does not own.
/// </summary>
[RegisterConverter]
internal sealed class RemoteReplicaHeartbeatReadConverter : IConverter<RemoteReplicaHeartbeatRead, RemoteReplicaHeartbeatReadSurrogate>
{
    private const string Answer = "remote replica heartbeat read";

    /// <inheritdoc/>
    /// <exception cref="ReplicaReplyDecodingException">The tag names no case, or its case lacks a field or carries one it does not own.</exception>
    public RemoteReplicaHeartbeatRead ConvertFromSurrogate(in RemoteReplicaHeartbeatReadSurrogate surrogate)
    {
        var unowned = surrogate.Case != ReplicaReplyTag.First && surrogate.LeaseGrantTicks != 0 ? "LeaseGrantTicks"
            : surrogate.Case != ReplicaReplyTag.First && surrogate.Outcomes is not null ? "Outcomes"
            : ReplicaReplyTag.Unowned(surrogate.Refusal, surrogate.Message, slot: null, ownsRefusal: surrogate.Case == ReplicaReplyTag.Refused, ownsMessage: surrogate.Case == ReplicaReplyTag.Refused, ownsSlot: false);
        ReplicaReplyTag.ThrowIfCarries(Answer, surrogate.Case, unowned);
        return surrogate.Case switch
        {
            ReplicaReplyTag.First => DecodeReply(surrogate.LeaseGrantTicks, surrogate.Outcomes),
            ReplicaReplyTag.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
            ReplicaReplyTag.NotThisNode => NotThisNode.Instance,
            ReplicaReplyTag.Refused => ReplicaReplyTag.Refusal(surrogate.Refusal, surrogate.Message),
            _ => throw ReplicaReplyTag.Unknown(Answer, surrogate.Case, 1, 4, 5, 6),
        };
    }

    private static RemoteReplicaHeartbeatRead.Reply DecodeReply(long leaseGrantTicks, byte[]? outcomes)
    {
        if (leaseGrantTicks < 0)
        {
            throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A heartbeat reply's lease grant ticks cannot be negative: {leaseGrantTicks}."));
        }

        if (outcomes is null)
        {
            throw new ReplicaReplyDecodingException("A heartbeat reply carries no outcomes.");
        }

        foreach (var raw in outcomes)
        {
            if (!Enum.IsDefined((ReplicaHeartbeatStatus)raw))
            {
                throw new ReplicaReplyDecodingException(string.Create(CultureInfo.InvariantCulture, $"A heartbeat reply carries unknown status {raw}."));
            }
        }

        return new RemoteReplicaHeartbeatRead.Reply(leaseGrantTicks, outcomes);
    }

    /// <inheritdoc/>
    public RemoteReplicaHeartbeatReadSurrogate ConvertToSurrogate(in RemoteReplicaHeartbeatRead value) => value switch
    {
        RemoteReplicaHeartbeatRead.Reply reply => new() { Case = ReplicaReplyTag.First, LeaseGrantTicks = reply.LeaseGrantTicks, Outcomes = reply.Outcomes },
        ReplicaWriteOutcome.Backpressure => new() { Case = ReplicaReplyTag.Backpressure },
        NotThisNode => new() { Case = ReplicaReplyTag.NotThisNode },
        ReplicaCallRefused refused => new() { Case = ReplicaReplyTag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        null => throw new UnreachableException(),
    };
}
