using System;

namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    [GenerateSerializer]
    public readonly struct MembershipVersion : IComparable<MembershipVersion>, IEquatable<MembershipVersion>
    {
        public MembershipVersion(long version) => Value = version;

        [Id(1)]
        public long Value { get; }

        public static MembershipVersion MinValue => new(long.MinValue);
        public static MembershipVersion Zero => new(0);

        public bool IsSuccessorTo(MembershipVersion predecessor) => Value == predecessor.Value + 1;

        public MembershipVersion Next() => new(Value + 1);

        public int CompareTo(MembershipVersion other) => Value.CompareTo(other.Value);

        public bool Equals(MembershipVersion other) => Value == other.Value;

        public override bool Equals(object obj) => obj is MembershipVersion other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString();

        public static bool operator ==(MembershipVersion left, MembershipVersion right) => left.Value == right.Value;
        public static bool operator !=(MembershipVersion left, MembershipVersion right) => left.Value != right.Value;
        public static bool operator >=(MembershipVersion left, MembershipVersion right) => left.Value >= right.Value;
        public static bool operator <=(MembershipVersion left, MembershipVersion right) => left.Value <= right.Value;
        public static bool operator >(MembershipVersion left, MembershipVersion right) => left.Value > right.Value;
        public static bool operator <(MembershipVersion left, MembershipVersion right) => left.Value < right.Value;
    }
}
