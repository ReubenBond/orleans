namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// The form of debug symbols detected for an assembly being rewritten. Determines whether, and how,
/// the engine can preserve symbols in the rewritten output.
/// </summary>
public enum SymbolKind
{
    /// <summary>No debug symbols are present (or none could be read).</summary>
    None,

    /// <summary>A portable PDB stored alongside the assembly as a separate <c>.pdb</c> file.</summary>
    Portable,

    /// <summary>A portable PDB embedded inside the assembly image.</summary>
    Embedded,

    /// <summary>
    /// A symbol form the engine cannot preserve when rewriting (e.g. a native/Windows PDB). The
    /// rewrite proceeds without symbols and reports the loss explicitly.
    /// </summary>
    Unsupported,
}
