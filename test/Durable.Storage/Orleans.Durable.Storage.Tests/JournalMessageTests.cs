using System.Security.Cryptography;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Serialization;
using PageTag = Orleans.Durable.Storage.JournalForwardedPageConverter.Tag;
using ReadTag = Orleans.Durable.Storage.JournalForwardedReadConverter.Tag;
using WriteTag = Orleans.Durable.Storage.JournalForwardedWriteOutcomeConverter.Tag;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The journal node grain's messages (orleans.md §5.5, §7.2, §9, conventions.md §15): every case of the three forwarded
/// answers crosses through its surrogate, whose wire encoding of every case a snapshot pins, as does the forwarded entry;
/// every case and the entry cross unchanged, at the page's boundaries too; and each value has exactly one accepted
/// encoding, every other refused with <see cref="InvalidDataException"/> and the bytes it decoded released: an unknown case
/// tag, a missing or malformed version, a field a case does not carry, an owner of <see cref="Guid.Empty"/>, a case other
/// than a page or a <c>Found</c> carrying bytes, an empty page, and a <c>Found</c> whose first page or snapshot does not
/// match its length or whose absent journal has content.
/// </summary>
[NoPageLeakCheck("Grain messages carry Orleans buffers and no Clog pages (OR-I18).")]
public sealed class JournalMessageTests : IDisposable
{
    private const byte PageFill = 0xa5;

    private static readonly JournalVersion Version = JournalVersion.Parse("BAEAAAQA");

    private static readonly NodeId Owner = new(new Guid("6a0e1c2d-3b4f-4a5b-8c6d-7e8f9a0b1c2d"));

    private static readonly JournalForwardedWriteOutcome[] WriteCases =
    [
        new JournalWriteOutcome.Accepted(Version, ReplaceRequested: true),
        new JournalWriteOutcome.Accepted(JournalVersion.Origin, ReplaceRequested: false),
        new JournalWriteOutcome.Rejected(Version),
        JournalWriteOutcome.ReplaceRequired.Instance,
        new JournalMoved(Owner),
        new JournalMoved(Owner: null),
        JournalWriteOutcome.Fenced.Instance,
        JournalWriteOutcome.Ambiguous.Instance,
        LocalJournalWriteOutcome.Throttled.Instance,
        LocalJournalWriteOutcome.Unavailable.Instance,
        NotThisNode.Instance,
        new JournalCallRefused(JournalRefusal.Argument, "no journal key"),
        new JournalCallRefused(JournalRefusal.Invalid, "a record which fails its CRC"),
    ];

    private static readonly JournalForwardedRead[] ReadCases =
    [
        new JournalForwardedRead.Found(Version, Exists: true, ContentLength: 3, ReplaceRequested: false, WireBytes.Of(1, 2, 3), Snapshot: null),
        new JournalForwardedRead.Found(Version, Exists: true, ContentLength: 2, ReplaceRequested: true, WireBytes.Of(4, 5), Snapshot: null),
        new JournalForwardedRead.Found(JournalVersion.Origin, Exists: false, ContentLength: 0, ReplaceRequested: false, default, Snapshot: null),
        new JournalMoved(Owner),
        new JournalMoved(Owner: null),
        JournalForwardedRead.Busy.Instance,
        new JournalCallRefused(JournalRefusal.Argument, "no journal key"),
        NotThisNode.Instance,
    ];

    private static readonly JournalForwardedPage[] PageCases =
    [
        new JournalForwardedPage.Page(WireBytes.Of(4, 5)),
        JournalForwardedPage.Expired.Instance,
        new JournalCallRefused(JournalRefusal.Argument, "an offset outside the snapshot"),
        NotThisNode.Instance,
    ];

    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(IJournalStoreGrain).Assembly))
        .BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    /// <summary>
    /// Every case's wire encoding, and the entry's, is pinned; a <c>Found</c> of more than one page is pinned with its page's
    /// bytes elided, its length shown, and the whole wire's hash as a cross-check.
    /// </summary>
    [Fact]
    public Task EveryAnswerCaseAndTheEntryHaveAPinnedWireEncoding()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var entry = new JournalForwardedEntry { Key = "a journal key", Record = WireBytes.Arc(7, 8, 9) };
        var pinned = Pinned(FullPage());
        try
        {
            var wire = serializer.SerializeToArray(pinned);
            var page = wire.AsSpan().IndexOf(Enumerable.Repeat(PageFill, JournalForwardedRead.PageBytes).ToArray());
            Assert.True(page >= 0, "The pinned read's wire carries its first page whole.");
            return Verify(new
            {
                Writes = WriteCases.Select(outcome => new { Case = outcome.ToString(), Wire = Wire(serializer, outcome) }),
                Reads = ReadCases.Select(read => new { Case = read.ToString(), Wire = Wire(serializer, read) }),
                Pages = PageCases.Select(page => new { Case = page.ToString(), Wire = Wire(serializer, page) }),
                Entry = Wire(serializer, entry),
                Pinned = new
                {
                    Case = pinned.ToString(),
                    Before = Convert.ToHexStringLower(wire.AsSpan(0, page)),
                    Page = $"{JournalForwardedRead.PageBytes} bytes of {PageFill:x2}",
                    After = Convert.ToHexStringLower(wire.AsSpan(page + JournalForwardedRead.PageBytes)),
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(wire)),
                },
            });
        }
        finally
        {
            entry.Record.Dispose();
            ((JournalForwardedRead.Found)pinned.Value).FirstPage.Dispose();
        }
    }

    /// <summary>Every case and the entry cross unchanged, with a first page and a page of exactly one page's bytes among them.</summary>
    [Fact]
    public void EveryAnswerCaseAndTheEntryCrossUnchanged()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        foreach (var outcome in WriteCases)
        {
            Assert.Equal(outcome, serializer.Deserialize<JournalForwardedWriteOutcome>(serializer.SerializeToArray(outcome)));
        }

        var onePage = new JournalForwardedRead.Found(Version, Exists: true, ContentLength: JournalForwardedRead.PageBytes, ReplaceRequested: false, FullPage(), Snapshot: null);
        foreach (var read in ReadCases.Append(onePage).Append(Pinned(FullPage())))
        {
            var crossed = serializer.Deserialize<JournalForwardedRead>(serializer.SerializeToArray(read));
            Assert.Equal(read, crossed);
            (crossed.Value as JournalForwardedRead.Found)?.FirstPage.Dispose();
        }

        foreach (var page in PageCases.Append(new JournalForwardedPage.Page(FullPage())))
        {
            var crossed = serializer.Deserialize<JournalForwardedPage>(serializer.SerializeToArray(page));
            Assert.Equal(page, crossed);
            (crossed.Value as JournalForwardedPage.Page)?.Bytes.Dispose();
        }

        var sent = new JournalForwardedEntry { Key = "journal", Record = WireBytes.Arc(7, 8, 9) };
        var entry = serializer.Deserialize<JournalForwardedEntry>(serializer.SerializeToArray(sent));
        Assert.Equal("journal", entry.Key);
        Assert.Equal([7, 8, 9], entry.Record.ToArray());
        entry.Record.Dispose();
        sent.Record.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(255)]
    public void AnUnknownWriteCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedWriteOutcomeConverter().ConvertFromSurrogate(new JournalForwardedWriteOutcomeSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(255)]
    public void AnUnknownReadCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedReadConverter().ConvertFromSurrogate(new JournalForwardedReadSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(255)]
    public void AnUnknownPageCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedPageConverter().ConvertFromSurrogate(new JournalForwardedPageSurrogate { Case = tag }));

    /// <summary>An <c>Accepted</c>, a <c>Rejected</c>, and a <c>Found</c> carry a version's canonical text: none, or another text, is refused.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a version")]
    public void AMissingOrMalformedVersionIsRefused(string? version)
    {
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedWriteOutcomeConverter().ConvertFromSurrogate(new JournalForwardedWriteOutcomeSurrogate { Case = WriteTag.Accepted, Version = version }));
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedWriteOutcomeConverter().ConvertFromSurrogate(new JournalForwardedWriteOutcomeSurrogate { Case = WriteTag.Rejected, Version = version }));
        var page = WireBytes.Of(1);
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedReadConverter().ConvertFromSurrogate(new JournalForwardedReadSurrogate { Case = ReadTag.Found, Version = version, Exists = true, ContentLength = 1, FirstPage = page }));
        Assert.True(WireBytes.IsReleased(page));
    }

    /// <summary>
    /// A <c>Found</c>'s first page is its content's first page, and it names a snapshot exactly when more pages follow: one
    /// whose page is longer or shorter, or whose snapshot does not match, at the page's boundary too, is refused, its page
    /// released; so is one whose journal does not exist but has content (journals.md §2.3).
    /// </summary>
    [Theory]
    [InlineData(3, 2, false, true)]
    [InlineData(3, 4, false, true)]
    [InlineData(3, 3, true, true)]
    [InlineData(-1, 1, false, true)]
    [InlineData(JournalForwardedRead.PageBytes, JournalForwardedRead.PageBytes, true, true)]
    [InlineData(JournalForwardedRead.PageBytes + 1, JournalForwardedRead.PageBytes, false, true)]
    [InlineData(3, 3, false, false)]
    public void AFoundWhosePageOrSnapshotBreaksItsLengthIsRefused(long length, int page, bool snapshot, bool exists)
    {
        var bytes = WireBytes.Of(new byte[page]);
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedReadConverter().ConvertFromSurrogate(new JournalForwardedReadSurrogate
        {
            Case = ReadTag.Found,
            Version = Version.ToString(),
            Exists = exists,
            ContentLength = length,
            FirstPage = bytes,
            Snapshot = snapshot ? Guid.NewGuid() : null,
        }));
        Assert.True(WireBytes.IsReleased(bytes));
    }

    /// <summary>
    /// Only a <c>Found</c> and a page carry bytes, and a page at least one byte and at most one page of them: any other is
    /// refused, its bytes released.
    /// </summary>
    [Fact]
    public void ACaseWhichCarriesBytesItHasNoneOfIsRefusedAndReleasesThem()
    {
        var moved = WireBytes.Of(1);
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedReadConverter().ConvertFromSurrogate(new JournalForwardedReadSurrogate { Case = ReadTag.Moved, FirstPage = moved }));
        Assert.True(WireBytes.IsReleased(moved));

        var expired = WireBytes.Of(1);
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedPageConverter().ConvertFromSurrogate(new JournalForwardedPageSurrogate { Case = PageTag.Expired, Bytes = expired }));
        Assert.True(WireBytes.IsReleased(expired));

        var tooLong = WireBytes.Of(new byte[JournalForwardedRead.PageBytes + 1]);
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedPageConverter().ConvertFromSurrogate(new JournalForwardedPageSurrogate { Case = PageTag.Page, Bytes = tooLong }));
        Assert.True(WireBytes.IsReleased(tooLong));

        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedPageConverter().ConvertFromSurrogate(new JournalForwardedPageSurrogate { Case = PageTag.Page }));
    }

    /// <summary>
    /// orleans.md §7.2: a refusal carries a kind its union takes and a message. A write takes <c>Argument</c> and
    /// <c>Invalid</c>, a read and a page <c>Argument</c> alone; an unknown kind, a kind the union does not take, and a
    /// refusal with no message are refused. A wrong acceptance would encode back to itself, so these rows are the oracle.
    /// </summary>
    [Theory]
    [InlineData("write", 0, true)]
    [InlineData("write", 3, true)]
    [InlineData("write", 1, false)]
    [InlineData("read", 0, true)]
    [InlineData("read", 2, true)]
    [InlineData("read", 3, true)]
    [InlineData("read", 1, false)]
    [InlineData("page", 0, true)]
    [InlineData("page", 2, true)]
    [InlineData("page", 3, true)]
    [InlineData("page", 1, false)]
    public void ARefusalOfAKindItsUnionDoesNotTakeOrWithoutAMessageIsRefused(string union, byte kind, bool message)
    {
        var text = message ? "refused" : null;
        _ = Assert.Throws<InvalidDataException>(() => _ = union switch
        {
            "write" => new JournalForwardedWriteOutcomeConverter().ConvertFromSurrogate(new JournalForwardedWriteOutcomeSurrogate { Case = WriteTag.Refused, Refusal = kind, Message = text }).Value,
            "read" => new JournalForwardedReadConverter().ConvertFromSurrogate(new JournalForwardedReadSurrogate { Case = ReadTag.Refused, Refusal = kind, Message = text }).Value,
            _ => new JournalForwardedPageConverter().ConvertFromSurrogate(new JournalForwardedPageSurrogate { Case = PageTag.Refused, Refusal = kind, Message = text }).Value,
        });
    }

    /// <summary>A <c>Moved</c> names no owner by an absent UUID: the empty UUID, a second spelling of none, is refused.</summary>
    [Fact]
    public void AMovedNamingTheEmptyUuidIsRefused()
    {
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedWriteOutcomeConverter().ConvertFromSurrogate(new JournalForwardedWriteOutcomeSurrogate { Case = WriteTag.Moved, Owner = Guid.Empty }));
        _ = Assert.Throws<InvalidDataException>(() => new JournalForwardedReadConverter().ConvertFromSurrogate(new JournalForwardedReadSurrogate { Case = ReadTag.Moved, Owner = Guid.Empty }));
    }

    /// <summary>
    /// Each value has exactly one accepted encoding: over random write surrogates, each one is either refused with
    /// <see cref="InvalidDataException"/> or decodes to a value which encodes back to it.
    /// </summary>
    [Fact]
    public void EveryWriteEncodingIsRefusedOrItsValuesOneEncoding() =>
        Gen.Select(Gen.Byte[0, 10], Versions(), Gen.Bool, Owners(), Gen.Byte[0, 3], Messages())
            .Sample(static (tag, version, replaceRequested, owner, refusal, message) =>
            {
                var surrogate = new JournalForwardedWriteOutcomeSurrogate { Case = tag, Version = version, ReplaceRequested = replaceRequested, Owner = owner, Refusal = refusal, Message = message };
                var converter = new JournalForwardedWriteOutcomeConverter();
                JournalForwardedWriteOutcome value;
                try
                {
                    value = converter.ConvertFromSurrogate(surrogate);
                }
                catch (InvalidDataException)
                {
                    return;
                }

                var again = converter.ConvertToSurrogate(value);
                Assert.Equal(
                    (surrogate.Case, surrogate.Version, surrogate.ReplaceRequested, surrogate.Owner, surrogate.Refusal, surrogate.Message),
                    (again.Case, again.Version, again.ReplaceRequested, again.Owner, again.Refusal, again.Message));
            });

    /// <summary>
    /// Each value has exactly one accepted encoding: over random read surrogates, each one is either refused with
    /// <see cref="InvalidDataException"/>, its first page released, or decodes to a value which encodes back to it.
    /// </summary>
    [Fact]
    public void EveryReadEncodingIsRefusedOrItsValuesOneEncoding() =>
        Gen.Select(
                Gen.Select(Gen.Byte[0, 6], Versions(), Gen.Bool, Gen.OneOfConst(-1L, 0L, 1L, 3L, JournalForwardedRead.PageBytes, JournalForwardedRead.PageBytes + 1L)),
                Gen.Select(Gen.Bool, Gen.OneOfConst(0, 1, 3, JournalForwardedRead.PageBytes), Gen.OneOfConst<Guid?>(null, new Guid("0b1c2d3e-4f5a-4b6c-9d7e-8f9a0b1c2d3e")), Owners()),
                Gen.Select(Gen.Byte[0, 3], Messages()))
            .Sample(
                static (head, body, refusal) =>
                {
                    var page = WireBytes.Of(new byte[body.Item2]);
                    var surrogate = new JournalForwardedReadSurrogate
                    {
                        Case = head.Item1,
                        Version = head.Item2,
                        Exists = head.Item3,
                        ContentLength = head.Item4,
                        ReplaceRequested = body.Item1,
                        FirstPage = page,
                        Snapshot = body.Item3,
                        Owner = body.Item4,
                        Refusal = refusal.Item1,
                        Message = refusal.Item2,
                    };
                    var converter = new JournalForwardedReadConverter();
                    JournalForwardedRead value;
                    try
                    {
                        value = converter.ConvertFromSurrogate(surrogate);
                    }
                    catch (InvalidDataException)
                    {
                        Assert.True(page.IsEmpty || WireBytes.IsReleased(page), "A refused read released the page it decoded.");
                        return;
                    }

                    var again = converter.ConvertToSurrogate(value);
                    try
                    {
                        Assert.Equal(
                            (surrogate.Case, surrogate.Version, surrogate.Exists, surrogate.ContentLength, surrogate.ReplaceRequested, surrogate.Snapshot, surrogate.Owner, surrogate.Refusal, surrogate.Message),
                            (again.Case, again.Version, again.Exists, again.ContentLength, again.ReplaceRequested, again.Snapshot, again.Owner, again.Refusal, again.Message));
                        Assert.Equal(body.Item2, again.FirstPage.Length);
                    }
                    finally
                    {
                        (value.Value as JournalForwardedRead.Found)?.FirstPage.Dispose();
                    }
                },
                iter: 300);

    /// <summary>
    /// Each value has exactly one accepted encoding: over random page surrogates, each one is either refused with
    /// <see cref="InvalidDataException"/>, its bytes released, or decodes to a value which encodes back to it.
    /// </summary>
    [Fact]
    public void EveryPageEncodingIsRefusedOrItsValuesOneEncoding() =>
        Gen.Select(Gen.Byte[0, 5], Gen.OneOfConst(0, 1, 3), Gen.Byte[0, 3], Messages())
            .Sample(static (tag, length, refusal, message) =>
            {
                var bytes = WireBytes.Of(new byte[length]);
                var surrogate = new JournalForwardedPageSurrogate { Case = tag, Bytes = bytes, Refusal = refusal, Message = message };
                var converter = new JournalForwardedPageConverter();
                JournalForwardedPage value;
                try
                {
                    value = converter.ConvertFromSurrogate(surrogate);
                }
                catch (InvalidDataException)
                {
                    Assert.True(bytes.IsEmpty || WireBytes.IsReleased(bytes), "A refused page released the bytes it decoded.");
                    return;
                }

                var again = converter.ConvertToSurrogate(value);
                try
                {
                    Assert.Equal((surrogate.Case, surrogate.Refusal, surrogate.Message, length), (again.Case, again.Refusal, again.Message, again.Bytes.Length));
                }
                finally
                {
                    (value.Value as JournalForwardedPage.Page)?.Bytes.Dispose();
                }
            });

    private static Gen<string?> Versions() => Gen.OneOfConst<string?>(null, Version.ToString(), string.Empty, "not a version");

    private static Gen<Guid?> Owners() => Gen.OneOfConst<Guid?>(null, Guid.Empty, Owner.Value);

    private static Gen<string?> Messages() => Gen.OneOfConst<string?>(null, "refused");

    private static OrleansBuffer FullPage() => WireBytes.Of(Enumerable.Repeat(PageFill, JournalForwardedRead.PageBytes).ToArray());

    private static JournalForwardedRead Pinned(OrleansBuffer firstPage) =>
        new JournalForwardedRead.Found(Version, Exists: true, ContentLength: JournalForwardedRead.PageBytes + 1, ReplaceRequested: true, firstPage, new Guid("0b1c2d3e-4f5a-4b6c-9d7e-8f9a0b1c2d3e"));

    private static string Wire<T>(Serializer serializer, T value) => Convert.ToHexStringLower(serializer.SerializeToArray(value));
}
