using System.Buffers.Binary;
using System.Globalization;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// An operation on one conditionally written value: a read, or a write conditioned on the value the writer read.
/// </summary>
/// <param name="IsRead">Whether the operation reads.</param>
/// <param name="Expected">The value a write expects, <see langword="null"/> for absent.</param>
/// <param name="Value">The value a write writes.</param>
internal readonly record struct ConditionalInput(bool IsRead, int? Expected, int Value)
{
    public static ConditionalInput Read() => new(IsRead: true, Expected: null, Value: 0);

    public static ConditionalInput Write(int? expected, int value) => new(IsRead: false, expected, value);

    public override string ToString() => IsRead
        ? "read"
        : string.Create(CultureInfo.InvariantCulture, $"write {Value} at {Expected?.ToString(CultureInfo.InvariantCulture) ?? "absent"}");
}

/// <summary>
/// How an operation on a conditionally written value answered.
/// </summary>
internal enum ConditionalOutcome
{
    /// <summary>A read returned <see cref="ConditionalOutput.Value"/>, <see langword="null"/> for absent.</summary>
    Read,

    /// <summary>A write applied.</summary>
    Applied,

    /// <summary>A write's condition did not hold.</summary>
    Mismatch,

    /// <summary>The operation threw: a read without effect, a write whose outcome is unknown.</summary>
    Failed,
}

/// <summary>
/// The answer of an operation on a conditionally written value.
/// </summary>
internal readonly record struct ConditionalOutput(ConditionalOutcome Outcome, int? Value = null)
{
    public override string ToString() => Outcome == ConditionalOutcome.Read
        ? Value?.ToString(CultureInfo.InvariantCulture) ?? "absent"
        : Outcome.ToString();
}

/// <summary>
/// One value replaced only by a write which names the value it expects: the sequential specification of a
/// <c>IConditionalRegister</c> (MS-I1 to MS-I3) and of an object's versioned reads and conditional replaces (archive-lsm.md
/// §3), when every write writes a distinct value, so that a version identifies the value read at it.
/// </summary>
/// <remarks>
/// A write whose outcome is unknown applies where its condition holds, or, recorded with an unbounded response and so
/// linearizable after everything, never (MS-I3). A mismatch asserts only that the write can no longer land: after a
/// backend-SDK retry it may follow an earlier attempt which applied, so a write answered mismatched applies where its
/// condition holds (metastore.md §2.2).
/// </remarks>
internal sealed class ConditionalValueModel : LinearizabilityModel<int?, ConditionalInput, ConditionalOutput>
{
    public static ConditionalValueModel Instance { get; } = new();

    public override int? Init() => null;

    public override bool Step(int? state, ConditionalInput input, ConditionalOutput output, out int? next)
    {
        next = state;
        if (input.IsRead)
        {
            return output.Outcome == ConditionalOutcome.Failed || output.Outcome == ConditionalOutcome.Read && output.Value == state;
        }

        var holds = state == input.Expected;
        if (output.Outcome is not (ConditionalOutcome.Applied or ConditionalOutcome.Mismatch or ConditionalOutcome.Failed))
        {
            return false;
        }

        next = holds ? input.Value : state;
        return holds || output.Outcome != ConditionalOutcome.Applied;
    }

    public override string DescribeState(int? state) => state?.ToString(CultureInfo.InvariantCulture) ?? "absent";
}

/// <summary>
/// Encodes the distinct values clients write to a register or an object.
/// </summary>
internal static class ConditionalValues
{
    public static byte[] Encode(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static int Decode(byte[] bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes);
}
