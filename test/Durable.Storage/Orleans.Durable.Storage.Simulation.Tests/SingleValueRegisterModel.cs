using System.Globalization;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// A client operation on one ledger replica address: a write of the address's only value, or a read.
/// </summary>
/// <param name="Address">The address.</param>
/// <param name="IsWrite">Whether the operation writes.</param>
internal readonly record struct RegisterInput(long Address, bool IsWrite)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(IsWrite ? "write" : "read")} {Address}");
}

/// <summary>
/// How a ledger replica operation answered.
/// </summary>
internal enum RegisterOutput
{
    /// <summary>A write reported DATA at the address.</summary>
    Written,

    /// <summary>A read found nothing at the address.</summary>
    Missing,

    /// <summary>A read returned the address's value.</summary>
    Data,

    /// <summary>A read returned a value other than the one written there.</summary>
    WrongData,

    /// <summary>The operation threw: a read without effect, or a write whose outcome is unknown.</summary>
    Failed,
}

/// <summary>
/// The sequential specification of a quorum's addresses as its reads see them at or above its known contiguous tail:
/// each address holds one value (LR-I1), and a write which the quorum acknowledged decides it, after which every read
/// returns that value (LP-I4). Before then a read returns the value a write attempted or nothing: the quorum returns
/// the first DATA any replica holds and repairs nothing, so DATA there asserts only that a write was attempted, and
/// reads of an address no write decided need not agree (ledger-protocol.md §8.3). Addresses are independent, so the history
/// partitions by address.
/// </summary>
/// <remarks>
/// "individual addresses are single-value registers: i.e., they are only linearizable for write/read operations if a
/// single value is written to them" (research/papers/logdrive/paper.md, §3.1): each replica's address is one, and no
/// read ever returns another value than the address's. A failed write, recorded with an unbounded response, decides the
/// address where it is linearized or, linearized after everything, never.
/// </remarks>
internal sealed class SingleValueRegisterModel : LinearizabilityModel<bool, RegisterInput, RegisterOutput>
{
    public static SingleValueRegisterModel Instance { get; } = new();

    public override IEqualityComparer<RegisterInput>? PartitionComparer { get; } = new AddressComparer();

    public override bool Init() => false;

    public override bool Step(bool state, RegisterInput input, RegisterOutput output, out bool next)
    {
        next = state;
        if (input.IsWrite)
        {
            var effective = output is RegisterOutput.Written or RegisterOutput.Failed;
            next = effective;
            return effective;
        }

        return output switch
        {
            RegisterOutput.Failed or RegisterOutput.Data => true,
            RegisterOutput.Missing => !state,
            RegisterOutput.Written or RegisterOutput.WrongData => false,
            _ => false,
        };
    }

    public override string DescribeState(bool state) => state ? "decided" : "undecided";

    private sealed class AddressComparer : IEqualityComparer<RegisterInput>
    {
        public bool Equals(RegisterInput x, RegisterInput y) => x.Address == y.Address;

        public int GetHashCode(RegisterInput obj) => obj.Address.GetHashCode();
    }
}
