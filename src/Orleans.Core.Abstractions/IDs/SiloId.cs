using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct SiloId : IEquatable<SiloId>, IComparable<SiloId>, ISerializable
    {
        private readonly SpanId _value;

        public SiloId(byte[] value) => _value = new SpanId(value);

        public SiloId(byte[] value, int hashCode) => _value = new SpanId(value, hashCode);

        public SiloId(SerializationInfo info, StreamingContext context)
        {
            _value = new SpanId((byte[])info.GetValue("v", typeof(byte[])), info.GetInt32("h"));
        }

        public SiloId(SpanId id) => _value = id;

        public static SiloId Create(string value) => new SiloId(Encoding.UTF8.GetBytes(value));

        public static explicit operator SpanId(SiloId kind) => kind._value;

        public static explicit operator SiloId(SpanId id) => new SiloId(id);

        public readonly bool IsDefault => _value.IsDefault;

        public readonly ReadOnlyMemory<byte> Value => _value.Value;

        public override readonly bool Equals(object obj) => obj is SiloId id && this.Equals(id);

        public readonly bool Equals(SiloId obj) => _value.Equals(obj._value);

        public override readonly int GetHashCode() => _value.GetHashCode();

        public static byte[] UnsafeGetArray(SiloId id) => SpanId.UnsafeGetArray(id._value);

        public static SpanId AsSpanId(SiloId id) => id._value;

        public readonly int CompareTo(SiloId other) => _value.CompareTo(other._value);

        public readonly void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", SpanId.UnsafeGetArray(_value));
            info.AddValue("h", _value.GetHashCode());
        }

        public readonly string ToStringUtf8() => _value.ToStringUtf8();

        public sealed class Comparer : IEqualityComparer<SiloId>, IComparer<SiloId>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(SiloId x, SiloId y) => x.CompareTo(y);

            public bool Equals(SiloId x, SiloId y) => x.Equals(y);

            public int GetHashCode(SiloId obj) => obj.GetHashCode();
        }
    }
}
