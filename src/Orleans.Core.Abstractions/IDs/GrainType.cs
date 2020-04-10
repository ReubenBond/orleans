using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    /// <summary>
    /// Uniquely identifies the type of a logical entity.
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct GrainType : IEquatable<GrainType>, IComparable<GrainType>, ISerializable
    {
        private readonly IdSpan _value;
        
        public GrainType(byte[] value) => _value = new IdSpan(value);

        public GrainType(byte[] value, int hashCode) => _value = new IdSpan(value, hashCode);
        
        public GrainType(SerializationInfo info, StreamingContext context)
        {
            _value = new IdSpan((byte[])info.GetValue("v", typeof(byte[])), info.GetInt32("h"));
        }

        public GrainType(IdSpan id) => _value = id;

        public static GrainType Create(string value) => new GrainType(Encoding.UTF8.GetBytes(value));

        public static explicit operator IdSpan(GrainType kind) => kind._value;

        public static explicit operator GrainType(IdSpan id) => new GrainType(id);

        public readonly bool IsDefault => _value.IsDefault;

        public readonly ReadOnlyMemory<byte> Value => _value.Value;

        public override readonly bool Equals(object obj) => obj is GrainType kind && this.Equals(kind);

        public readonly bool Equals(GrainType obj) => _value.Equals(obj._value);

        public override readonly int GetHashCode() => _value.GetHashCode();

        public static byte[] UnsafeGetArray(GrainType id) => IdSpan.UnsafeGetArray(id._value);

        public static IdSpan AsSpanId(GrainType id) => id._value;

        public readonly int CompareTo(GrainType other) => _value.CompareTo(other._value);

        public readonly void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", IdSpan.UnsafeGetArray(_value));
            info.AddValue("h", _value.GetHashCode());
        }

        public override string ToString() => this.ToStringUtf8();

        public readonly string ToStringUtf8() => _value.ToStringUtf8();

        public sealed class Comparer : IEqualityComparer<GrainType>, IComparer<GrainType>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(GrainType x, GrainType y) => x.CompareTo(y);

            public bool Equals(GrainType x, GrainType y) => x.Equals(y);

            public int GetHashCode(GrainType obj) => obj.GetHashCode();
        }
    }
}
