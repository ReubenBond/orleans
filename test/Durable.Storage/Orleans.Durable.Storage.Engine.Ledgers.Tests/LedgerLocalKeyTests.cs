using System.Text;
using CsCheck;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Golden vectors, boundary checks, and CsCheck properties for <see cref="LocalStreamName"/> and
/// <see cref="LedgerLocalKey"/> (ledgers.md §2.2, §4, §7, LG-I15).
/// </summary>
public sealed class LedgerLocalKeyTests : IDisposable
{
    private const string Area = "ledgers";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void LocalKeyVectorsMatchInBothDirections()
    {
        foreach (var vector in Cases(Area, "local-keys.json"))
        {
            var ledger = new LedgerId(UInt64(vector, "ledger"));
            var range = new KeyRange(Hex(vector, "prefix"), vector.GetProperty("depth").GetInt32());
            var stream = LocalStreamName.Parse(Text(vector, "stream"));

            var encoded = LedgerLocalKey.Encode(ledger, range, stream);
            AssertBytes(vector, encoded);
            Assert.True(range.Contains(encoded));
            Assert.Null(LedgerLocalKey.Validate(encoded, range, ledger, out var decoded));
            Assert.True(stream.Span.SequenceEqual(decoded));
            Assert.True(LedgerLocalKey.TryDecode(encoded, range, ledger, out var tryDecoded));
            Assert.True(stream.Span.SequenceEqual(tryDecoded));
        }
    }

    [Fact]
    public void LocalKeyRejectionVectorsAreRejectedForTheirReason()
    {
        foreach (var vector in Cases(Area, "local-key-rejections.json"))
        {
            var ledger = new LedgerId(UInt64(vector, "ledger"));
            var range = new KeyRange(Hex(vector, "prefix"), vector.GetProperty("depth").GetInt32());
            var bytes = Hex(vector);

            var rejection = LedgerLocalKey.Validate(bytes, range, ledger, out var streamName);
            AssertRejected(vector, rejection);
            Assert.True(streamName.IsEmpty);
            Assert.False(LedgerLocalKey.TryDecode(bytes, range, ledger, out _));
        }
    }

    [Fact]
    public void LocalStreamNameValidatesBoundaries()
    {
        Assert.Equal("orleans.wakeup", LocalStreamName.Parse("orleans.wakeup").Value);
        Assert.Equal(new string('a', LocalStreamName.MaxLength), LocalStreamName.Parse(new string('a', LocalStreamName.MaxLength)).Value);
        Assert.Equal(LocalStreamName.Parse("a"), LocalStreamName.Parse("a"));
        Assert.True(LocalStreamName.Parse("a").CompareTo(LocalStreamName.Parse("b")) < 0);

        Assert.Throws<ArgumentNullException>(() => new LocalStreamName(null!));
        Assert.Throws<ArgumentException>(() => new LocalStreamName(string.Empty));
        Assert.Throws<ArgumentException>(() => new LocalStreamName(new string('a', LocalStreamName.MaxLength + 1)));
        Assert.Throws<ArgumentException>(() => new LocalStreamName("Upper"));
        Assert.Throws<ArgumentException>(() => new LocalStreamName("a/b"));
        Assert.Throws<ArgumentException>(() => new LocalStreamName("a b"));
        Assert.Throws<ArgumentException>(() => new LocalStreamName("é"));
        Assert.False(LocalStreamName.TryParse("Upper", out _));
        Assert.False(LocalStreamName.TryParse("a/b"u8, out _));
    }

    [Fact]
    public void EncodeRejectsInvalidArgumentsAndDepthAboveMax()
    {
        var stream = LocalStreamName.Parse("s");
        Assert.Throws<ArgumentException>(() => LedgerLocalKey.Encode(default, KeyRange.Root, stream));
        Assert.Throws<ArgumentNullException>(() => LedgerLocalKey.Encode(new LedgerId(1), KeyRange.Root, null!));
        var tooDeep = new KeyRange(new byte[33], LedgerLocalKey.MaxRangeDepth + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => LedgerLocalKey.Encode(new LedgerId(1), tooDeep, stream));
        Assert.Throws<ArgumentException>(() => LedgerLocalKey.Write(new LedgerId(1), KeyRange.Root, stream, new byte[4]));
    }

    /// <summary>
    /// Across depths 0..256, every <see cref="LedgerLocalKey"/> lies inside its range, round-trips cleanly, stays within
    /// <see cref="LedgerLocalKey.MaxEncodedLength"/>, and is disjoint from every valid UTF-8 journal row key (whose bytes
    /// from index 4 onward are valid UTF-8 and therefore never contain <c>0xFF</c>).
    /// </summary>
    [Fact]
    public void PropertiesHoldAcrossDepthsAndValidUtf8Keys()
    {
        var alphabet = "abcdefghijklmnopqrstuvwxyz0123456789._-";
        var streamGen = Gen.Int[1, LocalStreamName.MaxLength].SelectMany(len =>
            Gen.Int[0, alphabet.Length - 1].Array[len].Select(indices =>
            {
                var chars = new char[indices.Length];
                for (var i = 0; i < indices.Length; i++)
                {
                    chars[i] = alphabet[indices[i]];
                }

                return LocalStreamName.Parse(new string(chars));
            }));

        var rangeGen = Gen.Int[0, LedgerLocalKey.MaxRangeDepth].SelectMany(depth =>
        {
            var byteLen = (depth + 7) / 8;
            return Gen.Byte.Array[byteLen].Select(bytes =>
            {
                if (depth > 0 && depth % 8 != 0)
                {
                    bytes[^1] &= (byte)((0xFF << (8 - depth % 8)) & 0xFF);
                }

                return new KeyRange(bytes, depth);
            });
        });

        var ledgerGen = Gen.ULong[1, ulong.MaxValue].Select(static id => new LedgerId(id));

        Gen.Select(ledgerGen, rangeGen, streamGen).Sample((ledger, range, stream) =>
        {
            var key = LedgerLocalKey.Encode(ledger, range, stream);
            Assert.InRange(key.Length, 14, LedgerLocalKey.MaxEncodedLength);
            Assert.True(range.Contains(key));
            Assert.Null(LedgerLocalKey.Validate(key, range, ledger, out var decoded));
            Assert.True(stream.Span.SequenceEqual(decoded));

            // 0xFF lies at index >= 4, so the suffix starting at index 4 is never valid UTF-8 (disjoint from every JournalRowKey).
            Assert.False(System.Text.Unicode.Utf8.IsValid(key.AsSpan(4)));

            // Corrupting the sentinel fails validation.
            var badSentinel = (byte[])key.Clone();
            var prefixLen = LedgerLocalKey.PrefixByteLength(range.Depth);
            badSentinel[prefixLen] = 0xFE;
            var sentinelErr = LedgerLocalKey.Validate(badSentinel, range, ledger, out _);
            Assert.NotNull(sentinelErr);
            Assert.Equal(LedgerLocalKey.ReasonBadSentinel, DecodeRejection.ReasonOf(sentinelErr));

            // Flipping an unused low bit in a non-byte-aligned depth fails validation.
            if (range.Depth > 0 && range.Depth % 8 != 0)
            {
                var badLowBit = (byte[])key.Clone();
                badLowBit[(range.Depth - 1) / 8] |= 0x01;
                var prefixErr = LedgerLocalKey.Validate(badLowBit, range, ledger, out _);
                Assert.NotNull(prefixErr);
                Assert.Equal(LedgerLocalKey.ReasonPrefixMismatch, DecodeRejection.ReasonOf(prefixErr));
            }

            // Different ledger ID fails validation.
            var otherLedger = new LedgerId(ledger.Value == ulong.MaxValue ? 1 : ledger.Value + 1);
            var ledgerErr = LedgerLocalKey.Validate(key, range, otherLedger, out _);
            Assert.NotNull(ledgerErr);
            Assert.Equal(LedgerLocalKey.ReasonLedgerMismatch, DecodeRejection.ReasonOf(ledgerErr));
        });
    }
}
