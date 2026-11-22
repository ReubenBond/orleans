using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Identifies one sequencer lifetime, which no other lifetime's ID names (LP-I15).
/// </summary>
public readonly record struct SequencerInstanceId
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SequencerInstanceId"/> struct.
    /// </summary>
    public SequencerInstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A sequencer instance id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the underlying identifier.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Creates an identifier for a new sequencer lifetime.
    /// </summary>
    public static SequencerInstanceId New() => new(Guid.NewGuid());

    /// <summary>
    /// Parses an identifier encoded using the invariant compact GUID format.
    /// </summary>
    public static SequencerInstanceId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SequencerInstanceId(Guid.ParseExact(value, "N"));
    }

    /// <inheritdoc/>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
