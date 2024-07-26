using System;
using System.Collections.Generic;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

[GenerateSerializer, Immutable]
[Alias(nameof(RingRange))]
public readonly struct RingRange(uint start, uint end) : IEquatable<RingRange>, IRingRange, ISpanFormattable
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

    public static RingRange GetEquallyDividedSubRange(int count, int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count, nameof(index));

        var rangeSize = uint.MaxValue;
        var portion = (uint)(rangeSize / count);
        var remainder = (uint)(rangeSize - portion * count);
        var start = 0u;
        for (var i = 0; i < count; i++)
        {
            // (Start, End]
            var end = unchecked(start + portion);

            // Overflow on purpose. It will do the right thing.
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

        if (_start >= _end)
        {
            // Start > End (wrap-around case)
            if (n <= _start)
            {
                // Range starts after N (range > N)
                return -1;
            }

            // n > _end
            // Range starts & ends before N (range < N)
            return 1;
        }

        if (n <= _start)
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
        if (_start < _end)
        {
            return num > _start && num <= _end;
        }

        // Start > End
        return num > _start || num <= _end;
    }

    public float SizePercent => Length * (100.0f / uint.MaxValue);

    public bool Equals(RingRange other) => _start == other._start && _end == other._end;

    public override bool Equals(object? obj) => obj is RingRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_start, _end);

    public override string ToString() => IsFull
        ? $"(0, 0], Size=0x{Length:X8} (100.0%)"
        : $"(x{_start:X8}, x{_end:X8}], Size=0x{Length:X8} ({SizePercent:0.0}%)";

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        return IsEmpty
            ? destination.TryWrite($"(0 0), Size=0x0 (0%)", out charsWritten)
            : IsFull
                ? destination.TryWrite($"(0, 0], Size=0x{uint.MaxValue:X8} (100%)", out charsWritten)
                : destination.TryWrite($"(x{_start:X8}, x{_end:X8}], Size=0x{Length:X8} ({SizePercent:0.0}%)", out charsWritten);
    }

    internal bool Overlaps(RingRange other) => !IsEmpty && !other.IsEmpty && (Equals(other) || Contains(other._start) || other.Contains(_start));

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

        if (Contains(other._start))
        {
            return MergeEnds(other);
        }

        if (other.Contains(_start))
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

        return new RingRange(_end, _start);
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
        else if (IsWrapped ^ other.IsWrapped)
        {
            var wrapped = IsWrapped ? this : other;
            var normal = IsWrapped ? other : this;

            // There are possibly two intersections, between the normal and wrapped range.
            //         low         high
            // ...---NB====WE----WB====NE----...

            // Intersection at the low side.
            if (wrapped._end > normal._start)
            {
                // ---NB====WE---
                yield return new RingRange(normal._start, wrapped._end);
            }

            // Intersection at the high side.
            if (wrapped._start < normal._end)
            {
                // ---WB====NE---
                yield return new RingRange(wrapped._start, normal._end);
            }
        }
        else
        {
            yield return new RingRange(Math.Max(_start, other._start), Math.Min(_end, other._end));
        }
    }

    // Gets the sub-ranges which are in this range but are not in the 'previous' range.
    internal IEnumerable<RingRange> GetAdditions(RingRange previous)
    {
        // Additions are the intersections between this range and the inverse of the previous range.
        foreach (var intersection in Intersections(previous.Inverse()))
        {
            yield return intersection;
        }
    }

    // Gets the sub-ranges which are not in this range but are in the 'previous' range.
    internal IEnumerable<RingRange> GetRemovals(RingRange previous)
    {
        // Removals are the intersections between the inverse of this range and the previous range.
        foreach (var intersection in Inverse().Intersections(previous))
        {
            yield return intersection;
        }
    }

    // other range starts inside this range, merge it based on where it ends
    private RingRange MergeEnds(RingRange other)
    {
        if (_start == other._end)
        {
            return Full;
        }

        if (!Contains(other._end))
        {
            return new RingRange(_start, other._end);
        }

        if (other.Contains(_start))
        {
            return Full;
        }

        return this;
    }

    public static bool operator ==(RingRange left, RingRange right) => left.Equals(right);

    public static bool operator !=(RingRange left, RingRange right) => !(left == right);
}