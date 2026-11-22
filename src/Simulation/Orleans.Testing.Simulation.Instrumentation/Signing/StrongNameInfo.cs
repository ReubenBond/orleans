namespace Orleans.Testing.Simulation.Instrumentation.Signing;

/// <summary>
/// The strong-name identity of an assembly: whether it is signed/delay-signed/unsigned and, if it
/// carries a public key, that key's public-key token (the lower-case hex of the 8-byte token used in
/// assembly references).
/// </summary>
/// <param name="Status">The observed strong-name state.</param>
/// <param name="PublicKeyToken">The lower-case hex public-key token, or <see langword="null"/> if unsigned.</param>
public readonly record struct StrongNameInfo(StrongNameStatus Status, string? PublicKeyToken)
{
    /// <summary>Gets a value indicating whether the assembly carries a public key (signed or delay-signed).</summary>
    public bool HasPublicKey => Status is StrongNameStatus.StrongNameSigned or StrongNameStatus.DelaySigned;

    /// <summary>Gets the strong-name state of an unsigned assembly.</summary>
    public static StrongNameInfo NotSigned => new(StrongNameStatus.None, null);
}
