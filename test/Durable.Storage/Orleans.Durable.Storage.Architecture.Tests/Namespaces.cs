namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>The namespace rules the architecture tests share (spec README §2.6).</summary>
internal static class Namespaces
{
    private const string AbstractionsSuffix = ".Abstractions";

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="namespaceName"/> or a namespace within it.</summary>
    public static bool IsWithin(string candidate, string namespaceName) =>
        string.Equals(candidate, namespaceName, StringComparison.Ordinal)
        || candidate.StartsWith(namespaceName + ".", StringComparison.Ordinal);

    /// <summary>The namespace a project's namespaces start with: its name, except that an <c>.Abstractions</c> project uses its layer's.</summary>
    public static string OfProject(string project) =>
        project.EndsWith(AbstractionsSuffix, StringComparison.Ordinal) ? project[..^AbstractionsSuffix.Length] : project;

    /// <summary>
    /// The namespaces a project's types may use: for a production project, the roots of the components it compiles and
    /// the namespaces of the types they list (<see cref="Components"/>); for any other, its own (<see cref="OfProject"/>).
    /// </summary>
    public static string[] Allowed(string project)
    {
        var map = Components.Production;
        var components = map.ByName.Values.Where(component => component.Assembly == project).Select(static component => component.Name).ToHashSet(StringComparer.Ordinal);
        if (components.Count == 0)
        {
            return [OfProject(project)];
        }

        return
        [
            .. map.ByName.Values.Where(component => components.Contains(component.Name)).SelectMany(static component => component.Roots)
                .Concat(map.Members.Where(member => components.Contains(member.Value)).Select(static member => member.Key[..member.Key.LastIndexOf('.')]))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }
}
