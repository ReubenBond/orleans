namespace Orleans.Testing.Simulation.Instrumentation.Signing;

/// <summary>
/// The strong-name state of an assembly, as observed from its metadata and CLI header.
/// </summary>
public enum StrongNameStatus
{
    /// <summary>The assembly has no public key: it is not strong-named.</summary>
    None,

    /// <summary>
    /// The assembly carries a public key but the <c>StrongNameSigned</c> CLI flag is not set: it is
    /// delay-signed and must be signed (or re-signed) before it can be loaded with verification.
    /// </summary>
    DelaySigned,

    /// <summary>
    /// The assembly carries a public key and the <c>StrongNameSigned</c> CLI flag is set. This covers
    /// both a fully signed assembly and a public-signed one; distinguishing the two requires
    /// verifying the signature bytes, which this inspector does not claim to do.
    /// </summary>
    StrongNameSigned,
}
