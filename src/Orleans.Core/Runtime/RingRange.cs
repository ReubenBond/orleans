using System;
using System.Collections.Generic;
using System.Diagnostics;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a range or set of ranges around a virtual ring where points along the ring are identified using <see cref="uint"/> values.
    /// </summary>
    public interface IRingRange
    {
        /// <summary>
        /// Returns a value indicating whether <paramref name="value"/> is within this ring range.
        /// </summary>
        /// <param name="value">
        /// The value to check.
        /// </param>
        /// <returns><see langword="true"/> if the reminder is in our responsibility range, <see langword="false"/> otherwise.</returns>
        bool Contains(uint value);

        /// <summary>
        /// Returns a value indicating whether <paramref name="grainId"/> is within this ring range.
        /// </summary>
        /// <param name="grainId">The value to check.</param>
        /// <returns><see langword="true"/> if the reminder is in our responsibility range, <see langword="false"/> otherwise.</returns>
        public sealed bool InRange(GrainId grainId) => Contains(grainId.GetUniformHashCode());
    }

    // This is the internal interface to be used only by the different range implementations.
    internal interface IRingRangeInternal : IRingRange
    {
        double RangePercentage();
    }

    /// <summary>
    /// Represents a single, contiguous range round a virtual ring where points along the ring are identified using <see cref="uint"/> values.
    /// </summary>
    /// <seealso cref="IRingRange" />
    public interface ISingleRange : IRingRange
    {
        /// <summary>
        /// Gets the exclusive lower bound of the range.
        /// </summary>
        uint Begin { get; }

        /// <summary>
        /// Gets the inclusive upper bound of the range.
        /// </summary>
        uint End { get; }
    }

    internal sealed class EmptyRange : IRingRangeInternal
    {
        public static readonly EmptyRange Instance = new();
        public bool Contains(uint value) => false;
        public double RangePercentage() => 0.0;
    }

    [Serializable, GenerateSerializer, Immutable]
    [Alias("Orleans.Runtime.SingleRange")]
    internal sealed class SingleRange(uint begin, uint end) : IRingRangeInternal, IEquatable<SingleRange>, ISingleRange, ISpanFormattable
    {
        [Id(0)]
        private readonly uint _begin = begin == end && begin > 1 ? 1 : begin;

        [Id(1)]
        private readonly uint _end = begin == end && begin > 1 ? 1 : end;

        public bool IsEmpty => _begin == _end && _begin == 0;
        public bool IsFull => _begin == _end && _begin != 0;
        private bool IsWrapped => _begin >= _end;

        public static SingleRange Full { get; } = new SingleRange(1, 1);
        public static SingleRange Empty { get; } = new SingleRange(0, 0);

        /// <summary>
        /// Exclusive
        /// </summary>
        public uint Begin => _begin;

        /// <summary>
        /// Inclusive
        /// </summary>
        public uint End => _end;

        /// <summary>
        /// Gets the length of the range.
        /// </summary>
        public uint Length
        {
            get
            {
                if (_begin == _end)
                {
                    // Empty
                    if (_begin == 0) return 0;

                    // Full
                    return uint.MaxValue;
                }

                // Normal
                if (_end > _begin) return _end - _begin;

                // Wrapped
                return uint.MaxValue - _begin + _end;
            }
        }

        public int Compare(uint n)
        {
            if (Contains(n))
            {
                return 0;
            }

            if (_begin >= _end)
            {
                // Begin > End (wrap-around case)
                if (n <= _begin)
                {
                    // Range starts after N (range > N)
                    return -1;
                }

                // n > _end
                // Range starts & ends before N (range < N)
                return 1;
            }

            if (n <= _begin)
            {
                // Range starts after N (range > N)
                return 1;
            }

            // n > _end
            // Range starts & ends before N (range < N)
            return -1;
        }

        /// <summary>
        /// checks if n is element of (Begin, End], while remembering that the ranges are on a ring
        /// </summary>
        /// <param name="point"></param>
        /// <returns>true if n is in (Begin, End], false otherwise</returns>
        public bool Contains(uint point)
        {
            if (IsEmpty)
            {
                return false;
            }

            uint num = point;
            if (_begin < _end)
            {
                return num > _begin && num <= _end;
            }

            // Begin > End
            return num > _begin || num <= _end;
        }

        public double RangePercentage() => Length * (100.0 / uint.MaxValue);

        public bool Equals(SingleRange? other) => other != null && _begin == other._begin && _end == other._end;

        public override bool Equals(object? obj) => Equals(obj as SingleRange);

        public override int GetHashCode() => HashCode.Combine(GetType(), _begin, _end);

        public override string ToString() => _begin == 0 && _end == 0 ? "<(0 0], Size=x100000000, %Ring=100%>" : $"{this}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            return _begin == 0 && _end == 0
                ? destination.TryWrite($"<(0 0], Size=x100000000, %Ring=100%>", out charsWritten)
                : destination.TryWrite($"<(x{_begin:X8} x{_end:X8}], Size=x{Length:X8}, %Ring={RangePercentage():0.000}%>", out charsWritten);
        }

        internal bool Overlaps(SingleRange other) => !IsEmpty && !other.IsEmpty && (Equals(other) || Contains(other._begin) || other.Contains(_begin));

        internal SingleRange Merge(SingleRange other)
        {
            if (Equals(other))
            {
                return this;
            }

            if (IsEmpty || other.IsFull)
            {
                return other;
            }

            if (IsFull || other.IsEmpty)
            {
                return this;
            }

            if (Contains(other._begin))
            {
                return MergeEnds(other);
            }

            if (other.Contains(_begin))
            {
                return other.MergeEnds(this);
            }

            throw new InvalidOperationException("Ranges don't overlap");
        }

        public SingleRange Inverse()
        {
            if (IsEmpty)
            {
                return Full;
            }

            if (IsFull)
            {
                return Empty;
            }

            return new SingleRange(_end, _begin);
        }

        public IEnumerable<SingleRange> Intersections(SingleRange other)
        {
            if (!Overlaps(other))
            {
                // No intersections.
                yield break;
            }

            if (IsFull)
            {
                // One intersection, the other range.
                yield return other;
            }
            else if (IsWrapped ^ other.IsWrapped)
            {
                var wrapped = IsWrapped ? this : other;
                var normal = IsWrapped ? other : this;

                // There are possibly two intersections, between the normal and wrapped range.
                // ...---NB====WE----WB====NE----...

                // Intersection at the low side.
                if (wrapped._end > normal._begin)
                {
                    // ---NB====WE---
                    yield return new SingleRange(normal._begin, wrapped._end);
                }

                // Intersection at the high side.
                if (wrapped._begin < normal._end)
                {
                    // ---WB====NE---
                    yield return new SingleRange(wrapped._begin, normal._end);
                }
            }
            else
            {
                yield return new SingleRange(Math.Max(_begin, other._begin), Math.Min(_end, other._end));
            }
        }

        // Gets the sub-ranges which are in this range but are not in the 'previous' range.
        public IEnumerable<SingleRange> GetAdditions(SingleRange previous)
        {
            if (!Overlaps(previous))
            {
                // This entire sub-range is new.
                yield return this;
                yield break;
            }

            if (Equals(previous))
            {
                // Nothing was added.
                yield break;
            }

            // Extensions to the beginning
            // Extensions to the end

            if (IsWrapped)
            {
                if (previous.IsWrapped)
                {
                    // Both wrapped.


                    yield break;
                }

                // This is wrapped.
                yield break;
            }

            if (previous.IsWrapped)
            {
                // Previous is wrapped.
                if (_begin < previous._begin && _end > previous._end)
                {
                    // Some prefix or suffix was added.
                    var newBegin = Math.Max(_begin, previous.End);
                    var newEnd = Math.Min(_end, previous.Begin);
                    yield return new SingleRange(newBegin, newEnd);
                }

                yield break;
            }


            // None wrapped
            if (_begin < previous._begin)
            {
                // A prefix was added.
                yield return new SingleRange(_begin, previous._begin);
            }

            if (_end > previous._end)
            {
                // A suffix was added.
                yield return new SingleRange(previous._end, _end);
            }
        }

        // Gets the sub-ranges which are not in this range but are in the 'previous' range.
        public IEnumerable<SingleRange> GetRemovals(SingleRange previous)
        {
            if (!Overlaps(previous))
            {
                // The entire previous sub-range was removed.
                yield return previous;
                yield break;
            }

            if (Equals(previous))
            {
                // Nothing was removed.
                yield break;
            }

            // Removals from the beginning

            // Removals from the end
        }

        // other range begins inside this range, merge it based on where it ends
        private SingleRange MergeEnds(SingleRange other)
        {
            if (_begin == other._end)
            {
                return RangeFactory.FullRange;
            }

            if (!Contains(other._end))
            {
                return new SingleRange(_begin, other._end);
            }

            if (other.Contains(_begin))
            {
                return RangeFactory.FullRange;
            }

            return this;
        }
    }

    /// <summary>
    /// Utility class for creating <see cref="IRingRange" /> values.
    /// </summary>
    public static class RangeFactory
    {
        /// <summary>
        /// The ring size.
        /// </summary>
        public const long RING_SIZE = (long)uint.MaxValue + 1;

        /// <summary>
        /// Represents an empty range.
        /// </summary>
        private static readonly GeneralMultiRange EmptyRange = new(new());

        /// <summary>
        /// Represents a full range.
        /// </summary>
        internal static readonly SingleRange FullRange = new(0, 0);

        /// <summary>
        /// Creates the full range.
        /// </summary>
        /// <returns>IRingRange.</returns>
        public static IRingRange CreateFullRange() => FullRange;

        /// <summary>
        /// Creates a new <see cref="IRingRange"/> representing the values between the exclusive lower bound, <paramref name="begin"/>, and the inclusive upper bound, <paramref name="end"/>.
        /// </summary>
        /// <param name="begin">The exclusive lower bound.</param>
        /// <param name="end">The inclusive upper bound.</param>
        /// <returns>A new <see cref="IRingRange"/> representing the values between the exclusive lower bound, <paramref name="begin"/>, and the inclusive upper bound, <paramref name="end"/>.</returns>
        public static IRingRange CreateRange(uint begin, uint end) => new SingleRange(begin, end);

        /// <summary>
        /// Creates a new <see cref="IRingRange"/> representing the union of all provided ranges.
        /// </summary>
        /// <param name="inRanges">The ranges.</param>
        /// <returns>A new <see cref="IRingRange"/> representing the union of all provided ranges.</returns>
        public static IRingRange CreateRange(List<IRingRange> inRanges) => inRanges.Count switch
        {
            0 => EmptyRange,
            1 => inRanges[0],
            _ => GeneralMultiRange.Create(inRanges)
        };

        /// <summary>
        /// Creates equally divided sub-ranges from the provided range and returns one sub-range from that range.
        /// </summary>
        /// <param name="range">The range.</param>
        /// <param name="numSubRanges">The number of sub-ranges.</param>
        /// <param name="mySubRangeIndex">The index of the sub-range to return.</param>
        /// <returns>The identified sub-range.</returns>
        internal static IRingRange GetEquallyDividedSubRange(IRingRange range, int numSubRanges, int mySubRangeIndex)
            => EquallyDividedMultiRange.GetEquallyDividedSubRange(range, numSubRanges, mySubRangeIndex);

        /// <summary>
        /// Creates equally divided sub-ranges from the provided range and returns one sub-range from that range.
        /// </summary>
        /// <param name="range">The range.</param>
        /// <param name="numSubRanges">The number of sub-ranges.</param>
        /// <param name="mySubRangeIndex">The index of the sub-range to return.</param>
        /// <returns>The identified sub-range.</returns>
        internal static SingleRange GetEquallyDividedSubRange(SingleRange range, int numSubRanges, int mySubRangeIndex)
            => EquallyDividedMultiRange.GetEquallyDividedSubRange(range, numSubRanges, mySubRangeIndex);

        /// <summary>
        /// Gets the contiguous sub-ranges represented by the provided range.
        /// </summary>
        /// <param name="range">The range.</param>
        /// <returns>The contiguous sub-ranges represented by the provided range.</returns>
        public static IEnumerable<ISingleRange> GetSubRanges(IRingRange range) => range switch
        {
            ISingleRange single => new[] { single },
            GeneralMultiRange m => m.Ranges,
            _ => throw new NotSupportedException(),
        };
    }

    [Serializable, GenerateSerializer, Immutable]
    internal sealed class GeneralMultiRange : IRingRangeInternal, ISpanFormattable
    {
        [Id(0)]
        private readonly List<SingleRange> ranges;

        [Id(1)]
        private readonly long rangeSize;

        internal List<SingleRange> Ranges => ranges;

        internal GeneralMultiRange(List<SingleRange> ranges)
        {
            Debug.Assert(ranges.Count != 1);
            this.ranges = ranges;
            foreach (var r in ranges)
                rangeSize += r.Length;
        }

        internal static IRingRange Create(List<IRingRange> inRanges)
        {
            var ranges = inRanges.ConvertAll(r => (SingleRange)r);
            return HasOverlaps() ? Compact() : new GeneralMultiRange(ranges);

            bool HasOverlaps()
            {
                var last = ranges[0];
                for (var i = 1; i < ranges.Count; i++)
                {
                    if (last.Overlaps(last = ranges[i])) return true;
                }

                return false;
            }

            IRingRange Compact()
            {
                var lastIdx = 0;
                var last = ranges[0];
                for (var i = 1; i < ranges.Count; i++)
                {
                    var r = ranges[i];
                    if (last.Overlaps(r)) ranges[lastIdx] = last = last.Merge(r);
                    else ranges[++lastIdx] = last = r;
                }
                if (lastIdx == 0) return last;
                ranges.RemoveRange(++lastIdx, ranges.Count - lastIdx);
                return new GeneralMultiRange(ranges);
            }
        }

        public bool Contains(uint n)
        {
            foreach (var s in ranges)
            {
                if (s.Contains(n)) return true;
            }
            return false;
        }

        public double RangePercentage() => rangeSize * (100.0 / RangeFactory.RING_SIZE);

        public override string ToString() => ranges.Count == 0 ? "Empty MultiRange" : $"{this}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            return ranges.Count == 0
                ? destination.TryWrite($"Empty MultiRange", out charsWritten)
                : destination.TryWrite($"<MultiRange: Size=x{rangeSize:X8}, %Ring={RangePercentage():0.000}%>", out charsWritten);
        }
    }

    internal static class EquallyDividedMultiRange
    {
        public static SingleRange GetEquallyDividedSubRange(SingleRange singleRange, int numSubRanges, int mySubRangeIndex)
        {
            var rangeSize = singleRange.Length;
            uint portion = (uint)(rangeSize / numSubRanges);
            uint remainder = (uint)(rangeSize - portion * numSubRanges);
            uint start = singleRange.Begin;
            for (int i = 0; i < numSubRanges; i++)
            {
                // (Begin, End]
                uint end = unchecked(start + portion);
                // I want it to overflow on purpose. It will do the right thing.
                if (remainder > 0)
                {
                    end++;
                    remainder--;
                }
                if (i == mySubRangeIndex)
                {
                    return new SingleRange(start, end);
                }

                start = end; // nextStart
            }
            throw new ArgumentException(nameof(mySubRangeIndex));
        }

        // Takes a range and divides it into numSubRanges equal ranges and returns the subrange at mySubRangeIndex.
        public static IRingRange GetEquallyDividedSubRange(IRingRange range, int numSubRanges, int mySubRangeIndex)
        {
            if (numSubRanges <= 0) throw new ArgumentOutOfRangeException(nameof(numSubRanges));
            if ((uint)mySubRangeIndex >= (uint)numSubRanges) throw new ArgumentOutOfRangeException(nameof(mySubRangeIndex));

            if (numSubRanges == 1) return range;

            switch (range)
            {
                case SingleRange singleRange:
                    return GetEquallyDividedSubRange(singleRange, numSubRanges, mySubRangeIndex);

                case GeneralMultiRange multiRange:
                    switch (multiRange.Ranges.Count)
                    {
                        case 0: return multiRange;
                        default:
                            // Take each of the single ranges in the multi range and divide each into equal sub ranges.
                            var singlesForThisIndex = new List<SingleRange>(multiRange.Ranges.Count);
                            foreach (var singleRange in multiRange.Ranges)
                                singlesForThisIndex.Add(GetEquallyDividedSubRange(singleRange, numSubRanges, mySubRangeIndex));
                            return new GeneralMultiRange(singlesForThisIndex);
                    }

                default: throw new ArgumentOutOfRangeException(nameof(range));
            }
        }
    }
}

