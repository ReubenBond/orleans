using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Identifies a node: the random, non-empty UUID its LedgerStore creates and keeps for its lifetime (identity.md §2,
/// LS-I6).
/// </summary>
/// <remarks>
/// Equality is by value; nodes have no order. Binary encodings carry the UUID's 16 bytes in big-endian (RFC 4122)
/// order, and its text form is 32 lowercase hexadecimal digits, <c>{node:N}</c>. Only the constructor makes a node
/// identity: reading <see cref="Value"/> of the default instance throws, so every encoding reads a validated identity.
/// </remarks>
public readonly record struct NodeId
{
    private readonly Guid _value;

    /// <summary>Initializes a new instance of the <see cref="NodeId"/> struct.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    public NodeId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A node identity cannot be empty.", nameof(value));
        }

        _value = value;
    }

    /// <summary>Gets the node's UUID.</summary>
    /// <exception cref="InvalidOperationException">The instance is the default, which no constructor made.</exception>
    public Guid Value =>
        _value != Guid.Empty ? _value : throw new InvalidOperationException("The default NodeId is not a node identity.");

    /// <summary>Creates the identity of a new node.</summary>
    public static NodeId New() => new(Guid.NewGuid());

    /// <summary>Returns the text form, <c>{node:N}</c>.</summary>
    public override string ToString() => _value.ToString("N", CultureInfo.InvariantCulture);
}
