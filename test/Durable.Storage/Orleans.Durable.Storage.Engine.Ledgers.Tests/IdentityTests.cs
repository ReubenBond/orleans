using System.Globalization;
using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the identity types of identity.md §2 and §3: a node identity is a non-empty UUID, equal by value, whose text
/// form is <c>{node:N}</c>; a ledger ID is a non-zero <c>u64</c>, equal by value, ordered numerically, whose text
/// form is its decimal digits.
/// </summary>
[NoPageLeakCheck("Identities are values; the tests rent no pages.")]
public sealed class IdentityTests
{
    [Fact]
    public void ANodeIdentityIsANonEmptyUuidWrittenAsThirtyTwoHexDigits()
    {
        var value = Guid.ParseExact("00112233445566778899aabbccddeeff", "N");
        var node = new NodeId(value);

        Assert.Equal(value, node.Value);
        Assert.Equal("00112233445566778899aabbccddeeff", node.ToString());
        Assert.Equal(new NodeId(value), node);
        Assert.NotEqual(NodeId.New(), node);
        Assert.NotEqual(Guid.Empty, NodeId.New().Value);
        Assert.Equal("value", Assert.Throws<ArgumentException>(() => new NodeId(Guid.Empty)).ParamName);

        // Only the constructor makes a node identity, so the default instance has no value to encode.
        _ = Assert.Throws<InvalidOperationException>(() => default(NodeId).Value);
    }

    [Fact]
    public void ALedgerIdIsANonZeroU64OrderedNumericallyAndWrittenInDecimal()
    {
        Assert.Equal("value", Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerId(0)).ParamName);

        // Only the constructor makes a ledger ID, so the default instance has no value to encode or compare.
        _ = Assert.Throws<InvalidOperationException>(() => default(LedgerId).Value);
        _ = Assert.Throws<InvalidOperationException>(() => new LedgerId(1) >= default(LedgerId));
        _ = Assert.Throws<InvalidOperationException>(() => new LedgerId(1).CompareTo(default));
        var values = Gen.Frequency((3, Gen.ULong[1, 4]), (1, Gen.ULong[1, ulong.MaxValue]));
        Gen.Select(values, values).Sample(static (left, right) =>
        {
            var a = new LedgerId(left);
            var b = new LedgerId(right);
            Assert.Equal(left, a.Value);
            Assert.Equal(left.ToString(CultureInfo.InvariantCulture), a.ToString());
            Assert.Equal(left == right, a == b);
            Assert.Equal(left == right, a.Equals(b) && a.GetHashCode() == b.GetHashCode());
            Assert.Equal(Math.Sign(left.CompareTo(right)), Math.Sign(a.CompareTo(b)));
            Assert.Equal((left < right, left <= right, left > right, left >= right), (a < b, a <= b, a > b, a >= b));
        });
    }
}
