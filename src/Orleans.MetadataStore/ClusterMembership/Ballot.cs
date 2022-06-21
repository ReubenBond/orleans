using Orleans.Runtime;
using System;

namespace Orleans.MetadataStore
{
    [Immutable]
    [GenerateSerializer]
    public readonly struct Ballot : IComparable<Ballot>
    {
        /// <summary>
        /// The proposal number.
        /// </summary>
        [Id(0)]
        public readonly int Round;

        /// <summary>
        /// The unique identifier of the proposer.
        /// </summary>
        [Id(1)]
        public readonly Guid Proposer;

        public Ballot(int round, Guid proposer)
        {
            Round = round;
            Proposer = proposer;
        }

        public Ballot Successor(Guid proposer) => new(Round + 1, proposer);

        public Ballot FastRoundSuccessor() => new(Round + 1, Guid.Empty);

        public Ballot AdvancePast(Ballot other) => new(Math.Max(Round, other.Round), Proposer);

        public bool IsFastRoundBallot => Guid.Empty.Equals(Proposer);

        public bool IsClassicRoundBallot => !IsFastRoundBallot;

        public static Ballot Zero => default;

        public bool IsZero() => Equals(Zero);

        /// <inheritdoc />
        public override string ToString() => IsZero() ? $"{nameof(Ballot)}(ø)" : $"{nameof(Ballot)}({Round}.{Proposer})";

        public bool Equals(Ballot other)
        {
            return Round == other.Round && Proposer == other.Proposer;
        }

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is Ballot ballot && Equals(ballot);

        /// <inheritdoc />
        public int CompareTo(Ballot other)
        {
            var counterComparison = Round - other.Round;
            if (counterComparison != 0)
            {
                return counterComparison;
            }

            return Proposer.CompareTo(other.Proposer);
        }

        public static bool operator ==(Ballot left, Ballot right) => left.Equals(right);

        public static bool operator !=(Ballot left, Ballot right) => !left.Equals(right);

        public static bool operator <(Ballot left, Ballot right) => left.CompareTo(right) < 0;

        public static bool operator >(Ballot left, Ballot right) => left.CompareTo(right) > 0;

        public static bool operator <=(Ballot left, Ballot right) => left.CompareTo(right) <= 0;

        public static bool operator >=(Ballot left, Ballot right) => left.CompareTo(right) >= 0;

        public override int GetHashCode() => HashCode.Combine(Round, Proposer);
    }
}