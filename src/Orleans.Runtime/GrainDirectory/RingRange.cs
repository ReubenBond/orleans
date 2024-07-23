using System;
using System.Collections.Generic;
using System.Diagnostics;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[GenerateSerializer, Immutable]
[Alias(nameof(RingRange))]
internal readonly struct RingRange(uint start, uint end) : IEquatable<RingRange>, ISpanFormattable
{
    [Id(0)]
    private readonly uint _start = start == end && start > 1 ? 1 : start;

    [Id(1)]
    private readonly uint _end = start == end && start > 1 ? 1 : end;

    public bool IsEmpty => _start == _end && _start == 0;

    public bool IsFull => _start == _end && _start != 0;

    private bool IsWrapped => _start >= _end;

    public static RingRange Full { get; } = new RingRange(1, 1);

    public static RingRange Empty { get; } = new RingRange(0, 0);

    public uint Start => IsFull ? 0 : _start;

    public uint End => IsFull ? 0 : _end;

    public static RingRange CreateEquallyDividedRange(int count, int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return Core((uint)count, (uint)index);
        static RingRange Core(uint count, uint index)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));

            if (count == 1 && index == 0)
            {
                return Full;
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
                    return new RingRange(start, end);
                }

                start = end;
            }

            throw new ArgumentException(null, nameof(index));
        }
    }

    /// <summary>
    /// Gets the length of the range.
    /// </summary>
    public uint Length
    {
        get
        {
            if (_start == _end)
            {
                // Empty
                if (_start == 0) return 0;

                // Full
                return uint.MaxValue;
            }

            // Normal
            if (_end > _start) return _end - _start;

            // Wrapped
            return uint.MaxValue - _start + _end;
        }
    }

    internal int Compare(uint n)
    {
        if (Contains(n))
        {
            return 0;
        }

        var start = Start;
        if (start >= End)
        {
            // Start > End (wrap-around case)
            if (n <= start)
            {
                // Range starts after N (range > N)
                return -1;
            }

            // n > _end
            // Range starts & ends before N (range < N)
            return 1;
        }

        if (n <= start)
        {
            // Range starts after N (range > N)
            return 1;
        }

        // n > _end
        // Range starts & ends before N (range < N)
        return -1;
    }

    /// <summary>
    /// Checks if n is element of (Start, End], while remembering that the ranges are on a ring
    /// </summary>
    /// <returns>true if n is in (Start, End], false otherwise</returns>
    internal bool Contains(GrainId grainId) => Contains(grainId.GetUniformHashCode());

    /// <summary>
    /// checks if n is element of (Start, End], while remembering that the ranges are on a ring
    /// </summary>
    /// <param name="point"></param>
    /// <returns>true if n is in (Start, End], false otherwise</returns>
    public bool Contains(uint point)
    {
        if (IsEmpty)
        {
            return false;
        }

        var num = point;
        if (Start < End)
        {
            return num > Start && num <= End;
        }

        // Start > End
        return num > Start || num <= End;
    }

    public float SizePercent => Length * (100.0f / uint.MaxValue);

    public bool Equals(RingRange other) => _start == other._start && _end == other._end;

    public override bool Equals(object? obj) => obj is RingRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_start, _end);

    public override string ToString() => IsFull
        ? $"(0, 0], Size=0x{Length:X8} (100.0%)"
        : $"(0x{Start:X8}, 0x{End:X8}], Size=0x{Length:X8} ({SizePercent:0.0}%)";

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        return IsEmpty
            ? destination.TryWrite($"(0 0), Size=0x0 (0%)", out charsWritten)
            : IsFull
                ? destination.TryWrite($"(0, 0], Size=0x{uint.MaxValue:X8} (100%)", out charsWritten)
                : destination.TryWrite($"(0x{Start:X8}, 0x{End:X8}], Size=0x{Length:X8} ({SizePercent:0.0}%)", out charsWritten);
    }

    internal bool Overlaps(RingRange other) => !IsEmpty && !other.IsEmpty && (Equals(other) || Contains(other.End) || other.Contains(End));

    internal RingRange Merge(RingRange other)
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

        if (Contains(other.Start))
        {
            return MergeEnds(other);
        }

        if (other.Contains(Start))
        {
            return other.MergeEnds(this);
        }

        throw new InvalidOperationException("Ranges don't overlap");
    }

    internal RingRange Inverse()
    {
        if (IsEmpty)
        {
            return Full;
        }

        if (IsFull)
        {
            return Empty;
        }

        return new RingRange(End, Start);
    }

    internal IEnumerable<RingRange> Intersections(RingRange other)
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
        else if (other.IsFull)
        {
            yield return this;
        }
        else if (IsWrapped ^ other.IsWrapped)
        {
            var wrapped = IsWrapped ? this : other;
            var normal = IsWrapped ? other : this;
            var (normalStart, normalEnd) = (normal.Start, normal.End);
            var (wrappedStart, wrappedEnd) = (wrapped.Start, wrapped.End);

            // There are possibly two intersections, between the normal and wrapped range.
            //         low         high
            // ...---NB====WE----WB====NE----...

            // Intersection at the low side.
            if (wrappedEnd > normalStart)
            {
                // ---NB====WE---
                yield return new RingRange(normalStart, wrappedEnd);
            }

            // Intersection at the high side.
            if (wrappedStart < normalEnd)
            {
                // ---WB====NE---
                yield return new RingRange(wrappedStart, normalEnd);
            }
        }
        else
        {
            yield return new RingRange(Math.Max(Start, other.Start), Math.Min(End, other.End));
        }
    }

    // Gets the sub-ranges which are in this range but are not in the 'previous' range.
    internal IEnumerable<RingRange> GetAdditions(RingRange previous)
    {
        // Additions are the intersections between this range and the inverse of the previous range.
        foreach (var addition in Intersections(previous.Inverse()))
        {
            Debug.Assert(!addition.Overlaps(previous));
            Debug.Assert(addition.Overlaps(this));
            yield return addition;
        }
    }

    // Gets the sub-ranges which are not in this range but are in the 'previous' range.
    internal IEnumerable<RingRange> GetRemovals(RingRange previous)
    {
        // Removals are the intersections between the inverse of this range and the previous range.
        foreach (var removal in Inverse().Intersections(previous))
        {
            Debug.Assert(removal.Overlaps(previous));
            Debug.Assert(!removal.Overlaps(this));
            yield return removal;
        }
    }

    // other range starts inside this range, merge it based on where it ends
    private RingRange MergeEnds(RingRange other)
    {
        var (start, end) = (Start, End);
        var (otherStart, otherEnd) = (other.Start, other.End);
        if (start == otherEnd)
        {
            return Full;
        }

        if (!Contains(otherEnd))
        {
            return new RingRange(start, otherEnd);
        }

        if (other.Contains(Start))
        {
            return Full;
        }

        return this;
    }

    public static bool operator ==(RingRange left, RingRange right) => left.Equals(right);

    public static bool operator !=(RingRange left, RingRange right) => !(left == right);
}