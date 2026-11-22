using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Serialization;

namespace Orleans.Durable.Storage.Fuzz.Orleans;

/// <summary>
/// The journal node grain's three answers (orleans.md §7.2), each decoded from its surrogate as a grain reply carries it:
/// Orleans' codec reads the input into the surrogate, a framing it owns, so an input it refuses lies outside the target's
/// domain; the composition's converter then either refuses the surrogate with <see cref="InvalidDataException"/>,
/// releasing the bytes it carried, or accepts a value whose encoding is that same surrogate, since each value has exactly
/// one accepted encoding. The seeds are every case's encoding and encodings the converter refuses: the wire of a surrogate
/// is the wire of the answer it stands for.
/// </summary>
/// <typeparam name="TAnswer">The answer.</typeparam>
/// <typeparam name="TSurrogate">Its surrogate.</typeparam>
internal abstract class JournalAnswerTarget<TAnswer, TSurrogate> : FuzzTarget
    where TAnswer : struct
    where TSurrogate : struct
{
    private static readonly Lazy<ServiceProvider> Services = new(static () => new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(IJournalStoreGrain).Assembly))
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
        TAnswer value;
        try
        {
            value = FromSurrogate(surrogate);
        }
        catch (InvalidDataException)
        {
            Require(carried.IsEmpty || IsReleased(carried), "refused a surrogate without releasing the bytes it carried.");
            return FuzzOutcome.Rejected;
        }

        try
        {
            Require(Same(surrogate, ToSurrogate(value)), "accepted a surrogate which is not its value's one encoding.");
        }
        finally
        {
            Release(value);
        }

        return FuzzOutcome.Accepted;
    }

    /// <summary>Returns a value of every case.</summary>
    protected abstract IEnumerable<TAnswer> Cases();

    /// <summary>Returns surrogates the converter refuses.</summary>
    protected abstract IEnumerable<TSurrogate> Refused();

    protected abstract TAnswer FromSurrogate(in TSurrogate surrogate);

    protected abstract TSurrogate ToSurrogate(in TAnswer value);

    /// <summary>Returns the bytes a surrogate carries, which its decoding owns.</summary>
    protected abstract OrleansBuffer Bytes(in TSurrogate surrogate);

    /// <summary>Returns whether two surrogates are one encoding, their bytes compared by content.</summary>
    protected abstract bool Same(in TSurrogate decoded, in TSurrogate encoded);

    /// <summary>Releases the bytes an accepted value holds.</summary>
    protected abstract void Release(in TAnswer value);

    private static bool IsReleased(OrleansBuffer buffer)
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

/// <summary>The forwarded write's outcome (<c>orleans-journal-write</c>).</summary>
internal sealed class JournalWriteAnswerTarget : JournalAnswerTarget<JournalForwardedWriteOutcome, JournalForwardedWriteOutcomeSurrogate>
{
    private static readonly JournalForwardedWriteOutcomeConverter Converter = new();

    public override string Name => "orleans-journal-write";

    protected override IEnumerable<JournalForwardedWriteOutcome> Cases() =>
    [
        new JournalWriteOutcome.Accepted(JournalSeeds.Version, ReplaceRequested: true),
        new JournalWriteOutcome.Rejected(JournalSeeds.Version),
        JournalWriteOutcome.ReplaceRequired.Instance,
        new JournalMoved(JournalSeeds.Owner),
        new JournalMoved(Owner: null),
        JournalWriteOutcome.Fenced.Instance,
        JournalWriteOutcome.Ambiguous.Instance,
        LocalJournalWriteOutcome.Throttled.Instance,
        NotThisNode.Instance,
        new JournalCallRefused(JournalRefusal.Invalid, "a record which fails its CRC"),
    ];

    protected override IEnumerable<JournalForwardedWriteOutcomeSurrogate> Refused() =>
    [
        new() { Case = 0 },
        new() { Case = JournalForwardedWriteOutcomeConverter.Tag.Accepted, Version = "not a version" },
        new() { Case = JournalForwardedWriteOutcomeConverter.Tag.Moved, Owner = Guid.Empty },
        new() { Case = JournalForwardedWriteOutcomeConverter.Tag.Fenced, ReplaceRequested = true },
        new() { Case = JournalForwardedWriteOutcomeConverter.Tag.Refused, Refusal = 3, Message = "m" },
    ];

    protected override JournalForwardedWriteOutcome FromSurrogate(in JournalForwardedWriteOutcomeSurrogate surrogate) => Converter.ConvertFromSurrogate(surrogate);

    protected override JournalForwardedWriteOutcomeSurrogate ToSurrogate(in JournalForwardedWriteOutcome value) => Converter.ConvertToSurrogate(value);

    protected override OrleansBuffer Bytes(in JournalForwardedWriteOutcomeSurrogate surrogate) => default;

    protected override bool Same(in JournalForwardedWriteOutcomeSurrogate decoded, in JournalForwardedWriteOutcomeSurrogate encoded) =>
        (decoded.Case, decoded.Version, decoded.ReplaceRequested, decoded.Owner, decoded.Refusal, decoded.Message)
            == (encoded.Case, encoded.Version, encoded.ReplaceRequested, encoded.Owner, encoded.Refusal, encoded.Message);

    protected override void Release(in JournalForwardedWriteOutcome value)
    {
    }
}

/// <summary>The forwarded read's answer (<c>orleans-journal-read</c>).</summary>
internal sealed class JournalReadAnswerTarget : JournalAnswerTarget<JournalForwardedRead, JournalForwardedReadSurrogate>
{
    private static readonly JournalForwardedReadConverter Converter = new();

    public override string Name => "orleans-journal-read";

    protected override IEnumerable<JournalForwardedRead> Cases() =>
    [
        new JournalForwardedRead.Found(JournalSeeds.Version, Exists: true, ContentLength: 3, ReplaceRequested: true, JournalSeeds.Bytes(1, 2, 3), Snapshot: null),
        new JournalForwardedRead.Found(JournalVersion.Origin, Exists: false, ContentLength: 0, ReplaceRequested: false, default, Snapshot: null),
        new JournalMoved(JournalSeeds.Owner),
        JournalForwardedRead.Busy.Instance,
        new JournalCallRefused(JournalRefusal.Argument, "no journal key"),
        NotThisNode.Instance,
    ];

    protected override IEnumerable<JournalForwardedReadSurrogate> Refused() =>
    [
        new() { Case = 0 },
        new() { Case = JournalForwardedReadConverter.Tag.Found, Version = JournalSeeds.Version.ToString(), Exists = true, ContentLength = 3, FirstPage = JournalSeeds.Bytes(1, 2) },
        new() { Case = JournalForwardedReadConverter.Tag.Found, Version = JournalSeeds.Version.ToString(), Exists = false, ContentLength = 1, FirstPage = JournalSeeds.Bytes(1) },
        new() { Case = JournalForwardedReadConverter.Tag.Moved, FirstPage = JournalSeeds.Bytes(1) },
        new() { Case = JournalForwardedReadConverter.Tag.Busy, Owner = JournalSeeds.Owner.Value },
    ];

    protected override JournalForwardedRead FromSurrogate(in JournalForwardedReadSurrogate surrogate) => Converter.ConvertFromSurrogate(surrogate);

    protected override JournalForwardedReadSurrogate ToSurrogate(in JournalForwardedRead value) => Converter.ConvertToSurrogate(value);

    protected override OrleansBuffer Bytes(in JournalForwardedReadSurrogate surrogate) => surrogate.FirstPage;

    protected override bool Same(in JournalForwardedReadSurrogate decoded, in JournalForwardedReadSurrogate encoded) =>
        (decoded.Case, decoded.Version, decoded.Exists, decoded.ContentLength, decoded.ReplaceRequested, decoded.Snapshot, decoded.Owner, decoded.Refusal, decoded.Message)
            == (encoded.Case, encoded.Version, encoded.Exists, encoded.ContentLength, encoded.ReplaceRequested, encoded.Snapshot, encoded.Owner, encoded.Refusal, encoded.Message)
        && decoded.FirstPage.ToArray().AsSpan().SequenceEqual(encoded.FirstPage.ToArray());

    protected override void Release(in JournalForwardedRead value) => (value.Value as JournalForwardedRead.Found)?.FirstPage.Dispose();
}

/// <summary>A forwarded read's page (<c>orleans-journal-page</c>).</summary>
internal sealed class JournalPageAnswerTarget : JournalAnswerTarget<JournalForwardedPage, JournalForwardedPageSurrogate>
{
    private static readonly JournalForwardedPageConverter Converter = new();

    public override string Name => "orleans-journal-page";

    protected override IEnumerable<JournalForwardedPage> Cases() =>
    [
        new JournalForwardedPage.Page(JournalSeeds.Bytes(4, 5)),
        JournalForwardedPage.Expired.Instance,
        new JournalCallRefused(JournalRefusal.Argument, "an offset outside the snapshot"),
        NotThisNode.Instance,
    ];

    protected override IEnumerable<JournalForwardedPageSurrogate> Refused() =>
    [
        new() { Case = 0 },
        new() { Case = JournalForwardedPageConverter.Tag.Page },
        new() { Case = JournalForwardedPageConverter.Tag.Expired, Bytes = JournalSeeds.Bytes(1) },
        new() { Case = JournalForwardedPageConverter.Tag.Refused, Refusal = (byte)JournalRefusal.Invalid, Message = "m" },
    ];

    protected override JournalForwardedPage FromSurrogate(in JournalForwardedPageSurrogate surrogate) => Converter.ConvertFromSurrogate(surrogate);

    protected override JournalForwardedPageSurrogate ToSurrogate(in JournalForwardedPage value) => Converter.ConvertToSurrogate(value);

    protected override OrleansBuffer Bytes(in JournalForwardedPageSurrogate surrogate) => surrogate.Bytes;

    protected override bool Same(in JournalForwardedPageSurrogate decoded, in JournalForwardedPageSurrogate encoded) =>
        (decoded.Case, decoded.Refusal, decoded.Message) == (encoded.Case, encoded.Refusal, encoded.Message)
        && decoded.Bytes.ToArray().AsSpan().SequenceEqual(encoded.Bytes.ToArray());

    protected override void Release(in JournalForwardedPage value) => (value.Value as JournalForwardedPage.Page)?.Bytes.Dispose();
}

/// <summary>The values the journal answers' seeds carry.</summary>
internal static class JournalSeeds
{
    public static readonly JournalVersion Version = JournalVersion.Parse("BAEAAAQA");

    public static readonly NodeId Owner = new(new Guid("6a0e1c2d-3b4f-4a5b-8c6d-7e8f9a0b1c2d"));

    /// <summary>Returns a new Orleans buffer of <paramref name="bytes"/>.</summary>
    public static OrleansBuffer Bytes(params ReadOnlySpan<byte> bytes)
    {
        using var writer = new OrleansBufferWriter();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }
}
