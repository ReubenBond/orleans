using Orleans.Testing.Simulation.Runtime.Random;

namespace Orleans.Testing.Simulation;

/// <summary>
/// <para>
/// Derives stable, deterministic <see cref="int"/> seeds from strings such as test names, so
/// callers do not need to hand-pick or hard-code arbitrary integer seeds for
/// <see cref="SimulationCluster"/>/<see cref="SimulationRandom"/>.
/// </para>
/// <para>
/// This intentionally never uses <see cref="string.GetHashCode()"/> (or
/// <see cref="object.GetHashCode()"/> in general): the runtime explicitly documents that hash
/// code as unstable across processes, .NET versions, and even multiple runs of the same process
/// (string hashing is randomized per-process by default). Using it here would silently break
/// reproducibility - the entire point of a "seed" - the moment a test suite runs on a different
/// machine, a different .NET version, or even just a second time.
/// </para>
/// <para>
/// Instead, the seed is derived by SHA-256 hashing the UTF-8 bytes of the input and interpreting
/// the first four bytes of the digest as a big-endian, signed <see cref="int"/> - see
/// <see cref="SimulationStableHash"/>, the shared primitive this type delegates to (also used by
/// <see cref="Orleans.Testing.Simulation.Runtime.Random.SimulationSeedAuthority"/>'s per-domain derivation, so
/// "derive a stable seed from a string" has exactly one implementation across the codebase).
/// SHA-256 is a fixed, versioned algorithm with no process- or platform-specific behavior, and
/// UTF-8 encoding is likewise fixed and unambiguous, so the same string always produces the same
/// seed on any machine, any .NET version, and any run - including across separate processes and
/// separate machines, which is what "cross-process-safe" means here. The result can be negative:
/// any <see cref="int"/> value is a valid seed for <see cref="Random"/>/<see cref="SimulationRandom"/>.
/// </para>
/// </summary>
public static class SimulationSeed
{
    /// <summary>
    /// Derives a deterministic seed from a single string, such as a test name.
    /// </summary>
    /// <param name="value">The string to derive a seed from.</param>
    /// <returns>
    /// A deterministic <see cref="int"/> seed: the first four bytes of the SHA-256 hash of the
    /// UTF-8 encoding of <paramref name="value"/>, interpreted as a big-endian signed integer.
    /// The same <paramref name="value"/> always produces the same result, on any machine, .NET
    /// version, or process.
    /// </returns>
    public static int FromString(string value) => SimulationStableHash.ToInt32(value);

    /// <summary>
    /// Derives a single deterministic seed from multiple string components (for example, a test
    /// class name and a test method name), combined in order so that the seed depends on both the
    /// content and the position of each component.
    /// </summary>
    /// <param name="values">The components to combine, in order.</param>
    /// <returns>A deterministic seed derived from all of <paramref name="values"/>; see <see cref="FromString(string)"/> for the underlying algorithm.</returns>
    public static int FromStrings(IEnumerable<string> values) => SimulationStableHash.ToInt32(values);

    /// <summary>
    /// Derives a single deterministic seed from multiple string components (for example, a test
    /// class name and a test method name). See <see cref="FromStrings(IEnumerable{string})"/>.
    /// </summary>
    /// <param name="values">The components to combine, in order.</param>
    /// <returns>A deterministic seed derived from all of <paramref name="values"/>.</returns>
    public static int FromStrings(params string[] values) => FromStrings((IEnumerable<string>)values);
}
