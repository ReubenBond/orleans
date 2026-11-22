using System.ComponentModel;

namespace Orleans.Testing.Simulation.Shims.System;

/// <summary>
/// <para>
/// The deterministic replacements for <see cref="Guid.NewGuid"/> and the .NET 11
/// <see cref="Guid.CreateVersion7()"/> overloads. Instrumented code has its direct calls redirected
/// here; each method requires an active simulation with a complete runtime.
/// </para>
/// <para>
/// Both shims draw their random bytes from the environment's per-node <em>identity</em> stream (see
/// <see cref="ISimulationRuntimeEnvironment.FillIdentityBytes"/>), which is independent of the
/// application random streams so generating GUIDs never perturbs application randomness. GUIDs are
/// constructed from a 16-byte buffer in RFC 9562 big-endian order and then materialised with the
/// big-endian <see cref="Guid"/> constructor, so the RFC variant and version fields land exactly where
/// <see cref="Guid.Version"/> and <see cref="Guid.Variant"/> read them.
/// </para>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ControlledGuid
{
    private const byte VariantMask = 0x3F;
    private const byte VariantRfc4122 = 0x80;

    /// <summary>Deterministic replacement for <see cref="Guid.NewGuid"/> (an RFC version 4 GUID).</summary>
    /// <returns>A deterministic version 4 GUID.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Guid NewGuid()
    {
        var (_, env, node) = SimulationRuntimeDispatch.RequireEnvironment("System.Guid.NewGuid");
        Span<byte> bytes = stackalloc byte[16];
        env.FillIdentityBytes(node, bytes);

        // Version 4 (random) in the high nibble of byte 6; RFC 4122 variant in the high bits of byte 8.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & VariantMask) | VariantRfc4122);

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Deterministic replacement for <see cref="Guid.CreateVersion7()"/>.</summary>
    /// <returns>A deterministic version 7 GUID stamped with the virtual UTC time when simulating.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Guid CreateVersion7()
    {
        var (_, env, node) = SimulationRuntimeDispatch.RequireEnvironment("System.Guid.CreateVersion7");
        return BuildVersion7(env.GetUtcNow(node), env, node);
    }

    /// <summary>Deterministic replacement for <see cref="Guid.CreateVersion7(DateTimeOffset)"/>.</summary>
    /// <param name="timestamp">The timestamp to embed in the GUID.</param>
    /// <returns>A deterministic version 7 GUID stamped with <paramref name="timestamp"/> when simulating.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Guid CreateVersion7(DateTimeOffset timestamp)
    {
        var (_, env, node) = SimulationRuntimeDispatch.RequireEnvironment("System.Guid.CreateVersion7");
        return BuildVersion7(timestamp, env, node);
    }

    private static Guid BuildVersion7(
        DateTimeOffset timestamp,
        ISimulationRuntimeEnvironment environment,
        Orleans.Testing.Simulation.Runtime.Execution.SimulationNodeIdentity? node)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timestamp, DateTimeOffset.UnixEpoch);

        // Matches Guid.CreateVersion7: a 48-bit big-endian Unix-millisecond timestamp in bytes 0-5,
        // deterministic random bits elsewhere, version 7 and the RFC variant applied. Repeated calls
        // at the same instant differ in their random bits but carry no monotonicity guarantee - the
        // same contract the BCL provides.
        var unixMs = timestamp.ToUnixTimeMilliseconds();

        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

        // Fill the remaining 10 bytes (rand_a + rand_b) from the deterministic identity stream.
        environment.FillIdentityBytes(node, bytes[6..]);

        // Version 7 in the high nibble of byte 6; RFC 4122 variant in the high bits of byte 8.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & VariantMask) | VariantRfc4122);

        return new Guid(bytes, bigEndian: true);
    }
}
