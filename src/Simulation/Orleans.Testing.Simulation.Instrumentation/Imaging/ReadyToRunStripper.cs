using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Imaging;

/// <summary>
/// Produces an IL-only assembly from a ReadyToRun (crossgen'd) input by round-tripping it through
/// Mono.Cecil: Cecil reads the embedded IL and drops the ahead-of-time native image on write, and
/// this helper additionally sets the <c>ILOnly</c> CLI flag so the result is a genuine managed-only
/// assembly the JIT compiles from IL. This guarantees no stale native code is emitted. Instrumentation
/// must still run before AOT/single-file publishing; stripping is a fallback for inputs that were
/// already crossgen'd, not a substitute for correct build ordering.
/// </summary>
public static class ReadyToRunStripper
{
    /// <summary>Writes an IL-only copy of a ReadyToRun assembly, dropping its native image.</summary>
    /// <param name="inputPath">The ReadyToRun assembly to strip.</param>
    /// <param name="outputPath">The path to write the IL-only assembly to.</param>
    public static void StripToIL(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(
            inputPath, new ReaderParameters { InMemory = true, ReadSymbols = false });

        foreach (ModuleDefinition module in definition.Modules)
        {
            // Cecil drops the ReadyToRun native header on write; assert the ILOnly flag so the result
            // is unambiguously a managed-only image rather than a hollow mixed-mode shell.
            module.Attributes |= ModuleAttributes.ILOnly;
        }

        definition.Write(outputPath);
    }
}
