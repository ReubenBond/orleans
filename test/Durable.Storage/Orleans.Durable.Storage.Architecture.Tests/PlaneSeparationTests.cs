using Orleans.Durable.Storage.Engine.ObjectStore;
using static Orleans.Durable.Storage.Architecture.Tests.Namespaces;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// Pins spec README §2.5 for the layers which separate their planes by namespace: no type in a data-plane namespace
/// references a type in a <c>.Control</c> namespace or calls a write of the control plane's registers; no type in a
/// data-plane or <c>.Control</c> namespace references a type in a <c>.Hosting</c> namespace; within those layers, each
/// register's write is called only by its one writer; and each type the planes table (§2.1) names is declared in its
/// cell's namespace. The checks read the compiled metadata <see cref="MetadataReferences"/> scans: declarations,
/// signatures, custom attributes and their arguments, and method bodies' locals, catch types, and IL operands. They do
/// not see a reference made only at run time, such as a type found by name through reflection.
/// </summary>
public sealed class PlaneSeparationTests
{
    private const string FixtureLayer = "Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures";

    /// <summary>The layers which separate their planes by namespace (spec README §2.1).</summary>
    private static readonly Planes ProductionPlanes =
        new(
            [
                "Orleans.Durable.Storage.Engine.Ledgers",
                "Orleans.Durable.Storage.Engine.Archive",
                "Orleans.Durable.Storage.Engine.Archive.Lsm",
                "Orleans.Durable.Storage.Engine.Archive.Logs",
                "Orleans.Durable.Storage.Engine.ObjectStore",
                "Orleans.Durable.Storage.Engine.Journals",
                "Orleans.Durable.Storage.Engine.Cluster",
                "Orleans.Durable.Storage",
            ],
            [.. Repository.ProductionAssemblies().Select(static assembly => assembly.Project).Where(static project => project != Components.Composition)]);

    private static readonly Planes FixturePlanes = new([FixtureLayer], []);

    /// <summary>
    /// The writes of the control plane's registers, which the request path never makes (spec README §2.1), each with
    /// its one writer within the plane-separated layers (§2.5): the conditional write of the ledger map's and the node
    /// registry's registers, the manifest's replace, the sweepers' deletes, and the object-ledger sweeper's tombstones in
    /// the object store, a node's replica drop,
    /// which only its reconciliation makes (ledgers.md §8.5), and a ledger's drop from the map, which outside Ledgers only
    /// the archive store's drop submitter builds, from its evidence of coverage (LSM-I3, HI-I2). Creating and reading
    /// objects stay on the request path: readers read archived objects.
    /// </summary>
    private static readonly RegisterWrite[] ProductionWrites =
    [
        new(
            new("Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister", "WriteAsync"),
            "Orleans.Durable.Storage.Engine.Ledgers.Control.LedgerMapCommitter",
            "Orleans.Durable.Storage.Control.NodeRegistryCommitter"),
        new(new("Orleans.Durable.Storage.Engine.ObjectStore.IObjectStore", "ReplaceAsync"), "Orleans.Durable.Storage.Engine.Archive.Lsm.Control.ManifestCommitter"),
        new(new("Orleans.Durable.Storage.Engine.ObjectStore.IObjectStore", "DeleteAsync"), "Orleans.Durable.Storage.Engine.Archive.Lsm.Control.Sweeper", "Orleans.Durable.Storage.Engine.Archive.Logs.Control.ArchiveLogSweeper", "Orleans.Durable.Storage.Engine.Ledgers.Control.ObjectLedgerSweeper"),
        new(new("Orleans.Durable.Storage.Engine.ObjectStore.IAppendableObjectStore", "ReplaceWithTombstoneAsync"), "Orleans.Durable.Storage.Engine.Ledgers.Control.ObjectLedgerSweeper"),
        new(new("Orleans.Durable.Storage.Engine.Archive.Logs.IArchiveLogStore", "AppendAsync"), "Orleans.Durable.Storage.Engine.Archive.Logs.Control.ArchiveLogWriter"),
        new(new("Orleans.Durable.Storage.Engine.Archive.Logs.IArchiveLogStore", "OpenAsync"), "Orleans.Durable.Storage.Engine.Archive.Logs.Control.ArchiveLogWriter"),
        new(new("Orleans.Durable.Storage.Engine.Archive.Logs.IArchiveLogStore", "DeleteGenerationAsync"), "Orleans.Durable.Storage.Engine.Archive.Logs.Control.ArchiveLogSweeper"),
        new(new("Orleans.Durable.Storage.Engine.Ledgers.ILedgerStore", "DropReplicaAsync"), "Orleans.Durable.Storage.Engine.Ledgers.Control.ReplicaReconciler"),
        // The record's compiler-generated clone constructs it too, as its outermost type, LedgerMapChange. On archive logs,
        // the drain worker builds a ledger's drop from its own Complete (archive-logs.md §2.4), and the composition's wire
        // converter rebuilds that drop where its grain call delivers it to the controller grain (orleans.md §7.2).
        new(
            new("Orleans.Durable.Storage.Engine.Ledgers.Control.LedgerMapChange+Drop", ".ctor"),
            "Orleans.Durable.Storage.Engine.Archive.Lsm.Control.LedgerDropper",
            "Orleans.Durable.Storage.Engine.Archive.Logs.Control.ArchiveLogDrainWorker",
            "Orleans.Durable.Storage.Engine.Ledgers.Control.LedgerMapChange",
            "Orleans.Durable.Storage.Control.LedgerMapChangeConverter"),
    ];

    /// <summary>
    /// The store views: a type which implements a store's writes by forwarding each call it is given to the store beneath it,
    /// unchanged but for its names, as <c>PrefixedObjectStore</c> places a store under a cluster's prefix (orleans.md §5.13),
    /// or by the writes of the store beneath it alone, as <c>ChunkArchiveLogStore</c> implements the sweeper's
    /// <c>DeleteGenerationAsync</c> by deleting a generation's objects (archive-logs.md §7.4). Its forwarding is no write of
    /// its own: its callers call a member which implements the write, so the checks match them as callers of the write,
    /// and its own call of the store beneath it is left out.
    /// </summary>
    private static readonly HashSet<string> StoreViews =
    [
        "Orleans.Durable.Storage.Engine.ObjectStore.PrefixedObjectStore",
        "Orleans.Durable.Storage.Engine.ObjectStore.PrefixedAppendableObjectStore",
        "Orleans.Durable.Storage.Engine.ObjectStore.PacedObjectStore",
        "Orleans.Durable.Storage.Engine.ObjectStore.PacedAppendableObjectStore",
        "Orleans.Durable.Storage.Engine.Archive.Logs.ChunkArchiveLogStore",
    ];

    /// <summary>The fixture layer's writes: no fixture is the register's writer, so each call of its write is reported.</summary>
    private static readonly RegisterWrite[] FixtureWrites =
    [
        new(new(FixtureLayer + ".IFixtureRegister`1", "WriteAsync")),
        new(new(FixtureLayer + ".IFixtureObjectStore", "ReplaceAsync"), FixtureLayer + ".Control.FixtureManifestCommitter"),
        new(new(FixtureLayer + ".IFixtureObjectStore", "DeleteAsync"), FixtureLayer + ".Control.FixtureSweeper"),
    ];

    /// <summary>
    /// The types in a plane-separated layer's namespaces of every assembly under src, so the namespaces decide which
    /// types are checked, whichever project holds them.
    /// </summary>
    private static readonly Lazy<ScannedType[]> ProductionTypes = new(static () =>
        [.. Repository.ProductionAssemblies().SelectMany(static assembly => MetadataReferences.Scan(assembly.Path, ProductionPlanes.IsInLayer))]);

    private static readonly Lazy<ScannedType[]> FixtureTypes = new(static () =>
        [.. MetadataReferences.Scan(typeof(PlaneSeparationTests).Assembly.Location, FixturePlanes.IsInLayer)]);

    /// <summary>The members whose call is a production write: each write's own, and each implementation of it under src.</summary>
    private static readonly Lazy<ILookup<ReferencedMember, RegisterWrite>> ProductionWriteCalls = new(static () =>
        CallableWrites(ProductionWrites, Repository.ProductionAssemblies().Select(static assembly => assembly.Path)));

    /// <summary>The members whose call is a fixture write: each write's own, and each implementation of it in this assembly.</summary>
    private static readonly Lazy<ILookup<ReferencedMember, RegisterWrite>> FixtureWriteCalls = new(static () =>
        CallableWrites(FixtureWrites, [typeof(PlaneSeparationTests).Assembly.Location]));

    public static TheoryData<string> SeparatedLayers => [.. ProductionPlanes.Layers];

    public static TheoryData<string, string> ProductionWriteDeclarations =>
        [.. ProductionWrites.Select(static write => (write.Member.DeclaringType, write.Member.Name))];

    public static TheoryData<string, string> FixtureWriteDeclarations =>
        [.. FixtureWrites.Select(static write => (write.Member.DeclaringType, write.Member.Name))];

    public static TheoryData<string, string, string, string[]> PlanesTableCells =>
        [.. PlanesTable.Read().Select(static cell => (cell.Layer, cell.Plane, cell.Namespace, cell.TypeNames.ToArray()))];

    [Theory]
    [MemberData(nameof(SeparatedLayers))]
    public void DataPlaneTypesReferenceNoControlPlaneType(string layer)
    {
        var dataPlane = LayerTypes(layer, ProductionPlanes.IsDataPlane);

        Assert.SkipWhen(dataPlane.Length == 0, $"{layer} has no data-plane type in code yet.");
        Assert.Empty(TypeViolations(dataPlane, ProductionPlanes.IsControlPlane));
    }

    /// <summary>Wiring in a layer's <c>.Hosting</c> namespace names both planes, and neither plane names it.</summary>
    [Theory]
    [MemberData(nameof(SeparatedLayers))]
    public void PlaneTypesReferenceNoHostingType(string layer)
    {
        var planeTypes = LayerTypes(layer, ProductionPlanes.IsInAPlane);

        Assert.SkipWhen(planeTypes.Length == 0, $"{layer} has no data-plane or control-plane type in code yet.");
        Assert.Empty(TypeViolations(planeTypes, ProductionPlanes.IsHosting));
    }

    /// <summary>
    /// The request path never writes the control plane's registers. A call is matched by the member it names: a write's
    /// interface member, through the interface or an instantiation of a generic one, or a member which implements it,
    /// through a concrete type (<see cref="CallableWrites"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(SeparatedLayers))]
    public void DataPlaneTypesCallNoControlPlaneWrite(string layer)
    {
        var dataPlane = LayerTypes(layer, ProductionPlanes.IsDataPlane);

        Assert.SkipWhen(dataPlane.Length == 0, $"{layer} has no data-plane type in code yet.");
        Assert.Empty(dataPlane.Where(static type => !StoreViews.Contains(OutermostType(type))).SelectMany(static type => type.Members
            .Where(static member => ProductionWriteCalls.Value.Contains(member))
            .Select(member => $"{type.FullName} -> {member}")));
    }

    /// <summary>
    /// Each register has one writer (spec README §2.5): within the layer, in every plane and its <c>.Hosting</c>
    /// namespace, each call of a register's write, matched as <see cref="DataPlaneTypesCallNoControlPlaneWrite"/>
    /// matches it, is made by a type <see cref="ProductionWrites"/> lists as that write's writer. A nested type, such
    /// as a compiler-generated closure or state machine, calls as its outermost type.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeparatedLayers))]
    public void OnlyEachRegistersCommitterCallsItsWrite(string layer)
    {
        var layerTypes = LayerTypes(layer, static _ => true);

        Assert.SkipWhen(layerTypes.Length == 0, $"{layer} has no type in code yet.");
        Assert.Empty(UnauthorizedWrites(layerTypes.Where(static type => !StoreViews.Contains(OutermostType(type))), ProductionWriteCalls.Value).Select(static call => $"{call.Caller.FullName} -> {call.Callee}"));
    }

    /// <summary>
    /// Each forbidden write names a member of its declaring type, so a rename, a typo, or a wrong arity cannot retire the
    /// write checks. It is skipped only while no project declares the type's namespace
    /// (<see cref="Repository.AssembliesDeclaring"/>: the assemblies which compile its components);
    /// once one does, exactly one of their assemblies must declare the type, with the member.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProductionWriteDeclarations))]
    public void EachForbiddenWriteIsAMemberOfItsDeclaringType(string declaringType, string member)
    {
        var typeNamespace = NamespaceAndName(declaringType).Namespace;
        var assemblies = Repository.AssembliesDeclaring(typeNamespace);

        Assert.SkipWhen(assemblies.Count == 0, $"No project declares {typeNamespace} yet.");
        var declarations = assemblies
            .Select(static assembly => MetadataReferences.Declarations(assembly.Path))
            .Where(declared => declared.ContainsKey(declaringType))
            .ToArray();
        Assert.True(
            declarations.Length == 1,
            $"{declaringType} is declared by {declarations.Length} of {string.Join(", ", assemblies.Select(static assembly => assembly.Project))}, not one.");
        Assert.Contains(member, declarations[0][declaringType]);
    }

    /// <summary>
    /// Each fixture write names a member of its declaring type in this assembly, as each forbidden write does in its
    /// layer's, so the self-tests below match calls of members which exist.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureWriteDeclarations))]
    public void EachFixtureWriteIsAMemberOfItsDeclaringType(string declaringType, string member) =>
        Assert.Contains(member, Assert.Contains(declaringType, MetadataReferences.Declarations(typeof(PlaneSeparationTests).Assembly.Location)));

    /// <summary>
    /// Each namespace the table of types by plane (spec README §2.1) names is in the plane of its column, and §5 has a
    /// row for the cell's layer. Among the top-level types of the layer's namespaces which the assemblies of its components
    /// declare, none with a simple name the cell names, ignoring a generic arity, is in another namespace. Once §5 no
    /// longer lists the layer as not yet in code, each type the cell names is declared in its namespace; while §5 does, a
    /// type not yet declared skips the cell.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlanesTableCells))]
    public void EachTypeThePlanesTableNamesIsDeclaredInItsCellsNamespace(
        string layer,
        string plane,
        string typeNamespace,
        string[] typeNames)
    {
        Func<string, bool> isInPlane = string.Equals(plane, "Data plane", StringComparison.Ordinal)
            ? ProductionPlanes.IsDataPlane
            : ProductionPlanes.IsControlPlane;
        Assert.True(isInPlane(typeNamespace), $"The {layer} row's {plane} cell names {typeNamespace}, which is not in that plane.");
        var notYetInCode = ImplementationStatus.IsNotYetInCode(layer);
        var layerNamespace = ProductionPlanes.Layers.Single(separated => ProductionPlanes.IsInLayer(typeNamespace, separated) && !ProductionPlanes.Layers.Any(inner => inner.Length > separated.Length && ProductionPlanes.IsInLayer(typeNamespace, inner)));
        var (misplaced, undeclared) = PlanesCellDeclarations(
            Repository.AssembliesDeclaring(layerNamespace).SelectMany(static assembly => MetadataReferences.Declarations(assembly.Path).Keys)
                .Where(fullName => ProductionPlanes.IsInLayer(NamespaceAndName(fullName.Split('+')[0]).Namespace, layerNamespace)),
            typeNamespace,
            typeNames);

        Assert.True(
            misplaced.Length == 0,
            $"The {layer} row's {plane} cell names types of {typeNamespace}, but {layerNamespace} declares them in another namespace: {string.Join(", ", misplaced)}.");
        Assert.SkipWhen(
            undeclared.Length > 0 && notYetInCode,
            $"Spec README §5 lists {layer} as not yet in code, and {typeNamespace} does not yet declare {string.Join(", ", undeclared)}.");
        Assert.True(
            undeclared.Length == 0,
            $"Spec README §5 lists {layer} as in code, but {typeNamespace} does not declare {string.Join(", ", undeclared)}.");
    }

    /// <summary>
    /// A layer's <c>.Control</c> namespace is its control plane, its <c>.Hosting</c> namespace is in neither plane,
    /// and the rest of the layer is its data plane; a namespace whose name only starts with a layer's is in neither.
    /// </summary>
    [Theory]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers", true, false, false)]
    [InlineData("Orleans.Durable.Storage.Engine.Archive.Lsm.Caches", true, false, false)]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers.Control", false, true, false)]
    [InlineData("Orleans.Durable.Storage.Engine.Cluster.Control", false, true, false)]
    [InlineData("Orleans.Durable.Storage.Control.Grains", false, true, false)]
    [InlineData("Orleans.Durable.Storage.Hosting", false, false, true)]
    [InlineData("Orleans.Durable.Storage.Engine.Archive.Lsm.Hosting.Registration", false, false, true)]
    [InlineData("Orleans.Durable.Storage.Engine.ArchiveExtensions", false, false, false)]
    [InlineData("Orleans.Durable.Storage.FileSystem.Archive", false, false, false)]
    [InlineData("Orleans.Durable.Storage.Engine.MetaStore", false, false, false)]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers.Controller", true, false, false)]
    [InlineData("Orleans.Durable.Storage.Engine.Ledgers.HostingPolicies", true, false, false)]
    public void ANamespaceIsInTheDataPlaneTheControlPlaneOrNeither(
        string typeNamespace,
        bool isDataPlane,
        bool isControlPlane,
        bool isHosting)
    {
        Assert.Equal(isDataPlane, ProductionPlanes.IsDataPlane(typeNamespace));
        Assert.Equal(isControlPlane, ProductionPlanes.IsControlPlane(typeNamespace));
        Assert.Equal(isHosting, ProductionPlanes.IsHosting(typeNamespace));
    }

    /// <summary>
    /// Each data-plane fixture listed reaches the control plane at exactly the sites listed, within one assembly, so
    /// each path of the scanner is checked by a site of its own; the fixtures which reach only the data plane, and the
    /// control plane's own references, are not reported.
    /// </summary>
    [Fact]
    public void TheCheckFindsEachKindOfReferenceWithinAnAssembly()
    {
        (string Fixture, ReferenceSite Site)[] expected =
        [
            ("BoxesControlEnumInAttribute", ReferenceSite.AttributePositionalArgument),
            ("CallsControl", ReferenceSite.InstructionOperand),
            ("CallsControlAfterSwitch", ReferenceSite.InstructionOperand),
            ("CallsControlInLambda", ReferenceSite.InstructionOperand),
            ("CallsGenericControlMethod", ReferenceSite.InstructionOperand),
            ("CallsWiringWithControlSignature", ReferenceSite.InstructionOperand),
            ("CatchesControl", ReferenceSite.CatchType),
            ("ChecksForControlType", ReferenceSite.InstructionOperand),
            ("ConstrainedToControl", ReferenceSite.GenericConstraint),
            ("ConstrainedTypeOfControl`1", ReferenceSite.GenericConstraint),
            ("ControlField", ReferenceSite.FieldSignature),
            ("DerivesFromControl", ReferenceSite.BaseType),
            ("DerivesFromControl", ReferenceSite.InstructionOperand),
            ("EventMarkedByControl", ReferenceSite.AttributeConstructor),
            ("EventOfControl", ReferenceSite.EventType),
            ("EventOfControl", ReferenceSite.MethodSignature),
            ("FieldMarkedByControl", ReferenceSite.AttributeConstructor),
            ("GenericArgumentOfControl", ReferenceSite.MethodSignature),
            ("GenericParameterMarkedByControl", ReferenceSite.AttributeConstructor),
            ("HoldsControlInLocal", ReferenceSite.Local),
            ("ImplementsControl", ReferenceSite.Interface),
            ("InstantiatesWithControl", ReferenceSite.InstructionOperand),
            ("MarkedByControl", ReferenceSite.AttributeConstructor),
            ("MethodMarkedByControl", ReferenceSite.AttributeConstructor),
            ("NamesControlArrayInAttribute", ReferenceSite.AttributePositionalArgument),
            ("NamesControlGenericArgumentInAttribute", ReferenceSite.AttributePositionalArgument),
            ("NamesControlGenericInAttribute", ReferenceSite.AttributePositionalArgument),
            ("NamesControlInArrayArgument", ReferenceSite.AttributeArrayElement),
            ("NamesControlInAttribute", ReferenceSite.AttributePositionalArgument),
            ("NamesControlInNamedArgument", ReferenceSite.AttributeNamedArgument),
            ("Outer", ReferenceSite.MethodSignature),
            ("ParameterMarkedByControl", ReferenceSite.AttributeConstructor),
            ("PinsControl", ReferenceSite.FieldSignature),
            ("PinsControl", ReferenceSite.InstructionOperand),
            ("PinsControl", ReferenceSite.Local),
            ("PropertyMarkedByControl", ReferenceSite.AttributeConstructor),
            ("PropertyOfControl", ReferenceSite.MethodSignature),
            ("PropertyOfControl", ReferenceSite.PropertySignature),
            ("ReadsControlField", ReferenceSite.InstructionOperand),
            ("ReadsWiringControlField", ReferenceSite.InstructionOperand),
            ("ReturnsControl", ReferenceSite.MethodSignature),
            ("ReturnValueMarkedByControl", ReferenceSite.AttributeConstructor),
            ("TakesControlArray", ReferenceSite.MethodSignature),
            ("TakesControlByReference", ReferenceSite.MethodSignature),
            ("TakesControlFunctionPointer", ReferenceSite.MethodSignature),
            ("TakesControlMatrix", ReferenceSite.MethodSignature),
            ("TakesControlParameter", ReferenceSite.MethodSignature),
            ("TakesControlPointer", ReferenceSite.MethodSignature),
            ("UsesControlService", ReferenceSite.FieldSignature),
            ("UsesControlService", ReferenceSite.InstructionOperand),
            ("UsesControlService", ReferenceSite.MethodSignature),
            ("VolatileControlField", ReferenceSite.FieldSignature),
        ];

        Assert.Equal(Sorted(expected), ReferencingFixtures(FixturePlanes.IsDataPlane, FixturePlanes.IsControlPlane));
    }

    /// <summary>
    /// References to types of another assembly arrive as type and member references rather than definitions, and a
    /// nested type's reference is scoped by its outer type's.
    /// </summary>
    [Fact]
    public void TheCheckFindsReferencesToAnotherAssembly() =>
        Assert.Equal(
            Sorted(
            [
                ("ChecksForExternalType", ReferenceSite.InstructionOperand),
                ("ExternalInBody", ReferenceSite.InstructionOperand),
                ("ExternalInSignature", ReferenceSite.MethodSignature),
                ("ExternalNestedInSignature", ReferenceSite.InstructionOperand),
                ("ExternalNestedInSignature", ReferenceSite.MethodSignature),
            ]),
            ReferencingFixtures(FixturePlanes.IsDataPlane, static typeNamespace => IsWithin(typeNamespace, "System.Collections.Concurrent")));

    /// <summary>
    /// Each fixture of either plane listed reaches the hosting namespace at exactly the sites listed; the hosting
    /// namespace's own references to both planes, and the plane fixtures which do not reach it, are not reported.
    /// </summary>
    [Fact]
    public void TheCheckFindsEachPlaneTypesReferenceToAHostingType()
    {
        (string Fixture, ReferenceSite Site)[] expected =
        [
            ("CallsWiringWithControlSignature", ReferenceSite.InstructionOperand),
            ("CallsWiringWithControlSignature", ReferenceSite.MethodSignature),
            ("Control.ControlUsesHosting", ReferenceSite.InstructionOperand),
            ("ReadsWiringControlField", ReferenceSite.InstructionOperand),
            ("ReadsWiringControlField", ReferenceSite.MethodSignature),
            ("UsesHosting", ReferenceSite.InstructionOperand),
        ];

        Assert.Equal(Sorted(expected), ReferencingFixtures(FixturePlanes.IsInAPlane, FixturePlanes.IsHosting));
    }

    /// <summary>
    /// A data-plane fixture which calls a forbidden write is found, whether the call names a member of this assembly's
    /// interface, of an instantiation of its generic interface, or of a concrete type which implements the interface;
    /// one which only reads, or creates, is not.
    /// </summary>
    [Fact]
    public void TheCheckFindsEachCallOfAForbiddenWrite() =>
        Assert.Equal(
            [
                ("CallsConcreteStore", new ReferencedMember(FixtureLayer + ".ConcreteFixtureObjectStore", "ReplaceAsync")),
                ("DeletesObject", new ReferencedMember(FixtureLayer + ".IFixtureObjectStore", "DeleteAsync")),
                ("ReplacesObject", new ReferencedMember(FixtureLayer + ".IFixtureObjectStore", "ReplaceAsync")),
                ("WritesRegister", new ReferencedMember(FixtureLayer + ".IFixtureRegister`1", "WriteAsync")),
            ],
            CallingFixtures(FixtureWriteCalls.Value));

    /// <summary>
    /// The implementations of a forbidden write are the methods named as it of a type which implements its interface,
    /// and an explicit implementation of an instantiation of its generic interface; the members which implement a read
    /// or a create are not.
    /// </summary>
    [Fact]
    public void TheCheckFindsEachImplementationOfAForbiddenWrite() =>
        Assert.Equal(
            [
                new MemberImplementation(
                    new(FixtureLayer + ".ConcreteFixtureObjectStore", "DeleteAsync"),
                    new(FixtureLayer + ".IFixtureObjectStore", "DeleteAsync")),
                new MemberImplementation(
                    new(FixtureLayer + ".ConcreteFixtureObjectStore", "ReplaceAsync"),
                    new(FixtureLayer + ".IFixtureObjectStore", "ReplaceAsync")),
                new MemberImplementation(
                    new(FixtureLayer + ".ExplicitFixtureRegister", FixtureLayer + ".IFixtureRegister<System.Int32>.WriteAsync"),
                    new(FixtureLayer + ".IFixtureRegister`1", "WriteAsync")),
            ],
            MetadataReferences
                .Implementations(typeof(PlaneSeparationTests).Assembly.Location, FixtureWrites.Select(static write => write.Member).ToHashSet())
                .OrderBy(static implementation => implementation.Implementation.ToString(), StringComparer.Ordinal));

    /// <summary>
    /// The production list finds a call of the MetaStore register's write, a member reference to another assembly's
    /// interface, and a call through its memory implementation, another assembly's type; not a call of its read.
    /// </summary>
    [Fact]
    public void TheProductionWritesMatchACallOfTheMetaStoreRegistersWrite() =>
        Assert.Equal(
            [
                ("WritesInMemoryRegister", new ReferencedMember("Orleans.Durable.Storage.Engine.MetaStore.InMemoryConditionalRegister", "WriteAsync")),
                ("WritesMetaStoreRegister", new ReferencedMember("Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister", "WriteAsync")),
            ],
            CallingFixtures(ProductionWriteCalls.Value));

    /// <summary>
    /// Every fixture listed, of either plane, calls a write whose writer it is not, with the member listed: the
    /// data-plane callers, the control-plane worker which bypasses the manifest's committer, and the sweeper's replace,
    /// since one register's writer may call no other's write. The manifest's committer, whose call is in its async
    /// state machine, and the sweeper's delete, in a closure and through the concrete store, are not reported.
    /// </summary>
    [Fact]
    public void TheCheckFindsAWriteOutsideItsRegistersCommitter() =>
        Assert.Equal(
            [
                ("CallsConcreteStore", new ReferencedMember(FixtureLayer + ".ConcreteFixtureObjectStore", "ReplaceAsync")),
                ("Control.BypassingWorker", new ReferencedMember(FixtureLayer + ".IFixtureObjectStore", "ReplaceAsync")),
                ("Control.FixtureSweeper", new ReferencedMember(FixtureLayer + ".ConcreteFixtureObjectStore", "ReplaceAsync")),
                ("DeletesObject", new ReferencedMember(FixtureLayer + ".IFixtureObjectStore", "DeleteAsync")),
                ("ReplacesObject", new ReferencedMember(FixtureLayer + ".IFixtureObjectStore", "ReplaceAsync")),
                ("WritesRegister", new ReferencedMember(FixtureLayer + ".IFixtureRegister`1", "WriteAsync")),
            ],
            SortedCalls(UnauthorizedWrites(FixtureTypes.Value, FixtureWriteCalls.Value)));

    /// <summary>
    /// The planes-table check finds each top-level type with a name a cell names in another namespace, a generic one
    /// whatever its arity, and each name the cell's namespace does not declare as a top-level type; a generic type of
    /// the cell's namespace declares its name, and a nested type of the same name does not.
    /// </summary>
    [Fact]
    public void TheCheckFindsATypeThePlanesTableNamesOutsideItsCellsNamespace()
    {
        var (misplaced, undeclared) = PlanesCellDeclarations(
            MetadataReferences.Declarations(typeof(PlaneSeparationTests).Assembly.Location).Keys,
            FixtureLayer,
            ["DataPlaneOnly", "IFixtureRegister", "ControlService", "FixtureWiring", "Nested", "Absent"]);

        Assert.Equal(
            [FixtureLayer + ".Control.ControlService", FixtureLayer + ".Hosting.FixtureWiring", FixtureLayer + ".Hosting.FixtureWiring`1"],
            misplaced.Order(StringComparer.Ordinal));
        Assert.Equal(["ControlService", "FixtureWiring", "Nested", "Absent"], undeclared);
    }

    private static ScannedType[] LayerTypes(string layer, Func<string, bool> isInPlane) =>
        [.. ProductionTypes.Value.Where(type => ProductionPlanes.IsInLayer(type.Namespace, layer) && isInPlane(type.Namespace))];

    private static IEnumerable<string> TypeViolations(IEnumerable<ScannedType> types, Func<string, bool> isForbidden) =>
        types.SelectMany(type => type.Types
            .Where(reference => isForbidden(reference.Namespace))
            .Select(reference => $"{type.FullName} -> {reference.FullName} ({reference.Site})"));

    /// <summary>
    /// The members whose call is one of <paramref name="writes"/>: each write's interface member, and each member which
    /// implements one (<see cref="MetadataReferences.Implementations"/>) of a type the assemblies at
    /// <paramref name="assemblyPaths"/> declare, which a call through that type names.
    /// </summary>
    private static ILookup<ReferencedMember, RegisterWrite> CallableWrites(RegisterWrite[] writes, IEnumerable<string> assemblyPaths)
    {
        var byMember = writes.ToDictionary(static write => write.Member);
        var members = byMember.Keys.ToHashSet();
        return writes
            .Select(static write => (Callee: write.Member, Write: write))
            .Concat(assemblyPaths
                .SelectMany(path => MetadataReferences.Implementations(path, members))
                .Select(implementation => (Callee: implementation.Implementation, Write: byMember[implementation.Implemented])))
            .ToLookup(static call => call.Callee, static call => call.Write);
    }

    /// <summary>
    /// Each call by <paramref name="types"/> of a member in <paramref name="writes"/> whose caller, as its outermost type
    /// (<see cref="OutermostType"/>), is not a writer of every write the member is.
    /// </summary>
    private static IEnumerable<(ScannedType Caller, ReferencedMember Callee)> UnauthorizedWrites(
        IEnumerable<ScannedType> types,
        ILookup<ReferencedMember, RegisterWrite> writes) =>
        types.SelectMany(type => type.Members
            .Where(member => writes[member].Any(write => !write.Writers.Contains(OutermostType(type), StringComparer.Ordinal)))
            .Select(member => (type, member)));

    /// <summary>The fixtures of a source namespace which reference a type of a target namespace, each with the site of the reference.</summary>
    private static (string Fixture, ReferenceSite Site)[] ReferencingFixtures(Func<string, bool> isSource, Func<string, bool> isTarget) =>
        Sorted(FixtureTypes.Value
            .Where(type => isSource(type.Namespace))
            .SelectMany(type => type.Types
                .Where(reference => isTarget(reference.Namespace))
                .Select(reference => (FixtureName(type), reference.Site))));

    /// <summary>The data-plane fixtures which call a member in <paramref name="writes"/>, each with the member.</summary>
    private static (string Fixture, ReferencedMember Member)[] CallingFixtures(ILookup<ReferencedMember, RegisterWrite> writes) =>
        SortedCalls(FixtureTypes.Value
            .Where(static type => FixturePlanes.IsDataPlane(type.Namespace))
            .SelectMany(type => type.Members.Where(writes.Contains).Select(member => (type, member))));

    private static (string Fixture, ReferenceSite Site)[] Sorted(IEnumerable<(string Fixture, ReferenceSite Site)> references) =>
        [
            .. references
                .Distinct()
                .OrderBy(static reference => reference.Fixture, StringComparer.Ordinal)
                .ThenBy(static reference => reference.Site),
        ];

    private static (string Fixture, ReferencedMember Member)[] SortedCalls(IEnumerable<(ScannedType Caller, ReferencedMember Callee)> calls) =>
        [
            .. calls
                .Select(static call => (Fixture: FixtureName(call.Caller), Member: call.Callee))
                .Distinct()
                .OrderBy(static call => call.Fixture, StringComparer.Ordinal)
                .ThenBy(static call => call.Member.ToString(), StringComparer.Ordinal),
        ];

    /// <summary>A fixture's name within the fixture layer; a nested type, such as a closure, is its outermost type's.</summary>
    private static string FixtureName(ScannedType type) => OutermostType(type)[(FixtureLayer.Length + 1)..];

    /// <summary>The full name of a type's outermost type: a nested type, such as a compiler-generated closure or state machine, acts as it.</summary>
    private static string OutermostType(ScannedType type) => type.FullName.Split('+')[0];

    /// <summary>A top-level type's namespace and its simple name, without a generic arity suffix.</summary>
    private static (string Namespace, string Name) NamespaceAndName(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        var name = fullName[(dot + 1)..];
        var arity = name.IndexOf('`', StringComparison.Ordinal);
        return (dot < 0 ? string.Empty : fullName[..dot], arity < 0 ? name : name[..arity]);
    }

    /// <summary>
    /// Checks a planes-table cell against <paramref name="declaredTypes"/>, the full names of the types some projects
    /// declare: the top-level types with a simple name in <paramref name="typeNames"/>, ignoring a generic arity, which
    /// are not in <paramref name="typeNamespace"/>; and the names in <paramref name="typeNames"/> which no top-level type
    /// of <paramref name="typeNamespace"/> has.
    /// </summary>
    private static (string[] Misplaced, string[] Undeclared) PlanesCellDeclarations(
        IEnumerable<string> declaredTypes,
        string typeNamespace,
        string[] typeNames)
    {
        var named = declaredTypes
            .Where(static fullName => !fullName.Contains('+', StringComparison.Ordinal))
            .Select(static fullName => (FullName: fullName, Type: NamespaceAndName(fullName)))
            .Where(declared => typeNames.Contains(declared.Type.Name, StringComparer.Ordinal))
            .ToLookup(declared => string.Equals(declared.Type.Namespace, typeNamespace, StringComparison.Ordinal));
        return (
            [.. named[false].Select(static declared => declared.FullName)],
            [.. typeNames.Except(named[true].Select(static declared => declared.Type.Name), StringComparer.Ordinal)]);
    }

    /// <summary>A write of a control register, and the full names of the types which alone may call it.</summary>
    private sealed record RegisterWrite(ReferencedMember Member, params string[] Writers);

    /// <summary>
    /// The planes of layers which separate them by namespace (spec README §2.1): a layer's <c>.Control</c> namespace
    /// is its control plane; its <c>.Hosting</c> namespace, whose registration and lifecycle code names both planes,
    /// is in neither; and the rest of the layer is its data plane. A namespace lies in a layer when it lies within the
    /// layer's namespace and within no other assembly's root namespace inside it (<paramref name="assemblies"/>): the
    /// composition's layer, <c>Orleans.Durable.Storage</c>, holds neither the Engine's namespaces nor a provider's.
    /// </summary>
    private sealed class Planes(IReadOnlyList<string> layers, IReadOnlyList<string> assemblies)
    {
        public IReadOnlyList<string> Layers => layers;

        public bool IsInLayer(string typeNamespace, string layer) =>
            IsWithin(typeNamespace, layer)
            && !assemblies.Any(root => root.Length > layer.Length && IsWithin(root, layer) && IsWithin(typeNamespace, root));

        public bool IsInLayer(string typeNamespace) => layers.Any(layer => IsInLayer(typeNamespace, layer));

        public bool IsDataPlane(string typeNamespace) =>
            IsInLayer(typeNamespace) && !IsControlPlane(typeNamespace) && !IsHosting(typeNamespace);

        public bool IsControlPlane(string typeNamespace) =>
            layers.Any(layer => IsInLayer(typeNamespace, layer) && IsWithin(typeNamespace, layer + ".Control"));

        public bool IsHosting(string typeNamespace) =>
            layers.Any(layer => IsInLayer(typeNamespace, layer) && IsWithin(typeNamespace, layer + ".Hosting"));

        public bool IsInAPlane(string typeNamespace) => IsDataPlane(typeNamespace) || IsControlPlane(typeNamespace);
    }
}
