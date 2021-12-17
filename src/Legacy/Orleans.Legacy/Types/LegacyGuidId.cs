using System;
using System.Runtime.Serialization;

namespace Orleans.Legacy.Runtime
{
    /// <summary>
    /// Wrapper object around Guid.
    /// Can be used in places where Guid is optional and in those cases it can be set to null and will not use the storage of an empty Guid struct.
    /// </summary>
    [Serializable]
    [Immutable]
    public sealed class LegacyGuidId : IEquatable<LegacyGuidId>, IComparable<LegacyGuidId>, ISerializable
    {
        private static readonly Lazy<Interner<Guid, LegacyGuidId>> guidIdInternCache = new Lazy<Interner<Guid, LegacyGuidId>>(
                    () => new Interner<Guid, LegacyGuidId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq));

        public readonly Guid Guid;

        // TODO: Need to integrate with Orleans serializer to really use Interner.
        private LegacyGuidId(Guid guid)
        {
            this.Guid = guid;
        }

        public static LegacyGuidId GetNewGuidId()
        {
            return FindOrCreateGuidId(Guid.NewGuid());
        }

        public static LegacyGuidId GetGuidId(Guid guid)
        {
            return FindOrCreateGuidId(guid);
        }

        private static LegacyGuidId FindOrCreateGuidId(Guid guid)
        {
            return guidIdInternCache.Value.FindOrCreate(guid, g => new LegacyGuidId(g));
        }

        public int CompareTo(LegacyGuidId other)
        {
            return this.Guid.CompareTo(other.Guid);
        }

        public bool Equals(LegacyGuidId other)
        {
            return other != null && this.Guid.Equals(other.Guid);
        }

        public override bool Equals(object obj)
        {
            return this.Equals(obj as LegacyGuidId);
        }

        public override int GetHashCode()
        {
            return this.Guid.GetHashCode();
        }

        public override string ToString()
        {
            return this.Guid.ToString();
        }

        internal string ToDetailedString()
        {
            return this.Guid.ToString();
        }

        public string ToParsableString()
        {
            return Guid.ToString();
        }

        public static LegacyGuidId FromParsableString(string guidId)
        {
            Guid id = System.Guid.Parse(guidId);
            return GetGuidId(id);
        }

        public void SerializeToStream(IBinaryTokenStreamWriter stream)
        {
            stream.Write(this.Guid);
        }

        internal static LegacyGuidId DeserializeFromStream(IBinaryTokenStreamReader stream)
        {
            Guid guid = stream.ReadGuid();
            return LegacyGuidId.GetGuidId(guid);
        }

        public static bool operator ==(LegacyGuidId a, LegacyGuidId b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (ReferenceEquals(a, null)) return false;
            if (ReferenceEquals(b, null)) return false;
            return a.Guid.Equals(b.Guid);
        }

        public static bool operator !=(LegacyGuidId a, LegacyGuidId b)
        {
            return !(a == b);
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Guid", Guid, typeof(Guid));
        }

        // The special constructor is used to deserialize values. 
        private LegacyGuidId(SerializationInfo info, StreamingContext context)
        {
            Guid = (Guid) info.GetValue("Guid", typeof(Guid));
        }
    }
}
