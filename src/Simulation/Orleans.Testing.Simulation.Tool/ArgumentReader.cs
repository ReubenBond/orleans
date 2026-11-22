namespace Orleans.Testing.Simulation.Tool;

/// <summary>
/// A minimal, dependency-free command-line reader for a single command's arguments. It parses
/// <c>--name value</c> and <c>--flag</c> forms (repeatable options are collected), collects free
/// positional arguments, and validates that every consumed option was recognized. Keeping the parser
/// small and explicit avoids taking an external command-line dependency and keeps output
/// deterministic.
/// </summary>
internal sealed class ArgumentReader
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    private ArgumentReader()
    {
    }

    /// <summary>Gets the free positional arguments, in order.</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>
    /// Parses the given arguments. Tokens after <c>--</c> are always treated as positional. An option
    /// token is <c>--name</c>; it consumes the following token as its value unless the next token is
    /// another option or absent, in which case it is recorded as a boolean flag.
    /// </summary>
    /// <param name="args">The raw arguments (excluding the command name).</param>
    /// <param name="valueOptions">Option names (without <c>--</c>) that always take a value.</param>
    /// <returns>The populated reader.</returns>
    /// <exception cref="UsageException">An option that requires a value was given none.</exception>
    public static ArgumentReader Parse(IReadOnlyList<string> args, ISet<string> valueOptions)
    {
        var reader = new ArgumentReader();
        bool positionalOnly = false;
        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (positionalOnly)
            {
                reader._positional.Add(token);
                continue;
            }

            if (token == "--")
            {
                positionalOnly = true;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                string name = token[2..];
                if (name.Length == 0)
                {
                    throw new UsageException("An option name must follow '--'.");
                }

                bool wantsValue = valueOptions.Contains(name);
                if (wantsValue)
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new UsageException($"Option '--{name}' requires a value.");
                    }

                    string value = args[++i];
                    if (!reader._options.TryGetValue(name, out List<string>? list))
                    {
                        list = [];
                        reader._options[name] = list;
                    }

                    list.Add(value);
                }
                else
                {
                    reader._flags.Add(name);
                }

                continue;
            }

            reader._positional.Add(token);
        }

        return reader;
    }

    /// <summary>Gets the last value supplied for a value option, or <paramref name="fallback"/>.</summary>
    public string? GetString(string name, string? fallback = null)
    {
        _consumed.Add(name);
        return _options.TryGetValue(name, out List<string>? list) && list.Count > 0 ? list[^1] : fallback;
    }

    /// <summary>Gets every value supplied for a repeatable value option, in order.</summary>
    public IReadOnlyList<string> GetMany(string name)
    {
        _consumed.Add(name);
        return _options.TryGetValue(name, out List<string>? list) ? list : [];
    }

    /// <summary>Gets whether a boolean flag was present.</summary>
    public bool GetFlag(string name)
    {
        _consumed.Add(name);
        return _flags.Contains(name);
    }

    /// <summary>Gets whether an option or flag was supplied without marking it consumed.</summary>
    public bool IsSupplied(string name) => _options.ContainsKey(name) || _flags.Contains(name);

    /// <summary>Throws if any supplied option or flag was never queried by the command.</summary>
    /// <exception cref="UsageException">An unrecognized option or flag was supplied.</exception>
    public void EnsureAllConsumed()
    {
        foreach (string name in _options.Keys.Concat(_flags))
        {
            if (!_consumed.Contains(name))
            {
                throw new UsageException($"Unknown option '--{name}'.");
            }
        }
    }
}

/// <summary>An error in how a command was invoked; maps to <see cref="ExitCode.UsageError"/>.</summary>
internal sealed class UsageException(string message) : Exception(message);
