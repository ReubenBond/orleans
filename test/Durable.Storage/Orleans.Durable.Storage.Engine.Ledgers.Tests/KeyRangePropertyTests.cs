using System.Collections.Concurrent;
using System.Diagnostics;
using CsCheck;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.KeyBits;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// CsCheck properties of the key-range algebra against <see cref="KeyBits"/>, a bit-by-bit reading of ledgers.md §2.1:
/// containment of keys and ranges, overlap, children, parents, and siblings, canonical prefixes, equality and hashing,
/// ranges as intervals of the key order, and the partition check (LG-I1's definition) against an independent oracle,
/// with splits and merges as metamorphic relations (verification.md §4.2).
/// </summary>
[NoPageLeakCheck("Key ranges are values; the tests rent no pages.")]
public sealed class KeyRangePropertyTests
{
    /// <summary>Pairs of ranges, half of them related: the second the first extended, or the first with a bit flipped.</summary>
    private static readonly Gen<(string Left, string Right)> RangePairs = Gen.Frequency(
        (2, Gen.Select(Ranges, Ranges)),
        (1, Gen.Select(Ranges, Gen.Bool.Array[0, 12]).Select(static pair => (pair.Item1, pair.Item1 + Text(pair.Item2)))),
        (1, Gen.Select(Ranges, Gen.Bool.Array[0, 12], Gen.Int.NonNegative).Select(static triple => (triple.Item1, Flip(triple.Item1 + Text(triple.Item2), triple.Item3)))));

    /// <summary>
    /// A range deeper than a byte and a key shorter than its prefix which repeats the prefix's leading bytes. The prefix's
    /// later bits are all 0, so the range holds the key, whose bits past its end are 0, or are any bits, often not all 0.
    /// </summary>
    private static readonly Gen<(string Range, byte[] Key)> ShortKeysOfLongerPrefixes =
        Gen.Select(Gen.Int[9, 72], Gen.Bool.Array[72], Gen.Int.NonNegative, Gen.Bool).Select(static sample =>
        {
            var (depth, bits, choice, laterBitsZero) = sample;
            var keyLength = 1 + choice % ((depth - 1) / 8);
            var range = Text([.. bits.Take(depth).Select((bit, index) => bit && (!laterBitsZero || index < 8 * keyLength))]);
            return (range, KeyIn(range, [])[..keyLength]);
        });

    /// <summary>
    /// A range with a key in it or near it: its bits, possibly with one flipped, then any tail; or a key shorter than the
    /// range's prefix which repeats its leading bytes.
    /// </summary>
    private static readonly Gen<(string Range, byte[] Key)> RangesAndKeys = Gen.Frequency(
        (1, Gen.Select(Ranges, Keys)),
        (2, Gen.Select(Ranges, Keys, Gen.Int.NonNegative, Gen.Bool).Select(static sample =>
            (sample.Item1, KeyIn(sample.Item4 ? Flip(sample.Item1, sample.Item3) : sample.Item1, sample.Item2)))),
        (1, ShortKeysOfLongerPrefixes));

    [Fact]
    public void ARangeContainsExactlyTheKeysWhoseLeadingBitsAreItsPrefix() =>
        RangesAndKeys.Sample(static (bits, key) =>
        {
            var range = ToRange(bits);
            Assert.Equal(bits, Of(range));
            Assert.Equal(KeyBits.Contains(bits, key), range.Contains(key));
        });

    /// <summary>
    /// Two ranges overlap exactly when one contains the other, overlapping ranges share a key, and a key of one of two
    /// disjoint ranges is not in the other.
    /// </summary>
    [Fact]
    public void RangesOverlapExactlyWhenOneContainsTheOther() =>
        Gen.Select(RangePairs, Keys).Sample(static (pair, tail) =>
        {
            var (left, right) = (ToRange(pair.Left), ToRange(pair.Right));
            Assert.Equal(Covers(pair.Left, pair.Right), left.Contains(right));
            Assert.Equal(Covers(pair.Right, pair.Left), right.Contains(left));
            Assert.Equal(!Disjoint(pair.Left, pair.Right), left.Overlaps(right));
            Assert.Equal(left.Overlaps(right), right.Overlaps(left));

            var leftKey = KeyIn(pair.Left, tail);
            var rightKey = KeyIn(pair.Right, tail);
            if (left.Overlaps(right))
            {
                var deeper = pair.Left.Length >= pair.Right.Length ? leftKey : rightKey;
                Assert.True(left.Contains(deeper) && right.Contains(deeper));
            }
            else
            {
                Assert.False(right.Contains(leftKey));
                Assert.False(left.Contains(rightKey));
            }
        });

    /// <summary>
    /// Containment is a partial order, and a key of a range is a key of every range containing it.
    /// </summary>
    [Fact]
    public void ContainmentIsAPartialOrderWhichKeysFollow() =>
        Gen.Select(Ranges, Gen.Bool.Array[0, 10], Gen.Bool.Array[0, 10], Keys).Sample(static (outer, middle, inner, tail) =>
        {
            var a = ToRange(outer);
            var b = ToRange(outer + Text(middle));
            var c = ToRange(outer + Text(middle) + Text(inner));
            Assert.True(a.Contains(a));
            Assert.True(a.Contains(b) && b.Contains(c) && a.Contains(c));
            Assert.Equal(middle.Length == 0, b.Contains(a));
            Assert.Equal(a == b, a.Contains(b) && b.Contains(a));

            var key = KeyIn(Of(c), tail);
            Assert.True(c.Contains(key) && b.Contains(key) && a.Contains(key));
        });

    /// <summary>
    /// A range's children are siblings, each its parent's child, which split its keys between them; a sibling's sibling
    /// is the range itself.
    /// </summary>
    [Fact]
    public void ChildrenSplitTheirParentsKeysBetweenThem() =>
        Gen.Select(Ranges, Keys).Sample(static (bits, tail) =>
        {
            var range = ToRange(bits);
            var (lower, upper) = range.Children;
            Assert.Equal(bits + "0", Of(lower));
            Assert.Equal(bits + "1", Of(upper));
            Assert.Equal(range, lower.Parent);
            Assert.Equal(range, upper.Parent);
            Assert.Equal(upper, lower.Sibling);
            Assert.Equal(lower, upper.Sibling);
            Assert.Equal(lower, lower.Sibling.Sibling);
            Assert.False(lower.Overlaps(upper));
            Assert.True(range.Contains(lower) && range.Contains(upper));

            var key = KeyIn(bits, tail);
            Assert.True(lower.Contains(key) ^ upper.Contains(key));
            if (bits.Length > 0)
            {
                Assert.Equal(bits[..^1], Of(range.Parent));
                Assert.Equal(bits[..^1] + (bits[^1] == '0' ? '1' : '0'), Of(range.Sibling));
            }
        });

    /// <summary>
    /// Construction rejects a prefix with a bit set past its depth or of another length; equal ranges are those of equal
    /// depth and bits, which hash alike; and <see cref="KeyRange.GetContainingHashCode"/> is the hash code of the range it
    /// names, which contains its key.
    /// </summary>
    [Fact]
    public void RangesAreCanonicalEqualByValueAndHashAlike() =>
        Gen.Select(RangePairs, Keys, Gen.Int[0, 7]).Sample(static (pair, key, extra) =>
        {
            var (left, right) = (ToRange(pair.Left), ToRange(pair.Right));
            Assert.Equal(pair.Left == pair.Right, left == right);
            Assert.Equal(left == right, left.Equals((object)right));
            if (left == right)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }

            var prefix = left.Prefix.ToArray();
            if (left.Depth % 8 != 0)
            {
                var unused = prefix.ToArray();
                unused[^1] |= (byte)(1 << Math.Min(extra, 7 - left.Depth % 8));
                _ = Assert.Throws<ArgumentException>(() => new KeyRange(unused, left.Depth));
            }

            _ = Assert.Throws<ArgumentException>(() => new KeyRange([.. prefix, 0], left.Depth));
            if (prefix.Length > 0)
            {
                _ = Assert.Throws<ArgumentException>(() => new KeyRange(prefix[..^1], left.Depth));
            }

            var depth = Math.Min(left.Depth, key.Length * 8 + extra);
            var containing = KeyRange.Containing(key, depth);
            Assert.True(containing.Contains(key));
            Assert.Equal(new string([.. Enumerable.Range(0, depth).Select(index => (char)('0' + Bit(key, index)))]), Of(containing));
            Assert.Equal(containing.GetHashCode(), KeyRange.GetContainingHashCode(key, depth));
        });

    /// <summary>
    /// A range holds exactly the keys between its bounds in the key order: its prefix padded with 0 bits, without trailing
    /// zero bytes, and its prefix padded with 1 bits to the longest key. Each bound and its neighbors in the key order,
    /// and keys in the range and near it, are checked against the bounds and against <see cref="KeyBits"/>.
    /// </summary>
    [Fact]
    public void ARangeHoldsExactlyTheKeysBetweenItsBounds() =>
        Gen.Select(Ranges, Keys).Sample(static (bits, tail) =>
        {
            var range = ToRange(bits);
            var lowest = LowestKey(bits);
            var highest = HighestKey(bits);
            byte[]?[] keys = [lowest, highest, Predecessor(lowest), Successor(lowest), Predecessor(highest), Successor(highest), KeyIn(bits, tail), Near(bits, tail)];
            foreach (var key in keys.OfType<byte[]>())
            {
                var between = key.AsSpan().SequenceCompareTo(lowest) >= 0 && key.AsSpan().SequenceCompareTo(highest) <= 0;
                Assert.Equal(between, KeyBits.Contains(bits, key));
                Assert.Equal(between, range.Contains(key));
            }
        });

    /// <summary>The keys of a range form an interval of the key order.</summary>
    [Fact]
    public void ARangesKeysFormAnIntervalOfTheKeyOrder() =>
        Gen.Select(Ranges, Keys, Keys, Keys).Sample(static (bits, first, second, third) =>
        {
            var range = ToRange(bits);
            byte[][] keys = [KeyIn(bits, first), Near(bits, second), KeyIn(bits, third)];
            Array.Sort(keys, static (left, right) => left.AsSpan().SequenceCompareTo(right));
            if (range.Contains(keys[0]) && range.Contains(keys[2]))
            {
                Assert.True(range.Contains(keys[1]));
            }
        });

    /// <summary>
    /// <see cref="KeyRange.IsPartition"/> agrees with <see cref="KeyBits.IsPartition"/>, pairwise disjoint ranges whose
    /// measures sum to 1, over lists biased toward near-partitions; <c>classify</c> reports each kind of list.
    /// </summary>
    [Fact]
    public void ThePartitionCheckAgreesWithTheOracleOnNearPartitions()
    {
        var kinds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        NearPartitions.Sample(
            ranges =>
            {
                var oracle = KeyBits.IsPartition(ranges);
                Assert.Equal(oracle, KeyRange.IsPartition([.. ranges.Select(ToRange)]));
                var kind = Classify(ranges, oracle);
                _ = kinds.AddOrUpdate(kind, 1, static (_, count) => count + 1);
                return kind;
            },
            TestDiagnostics.WriteLine,
            iter: 2_000);

        Assert.Equal(["gap", "overlap", "overlap whose measures sum to 1", "partition", "repeat"], kinds.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Splitting a member or merging two sibling members keeps a partition, a split then its merge restores it, and any
    /// order of its members gives the same answer; removing a member, adding any range, or replacing a member with
    /// another range of its depth breaks it, so a check of the measures alone fails. Every key lies in exactly one
    /// member.
    /// </summary>
    [Fact]
    public void SplitsAndMergesKeepAPartitionWhichEveryOtherChangeBreaks() =>
        Gen.Select(Partitions, Gen.Int.NonNegative, Ranges, Keys).Sample(static (members, choice, other, key) =>
        {
            KeyRange[] partition = [.. members.Select(ToRange)];
            var index = choice % partition.Length;
            Assert.True(KeyRange.IsPartition(partition));
            Assert.Single(partition, member => member.Contains(key));
            Assert.True(KeyRange.IsPartition([.. partition.Reverse()]));

            var (lower, upper) = partition[index].Children;
            KeyRange[] split = [.. partition.Where((_, at) => at != index), upper, lower];
            Assert.True(KeyRange.IsPartition(split));
            KeyRange[] merged = [.. split.Where(member => member != lower && member != upper), lower.Parent];
            Assert.True(KeyRange.IsPartition(merged));
            Assert.Equal(partition.Select(Of).Order(StringComparer.Ordinal), merged.Select(Of).Order(StringComparer.Ordinal));

            // The deepest member's sibling is a member too.
            var deepest = partition.MaxBy(static member => member.Depth);
            if (deepest.Depth > 0)
            {
                Assert.Contains(deepest.Sibling, partition);
                Assert.True(KeyRange.IsPartition([.. partition.Where(member => member != deepest && member != deepest.Sibling), deepest.Parent]));
            }

            Assert.False(KeyRange.IsPartition([.. partition.Where((_, at) => at != index)]));
            Assert.False(KeyRange.IsPartition([.. partition, ToRange(other)]));
            var replacement = ToRange(Flip(Of(partition[index]), choice));
            if (partition[index].Depth > 0)
            {
                Assert.False(KeyRange.IsPartition([.. partition.Where((_, at) => at != index), replacement]));
            }
        });

    /// <summary>
    /// Gets full binary tries in random order: leaves split at random from the whole keyspace, or a spine, every sibling
    /// along a random path from the whole keyspace to a depth of 60 to 600, which crosses the 64 bits a hash covers and
    /// compares long prefixes.
    /// </summary>
    private static Gen<string[]> Partitions { get; } =
        Gen.Frequency(
            (3, Gen.Int.NonNegative.Array[0, 24].Select(static choices =>
            {
                var leaves = new List<string> { string.Empty };
                foreach (var choice in choices)
                {
                    var index = choice % leaves.Count;
                    var leaf = leaves[index];
                    leaves.RemoveAt(index);
                    leaves.Add(leaf + "0");
                    leaves.Add(leaf + "1");
                }

                return leaves.ToArray();
            })),
            (1, Gen.Int[60, 600].SelectMany(static depth => Gen.Bool.Array[depth]).Select(static path =>
            {
                var bits = Text(path);
                return Enumerable.Range(0, bits.Length).Select(depth => Flip(bits[..(depth + 1)], depth)).Append(bits).ToArray();
            })))
        .SelectMany(static leaves => Gen.Shuffle(leaves));

    /// <summary>
    /// Gets partitions as they are, or with a member removed, a range added, a member repeated, or a member replaced with
    /// another of its depth.
    /// </summary>
    private static Gen<string[]> NearPartitions { get; } =
        Gen.Select(Partitions, Gen.Enum<PartitionChange>(), Gen.Int.NonNegative, Ranges).Select(static (members, change, choice, other) =>
        {
            var index = choice % members.Length;
            return change switch
            {
                PartitionChange.None => members,
                PartitionChange.Remove => [.. members.Where((_, at) => at != index)],
                PartitionChange.Add => [.. members, other],
                PartitionChange.Repeat => [.. members, members[index]],
                PartitionChange.Replace => [.. members.Where((_, at) => at != index), Flip(members[index], choice)],
                _ => throw new UnreachableException(),
            };
        });

    private enum PartitionChange
    {
        None,
        Remove,
        Add,
        Repeat,
        Replace,
    }

    private static string Classify(string[] ranges, bool partition)
    {
        if (partition)
        {
            return "partition";
        }

        if (ranges.Distinct(StringComparer.Ordinal).Count() < ranges.Length)
        {
            return "repeat";
        }

        if (PairwiseDisjoint(ranges))
        {
            return "gap";
        }

        return MeasuresSumToOne(ranges) ? "overlap whose measures sum to 1" : "overlap";
    }

    private static string Flip(string bits, int choice)
    {
        if (bits.Length == 0)
        {
            return bits;
        }

        var flipped = bits.ToCharArray();
        var at = choice % flipped.Length;
        flipped[at] = flipped[at] == '0' ? '1' : '0';
        return new string(flipped);
    }

    /// <summary>A key in the range or beside it: its bits with one flipped, then any tail.</summary>
    private static byte[] Near(string bits, byte[] tail) => KeyIn(tail.Length % 2 == 0 ? bits : Flip(bits, tail[0]), tail);

    private static string Text(bool[] bits) => new([.. bits.Select(static bit => bit ? '1' : '0')]);
}
