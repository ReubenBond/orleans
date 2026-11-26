using System.Collections.Concurrent;
using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Fuzz;
using Orleans.Durable.Storage.Fuzz.Journals;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Journals.Tests.JournalSamples;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// The entry record codec (journals.md §7), the version's text form, and the row key (§4.1): every entry decodes under
/// its row key as it was encoded, from any division into segments and as a forwarded record; <see cref="EntryRules"/>,
/// one independent reading of the rules, judges every mutated encoding and key; a record's CRC-32C covers its row key; a
/// record keeps its own lease on its payload; every version's text parses back to it and is the only text which does;
/// and the row key's preconditions.
/// </summary>
public sealed class JournalEntryCodecTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void EveryEntryDecodesAsItWasEncodedFromAnySegmentsAndAsAForwardedRecord() =>
        Gen.Select(Keys, Entries, Gen.Int[1, 32]).Sample((key, sample, segment) => _leaks.InScope(() =>
        {
            using var record = sample.Encode(key);
            Assert.Equal(key, record.Key);
            sample.AssertDecoded(record.Entry);
            using var bytes = record.ToArcBuffer();
            var array = bytes.ToArray();
            Assert.Equal(record.Length, array.Length);
            Assert.Null(JournalEntry.Decode(key.Span, Segments.Of(array, segment), out var entry));
            sample.AssertDecoded(entry);
            Assert.Null(EntryRules.Read(key.Span, array).Reason);

            Assert.Null(JournalEntryRecord.Decode(key, bytes, out var forwarded));
            using (forwarded)
            {
                sample.AssertDecoded(forwarded.Entry);
                Assert.Equal(record.Crc32C, forwarded.Crc32C);
                using var again = forwarded.ToArcBuffer();
                Assert.Equal(array, again.ToArray());
            }

            if (sample.Kind is JournalEntryKind.Append or JournalEntryKind.Replace)
            {
                var sequence = Segments.Of(sample.Payload, segment);
                using var borrowed = sample.Kind == JournalEntryKind.Append
                    ? JournalEntryRecord.AppendBorrowed(key, record.Entry.ExpectedVersion, sequence, record.Entry.Messages.AsSpan())
                    : JournalEntryRecord.ReplaceBorrowed(key, record.Entry.ExpectedVersion, sequence, record.Entry.Messages.AsSpan());
                Assert.Equal(record.Crc32C, borrowed.Crc32C);
                Assert.Equal(record.Length, borrowed.Length);
                Assert.Throws<InvalidOperationException>(() => _ = borrowed.Payload);

                var spanBytes = new byte[borrowed.Length];
                Assert.Equal(array.Length, borrowed.WriteTo(spanBytes));
                Assert.Equal(array, spanBytes);

                using var fromBorrowed = borrowed.ToArcBuffer();
                Assert.Equal(array, fromBorrowed.ToArray());

                using var retained = borrowed.Retain();
                Assert.Equal(sample.Payload, retained.Payload.ToArray());
                Assert.Equal(record.Crc32C, retained.Crc32C);
            }
        }));

    /// <summary>
    /// Every mutated encoding (a flipped bit of the record or of its row key, a truncation, or an extension), with its
    /// CRC-32C as mutated or recomputed, satisfies the <c>journals-entry</c> fuzz oracle: rejected for the reason the
    /// rules name, or accepted as the fields they give, which encode to the same bytes. <c>classify</c> reports each
    /// outcome; acceptance and rejection both arise.
    /// </summary>
    [Fact]
    public void EveryMutatedEncodingIsJudgedAsTheRulesJudgeIt()
    {
        var target = new EntryTarget();
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Gen.Select(Keys, Entries, Gen.Int[0, 3], Gen.Int[0, 4096], Gen.Byte.Array[1, 8], Gen.Bool).Sample(
            sample =>
            {
                var (key, entry, mutation, position, extra, seal) = sample;
                var outcome = "accepted";
                _leaks.InScope(() =>
                {
                    using var record = entry.Encode(key);
                    using var encoded = record.ToArcBuffer();
                    var bytes = encoded.ToArray();
                    var rowKey = key.Span.ToArray();
                    var mutated = mutation switch
                    {
                        0 => Flip(bytes, position),
                        1 => bytes[..(position % bytes.Length)],
                        2 => [.. bytes, .. extra],
                        _ => bytes,
                    };
                    var framedKey = mutation == 3 ? Flip(rowKey, position) : rowKey;
                    var input = EntryRules.Frame(framedKey, mutated);
                    input = seal ? EntryRules.Seal(input) ?? input : input;
                    outcome = target.Check(input) == FuzzOutcome.Accepted
                        ? "accepted"
                        : EntryRules.Read(framedKey, input.AsSpan(sizeof(ushort) + framedKey.Length)).Reason ?? "rejected";
                });
                _ = outcomes.AddOrUpdate(outcome, 1, static (_, count) => count + 1);
                return outcome;
            },
            TestDiagnostics.WriteLine,
            iter: 2000);

        Assert.Contains("accepted", outcomes.Keys);
        Assert.Contains("crc-mismatch", outcomes.Keys);
        Assert.Contains("truncated", outcomes.Keys);
    }

    /// <summary>
    /// A version's text form (journals.md §7) parses back to the version, and is canonical: re-encoding any text which
    /// parses gives the same text, so string equality is version equality. Text derived from a version's by a change of
    /// one character, a truncation, or an extension parses only to its own canonical form.
    /// </summary>
    [Fact]
    public void EveryVersionsTextParsesBackAndIsItsOnlyText() =>
        Gen.Select(Versions, Gen.Int[0, 3], Gen.Int[0, 64], Gen.Char['+', 'z']).Sample((version, mutation, position, character) =>
        {
            var text = version.ToString();
            Assert.InRange(text.Length, 3, 58);
            Assert.Equal(version, JournalVersion.Parse(text));
            var index = position % text.Length;
            var mutated = mutation switch
            {
                0 => string.Concat(text.AsSpan(0, index), character.ToString(), text.AsSpan(index + 1)),
                1 => text[..index],
                2 => text + character,
                _ => text,
            };
            if (JournalVersion.TryParse(mutated, out var parsed))
            {
                Assert.Equal(mutated, parsed.ToString());
            }
        });

    /// <summary>A version converts to and from Ledgers' <c>RowVersion</c> unchanged, and advances its count by one.</summary>
    [Fact]
    public void AVersionConvertsToAndFromARowVersionAndAdvancesItsCount()
    {
        var version = V(7, 41);

        Assert.Equal(new RowVersion(new RowToken([7]), 41), version.ToRowVersion());
        Assert.Equal(version, JournalVersion.FromRowVersion(version.ToRowVersion()));
        Assert.Equal(V(7, 42), version.Next());
        Assert.Equal(JournalVersion.Origin, default);
        _ = Assert.Throws<OverflowException>(static () => V(ulong.MaxValue).Next());
    }

    /// <summary>A record's CRC-32C covers its row key (JS-I5): it verifies only under the key it was written for.</summary>
    [Fact]
    public void ARecordVerifiesOnlyUnderItsOwnRowKey()
    {
        using var record = Append(V(3), "hello");
        using var encoded = record.ToArcBuffer();

        Assert.Null(JournalEntry.Decode(Key.Span, encoded.AsReadOnlySequence(), out _));
        var rejection = JournalEntryRecord.Decode(JournalRowKey.Encode("journal2"), encoded, out var forwarded);

        Assert.NotNull(rejection);
        Assert.Equal("crc-mismatch", DecodeRejection.ReasonOf(rejection));
        Assert.Null(forwarded.Key);
    }

    [Fact]
    public void ARecordKeepsItsOwnLeaseOnThePayloadWhichTheCallerReleasesSeparately()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        JournalEntryRecord record;
        using (var payload = ArcBuffer.Wrap(bytes))
        {
            record = JournalEntryRecord.Append(Key, V(4), payload);
        }

        using (record)
        {
            Assert.Equal(bytes, record.Payload.ToArray());
            using var encoded = record.ToArcBuffer();
            Assert.Null(JournalEntry.Decode(Key.Span, encoded.AsReadOnlySequence(), out var entry));
            Assert.Equal(bytes.Length, entry.PayloadLength);
        }
    }

    /// <summary>
    /// An empty snapshot is the default buffer and a valid <c>Replace</c>, after which the journal exists; an
    /// <c>Append</c> adds a non-empty payload, so its factory refuses the empty one (journals.md §2.1, §2.2).
    /// </summary>
    [Fact]
    public void AnEmptySnapshotIsTheDefaultBufferAndAnEmptyAppendIsRefused()
    {
        using var record = JournalEntryRecord.Replace(Key, V(1), default);
        Assert.Equal(0, record.Payload.Length);
        using var encoded = record.ToArcBuffer();
        Assert.Null(JournalEntryRecord.Decode(Key, encoded, out var forwarded));
        using (forwarded)
        {
            Assert.Equal(0, forwarded.Payload.Length);
            Assert.Equal(JournalEntryKind.Replace, forwarded.Entry.Kind);
        }

        _ = Assert.Throws<ArgumentException>("payload", static () => JournalEntryRecord.Append(Key, V(1), default));
    }

    /// <summary>A forwarded record which changed in transit fails its CRC-32C at the primary (JS-I5).</summary>
    [Fact]
    public void AForwardedRecordWhosePayloadChangedIsRejectedByItsCrc()
    {
        using var record = Append(V(3), "hello");
        using var encoded = record.ToArcBuffer();
        var bytes = encoded.ToArray();
        bytes[^1] ^= 0x20;
        using var changed = ArcBuffer.Wrap(bytes);

        var rejection = JournalEntryRecord.Decode(Key, changed, out _);

        Assert.NotNull(rejection);
        Assert.Equal("crc-mismatch", DecodeRejection.ReasonOf(rejection));
    }

    [Fact]
    public void TheFactoriesRefuseANullKey()
    {
        using var payload = ArcBuffer.Wrap(new byte[] { 1 });
        _ = Assert.Throws<ArgumentNullException>(() => JournalEntryRecord.Append(null!, V(1), payload));
        _ = Assert.Throws<ArgumentNullException>(static () => JournalEntryRecord.Replace(null!, V(1), default));
        _ = Assert.Throws<ArgumentNullException>(static () => JournalEntryRecord.Delete(null!, V(1), DateTimeOffset.UnixEpoch));
        _ = Assert.Throws<ArgumentNullException>(static () => JournalEntryRecord.Expired(null!, V(1)));
    }

    /// <summary>The default record holds no record: its CRC-32C is refused with a clear message.</summary>
    [Fact]
    public void TheDefaultRecordHasNoCrc()
    {
        var refused = Assert.Throws<InvalidOperationException>(static () => default(JournalEntryRecord).Crc32C);

        Assert.Contains("default", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeleteIsStampedInUtc()
    {
        var local = new DateTimeOffset(2026, 11, 13, 3, 0, 0, TimeSpan.FromHours(-8));

        using var record = JournalEntryRecord.Delete(Key, V(2), local);

        Assert.Equal(local.UtcTicks, record.Entry.DeletionTicks);
    }

    [Fact]
    public void ARowKeyHoldsAJournalKeyOfOneTo65531BytesOfStrictUtf8()
    {
        _ = Assert.Throws<ArgumentException>(static () => JournalRowKey.Encode(string.Empty));
        _ = Assert.Throws<ArgumentException>(static () => JournalRowKey.Encode("lone \ud800 surrogate"));
        _ = Assert.Throws<ArgumentException>(static () => JournalRowKey.Encode("trailing \udc00"));

        var longest = JournalRowKey.Encode(new string('k', JournalRowKey.MaxKeyBytes));
        Assert.Equal(KeyRange.MaxKeyLength, longest.Length);
        _ = Assert.Throws<ArgumentException>(static () => JournalRowKey.Encode(new string('k', JournalRowKey.MaxKeyBytes + 1)));

        // A three-byte character which would end one byte past the limit.
        var almost = new string('k', JournalRowKey.MaxKeyBytes - 2) + "\u65e5";
        _ = Assert.Throws<ArgumentException>(() => JournalRowKey.Encode(almost));
        Assert.Equal(KeyRange.MaxKeyLength, JournalRowKey.Encode(new string('k', JournalRowKey.MaxKeyBytes - 3) + "\u65e5").Length);
    }

    /// <summary>A row key is immutable and compares by its bytes, and names its journal.</summary>
    [Fact]
    public void ARowKeyComparesByItsBytesAndNamesItsJournal()
    {
        var key = JournalRowKey.Encode("naïve café");

        Assert.Equal(key, JournalRowKey.Encode("naïve café"));
        Assert.Equal(key.GetHashCode(), JournalRowKey.Encode("naïve café").GetHashCode());
        Assert.NotEqual(key, JournalRowKey.Encode("naive cafe"));
        Assert.Equal("naïve café", key.ToString());
    }

    /// <summary>
    /// <see cref="JournalRowKey.TryDecode"/> is <see cref="JournalRowKey.Encode"/>'s inverse: every well-formed journal key
    /// decodes from its row key, and any bytes which decode, a row key's with a byte changed or truncated, its hash
    /// recomputed or not, or arbitrary bytes behind their own hash, are exactly the row key of the key they decode to.
    /// </summary>
    [Fact]
    public void ARowKeyDecodesToItsJournalKeyAndNoOtherBytesDo()
    {
        var scalar = Gen.Int[0, 0x10FFFF - 0x800].Select(static value => value < 0xD800 ? value : value + 0x800);
        var keys = scalar.Array[1, 24].Select(static scalars => string.Concat(scalars.Select(char.ConvertFromUtf32)));
        keys.Sample(static key => Assert.Equal(key, JournalRowKey.TryDecode(JournalRowKey.Encode(key).Span)));

        var derived = Gen.Select(keys, Gen.Int[0, int.MaxValue], Gen.Byte, Gen.Bool).Select(static (key, position, value, rehash) =>
        {
            var bytes = JournalRowKey.Encode(key).Memory.ToArray();
            bytes = position % 2 == 0 ? bytes[..(position / 2 % bytes.Length)] : Changed(bytes, position / 2 % bytes.Length, value);
            return rehash ? Rehashed(bytes) : bytes;
        });
        var arbitrary = Gen.Byte.Array[0, 16].Select(static bytes => Rehashed([0, 0, 0, 0, .. bytes]));
        Gen.OneOf(derived, arbitrary).Sample(static bytes =>
        {
            if (JournalRowKey.TryDecode(bytes) is { } key)
            {
                Assert.Equal(bytes, JournalRowKey.Encode(key).Memory.ToArray());
            }
        });

        static byte[] Changed(byte[] bytes, int position, byte value)
        {
            var changed = (byte[])bytes.Clone();
            changed[position] = value;
            return changed;
        }

        static byte[] Rehashed(byte[] bytes)
        {
            if (bytes.Length >= JournalRowKey.PrefixLength)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, System.IO.Hashing.XxHash32.HashToUInt32(bytes.AsSpan(JournalRowKey.PrefixLength)));
            }

            return bytes;
        }
    }

    private static byte[] Flip(byte[] bytes, int position)
    {
        var flipped = (byte[])bytes.Clone();
        var bit = position % (flipped.Length * 8);
        flipped[bit / 8] ^= (byte)(1 << (bit % 8));
        return flipped;
    }
}
