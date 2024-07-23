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
        bool InRange(uint value);

        /// <summary>
        /// Returns a value indicating whether <paramref name="grainId"/> is within this ring range.
        /// </summary>
        /// <param name="grainId">The value to check.</param>
        /// <returns><see langword="true"/> if the reminder is in our responsibility range, <see langword="false"/> otherwise.</returns>
        public sealed bool InRange(GrainId grainId) => InRange(grainId.GetUniformHashCode());
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

    [Serializable, GenerateSerializer, Immutable]
    internal sealed class SingleRange(uint begin, uint end) : IRingRangeInternal, IEquatable<SingleRange>, ISingleRange, ISpanFormattable
    {
        [Id(0)]
        private readonly uint _begin = begin;

        [Id(1)]
        private readonly uint _end = end;

        /// <summary>
        /// Exclusive
        /// </summary>
        public uint Begin => _begin;

        /// <summary>
        /// Inclusive
        /// </summary>
        public uint End => _end;

        /// <summary>
        /// checks if n is element of (Begin, End], while remembering that the ranges are on a ring
        /// </summary>
        /// <param name="n"></param>
        /// <returns>true if n is in (Begin, End], false otherwise</returns>
        public bool InRange(uint n)
        {
            uint num = n;
            if (_begin < _end)
            {
                return num > _begin && num <= _end;
            }

            // Begin > End
            return num > _begin || num <= _end;
        }

        public long RangeSize()
        {
            if (_begin < _end)
            {
                return _end - _begin;
            }

            return RangeFactory.RING_SIZE - (_begin - _end);
        }

        public double RangePercentage() => RangeSize() * (100.0 / RangeFactory.RING_SIZE);

        public bool Equals(SingleRange? other) => other != null && _begin == other._begin && _end == other._end;

        public override bool Equals(object? obj) => Equals(obj as SingleRange);

        public override int GetHashCode() => HashCode.Combine(GetType(), _begin, _end);

        public override string ToString() => _begin == 0 && _end == 0 ? "<(0 0], Size=x100000000, %Ring=100%>" : $"{this}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            return _begin == 0 && _end == 0
                ? destination.TryWrite($"<(0 0], Size=x100000000, %Ring=100%>", out charsWritten)
                : destination.TryWrite($"<(x{_begin:X8} x{_end:X8}], Size=x{RangeSize():X8}, %Ring={RangePercentage():0.000}%>", out charsWritten);
        }

        internal bool Overlaps(SingleRange other) => Equals(other) || InRange(other._begin) || other.InRange(_begin);

        internal SingleRange Merge(SingleRange other)
        {
            if ((_begin | _end) == 0 || (other._begin | other._end) == 0)
            {
                return RangeFactory.FullRange;
            }

            if (Equals(other))
            {
                return this;
            }

            if (InRange(other._begin))
            {
                return MergeEnds(other);
            }

            if (other.InRange(_begin))
            {
                return other.MergeEnds(this);
            }

            throw new InvalidOperationException("Ranges don't overlap");
        }

        // other range begins inside this range, merge it based on where it ends
        private SingleRange MergeEnds(SingleRange other)
        {
            if (_begin == other._end)
            {
                return RangeFactory.FullRange;
            }

            if (!InRange(other._end))
            {
                return new SingleRange(_begin, other._end);
            }

            if (other.InRange(_begin))
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
                rangeSize += r.RangeSize();
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

        public bool InRange(uint n)
        {
            foreach (var s in ranges)
            {
                if (s.InRange(n)) return true;
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
            var rangeSize = singleRange.RangeSize();
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

