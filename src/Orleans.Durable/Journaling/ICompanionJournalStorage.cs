using System;
using System.Buffers;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Durable.Journaling;

/// <summary>
/// A validated ledger-local companion record written atomically with a journal segment via
/// <see cref="ICompanionJournalStorage"/> (orleans.md §5.15).
/// </summary>
public readonly struct JournalCompanionRecord
{
    /// <summary>The maximum length of a companion stream name in ASCII characters.</summary>
    public const int MaxStreamNameLength = 64;

    /// <summary>The maximum length of a companion record payload in bytes (64 KiB).</summary>
    public const int MaxPayloadLength = 65_536;

    /// <summary>Initializes a new instance of the <see cref="JournalCompanionRecord"/> struct.</summary>
    /// <param name="streamName">A non-empty ASCII stream name of 1 to 64 characters in <c>[a-z0-9._-]</c>.</param>
    /// <param name="payload">The companion payload bytes (at most <see cref="MaxPayloadLength"/> bytes).</param>
    /// <exception cref="ArgumentNullException"><paramref name="streamName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="streamName"/> is empty, exceeds 64 characters, or contains characters outside <c>[a-z0-9._-]</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> exceeds <see cref="MaxPayloadLength"/> bytes.</exception>
    public JournalCompanionRecord(string streamName, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(streamName);
        if (streamName.Length is 0 or > MaxStreamNameLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Companion stream name length must be in [1, {MaxStreamNameLength}], not {streamName.Length}."),
                nameof(streamName));
        }

        for (var i = 0; i < streamName.Length; i++)
        {
            var c = streamName[i];
            var valid = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!valid)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Companion stream name '{streamName}' contains an invalid character at index {i}: only ASCII [a-z0-9._-] is allowed."),
                    nameof(streamName));
            }
        }

        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                string.Create(CultureInfo.InvariantCulture, $"Companion record payload length {payload.Length} exceeds {MaxPayloadLength} bytes."));
        }

        StreamName = streamName;
        Payload = payload;
    }

    /// <summary>Gets the validated ASCII companion stream name.</summary>
    public string StreamName { get; }

    /// <summary>Gets the companion record payload bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Gets a value indicating whether this value is the uninitialized <see langword="default"/> struct.</summary>
    internal bool IsDefault => StreamName is null;
}

/// <summary>
/// Optional extension to <see cref="IJournalStorage"/> for storage providers that persist ledger-local companion
/// records atomically with a journal <c>Append</c> or <c>Replace</c> in one durability step (orleans.md §5.15).
/// </summary>
public interface ICompanionJournalStorage : IJournalStorage
{
    /// <summary>
    /// Appends a non-empty journal segment and its <paramref name="companions"/> atomically under the instance's
    /// established write precondition.
    /// </summary>
    /// <param name="value">The non-empty journal segment bytes.</param>
    /// <param name="companions">The companion records to commit atomically with <paramref name="value"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is empty.</exception>
    /// <exception cref="ArgumentException">Any element of <paramref name="companions"/> is <see langword="default"/> or invalid.</exception>
    ValueTask AppendWithCompanionsAsync(
        ReadOnlySequence<byte> value,
        ReadOnlyMemory<JournalCompanionRecord> companions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the journal with <paramref name="value"/> (which may be empty) and commits <paramref name="companions"/>
    /// atomically under the instance's established write precondition.
    /// </summary>
    /// <param name="value">The replacement journal segment bytes (may be empty).</param>
    /// <param name="companions">The companion records to commit atomically with <paramref name="value"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentException">Any element of <paramref name="companions"/> is <see langword="default"/> or invalid.</exception>
    ValueTask ReplaceWithCompanionsAsync(
        ReadOnlySequence<byte> value,
        ReadOnlyMemory<JournalCompanionRecord> companions,
        CancellationToken cancellationToken);
}

/// <summary>
/// Internal grain-scoped participant hooked into <see cref="JournaledStateManager"/>'s recovery, write-capture,
/// acknowledgement, and reset lifecycle to stage companion writes (such as <c>orleans.wakeup</c>) without adding a
/// journal stream to the state directory.
/// </summary>
internal interface ICompanionWriteParticipant
{
    void OnRecoveryCompleted();

    ValueTask PrepareWriteAsync(CancellationToken cancellationToken);

    void CaptureCompanions(JournaledStateManager stateManager);

    void OnWriteCompleted();

    void OnReset(bool clearPendingWork);
}
