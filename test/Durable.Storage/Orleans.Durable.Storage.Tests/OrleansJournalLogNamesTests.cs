using System.Buffers.Binary;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The composition's names of its journals' logs (orleans.md §5.13, LOG-I5): a journal's row key names its log by the
/// journal's identity, which Orleans' own Azure provider names the journal's write-ahead blob by, and no other key is named.
/// </summary>
[NoPageLeakCheck("Names are strings; the tests rent no pages.")]
public sealed class OrleansJournalLogNamesTests
{
    [Theory]
    [InlineData("counter/7")]
    [InlineData("a")]
    [InlineData("grains/é/𝄞")]
    public void AJournalsRowKeyNamesItsLogByItsIdentity(string journal)
    {
        var key = JournalRowKey.Encode(journal);

        var name = OrleansJournalLogNames.Names.Of(key.Span);

        Assert.Equal(journal, name.Value);
        Assert.Equal(ArchiveLogNames.KeyHash(key.Span), name.KeyHash);
        Assert.Equal(OrleansJournalLogNames.Scheme, OrleansJournalLogNames.Names.Scheme);
    }

    /// <summary>
    /// Every journal names its log by exactly its identity, so no two journals share a log: identities drawn across the
    /// whole Unicode scalar range, biased toward what a mapping which is not exact would fold, trim, collapse, or escape,
    /// such as upper case, white space, <c>.</c> and <c>/</c> segments, <c>%</c>, <c>+</c>, <c>#</c>, <c>?</c>, U+FEFF,
    /// and supplementary characters.
    /// </summary>
    [Fact]
    public void EveryJournalsRowKeyNamesItsLogByExactlyItsIdentity() =>
        Identities.Sample(static id => Assert.Equal(id, OrleansJournalLogNames.Names.Of(JournalRowKey.Encode(id).Span).Value));

    /// <summary>
    /// A journal's row key with a byte of its hash changed, or truncated within its UTF-8, its hash recomputed or not, never
    /// names the journal's log.
    /// </summary>
    [Fact]
    public void ARowKeyWithAChangedHashOrTruncatedUtf8NeverNamesItsJournalsLog() =>
        Gen.Select(Identities, Gen.Int[0, int.MaxValue], Gen.Byte[1, 255], Gen.Bool).Sample(static (id, position, change, rehash) =>
        {
            var key = JournalRowKey.Encode(id).Memory.ToArray();
            byte[] changed;
            if (position % 2 == 0)
            {
                changed = (byte[])key.Clone();
                changed[position / 2 % JournalRowKey.PrefixLength] ^= change;
            }
            else
            {
                changed = key[..(JournalRowKey.PrefixLength + position / 2 % (key.Length - JournalRowKey.PrefixLength))];
                if (rehash)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(changed, System.IO.Hashing.XxHash32.HashToUInt32(changed.AsSpan(JournalRowKey.PrefixLength)));
                }
            }

            Assert.NotEqual(id, OrleansJournalLogNames.Names.TryOf(changed)?.Value);
        });

    /// <summary>
    /// A key which is no journal's row key is named no log, so the store admits it under no name another key's log has:
    /// a key too short, one whose hash does not lead it, one of malformed UTF-8 behind its hash, and an identity under
    /// <c>durable-storage/</c>, which durable storage's own objects hold.
    /// </summary>
    [Fact]
    public void AKeyWhichIsNoJournalsRowKeyIsNamedNoLog()
    {
        var rowKey = JournalRowKey.Encode("counter/7").Memory.ToArray();
        var wrongHash = rowKey.ToArray();
        wrongHash[0] ^= 1;
        byte[] malformed = [0, 0, 0, 0, 0xff];
        BinaryPrimitives.WriteUInt32BigEndian(malformed, System.IO.Hashing.XxHash32.HashToUInt32(malformed.AsSpan(JournalRowKey.PrefixLength)));

        Assert.Null(OrleansJournalLogNames.Names.TryOf(rowKey.AsSpan(0, JournalRowKey.PrefixLength)));
        Assert.Null(OrleansJournalLogNames.Names.TryOf(wrongHash));
        Assert.Null(OrleansJournalLogNames.Names.TryOf(malformed));
        Assert.Null(OrleansJournalLogNames.Names.TryOf(JournalRowKey.Encode("durable-storage/store").Span));
        Assert.Equal("counter/7", OrleansJournalLogNames.Names.TryOf(rowKey)?.Value);
    }

    /// <summary>
    /// Journal identities a row key holds, outside <c>durable-storage/</c>: runs of scalars, each upper case, white space, a character
    /// a blob name escapes, U+FEFF, a supplementary character, or any scalar, and of segments such as <c>/./</c>.
    /// </summary>
    private static Gen<string> Identities { get; } = CreateIdentities();

    private static Gen<string> CreateIdentities()
    {
        var any = Gen.Int[0, 0x10FFFF - 0x800].Select(static value => value < 0xD800 ? value : value + 0x800);
        var scalar = Gen.Frequency(
            (4, Gen.Int['A', 'Z']),
            (3, Gen.OneOfConst(' ', '\t', '\n', '\u00A0', '\u2003', '\u3000').Select(static space => (int)space)),
            (4, Gen.OneOfConst('.', '/', '%', '+', '#', '?').Select(static special => (int)special)),
            (1, Gen.Const(0xFEFF)),
            (2, Gen.Int[0x10000, 0x10FFFF]),
            (3, any));
        var piece = Gen.Frequency(
            (8, scalar.Select(char.ConvertFromUtf32)),
            (1, Gen.OneOfConst("/./", "/../", "//", "%2F", "%2f", " /", "/ ")));
        return piece.Array[1, 24]
            .Select(static pieces => string.Concat(pieces))
            .Where(static id => !id.StartsWith(ArchiveLogNames.ReservedPrefix, StringComparison.Ordinal) && Encodes(id));

        static bool Encodes(string id)
        {
            try
            {
                _ = JournalRowKey.Encode(id);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
