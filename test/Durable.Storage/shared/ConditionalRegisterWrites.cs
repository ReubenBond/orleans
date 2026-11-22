using System.Buffers;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.MetaStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Writes a test's bytes to a conditional register, and reads a write's outcome.
/// </summary>
internal static class ConditionalRegisterWrites
{
    /// <summary>
    /// Writes <paramref name="value"/>, a test's own array, as one ledger.
    /// </summary>
    internal static ValueTask<RegisterWriteOutcome> WriteAsync(
        this IConditionalRegister register,
        MetaStoreVersion? expectedVersion,
        byte[] value,
        CancellationToken cancellationToken = default) =>
        register.WriteAsync(expectedVersion, new ReadOnlySequence<byte>(value), cancellationToken);

    /// <summary>
    /// Returns whether the write applied.
    /// </summary>
    internal static bool IsApplied(this RegisterWriteOutcome outcome) => outcome switch
    {
        RegisterWriteOutcome.Applied => true,
        RegisterWriteOutcome.Mismatch => false,
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Returns the version an applied write gave the register, and fails when the write mismatched.
    /// </summary>
    internal static MetaStoreVersion AppliedVersion(this RegisterWriteOutcome outcome) =>
        Assert.IsType<RegisterWriteOutcome.Applied>(outcome.Value).Version;
}
