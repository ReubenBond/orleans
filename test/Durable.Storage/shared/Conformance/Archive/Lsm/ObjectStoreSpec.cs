using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Testing;

#pragma warning disable CA1852 // Accordant generates a virtual clone factory for state types.

/// <summary>
/// The abstract state of an <see cref="Orleans.Durable.Storage.Engine.ObjectStore.IObjectStore"/>, with its
/// <see cref="Orleans.Durable.Storage.Engine.ObjectStore.IAppendableObjectStore"/>, over <see cref="ObjectStoreSpec.Names"/>:
/// each name's object (archive §3, object-ledgers.md §3.1).
/// </summary>
[State]
internal partial class ObjectStoreState
{
    /// <summary>
    /// Gets or sets the objects, one per name of <see cref="ObjectStoreSpec.Names"/>.
    /// </summary>
    public List<ObjectState> Objects { get; set; } = [new(), new(), new(), new(), new(), new()];
}

/// <summary>
/// One name's object: whether it exists, its value, and the values every write of the name has carried; or, for an
/// appendable object, whether it is open and the values of its appends.
/// </summary>
/// <remarks>
/// A value names the content <see cref="ObjectStoreSpec.Payload"/> stores, and the caller writes each value to a name at
/// most once, so a value names one version of the object under every provider, whether a version is a per-write token
/// or a digest of the content.
/// </remarks>
[State]
internal partial class ObjectState
{
    /// <summary>
    /// Gets or sets a value indicating whether the object exists.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets the object's value, when it exists.
    /// </summary>
    public byte Value { get; set; }

    /// <summary>
    /// Gets or sets the values the name's creates and replaces carried, whatever their outcomes.
    /// </summary>
    public List<byte> Written { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the object is appendable: created by a create of an appendable object, and
    /// not replaced by its tombstone since.
    /// </summary>
    public bool Appendable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an appendable object is open.
    /// </summary>
    public bool Open { get; set; }

    /// <summary>
    /// Gets or sets the values of an appendable object's appends, in order, each <see cref="ObjectStoreSpec.Chunk"/> of its
    /// value.
    /// </summary>
    public List<byte> Appends { get; set; } = [];
}

#pragma warning restore CA1852

/// <summary>
/// A request of <see cref="ObjectStoreSpec"/>, which names one of <see cref="ObjectStoreSpec.Names"/>.
/// </summary>
internal interface IObjectStoreRequest
{
    /// <summary>
    /// Gets the index of the request's name in <see cref="ObjectStoreSpec.Names"/>, or of its prefix in
    /// <see cref="ObjectStoreSpec.Prefixes"/> for a listing.
    /// </summary>
    int Name { get; }
}

/// <summary>
/// A <c>CreateAsync</c> of <see cref="ObjectStoreSpec.Payload"/> of <paramref name="Value"/>.
/// </summary>
internal sealed record ObjectCreate(int Name, byte Value) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Create({ObjectStoreSpec.Names[Name]}, {Value})");
}

/// <summary>
/// A <c>ReadAsync</c> of bytes <c>[Offset, Offset + Length)</c>.
/// </summary>
internal sealed record ObjectRead(int Name, int Offset, int Length) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Read({ObjectStoreSpec.Names[Name]}, {Offset}+{Length})");
}

/// <summary>
/// A <c>ReadVersionedAsync</c>.
/// </summary>
internal sealed record ObjectReadVersioned(int Name) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"ReadVersioned({ObjectStoreSpec.Names[Name]})");
}

/// <summary>
/// A <c>ReplaceAsync</c> of <see cref="ObjectStoreSpec.Payload"/> of <paramref name="Value"/>, naming the version of
/// <paramref name="Expected"/>'s content, or none when it is <see langword="null"/>.
/// </summary>
internal sealed record ObjectReplace(int Name, byte? Expected, byte Value) : IObjectStoreRequest
{
    public override string ToString() => Expected is { } expected
        ? string.Create(CultureInfo.InvariantCulture, $"Replace({ObjectStoreSpec.Names[Name]}, {expected} → {Value})")
        : string.Create(CultureInfo.InvariantCulture, $"Replace({ObjectStoreSpec.Names[Name]}, none → {Value})");
}

/// <summary>
/// A <c>DeleteAsync</c>.
/// </summary>
internal sealed record ObjectDelete(int Name) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Delete({ObjectStoreSpec.Names[Name]})");
}

/// <summary>
/// A <c>ListAsync</c> of the prefix <see cref="ObjectStoreSpec.Prefixes"/>[<paramref name="Name"/>], after
/// <see cref="ObjectStoreSpec.Starts"/>[<paramref name="Start"/>].
/// </summary>
internal sealed record ObjectList(int Name, int Start = 0) : IObjectStoreRequest
{
    public override string ToString() => ObjectStoreSpec.Starts[Start] is { } start
        ? string.Create(CultureInfo.InvariantCulture, $"List('{ObjectStoreSpec.Prefixes[Name]}' after '{start}')")
        : string.Create(CultureInfo.InvariantCulture, $"List('{ObjectStoreSpec.Prefixes[Name]}')");
}

/// <summary>
/// A <c>GetPropertiesAsync</c>.
/// </summary>
internal sealed record ObjectGetProperties(int Name) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Properties({ObjectStoreSpec.Names[Name]})");
}

/// <summary>
/// A <c>CreateAppendableAsync</c>.
/// </summary>
internal sealed record ObjectCreateAppendable(int Name) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"CreateAppendable({ObjectStoreSpec.Names[Name]})");
}

/// <summary>
/// An <c>AppendAsync</c> of <see cref="ObjectStoreSpec.Chunk"/> of <paramref name="Value"/> at the length of
/// <paramref name="Appends"/> chunks.
/// </summary>
internal sealed record ObjectAppend(int Name, int Appends, byte Value) : IObjectStoreRequest
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Append({ObjectStoreSpec.Names[Name]}, at {Appends * ObjectStoreSpec.ChunkLength}, {Value})");
}

/// <summary>
/// A <c>SealAsync</c>.
/// </summary>
internal sealed record ObjectSeal(int Name) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Seal({ObjectStoreSpec.Names[Name]})");
}

/// <summary>
/// A <c>ReplaceWithTombstoneAsync</c> of <see cref="ObjectStoreSpec.Payload"/> of <see cref="ObjectStoreSpec.TombstoneValue"/>.
/// </summary>
internal sealed record ObjectTombstone(int Name) : IObjectStoreRequest
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Tombstone({ObjectStoreSpec.Names[Name]})");
}

/// <summary>
/// The kinds of <see cref="ObjectOutcome"/>.
/// </summary>
internal enum ObjectOutcomeKind
{
    /// <summary>A create stored the object.</summary>
    Created,

    /// <summary>A create met an existing object.</summary>
    Exists,

    /// <summary>A replace applied.</summary>
    Applied,

    /// <summary>A replace met another version, or an object against its expectation.</summary>
    Mismatch,

    /// <summary>A read found no object.</summary>
    Absent,

    /// <summary>A read returned bytes of <see cref="ObjectOutcome.Value"/>'s content.</summary>
    Present,

    /// <summary>A read's range ended past the object's end.</summary>
    PastEnd,

    /// <summary>A delete completed.</summary>
    Deleted,

    /// <summary>A listing returned the names <see cref="ObjectOutcome.Listed"/> holds.</summary>
    Listed,

    /// <summary>A properties read returned <see cref="ObjectOutcome.Length"/> and <see cref="ObjectOutcome.Crc32C"/>.</summary>
    Properties,

    /// <summary>An append landed; the object holds <see cref="ObjectOutcome.Length"/> bytes.</summary>
    Appended,

    /// <summary>An append met an open object of <see cref="ObjectOutcome.Length"/> bytes.</summary>
    LengthMismatch,

    /// <summary>An append met a sealed or non-appendable object without its bytes, or a seal left the object sealed.</summary>
    Sealed,

    /// <summary>A seal met an object which is not appendable.</summary>
    NotAppendable,

    /// <summary>A tombstone replaced the object.</summary>
    Tombstoned,

    /// <summary>The call gave no answer: it threw, or its process crashed (archive §3).</summary>
    Unknown,
}

/// <summary>
/// What an object-store call answered: for <see cref="ObjectOutcomeKind.Present"/>, with the value read, or for an
/// appendable name the bytes read as hexadecimal <paramref name="Content"/>; for <see cref="ObjectOutcomeKind.Listed"/>,
/// with the set of listed names, bit <c>i</c> for <see cref="ObjectStoreSpec.Names"/>[<c>i</c>]; and for properties and
/// appends, with a length and a CRC-32C.
/// </summary>
internal sealed record ObjectOutcome(ObjectOutcomeKind Kind, byte Value = 0, int Listed = 0, long? Length = null, uint? Crc32C = null, string? Content = null)
{
    public static ObjectOutcome Unknown { get; } = new(ObjectOutcomeKind.Unknown);

    public override string ToString()
    {
        if (Kind == ObjectOutcomeKind.Present)
        {
            return Content is not null ? "Present(" + Content + ")" : string.Create(CultureInfo.InvariantCulture, $"Present({Value})");
        }

        if (Kind == ObjectOutcomeKind.Listed)
        {
            return "Listed[" + string.Join(", ", ObjectStoreSpec.NamesIn(Listed)) + "]";
        }

        if (Kind == ObjectOutcomeKind.Properties)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Properties({Length}, {Crc32C?.ToString("x8", CultureInfo.InvariantCulture) ?? "none"})");
        }

        return Kind is ObjectOutcomeKind.Appended or ObjectOutcomeKind.LengthMismatch
            ? string.Create(CultureInfo.InvariantCulture, $"{Kind}({Length})")
            : Kind.ToString();
    }
}

/// <summary>
/// The executable spec of <see cref="Orleans.Durable.Storage.Engine.ObjectStore.IObjectStore"/> (archive §3): a create stores an object only where
/// none exists, a replace applies exactly when the object holds the version it names, or none for a create, a read
/// returns exactly the requested bytes of the object or reports it absent, a delete leaves it absent, and a listing
/// returns exactly the existing names with its prefix after its start, a properties read gives the object's length and
/// the CRC-32C of a non-empty, non-appendable object. An appendable object is created empty and open, an append lands
/// exactly when the object is open at its expected length, a seal stops every later append, and a tombstone replaces the
/// object whole with a non-appendable one. An append to a sealed object which holds its bytes at its expected length,
/// as an earlier attempt's landing before the seal leaves it, answers <see cref="ObjectOutcomeKind.Appended"/>. A create which stored its object may answer
/// <see cref="ObjectOutcomeKind.Exists"/>, a replace which applied <see cref="ObjectOutcomeKind.Mismatch"/>, and an append
/// which landed <see cref="ObjectOutcomeKind.LengthMismatch"/> at its new length, as a client which retried its own landed
/// attempt reports it. Since every answer reflects durable state, the spec also judges
/// what a store recovers to after a crash (conventions.md §13).
/// </summary>
/// <remarks>
/// <para>
/// A listing is exact here, where nothing runs concurrently with it: the contract's hints are names created or deleted
/// during the listing, and the spec judges listings only alone.
/// </para>
/// <para>
/// With unknown outcomes, a call may give no answer, as one which throws or whose process crashes does. An unknown
/// create, replace, or delete is a pending step function which may take effect once at any later point, and only where
/// it applies, or never; a seal or tombstone may take effect wherever it lands; an unknown read or listing asserts
/// nothing.
/// </para>
/// </remarks>
internal static class ObjectStoreSpec
{
    internal const string CreateOperation = "Create";
    internal const string ReadOperation = "Read";
    internal const string ReadVersionedOperation = "ReadVersioned";
    internal const string ReplaceOperation = "Replace";
    internal const string DeleteOperation = "Delete";
    internal const string ListOperation = "List";
    internal const string PropertiesOperation = "Properties";
    internal const string CreateAppendableOperation = "CreateAppendable";
    internal const string AppendOperation = "Append";
    internal const string SealOperation = "Seal";
    internal const string TombstoneOperation = "Tombstone";

    /// <summary>
    /// The number of <see cref="Names"/> which creates and replaces write; the rest are appendable.
    /// </summary>
    internal const int PlainNames = 4;

    /// <summary>
    /// The length of each append's content, <see cref="Chunk"/> of its value.
    /// </summary>
    internal const int ChunkLength = 8;

    /// <summary>
    /// The value whose <see cref="Payload"/> every tombstone holds.
    /// </summary>
    internal const byte TombstoneValue = 17;

    private static readonly ConcurrentDictionary<byte, uint> PayloadCrcs = new();

    /// <summary>
    /// Gets the names the spec's requests address: the manifest, two drain outputs, a compaction output, and two
    /// appendable objects, an object-store ledger's extents.
    /// </summary>
    internal static IReadOnlyList<string> Names { get; } = ["manifest", "d/1/0/a1", "d/2/5/b2", "c/k/c3", "x/a", "x/b"];

    /// <summary>
    /// Gets the listing prefixes the spec's requests use: every name, the drain outputs, those of one ledger, the
    /// compaction outputs, and the appendable objects.
    /// </summary>
    internal static IReadOnlyList<string> Prefixes { get; } = ["", "d/", "d/1/", "c/", "x/"];

    /// <summary>
    /// Gets the positions after which the spec's listings start: none, a prefix, a name, and the names of the manifest and
    /// an appendable object.
    /// </summary>
    internal static IReadOnlyList<string?> Starts { get; } = [null, "c/", "d/1/0/a1", "manifest", "x/a"];

    /// <summary>
    /// The index of the one name the spec's caller replaces: the manifest.
    /// </summary>
    internal const int ReplacedName = 0;

    /// <summary>
    /// Returns whether <see cref="Names"/>[<paramref name="name"/>] is created once by <c>CreateAsync</c>.
    /// </summary>
    internal static bool IsCreatedOnce(int name) => name != ReplacedName && !IsAppendable(name);

    /// <summary>
    /// Returns whether <see cref="Names"/>[<paramref name="name"/>] is appendable.
    /// </summary>
    internal static bool IsAppendable(int name) => name >= PlainNames;

    /// <summary>
    /// Returns the content an append of <paramref name="value"/> carries: <see cref="ChunkLength"/> bytes of it.
    /// </summary>
    internal static byte[] Chunk(byte value)
    {
        var chunk = new byte[ChunkLength];
        Array.Fill(chunk, value);
        return chunk;
    }

    /// <summary>
    /// Returns the bytes <paramref name="target"/> holds: its appends' chunks, or its value's payload.
    /// </summary>
    internal static byte[] Bytes(ObjectState target) =>
        target.Appendable ? [.. target.Appends.SelectMany(Chunk)] : Payload(target.Value);

    /// <summary>
    /// The least value whose payload exceeds <see cref="LargeLength"/> bytes.
    /// </summary>
    internal const byte LargeValue = 200;

    /// <summary>
    /// The length a large payload exceeds: 4 MiB, four buffer pages.
    /// </summary>
    internal const int LargeLength = 4 * 1024 * 1024;

    /// <summary>
    /// Returns the content a write of <paramref name="value"/> stores: nothing for 0; <paramref name="value"/> repeated, from
    /// 1 to 1024 times, so a provider's objects span one or several sectors; or, from <see cref="LargeValue"/> up, more than
    /// 4 MiB times, so they span several buffer pages.
    /// </summary>
    internal static byte[] Payload(byte value)
    {
        var payload = GC.AllocateUninitializedArray<byte>(value == 0 ? 0 : value >= LargeValue ? LargeLength + value : 1 + value * 211 % 1024);
        Array.Fill(payload, value);
        return payload;
    }

    /// <summary>
    /// Returns the index of <paramref name="name"/> in <see cref="Names"/>, or -1 when no request addresses it.
    /// </summary>
    internal static int IndexOfName(string name)
    {
        for (var index = 0; index < Names.Count; index++)
        {
            if (string.Equals(Names[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the names whose bits <paramref name="listed"/> sets.
    /// </summary>
    internal static IEnumerable<string> NamesIn(int listed) => Names.Where((_, index) => (listed & (1 << index)) != 0);

    /// <summary>
    /// Returns whether a caller may issue <paramref name="request"/> in <paramref name="state"/>, as the callers' demand
    /// allows (object-store.md §3): the manifest is replaced and read only with versioned reads; the other plain names are
    /// created once, and read any way; a create or replace carries a value no earlier write of its name carried, a
    /// replace's non-empty; and an appendable call addresses only an appendable name.
    /// </summary>
    internal static bool IsCallerAllowed(object request, ObjectStoreState state) => request switch
    {
        ObjectCreate create => IsCreatedOnce(create.Name) && !state.Objects[create.Name].Written.Contains(create.Value),
        ObjectReplace replace => replace.Name == ReplacedName && replace.Value != 0 && !state.Objects[replace.Name].Written.Contains(replace.Value),
        ObjectReadVersioned read => !IsAppendable(read.Name),
        ObjectRead read => read.Name != ReplacedName,
        ObjectGetProperties properties => properties.Name != ReplacedName,
        ObjectCreateAppendable create => IsAppendable(create.Name),
        ObjectAppend append => IsAppendable(append.Name),
        ObjectSeal seal => IsAppendable(seal.Name),
        ObjectTombstone tombstone => IsAppendable(tombstone.Name),
        _ => true,
    };

    private static (int Name, byte Value)? Written(object request) => request switch
    {
        ObjectCreate create => (create.Name, create.Value),
        ObjectReplace replace => (replace.Name, replace.Value),
        _ => null,
    };

    /// <summary>
    /// Returns whether a caller may not issue <paramref name="first"/> and <paramref name="second"/> concurrently: two
    /// writes of one value to one name. The caller writes each value of a name at most once, as
    /// <see cref="IsCallerAllowed"/> requires of sequential writes, so each value has one version, on which
    /// <see cref="ObjectStoreExecutor"/>'s expected versions rest.
    /// </summary>
    internal static bool Conflicts(object first, object second) =>
        Written(first) is { } a && Written(second) is { } b && a == b;

    /// <summary>
    /// Returns the operation name of <paramref name="request"/>.
    /// </summary>
    internal static string OperationOf(object request) => request switch
    {
        ObjectCreate => CreateOperation,
        ObjectRead => ReadOperation,
        ObjectReadVersioned => ReadVersionedOperation,
        ObjectReplace => ReplaceOperation,
        ObjectDelete => DeleteOperation,
        ObjectList => ListOperation,
        ObjectGetProperties => PropertiesOperation,
        ObjectCreateAppendable => CreateAppendableOperation,
        ObjectAppend => AppendOperation,
        ObjectSeal => SealOperation,
        ObjectTombstone => TombstoneOperation,
        _ => throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{request} is not an object-store request."), nameof(request)),
    };

    /// <summary>
    /// Creates the spec; with <paramref name="unknownOutcomes"/>, every call may also give no answer.
    /// </summary>
    internal static Spec<ObjectStoreState> Create(bool unknownOutcomes)
    {
        var spec = new Spec<ObjectStoreState>();
        spec.Operation<ObjectCreate, ObjectOutcome>(CreateOperation, (request, state) =>
        {
            void Stored(ObjectStoreState next) => Store(Wrote(next, request.Name, request.Value), request.Value);
            ExpectedOutcome[] answered = state.Objects[request.Name].Exists
                ? [Is(ObjectOutcomeKind.Exists).ThenState<ObjectStoreState>(next => Wrote(next, request.Name, request.Value))]
                : [Is(ObjectOutcomeKind.Created).ThenState<ObjectStoreState>(Stored), Is(ObjectOutcomeKind.Exists).ThenState<ObjectStoreState>(Stored)];
            return Unknown(unknownOutcomes, answered, next => _ = Wrote(next, request.Name, request.Value), new Pending(request.ToString(), next => Land(next, request)));
        });
        spec.Operation<ObjectReplace, ObjectOutcome>(ReplaceOperation, (request, state) =>
        {
            void Stored(ObjectStoreState next) => Store(Wrote(next, request.Name, request.Value), request.Value);
            ExpectedOutcome[] answered = Applies(request, state.Objects[request.Name])
                ? [Is(ObjectOutcomeKind.Applied).ThenState<ObjectStoreState>(Stored), Is(ObjectOutcomeKind.Mismatch).ThenState<ObjectStoreState>(Stored)]
                : [Is(ObjectOutcomeKind.Mismatch).ThenState<ObjectStoreState>(next => Wrote(next, request.Name, request.Value))];
            return Unknown(unknownOutcomes, answered, next => _ = Wrote(next, request.Name, request.Value), new Pending(request.ToString(), next => Land(next, request)));
        });
        spec.Operation<ObjectDelete, ObjectOutcome>(DeleteOperation, (request, _) =>
        {
            ExpectedOutcome answered = Is(ObjectOutcomeKind.Deleted).ThenState<ObjectStoreState>(next => next.Objects[request.Name].Exists = false);
            return Unknown(unknownOutcomes, [answered], immediate: null, new Pending(request.ToString(), next => next.Objects[request.Name].Exists = false));
        });
        spec.Operation<ObjectRead, ObjectOutcome>(ReadOperation, (request, state) =>
        {
            var answered = ReadAnswer(request, state.Objects[request.Name]);
            return unknownOutcomes ? Expect.OneOf(answered, Is(ObjectOutcomeKind.Unknown).SameState()) : answered;
        });
        spec.Operation<ObjectReadVersioned, ObjectOutcome>(ReadVersionedOperation, (request, state) =>
        {
            var target = state.Objects[request.Name];
            ExpectedOutcome answered = target.Exists ? IsPresent(target.Value).SameState() : Is(ObjectOutcomeKind.Absent).SameState();
            return unknownOutcomes ? Expect.OneOf(answered, Is(ObjectOutcomeKind.Unknown).SameState()) : answered;
        });
        spec.Operation<ObjectList, ObjectOutcome>(ListOperation, (request, state) =>
        {
            var listed = Listing(state, Prefixes[request.Name], Starts[request.Start]);
            ExpectedOutcome answered = Expect.That<ObjectOutcome>(
                outcome => outcome.Kind == ObjectOutcomeKind.Listed && outcome.Listed == listed,
                "Expected " + new ObjectOutcome(ObjectOutcomeKind.Listed, Listed: listed) + ".").SameState();
            return unknownOutcomes ? Expect.OneOf(answered, Is(ObjectOutcomeKind.Unknown).SameState()) : answered;
        });
        spec.Operation<ObjectGetProperties, ObjectOutcome>(PropertiesOperation, (request, state) =>
        {
            var answered = PropertiesAnswer(state.Objects[request.Name]);
            return unknownOutcomes ? Expect.OneOf(answered, Is(ObjectOutcomeKind.Unknown).SameState()) : answered;
        });
        spec.Operation<ObjectCreateAppendable, ObjectOutcome>(CreateAppendableOperation, (request, state) =>
        {
            void Opened(ObjectStoreState next) => Open(next.Objects[request.Name]);
            ExpectedOutcome[] answered = state.Objects[request.Name].Exists
                ? [Is(ObjectOutcomeKind.Exists).SameState()]
                : [Is(ObjectOutcomeKind.Created).ThenState<ObjectStoreState>(Opened), Is(ObjectOutcomeKind.Exists).ThenState<ObjectStoreState>(Opened)];
            return Unknown(unknownOutcomes, answered, immediate: null, new Pending(request.ToString(), next =>
            {
                if (!next.Objects[request.Name].Exists)
                {
                    Open(next.Objects[request.Name]);
                }
            }));
        });
        spec.Operation<ObjectAppend, ObjectOutcome>(AppendOperation, (request, state) =>
        {
            var target = state.Objects[request.Name];
            void Appended(ObjectStoreState next) => next.Objects[request.Name].Appends.Add(request.Value);
            ExpectedOutcome[] answered = Appends(request, target)
                ?
                [
                    IsLength(ObjectOutcomeKind.Appended, (request.Appends + 1L) * ChunkLength).ThenState<ObjectStoreState>(Appended),
                    IsLength(ObjectOutcomeKind.LengthMismatch, (request.Appends + 1L) * ChunkLength).ThenState<ObjectStoreState>(Appended),
                ]
                : [AppendRefusal(request, target).SameState()];
            return Unknown(unknownOutcomes, answered, immediate: null, new Pending(request.ToString(), next =>
            {
                if (Appends(request, next.Objects[request.Name]))
                {
                    next.Objects[request.Name].Appends.Add(request.Value);
                }
            }));
        });
        spec.Operation<ObjectSeal, ObjectOutcome>(SealOperation, (request, state) =>
        {
            var target = state.Objects[request.Name];
            ExpectedOutcome answered = !target.Exists
                ? Is(ObjectOutcomeKind.Absent).SameState()
                : target.Appendable
                    ? Is(ObjectOutcomeKind.Sealed).ThenState<ObjectStoreState>(next => next.Objects[request.Name].Open = false)
                    : Is(ObjectOutcomeKind.NotAppendable).SameState();
            return Unknown(unknownOutcomes, [answered], immediate: null, new Pending(request.ToString(), next => next.Objects[request.Name].Open = false));
        });
        spec.Operation<ObjectTombstone, ObjectOutcome>(TombstoneOperation, (request, _) =>
        {
            ExpectedOutcome answered = Is(ObjectOutcomeKind.Tombstoned).ThenState<ObjectStoreState>(next => Tombstone(next.Objects[request.Name]));
            return Unknown(unknownOutcomes, [answered], immediate: null, new Pending(request.ToString(), next => Tombstone(next.Objects[request.Name])));
        });
        return spec;
    }

    /// <summary>
    /// Judges a sequential history of answered calls from an empty store, without unknown outcomes.
    /// </summary>
    /// <returns>The first answer the spec does not allow, described, or <see langword="null"/> when it allows them all.</returns>
    internal static string? FirstViolation(IEnumerable<(object Request, ObjectOutcome Outcome)> history)
    {
        var spec = Create(unknownOutcomes: false);
        var profile = new StateProfile(new ObjectStoreState());
        foreach (var (request, outcome) in history)
        {
            var (allowed, explanation, next) = spec.Allows(spec.GetOperation(OperationOf(request)), request, outcome, profile);
            if (!allowed)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{request} → {outcome}: {explanation}");
            }

            profile = next;
        }

        return null;
    }

    /// <summary>
    /// Returns the set of existing names with <paramref name="prefix"/> after <paramref name="start"/> in
    /// <paramref name="state"/>.
    /// </summary>
    internal static int Listing(ObjectStoreState state, string prefix, string? start = null)
    {
        var listed = 0;
        for (var index = 0; index < Names.Count; index++)
        {
            if (state.Objects[index].Exists
                && Names[index].StartsWith(prefix, StringComparison.Ordinal)
                && (start is null || string.CompareOrdinal(Names[index], start) > 0))
            {
                listed |= 1 << index;
            }
        }

        return listed;
    }

    /// <summary>
    /// Returns what a read of <paramref name="target"/> answers: absent, past its end, or its bytes: the value they
    /// repeat, or for an appendable name the bytes themselves.
    /// </summary>
    private static ExpectedOutcome ReadAnswer(ObjectRead read, ObjectState target)
    {
        if (!target.Exists)
        {
            return Is(ObjectOutcomeKind.Absent).SameState();
        }

        var bytes = Bytes(target);
        if (read.Offset + read.Length > bytes.Length)
        {
            return Is(ObjectOutcomeKind.PastEnd).SameState();
        }

        return IsAppendable(read.Name)
            ? IsContent(Convert.ToHexString(bytes, read.Offset, read.Length)).SameState()
            : IsPresent(target.Value).SameState();
    }

    /// <summary>
    /// Returns what a properties read of <paramref name="target"/> answers: absent, or its length and, unless it is empty
    /// or appendable, its CRC-32C.
    /// </summary>
    private static ExpectedOutcome PropertiesAnswer(ObjectState target)
    {
        if (!target.Exists)
        {
            return Is(ObjectOutcomeKind.Absent).SameState();
        }

        var bytes = Bytes(target);
        uint? crc32c = target.Appendable || bytes.Length == 0
            ? null
            : PayloadCrcs.GetOrAdd(target.Value, static (_, payload) => ObjectStoreChecksums.Crc32C(new ReadOnlySequence<byte>(payload)), bytes);
        var expected = new ObjectOutcome(ObjectOutcomeKind.Properties, Length: bytes.Length, Crc32C: crc32c);
        return Expect.That<ObjectOutcome>(outcome => outcome == expected, "Expected " + expected + ".").SameState();
    }

    /// <summary>
    /// Returns whether <paramref name="append"/> lands on <paramref name="target"/>: open at its expected length.
    /// </summary>
    private static bool Appends(ObjectAppend append, ObjectState target) =>
        target.Exists && target.Appendable && target.Open && target.Appends.Count == append.Appends;

    /// <summary>
    /// Returns what an append which does not land answers: absent; a mismatch with the length of an open object; appended,
    /// when a sealed object holds its bytes at its expected length; or sealed.
    /// </summary>
    private static ExpectedOutcomeBuilder<ObjectOutcome> AppendRefusal(ObjectAppend append, ObjectState target)
    {
        if (!target.Exists)
        {
            return Is(ObjectOutcomeKind.Absent);
        }

        if (target.Appendable && target.Open)
        {
            return IsLength(ObjectOutcomeKind.LengthMismatch, (long)target.Appends.Count * ChunkLength);
        }

        return target.Appendable && target.Appends.Count > append.Appends && target.Appends[append.Appends] == append.Value
            ? IsLength(ObjectOutcomeKind.Appended, (append.Appends + 1L) * ChunkLength)
            : Is(ObjectOutcomeKind.Sealed);
    }

    private static void Open(ObjectState target)
    {
        target.Exists = true;
        target.Appendable = true;
        target.Open = true;
        target.Appends.Clear();
        target.Value = 0;
    }

    private static void Tombstone(ObjectState target)
    {
        target.Exists = true;
        target.Appendable = false;
        target.Open = false;
        target.Appends.Clear();
        target.Value = TombstoneValue;
    }

    private static bool Applies(ObjectReplace replace, ObjectState target) =>
        replace.Expected is { } expected ? target.Exists && target.Value == expected : !target.Exists;

    private static ObjectState Wrote(ObjectStoreState state, int name, byte value)
    {
        var target = state.Objects[name];
        if (!target.Written.Contains(value))
        {
            target.Written.Add(value);
        }

        return target;
    }

    private static void Store(ObjectState target, byte value)
    {
        target.Exists = true;
        target.Value = value;
    }

    private static void Land(ObjectStoreState state, ObjectCreate create)
    {
        var target = state.Objects[create.Name];
        if (!target.Exists)
        {
            Store(target, create.Value);
        }
    }

    private static void Land(ObjectStoreState state, ObjectReplace replace)
    {
        var target = state.Objects[replace.Name];
        if (Applies(replace, target))
        {
            Store(target, replace.Value);
        }
    }

    /// <summary>
    /// Returns the <paramref name="answered"/> outcomes, or, with unknown outcomes, also no answer, which applies
    /// <paramref name="immediate"/> and leaves <paramref name="pending"/> to land later.
    /// </summary>
    private static ExpectedOutcomes Unknown(bool unknownOutcomes, ExpectedOutcome[] answered, Action<ObjectStoreState>? immediate, Pending pending)
    {
        if (!unknownOutcomes)
        {
            return Expect.OneOf(answered);
        }

        var unknown = Is(ObjectOutcomeKind.Unknown);
        return Expect.OneOf([.. answered, (immediate is null ? unknown.SameState() : unknown.ThenState(immediate)).Triggers(pending)]);
    }

    private static ExpectedOutcomeBuilder<ObjectOutcome> Is(ObjectOutcomeKind kind) =>
        Expect.That<ObjectOutcome>(outcome => outcome.Kind == kind, string.Create(CultureInfo.InvariantCulture, $"Expected {kind}."));

    private static ExpectedOutcomeBuilder<ObjectOutcome> IsLength(ObjectOutcomeKind kind, long? length) =>
        Expect.That<ObjectOutcome>(
            outcome => outcome.Kind == kind && outcome.Length == length,
            string.Create(CultureInfo.InvariantCulture, $"Expected {new ObjectOutcome(kind, Length: length)}."));

    private static ExpectedOutcomeBuilder<ObjectOutcome> IsContent(string content) =>
        Expect.That<ObjectOutcome>(
            outcome => outcome.Kind == ObjectOutcomeKind.Present && outcome.Content == content,
            "Expected Present(" + content + ").");

    private static ExpectedOutcomeBuilder<ObjectOutcome> IsPresent(byte value) =>
        Expect.That<ObjectOutcome>(
            outcome => outcome.Kind == ObjectOutcomeKind.Present && outcome.Value == value,
            string.Create(CultureInfo.InvariantCulture, $"Expected Present({value})."));

    /// <summary>
    /// A write with an unknown outcome, which may take effect once at any later point, only where it applies.
    /// </summary>
    private sealed class Pending(string id, Action<ObjectStoreState> land) : BaseStepFunction
    {
        public override string StepFunctionId => id;

        protected override IList<StepResult> ApplyInternal(IState state)
        {
            var next = (ObjectStoreState)((ObjectStoreState)state).Clone();
            land(next);
            return [new StepResult { State = next }];
        }
    }
}
