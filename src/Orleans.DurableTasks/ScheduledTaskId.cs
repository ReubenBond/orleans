using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Orleans.Serialization.Buffers;

namespace Orleans.DurableTasks;

public readonly struct ScheduledTaskId
{
    public static readonly ScheduledTaskId None = default;
    
    public ScheduledTaskId(string value)
    {
        Value = value;
    }

    public static implicit operator string(ScheduledTaskId id) => id.Value;
    public static implicit operator ScheduledTaskId(string value) => new (value);

    public string Value { get; }
}

internal class TaskIdSegment : ISpanFormattable
{
    public const char SegmentSeparator = '/';
    private static ReadOnlySpan<char> SegmentSeparatorSpan => "/";
    private TaskIdSegment? _parent;
    private string _value;

    public TaskIdSegment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        // TODO: escape value.
        _value = value; 
    }

    public TaskIdSegment(TaskIdSegment? parent, string value) : this(value)
    {
        _parent = parent;
    }

    [Pure]
    public TaskIdSegment CreateChild(string value) => new (this, value);

    public override string ToString() => _parent is null ? _value : $"{this}";
    public ReadOnlySpan<char> Value => ToString();
    public int Length
    {
        get
        {
            var length = 0;
            var c = this;
            while (c is not null)
            {
                length += c._value.Length;
                c = c._parent;
            }

            return length;
        }
    }

    public SpanEnumerator GetEnumerator() => new(this);

    public override bool Equals(object? obj)
    {
        if (obj is not TaskIdSegment other) return false;

        var a = new ReverseSpanEnumerator(this);
        var aSeg = ReadOnlySpan<char>.Empty;
        var aComplete = false;
        var b = new ReverseSpanEnumerator(other);
        var bSeg = ReadOnlySpan<char>.Empty;
        var bComplete = false;
        while (true)
        {
            if (aSeg.Length == 0 && !aComplete)
            {
                aComplete = !a.MoveNext();
                if (!aComplete)
                {
                    aSeg = a.Current;
                }
            }

            if (bSeg.Length == 0 && !bComplete)
            {
                bComplete = !b.MoveNext();
                if (!bComplete)
                {
                    bSeg = b.Current;
                }
            }

            var len = Math.Min(aSeg.Length, bSeg.Length);
            if (!aSeg[^len..].SequenceEqual(bSeg[^len..]))
            {
                return false;
            }

            // Skip the bytes which were just compared.
            aSeg = aSeg[..^len];
            bSeg = bSeg[..^len];

            if (aComplete && bComplete)
            {
                return aSeg.Length == 0 && bSeg.Length == 0;
            }
        }
    }

    public override int GetHashCode()
    {
        // Note that we want to ensure that GetHashCode returns equal values for semantically equivalent
        // instances. To achieve this, we treat the instances as a sequence of bytes, independent of
        // where in the chain of instances the various segments sit.
        // This allows for one instance with a value "foo/bar" and a child with "baz" to have the same
        // hash code as an instance with the value "foo/bar/baz".
        var length = Length;
        var array = length <= 256 ? null : ArrayPool<char>.Shared.Rent(length);
        Span<char> buffer = array ?? stackalloc char[256];

        // Write the value into the buffer.
        var didFormat = TryFormat(buffer, out var len, ReadOnlySpan<char>.Empty, null);
        buffer = buffer[..len];
        Debug.Assert(didFormat);

        HashCode hashCode = new();
        hashCode.AddBytes(MemoryMarshal.AsBytes(buffer));

        if (array is not null)
        {
            ArrayPool<char>.Shared.Return(array);
        }

        return hashCode.ToHashCode();
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_parent is not null)
        {
            if (_parent.TryFormat(destination, out charsWritten, format, provider))
            {
                destination = destination[charsWritten..];
                if (destination.Length > 0)
                {
                    destination[0] = SegmentSeparator;
                    destination = destination[1..];
                    ++charsWritten;
                }
            }
            else
            {
                return false;
            }
        }
        else
        {
            charsWritten = 0;
        }

        if (_value.AsSpan().TryCopyTo(destination))
        {
            charsWritten += _value.Length;
            return true;
        }

        return false;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public struct SpanEnumerator
    {
        private readonly TaskIdSegment? _segment;
        private int _remaining = -2;

        public SpanEnumerator(TaskIdSegment segment)
        {
            _segment = segment;
        }

        public ReadOnlySpan<char> Current => _remaining switch
        {
            -2 => throw new InvalidOperationException($"{nameof(MoveNext)} must be called before accessing {nameof(Current)}"),
            -1 => throw new InvalidOperationException("No remaining elements"),
            int depth => GetElement(_segment, depth),
        };

        private static int GetElementCount(TaskIdSegment? id)
        {
            var elements = 0;
            while (id is not null)
            {
                ++elements;
                id = id._parent;
            }

            // If there is more than one segment, insert a separator segment between each.
            if (elements > 1)
            {
                elements += elements - 1;
            }

            return elements;
        }

        private static ReadOnlySpan<char> GetElement(TaskIdSegment? id, int depth)
        {
            // Add a separator between each segment
            if (depth % 2 == 1) return SegmentSeparatorSpan;
            depth /= 2;
            while (depth-- > 0)
            {
                id = id!._parent;
            }

            return id!._value;
        }

        public bool MoveNext()
        {
            // Start: calculate the number of elements
            if (_remaining == -2)
            {
                _remaining = GetElementCount(_segment);
            }

            // If there are no elements remaining 
            if (_remaining == 0)
            {
                return false;
            }

            --_remaining;
            return true;
        }
    }

    public struct ReverseSpanEnumerator
    {
        private TaskIdSegment? _segment;
        private int _status;

        public ReverseSpanEnumerator(TaskIdSegment segment)
        {
            _segment = segment;
        }

        public ReadOnlySpan<char> Current => _status switch
        {
            0 => throw new InvalidOperationException($"{nameof(MoveNext)} must be called before accessing {nameof(Current)}"),
            1 => _segment!._value,
            2 => SegmentSeparatorSpan,
            3 => _segment!.Value,
            _ => throw new InvalidOperationException("No remaining values")
        };

        public bool MoveNext()
        {
            // Not started
            if (_status == 0)
            {
                if (_segment is null)
                {
                    // Completed
                    _status = 9;
                    return false;
                }

                // Return the current value from Current
                _status = 1;
                return true;
            }

            if (_status == 1)
            {
                if (_segment!._parent is null)
                {
                    // Completed
                    _status = 9;
                    return false;
                }

                // Return a path separator from Current
                _status = 2;
                return true;
            }

            if (_status == 2)
            {
                // Navigate to the parent and return the current value from Current
                _segment = _segment!._parent;

                _status = 1;
                return true;
            }

            return false;
        }
    }
}
