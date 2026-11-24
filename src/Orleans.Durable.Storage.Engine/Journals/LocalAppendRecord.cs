using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// One validated passthrough ledger-local append attached to an <c>Append</c>, <c>Replace</c>, or <c>Delete</c>
/// journal write (journals.md §4.2, §7, JS-I11).
/// </summary>
public readonly struct LocalAppendRecord
{
    /// <summary>Maximum payload length in bytes for a single ledger-local append record (64 KiB).</summary>
    public const int MaxPayloadLength = 65_536;

    /// <summary>Initializes a new instance of the <see cref="LocalAppendRecord"/> struct.</summary>
    /// <param name="stream">The target ledger-local stream name.</param>
    /// <param name="payload">The immutable payload bytes, at most <see cref="MaxPayloadLength"/> bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> exceeds <see cref="MaxPayloadLength"/> bytes.</exception>
    public LocalAppendRecord(LocalStreamName stream, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                string.Create(CultureInfo.InvariantCulture, $"A local append payload is at most {MaxPayloadLength} bytes, not {payload.Length}."));
        }

        Stream = stream;
        Payload = payload;
    }

    /// <summary>Gets the target ledger-local stream name.</summary>
    public LocalStreamName Stream { get; }

    /// <summary>Gets the immutable payload bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }
}
