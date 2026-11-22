using System.Diagnostics;
using System.Globalization;
using Microsoft.Accordant;

namespace Orleans.Durable.Storage.Testing;

#pragma warning disable CA1852 // Accordant generates a virtual clone factory for state types.

/// <summary>
/// The abstract state of an <see cref="Orleans.Durable.Storage.Engine.Archive.Logs.IArchiveLogStore"/> over one log (archive-logs.md §3): its
/// current generation, the lengths of the units appended to it, the opener which created it, and each generation's base.
/// </summary>
[State]
internal partial class ArchiveLogStoreState
{
    /// <summary>Gets or sets a value indicating whether the log exists.</summary>
    public bool Exists { get; set; }

    /// <summary>Gets or sets the current generation, 0 while the log is absent.</summary>
    public int Generation { get; set; }

    /// <summary>Gets or sets the lengths of the current generation's units, in order.</summary>
    public List<int> Units { get; set; } = [];

    /// <summary>Gets or sets the opener which created the current generation, <see cref="SpecOpener.None"/> for a first run's.</summary>
    public SpecOpener Opener { get; set; }

    /// <summary>Gets or sets the stored bases.</summary>
    public List<ArchiveLogBaseState> Bases { get; set; } = [];

    /// <summary>Gets or sets every opener the caller issued, whatever its outcome.</summary>
    public List<ArchiveLogIssuedOpenerState> Openers { get; set; } = [];
}

/// <summary>An opener the caller issued: its starting generation, its kind, and its base's length.</summary>
[State]
internal partial class ArchiveLogIssuedOpenerState
{
    /// <summary>Gets or sets the generation the opener starts from.</summary>
    public int From { get; set; }

    /// <summary>Gets or sets the opener's kind.</summary>
    public SpecOpener Opener { get; set; }

    /// <summary>Gets or sets the length of the opener's base.</summary>
    public int BaseLength { get; set; }
}

/// <summary>A stored base: its generation and its length; its bytes follow from both (<see cref="ArchiveLogStoreSpec.BaseByte"/>).</summary>
[State]
internal partial class ArchiveLogBaseState
{
    /// <summary>Gets or sets the base's generation.</summary>
    public int Generation { get; set; }

    /// <summary>Gets or sets the base's length.</summary>
    public int Length { get; set; }
}

#pragma warning restore CA1852

/// <summary>An opener's kind as the spec names it.</summary>
internal enum SpecOpener
{
    /// <summary>No opener: a generation 1 a key's first run created.</summary>
    None,

    /// <summary>A <c>Replace</c>.</summary>
    Replace,

    /// <summary>An empty <c>Delete</c>.</summary>
    Delete,

    /// <summary>A <c>Delete</c> with <see cref="ArchiveLogStoreSpec.Tombstone"/>.</summary>
    DeleteWithTombstone,
}

/// <summary>
/// An <c>AppendAsync</c> from <c>(G, P, B)</c>, <c>Origin</c> when <c>G</c> is 0, of units of <paramref name="Units"/>'
/// lengths, whose bytes follow from their generation and offsets (<see cref="ArchiveLogStoreSpec.UnitByte"/>), so every
/// writer of a position writes the same bytes.
/// </summary>
internal sealed record LogAppend(int G, int P, int B, int[] Units)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Append(({G}, {P}, {B}), [{string.Join(", ", Units)}])");
}

/// <summary>An <c>OpenAsync</c> from <c>(G, P, B)</c> of <paramref name="Opener"/>, with a base of <paramref name="BaseLength"/> bytes.</summary>
internal sealed record LogOpen(int G, int P, int B, SpecOpener Opener, int BaseLength)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Open(({G}, {P}, {B}), {ArchiveLogStoreSpec.OpenerName(Opener)}, {BaseLength})");
}

/// <summary>A <c>ReadAsync</c>.</summary>
internal sealed record LogRead
{
    public static LogRead Instance { get; } = new();

    public override string ToString() => "Read";
}

/// <summary>A <c>GetPropertiesAsync</c>.</summary>
internal sealed record LogProperties
{
    public static LogProperties Instance { get; } = new();

    public override string ToString() => "Properties";
}

/// <summary>A <c>ListGenerationsAsync</c>.</summary>
internal sealed record LogList
{
    public static LogList Instance { get; } = new();

    public override string ToString() => "List";
}

/// <summary>A <c>DeleteGenerationAsync</c> of generation <paramref name="G"/>'s base.</summary>
internal sealed record LogDeleteBase(int G)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"DeleteBase({G})");
}

/// <summary>The kinds of <see cref="LogOutcome"/>.</summary>
internal enum LogOutcomeKind
{
    /// <summary>A write applied.</summary>
    Applied,

    /// <summary>A write's condition failed.</summary>
    ConditionFailed,

    /// <summary>A read or properties read found no log.</summary>
    Absent,

    /// <summary>A read found the log changed or an object it names gone.</summary>
    Stale,

    /// <summary>A read found the log: <see cref="LogOutcome.Text"/> holds its fields and content.</summary>
    Read,

    /// <summary>A properties read found the log: <see cref="LogOutcome.Text"/> holds its position and opener.</summary>
    Properties,

    /// <summary>A listing found the bases <see cref="LogOutcome.Text"/> names.</summary>
    Listed,

    /// <summary>A deletion completed.</summary>
    Deleted,

    /// <summary>The call gave no answer: it threw, or its process crashed.</summary>
    Unknown,

    /// <summary>A read which a write overlapped found the log changed, or an object it names gone, as the target observed.</summary>
    StaleRacing,
}

/// <summary>What an archive-log store call answered, with its fields as text for the kinds which carry them.</summary>
internal sealed record LogOutcome(LogOutcomeKind Kind, string Text = "")
{
    public static LogOutcome Unknown { get; } = new(LogOutcomeKind.Unknown);

    public override string ToString() => Text.Length == 0 ? Kind.ToString() : Kind + "(" + Text + ")";
}

/// <summary>
/// The executable spec of <see cref="Orleans.Durable.Storage.Engine.Archive.Logs.IArchiveLogStore"/> over one log (archive-logs.md §3, LOG-I1,
/// LOG-I2): an append unit lands only while the log is at its generation with the length at which the unit starts, a run
/// from <c>Origin</c> first creating generation 1 only when the log is absent; an opener creates its base only when absent
/// and its generation only while the log is at the generation it starts from; a read answers the current generation's
/// base and appends, or <c>Stale</c> once that base is gone; a properties read the position and the opener; a listing
/// the stored bases; and a deletion removes one.
/// </summary>
/// <remarks>
/// <para>
/// A write which lands may answer <c>ConditionFailed</c>, as a retry which meets its own landing does, having landed its
/// units through that one and nothing after; and an append run may answer <c>ConditionFailed</c> having landed a shorter
/// prefix, as one does when another write lands between two of its requests. With unknown outcomes, a call may give no
/// answer: an append leaves any prefix of its units landed and each later one pending, a pending step which may land once
/// at any later point, only where its condition holds; an opener leaves nothing, its base, or both landed, the rest
/// pending; and a read, properties read, or listing asserts nothing. With concurrent readers, a read which a write
/// overlapped may also answer <c>Stale</c>, as one whose resumed body or base met a change does; a read no write
/// overlapped answers <c>Stale</c> only once its base is gone.
/// </para>
/// <para>
/// The caller's preconditions are the writer's: two openers from one generation agree, since a segment fixes each key's
/// write; and a base is deleted only once every possible state shows a newer generation (LOG-I2). A generation's units
/// have one length (<see cref="UnitLength"/>), so every request's position names its units as a token in a key's chain
/// does.
/// </para>
/// </remarks>
internal static class ArchiveLogStoreSpec
{
    internal const string AppendOperation = "Append";
    internal const string OpenOperation = "Open";
    internal const string ReadOperation = "Read";
    internal const string PropertiesOperation = "Properties";
    internal const string ListOperation = "List";
    internal const string DeleteBaseOperation = "DeleteBase";

    /// <summary>The tombstone of every tombstoned <c>Delete</c>.</summary>
    internal const ulong Tombstone = 0x0102030405060708;

    /// <summary>
    /// Returns the length of every unit of <paramref name="generation"/>: 2 bytes on generation 2 and 1 elsewhere, so a
    /// position's units follow from its length, as a token's in a key's chain do, and some units span two segments.
    /// </summary>
    internal static int UnitLength(int generation) => generation == 2 ? 2 : 1;

    /// <summary>Returns the byte a unit holds at <paramref name="offset"/> of generation <paramref name="generation"/>'s appends.</summary>
    internal static byte UnitByte(int generation, int offset) => unchecked((byte)(generation * 37 + offset * 11 + 5));

    /// <summary>Returns the byte generation <paramref name="generation"/>'s base holds at <paramref name="offset"/>.</summary>
    internal static byte BaseByte(int generation, int offset) => unchecked((byte)(generation * 13 + offset * 7 + 101));

    /// <summary>Returns an opener's name.</summary>
    internal static string OpenerName(SpecOpener opener) => opener switch
    {
        SpecOpener.None => "none",
        SpecOpener.Replace => "Replace",
        SpecOpener.Delete => "Delete",
        SpecOpener.DeleteWithTombstone => "Delete(tombstone)",
        _ => throw new UnreachableException(),
    };

    /// <summary>Returns the text of a read of a log at <c>(g, P, b)</c>: its position, base flag, tombstone, and content.</summary>
    internal static string ReadText(ulong generation, ulong length, ulong units, bool hasBase, ulong? tombstone, byte[] content) =>
        string.Create(CultureInfo.InvariantCulture, $"({generation}, {length}, {units}) base={hasBase} tomb={tombstone?.ToString("x16", CultureInfo.InvariantCulture) ?? "none"} content={Convert.ToHexStringLower(content)}");

    /// <summary>Returns the text of a properties read: the position and opener.</summary>
    internal static string PropertiesText(ulong generation, ulong length, ulong units, SpecOpener opener) =>
        string.Create(CultureInfo.InvariantCulture, $"({generation}, {length}, {units}) {OpenerName(opener)}");

    /// <summary>Returns whether a caller may issue <paramref name="request"/> in <paramref name="state"/>.</summary>
    internal static bool IsCallerAllowed(object request, ArchiveLogStoreState state) => request switch
    {
        LogOpen open => !state.Openers.Any(issued => issued.From == open.G && (issued.Opener, issued.BaseLength) != (open.Opener, open.BaseLength)),
        LogDeleteBase delete => state.Exists && state.Generation > delete.G,
        _ => true,
    };

    /// <summary>
    /// Returns whether two requests may not run concurrently: openers from one generation which differ, which no caller
    /// issues; and, since the spec judges each call at one point, a read with a write of several requests, whose middle a
    /// read may see: an append from <c>Origin</c>, which creates the log first, or of several units, and, for a listing, an
    /// opener, whose base lands before its generation. Reads beside single requests, and writes beside writes, run together.
    /// </summary>
    internal static bool Conflicts(object first, object second) =>
        first is LogOpen a && second is LogOpen b && a.G == b.G && a != b
        || SeesTheMiddle(first, second)
        || SeesTheMiddle(second, first);

    private static bool SeesTheMiddle(object read, object write) => (read, write) switch
    {
        (LogRead or LogProperties or LogList, LogAppend append) => append.G == 0 || append.Units.Length > 1,
        (LogList, LogOpen) => true,
        _ => false,
    };

    /// <summary>Returns the operation name of <paramref name="request"/>.</summary>
    internal static string OperationOf(object request) => request switch
    {
        LogAppend => AppendOperation,
        LogOpen => OpenOperation,
        LogRead => ReadOperation,
        LogProperties => PropertiesOperation,
        LogList => ListOperation,
        LogDeleteBase => DeleteBaseOperation,
        _ => throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{request} is not an archive-log store request."), nameof(request)),
    };

    /// <summary>
    /// Creates the spec; with <paramref name="unknownOutcomes"/>, every call may also give no answer; with
    /// <paramref name="staleReads"/>, a read which a write overlapped may answer <c>Stale</c> as one racing it does, which
    /// the target reports as <see cref="LogOutcomeKind.StaleRacing"/>; with
    /// <paramref name="retriesMeetLandings"/>, a write which landed may answer <c>ConditionFailed</c>, as a retry meeting its
    /// own landing does; with <paramref name="interleavedWrites"/>, another write may land between an append run's
    /// requests, a concurrent call's or a straggler's which lands late, so the run may answer <c>ConditionFailed</c> having
    /// landed any shorter prefix of the units it would land, its create alone among them. A request in flight after its
    /// call answered lands only where its condition holds, which positions only growing make the condition its retry
    /// already failed, so it lands nothing a <c>ConditionFailed</c> left out. With <paramref name="chunkLayout"/>, the
    /// store is the chunk layout's (archive-logs.md §7.4): a key's first append run creates an empty base of generation 1,
    /// which a listing names, and an opener creates its base only on the generation it starts from.
    /// </summary>
    internal static Spec<ArchiveLogStoreState> Create(bool unknownOutcomes, bool staleReads = false, bool retriesMeetLandings = true, bool interleavedWrites = true, bool chunkLayout = false)
    {
        var spec = new Spec<ArchiveLogStoreState>();
        spec.Operation<LogAppend, LogOutcome>(AppendOperation, (request, state) => AppendOutcomes(request, state, unknownOutcomes, retriesMeetLandings, interleavedWrites, chunkLayout));
        spec.Operation<LogOpen, LogOutcome>(OpenOperation, (request, state) => OpenOutcomes(request, state, unknownOutcomes, retriesMeetLandings, chunkLayout));
        spec.Operation<LogRead, LogOutcome>(ReadOperation, (_, state) =>
        {
            ExpectedOutcome answered = Is(ReadAnswer(state)).SameState();
            List<ExpectedOutcome> allowed = [answered];
            if (staleReads && state.Exists)
            {
                allowed.Add(Is(new LogOutcome(LogOutcomeKind.StaleRacing)).SameState());
            }

            if (unknownOutcomes)
            {
                allowed.Add(Is(LogOutcome.Unknown).SameState());
            }

            return Expect.OneOf([.. allowed]);
        });
        spec.Operation<LogProperties, LogOutcome>(PropertiesOperation, (_, state) =>
        {
            ExpectedOutcome answered = Is(state.Exists
                ? new LogOutcome(LogOutcomeKind.Properties, PropertiesText((ulong)state.Generation, (ulong)Length(state), (ulong)state.Units.Count, state.Opener))
                : new LogOutcome(LogOutcomeKind.Absent)).SameState();
            return unknownOutcomes ? Expect.OneOf(answered, Is(LogOutcome.Unknown).SameState()) : answered;
        });
        spec.Operation<LogList, LogOutcome>(ListOperation, (_, state) =>
        {
            ExpectedOutcome answered = Is(new LogOutcome(LogOutcomeKind.Listed, ListText(state))).SameState();
            return unknownOutcomes ? Expect.OneOf(answered, Is(LogOutcome.Unknown).SameState()) : answered;
        });
        spec.Operation<LogDeleteBase, LogOutcome>(DeleteBaseOperation, (request, _) =>
        {
            ExpectedOutcome answered = Is(new LogOutcome(LogOutcomeKind.Deleted)).ThenState<ArchiveLogStoreState>(next => RemoveBase(next, request.G));
            return unknownOutcomes
                ? Expect.OneOf(answered, Is(LogOutcome.Unknown).SameState().Triggers(new Pending(request.ToString(), next => RemoveBase(next, request.G))))
                : answered;
        });
        return spec;
    }

    /// <summary>Returns the text of a listing of <paramref name="state"/>'s bases: their generations in order.</summary>
    internal static string ListText(ArchiveLogStoreState state) => string.Join(",", state.Bases.Select(static stored => stored.Generation).Order());

    private static (int G, int P, int B) Start(LogAppend append) => append.G == 0 ? (1, 0, 0) : (append.G, append.P, append.B);

    private static int Length(ArchiveLogStoreState state) => state.Units.Sum();

    private static LogOutcome ReadAnswer(ArchiveLogStoreState state)
    {
        if (!state.Exists)
        {
            return new LogOutcome(LogOutcomeKind.Absent);
        }

        // A generation which a key's first run created names no base, even when a straggler opener's lies beside it.
        var stored = state.Opener == SpecOpener.None ? null : state.Bases.FirstOrDefault(candidate => candidate.Generation == state.Generation);
        if (state.Opener != SpecOpener.None && stored is null)
        {
            return new LogOutcome(LogOutcomeKind.Stale);
        }

        var content = new List<byte>();
        for (var offset = 0; offset < (stored?.Length ?? 0); offset++)
        {
            content.Add(BaseByte(state.Generation, offset));
        }

        var length = Length(state);
        for (var offset = 0; offset < length; offset++)
        {
            content.Add(UnitByte(state.Generation, offset));
        }

        var tombstone = state.Opener == SpecOpener.DeleteWithTombstone && stored is { Length: 0 } && length == 0 ? Tombstone : (ulong?)null;
        return new LogOutcome(LogOutcomeKind.Read, ReadText((ulong)state.Generation, (ulong)length, (ulong)state.Units.Count, state.Opener == SpecOpener.Replace, tombstone, [.. content]));
    }

    /// <summary>
    /// The outcomes of an append: <c>Applied</c> when every unit lands in turn; <c>ConditionFailed</c> having landed the
    /// units whose conditions held, or, as a retry meeting its own landing, any non-empty prefix of them, or, where another
    /// write may land between the run's requests, any shorter prefix, its create alone among them; and, with unknown
    /// outcomes, no answer with any prefix landed and the next unit pending.
    /// </summary>
    private static ExpectedOutcomes AppendOutcomes(LogAppend request, ArchiveLogStoreState state, bool unknownOutcomes, bool retriesMeetLandings, bool interleavedWrites, bool chunkLayout)
    {
        var landable = Landable(request, state);
        var outcomes = new List<ExpectedOutcome>();
        var failed = new SortedSet<int>();
        if (landable == request.Units.Length)
        {
            outcomes.Add(Is(new LogOutcome(LogOutcomeKind.Applied)).ThenState<ArchiveLogStoreState>(next => Land(next, request, landable, chunkLayout)));
        }
        else
        {
            _ = failed.Add(landable);
        }

        for (var landed = 1; retriesMeetLandings && landed <= landable; landed++)
        {
            _ = failed.Add(landed);
        }

        // The run's create and each of its units land apart, so another write may land between two of them, at the
        // position the next one starts from, which then fails its condition (archive-logs.md §3).
        for (var landed = request.G == 0 && !state.Exists ? 0 : 1; interleavedWrites && landed < landable; landed++)
        {
            _ = failed.Add(landed);
        }

        foreach (var count in failed)
        {
            outcomes.Add(Is(new LogOutcome(LogOutcomeKind.ConditionFailed)).ThenState<ArchiveLogStoreState>(next => Land(next, request, count, chunkLayout)));
        }

        if (unknownOutcomes)
        {
            if (request.G == 0 && !state.Exists)
            {
                // The run's create may not have landed either, or may land later, alone or with the first unit. One
                // Triggers call names both: each call replaces the pending steps an earlier one named.
                outcomes.Add(Is(LogOutcome.Unknown).SameState()
                    .Triggers(
                        new Pending(request + "#create", next =>
                        {
                            if (!next.Exists)
                            {
                                CreateLog(next, chunkLayout);
                            }
                        }),
                        new Pending(request + "#0", next => LandUnit(next, request, 0, chunkLayout))));
            }

            for (var landed = 0; landed <= landable; landed++)
            {
                var count = landed;
                var unknown = Is(LogOutcome.Unknown).ThenState<ArchiveLogStoreState>(next => Land(next, request, count, chunkLayout));
                outcomes.Add(count < request.Units.Length
                    ? unknown.Triggers(new Pending(request + "#" + count.ToString(CultureInfo.InvariantCulture), next => LandUnit(next, request, count, chunkLayout)))
                    : unknown);
            }
        }

        return Expect.OneOf([.. outcomes]);
    }

    /// <summary>
    /// The outcomes of an opener: <c>Applied</c> when its generation lands; <c>ConditionFailed</c> having landed its base
    /// alone, or both when its generation landed and a retry met it; and, with unknown outcomes, no answer with nothing,
    /// its base, or both landed, the rest pending.
    /// </summary>
    private static ExpectedOutcomes OpenOutcomes(LogOpen request, ArchiveLogStoreState state, bool unknownOutcomes, bool retriesMeetLandings, bool chunkLayout)
    {
        var opens = OpenApplies(request, state);
        var outcomes = new List<ExpectedOutcome>();
        if (chunkLayout)
        {
            // The chunk layout creates an opener's base only once it has listed the log at the generation the opener starts
            // from, so an opener whose condition fails creates nothing; one whose base lands late lands it as its generation
            // or, once newer generations replaced it, below them.
            if (opens)
            {
                outcomes.Add(Is(new LogOutcome(LogOutcomeKind.Applied)).ThenState<ArchiveLogStoreState>(next => Open(Issue(next, request), request)));
                if (retriesMeetLandings)
                {
                    outcomes.Add(Is(new LogOutcome(LogOutcomeKind.ConditionFailed)).ThenState<ArchiveLogStoreState>(next => Open(Issue(next, request), request)));
                }
            }
            else
            {
                outcomes.Add(Is(new LogOutcome(LogOutcomeKind.ConditionFailed)).ThenState<ArchiveLogStoreState>(next => Issue(next, request)));
            }

            if (unknownOutcomes)
            {
                outcomes.Add(Is(LogOutcome.Unknown).ThenState<ArchiveLogStoreState>(next => Issue(next, request))
                    .Triggers(new Pending(request + "#base", next => LandBase(next, request))));
                if (opens)
                {
                    outcomes.Add(Is(LogOutcome.Unknown).ThenState<ArchiveLogStoreState>(next => Open(Issue(next, request), request)));
                }
            }

            return Expect.OneOf([.. outcomes]);
        }
        if (opens)
        {
            outcomes.Add(Is(new LogOutcome(LogOutcomeKind.Applied)).ThenState<ArchiveLogStoreState>(next => Open(Issue(next, request), request)));
            if (retriesMeetLandings)
            {
                outcomes.Add(Is(new LogOutcome(LogOutcomeKind.ConditionFailed)).ThenState<ArchiveLogStoreState>(next => Open(Issue(next, request), request)));
            }
        }
        else
        {
            outcomes.Add(Is(new LogOutcome(LogOutcomeKind.ConditionFailed)).ThenState<ArchiveLogStoreState>(next => CreateBase(Issue(next, request), request)));
        }

        if (unknownOutcomes)
        {
            outcomes.Add(Is(LogOutcome.Unknown).ThenState<ArchiveLogStoreState>(next => Issue(next, request))
                .Triggers(
                    new Pending(request + "#base", next => CreateBase(next, request)),
                    new Pending(request + "#all", next => Open(next, request))));
            outcomes.Add(Is(LogOutcome.Unknown).ThenState<ArchiveLogStoreState>(next => CreateBase(Issue(next, request), request))
                .Triggers(new Pending(request + "#generation", next => OpenGeneration(next, request))));
            if (opens)
            {
                outcomes.Add(Is(LogOutcome.Unknown).ThenState<ArchiveLogStoreState>(next => Open(Issue(next, request), request)));
            }
        }

        return Expect.OneOf([.. outcomes]);
    }

    /// <summary>Returns how many of an append's units land in turn from <paramref name="state"/>.</summary>
    private static int Landable(LogAppend request, ArchiveLogStoreState state)
    {
        var (generation, offset, _) = Start(request);
        var exists = state.Exists || request.G == 0;
        var current = state.Exists ? state.Generation : generation;
        var length = state.Exists ? Length(state) : 0;
        var landed = 0;
        foreach (var unit in request.Units)
        {
            if (!exists || current != generation || length != offset)
            {
                break;
            }

            landed++;
            length += unit;
            offset += unit;
        }

        return landed;
    }

    private static void Land(ArchiveLogStoreState state, LogAppend request, int count, bool chunkLayout)
    {
        if (request.G == 0 && !state.Exists)
        {
            CreateLog(state, chunkLayout);
        }

        for (var unit = 0; unit < count; unit++)
        {
            LandUnit(state, request, unit, chunkLayout);
        }
    }

    /// <summary>Lands one unit of an append, only where its condition holds: a run from <c>Origin</c> creates the log first.</summary>
    private static void LandUnit(ArchiveLogStoreState state, LogAppend request, int unit, bool chunkLayout)
    {
        if (request.G == 0 && !state.Exists)
        {
            CreateLog(state, chunkLayout);
        }

        var (generation, offset, _) = Start(request);
        offset += request.Units.Take(unit).Sum();
        if (state.Exists && state.Generation == generation && Length(state) == offset)
        {
            state.Units.Add(request.Units[unit]);
        }
    }

    /// <summary>
    /// Creates the log at generation 1, as a key's first append run does; on the chunk layout with an empty base of no
    /// kind, which a listing names.
    /// </summary>
    private static void CreateLog(ArchiveLogStoreState state, bool chunkLayout)
    {
        state.Exists = true;
        state.Generation = 1;
        state.Units.Clear();
        state.Opener = SpecOpener.None;
        if (chunkLayout && !state.Bases.Any(static stored => stored.Generation == 1))
        {
            state.Bases.Add(new ArchiveLogBaseState { Generation = 1, Length = 0 });
        }
    }

    private static bool OpenApplies(LogOpen request, ArchiveLogStoreState state) =>
        request.G == 0 ? !state.Exists : state.Exists && state.Generation == request.G;

    private static ArchiveLogStoreState Issue(ArchiveLogStoreState state, LogOpen request)
    {
        if (!state.Openers.Any(issued => issued.From == request.G && issued.Opener == request.Opener && issued.BaseLength == request.BaseLength))
        {
            state.Openers.Add(new ArchiveLogIssuedOpenerState { From = request.G, Opener = request.Opener, BaseLength = request.BaseLength });
        }

        return state;
    }

    private static ArchiveLogStoreState CreateBase(ArchiveLogStoreState state, LogOpen request)
    {
        if (!state.Bases.Any(stored => stored.Generation == request.G + 1))
        {
            state.Bases.Add(new ArchiveLogBaseState { Generation = request.G + 1, Length = request.BaseLength });
        }

        return state;
    }

    private static void OpenGeneration(ArchiveLogStoreState state, LogOpen request)
    {
        if (OpenApplies(request, state))
        {
            state.Exists = true;
            state.Generation = request.G + 1;
            state.Units.Clear();
            state.Opener = request.Opener;
        }
    }

    private static void Open(ArchiveLogStoreState state, LogOpen request) => OpenGeneration(CreateBase(state, request), request);

    /// <summary>
    /// Lands a chunk-layout opener's base late: as its generation while the log is at the one it starts from, and, once
    /// newer generations replaced its own, as a base below them, which the sweeper deletes again.
    /// </summary>
    private static void LandBase(ArchiveLogStoreState state, LogOpen request)
    {
        if (OpenApplies(request, state))
        {
            Open(state, request);
        }
        else if (state.Exists && state.Generation > request.G + 1)
        {
            _ = CreateBase(state, request);
        }
    }

    private static void RemoveBase(ArchiveLogStoreState state, int generation) =>
        _ = state.Bases.RemoveAll(stored => stored.Generation == generation);

    private static ExpectedOutcomeBuilder<LogOutcome> Is(LogOutcome expected) =>
        Expect.That<LogOutcome>(outcome => outcome == expected, "Expected " + expected + ".");

    /// <summary>
    /// A write with an unknown outcome, which may take effect once at any later point, only where it applies. Where it
    /// would change nothing it does not fire, and stays pending: firing it there reaches no state its staying pending
    /// does not, and each such firing would double the states the spec tracks.
    /// </summary>
    private sealed class Pending(string id, Action<ArchiveLogStoreState> land) : BaseStepFunction
    {
        public override string StepFunctionId => id;

        protected override IList<StepResult> ApplyInternal(IState state)
        {
            var next = (ArchiveLogStoreState)((ArchiveLogStoreState)state).Clone();
            land(next);
            return next.StringRepresentation() == state.StringRepresentation() ? [] : [new StepResult { State = next }];
        }
    }
}
