using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The case tags of the control plane's surrogates (orleans.md §7.2). An unknown tag, and a case without the fields it
/// carries, is refused with <see cref="InvalidDataException"/>; every array, dictionary, and nested value a surrogate
/// writes is present, so one absent from a message is a field missing.
/// </summary>
internal static class ControlTags
{
    /// <summary>Returns the refusal of <paramref name="tag"/>, which names no case of <paramref name="what"/>.</summary>
    public static InvalidDataException Unknown(string what, byte tag, int highest) =>
        new(string.Create(CultureInfo.InvariantCulture, $"A {what}'s case tag is 1 to {highest}, not {tag}."));
}

/// <summary>A transition's choice as a grain message carries it: its executor, location, window, and provenance.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct TransitionChoiceSurrogate
{
    /// <summary>Gets or initializes the executor.</summary>
    [Id(0)]
    public NodeId Executor { get; init; }

    /// <summary>Gets or initializes the location of the ledgers the transition opens.</summary>
    [Id(1)]
    public LedgerLocation? Location { get; init; }

    /// <summary>Gets or initializes the sequencer window.</summary>
    [Id(2)]
    public int K { get; init; }

    /// <summary>Gets or initializes the revision of the registry read the nodes were chosen from.</summary>
    [Id(3)]
    public ulong ChosenAt { get; init; }

    /// <summary>Gets or initializes the epoch of the map value the transition was chosen from.</summary>
    [Id(4)]
    public ulong Epoch { get; init; }
}

/// <summary>Converts a transition's choice to its surrogate and back, refusing fields its constructor refuses.</summary>
[RegisterConverter]
internal sealed class TransitionChoiceConverter : IConverter<TransitionChoice, TransitionChoiceSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The choice's location, window, or executor break its rules.</exception>
    public TransitionChoice ConvertFromSurrogate(in TransitionChoiceSurrogate surrogate)
    {
        var location = surrogate.Location ?? throw new InvalidDataException("A transition choice names its location.");
        return SurrogateFields.Build("transition choice", (surrogate, location), static fields =>
            new TransitionChoice(fields.surrogate.Executor, fields.location, fields.surrogate.K, fields.surrogate.ChosenAt, fields.surrogate.Epoch));
    }

    /// <inheritdoc/>
    public TransitionChoiceSurrogate ConvertToSurrogate(in TransitionChoice value) => new()
    {
        Executor = value.Executor,
        Location = value.Location,
        K = value.K,
        ChosenAt = value.ChosenAt,
        Epoch = value.Epoch,
    };
}

/// <summary>
/// A <see cref="Transition"/> as a grain message carries it: tag 1 <c>Bootstrap</c>, 2 <c>Replace</c>, 3 <c>Split</c>,
/// or 4 <c>Merge</c>, the ledgers it closes, and its choice.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct TransitionSurrogate
{
    /// <summary>The tag of <see cref="Transition.Bootstrap"/>.</summary>
    public const byte BootstrapTag = 1;

    /// <summary>The tag of <see cref="Transition.Replace"/>.</summary>
    public const byte ReplaceTag = 2;

    /// <summary>The tag of <see cref="Transition.Split"/>.</summary>
    public const byte SplitTag = 3;

    /// <summary>The tag of <see cref="Transition.Merge"/>.</summary>
    public const byte MergeTag = 4;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the ledger a replace or split closes, or a merge's first; the default for a bootstrap.</summary>
    [Id(1)]
    public LedgerId Ledger { get; init; }

    /// <summary>Gets or initializes a merge's second ledger; the default for the other cases.</summary>
    [Id(2)]
    public LedgerId Second { get; init; }

    /// <summary>Gets or initializes the transition's choice, which every case carries.</summary>
    [Id(3)]
    public TransitionChoice? Choice { get; init; }

    /// <summary>Gets or initializes a replace's predecessor release hint; zero (<c>TakenOver</c>) for other cases.</summary>
    [Id(4)]
    public byte PredecessorHint { get; init; }
}

/// <summary>Converts a <see cref="Transition"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class TransitionConverter : IConverter<Transition, TransitionSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, a replace names an unknown predecessor hint, or a case lacks its choice or the ledgers it closes.</exception>
    public Transition ConvertFromSurrogate(in TransitionSurrogate surrogate)
    {
        if (surrogate.Case is < TransitionSurrogate.BootstrapTag or > TransitionSurrogate.MergeTag)
        {
            throw ControlTags.Unknown("transition", surrogate.Case, TransitionSurrogate.MergeTag);
        }

        if (surrogate.Case == TransitionSurrogate.ReplaceTag && !Enum.IsDefined((PredecessorReleaseHint)surrogate.PredecessorHint))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A replace's predecessor hint is a PredecessorReleaseHint, not {surrogate.PredecessorHint}."));
        }

        var fields = (surrogate.Ledger, surrogate.Second, Choice: surrogate.Choice ?? throw new InvalidDataException("A transition carries its choice."), Hint: (PredecessorReleaseHint)surrogate.PredecessorHint);
        return surrogate.Case switch
        {
            TransitionSurrogate.BootstrapTag => new Transition.Bootstrap(fields.Choice),
            TransitionSurrogate.ReplaceTag => SurrogateFields.Build("replace", fields, static fields => new Transition.Replace(fields.Ledger, fields.Choice, fields.Hint)),
            TransitionSurrogate.SplitTag => SurrogateFields.Build("split", fields, static fields => new Transition.Split(fields.Ledger, fields.Choice)),
            _ => SurrogateFields.Build("merge", fields, static fields => new Transition.Merge(fields.Ledger, fields.Second, fields.Choice)),
        };
    }

    /// <inheritdoc/>
    public TransitionSurrogate ConvertToSurrogate(in Transition value) => value switch
    {
        Transition.Bootstrap bootstrap => new() { Case = TransitionSurrogate.BootstrapTag, Choice = bootstrap.Choice },
        Transition.Replace replace => new() { Case = TransitionSurrogate.ReplaceTag, Ledger = replace.Ledger, Choice = replace.Choice, PredecessorHint = (byte)replace.PredecessorHint },
        Transition.Split split => new() { Case = TransitionSurrogate.SplitTag, Ledger = split.Ledger, Choice = split.Choice },
        Transition.Merge merge => new() { Case = TransitionSurrogate.MergeTag, Ledger = merge.First, Second = merge.Second, Choice = merge.Choice },
        null => throw new UnreachableException(),
    };
}

/// <summary>
/// A <see cref="LedgerMapCommitOutcome"/>, or a <see cref="TransitionReply"/>, as a grain reply carries it: tag 1
/// <c>Applied</c> or 2 <c>Superseded</c> with the deciding value's epoch, 3 <c>Refused</c>, 4 <c>Rejected</c> with its
/// reason, and, for a transition's reply only, 5 <c>Declined</c>.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerMapVerdictSurrogate
{
    /// <summary>The tag of <c>Applied</c>.</summary>
    public const byte AppliedTag = 1;

    /// <summary>The tag of <c>Superseded</c>.</summary>
    public const byte SupersededTag = 2;

    /// <summary>The tag of <c>Refused</c>.</summary>
    public const byte RefusedTag = 3;

    /// <summary>The tag of <c>Rejected</c>.</summary>
    public const byte RejectedTag = 4;

    /// <summary>The tag of <see cref="TransitionReply.Declined"/>.</summary>
    public const byte DeclinedTag = 5;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>
    /// Gets or initializes the deciding value's epoch for <c>Applied</c> and <c>Superseded</c>, which both carry, 0 among
    /// them for an absent register; absent otherwise.
    /// </summary>
    [Id(1)]
    public ulong? Epoch { get; init; }

    /// <summary>Gets or initializes a rejection's reason; 0 otherwise.</summary>
    [Id(2)]
    public int Reason { get; init; }

    /// <summary>Returns the commit outcome the surrogate names.</summary>
    /// <exception cref="InvalidDataException">The tag names no commit outcome, a verdict lacks its epoch, or a rejection names no reason.</exception>
    public LedgerMapCommitOutcome ToOutcome() => Case switch
    {
        AppliedTag => new LedgerMapCommitOutcome.Applied(Epoch ?? throw new InvalidDataException("An applied verdict carries its epoch.")),
        SupersededTag => new LedgerMapCommitOutcome.Superseded(Epoch ?? throw new InvalidDataException("A superseded verdict carries its epoch.")),
        RefusedTag => LedgerMapCommitOutcome.Refused.Instance,
        RejectedTag when Enum.IsDefined((LedgerMapRejection)Reason) => new LedgerMapCommitOutcome.Rejected((LedgerMapRejection)Reason),
        RejectedTag => throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A rejection's reason is a LedgerMapRejection, not {Reason}.")),
        var tag => throw ControlTags.Unknown("ledger map commit outcome", tag, RejectedTag),
    };

    /// <summary>Returns the surrogate of <paramref name="outcome"/>.</summary>
    public static LedgerMapVerdictSurrogate Of(LedgerMapCommitOutcome outcome) => outcome switch
    {
        LedgerMapCommitOutcome.Applied applied => new() { Case = AppliedTag, Epoch = applied.Epoch },
        LedgerMapCommitOutcome.Superseded superseded => new() { Case = SupersededTag, Epoch = superseded.Epoch },
        LedgerMapCommitOutcome.Refused => new() { Case = RefusedTag },
        LedgerMapCommitOutcome.Rejected rejected => new() { Case = RejectedTag, Reason = (int)rejected.Reason },
        null => throw new UnreachableException(),
    };
}

/// <summary>Converts a <see cref="LedgerMapCommitOutcome"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class LedgerMapCommitOutcomeConverter : IConverter<LedgerMapCommitOutcome, LedgerMapVerdictSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no commit outcome, or a rejection names no reason.</exception>
    public LedgerMapCommitOutcome ConvertFromSurrogate(in LedgerMapVerdictSurrogate surrogate) => surrogate.ToOutcome();

    /// <inheritdoc/>
    public LedgerMapVerdictSurrogate ConvertToSurrogate(in LedgerMapCommitOutcome value) => LedgerMapVerdictSurrogate.Of(value);
}

/// <summary>Converts a <see cref="TransitionReply"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class TransitionReplyConverter : IConverter<TransitionReply, LedgerMapVerdictSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, or a rejection names no reason.</exception>
    public TransitionReply ConvertFromSurrogate(in LedgerMapVerdictSurrogate surrogate) => surrogate.Case switch
    {
        LedgerMapVerdictSurrogate.DeclinedTag => TransitionReply.Declined.Instance,
        >= LedgerMapVerdictSurrogate.AppliedTag and <= LedgerMapVerdictSurrogate.RejectedTag => TransitionReply.Of(surrogate.ToOutcome()),
        var tag => throw ControlTags.Unknown("transition reply", tag, LedgerMapVerdictSurrogate.DeclinedTag),
    };

    /// <inheritdoc/>
    public LedgerMapVerdictSurrogate ConvertToSurrogate(in TransitionReply value) => value switch
    {
        TransitionReply.Declined => new() { Case = LedgerMapVerdictSurrogate.DeclinedTag },
        LedgerMapCommitOutcome.Applied applied => LedgerMapVerdictSurrogate.Of(applied),
        LedgerMapCommitOutcome.Superseded superseded => LedgerMapVerdictSurrogate.Of(superseded),
        LedgerMapCommitOutcome.Refused refused => LedgerMapVerdictSurrogate.Of(refused),
        LedgerMapCommitOutcome.Rejected rejected => LedgerMapVerdictSurrogate.Of(rejected),
        null => throw new UnreachableException(),
    };
}

/// <summary>A ledger an install closes, as a grain message carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerClosingSurrogate
{
    /// <summary>Gets or initializes the ledger.</summary>
    [Id(0)]
    public LedgerId Id { get; init; }

    /// <summary>Gets or initializes its end, <c>T</c>.</summary>
    [Id(1)]
    public Slot T { get; init; }
}

/// <summary>Converts a closing to its surrogate and back, refusing one which names no ledger.</summary>
[RegisterConverter]
internal sealed class LedgerClosingConverter : IConverter<LedgerClosing, LedgerClosingSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The closing names no ledger.</exception>
    public LedgerClosing ConvertFromSurrogate(in LedgerClosingSurrogate surrogate)
    {
        return SurrogateFields.Build("ledger closing", surrogate, static fields => new LedgerClosing(fields.Id, fields.T));
    }

    /// <inheritdoc/>
    public LedgerClosingSurrogate ConvertToSurrogate(in LedgerClosing value) => new() { Id = value.Id, T = value.T };
}

/// <summary>A ledger an install opens, as a grain message carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerOpeningSurrogate
{
    /// <summary>Gets or initializes the ledger's range; nullable, since the default range is the whole keyspace.</summary>
    [Id(0)]
    public KeyRange? Range { get; init; }

    /// <summary>Gets or initializes its location.</summary>
    [Id(1)]
    public LedgerLocation? Location { get; init; }

    /// <summary>Gets or initializes its sequencer window.</summary>
    [Id(2)]
    public int K { get; init; }

    /// <summary>Gets or initializes its primary claim.</summary>
    [Id(3)]
    public PrimaryClaim Primary { get; init; }
}

/// <summary>Converts an opening to its surrogate and back, refusing fields its constructor refuses.</summary>
[RegisterConverter]
internal sealed class LedgerOpeningConverter : IConverter<LedgerOpening, LedgerOpeningSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The opening's location, window, or claim break its rules.</exception>
    public LedgerOpening ConvertFromSurrogate(in LedgerOpeningSurrogate surrogate)
    {
        var range = surrogate.Range ?? throw new InvalidDataException("A ledger opening names its range.");
        var location = surrogate.Location ?? throw new InvalidDataException("A ledger opening names its location.");
        return SurrogateFields.Build("ledger opening", (surrogate, range, location), static fields =>
            new LedgerOpening(fields.range, fields.location, fields.surrogate.K, fields.surrogate.Primary));
    }

    /// <inheritdoc/>
    public LedgerOpeningSurrogate ConvertToSurrogate(in LedgerOpening value) => new()
    {
        Range = value.Range,
        Location = value.Location,
        K = value.K,
        Primary = value.Primary,
    };
}

/// <summary>
/// A <see cref="LedgerMapChange"/> as a grain message carries it: tag 1 <c>Install</c> with its closings, openings, and
/// <c>ChosenAt</c>, 2 <c>RaiseFloor</c> with its revision, or 3 <c>Drop</c> with its ledger's ID, which only archive logs' drain
/// workers submit, each the authority its own markers make for its ledger's drop; on the LSM the controller grain's
/// <c>LedgerDropper</c>, which reads the manifest, is the one authority, and the controller grain refuses a drop a call
/// carries (§5.6, §7.2).
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct LedgerMapChangeSurrogate
{
    /// <summary>The tag of <see cref="LedgerMapChange.Install"/>.</summary>
    public const byte InstallTag = 1;

    /// <summary>The tag of <see cref="LedgerMapChange.RaiseFloor"/>.</summary>
    public const byte RaiseFloorTag = 2;

    /// <summary>The tag of <see cref="LedgerMapChange.Drop"/>.</summary>
    public const byte DropTag = 3;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes an install's closings.</summary>
    [Id(1)]
    public LedgerClosing[]? Closes { get; init; }

    /// <summary>Gets or initializes an install's openings.</summary>
    [Id(2)]
    public LedgerOpening[]? Opens { get; init; }

    /// <summary>Gets or initializes an install's <c>ChosenAt</c>, a floor raise's revision, or a drop's ledger ID.</summary>
    [Id(3)]
    public ulong Revision { get; init; }
}

/// <summary>Converts a <see cref="LedgerMapChange"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class LedgerMapChangeConverter : IConverter<LedgerMapChange, LedgerMapChangeSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, or the case's fields break its rules.</exception>
    public LedgerMapChange ConvertFromSurrogate(in LedgerMapChangeSurrogate surrogate) => surrogate.Case switch
    {
        LedgerMapChangeSurrogate.InstallTag => SurrogateFields.Build(
            "install",
            (Closes: SurrogateFields.Required(surrogate.Closes, "install's closings"), Opens: SurrogateFields.Required(surrogate.Opens, "install's openings"), surrogate.Revision),
            static fields => new LedgerMapChange.Install(fields.Closes, fields.Opens, fields.Revision)),
        LedgerMapChangeSurrogate.RaiseFloorTag => new LedgerMapChange.RaiseFloor(surrogate.Revision),
        LedgerMapChangeSurrogate.DropTag => SurrogateFields.Build("drop", surrogate.Revision, static ledger => new LedgerMapChange.Drop(new LedgerId(ledger))),
        var tag => throw ControlTags.Unknown("ledger map change", tag, LedgerMapChangeSurrogate.DropTag),
    };

    /// <inheritdoc/>
    public LedgerMapChangeSurrogate ConvertToSurrogate(in LedgerMapChange value) => value switch
    {
        LedgerMapChange.Install install => new() { Case = LedgerMapChangeSurrogate.InstallTag, Closes = [.. install.Closes], Opens = [.. install.Opens], Revision = install.ChosenAt },
        LedgerMapChange.RaiseFloor raise => new() { Case = LedgerMapChangeSurrogate.RaiseFloorTag, Revision = raise.Revision },
        LedgerMapChange.Drop drop => new() { Case = LedgerMapChangeSurrogate.DropTag, Revision = drop.Ledger.Value },
        null => throw new UnreachableException(),
    };
}

/// <summary>An object's name as a grain message carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct ObjectNameSurrogate
{
    /// <summary>Gets or initializes the name.</summary>
    [Id(0)]
    public string? Value { get; init; }
}

/// <summary>Converts an object's name to its surrogate and back, refusing a name the object store refuses.</summary>
[RegisterConverter]
internal sealed class ObjectNameConverter : IConverter<ObjectName, ObjectNameSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The name is absent or invalid.</exception>
    public ObjectName ConvertFromSurrogate(in ObjectNameSurrogate surrogate) =>
        SurrogateFields.Build("object name", surrogate.Value ?? throw new InvalidDataException("An object name carries its name."), static value => new ObjectName(value));

    /// <inheritdoc/>
    public ObjectNameSurrogate ConvertToSurrogate(in ObjectName value) => new() { Value = value.Value };
}

/// <summary>A section's reference as a grain message carries it: its object, bytes, partition, tags, and tail.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct SectionRefSurrogate
{
    /// <summary>Gets or initializes the object holding the section.</summary>
    [Id(0)]
    public ObjectName? Object { get; init; }

    /// <summary>Gets or initializes the section's offset in the object.</summary>
    [Id(1)]
    public long Offset { get; init; }

    /// <summary>Gets or initializes the section's length.</summary>
    [Id(2)]
    public long Length { get; init; }

    /// <summary>Gets or initializes the section's partition; nullable, since the default range is the whole keyspace.</summary>
    [Id(3)]
    public KeyRange? Partition { get; init; }

    /// <summary>Gets or initializes the section's least tag.</summary>
    [Id(4)]
    public Tag MinTag { get; init; }

    /// <summary>Gets or initializes the section's greatest tag.</summary>
    [Id(5)]
    public Tag MaxTag { get; init; }

    /// <summary>Gets or initializes the offset of the object's tail.</summary>
    [Id(6)]
    public long TailOffset { get; init; }

    /// <summary>Gets or initializes the length of the object's tail.</summary>
    [Id(7)]
    public long TailLength { get; init; }
}

/// <summary>Converts a section's reference to its surrogate and back, refusing fields its constructor refuses.</summary>
[RegisterConverter]
internal sealed class SectionRefConverter : IConverter<SectionRef, SectionRefSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The reference names no object, or its bytes, tags, or tail break its rules.</exception>
    public SectionRef ConvertFromSurrogate(in SectionRefSurrogate surrogate)
    {
        var name = surrogate.Object ?? throw new InvalidDataException("A section names its object.");
        var partition = surrogate.Partition ?? throw new InvalidDataException("A section names its partition.");
        if (!ObjectRange.IsValid(surrogate.TailOffset, surrogate.TailLength))
        {
            throw new InvalidDataException("A section's tail is a valid range.");
        }

        return SurrogateFields.Build(
            "section reference",
            (surrogate, name, partition),
            static fields => new SectionRef(
                fields.name,
                fields.surrogate.Offset,
                fields.surrogate.Length,
                fields.partition,
                fields.surrogate.MinTag,
                fields.surrogate.MaxTag,
                new ObjectRange(fields.surrogate.TailOffset, fields.surrogate.TailLength)));
    }

    /// <inheritdoc/>
    public SectionRefSurrogate ConvertToSurrogate(in SectionRef value) => new()
    {
        Object = value.Object,
        Offset = value.Offset,
        Length = value.Length,
        Partition = value.Partition,
        MinTag = value.MinTag,
        MaxTag = value.MaxTag,
        TailOffset = value.Tail.Offset,
        TailLength = value.Tail.Length,
    };
}

/// <summary>
/// A <see cref="ManifestChange"/> as a grain message carries it: tag 1 <c>Drain</c> with its ledger, slots, sections,
/// and completion; or 2 <c>Compaction</c> with its inputs and outputs.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct ManifestChangeSurrogate
{
    /// <summary>The tag of <see cref="ManifestChange.Drain"/>.</summary>
    public const byte DrainTag = 1;

    /// <summary>The tag of <see cref="ManifestChange.Compaction"/>.</summary>
    public const byte CompactionTag = 2;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a drain's ledger.</summary>
    [Id(1)]
    public LedgerId Ledger { get; init; }

    /// <summary>Gets or initializes a drain's watermark it starts from.</summary>
    [Id(2)]
    public Slot From { get; init; }

    /// <summary>Gets or initializes the slot a drain ends below.</summary>
    [Id(3)]
    public Slot Below { get; init; }

    /// <summary>Gets or initializes a drain's sections, or a compaction's inputs.</summary>
    [Id(4)]
    public SectionRef[]? Sections { get; init; }

    /// <summary>Gets or initializes whether a drain completes its ledger.</summary>
    [Id(5)]
    public bool Complete { get; init; }

    /// <summary>Gets or initializes a compaction's outputs.</summary>
    [Id(6)]
    public SectionRef[]? Outputs { get; init; }
}

/// <summary>Converts a <see cref="ManifestChange"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class ManifestChangeConverter : IConverter<ManifestChange, ManifestChangeSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, or the case's fields break its rules.</exception>
    public ManifestChange ConvertFromSurrogate(in ManifestChangeSurrogate surrogate) => surrogate.Case switch
    {
        ManifestChangeSurrogate.DrainTag => SurrogateFields.Build(
            "drain",
            (surrogate, Sections: SurrogateFields.Required(surrogate.Sections, "drain's sections")),
            static fields => new ManifestChange.Drain(fields.surrogate.Ledger, fields.surrogate.From, fields.surrogate.Below, fields.Sections, fields.surrogate.Complete)),
        ManifestChangeSurrogate.CompactionTag => SurrogateFields.Build(
            "compaction",
            (Inputs: SurrogateFields.Required(surrogate.Sections, "compaction's inputs"), Outputs: SurrogateFields.Required(surrogate.Outputs, "compaction's outputs")),
            static fields => new ManifestChange.Compaction(fields.Inputs, fields.Outputs)),
        var tag => throw ControlTags.Unknown("manifest change", tag, ManifestChangeSurrogate.CompactionTag),
    };

    /// <inheritdoc/>
    public ManifestChangeSurrogate ConvertToSurrogate(in ManifestChange value) => value switch
    {
        ManifestChange.Drain drain => new()
        {
            Case = ManifestChangeSurrogate.DrainTag,
            Ledger = drain.Ledger,
            From = drain.From,
            Below = drain.Below,
            Sections = [.. drain.Sections],
            Complete = drain.Complete,
        },
        ManifestChange.Compaction compaction => new() { Case = ManifestChangeSurrogate.CompactionTag, Sections = [.. compaction.Inputs], Outputs = [.. compaction.Outputs] },
        null => throw new UnreachableException(),
    };
}

/// <summary>A <see cref="ManifestCommitOutcome"/> as a grain reply carries it: tag 1 <c>Applied</c> or 2 <c>Superseded</c>, with the deciding value's epoch.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct ManifestCommitOutcomeSurrogate
{
    /// <summary>The tag of <see cref="ManifestCommitOutcome.Applied"/>.</summary>
    public const byte AppliedTag = 1;

    /// <summary>The tag of <see cref="ManifestCommitOutcome.Superseded"/>.</summary>
    public const byte SupersededTag = 2;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the deciding value's epoch, which both cases carry, 0 among them for an absent manifest.</summary>
    [Id(1)]
    public ulong? Epoch { get; init; }
}

/// <summary>Converts a <see cref="ManifestCommitOutcome"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class ManifestCommitOutcomeConverter : IConverter<ManifestCommitOutcome, ManifestCommitOutcomeSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case.</exception>
    public ManifestCommitOutcome ConvertFromSurrogate(in ManifestCommitOutcomeSurrogate surrogate) => surrogate.Case switch
    {
        ManifestCommitOutcomeSurrogate.AppliedTag => new ManifestCommitOutcome.Applied(surrogate.Epoch ?? throw new InvalidDataException("An applied verdict carries its epoch.")),
        ManifestCommitOutcomeSurrogate.SupersededTag => new ManifestCommitOutcome.Superseded(surrogate.Epoch ?? throw new InvalidDataException("A superseded verdict carries its epoch.")),
        var tag => throw ControlTags.Unknown("manifest commit outcome", tag, ManifestCommitOutcomeSurrogate.SupersededTag),
    };

    /// <inheritdoc/>
    public ManifestCommitOutcomeSurrogate ConvertToSurrogate(in ManifestCommitOutcome value) => value switch
    {
        ManifestCommitOutcome.Applied applied => new() { Case = ManifestCommitOutcomeSurrogate.AppliedTag, Epoch = applied.Epoch },
        ManifestCommitOutcome.Superseded superseded => new() { Case = ManifestCommitOutcomeSurrogate.SupersededTag, Epoch = superseded.Epoch },
        null => throw new UnreachableException(),
    };
}

/// <summary>A <see cref="NodeReport"/> as a grain message carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct NodeReportSurrogate
{
    /// <summary>Gets or initializes the reporting node.</summary>
    [Id(0)]
    public NodeId Node { get; init; }

    /// <summary>Gets or initializes the open ledgers whose claims name the node under a lifetime its process does not hold.</summary>
    [Id(1)]
    public LedgerId[]? Unserved { get; init; }

    /// <summary>Gets or initializes the bytes per second each ledger the node is primary of appended.</summary>
    [Id(2)]
    public Dictionary<LedgerId, long>? ProposedBytesPerSecond { get; init; }

    /// <summary>Gets or initializes whether the node is under disk pressure.</summary>
    [Id(3)]
    public bool DiskPressure { get; init; }

    /// <summary>Gets or initializes when each closed ledger the node drains last advanced.</summary>
    [Id(4)]
    public Dictionary<LedgerId, DateTimeOffset>? ClosedDrainProgress { get; init; }

    /// <summary>Gets or initializes the generation of the reporting node's silo.</summary>
    [Id(5)]
    public int Generation { get; init; }

    /// <summary>Gets or initializes the ledgers whose replica on the node its store found damaged, which the node lost.</summary>
    [Id(6)]
    public LedgerId[]? LostReplicas { get; init; }

    /// <summary>Gets or initializes, on archive logs, the keys of the closed segments the node has yet to drain.</summary>
    [Id(7)]
    public long UndrainedKeys { get; init; }

    /// <summary>Gets or initializes, on archive logs, the rate the node's drains run at, if it has a share.</summary>
    [Id(8)]
    public double? ArchiveLogRequestsPerSecond { get; init; }

    /// <summary>Gets or initializes the report's number in its process, from 1.</summary>
    [Id(9)]
    public long Sequence { get; init; }

    /// <summary>Gets or initializes whether the node hands its ledgers off.</summary>
    [Id(10)]
    public bool HandingOff { get; init; }

    /// <summary>Gets or initializes whether the node's silo passed its archive gate.</summary>
    [Id(11)]
    public bool ArchiveGatePassed { get; init; }

    /// <summary>Gets or initializes whether the node drains: it serves and its silo's archive gate passed.</summary>
    [Id(12)]
    public bool Draining { get; init; }

    /// <summary>Gets or initializes the node's demand on the shared object-store request budget.</summary>
    [Id(13)]
    public long ObjectStoreDemand { get; init; }

    /// <summary>Gets or initializes the rate the node's object-store budget runs at, if it has a share.</summary>
    [Id(14)]
    public double? ObjectStoreRequestsPerSecond { get; init; }

    /// <summary>Gets or initializes whether the node's silo has object-store ledgers configured and its gate passed.</summary>
    [Id(15)]
    public bool ObjectLedgersReady { get; init; }

    /// <summary>Gets or initializes open ledgers the node cooperatively drained via <c>PrepareRelease</c>, mapped to each ledger's contiguous tail.</summary>
    [Id(16)]
    public Dictionary<LedgerId, Slot>? ReleasedLedgers { get; init; }

    /// <summary>Gets or initializes whether the node's initial apply-loop catch-up is still pending (inverted so default <see langword="false"/> means ready).</summary>
    [Id(17)]
    public bool CatchUpPending { get; init; }
}

/// <summary>Converts a <see cref="NodeReport"/> to its surrogate and back, refusing a report which names no node or silo generation.</summary>
[RegisterConverter]
internal sealed class NodeReportConverter : IConverter<NodeReport, NodeReportSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">
    /// The report names no node, no positive generation, or the default ledger, is not numbered from 1, counts negative
    /// undrained keys or object-store demand, runs its budgets at a rate which is not positive and finite, or lacks a field.
    /// </exception>
    public NodeReport ConvertFromSurrogate(in NodeReportSurrogate surrogate)
    {
        var unserved = SurrogateFields.Required(surrogate.Unserved, "report's unserved ledgers");
        var load = (surrogate.ProposedBytesPerSecond ?? throw new InvalidDataException("A node report carries its loads.")).ToImmutableDictionary();
        var progress = (surrogate.ClosedDrainProgress ?? throw new InvalidDataException("A node report carries its drains' progress.")).ToImmutableDictionary();
        var lost = SurrogateFields.Required(surrogate.LostReplicas, "report's lost replicas");
        var released = surrogate.ReleasedLedgers is null ? [] : surrogate.ReleasedLedgers.ToImmutableDictionary();
        if (surrogate.Node == default)
        {
            throw new InvalidDataException("A node report names its node.");
        }

        if (surrogate.Generation <= 0)
        {
            throw new InvalidDataException("A node report names its silo's positive generation.");
        }

        if (unserved.Contains(default) || load.ContainsKey(default) || progress.ContainsKey(default) || lost.Contains(default) || released.ContainsKey(default))
        {
            throw new InvalidDataException("A node report names its ledgers by ID.");
        }

        if (surrogate.Sequence <= 0)
        {
            throw new InvalidDataException("A node report is numbered in its process from 1.");
        }

        if (surrogate.UndrainedKeys < 0)
        {
            throw new InvalidDataException("A node report counts its undrained keys from zero.");
        }

        if (surrogate.ObjectStoreDemand < 0)
        {
            throw new InvalidDataException("A node report counts its object-store demand from zero.");
        }

        CheckRate(surrogate.ArchiveLogRequestsPerSecond, "A node report's archive-log rate");
        CheckRate(surrogate.ObjectStoreRequestsPerSecond, "A node report's object-store rate");
        return new NodeReport(surrogate.Node, surrogate.Generation, surrogate.Sequence, unserved, load, surrogate.DiskPressure, progress, lost, surrogate.UndrainedKeys, surrogate.ArchiveLogRequestsPerSecond, surrogate.HandingOff, surrogate.ArchiveGatePassed, surrogate.Draining, surrogate.ObjectStoreDemand, surrogate.ObjectStoreRequestsPerSecond, surrogate.ObjectLedgersReady, released, !surrogate.CatchUpPending);
    }

    /// <inheritdoc/>
    public NodeReportSurrogate ConvertToSurrogate(in NodeReport value) => new()
    {
        Node = value.Node,
        Generation = value.Generation,
        Sequence = value.Sequence,
        Unserved = [.. value.Unserved],
        ProposedBytesPerSecond = value.ProposedBytesPerSecond.ToDictionary(),
        DiskPressure = value.DiskPressure,
        ClosedDrainProgress = value.ClosedDrainProgress.ToDictionary(),
        LostReplicas = [.. value.LostReplicas],
        UndrainedKeys = value.UndrainedKeys,
        ArchiveLogRequestsPerSecond = value.ArchiveLogRequestsPerSecond,
        HandingOff = value.HandingOff,
        ArchiveGatePassed = value.ArchiveGatePassed,
        Draining = value.Draining,
        ObjectStoreDemand = value.ObjectStoreDemand,
        ObjectStoreRequestsPerSecond = value.ObjectStoreRequestsPerSecond,
        ObjectLedgersReady = value.ObjectLedgersReady,
        ReleasedLedgers = value.ReleasedLedgersOrEmpty.ToDictionary(),
        CatchUpPending = !value.CatchUpReady,
    };

    /// <summary>Refuses a rate which is present and not one a node's drains can run at (<see cref="ArchiveLogRequestShare.IsRate"/>).</summary>
    /// <exception cref="InvalidDataException">The rate is not positive and finite.</exception>
    internal static void CheckRate(double? rate, string what)
    {
        if (rate is { } value && !ArchiveLogRequestShare.IsRate(value))
        {
            throw new InvalidDataException(what + " is positive and finite.");
        }
    }
}

/// <summary>A <see cref="NodeReportReply"/> as a grain reply carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct NodeReportReplySurrogate
{
    /// <summary>Gets or initializes the newest map epoch the controller knows durable.</summary>
    [Id(0)]
    public ulong MapEpoch { get; init; }

    /// <summary>Gets or initializes the closed ledgers the node is to drain too.</summary>
    [Id(1)]
    public LedgerId[]? DrainToo { get; init; }

    /// <summary>Gets or initializes, on archive logs, the node's share of the cluster's archive-log request budget.</summary>
    [Id(2)]
    public double? ArchiveLogShare { get; init; }

    /// <summary>Gets or initializes, when object-store ledgers are configured, the node's share of the object-store request budget.</summary>
    [Id(3)]
    public double? ObjectStoreShare { get; init; }
}

/// <summary>Converts a <see cref="NodeReportReply"/> to its surrogate and back.</summary>
[RegisterConverter]
internal sealed class NodeReportReplyConverter : IConverter<NodeReportReply, NodeReportReplySurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">
    /// The reply names the default ledger, lacks its ledgers to drain, or assigns a share which is not positive and finite.
    /// </exception>
    public NodeReportReply ConvertFromSurrogate(in NodeReportReplySurrogate surrogate)
    {
        var drainToo = SurrogateFields.Required(surrogate.DrainToo, "report reply's ledgers to drain");
        if (drainToo.Contains(default))
        {
            throw new InvalidDataException("A report reply names ledgers by ID.");
        }

        NodeReportConverter.CheckRate(surrogate.ArchiveLogShare, "A report reply's share of the archive-log request budget");
        NodeReportConverter.CheckRate(surrogate.ObjectStoreShare, "A report reply's share of the object-store request budget");
        return new NodeReportReply(surrogate.MapEpoch, drainToo, surrogate.ArchiveLogShare, surrogate.ObjectStoreShare);
    }

    /// <inheritdoc/>
    public NodeReportReplySurrogate ConvertToSurrogate(in NodeReportReply value) =>
        new() { MapEpoch = value.MapEpoch, DrainToo = [.. value.DrainToo], ArchiveLogShare = value.ArchiveLogShare, ObjectStoreShare = value.ObjectStoreShare };
}

/// <summary>A <see cref="CompactionAssignment"/> as a grain message carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct CompactionAssignmentSurrogate
{
    /// <summary>Gets or initializes the partition, which every assignment carries; the root range is a partition too.</summary>
    [Id(0)]
    public KeyRange? Partition { get; init; }

    /// <summary>Gets or initializes the node which compacts it.</summary>
    [Id(1)]
    public NodeId Assignee { get; init; }

    /// <summary>Gets or initializes the epoch of the manifest value which showed the partition overfull.</summary>
    [Id(2)]
    public ulong ManifestEpoch { get; init; }
}

/// <summary>Converts a <see cref="CompactionAssignment"/> to its surrogate and back, refusing one which names no assignee.</summary>
[RegisterConverter]
internal sealed class CompactionAssignmentConverter : IConverter<CompactionAssignment, CompactionAssignmentSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The assignment names no partition, no assignee, or no manifest epoch.</exception>
    public CompactionAssignment ConvertFromSurrogate(in CompactionAssignmentSurrogate surrogate) =>
        surrogate.Partition is not { } partition || surrogate.Assignee == default
            ? throw new InvalidDataException("A compaction assignment names its partition and its assignee.")
            : SurrogateFields.Build("compaction assignment", (partition, surrogate.Assignee, surrogate.ManifestEpoch), static fields => new CompactionAssignment(fields.partition, fields.Assignee, fields.ManifestEpoch));

    /// <inheritdoc/>
    public CompactionAssignmentSurrogate ConvertToSurrogate(in CompactionAssignment value) =>
        new() { Partition = value.Partition, Assignee = value.Assignee, ManifestEpoch = value.ManifestEpoch };
}

/// <summary>
/// A <see cref="CompactionReply"/> as a grain reply carries it: tag 1 <c>Compacted</c> with the manifest's verdict,
/// 2 <c>NothingToCompact</c>, or 3 <c>Corrupt</c> with its partition and reason.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct CompactionReplySurrogate
{
    /// <summary>The tag of <see cref="CompactionReply.Compacted"/>.</summary>
    public const byte CompactedTag = 1;

    /// <summary>The tag of <see cref="CompactionReply.NothingToCompact"/>.</summary>
    public const byte NothingToCompactTag = 2;

    /// <summary>The tag of <see cref="CompactionReply.Corrupt"/>.</summary>
    public const byte CorruptTag = 3;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a corrupt reply's partition; the root range is a partition too.</summary>
    [Id(1)]
    public KeyRange? Partition { get; init; }

    /// <summary>Gets or initializes the corruption's reason.</summary>
    [Id(2)]
    public string? Reason { get; init; }

    /// <summary>Gets or initializes the manifest's verdict on a compaction's change.</summary>
    [Id(3)]
    public ManifestCommitOutcome? Outcome { get; init; }
}

/// <summary>Converts a <see cref="CompactionReply"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class CompactionReplyConverter : IConverter<CompactionReply, CompactionReplySurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, a compacted reply gives no verdict, or a corrupt reply
    /// gives no reason.</exception>
    public CompactionReply ConvertFromSurrogate(in CompactionReplySurrogate surrogate) => surrogate.Case switch
    {
        CompactionReplySurrogate.CompactedTag when surrogate.Outcome is { } outcome => new CompactionReply.Compacted(outcome),
        CompactionReplySurrogate.CompactedTag => throw new InvalidDataException("A compacted reply gives the manifest's verdict."),
        CompactionReplySurrogate.NothingToCompactTag => CompactionReply.NothingToCompact.Instance,
        CompactionReplySurrogate.CorruptTag when surrogate.Partition is { } partition && surrogate.Reason is { } reason => new CompactionReply.Corrupt(partition, reason),
        CompactionReplySurrogate.CorruptTag => throw new InvalidDataException("A corrupt compaction's reply gives its partition and the corruption's reason."),
        var tag => throw ControlTags.Unknown("compaction reply", tag, CompactionReplySurrogate.CorruptTag),
    };

    /// <inheritdoc/>
    public CompactionReplySurrogate ConvertToSurrogate(in CompactionReply value) => value switch
    {
        CompactionReply.Compacted compacted => new() { Case = CompactionReplySurrogate.CompactedTag, Outcome = compacted.Outcome },
        CompactionReply.NothingToCompact => new() { Case = CompactionReplySurrogate.NothingToCompactTag },
        CompactionReply.Corrupt corrupt => new() { Case = CompactionReplySurrogate.CorruptTag, Partition = corrupt.Partition, Reason = corrupt.Reason },
        null => throw new UnreachableException(),
    };
}

/// <summary>
/// A <see cref="NodeStanding"/> as a grain reply carries it: case tag 1 <c>NotRegistered</c>, 2 <c>Active</c>, or 3
/// <c>Retired</c> with its revision, the map value's epoch and floor, and the ledgers which name the node.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct NodeStandingSurrogate
{
    /// <summary>The tag of <see cref="NodeStanding.NotRegistered"/>.</summary>
    public const byte NotRegisteredTag = 1;

    /// <summary>The tag of <see cref="NodeStanding.Active"/>.</summary>
    public const byte ActiveTag = 2;

    /// <summary>The tag of <see cref="NodeStanding.Retired"/>.</summary>
    public const byte RetiredTag = 3;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the revision of the registry write which retired the node.</summary>
    [Id(1)]
    public ulong RetiredRevision { get; init; }

    /// <summary>Gets or initializes the epoch of the map value the gate was decided from.</summary>
    [Id(2)]
    public ulong Epoch { get; init; }

    /// <summary>Gets or initializes that map value's retirement floor.</summary>
    [Id(3)]
    public ulong Floor { get; init; }

    /// <summary>Gets or initializes the ledgers which still name the node.</summary>
    [Id(4)]
    public LedgerId[]? Ledgers { get; init; }
}

/// <summary>Converts a <see cref="NodeStanding"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class NodeStandingConverter : IConverter<NodeStanding, NodeStandingSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, or a retired standing names no revision, lacks its
    /// ledgers, or names the default ledger.</exception>
    public NodeStanding ConvertFromSurrogate(in NodeStandingSurrogate surrogate) => surrogate.Case switch
    {
        NodeStandingSurrogate.NotRegisteredTag => NodeStanding.NotRegistered.Instance,
        NodeStandingSurrogate.ActiveTag => NodeStanding.Active.Instance,
        NodeStandingSurrogate.RetiredTag => Retired(surrogate),
        var tag => throw ControlTags.Unknown("node standing", tag, NodeStandingSurrogate.RetiredTag),
    };

    /// <inheritdoc/>
    public NodeStandingSurrogate ConvertToSurrogate(in NodeStanding value) => value switch
    {
        NodeStanding.NotRegistered => new() { Case = NodeStandingSurrogate.NotRegisteredTag },
        NodeStanding.Active => new() { Case = NodeStandingSurrogate.ActiveTag },
        NodeStanding.Retired retired => new()
        {
            Case = NodeStandingSurrogate.RetiredTag,
            RetiredRevision = retired.RetiredRevision,
            Epoch = retired.Epoch,
            Floor = retired.Floor,
            Ledgers = [.. retired.Ledgers],
        },
        null => throw new UnreachableException(),
    };

    private static NodeStanding.Retired Retired(in NodeStandingSurrogate surrogate)
    {
        var ledgers = SurrogateFields.Required(surrogate.Ledgers, "retired standing's ledgers");
        return surrogate.RetiredRevision == 0 || ledgers.Contains(default)
            ? throw new InvalidDataException("A retired standing names the revision which retired the node, and ledgers by ID.")
            : new NodeStanding.Retired(surrogate.RetiredRevision, surrogate.Epoch, surrogate.Floor, ledgers);
    }
}

/// <summary>An <see cref="OrdinalNode"/> as a grain reply carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct OrdinalNodeSurrogate
{
    /// <summary>Gets or initializes the node.</summary>
    [Id(0)]
    public NodeId Node { get; init; }

    /// <summary>Gets or initializes its ordinal.</summary>
    [Id(1)]
    public int Ordinal { get; init; }

    /// <summary>Gets or initializes its standing.</summary>
    [Id(2)]
    public NodeStanding Standing { get; init; }
}

/// <summary>Converts an <see cref="OrdinalNode"/> to its surrogate and back.</summary>
[RegisterConverter]
internal sealed class OrdinalNodeConverter : IConverter<OrdinalNode, OrdinalNodeSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The answer names no node, a negative ordinal, or no standing.</exception>
    public OrdinalNode ConvertFromSurrogate(in OrdinalNodeSurrogate surrogate) =>
        surrogate.Node == default || surrogate.Ordinal < 0 || surrogate.Standing.Value is null
            ? throw new InvalidDataException("A node at an ordinal names the node, its non-negative ordinal, and its standing.")
            : new OrdinalNode(surrogate.Node, surrogate.Ordinal, surrogate.Standing);

    /// <inheritdoc/>
    public OrdinalNodeSurrogate ConvertToSurrogate(in OrdinalNode value) =>
        new() { Node = value.Node, Ordinal = value.Ordinal, Standing = value.Standing };
}

/// <summary>
/// A <see cref="DrainAllStartOutcome"/> as a grain reply carries it: case tag 1 <c>Started</c>, 2
/// <c>ObjectLedgersNotConfigured</c>, or 3 <c>ObjectLedgersNotReady</c>.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct DrainAllStartOutcomeSurrogate
{
    /// <summary>The tag of <see cref="DrainAllStartOutcome.Started"/>.</summary>
    public const byte StartedTag = 1;

    /// <summary>The tag of <see cref="DrainAllStartOutcome.ObjectLedgersNotConfigured"/>.</summary>
    public const byte ObjectLedgersNotConfiguredTag = 2;

    /// <summary>The tag of <see cref="DrainAllStartOutcome.ObjectLedgersNotReady"/>.</summary>
    public const byte ObjectLedgersNotReadyTag = 3;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }
}

/// <summary>Converts a <see cref="DrainAllStartOutcome"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class DrainAllStartOutcomeConverter : IConverter<DrainAllStartOutcome, DrainAllStartOutcomeSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case.</exception>
    public DrainAllStartOutcome ConvertFromSurrogate(in DrainAllStartOutcomeSurrogate surrogate) => surrogate.Case switch
    {
        DrainAllStartOutcomeSurrogate.StartedTag => DrainAllStartOutcome.Started.Instance,
        DrainAllStartOutcomeSurrogate.ObjectLedgersNotConfiguredTag => DrainAllStartOutcome.ObjectLedgersNotConfigured.Instance,
        DrainAllStartOutcomeSurrogate.ObjectLedgersNotReadyTag => DrainAllStartOutcome.ObjectLedgersNotReady.Instance,
        var tag => throw ControlTags.Unknown("drain-all start outcome", tag, DrainAllStartOutcomeSurrogate.ObjectLedgersNotReadyTag),
    };

    /// <inheritdoc/>
    public DrainAllStartOutcomeSurrogate ConvertToSurrogate(in DrainAllStartOutcome value) => value switch
    {
        DrainAllStartOutcome.Started => new() { Case = DrainAllStartOutcomeSurrogate.StartedTag },
        DrainAllStartOutcome.ObjectLedgersNotConfigured => new() { Case = DrainAllStartOutcomeSurrogate.ObjectLedgersNotConfiguredTag },
        DrainAllStartOutcome.ObjectLedgersNotReady => new() { Case = DrainAllStartOutcomeSurrogate.ObjectLedgersNotReadyTag },
        null => throw new UnreachableException(),
    };
}

/// <summary>A <see cref="DrainAllStatus"/> as a grain reply carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct DrainAllStatusSurrogate
{
    /// <summary>Gets or initializes whether drain-all is active in the durable node registry (<c>CLNR.DrainAllRevision</c> &gt; 0).</summary>
    [Id(0)]
    public bool Active { get; init; }

    /// <summary>Gets or initializes the epoch of the ledger map value the status was decided from.</summary>
    [Id(1)]
    public ulong Epoch { get; init; }

    /// <summary>Gets or initializes that ledger map value's retirement floor.</summary>
    [Id(2)]
    public ulong Floor { get; init; }

    /// <summary>Gets or initializes the revision of the node registry value the status was decided from.</summary>
    [Id(3)]
    public ulong RegistryRevision { get; init; }

    /// <summary>Gets or initializes the greatest retirement revision among retired diskful nodes, or 0.</summary>
    [Id(4)]
    public ulong LatestDiskfulRetiredRevision { get; init; }

    /// <summary>Gets or initializes the node-backed ledgers still in the ledger map.</summary>
    [Id(5)]
    public LedgerId[]? NodeLedgers { get; init; }

    /// <summary>Gets or initializes the active diskful nodes still in the node registry.</summary>
    [Id(6)]
    public NodeId[]? ActiveDiskfulNodes { get; init; }
}

/// <summary>Converts a <see cref="DrainAllStatus"/> to its surrogate and back.</summary>
[RegisterConverter]
internal sealed class DrainAllStatusConverter : IConverter<DrainAllStatus, DrainAllStatusSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">
    /// The status lacks its node ledgers or active diskful nodes, names a default or duplicate ledger or node ID, names a
    /// retirement revision past the registry revision, is active or lists active diskful nodes at registry revision zero,
    /// or names a positive floor or node ledgers at map epoch zero.
    /// </exception>
    public DrainAllStatus ConvertFromSurrogate(in DrainAllStatusSurrogate surrogate)
    {
        var ledgers = SurrogateFields.Required(surrogate.NodeLedgers, "drain-all status's node ledgers");
        var nodes = SurrogateFields.Required(surrogate.ActiveDiskfulNodes, "drain-all status's active diskful nodes");
        if (ledgers.Contains(default))
        {
            throw new InvalidDataException("A drain-all status names ledgers by ID.");
        }

        if (nodes.Contains(default))
        {
            throw new InvalidDataException("A drain-all status names nodes by ID.");
        }

        if (HasDuplicates(ledgers))
        {
            throw new InvalidDataException("A drain-all status names each node ledger at most once.");
        }

        if (HasDuplicates(nodes))
        {
            throw new InvalidDataException("A drain-all status names each active diskful node at most once.");
        }

        if (surrogate.LatestDiskfulRetiredRevision > surrogate.RegistryRevision)
        {
            throw new InvalidDataException("A drain-all status's latest diskful retirement revision cannot exceed its registry revision.");
        }

        if (surrogate.RegistryRevision == 0 && (surrogate.Active || nodes.Length > 0))
        {
            throw new InvalidDataException("A drain-all status at registry revision zero cannot be active or list active diskful nodes.");
        }

        if (surrogate.Epoch == 0 && (surrogate.Floor > 0 || ledgers.Length > 0))
        {
            throw new InvalidDataException("A drain-all status at map epoch zero cannot have a positive floor or list node ledgers.");
        }

        return new DrainAllStatus(
            surrogate.Active,
            surrogate.Epoch,
            surrogate.Floor,
            surrogate.RegistryRevision,
            surrogate.LatestDiskfulRetiredRevision,
            ledgers,
            nodes);
    }

    private static bool HasDuplicates<T>(ImmutableArray<T> values)
        where T : IEquatable<T>
    {
        if (values.Length <= 1)
        {
            return false;
        }

        var seen = new HashSet<T>(values.Length);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public DrainAllStatusSurrogate ConvertToSurrogate(in DrainAllStatus value) => new()
    {
        Active = value.Active,
        Epoch = value.Epoch,
        Floor = value.Floor,
        RegistryRevision = value.RegistryRevision,
        LatestDiskfulRetiredRevision = value.LatestDiskfulRetiredRevision,
        NodeLedgers = ImmutableCollectionsMarshal.AsArray(value.NodeLedgers) ?? [],
        ActiveDiskfulNodes = ImmutableCollectionsMarshal.AsArray(value.ActiveDiskfulNodes) ?? [],
    };
}

/// <summary>A <see cref="PrepareReleaseRequest"/> as a grain message carries it.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct PrepareReleaseRequestSurrogate
{
    /// <summary>Gets or initializes the open ledger to drain and stop admitting writes on.</summary>
    [Id(0)]
    public LedgerId Ledger { get; init; }

    /// <summary>Gets or initializes the ledger's key range.</summary>
    [Id(1)]
    public KeyRange? Range { get; init; }

    /// <summary>Gets or initializes the cooperative drain deadline.</summary>
    [Id(2)]
    public TimeSpan Deadline { get; init; }
}

/// <summary>Converts a <see cref="PrepareReleaseRequest"/> to its surrogate and back, refusing invalid fields.</summary>
[RegisterConverter]
internal sealed class PrepareReleaseRequestConverter : IConverter<PrepareReleaseRequest, PrepareReleaseRequestSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The request lacks its range, names no ledger, or specifies a non-positive deadline.</exception>
    public PrepareReleaseRequest ConvertFromSurrogate(in PrepareReleaseRequestSurrogate surrogate)
    {
        var range = surrogate.Range ?? throw new InvalidDataException("A prepare-release request names its ledger's key range.");
        return SurrogateFields.Build("prepare-release request", (surrogate.Ledger, range, surrogate.Deadline), static fields =>
            new PrepareReleaseRequest(fields.Ledger, fields.range, fields.Deadline));
    }

    /// <inheritdoc/>
    public PrepareReleaseRequestSurrogate ConvertToSurrogate(in PrepareReleaseRequest value) => new()
    {
        Ledger = value.Ledger,
        Range = value.Range,
        Deadline = value.Deadline,
    };
}

/// <summary>
/// A <see cref="PrepareReleaseReply"/> as a grain reply carries it: tag 1 <c>Released</c> with its contiguous tail,
/// 2 <c>Sealed</c>, 3 <c>TimedOut</c>, 4 <c>NotServed</c>, or 5 <c>Declined</c>.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct PrepareReleaseReplySurrogate
{
    /// <summary>The tag of <see cref="PrepareReleaseReply.Released"/>.</summary>
    public const byte ReleasedTag = 1;

    /// <summary>The tag of <see cref="PrepareReleaseReply.Sealed"/>.</summary>
    public const byte SealedTag = 2;

    /// <summary>The tag of <see cref="PrepareReleaseReply.TimedOut"/>.</summary>
    public const byte TimedOutTag = 3;

    /// <summary>The tag of <see cref="PrepareReleaseReply.NotServed"/>.</summary>
    public const byte NotServedTag = 4;

    /// <summary>The tag of <see cref="PrepareReleaseReply.Declined"/>.</summary>
    public const byte DeclinedTag = 5;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes a released reply's contiguous tail.</summary>
    [Id(1)]
    public Slot? ContiguousTail { get; init; }
}

/// <summary>Converts a <see cref="PrepareReleaseReply"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class PrepareReleaseReplyConverter : IConverter<PrepareReleaseReply, PrepareReleaseReplySurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The tag names no case, or a released reply lacks its contiguous tail.</exception>
    public PrepareReleaseReply ConvertFromSurrogate(in PrepareReleaseReplySurrogate surrogate) => surrogate.Case switch
    {
        PrepareReleaseReplySurrogate.ReleasedTag when surrogate.ContiguousTail is { } tail => new PrepareReleaseReply.Released(tail),
        PrepareReleaseReplySurrogate.ReleasedTag => throw new InvalidDataException("A released reply carries the ledger's contiguous tail."),
        PrepareReleaseReplySurrogate.SealedTag => PrepareReleaseReply.Sealed.Instance,
        PrepareReleaseReplySurrogate.TimedOutTag => PrepareReleaseReply.TimedOut.Instance,
        PrepareReleaseReplySurrogate.NotServedTag => PrepareReleaseReply.NotServed.Instance,
        PrepareReleaseReplySurrogate.DeclinedTag => PrepareReleaseReply.Declined.Instance,
        var tag => throw ControlTags.Unknown("prepare-release reply", tag, PrepareReleaseReplySurrogate.DeclinedTag),
    };

    /// <inheritdoc/>
    public PrepareReleaseReplySurrogate ConvertToSurrogate(in PrepareReleaseReply value) => value switch
    {
        PrepareReleaseReply.Released released => new() { Case = PrepareReleaseReplySurrogate.ReleasedTag, ContiguousTail = released.ContiguousTail },
        PrepareReleaseReply.Sealed => new() { Case = PrepareReleaseReplySurrogate.SealedTag },
        PrepareReleaseReply.TimedOut => new() { Case = PrepareReleaseReplySurrogate.TimedOutTag },
        PrepareReleaseReply.NotServed => new() { Case = PrepareReleaseReplySurrogate.NotServedTag },
        PrepareReleaseReply.Declined => new() { Case = PrepareReleaseReplySurrogate.DeclinedTag },
        null => throw new UnreachableException(),
    };
}
