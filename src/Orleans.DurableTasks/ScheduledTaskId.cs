using System.Collections;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;

namespace Orleans.DurableTasks;

public readonly struct ScheduledTaskId : IEnumerable<string>
{
    public static readonly ScheduledTaskId None = default;
    private byte[] _array;
    
    public ScheduledTaskId(string value)
    {
        Value = value;
    }

    public static implicit operator string(ScheduledTaskId id) => id.Value;
    public static implicit operator ScheduledTaskId(string value) => new (value);

    public string Value { get; }

    public readonly SegmentIterator GetEnumerator() => new(this);
    public readonly SegmentStringIterator GetStringEnumerator() => new(this);
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => GetStringEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetStringEnumerator();

    public struct SegmentIterator
    {
        private byte[] _array;
        private int _segmentStart;
        private int _segmentLength;

        public SegmentIterator(ScheduledTaskId id)
        {
            _array = id._array;
        }

        public ReadOnlySpan<char> Current => _segmentLength switch
        {
            0 => ReadOnlySpan<char>.Empty,
            _ => MemoryMarshal.Cast<byte, char>(_array.AsSpan(_segmentStart, _segmentLength))
        };

        public bool MoveNext()
        {
            if (_array.Length == _segmentStart + _segmentLength)
            {
                return false;
            }

            var newStart = _segmentStart + _segmentLength;
            var reader = Reader.Create(_array.AsSpan(newStart), null);
            var newLength = reader.ReadVarInt32();
            _segmentStart = newStart;
            _segmentLength = newLength;
            return true;
        }
    }

    public struct SegmentStringIterator : IEnumerator<string>
    {
        private byte[] _array;
        private int _segmentStart;
        private int _segmentLength;

        public SegmentStringIterator(ScheduledTaskId id)
        {
            _array = id._array;
        }

        public string Current => _segmentLength switch
        {
            0 => string.Empty,
            _ => new string(MemoryMarshal.Cast<byte, char>(_array.AsSpan(_segmentStart, _segmentLength)))
        };

        object IEnumerator.Current => Current;

        public void Dispose() { }

        public bool MoveNext()
        {
            if (_array.Length == _segmentStart + _segmentLength)
            {
                return false;
            }

            var newStart = _segmentStart + _segmentLength;
            var reader = Reader.Create(_array.AsSpan(newStart), null);
            var newLength = reader.ReadVarInt32();
            _segmentStart = newStart;
            _segmentLength = newLength;
            return true;
        }

        public void Reset() => _segmentLength = _segmentStart = 0;
    }
}

internal class TaskIdSegment
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

    public override string ToString() => _parent is null ? _value : $"{_parent.Value}{SegmentSeparator}{_value}";
    public ReadOnlySpan<char> Value => _parent is null ? _value : $"{_parent.Value}{SegmentSeparator}{_value}";

    public SpanEnumerator GetEnumerator() => new SpanEnumerator(this);

    public override bool Equals(object? obj)
    {
        if (obj is not TaskIdSegment other) return false;

        var a = new SpanEnumerator(this);
        var aSeg = ReadOnlySpan<char>.Empty;
        var aComplete = false;
        var b = new SpanEnumerator(other);
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
            if (!aSeg[..len].SequenceEqual(bSeg[..len]))
            {
                return false;
            }

            // Skip the bytes which were just compared.
            aSeg = aSeg[len..];
            bSeg = bSeg[len..];

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
        HashCode hashCode = new();
        foreach (var span in this)
        {
            hashCode.AddBytes(MemoryMarshal.AsBytes(span));
        }

        return hashCode.ToHashCode();
    }

    public struct SpanEnumerator
    {
        private TaskIdSegment? _segment;
        private int _status;

        public SpanEnumerator(TaskIdSegment segment)
        {
            _segment = segment;
        }

        public ReadOnlySpan<char> Current => _status switch
        {
            0 => throw new InvalidOperationException($"{nameof(MoveNext)} must be called before accessing {nameof(Current)}"),
            1 => _segment!._value,
            2 => SegmentSeparatorSpan,
            3 => _segment switch
            {
                null => ReadOnlySpan<char>.Empty,
                _ => _segment.Value,
            },
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
