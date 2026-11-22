using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the ends of the key-range algebra (ledgers.md §2.1, §4; conventions.md §8): the whole keyspace, ranges at the
/// deepest depth, keys of every boundary length, the bit order, canonical prefixes, and the text form.
/// </summary>
[NoPageLeakCheck("Key ranges are values; the tests rent no pages.")]
public sealed class KeyRangeTests
{
    private static readonly byte[] LongestKey = new byte[KeyRange.MaxKeyLength];

    [Fact]
    public void TheDefaultIsTheWholeKeyspace()
    {
        var constructed = new KeyRange([], 0);

        Assert.Equal(constructed, default);
        Assert.Equal(KeyRange.Root, constructed);
        Assert.Equal(0, KeyRange.Root.Depth);
        Assert.True(KeyRange.Root.Prefix.IsEmpty);
        Assert.Equal(constructed.GetHashCode(), default(KeyRange).GetHashCode());
        Assert.Equal(KeyRange.Root, KeyRange.Containing([0xff], 0));
        Assert.Equal(KeyRange.Root.GetHashCode(), KeyRange.GetContainingHashCode([0xff], 0));
        Assert.Equal("/0", KeyRange.Root.ToString());
        Assert.True(KeyRange.Root.Contains([0x00]));
        Assert.True(KeyRange.Root.Contains(LongestKey));
        Assert.True(KeyRange.Root.Contains(KeyRange.Containing(LongestKey, KeyRange.MaxDepth)));
    }

    [Fact]
    public void TheWholeKeyspaceHasNoParentOrSibling()
    {
        _ = Assert.Throws<InvalidOperationException>(() => KeyRange.Root.Parent);
        _ = Assert.Throws<InvalidOperationException>(() => KeyRange.Root.Sibling);

        var (lower, upper) = KeyRange.Root.Children;
        Assert.Equal("0/1", lower.ToString());
        Assert.Equal("1/1", upper.ToString());
        Assert.Equal(KeyRange.Root, lower.Parent);
        Assert.Equal(upper, lower.Sibling);
    }

    [Fact]
    public void ARangeAtTheDeepestDepthHasNoChildren()
    {
        Assert.Equal(8 * 65_535, KeyRange.MaxDepth);
        var deepest = KeyRange.Containing(LongestKey, KeyRange.MaxDepth);

        _ = Assert.Throws<InvalidOperationException>(() => deepest.Children);
        Assert.Equal(KeyRange.MaxDepth, deepest.Depth);
        Assert.Equal(KeyRange.MaxKeyLength, deepest.Prefix.Length);
        Assert.True(deepest.Contains(LongestKey));
        Assert.Equal(KeyRange.MaxDepth - 1, deepest.Parent.Depth);
        Assert.Equal(deepest, deepest.Parent.Children.Lower);
        Assert.Equal(deepest.Parent.Children.Upper, deepest.Sibling);
        Assert.False(deepest.Sibling.Contains(LongestKey));
        Assert.Equal(KeyRange.MaxDepth, new KeyRange(LongestKey, KeyRange.MaxDepth).Depth);
        Assert.Equal("depth", Assert.Throws<ArgumentOutOfRangeException>(() => KeyRange.Containing(LongestKey, KeyRange.MaxDepth + 1)).ParamName);
    }

    [Fact]
    public void KeysAreOneTo65535Bytes()
    {
        var range = KeyRange.Containing([0x80], 1);
        byte[] tooLong = new byte[KeyRange.MaxKeyLength + 1];

        Assert.True(range.Contains([0x80]));
        Assert.False(range.Contains(LongestKey));
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => range.Contains([])).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => range.Contains(tooLong)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => KeyRange.Containing([], 0)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => KeyRange.Containing(tooLong, 1)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => KeyRange.GetContainingHashCode([], 0)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => KeyRange.GetContainingHashCode(tooLong, 1)).ParamName);
    }

    /// <summary>
    /// Bit <c>i</c> of a key is bit <c>7 − (i mod 8)</c> of byte <c>i / 8</c>, the most significant first, and the bits
    /// past a key's end are 0, so a key's ranges are its byte-order prefixes.
    /// </summary>
    [Fact]
    public void KeyBitsRunFromEachBytesMostSignificantBit()
    {
        var range = new KeyRange([0b1010_0000], 3);

        Assert.Equal("101/3", range.ToString());
        Assert.True(range.Contains([0b1011_1111]));
        Assert.False(range.Contains([0b1000_0000]));
        Assert.False(range.Contains([0b0000_0101]));
        Assert.Equal(range, KeyRange.Containing([0b1011_0110, 0xff], 3));

        // A key shorter than the prefix has 0 bits past its end.
        var deeper = new KeyRange([0x12, 0x00], 12);
        Assert.Equal("000100100000/12", deeper.ToString());
        Assert.True(deeper.Contains([0x12]));
        Assert.False(new KeyRange([0x12, 0x10], 12).Contains([0x12]));

        // A prefix's whole bytes past a shorter key's end are 0 for the key to lie in the range.
        Assert.True(new KeyRange([0x12, 0x00], 16).Contains([0x12]));
        Assert.False(new KeyRange([0x12, 0x34], 16).Contains([0x12]));
    }

    [Fact]
    public void ConstructionRequiresACanonicalPrefix()
    {
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => new KeyRange([0x00], 0)).ParamName);
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => new KeyRange([], 1)).ParamName);
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => new KeyRange([0x00, 0x00], 8)).ParamName);
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => new KeyRange([0x01], 7)).ParamName);
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => new KeyRange([0xff, 0x40], 9)).ParamName);
        Assert.Equal("depth", Assert.Throws<ArgumentOutOfRangeException>(() => new KeyRange([], -1)).ParamName);
        Assert.Equal("depth", Assert.Throws<ArgumentOutOfRangeException>(() => new KeyRange(new byte[KeyRange.MaxKeyLength + 1], KeyRange.MaxDepth + 1)).ParamName);

        Assert.Equal("11111111/8", new KeyRange([0xff], 8).ToString());
        Assert.Equal("111111111/9", new KeyRange([0xff, 0x80], 9).ToString());
    }

    [Fact]
    public void APrefixIsCopiedAtConstruction()
    {
        byte[] prefix = [0x80];
        var range = new KeyRange(prefix, 1);
        prefix[0] = 0;

        Assert.Equal("1/1", range.ToString());
    }

    /// <summary>
    /// The partition check's ends: an empty list, the whole keyspace alone, and a list whose ranges nest or repeat.
    /// </summary>
    [Fact]
    public void ThePartitionCheckRejectsEmptyNestedAndRepeatedLists()
    {
        var (lower, upper) = KeyRange.Root.Children;

        Assert.False(KeyRange.IsPartition([]));
        Assert.True(KeyRange.IsPartition([KeyRange.Root]));
        Assert.True(KeyRange.IsPartition([upper, lower]));
        Assert.False(KeyRange.IsPartition([KeyRange.Root, KeyRange.Root]));
        Assert.False(KeyRange.IsPartition([KeyRange.Root, lower]));
        Assert.False(KeyRange.IsPartition([lower, upper, upper]));
        Assert.False(KeyRange.IsPartition([lower, lower]));
        Assert.False(KeyRange.IsPartition([lower, upper.Children.Lower]));
    }
}
