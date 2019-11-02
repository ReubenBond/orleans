using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct GrainId : IEquatable<GrainId>, IComparable<GrainId>, ISerializable
    {
        private readonly GrainKind _kind;
        private readonly SpanId _key;

        public GrainId(GrainKind kind, byte[] key, int keyHashCode)
        {
            _kind = kind;
            _key = new SpanId(key, keyHashCode);
        }

        public GrainId(SerializationInfo info, StreamingContext context)
        {
            _kind = new GrainKind((byte[])info.GetValue("tv", typeof(byte[])), info.GetInt32("th"));
            _key = new SpanId((byte[])info.GetValue("kv", typeof(byte[])), info.GetInt32("kh"));
        }

        public readonly GrainKind Kind => _kind;

        public readonly ReadOnlyMemory<byte> Key => _key.Value;

        public override bool Equals(object obj) => obj is GrainId id && this.Equals(id);

        public bool Equals(GrainId other) => this.Kind.Equals(other.Kind) && this.Key.Equals(other.Key);

        public override int GetHashCode() => HashCode.Combine(_kind, _key);

        public readonly void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("tv", GrainKind.UnsafeGetArray(_kind));
            info.AddValue("th", _kind.GetHashCode());
            info.AddValue("kv", SpanId.UnsafeGetArray(_key));
            info.AddValue("kh", _key.GetHashCode());
        }

        public int CompareTo(GrainId other)
        {
            var kinds = _kind.CompareTo(other._kind);
            if (kinds != 0) return kinds;

            return _key.CompareTo(other._key);
        }

        public static (byte[] Key, int KeyHashCode) UnsafeGetKey(GrainId id) => (SpanId.UnsafeGetArray(id._key), id._key.GetHashCode());

        public sealed class Comparer : IEqualityComparer<GrainId>, IComparer<GrainId>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(GrainId x, GrainId y) => x.CompareTo(y);

            public bool Equals(GrainId x, GrainId y) => x.Equals(y);

            public int GetHashCode(GrainId obj) => obj.GetHashCode();
        }
    }
}
