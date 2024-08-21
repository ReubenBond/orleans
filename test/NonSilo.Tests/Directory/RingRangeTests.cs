using System.Collections.Immutable;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace NonSilo.Tests.Directory;

[TestCategory("BVT")]
public sealed class RingRangeTests
{
    [Fact]
    public void RingRangeAdditionsTest()
    {
        var previous = RingRange.Empty;
        var current = RingRange.CreateEquallyDividedRange(2, 0);
        Assert.Empty(current.Difference(current));

        Assert.Equal(current, Assert.Single(current.Difference(previous)));
        Assert.Empty(previous.Difference(current));

        var firstHalf = RingRange.CreateEquallyDividedRange(2, 0);
        var secondHalf = RingRange.CreateEquallyDividedRange(2, 1);

        Assert.Equal(firstHalf, Assert.Single(firstHalf.Difference(secondHalf)));
        Assert.Equal(secondHalf, Assert.Single(secondHalf.Difference(firstHalf)));
    }

    [Fact]
    public void RingRangeAdditionsTest_HolePunch()
    {
        var first = RingRange.CreateEquallyDividedRange(8, 0);
        var second = RingRange.CreateEquallyDividedRange(8, 1);
        var third = RingRange.CreateEquallyDividedRange(8, 2);
        var fullRange = RingRange.Create(first.Start, third.End);

        var midPunch = fullRange.Difference(second);
        Assert.Equal(2, midPunch.Count());
        Assert.Equal(first, midPunch.First());
        Assert.Equal(third, midPunch.Last());
    }

    [Fact]
    public void RingRangeAdditionsTest_End()
    {
        var current = RingRange.Create(0x33333334, 0x66666667);
        var result = current.Difference(RingRange.Empty);
        Assert.Equal(current, Assert.Single(result));
    }

    [Fact]
    public void RingRangeAdditionsTest_End_Two()
    {
        var current = RingRange.Create(0x33333334, 0x66666667);
        var previous = RingRange.Create(uint.MaxValue - 1, 1);
        var result = Assert.Single(current.Difference(previous));
        Assert.Equal(current, result);
        Assert.Equal(previous, Assert.Single(previous.Difference(current)));
    }

    [Fact]
    public void RingRangeIntersection()
    {
        Assert.Empty(RingRange.Empty.Difference(RingRange.Empty));

        Assert.Empty(RingRange.Full.Difference(RingRange.Full));

        Assert.Equal(RingRange.Full, Assert.Single(RingRange.Full.Difference(RingRange.Empty)));

        Assert.Empty(RingRange.Empty.Difference(RingRange.Full));
    }

    [Fact]
    public void RingRangeContains()
    {
        Assert.False(RingRange.Empty.Contains(0));
        Assert.False(RingRange.Empty.Contains(1));
        Assert.False(RingRange.Empty.Contains(uint.MaxValue));
        Assert.False(RingRange.Empty.Contains(uint.MaxValue / 2));

        Assert.True(RingRange.Full.Contains(0));
        Assert.True(RingRange.Full.Contains(1));
        Assert.True(RingRange.Full.Contains(uint.MaxValue));
        Assert.True(RingRange.Full.Contains(uint.MaxValue / 2));

        var wrapped = RingRange.Create(uint.MaxValue - 10, 10);
        Assert.True(wrapped.Contains(0));
        Assert.True(wrapped.Contains(1));
        Assert.True(wrapped.Contains(uint.MaxValue));
        Assert.False(wrapped.Contains(uint.MaxValue / 2));
    }

    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(33)]
    [Theory]
    public void EqualRangeInvariants(int count)
    {
        var sum = 0ul;
        var previous = RingRange.Empty;
        for (var i = 0; i < count; i++)
        {
            var range = RingRange.CreateEquallyDividedRange(count, i);
            Assert.False(previous.Intersects(range));
            sum += range.Size;
            previous = range;
        }

        Assert.Equal(uint.MaxValue, sum);
    }
}

[TestCategory("BVT")]
public sealed class RingRangeCollectionTests
{
    [Fact]
    public void ContainsTest()
    {
        var ranges = new RingRange[]
        {
            RingRange.Create(0x10930012, 0x179C5AD4),
            RingRange.Create(0x287844C7, 0x2B5DCCCB),
            RingRange.Create(0x32AC80C2, 0x36F72978),
            RingRange.Create(0x6F5C3AAC, 0x7776E202),
            RingRange.Create(0x7D2B02F3, 0x7DF52810),
            RingRange.Create(0xA18205D1, 0xA3A44031),
            RingRange.Create(0xA847CD39, 0xAD6C28D0),
            RingRange.Create(0xAF60D42F, 0xB278D2BE),
            RingRange.Create(0xBB8EA837, 0xC61DA5E1),
            RingRange.Create(0xF08C2237, 0xF3030A5A)
        }.ToImmutableArray();
        var collection = new RingRangeCollection(ranges);
        uint point = 0x16F4037C;
        Assert.True(ranges[0].Contains(point));
        Assert.True(collection.Contains(point));

        // Just outside the last range.
        point = 0xF3030A5A + 1;
        Assert.False(ranges[^1].Contains(point));
        Assert.False(collection.Contains(point));

        // Just inside the last range.
        point = 0xF3030A5A;
        Assert.True(ranges[^1].Contains(point));
        Assert.True(collection.Contains(point));

        // Between ranges.
        point = 0xF08C2237 - 1;
        Assert.False(collection.Contains(point));

        // In an interior range.
        point = 0x7D2B02F3 + 1;
        Assert.True(collection.Contains(point));
    }
}

[TestCategory("BVT")]
public sealed class DirectoryMembershipSnapshotTests
{
    [Fact]
    public void GetOwnerTest()
    {
    }

    [Fact]
    public void BoundaryTest()
    {
    }

    [Fact]
    public void HashCollisionTest()
    {
        // Tests that silos which have hash collisions are correctly handled.
    }
}
