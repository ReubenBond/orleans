using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>
/// The decoding rules the forwarded journal answers' surrogates share (orleans.md §7.2, conventions.md §15). Each value has
/// exactly one accepted encoding: a version crosses as its canonical text (journals.md §7), a <c>Moved</c>'s owner as its
/// node's UUID, absent when it names none, and every field a case does not carry at its default. An unknown tag, a case
/// without a field it carries or with one it does not, a version which is not a version's canonical text, an owner of
/// <see cref="Guid.Empty"/>, and an unknown refusal kind are refused with <see cref="InvalidDataException"/>.
/// </summary>
internal static class JournalForwardingWire
{
    /// <summary>Parses a version's canonical text, refusing a missing or malformed one.</summary>
    public static JournalVersion Version(string? text, string answer)
    {
        if (text is null || !JournalVersion.TryParse(text, out var version))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A {answer} carries {(text is null ? "no version" : $"'{text}', which is no version's canonical text")}."));
        }

        return version;
    }

    /// <summary>Returns the <c>Moved</c> a surrogate carries: its owner's node, or none; an owner of <see cref="Guid.Empty"/> is refused.</summary>
    public static JournalMoved Moved(Guid? owner, string answer)
    {
        if (owner == Guid.Empty)
        {
            throw new InvalidDataException($"A {answer}'s Moved names no owner by an absent UUID, not the empty one.");
        }

        return new JournalMoved(owner is { } node ? new NodeId(node) : null);
    }

    /// <summary>Returns the refusal a surrogate carries, refusing an unknown kind, a kind <paramref name="kinds"/> excludes, or no message.</summary>
    public static JournalCallRefused Refused(byte kind, string? message, string answer, params ReadOnlySpan<JournalRefusal> kinds)
    {
        if (!kinds.Contains((JournalRefusal)kind))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A {answer}'s refusal kind is one of {string.Join(", ", kinds.ToArray())}, not {kind}."));
        }

        if (message is null)
        {
            throw new InvalidDataException($"A {answer}'s refusal carries no message.");
        }

        return new JournalCallRefused((JournalRefusal)kind, message);
    }

    /// <summary>Refuses a field which the case <paramref name="tag"/> does not carry, set away from its default.</summary>
    public static void Unset(bool set, string field, byte tag, string answer)
    {
        if (set)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A {answer}'s case {tag} carries a {field}, which it has none of."));
        }
    }

    /// <summary>Returns the exception for an unknown tag of the answer <paramref name="answer"/>, which takes <paramref name="tags"/>.</summary>
    public static InvalidDataException Unknown(string answer, byte tag, ReadOnlySpan<byte> tags) =>
        new(string.Create(CultureInfo.InvariantCulture, $"A {answer}'s case tag is one of {string.Join(", ", tags.ToArray())}, not {tag}."));
}

/// <summary>A <see cref="JournalForwardedWriteOutcome"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct JournalForwardedWriteOutcomeSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="JournalForwardedWriteOutcomeConverter.Tag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes an <c>Accepted</c>'s version or a <c>Rejected</c>'s current version, as canonical text.</summary>
    [Id(1)]
    public string? Version { get; init; }

    /// <summary>Gets or initializes an <c>Accepted</c>'s replace request.</summary>
    [Id(2)]
    public bool ReplaceRequested { get; init; }

    /// <summary>Gets or initializes a <c>Moved</c>'s owner, absent when it names none.</summary>
    [Id(3)]
    public Guid? Owner { get; init; }

    /// <summary>Gets or initializes a refusal's kind (<see cref="JournalRefusal"/>); zero for the other cases.</summary>
    [Id(4)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's message.</summary>
    [Id(5)]
    public string? Message { get; init; }
}

/// <summary>Converts a <see cref="JournalForwardedWriteOutcome"/> to its surrogate and back, accepting only canonical encodings.</summary>
[RegisterConverter]
internal sealed class JournalForwardedWriteOutcomeConverter : IConverter<JournalForwardedWriteOutcome, JournalForwardedWriteOutcomeSurrogate>
{
    private const string Answer = "forwarded journal write outcome";

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, or its case lacks a field or carries one it has none of.</exception>
    public JournalForwardedWriteOutcome ConvertFromSurrogate(in JournalForwardedWriteOutcomeSurrogate surrogate)
    {
        var tag = surrogate.Case;
        JournalForwardingWire.Unset(surrogate.Version is not null && tag is not (Tag.Accepted or Tag.Rejected), "version", tag, Answer);
        JournalForwardingWire.Unset(surrogate.ReplaceRequested && tag != Tag.Accepted, "replace request", tag, Answer);
        JournalForwardingWire.Unset(surrogate.Owner is not null && tag != Tag.Moved, "owner", tag, Answer);
        JournalForwardingWire.Unset((surrogate.Refusal != 0 || surrogate.Message is not null) && tag != Tag.Refused, "refusal", tag, Answer);
        return tag switch
        {
            Tag.Accepted => new JournalWriteOutcome.Accepted(JournalForwardingWire.Version(surrogate.Version, Answer), surrogate.ReplaceRequested),
            Tag.Rejected => new JournalWriteOutcome.Rejected(JournalForwardingWire.Version(surrogate.Version, Answer)),
            Tag.ReplaceRequired => JournalWriteOutcome.ReplaceRequired.Instance,
            Tag.Moved => JournalForwardingWire.Moved(surrogate.Owner, Answer),
            Tag.Fenced => JournalWriteOutcome.Fenced.Instance,
            Tag.Ambiguous => JournalWriteOutcome.Ambiguous.Instance,
            Tag.Throttled => LocalJournalWriteOutcome.Throttled.Instance,
            Tag.NotThisNode => NotThisNode.Instance,
            Tag.Refused => JournalForwardingWire.Refused(surrogate.Refusal, surrogate.Message, Answer, JournalRefusal.Argument, JournalRefusal.Invalid),
            Tag.Unavailable => LocalJournalWriteOutcome.Unavailable.Instance,
            _ => throw JournalForwardingWire.Unknown(Answer, tag, Tag.All),
        };
    }

    /// <inheritdoc/>
    public JournalForwardedWriteOutcomeSurrogate ConvertToSurrogate(in JournalForwardedWriteOutcome value) => value switch
    {
        JournalWriteOutcome.Accepted accepted => new() { Case = Tag.Accepted, Version = accepted.Version.ToString(), ReplaceRequested = accepted.ReplaceRequested },
        JournalWriteOutcome.Rejected rejected => new() { Case = Tag.Rejected, Version = rejected.CurrentVersion.ToString() },
        JournalWriteOutcome.ReplaceRequired => new() { Case = Tag.ReplaceRequired },
        JournalMoved moved => new() { Case = Tag.Moved, Owner = moved.Owner?.Value },
        JournalWriteOutcome.Fenced => new() { Case = Tag.Fenced },
        JournalWriteOutcome.Ambiguous => new() { Case = Tag.Ambiguous },
        LocalJournalWriteOutcome.Throttled => new() { Case = Tag.Throttled },
        NotThisNode => new() { Case = Tag.NotThisNode },
        JournalCallRefused refused => new() { Case = Tag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        LocalJournalWriteOutcome.Unavailable => new() { Case = Tag.Unavailable },
        null => throw new UnreachableException(),
    };

    /// <summary>The write outcome's case tags (orleans.md §7.2).</summary>
    internal static class Tag
    {
        /// <summary><c>Accepted</c>, with the version and the replace request.</summary>
        public const byte Accepted = 1;

        /// <summary><c>Rejected</c>, with the current version.</summary>
        public const byte Rejected = 2;

        /// <summary><c>ReplaceRequired</c>.</summary>
        public const byte ReplaceRequired = 3;

        /// <summary><c>Moved</c>, with its owner.</summary>
        public const byte Moved = 4;

        /// <summary><c>Fenced</c>.</summary>
        public const byte Fenced = 5;

        /// <summary><c>Ambiguous</c>.</summary>
        public const byte Ambiguous = 6;

        /// <summary><c>Throttled</c>.</summary>
        public const byte Throttled = 7;

        /// <summary><c>NotThisNode</c>.</summary>
        public const byte NotThisNode = 8;

        /// <summary><c>Refused</c>, with its kind and message.</summary>
        public const byte Refused = 9;

        /// <summary><c>Unavailable</c>.</summary>
        public const byte Unavailable = 10;

        /// <summary>Gets every tag.</summary>
        public static ReadOnlySpan<byte> All => [Accepted, Rejected, ReplaceRequired, Moved, Fenced, Ambiguous, Throttled, NotThisNode, Refused, Unavailable];
    }
}

/// <summary>A <see cref="JournalForwardedRead"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct JournalForwardedReadSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="JournalForwardedReadConverter.Tag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a <c>Found</c>'s version, as canonical text.</summary>
    [Id(1)]
    public string? Version { get; init; }

    /// <summary>Gets or initializes whether a <c>Found</c>'s journal exists.</summary>
    [Id(2)]
    public bool Exists { get; init; }

    /// <summary>Gets or initializes a <c>Found</c>'s content length.</summary>
    [Id(3)]
    public long ContentLength { get; init; }

    /// <summary>Gets or initializes a <c>Found</c>'s replace request.</summary>
    [Id(4)]
    public bool ReplaceRequested { get; init; }

    /// <summary>Gets or initializes a <c>Found</c>'s first page; empty for the other cases.</summary>
    [Id(5)]
    public OrleansBuffer FirstPage { get; init; }

    /// <summary>Gets or initializes a <c>Found</c>'s pinned snapshot, when more pages follow.</summary>
    [Id(6)]
    public Guid? Snapshot { get; init; }

    /// <summary>Gets or initializes a <c>Moved</c>'s owner, absent when it names none.</summary>
    [Id(7)]
    public Guid? Owner { get; init; }

    /// <summary>Gets or initializes a refusal's kind (<see cref="JournalRefusal"/>); zero for the other cases.</summary>
    [Id(8)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's message.</summary>
    [Id(9)]
    public string? Message { get; init; }
}

/// <summary>
/// Converts a <see cref="JournalForwardedRead"/> to its surrogate and back, accepting only canonical encodings. A
/// <c>Found</c>'s first page passes to the caller with its answer (OR-I18); any other case carries no bytes, so the
/// converter releases what it decoded for one, and refuses the case when that is not empty. A <c>Found</c> is refused when
/// its length is negative, when its first page is not its content's first page, when it names a snapshot though its
/// content fits one page or none though more pages follow, and when its journal does not exist but has content
/// (journals.md §2.3).
/// </summary>
[RegisterConverter]
internal sealed class JournalForwardedReadConverter : IConverter<JournalForwardedRead, JournalForwardedReadSurrogate>
{
    private const string Answer = "forwarded journal read";

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, its case lacks a field, carries one it has none of, or breaks a bound.</exception>
    public JournalForwardedRead ConvertFromSurrogate(in JournalForwardedReadSurrogate surrogate)
    {
        var page = surrogate.FirstPage;
        try
        {
            var tag = surrogate.Case;
            JournalForwardingWire.Unset(surrogate.Owner is not null && tag != Tag.Moved, "owner", tag, Answer);
            JournalForwardingWire.Unset((surrogate.Refusal != 0 || surrogate.Message is not null) && tag != Tag.Refused, "refusal", tag, Answer);
            if (tag == Tag.Found)
            {
                var found = Found(surrogate, page);
                page = default;
                return found;
            }

            JournalForwardingWire.Unset(page.Length > 0, "first page", tag, Answer);
            JournalForwardingWire.Unset(
                surrogate.Version is not null || surrogate.Exists || surrogate.ContentLength != 0 || surrogate.ReplaceRequested || surrogate.Snapshot is not null,
                "read's field",
                tag,
                Answer);
            return tag switch
            {
                Tag.Moved => JournalForwardingWire.Moved(surrogate.Owner, Answer),
                Tag.NotThisNode => NotThisNode.Instance,
                Tag.Busy => JournalForwardedRead.Busy.Instance,
                Tag.Refused => JournalForwardingWire.Refused(surrogate.Refusal, surrogate.Message, Answer, JournalRefusal.Argument),
                _ => throw JournalForwardingWire.Unknown(Answer, tag, Tag.All),
            };
        }
        finally
        {
            page.Dispose();
        }
    }

    /// <inheritdoc/>
    public JournalForwardedReadSurrogate ConvertToSurrogate(in JournalForwardedRead value) => value switch
    {
        JournalForwardedRead.Found found => new()
        {
            Case = Tag.Found,
            Version = found.Version.ToString(),
            Exists = found.Exists,
            ContentLength = found.ContentLength,
            ReplaceRequested = found.ReplaceRequested,
            FirstPage = found.FirstPage,
            Snapshot = found.Snapshot,
        },
        JournalMoved moved => new() { Case = Tag.Moved, Owner = moved.Owner?.Value },
        NotThisNode => new() { Case = Tag.NotThisNode },
        JournalForwardedRead.Busy => new() { Case = Tag.Busy },
        JournalCallRefused refused => new() { Case = Tag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        null => throw new UnreachableException(),
    };

    private static JournalForwardedRead.Found Found(in JournalForwardedReadSurrogate surrogate, OrleansBuffer page)
    {
        var version = JournalForwardingWire.Version(surrogate.Version, Answer);
        var length = surrogate.ContentLength;
        if (length < 0)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A {Answer}'s Found has a negative content length, {length}."));
        }

        if (!surrogate.Exists && length > 0)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A {Answer}'s Found of a journal which does not exist has {length} bytes of content."));
        }

        if (page.Length != Math.Min(length, JournalForwardedRead.PageBytes))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A {Answer}'s Found of {length} bytes carries a first page of {page.Length} bytes, not min({length}, {JournalForwardedRead.PageBytes})."));
        }

        var morePages = length > JournalForwardedRead.PageBytes;
        if (morePages != surrogate.Snapshot.HasValue)
        {
            throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"A {Answer}'s Found of {length} bytes {(morePages ? "names no snapshot although more pages follow" : "names a snapshot although its content fits one page")}."));
        }

        return new JournalForwardedRead.Found(version, surrogate.Exists, length, surrogate.ReplaceRequested, page, surrogate.Snapshot);
    }

    /// <summary>The read's case tags (orleans.md §7.2).</summary>
    internal static class Tag
    {
        /// <summary><c>Found</c>, with the read's fields and its first page.</summary>
        public const byte Found = 1;

        /// <summary><c>Moved</c>, with its owner.</summary>
        public const byte Moved = 2;

        /// <summary><c>NotThisNode</c>.</summary>
        public const byte NotThisNode = 3;

        /// <summary><c>Busy</c>.</summary>
        public const byte Busy = 4;

        /// <summary><c>Refused</c>, with its kind and message.</summary>
        public const byte Refused = 5;

        /// <summary>Gets every tag.</summary>
        public static ReadOnlySpan<byte> All => [Found, Moved, NotThisNode, Busy, Refused];
    }
}

/// <summary>A <see cref="JournalForwardedPage"/> as a grain reply carries it: its case's tag and fields.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct JournalForwardedPageSurrogate
{
    /// <summary>Gets or initializes the case's tag (<see cref="JournalForwardedPageConverter.Tag"/>).</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a page's bytes; empty for the other cases.</summary>
    [Id(1)]
    public OrleansBuffer Bytes { get; init; }

    /// <summary>Gets or initializes a refusal's kind (<see cref="JournalRefusal"/>); zero for the other cases.</summary>
    [Id(2)]
    public byte Refusal { get; init; }

    /// <summary>Gets or initializes a refusal's message.</summary>
    [Id(3)]
    public string? Message { get; init; }
}

/// <summary>
/// Converts a <see cref="JournalForwardedPage"/> to its surrogate and back, accepting only canonical encodings. A page's
/// bytes pass to the caller with its answer (OR-I18); any other case carries none, so the converter releases what it decoded
/// for one, and refuses the case when that is not empty, and a page which is empty or longer than
/// <see cref="JournalForwardedRead.PageBytes"/>.
/// </summary>
[RegisterConverter]
internal sealed class JournalForwardedPageConverter : IConverter<JournalForwardedPage, JournalForwardedPageSurrogate>
{
    private const string Answer = "forwarded journal page";

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, a page is empty or too long, or a case carries a field it has none of.</exception>
    public JournalForwardedPage ConvertFromSurrogate(in JournalForwardedPageSurrogate surrogate)
    {
        var bytes = surrogate.Bytes;
        try
        {
            var tag = surrogate.Case;
            JournalForwardingWire.Unset((surrogate.Refusal != 0 || surrogate.Message is not null) && tag != Tag.Refused, "refusal", tag, Answer);
            if (tag == Tag.Page)
            {
                if (bytes.Length is 0 or > JournalForwardedRead.PageBytes)
                {
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A {Answer} of {bytes.Length} bytes is not between 1 and {JournalForwardedRead.PageBytes} bytes."));
                }

                var page = new JournalForwardedPage.Page(bytes);
                bytes = default;
                return page;
            }

            JournalForwardingWire.Unset(bytes.Length > 0, "page's bytes", tag, Answer);
            return tag switch
            {
                Tag.Expired => JournalForwardedPage.Expired.Instance,
                Tag.NotThisNode => NotThisNode.Instance,
                Tag.Refused => JournalForwardingWire.Refused(surrogate.Refusal, surrogate.Message, Answer, JournalRefusal.Argument),
                _ => throw JournalForwardingWire.Unknown(Answer, tag, Tag.All),
            };
        }
        finally
        {
            bytes.Dispose();
        }
    }

    /// <inheritdoc/>
    public JournalForwardedPageSurrogate ConvertToSurrogate(in JournalForwardedPage value) => value switch
    {
        JournalForwardedPage.Page page => new() { Case = Tag.Page, Bytes = page.Bytes },
        JournalForwardedPage.Expired => new() { Case = Tag.Expired },
        NotThisNode => new() { Case = Tag.NotThisNode },
        JournalCallRefused refused => new() { Case = Tag.Refused, Refusal = (byte)refused.Kind, Message = refused.Message },
        null => throw new UnreachableException(),
    };

    /// <summary>The page's case tags (orleans.md §7.2).</summary>
    internal static class Tag
    {
        /// <summary><c>Page</c>, with its bytes.</summary>
        public const byte Page = 1;

        /// <summary><c>Expired</c>.</summary>
        public const byte Expired = 2;

        /// <summary><c>NotThisNode</c>.</summary>
        public const byte NotThisNode = 3;

        /// <summary><c>Refused</c>, with its kind and message.</summary>
        public const byte Refused = 4;

        /// <summary>Gets every tag.</summary>
        public static ReadOnlySpan<byte> All => [Page, Expired, NotThisNode, Refused];
    }
}
