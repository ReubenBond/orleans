using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Transactions;

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

internal class TaskId : ISpanFormattable
{
    public const char SegmentSeparator = '/';
    private static ReadOnlySpan<char> SegmentSeparatorSpan => "/";
    private readonly TaskId? _parent;
    private readonly string _value;

    public TaskId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (!IsValid(value))
        {
            throw new ArgumentException("Value must not contain empty segments", nameof(value));
        }

        _value = value; 
    }

    public TaskId(TaskId? parent, string value) : this(value)
    {
        _parent = parent;
    }

    private bool IsValid(string value)
    {
        var isEscaped = false;
        var segmentLength = 0;
        var consumed = 0;
        foreach (var c in value)
        {
            ++segmentLength;
            ++consumed;
            if (c == '\\')
            {
                isEscaped = !isEscaped;
            }
            else if (c == SegmentSeparator && !isEscaped)
            {
                if (segmentLength == 1)
                {
                    // Empty segment (only an segment separator)
                    return false;
                }

                segmentLength = 0;
                isEscaped = false;
            }
            else
            {
                isEscaped = false;
            }
        }

        return !isEscaped && segmentLength > 0;
    }

    [Pure]
    public TaskId CreateChild(string value) => new (this, value);

    public override string ToString() => _parent is null ? _value : $"{this}";
    public ReadOnlySpan<char> Value => ToString();

    public int Length
    {
        get
        {
            var length = 0;
            foreach (var segment in this)
            {
                // Account for segment separators.
                if (length > 0)
                {
                    ++length;
                }

                length += segment.Length;
            }

            return length;
        }
    }

    public override bool Equals(object? obj)
    {
        if (obj is not TaskId other) return false;

        var left = GetEnumerator();
        var right = other.GetEnumerator();
        while (true)
        {
            var leftValid = left.MoveNext();
            var rightValid = right.MoveNext();
            if (!leftValid && !rightValid)
            {
                // Completed enumeration.
                return true;
            }
            else if (leftValid ^ rightValid)
            {
                // One side is complete and the other is not.
                return false;
            }
            else if (!left.Current.SequenceEqual(right.Current))
            {
                // Some segment is not equal.
                return false;
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

    public SegmentEnumerator GetEnumerator() => new(this);

    public ref struct SegmentEnumerator
    {
        private RawValueEnumerator _enumerator;
        private ReadOnlySpan<char> _buffer;

        public SegmentEnumerator(TaskId id)
        {
            _enumerator = new RawValueEnumerator(id);
            _buffer = ReadOnlySpan<char>.Empty;
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public bool MoveNext()
        {
            if (_buffer.Length == 0)
            {
                if (!_enumerator.MoveNext())
                {
                    return false;
                }

                _buffer = _enumerator.Current;
            }

            Current = GetNextSegment();
            _buffer = _buffer[Current.Length..];

            if (_buffer.Length > 0 && _buffer[0] == SegmentSeparator)
            {
                _buffer = _buffer[1..];
            }

            while (Current.Length == 0)
            {
                // Advance
                if (!MoveNext())
                {
                    return false;
                }
            }

            return true;
        }

        private ReadOnlySpan<char> GetNextSegment()
        {
            var buffer = _buffer;
            var isEscaped = false;
            var length = 0;
            foreach (var c in buffer)
            {
                ++length;
                if (c == '\\')
                {
                    isEscaped = !isEscaped;
                    continue;
                }
                else if (c == SegmentSeparator && !isEscaped)
                {
                    --length;
                    break;
                }

                isEscaped = false;
            }

            return buffer[..length];
        }
    }

    private struct RawValueEnumerator
    {
        private readonly TaskId? _current;
        private int _remaining = -2;

        public RawValueEnumerator(TaskId value)
        {
            _current = value;
        }

        public ReadOnlySpan<char> Current => _remaining switch
        {
            -2 => throw new InvalidOperationException($"{nameof(MoveNext)} must be called before accessing {nameof(Current)}"),
            -1 => throw new InvalidOperationException("No remaining elements"),
            int depth => GetElement(_current, depth),
        };

        private static int GetElementCount(TaskId? current)
        {
            var elements = 0;
            while (current is not null)
            {
                ++elements;
                current = current._parent;
            }

            // If there is more than one segment, insert a separator segment between each.
            if (elements > 1)
            {
                elements += elements - 1;
            }

            return elements;
        }

        private static ReadOnlySpan<char> GetElement(TaskId? current, int depth)
        {
            // Add a separator between each segment
            if (depth % 2 == 1) return SegmentSeparatorSpan;
            depth /= 2;
            while (depth-- > 0)
            {
                current = current!._parent;
            }

            return current!._value;
        }

        public bool MoveNext()
        {
            // Start: calculate the number of elements
            if (_remaining == -2)
            {
                _remaining = GetElementCount(_current);
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
}
