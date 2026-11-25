using System.Text.RegularExpressions;
using static Orleans.Durable.Storage.Architecture.Tests.Namespaces;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>The roles spec README §2.5 gives a component.</summary>
internal enum ComponentKind
{
    /// <summary>A layer, a layer's contract, or a base beneath every layer.</summary>
    Layer,

    /// <summary>A technology package: one technology's providers for several layers' contracts.</summary>
    Provider,

    /// <summary>A composition, which depends on layers and on which no layer depends.</summary>
    Composition,
}

/// <summary>
/// A unit of spec README §2.5's dependency rules: a layer's contract or implementation, a base, a technology package, or a
/// composition. A component claims the types of its namespace <see cref="Roots"/>, the longest root deciding, and the
/// types <c>ComponentMembers.txt</c> lists for it, which a component whose namespace another shares claims instead of a
/// root. It is compiled into <see cref="Assembly"/>, and may use the types of its <see cref="Dependencies"/> and of what
/// they reach, the external assemblies its <see cref="Technologies"/> name and those of what it reaches, and the
/// non-public types and members of a component which lists it among its <see cref="Grants"/>.
/// </summary>
internal sealed record Component(string Name, ComponentKind Kind, string[] Dependencies)
{
    /// <summary>The assembly which compiles the component's types.</summary>
    public string Assembly { get; init; } = Name;

    /// <summary>The namespaces whose types the component claims, with those of every namespace within them.</summary>
    public string[] Roots { get; init; } = [Name];

    /// <summary>Whether the component is a contract of spec README §2.6, which technology packages may use.</summary>
    public bool IsContract { get; init; }

    /// <summary>
    /// The external assemblies the component uses beyond the platform's, each by name or by a prefix ending in a dot: the
    /// packages it takes.
    /// </summary>
    public string[] Technologies { get; init; } = [];

    /// <summary>
    /// The components which may use this one's non-public types and members, each with why: the
    /// <c>InternalsVisibleTo</c> grants between assemblies, which keep holding where components share one.
    /// </summary>
    public IReadOnlyDictionary<string, string> Grants { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Projects of another Orleans family which the component's project references, such as the consensus family's.</summary>
    public string[] ExternalProjects { get; init; } = [];

    /// <summary>
    /// Whether the component takes Orleans' runtime although it is no composition: a technology package whose technology
    /// Orleans hosts, as the native consensus's voters are system targets on silos.
    /// </summary>
    public bool HostsOnOrleans { get; init; }
}

/// <summary>
/// A table of components (<see cref="Component"/>) and the listed members of the namespaces they share, which decides the
/// component of every type in its <see cref="Scope"/>.
/// </summary>
internal sealed class ComponentMap
{
    private readonly IReadOnlyList<(string Root, Component Component)> _roots;

    public ComponentMap(IReadOnlyList<string> scope, IEnumerable<Component> components, IReadOnlyDictionary<string, string> members)
    {
        Scope = scope;
        ByName = components.ToDictionary(static component => component.Name, StringComparer.Ordinal);
        Members = members;
        _roots = [.. ByName.Values.SelectMany(static component => component.Roots.Select(root => (root, component)))];
        var unknown = members.Values.Concat(ByName.Values.SelectMany(static component => component.Dependencies.Concat(component.Grants.Keys)))
            .Where(name => !ByName.ContainsKey(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException("The component table names unknown components: " + string.Join(", ", unknown));
        }
    }

    /// <summary>The namespaces each of whose types belongs to a component; a type outside them, such as one a compiler generates, belongs to none.</summary>
    public IReadOnlyList<string> Scope { get; }

    public IReadOnlyDictionary<string, Component> ByName { get; }

    /// <summary>The listed types, by full name, each with the component which claims it.</summary>
    public IReadOnlyDictionary<string, string> Members { get; }

    /// <summary>Whether a type of <paramref name="typeNamespace"/> must belong to a component.</summary>
    public bool IsInScope(string typeNamespace) => Scope.Any(root => IsWithin(typeNamespace, root));

    /// <summary>
    /// The component of a type, by its namespace and its full name, whose outermost type decides: the component which
    /// lists it, or else the one whose root is the longest which contains its namespace; null when none claims it.
    /// </summary>
    public Component? Of(string typeNamespace, string fullName)
    {
        if (Members.TryGetValue(fullName.Split('+')[0], out var listed))
        {
            return ByName[listed];
        }

        return _roots
            .Where(entry => IsWithin(typeNamespace, entry.Root))
            .OrderByDescending(static entry => entry.Root.Length)
            .Select(static entry => entry.Component)
            .FirstOrDefault();
    }

    /// <summary>The components <paramref name="component"/> depends on, directly or through others.</summary>
    public HashSet<string> Reachable(string component)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([component]);
        while (pending.TryPop(out var next))
        {
            foreach (var dependency in ByName[next].Dependencies)
            {
                if (reachable.Add(dependency))
                {
                    pending.Push(dependency);
                }
            }
        }

        return reachable;
    }

    /// <summary>Whether <paramref name="component"/> or a component it reaches takes the external assembly <paramref name="assembly"/>.</summary>
    public bool Takes(string component, string assembly) =>
        Reachable(component).Append(component).Any(name => ByName[name].Technologies.Any(technology =>
            technology.EndsWith('.')
                ? assembly.StartsWith(technology, StringComparison.Ordinal)
                : string.Equals(assembly, technology, StringComparison.Ordinal)));

    /// <summary>Reads a members file: a <c>[component]</c> line, then the full name of each type it claims; <c>#</c> starts a comment.</summary>
    public static IReadOnlyDictionary<string, string> ReadMembers(string path)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        string? component = null;
        foreach (var line in File.ReadAllLines(path).Select(static line => line.Trim()))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                component = line[1..^1];
            }
            else
            {
                members.Add(line, component ?? throw new InvalidDataException($"{path}: {line} precedes every component."));
            }
        }

        return members;
    }
}

/// <summary>
/// The production components and the assemblies which compile them (spec README §2.5, §2.6): the managed layers share the
/// Engine, each technology package has an assembly of its own, and the composition has its own. The table is the one
/// authority for both checks: <see cref="LayeringTests"/> compares each project's references with its components'
/// dependencies, and <see cref="ComponentDependencyTests"/> checks every type's references against the components, which
/// keep holding where several components share an assembly.
/// </summary>
internal static partial class Components
{
    public const string Engine = "Orleans.Durable.Storage.Engine";
    public const string Diagnostics = "Orleans.Durable.Storage.Diagnostics";
    public const string Composition = "Orleans.Durable.Storage";
    public const string AzureStorage = "Orleans.Durable.Storage.AzureStorage";
    public const string S3 = "Orleans.Durable.Storage.S3";
    public const string GoogleCloud = "Orleans.Durable.Storage.GoogleCloud";
    public const string FileSystem = "Orleans.Durable.Storage.FileSystem";
    public const string CasPaxos = "Orleans.Durable.Storage.CasPaxos";
    public const string RocksDb = "Orleans.Durable.Storage.RocksDb";

    private static readonly Lazy<ComponentMap> ProductionMap = new(static () => new(
        ["Orleans.Durable.Storage", "OrleansCodeGen"],
        Table,
        ComponentMap.ReadMembers(Path.Combine(Repository.StorageRoot, "Orleans.Durable.Storage.Architecture.Tests", "ComponentMembers.txt"))));

    public static ComponentMap Production => ProductionMap.Value;

    private static IEnumerable<Component> Table =>
    [
        // A key's history across the live ledgers and the store they drain into, whose contract the archive stores
        // implement (archive.md).
        new("Orleans.Durable.Storage.Engine.Archive", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.Ledgers"])
        {
            Assembly = Engine,
            IsContract = true,
            Technologies = ["Polly.Core", "System.IO.Hashing"],
            Grants = Granted(
                ("Orleans.Durable.Storage.Engine.Archive.Logs", "the drains' shared scan, rows, and memory bound (DrainScan, DrainRows, ByteBudget, MessageFolds), and the archive write builder"),
                ("Orleans.Durable.Storage.Engine.Archive.Lsm", "the drains' shared scan, rows, and memory bound (DrainScan, DrainRows, ByteBudget, MessageFolds)")),
        },

        // An archive store: archive logs implement the Archive's contract over the ledgers' rows and an archive-log
        // store, with durable storage's objects under durable-storage/ in an object store (archive-logs.md §3).
        new("Orleans.Durable.Storage.Engine.Archive.Logs", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Archive", "Orleans.Durable.Storage.Engine.Archive.Logs.Abstractions", "Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.Ledgers"])
        {
            Assembly = Engine,
            IsContract = true,
            Technologies = ["Polly.Core", "System.IO.Hashing"],
        },
        new("Orleans.Durable.Storage.Engine.Archive.Logs.Abstractions", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine.Buffers"])
        {
            Assembly = Engine,
            Roots = [],
            IsContract = true,
        },

        // An archive store: the LSM over an object store (archive-lsm.md); its volatile-disk block cache uses the WAL's file-system seam.
        new("Orleans.Durable.Storage.Engine.Archive.Lsm", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Archive", "Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.GroupCommit", "Orleans.Durable.Storage.Engine.Ledgers", "Orleans.Durable.Storage.Engine.Wal"])
        {
            Assembly = Engine,
            Technologies = ["Polly.Core", "System.IO.Hashing"],
        },

        new("Orleans.Durable.Storage.S3", ComponentKind.Provider, ["Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine.MetaStore.Abstractions"])
        {
            Assembly = S3,
            Technologies = ["AWSSDK."],
        },
        new("Orleans.Durable.Storage.AzureStorage", ComponentKind.Provider, ["Orleans.Durable.Storage.Engine.Archive.Logs.Abstractions", "Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.MetaStore.Abstractions"])
        {
            Assembly = AzureStorage,
            Technologies = ["Azure.", "System.IO.Hashing", "System.Memory.Data"],
        },
        // The object store contract, which names no caller: the LSM's objects and archive logs' chunks and Clog's
        // objects lie in object stores the technology packages implement (object-ledgers.md §3.1, archive-logs.md §7.4).
        // Its writers' one report of content changed after verification stops the process through the diagnostics.
        new("Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", ComponentKind.Layer, ["Orleans.Durable.Storage.Diagnostics", "Orleans.Durable.Storage.Engine.Buffers"])
        {
            Assembly = Engine,
            Roots = ["Orleans.Durable.Storage.Engine.ObjectStore"],
            IsContract = true,
            Technologies = ["System.IO.Hashing"],
        },
        new("Orleans.Durable.Storage.Engine.Buffers", ComponentKind.Layer, [])
        {
            Assembly = Engine,
            IsContract = true,
            Grants = Granted(("Orleans.Durable.Storage.Engine.Wal", "the WAL writes a buffer's pages in place through its page segments")),
        },

        // The shared base beneath every layer: the fatal-error handler and its exit codes (conventions.md §14), in an
        // ordinary-IL assembly of their own which simulations load unrewritten (verification.md §7.1).
        new("Orleans.Durable.Storage.Diagnostics", ComponentKind.Layer, [])
        {
            Assembly = Diagnostics,
            IsContract = true,
        },

        // The helpers which several layers and technology packages share, in the Engine's root namespace: bounded
        // jittered backoff, a decoder's rejection, and the fail-stop answers of a file system's contention
        // (conventions.md §14).
        new("Orleans.Durable.Storage.Engine", ComponentKind.Layer, ["Orleans.Durable.Storage.Diagnostics"])
        {
            Assembly = Engine,
            IsContract = true,
        },

        // The group committer beneath the layers whose values it writes: the ledger map's, the manifest's, and the node
        // registry's committers (conventions.md §6), over buffers and the admission gate.
        new("Orleans.Durable.Storage.Engine.GroupCommit", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Buffers", "Orleans.Durable.Storage.Engine.Threading"]) { Assembly = Engine },

        // The MetaStore's Fast CASPaxos registers and the native consensus which serves them on the cluster's silos
        // (caspaxos.md): the one technology package which takes Orleans' runtime, since its voters are system targets
        // addressed over Orleans RPC, which use the runtime's internals under the grants Orleans.Durable holds. Orleans'
        // code generator writes the voter messages' serializers and proxies in its OrleansCodeGen namespace, and its
        // consensus SQLite stores report their storage failures through the diagnostics (conventions.md §14).
        new("Orleans.Durable.Storage.CasPaxos", ComponentKind.Provider, ["Orleans.Durable.Storage.Engine.MetaStore.Abstractions", "Orleans.Durable.Storage.Diagnostics"])
        {
            Assembly = CasPaxos,
            Roots = ["Orleans.Durable.Storage.CasPaxos", "OrleansCodeGen.Orleans.Durable.Storage.CasPaxos", "OrleansCodeGen.OrleansDurableStorageCasPaxos"],
            Technologies = ["Microsoft.Extensions.Configuration.Binder", "Orleans."],
            ExternalProjects = ["Orleans.Consensus.CasPaxos.Server", "Orleans.Consensus.CasPaxos.Sqlite"],
            HostsOnOrleans = true,
        },
        new("Orleans.Durable.Storage.FileSystem", ComponentKind.Provider, ["Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine.MetaStore.Abstractions", "Orleans.Durable.Storage.Engine.Wal"])
        {
            Assembly = FileSystem,
        },
        new("Orleans.Durable.Storage.GoogleCloud", ComponentKind.Provider, ["Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.MetaStore.Abstractions"])
        {
            Assembly = GoogleCloud,
            Technologies = ["Google.", "Grpc.", "Polly.Core"],
        },

        // Journals over rows: the history read through the Archive, which names no store, the ledgers' writers and
        // map, and the node's journal index (journals.md §3, §8.3).
        new("Orleans.Durable.Storage.Engine.Journals", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Archive", "Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.Ledgers", "Orleans.Durable.Storage.Engine.Threading"])
        {
            Assembly = Engine,
            IsContract = true,
            Technologies = ["System.IO.Hashing"],
        },

        // The ledger protocol reaches its replicas only through the replica contract, never a provider (decision 12):
        // the component's one provider, the memory provider, lies in a namespace which no other type of the layer
        // references (NoLayerTypeOutsideItsMemoryProviderReferencesIt). The map's view and committer use the
        // MetaStore's register contract. Object-store ledgers reach their objects only through the object store contract
        // (object-ledgers.md §3.1).
        new("Orleans.Durable.Storage.Engine.Ledgers", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.GroupCommit", "Orleans.Durable.Storage.Engine.Ledgers.Abstractions", "Orleans.Durable.Storage.Engine.MetaStore.Abstractions", "Orleans.Durable.Storage.Engine.ObjectStore.Abstractions", "Orleans.Durable.Storage.Engine.Threading"])
        {
            Assembly = Engine,
            Technologies = ["Polly.Core", "System.IO.Hashing"],
            Grants = Granted(
                ("Orleans.Durable.Storage.Engine.Archive", "the history's read decodes rows (RowKinds, Varint) and reads ranges through LedgerReader"),
                ("Orleans.Durable.Storage.Engine.Journals", "admission writes through LedgerWriterRegistry and decodes versions (RowVersionCodec)"),
                ("Orleans.Durable.Storage.Engine.Archive.Logs", "the drains decode rows (Varint) and order a segment's keys (LedgerSegmentDictionary)")),
        },
        new("Orleans.Durable.Storage.Engine.Ledgers.Abstractions", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Buffers"])
        {
            Assembly = Engine,
            Roots = [],
            IsContract = true,
        },
        new("Orleans.Durable.Storage.Engine.Ledgers.Store", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.Ledgers.Abstractions", "Orleans.Durable.Storage.Engine.Threading", "Orleans.Durable.Storage.Engine.Wal"])
        {
            Assembly = Engine,
            Technologies = ["System.IO.Hashing"],
        },
        new("Orleans.Durable.Storage.Engine.MetaStore", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.MetaStore.Abstractions"]) { Assembly = Engine },
        new("Orleans.Durable.Storage.Engine.MetaStore.Abstractions", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Buffers"])
        {
            Assembly = Engine,
            Roots = [],
            IsContract = true,
        },

        // The Orleans composition (orleans.md §2): each silo's node over its LedgerStore, the node registry in a MetaStore
        // register, the replica transport, whose per-node budgets are the Ledgers layer's, the grains which host the
        // controllers' decisions, and the node's Journals. Orleans' code generator writes its serializers and proxies in
        // OrleansCodeGen namespaces. The silo's options bind through the configuration binding generator, whose runtime,
        // Microsoft.Extensions.Configuration.Binder, Orleans' own closure brings (orleans.md §2).
        new(
            "Orleans.Durable.Storage",
            ComponentKind.Composition,
            [
                "Orleans.Durable.Storage.Engine.Archive",
                "Orleans.Durable.Storage.Engine.Archive.Logs",
                "Orleans.Durable.Storage.Engine.Archive.Lsm",
                "Orleans.Durable.Storage.Engine",
                "Orleans.Durable.Storage.Engine.GroupCommit",
                "Orleans.Durable.Storage.Engine.Journals",
                "Orleans.Durable.Storage.Engine.Ledgers",
                "Orleans.Durable.Storage.Engine.Ledgers.Abstractions",
                "Orleans.Durable.Storage.Engine.Ledgers.Store",
                "Orleans.Durable.Storage.Engine.MetaStore.Abstractions",
                "Orleans.Durable.Storage.Engine.Cluster.Control",
                "Orleans.Durable.Storage.Engine.Threading",
                "Orleans.Durable.Storage.Engine.Wal",
            ])
        {
            Assembly = Composition,
            Roots = ["Orleans.Durable.Storage", "OrleansCodeGen"],
            Technologies = ["Microsoft.Extensions.Configuration.Binder", "Orleans.", "Polly.Core", "System.IO.Hashing"],
        },

        // The controllers' decisions over the ledger map's transitions and the LSM's and archive logs' workers, which name
        // no Orleans type: control-plane policy over the Engine's layers, compiled into the Engine so a simulation of them
        // loads no Orleans assembly (orleans.md §2, verification.md §7.1).
        new("Orleans.Durable.Storage.Engine.Cluster.Control", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Archive", "Orleans.Durable.Storage.Engine.Archive.Logs", "Orleans.Durable.Storage.Engine.Archive.Lsm", "Orleans.Durable.Storage.Engine.Ledgers"])
        {
            Assembly = Engine,
            Grants = Granted(("Orleans.Durable.Storage", "the grains host the controllers' decisions and carry their messages")),
        },
        // The journal index's RocksDB provider and the dedicated threads its blocking calls run on, apart from the
        // Engine so that RocksDB's native library is the package's alone.
        new("Orleans.Durable.Storage.RocksDb", ComponentKind.Provider, ["Orleans.Durable.Storage.Engine", "Orleans.Durable.Storage.Engine.Journals", "Orleans.Durable.Storage.Engine.Ledgers.Abstractions", "Orleans.Durable.Storage.Engine.Threading"])
        {
            Assembly = RocksDb,
            Technologies = ["RocksDbSharp"],
        },
        new("Orleans.Durable.Storage.Engine.Threading", ComponentKind.Layer, [])
        {
            Assembly = Engine,
            IsContract = true,
        },
        new("Orleans.Durable.Storage.Engine.Wal", ComponentKind.Layer, ["Orleans.Durable.Storage.Engine.Buffers", "Orleans.Durable.Storage.Engine"])
        {
            Assembly = Engine,
            IsContract = true,
            Technologies = ["System.IO.Hashing"],
        },
    ];
    /// <summary>
    /// The component of a source file under src: that of the first type it declares, in the first namespace it
    /// declares; null for a file which declares no type, such as an assembly's attributes.
    /// </summary>
    public static Component? OfSource(string file)
    {
        var text = File.ReadAllText(file);
        var declaredNamespace = NamespaceDeclaration().Match(text);
        var declaredType = TypeDeclaration().Match(text);
        if (!declaredNamespace.Success || !declaredType.Success)
        {
            return null;
        }

        var typeNamespace = declaredNamespace.Groups["namespace"].Value;
        return Production.Of(typeNamespace, typeNamespace + "." + declaredType.Groups["type"].Value)
            ?? throw new InvalidOperationException($"{file} belongs to no component.");
    }

    [GeneratedRegex(@"^\s*namespace\s+(?<namespace>[\w.]+)", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex NamespaceDeclaration();

    [GeneratedRegex(
        @"^\s*(?:\[[^\]\n]*\]\s*)*(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?|delegate\s+[\w<>?,\s.]+?|union)\s+(?<type>\w+)",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 10_000)]
    private static partial Regex TypeDeclaration();

    private static Dictionary<string, string> Granted(params (string Component, string Reason)[] grants) =>
        grants.ToDictionary(static grant => grant.Component, static grant => grant.Reason, StringComparer.Ordinal);
}
