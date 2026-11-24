using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Server;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>A ballot as a voter message carries it, so the consensus kernel names no Orleans type.</summary>
[GenerateSerializer]
[Immutable]
internal readonly struct BallotSurrogate
{
    /// <summary>Gets or initializes the ballot's round.</summary>
    [Id(0)]
    public int Round { get; init; }

    /// <summary>Gets or initializes the ballot's proposer; zero is the shared fast round's.</summary>
    [Id(1)]
    public int Proposer { get; init; }

    /// <summary>Gets or initializes the ballot's 1-based configuration number; zero exclusively for the zero ballot.</summary>
    [Id(2)]
    public long Configuration { get; init; }
}

/// <summary>Converts a ballot to its surrogate and back.</summary>
[RegisterConverter]
internal sealed class BallotConverter : IConverter<Ballot, BallotSurrogate>
{
    /// <inheritdoc/>
    public Ballot ConvertFromSurrogate(in BallotSurrogate surrogate) => new(surrogate.Configuration, surrogate.Round, surrogate.Proposer);

    /// <inheritdoc/>
    public BallotSurrogate ConvertToSurrogate(in Ballot value) => new() { Configuration = value.Configuration, Round = value.Round, Proposer = value.Proposer };
}

/// <summary>
/// Protocol bytes as a voter message carries them: <see langword="null"/> for the uninitialized value, which no
/// initialized value, including an empty one, crosses as.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct ByteStringSurrogate
{
    /// <summary>Gets or initializes the bytes, which the surrogate owns.</summary>
    [Id(0)]
    public byte[]? Bytes { get; init; }
}

/// <summary>Converts protocol bytes to their surrogate and back, each direction copying the bytes once.</summary>
[RegisterConverter]
internal sealed class ByteStringConverter : IConverter<ByteString, ByteStringSurrogate>
{
    /// <inheritdoc/>
    public ByteString ConvertFromSurrogate(in ByteStringSurrogate surrogate) => surrogate.Bytes is { } bytes ? new ByteString(bytes) : default;

    /// <inheritdoc/>
    public ByteStringSurrogate ConvertToSurrogate(in ByteString value) => new() { Bytes = value.IsDefault ? null : value.ToArray() };
}
