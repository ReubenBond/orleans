using CsCheck;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// Archive logs' tokens (archive-logs.md §7.2): <c>tokens.json</c> in both directions, each malformed token rejected by its
/// reason, as the store's <see cref="ArchiveCorruptException"/> too, and every position's token canonical.
/// </summary>
[NoPageLeakCheck("Tokens are values; the tests rent no pages.")]
public sealed class ArchiveLogTokensTests
{
    public static TheoryData<string> Cases => [.. SharedVectors.Cases("archive-logs", "tokens.json").Select(SharedVectors.Name)];

    public static TheoryData<string> Rejections => [.. SharedVectors.Cases("archive-logs", "tokens.json", "rejections").Select(SharedVectors.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void ATokenVectorDecodesToItsPositionAndEncodesBack(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "tokens.json").Single(element => SharedVectors.Name(element) == name);
        var position = new ArchiveLogPosition(SharedVectors.UInt64(vector, "generation"), SharedVectors.UInt64(vector, "base_rows"), SharedVectors.UInt64(vector, "length"), SharedVectors.UInt64(vector, "units"));

        Assert.Equal(position, Assert.IsType<ArchiveLogPosition>(ArchiveLogTokens.Decode(SharedVectors.Hex(vector)).Value));
        SharedVectors.AssertBytes(vector, ArchiveLogTokens.Encode(position).AsSpan());
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void AMalformedTokenIsRejectedByItsReason(string name)
    {
        var vector = SharedVectors.Cases("archive-logs", "tokens.json", "rejections").Single(element => SharedVectors.Name(element) == name);
        var bytes = SharedVectors.Hex(vector);

        var rejection = Assert.IsType<InvalidDataException>(ArchiveLogTokens.Decode(bytes).Value);
        Assert.Equal(SharedVectors.Text(vector, "reason"), DecodeRejection.ReasonOf(rejection));
        var corrupt = Assert.Throws<ArchiveCorruptException>(() => ArchiveLogTokens.PositionOf(new RowToken(bytes)));
        Assert.Same(rejection.GetType(), corrupt.InnerException?.GetType());
    }

    /// <summary>Every valid position's token decodes back to it, is at most 27 bytes, and is the only encoding of it.</summary>
    [Fact]
    public void EveryPositionsTokenRoundTripsAndIsCanonical() =>
        Gen.Select(Gen.ULong[1, ArchiveLogTokens.MaxWide], Gen.ULong[0, ArchiveLogTokens.MaxNarrow], Gen.ULong[0, ArchiveLogTokens.MaxWide], Gen.ULong[0, ArchiveLogTokens.MaxNarrow])
            .Where(static fields => fields.Item4 <= fields.Item3 && fields.Item4 == 0 == (fields.Item3 == 0))
            .Sample(static fields =>
            {
                var position = new ArchiveLogPosition(fields.Item1, fields.Item2, fields.Item3, fields.Item4);
                var token = ArchiveLogTokens.Encode(position);
                Assert.True(token.Length <= ArchiveLogTokens.MaxLength);
                Assert.Equal(position, ArchiveLogTokens.PositionOf(token));
                Assert.Equal(token, ArchiveLogTokens.Encode(ArchiveLogTokens.PositionOf(token)));
            });

    [Fact]
    public void OriginIsTheEmptyTokenAndNoPositionOffTheRulesEncodes()
    {
        Assert.True(ArchiveLogTokens.Encode(ArchiveLogPosition.Origin).IsEmpty);
        Assert.Equal(ArchiveLogPosition.Origin, ArchiveLogTokens.PositionOf(default));
        Assert.Throws<ArgumentException>(() => ArchiveLogTokens.Encode(new ArchiveLogPosition(0, 0, 1, 1)));
        Assert.Throws<ArgumentException>(() => ArchiveLogTokens.Encode(new ArchiveLogPosition(1, 0, 1, 2)));
        Assert.Throws<ArgumentException>(() => ArchiveLogTokens.Encode(new ArchiveLogPosition(1, 0, 1, 0)));
        Assert.Throws<ArgumentException>(() => ArchiveLogTokens.Encode(new ArchiveLogPosition(ArchiveLogTokens.MaxWide + 1, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => ArchiveLogTokens.Encode(new ArchiveLogPosition(1, ArchiveLogTokens.MaxNarrow + 1, 0, 0)));
    }
}
