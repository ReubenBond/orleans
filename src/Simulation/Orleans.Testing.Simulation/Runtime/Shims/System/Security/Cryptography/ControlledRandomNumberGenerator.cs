using System.ComponentModel;
using System.Security.Cryptography;
using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Shims.System.Security.Cryptography;

/// <summary>
/// <para>
/// Deterministic shims for the static randomness APIs on <see cref="RandomNumberGenerator"/>.
/// Instrumented code has the static
/// <c>RandomNumberGenerator</c> members redirected here (including the two <see cref="RandomNumberGenerator.Create()"/>
/// factories). Every controlled API draws from a reproducible, per-node non-cryptographic stream
/// isolated from application, identity, scheduler, network, and fault-injection randomness.
/// </para>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ControlledRandomNumberGenerator
{
    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.Create()"/>.</summary>
    /// <returns>A deterministic non-cryptographic generator.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static RandomNumberGenerator Create()
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.Create", out var env, out var node);
        return new SimulationRandomNumberGenerator(env, node);
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.Create(string)"/>.</summary>
    /// <param name="name">The algorithm name.</param>
    /// <returns>A deterministic non-cryptographic generator for a known algorithm name; otherwise, <see langword="null"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static RandomNumberGenerator? Create(string name)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.Create", out var env, out var node);
#pragma warning disable SYSLIB0045 // Probe the BCL registry so known and unknown names retain its contract.
        using RandomNumberGenerator? knownAlgorithm = RandomNumberGenerator.Create(name);
#pragma warning restore SYSLIB0045
        return knownAlgorithm is null
            ? null
            : new SimulationRandomNumberGenerator(env, node);
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.Fill(Span{byte})"/>.</summary>
    /// <param name="data">The buffer to fill.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Fill(Span<byte> data)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.Fill", out var env, out var node);
        env.FillCryptoRandomBytes(node, data);
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetBytes(int)"/>.</summary>
    /// <param name="count">The number of bytes to produce.</param>
    /// <returns>A buffer of deterministic non-cryptographic bytes.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static byte[] GetBytes(int count)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetBytes", out var env, out var node);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var buffer = new byte[count];
        env.FillCryptoRandomBytes(node, buffer);
        return buffer;
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetInt32(int)"/>.</summary>
    /// <param name="toExclusive">The exclusive upper bound.</param>
    /// <returns>A value in <c>[0, toExclusive)</c>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int GetInt32(int toExclusive)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetInt32", out var env, out var node);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toExclusive);
        return GetInt32(0, toExclusive, env, node);
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetInt32(int, int)"/>.</summary>
    /// <param name="fromInclusive">The inclusive lower bound.</param>
    /// <param name="toExclusive">The exclusive upper bound.</param>
    /// <returns>A value in <c>[fromInclusive, toExclusive)</c>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int GetInt32(int fromInclusive, int toExclusive)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetInt32", out var env, out var node);
        return GetInt32(fromInclusive, toExclusive, env, node);
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetHexString(Span{char}, bool)"/>.</summary>
    /// <param name="destination">The destination buffer.</param>
    /// <param name="lowercase">Whether to emit lowercase hex.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void GetHexString(Span<char> destination, bool lowercase = false)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetHexString", out var env, out var node);
        FillHex(destination, lowercase, env, node);
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetHexString(int, bool)"/>.</summary>
    /// <param name="stringLength">The number of hex characters to produce.</param>
    /// <param name="lowercase">Whether to emit lowercase hex.</param>
    /// <returns>A deterministic hex string.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string GetHexString(int stringLength, bool lowercase = false)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetHexString", out var env, out var node);
        if (stringLength == 0)
        {
            return string.Empty;
        }

        return string.Create(stringLength, (env, node, lowercase), static (span, state) =>
            FillHex(span, state.lowercase, state.env, state.node));
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetItems{T}(ReadOnlySpan{T}, Span{T})"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="choices">The items to choose from.</param>
    /// <param name="destination">The destination to fill with selected items.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void GetItems<T>(ReadOnlySpan<T> choices, Span<T> destination)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetItems", out var env, out var node);
        if (choices.IsEmpty)
        {
            RandomNumberGenerator.GetItems(choices, destination);
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = choices[GetInt32(0, choices.Length, env, node)];
        }
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetItems{T}(ReadOnlySpan{T}, int)"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="choices">The items to choose from.</param>
    /// <param name="length">The number of items to return.</param>
    /// <returns>An array filled with deterministic selections from <paramref name="choices"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static T[] GetItems<T>(ReadOnlySpan<T> choices, int length)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetItems", out var env, out var node);
        if (choices.IsEmpty || length < 0)
        {
            return RandomNumberGenerator.GetItems(choices, length);
        }

        var result = new T[length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = choices[GetInt32(0, choices.Length, env, node)];
        }

        return result;
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.GetString(ReadOnlySpan{char}, int)"/>.</summary>
    /// <param name="choices">The allowed characters.</param>
    /// <param name="length">The number of characters to produce.</param>
    /// <returns>A deterministic string drawn from <paramref name="choices"/>.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string GetString(ReadOnlySpan<char> choices, int length)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.GetString", out var env, out var node);
        if (choices.IsEmpty || length < 0)
        {
            // Defer to the BCL's own argument validation for the exact exception shape.
            return RandomNumberGenerator.GetString(choices, length);
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var result = new char[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = choices[GetInt32(0, choices.Length, env, node)];
        }

        return new string(result);
    }

    /// <summary>Deterministic shim for <see cref="RandomNumberGenerator.Shuffle{T}(Span{T})"/>.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="values">The values to shuffle in place.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Shuffle<T>(Span<T> values)
    {
        ResolveEnvironment("System.Security.Cryptography.RandomNumberGenerator.Shuffle", out var env, out var node);
        for (var i = values.Length - 1; i > 0; i--)
        {
            var selected = GetInt32(0, i + 1, env, node);
            (values[i], values[selected]) = (values[selected], values[i]);
        }
    }

    private static void ResolveEnvironment(
        string apiName,
        out ISimulationRuntimeEnvironment environment,
        out SimulationNodeIdentity? node)
    {
        var (_, resolved, resolvedNode) = SimulationRuntimeDispatch.RequireEnvironment(apiName);
        environment = resolved;
        node = resolvedNode;
    }

    private static int GetInt32(int fromInclusive, int toExclusive, ISimulationRuntimeEnvironment environment, SimulationNodeIdentity? node)
    {
        if (fromInclusive >= toExclusive)
        {
            throw new ArgumentException("fromInclusive must be less than toExclusive.");
        }

        var range = (uint)((long)toExclusive - fromInclusive);
        var threshold = unchecked(0U - range) % range;
        uint value;
        do
        {
            value = NextUInt32(environment, node);
        }
        while (value < threshold);

        return (int)(fromInclusive + (long)(value % range));
    }

    private static void FillHex(Span<char> destination, bool lowercase, ISimulationRuntimeEnvironment environment, SimulationNodeIdentity? node)
    {
        const string Upper = "0123456789ABCDEF";
        const string Lower = "0123456789abcdef";
        var alphabet = lowercase ? Lower : Upper;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = alphabet[(int)(NextUInt32(environment, node) & 0x0F)];
        }
    }

    private static uint NextUInt32(ISimulationRuntimeEnvironment environment, SimulationNodeIdentity? node)
    {
        Span<byte> bytes = stackalloc byte[4];
        environment.FillCryptoRandomBytes(node, bytes);
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }
}
