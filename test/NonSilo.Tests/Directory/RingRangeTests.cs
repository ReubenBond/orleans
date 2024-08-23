using System.Collections.Immutable;
using Orleans.Runtime.GrainDirectory;
using CsCheck;
using Xunit;
using Orleans.Configuration;

namespace NonSilo.Tests.Directory;

[TestCategory("BVT")]
public sealed class RingRangeTests
{
    internal static Gen<RingRange> GenRingRange => Gen.Select(Gen.UInt, Gen.UInt, RingRange.Create);

    [Fact]
    public void RingRangeDifference_EquallyDividedRange()
    {
        var previous = RingRange.Empty;
        var current = CreateEquallyDividedRange(2, 0);
        Assert.Empty(current.Difference(current));

        Assert.Equal(current, Assert.Single(current.Difference(previous)));
        Assert.Empty(previous.Difference(current));

        var firstHalf = CreateEquallyDividedRange(2, 0);
        var secondHalf = CreateEquallyDividedRange(2, 1);

        Assert.Equal(firstHalf, Assert.Single(firstHalf.Difference(secondHalf)));
        Assert.Equal(secondHalf, Assert.Single(secondHalf.Difference(firstHalf)));
    }

    [Fact]
    public void ComplementDoesNotIntersect()
    {
        GenRingRange.Where(range => !range.IsEmpty && !range.IsFull)
            .Sample((sample) =>
            {
                var inverse = sample.Complement();
                Assert.False(sample.Intersects(inverse));
                Assert.Empty(sample.Intersections(inverse));
                Assert.False(sample.Contains(inverse.End));
                var difference = Assert.Single(sample.Difference(inverse));
                Assert.Equal(sample, difference);
                var inverseDifference = Assert.Single(inverse.Difference(sample));
                Assert.Equal(inverse, inverseDifference);
            });
    }

    [Fact]
    public void RingRangeDifference_HolePunch()
    {
        var first = CreateEquallyDividedRange(8, 0);
        var second = CreateEquallyDividedRange(8, 1);
        var third = CreateEquallyDividedRange(8, 2);
        var fullRange = RingRange.Create(first.Start, third.End);

        var midPunch = fullRange.Difference(second);
        Assert.Equal(2, midPunch.Count());
        Assert.Equal(first, midPunch.First());
        Assert.Equal(third, midPunch.Last());
    }

    [Fact]
    public void RingRangeDifference_Empty()
    {
        var current = RingRange.Create(0x33333334, 0x66666667);
        var result = current.Difference(RingRange.Empty);
        Assert.Equal(current, Assert.Single(result));
    }

    [Fact]
    public void RingRangeDifference_Empty_Two()
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
            var range = CreateEquallyDividedRange(count, i);
            Assert.False(previous.Intersects(range));
            sum += range.Size;
            previous = range;
        }

        Assert.Equal(uint.MaxValue, sum);
    }

    private static RingRange CreateEquallyDividedRange(int count, int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return Core((uint)count, (uint)index);
        static RingRange Core(uint count, uint index)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));

            if (count == 1 && index == 0)
            {
                return RingRange.Full;
            }

            var rangeSize = (ulong)uint.MaxValue + 1;
            var portion = rangeSize / count;
            var remainder = rangeSize - portion * count;
            var start = 0u;
            for (var i = 0; i < count; i++)
            {
                // (Start, End]
                var end = unchecked((uint)(start + portion));

                if (remainder > 0)
                {
                    end++;
                    remainder--;
                }

                if (i == index)
                {
                    return RingRange.Create(start, end);
                }

                start = end;
            }

            throw new ArgumentException(null, nameof(index));
        }
    }
}

[TestCategory("BVT")]
public sealed class RingRangeCollectionTests
{
    private static readonly Gen<RingRangeCollection> GenRingRangeCollection = Gen.Int[0, 100].SelectMany(count => Gen.Select(Gen.UInt, Gen.Bool, static (boundary, included) => (boundary, included)).Array[count].Select(elements =>
    {
        var arr = ImmutableArray.CreateBuilder<RingRange>(elements.Length);
        for (var i = 1; i < arr.Count;)
        {
            var prev = elements[i - 1];
            var curr = elements[i];
            if (!curr.included)
            {
                continue;
            }

            arr.Add(RingRange.Create(prev.boundary, curr.boundary));
        }

        return RingRangeCollection.Create(arr);
    }));

    [Fact]
    public void Contains()
    {
        Gen.Select(GenRingRangeCollection, Gen.UInt).Sample((ranges, point) =>
        {
            var doesContain = ranges.Ranges.Any(r => r.Contains(point));
            Assert.Equal(doesContain, ranges.Contains(point));
        });
    }

    [Fact]
    public void Intersects()
    {
        GenRingRangeCollection.Sample(ranges =>
        {
            foreach (var range in ranges.Ranges)
            {
                Assert.True(ranges.Intersects(range));
            }
        });
    }

    [Fact]
    public void Difference()
    {
        var ringWithUpdates = GenRingRangeCollection.SelectMany(original => Gen.Float[0f, 1f].Array[original.Ranges.Length].Select(diffs =>
        {
            // Increase or decrease the end of each range by some amount.
            var arr = ImmutableArray.CreateBuilder<RingRange>(original.Ranges.Length);
            for (var i = 0; i < diffs.Length; i++)
            {
                var orig = original.Ranges[i];
                var next = original.Ranges[(i + 1) % original.Ranges.Length];
                var maxPossibleLength = RingRange.Create(orig.Start, next.Start).Size;
                var newEnd = orig.Start + maxPossibleLength * diffs[i];
                arr.Add(RingRange.Create(orig.Start, (uint)Math.Clamp(orig.End + diffs[i], orig.Start + 1, next.Start)));
            }

            return (original, RingRangeCollection.Create(arr));
        }));

        ringWithUpdates.Sample((original, updated) =>
        {
            var additions = updated.Difference(original);
            
            foreach (var addition in additions)
            {
                Assert.True(updated.Intersects(addition));
                Assert.False(original.Intersects(addition));
            }

            var removals = updated.Difference(original);
            
            foreach (var removal in removals)
            {
                Assert.False(updated.Intersects(removal));
                Assert.True(original.Intersects(removal));
            }
        });
    }

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
    private static readonly Gen<ClusterMembershipSnapshot> GenClusterMembershipSnapshot = Gen.Select(Gen.UInt, Gen.Enum<SiloStatus>(), (hash, status) => (hash, status))
        .Array[Gen.Int[1, 30]].Select((tuple) =>
    {
        var dict = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        var port = 1;
        foreach (var item in tuple)
        {
            var (hash, status) = item;
            var addr = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port++), (int)hash);
            dict.Add(addr, new ClusterMember(addr, status, $"Silo_{hash}"));
        }

        return new ClusterMembershipSnapshot(dict.ToImmutable(), new(1));
    });

    private static readonly Gen<DirectoryMembershipSnapshot> GenDirectoryMembershipSnapshot =
        GenClusterMembershipSnapshot.SelectMany(snapshot => Gen.UInt.Array[ConsistentRingOptions.DEFAULT_NUM_VIRTUAL_RING_BUCKETS].Array[snapshot.Members.Count].Select(hashes => 
    {
        var i = 0;
        return new DirectoryMembershipSnapshot(snapshot, (_, _) => hashes[i++]);
    }));

    [Fact]
    public void GetOwnerTest()
    {
        // As long as the cluster has at least one member, we should be able to find an owner.
        Gen.Select(GenDirectoryMembershipSnapshot, Gen.UInt)
            .Sample((snapshot, hash) => Assert.Equal(snapshot.Members.Length > 0, snapshot.TryGetOwner(hash, out var owner)));
    }

    [Fact]
    public void MembersDoNotIntersectTest()
    {
        // Member ranges should not intersect.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                foreach (var range in snapshot.RangeOwners)
                {
                    foreach (var otherRange in snapshot.RangeOwners)
                    {
                        if (range == otherRange)
                        {
                            continue;
                        }

                        Assert.False(range.Range.Intersects(otherRange.Range));
                    }
                }
            });
    }

    [Fact]
    public void ViewCoversRingTest()
    {
        // The union of all member ranges should cover the entire ring.
        GenDirectoryMembershipSnapshot.Where(s => s.Members.Length > 0)
            .Sample(snapshot =>
            {
                uint sum = 0;
                var allRanges = new List<RingRange>();
                foreach (var member in snapshot.Members)
                {
                    foreach (var range in snapshot.GetRanges(member))
                    {
                        allRanges.Add(range);
                        sum += range.Size;
                    }
                }

                Assert.Equal(uint.MaxValue, sum);
                var allRangesCollection = RingRangeCollection.Create(allRanges);
                Assert.Equal(uint.MaxValue, allRangesCollection.Size);
                Assert.Equal(100f, allRangesCollection.SizePercent);
                Assert.False(allRangesCollection.IsEmpty);
                Assert.False(allRangesCollection.IsDefault);
                Assert.True(allRangesCollection.IsFull);
            });
    }
}
