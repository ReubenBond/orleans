using Orleans.Testing.Simulation.Instrumentation.Imaging;
using Orleans.Testing.Simulation.Instrumentation.Signing;

namespace Orleans.Testing.Simulation.Instrumentation.Inspection;

/// <summary>
/// The decoded idempotence metadata recorded on an assembly the engine has rewritten.
/// </summary>
/// <param name="EngineVersion">The engine version that performed the rewrite.</param>
/// <param name="RuleSetId">The identity of the applied rule set.</param>
/// <param name="RuleSetVersion">The version of the applied rule set.</param>
/// <param name="Signature">The stable content hash of the applied rule set and engine version.</param>
/// <param name="OptionsFingerprint">The semantic rewrite-options fingerprint, or empty for a legacy marker.</param>
public readonly record struct InstrumentationMarker(
    string EngineVersion,
    string RuleSetId,
    string RuleSetVersion,
    string Signature,
    string OptionsFingerprint);

/// <summary>The debug-symbol form associated with an assembly.</summary>
public enum SymbolPresence
{
    /// <summary>No portable-PDB symbols were found (neither embedded nor a sidecar file).</summary>
    None,

    /// <summary>A sidecar <c>*.pdb</c> file is present next to the assembly.</summary>
    Pdb,

    /// <summary>Portable-PDB symbols are embedded in the assembly image.</summary>
    Embedded,
}

/// <summary>
/// A deterministic, fact-only inspection of a single file: whether it is a managed assembly, its
/// image shape, strong-name state, symbol form, and any Clockwork idempotence marker. Non-managed or
/// unreadable files are reported with <see cref="IsManaged"/> set to <see langword="false"/>.
/// </summary>
/// <param name="Path">The absolute path of the inspected file.</param>
/// <param name="IsManaged">Whether the file is a managed assembly.</param>
/// <param name="Image">The PE/CLI image shape (default when the file is not a valid PE image).</param>
/// <param name="StrongName">The strong-name state (<see cref="StrongNameStatus.None"/> when unmanaged).</param>
/// <param name="Symbols">The debug-symbol form.</param>
/// <param name="Marker">The idempotence marker, or <see langword="null"/> if the assembly has not been rewritten by Orleans.Testing.Simulation.</param>
public sealed record AssemblyInspection(
    string Path,
    bool IsManaged,
    AssemblyImageInfo Image,
    StrongNameInfo StrongName,
    SymbolPresence Symbols,
    InstrumentationMarker? Marker)
{
    /// <summary>Gets a value indicating whether Clockwork has already rewritten this assembly.</summary>
    public bool IsInstrumented => Marker is not null;
}
