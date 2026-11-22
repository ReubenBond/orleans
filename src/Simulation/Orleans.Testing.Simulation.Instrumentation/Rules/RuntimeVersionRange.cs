namespace Orleans.Testing.Simulation.Instrumentation.Rules;

/// <summary>
/// An inclusive range of .NET runtime feature versions (see <see cref="RuntimeFeatureVersion"/>) a
/// <see cref="RewriteRule"/> supports. Either bound may be open (<see langword="null"/>). The default
/// <see cref="All"/> matches every version.
/// </summary>
/// <param name="Minimum">The inclusive lower bound, or <see langword="null"/> for no lower bound.</param>
/// <param name="Maximum">The inclusive upper bound, or <see langword="null"/> for no upper bound.</param>
public readonly record struct RuntimeVersionRange(Version? Minimum, Version? Maximum)
{
    /// <summary>Gets a range that includes every version.</summary>
    public static RuntimeVersionRange All => default;

    /// <summary>Creates a range with only a lower bound.</summary>
    public static RuntimeVersionRange AtLeast(Version minimum) => new(minimum, null);

    /// <summary>Creates a range with only an upper bound.</summary>
    public static RuntimeVersionRange AtMost(Version maximum) => new(null, maximum);

    /// <summary>Returns <see langword="true"/> if <paramref name="version"/> falls within this range.</summary>
    /// <param name="version">The runtime feature version.</param>
    /// <returns><see langword="true"/> if the range includes <paramref name="version"/>.</returns>
    public bool Includes(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return (Minimum is null || version >= Minimum) && (Maximum is null || version <= Maximum);
    }

    /// <summary>Returns the versions both ranges include.</summary>
    /// <param name="other">The other range.</param>
    /// <returns>The intersection, which is empty (its minimum exceeds its maximum) when the ranges are disjoint.</returns>
    public RuntimeVersionRange Intersect(RuntimeVersionRange other) => new(
        Minimum is null ? other.Minimum : other.Minimum is null || Minimum >= other.Minimum ? Minimum : other.Minimum,
        Maximum is null ? other.Maximum : other.Maximum is null || Maximum <= other.Maximum ? Maximum : other.Maximum);

    /// <summary>Returns <see langword="true"/> if both bounds are open or feature versions.</summary>
    public bool HasFeatureVersionBounds =>
        (Minimum is null || RuntimeFeatureVersion.IsFeatureVersion(Minimum))
        && (Maximum is null || RuntimeFeatureVersion.IsFeatureVersion(Maximum));

    /// <summary>Returns the range for people, such as <c>11.0</c>, <c>11.0 to 12.0</c>, or <c>11.0 or later</c>.</summary>
    public string ToDisplayString() => Runtime.SupportedRuntimesMarker.DescribeRange(Minimum, Maximum);

    /// <summary>Returns a stable canonical string for signature hashing.</summary>
    public string ToCanonicalString()
    {
        var canonical = new CanonicalEncoding(nameof(RuntimeVersionRange));
        canonical.AddString(nameof(Minimum), Minimum?.ToString());
        canonical.AddString(nameof(Maximum), Maximum?.ToString());
        return canonical.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => ToCanonicalString();
}
