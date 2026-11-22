using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Records where pages are rented, deduplicated by call site, for leak reports
/// (docs/spec/verification.md §11.1).
/// </summary>
/// <remarks>
/// A rental captures its stack without source information and maps it to a call-site identifier, so a run which rents
/// millions of pages keeps one stack per distinct call site. Source files and lines are read from the portable PDBs
/// only when a leak report describes a call site.
/// </remarks>
internal static class PageRentalSites
{
    /// <summary>
    /// The identifier of a rental whose call site was not kept because <see cref="MaximumSites"/> were already kept.
    /// </summary>
    internal const int Unrecorded = -1;

    // Bounds the stacks kept for one test process.
    private const int MaximumSites = 4096;

    // Frames inside the accounting and the pool which every rental shares.
    private const int SkippedFrames = 3;

    private static readonly ConcurrentDictionary<ulong, int> Ids = new();
    private static readonly ConcurrentDictionary<int, StackTrace> Stacks = new();
    private static readonly ConcurrentDictionary<string, MetadataReaderProvider?> Symbols = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>
    /// Returns the identifier of the call site renting a page.
    /// </summary>
    internal static int Capture()
    {
        var stack = new StackTrace(SkippedFrames, fNeedFileInfo: false);
        var key = Key(stack);
        if (Ids.TryGetValue(key, out var id))
        {
            return id;
        }

        lock (Gate)
        {
            if (Ids.TryGetValue(key, out id))
            {
                return id;
            }

            if (Stacks.Count == MaximumSites)
            {
                return Unrecorded;
            }

            id = Stacks.Count;
            Stacks[id] = stack;
            Ids[key] = id;
            return id;
        }
    }

    /// <summary>
    /// Describes a call site, frame by frame, with the source file and line of each frame which has them.
    /// </summary>
    internal static string Describe(int id)
    {
        if (!Stacks.TryGetValue(id, out var stack))
        {
            return string.Create(CultureInfo.InvariantCulture, $"   (not recorded: more than {MaximumSites} distinct call sites rented pages)");
        }

        var description = new StringBuilder();
        foreach (var frame in stack.GetFrames())
        {
            if (frame.GetMethod() is not { } method || IsFramework(method))
            {
                continue;
            }

            if (description.Length > 0)
            {
                _ = description.AppendLine();
            }

            _ = description.Append("   at ").Append(Name(method));
            if (Locate(method, frame.GetILOffset()) is ({ } file, var line))
            {
                _ = description.Append(CultureInfo.InvariantCulture, $" in {file}:line {line}");
            }
        }

        return description.ToString();
    }

    // The runtime, the test framework, and the ASP.NET Core pipeline frame every call site alike.
    private static bool IsFramework(MethodBase method) =>
        method.DeclaringType?.Namespace is { } name
        && (name == "System" || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("Xunit", StringComparison.Ordinal));

    private static ulong Key(StackTrace stack)
    {
        // FNV-1a over each frame's method and IL offset, which identify a call site whatever the JIT tier. The runtime
        // returns one MethodBase per method, dynamic methods included, which have no metadata token.
        var hash = 14695981039346656037UL;
        foreach (var frame in stack.GetFrames())
        {
            hash = Mix(hash, frame.GetMethod() is { } method ? RuntimeHelpers.GetHashCode(method) : 0);
            hash = Mix(hash, frame.GetILOffset());
        }

        return hash;

        static ulong Mix(ulong hash, int value) => unchecked((hash ^ (uint)value) * 1099511628211UL);
    }

    private static string Name(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type is null)
        {
            return method.Name;
        }

        // An async method's frame is its state machine's MoveNext; name the method the state machine implements.
        if (method.Name == "MoveNext" && type.Name.StartsWith('<') && type.Name.IndexOf('>', StringComparison.Ordinal) is > 1 and var end && type.DeclaringType is { } owner)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{owner.FullName}.{type.Name[1..end]}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{type.FullName}.{method.Name}");
    }

    private static (string File, int Line)? Locate(MethodBase method, int offset)
    {
        var path = method.Module.Assembly.Location;
        if (offset < 0 || method is DynamicMethod || method.Module.Assembly.IsDynamic || path.Length == 0
            || Symbols.GetOrAdd(path, OpenSymbols) is not { } symbols)
        {
            return null;
        }

        try
        {
            var reader = symbols.GetMetadataReader();
            SequencePoint? located = null;
            foreach (var point in reader.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(method.MetadataToken)).GetSequencePoints())
            {
                if (point.Offset > offset)
                {
                    break;
                }

                if (!point.IsHidden)
                {
                    located = point;
                }
            }

            return located is { } sequence ? (reader.GetString(reader.GetDocument(sequence.Document).Name), sequence.StartLine) : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static MetadataReaderProvider? OpenSymbols(string path)
    {
        try
        {
            using var image = new PEReader(File.OpenRead(path));
            return image.TryOpenAssociatedPortablePdb(path, static pdb => File.Exists(pdb) ? File.OpenRead(pdb) : null, out var symbols, out _)
                ? symbols
                : null;
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
