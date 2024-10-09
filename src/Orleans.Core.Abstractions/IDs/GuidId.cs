using System;
using System.Runtime.Serialization;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// A unique identifier based on a <see cref="Guid"/>.
    /// </summary>
    [Serializable]
    [Immutable]
    [GenerateSerializer]
    public sealed class GuidId : IEquatable<GuidId>, IComparable<GuidId>, ISerializable
    {
        private static readonly Interner<Guid, GuidId> guidIdInternCache = new Interner<Guid, GuidId>(InternerConstants.SIZE_LARGE);

        /// <summary>
        /// The underlying <see cref="Guid"/>.
        /// </summary>
        [Id(0)]
#pragma warning disable CA1720 // Identifier contains type name
        public Guid Guid { get; }
#pragma warning restore CA1720 // Identifier contains type name

        /// <summary>
        /// Initializes a new instance of the <see cref="GuidId"/> class.
        /// </summary>
        /// <param name="guid">
        /// The underlying <see cref="Guid"/>.
        /// </param>
        private GuidId(Guid guid)
        {
            this.Guid = guid;
        }

        /// <summary>
        /// Returns a new, randomly generated <see cref="GuidId"/>.
        /// </summary>
        /// <returns>A new, randomly generated <see cref="GuidId"/>.</returns>
        public static GuidId GetNewGuidId()
        {
            return FindOrCreateGuidId(Guid.NewGuid());
        }

        /// <summary>
        /// Returns a <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.
        /// </summary>
        /// <param name="value">The guid.</param>
        /// <returns>A <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.</returns>
        public static GuidId GetGuidId(Guid value)
        {
            return FindOrCreateGuidId(value);
        }

        /// <summary>
        /// Returns a <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.
        /// </summary>
        /// <param name="guid">The <see cref="Guid"/>.</param>
        /// <returns>A <see cref="GuidId"/> instance corresponding to the provided <see cref="Guid"/>.</returns>
        private static GuidId FindOrCreateGuidId(Guid guid)
        {
            return guidIdInternCache.FindOrCreate(guid, g => new GuidId(g));
        }

        /// <inheritdoc />
        public int CompareTo(GuidId? other) => other is null ? 1 : Guid.CompareTo(other.Guid);

        /// <inheritdoc />
        public bool Equals(GuidId? other) => other is not null && Guid.Equals(other.Guid);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as GuidId);

        /// <inheritdoc />
        public override int GetHashCode() => Guid.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => Guid.ToString();

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(GuidId? left, GuidId? right) => ReferenceEquals(left, right) || (left?.Equals(right) ?? false);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(GuidId? left, GuidId? right) => !(left == right);

        /// <summary>
        /// Returns true if left is less than right.
        /// </summary>
        public static bool operator <(GuidId left, GuidId right) => left.CompareTo(right) < 0;

        /// <summary>
        /// Returns true if left is less than or equal to right.
        /// </summary>
        public static bool operator <=(GuidId left, GuidId right) => left.CompareTo(right) <= 0;

        /// <summary>
        /// Returns true if left is greater than right.
        /// </summary>
        public static bool operator >(GuidId left, GuidId right) => left.CompareTo(right) > 0;

        /// <summary>
        /// Returns true if left is greater than or equal to right.
        /// </summary>
        public static bool operator >=(GuidId left, GuidId right) => left.CompareTo(right) >= 0;

        /// <inheritdoc />
        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("Guid", Guid, typeof(Guid));
        }

        private GuidId(SerializationInfo info, StreamingContext context)
        {
            Guid = (Guid)info.GetValue("Guid", typeof(Guid))!;
        }
    }
}
