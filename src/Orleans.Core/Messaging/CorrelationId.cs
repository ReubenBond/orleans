using System;

#nullable enable
namespace Orleans.Runtime
{
    [Serializable, GenerateSerializer, Immutable]
    internal readonly struct CorrelationId : IEquatable<CorrelationId>, IComparable<CorrelationId>, ISpanFormattable
    {
        [Id(0)]
        public readonly long Value;
        private static long lastUsed;

        public CorrelationId(long value) => Value = value;

        public CorrelationId(CorrelationId other) => Value = other.Value;

        public static CorrelationId GetNext() => new(System.Threading.Interlocked.Increment(ref lastUsed));

        public override int GetHashCode() => Value.GetHashCode();

        public override bool Equals(object? obj) => obj is CorrelationId correlationId && Equals(correlationId);

        public bool Equals(CorrelationId other) => Value == other.Value;

        public static bool operator ==(CorrelationId lhs, CorrelationId rhs) => rhs.Value == lhs.Value;

        public static bool operator !=(CorrelationId lhs, CorrelationId rhs) => rhs.Value != lhs.Value;

        public int CompareTo(CorrelationId other) => Value.CompareTo(other.Value);

        public override string ToString() => Value.ToString();

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => Value.ToString(format, formatProvider);

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => Value.TryFormat(destination, out charsWritten, format, provider);

        internal long ToInt64() => Value;
    }
}
