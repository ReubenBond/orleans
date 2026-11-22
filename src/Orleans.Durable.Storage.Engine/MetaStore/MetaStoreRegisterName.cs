using System.Buffers;

namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// Validates register names and forms register keys.
/// </summary>
/// <remarks>
/// A register name is one or more components separated by <c>/</c>, each matching <c>[a-z0-9._-]{1,128}</c>,
/// compared case-sensitively. A register key is <c>{cluster:N}/{name}</c>, which each backend places below its
/// configured root (metastore.md §4, §7).
/// </remarks>
public static class MetaStoreRegisterName
{
    /// <summary>
    /// The maximum length of one name component.
    /// </summary>
    public const int MaximumComponentLength = 128;

    /// <summary>
    /// The character which separates name components, and the cluster from the name in a register key.
    /// </summary>
    public const char Separator = '/';

    private static readonly SearchValues<char> AllowedCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789._-");

    /// <summary>
    /// Gets a description of the valid name syntax.
    /// </summary>
    public static string Description =>
        "A MetaStore register name is one or more components separated by '/', each of 1 to 128 characters " +
        "from 'a'-'z', '0'-'9', '.', '_', and '-'.";

    /// <summary>
    /// Determines whether a register name is valid.
    /// </summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var span = name.AsSpan();
        foreach (var range in span.Split(Separator))
        {
            var component = span[range];
            if (component.Length is 0 or > MaximumComponentLength || component.ContainsAnyExcept(AllowedCharacters))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Throws when a register name is invalid.
    /// </summary>
    /// <param name="name">The register name.</param>
    /// <param name="paramName">The parameter name to report.</param>
    public static void Validate(string? name, string paramName)
    {
        if (name is null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (!IsValid(name))
        {
            throw new ArgumentException($"'{name}' is not a valid MetaStore register name. {Description}", paramName);
        }
    }

    /// <summary>
    /// Forms the register key <c>{cluster:N}/{name}</c>, before backend encoding.
    /// </summary>
    /// <param name="cluster">The cluster which owns the register; it must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="name">The register name.</param>
    /// <exception cref="ArgumentException">The cluster is empty or the name is invalid.</exception>
    public static string ToKey(Guid cluster, string name)
    {
        if (cluster == Guid.Empty)
        {
            throw new ArgumentException("A MetaStore register's cluster must not be empty.", nameof(cluster));
        }

        Validate(name, nameof(name));
        return string.Concat(cluster.ToString("N"), "/", name);
    }
}
