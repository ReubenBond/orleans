using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// A <see cref="NodeRegistrationOutcome"/> as a grain reply carries it (orleans.md §7.2, conventions.md §15): its case's
/// tag, the registered revision, and an occupied ordinal with its occupant and the registering node's own entry.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct NodeRegistrationOutcomeSurrogate
{
    /// <summary>The tag of <see cref="NodeRegistrationOutcome.Registered"/>.</summary>
    public const byte RegisteredTag = 1;

    /// <summary>The tag of <see cref="NodeRegistrationOutcome.Stale"/>.</summary>
    public const byte StaleTag = 2;

    /// <summary>The tag of <see cref="NodeRegistrationOutcome.WrongArchiveStore"/>.</summary>
    public const byte WrongArchiveStoreTag = 3;

    /// <summary>The tag of <see cref="NodeRegistrationOutcome.Refused"/>.</summary>
    public const byte RefusedTag = 4;

    /// <summary>The tag of <see cref="NodeRegistrationOutcome.OrdinalOccupied"/>.</summary>
    public const byte OrdinalOccupiedTag = 5;

    /// <summary>The tag of <see cref="NodeRegistrationOutcome.WrongObjectLedgerStore"/>.</summary>
    public const byte WrongObjectLedgerStoreTag = 6;

    /// <summary>Gets or initializes the case's tag.</summary>
    [Id(0)]
    public byte Case { get; init; }

    /// <summary>Gets or initializes the revision of a <see cref="NodeRegistrationOutcome.Registered"/> answer; 0 for the other cases.</summary>
    [Id(1)]
    public ulong Revision { get; init; }

    /// <summary>Gets or initializes the ordinal of an <see cref="NodeRegistrationOutcome.OrdinalOccupied"/> answer; 0 for the other cases.</summary>
    [Id(2)]
    public int Ordinal { get; init; }

    /// <summary>Gets or initializes the occupant of an <see cref="NodeRegistrationOutcome.OrdinalOccupied"/> answer; the default identity for the other cases.</summary>
    [Id(3)]
    public NodeId Occupant { get; init; }

    /// <summary>Gets or initializes whether the registering node of an <see cref="NodeRegistrationOutcome.OrdinalOccupied"/> answer has an active entry.</summary>
    [Id(4)]
    public bool HasEntry { get; init; }

    /// <summary>Gets or initializes the ordinal of that entry, when it has one.</summary>
    [Id(5)]
    public int? EntryOrdinal { get; init; }
}

/// <summary>Converts a <see cref="NodeRegistrationOutcome"/> to its surrogate and back, refusing an unknown tag.</summary>
[RegisterConverter]
internal sealed class NodeRegistrationOutcomeConverter : IConverter<NodeRegistrationOutcome, NodeRegistrationOutcomeSurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The surrogate's tag names no case, or an <see cref="NodeRegistrationOutcome.OrdinalOccupied"/> answer names no occupant, a negative ordinal, or an entry ordinal without an entry.</exception>
    public NodeRegistrationOutcome ConvertFromSurrogate(in NodeRegistrationOutcomeSurrogate surrogate) => surrogate.Case switch
    {
        NodeRegistrationOutcomeSurrogate.RegisteredTag => new NodeRegistrationOutcome.Registered(surrogate.Revision),
        NodeRegistrationOutcomeSurrogate.StaleTag => NodeRegistrationOutcome.Stale.Instance,
        NodeRegistrationOutcomeSurrogate.WrongArchiveStoreTag => NodeRegistrationOutcome.WrongArchiveStore.Instance,
        NodeRegistrationOutcomeSurrogate.RefusedTag => NodeRegistrationOutcome.Refused.Instance,
        NodeRegistrationOutcomeSurrogate.OrdinalOccupiedTag when IsOrdinalOccupied(surrogate) =>
            new NodeRegistrationOutcome.OrdinalOccupied(surrogate.Ordinal, surrogate.Occupant, surrogate.HasEntry, surrogate.EntryOrdinal),
        NodeRegistrationOutcomeSurrogate.OrdinalOccupiedTag => throw new InvalidDataException("An occupied ordinal's answer names a non-negative ordinal and its occupant, and an entry ordinal, non-negative, only with an entry."),
        NodeRegistrationOutcomeSurrogate.WrongObjectLedgerStoreTag => NodeRegistrationOutcome.WrongObjectLedgerStore.Instance,
        _ => throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A node registration outcome's case tag is 1 to 6, not {surrogate.Case}.")),
    };

    /// <inheritdoc/>
    public NodeRegistrationOutcomeSurrogate ConvertToSurrogate(in NodeRegistrationOutcome value) => value switch
    {
        NodeRegistrationOutcome.Registered registered => new() { Case = NodeRegistrationOutcomeSurrogate.RegisteredTag, Revision = registered.Revision },
        NodeRegistrationOutcome.Stale => new() { Case = NodeRegistrationOutcomeSurrogate.StaleTag },
        NodeRegistrationOutcome.WrongArchiveStore => new() { Case = NodeRegistrationOutcomeSurrogate.WrongArchiveStoreTag },
        NodeRegistrationOutcome.Refused => new() { Case = NodeRegistrationOutcomeSurrogate.RefusedTag },
        NodeRegistrationOutcome.OrdinalOccupied occupied => new()
        {
            Case = NodeRegistrationOutcomeSurrogate.OrdinalOccupiedTag,
            Ordinal = occupied.Ordinal,
            Occupant = occupied.Occupant,
            HasEntry = occupied.HasEntry,
            EntryOrdinal = occupied.EntryOrdinal,
        },
        NodeRegistrationOutcome.WrongObjectLedgerStore => new() { Case = NodeRegistrationOutcomeSurrogate.WrongObjectLedgerStoreTag },
        null => throw new UnreachableException(),
    };

    private static bool IsOrdinalOccupied(in NodeRegistrationOutcomeSurrogate surrogate) =>
        surrogate.Ordinal >= 0 && surrogate.Occupant != default && (surrogate.EntryOrdinal is not { } entryOrdinal || surrogate.HasEntry && entryOrdinal >= 0);
}
