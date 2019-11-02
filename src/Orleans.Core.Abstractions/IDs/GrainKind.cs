using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct GrainKind : IEquatable<GrainKind>, IComparable<GrainKind>, ISerializable
    {
        private readonly SpanId _value;

        public GrainKind(byte[] value) => _value = new SpanId(value);

        public GrainKind(byte[] value, int hashCode) => _value = new SpanId(value, hashCode);

        public GrainKind(SerializationInfo info, StreamingContext context)
        {
            _value = new SpanId((byte[])info.GetValue("v", typeof(byte[])), info.GetInt32("h"));
        }

        private GrainKind(SpanId id) => _value = id;

        public static explicit operator SpanId(GrainKind kind) => kind._value;

        public static explicit operator GrainKind(SpanId id) => new GrainKind(id);

        public readonly ReadOnlyMemory<byte> Value => _value.Value;

        public override readonly bool Equals(object obj) => obj is GrainKind kind && this.Equals(kind);

        public readonly bool Equals(GrainKind obj) => _value.Equals(obj._value);

        public override readonly int GetHashCode() => _value.GetHashCode();

        public static byte[] UnsafeGetArray(GrainKind id) => SpanId.UnsafeGetArray(id._value);

        public int CompareTo(GrainKind other) => _value.CompareTo(other._value);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", SpanId.UnsafeGetArray(_value));
            info.AddValue("h", _value.GetHashCode());
        }

        public sealed class Comparer : IEqualityComparer<GrainKind>, IComparer<GrainKind>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(GrainKind x, GrainKind y) => x.CompareTo(y);

            public bool Equals(GrainKind x, GrainKind y) => x.Equals(y);

            public int GetHashCode(GrainKind obj) => obj.GetHashCode();
        }
    }
}
