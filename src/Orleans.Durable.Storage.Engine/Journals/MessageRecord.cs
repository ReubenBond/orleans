using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// One unversioned message attached to a journal write, targeting a <see cref="MessageChannel"/> and decided
/// atomically in the same ledger batch slot as the journal row (journals.md §2.1, JS-I11).
/// </summary>
public readonly struct MessageRecord
{
    /// <summary>The maximum payload length of a single message record (64 KiB).</summary>
    public const int MaxPayloadLength = 65_536;

    /// <summary>Initializes a new instance of the <see cref="MessageRecord"/> struct.</summary>
    /// <param name="channel">The target message channel.</param>
    /// <param name="payload">The opaque payload bytes, at most <see cref="MaxPayloadLength"/> bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> exceeds <see cref="MaxPayloadLength"/> bytes.</exception>
    public MessageRecord(MessageChannel channel, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                string.Create(CultureInfo.InvariantCulture, $"Message payload is {payload.Length} bytes, exceeding the {MaxPayloadLength}-byte limit."));
        }

        Channel = channel;
        Payload = payload;
    }

    /// <summary>Gets the target message channel.</summary>
    public MessageChannel Channel { get; }

    /// <summary>Gets the opaque payload bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Gets a value indicating whether this record is the uninitialized <see langword="default"/> value.</summary>
    public bool IsDefault => Channel is null;
}
