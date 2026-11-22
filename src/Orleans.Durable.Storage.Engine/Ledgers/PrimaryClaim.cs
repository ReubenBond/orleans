
namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// An open ledger's primary: the node whose process holds the ledger's sequencer lifetime, and that lifetime
/// (ledgers.md §4). Every claim names a fresh lifetime, so a claim is unique to the change which opened it.
/// </summary>
public readonly record struct PrimaryClaim
{
    /// <summary>Initializes a new instance of the <see cref="PrimaryClaim"/> struct.</summary>
    /// <exception cref="ArgumentException"><paramref name="node"/> or <paramref name="sequencer"/> is the default.</exception>
    public PrimaryClaim(NodeId node, SequencerInstanceId sequencer)
    {
        if (node == default)
        {
            throw new ArgumentException("A primary claim names a node.", nameof(node));
        }

        if (sequencer == default)
        {
            throw new ArgumentException("A primary claim names a sequencer lifetime.", nameof(sequencer));
        }

        Node = node;
        Sequencer = sequencer;
    }

    /// <summary>Gets the node whose process holds the lifetime.</summary>
    public NodeId Node { get; }

    /// <summary>Gets the sequencer lifetime.</summary>
    public SequencerInstanceId Sequencer { get; }
}
