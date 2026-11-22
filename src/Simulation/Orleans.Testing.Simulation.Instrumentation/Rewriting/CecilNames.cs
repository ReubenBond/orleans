using System.Globalization;
using Mono.Cecil;

namespace Orleans.Testing.Simulation.Instrumentation.Rewriting;

/// <summary>
/// Helpers for turning Mono.Cecil references into the stable, normalized names the rule model and
/// manifest use. All names follow Cecil's full-name convention (namespace-qualified, nested types
/// joined with <c>/</c>, open generics carrying their arity backtick).
/// </summary>
internal static class CecilNames
{
    /// <summary>
    /// Returns the normalized full name of a type reference: for a generic instance the open element
    /// type's full name (e.g. <c>System.Collections.Generic.List`1</c>), otherwise the full name.
    /// </summary>
    public static string NormalizedTypeFullName(TypeReference type) =>
        type is GenericInstanceType generic ? generic.ElementType.FullName : type.FullName;

    /// <summary>
    /// Returns the full name used to match a parameter type (Cecil's full name, preserving by-ref
    /// <c>&amp;</c>, array <c>[]</c>, and pointer <c>*</c> suffixes and generic arguments).
    /// </summary>
    public static string ParameterFullName(TypeReference type) => type.FullName;

    /// <summary>
    /// Returns a fully-qualified method name (declaring type full name + member name) for diagnostics
    /// and manifest locations.
    /// </summary>
    public static string FullyQualifiedMethodName(MethodReference method) =>
        $"{NormalizedTypeFullName(method.DeclaringType)}.{method.Name}";

    /// <summary>
    /// Formats an IL offset as a stable <c>IL_xxxx</c> label using invariant, lower-case hex.
    /// </summary>
    public static string FormatOffset(int offset) =>
        string.Create(CultureInfo.InvariantCulture, $"IL_{offset:x4}");
}
