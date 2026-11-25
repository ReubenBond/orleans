using System;
using System.Buffers;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Durable.Journaling;

/// <summary>
/// One unversioned message attached to a journal write, targeting a message channel (1..64 ASCII bytes in
/// <c>[a-z0-9._-]</c>) with an opaque payload of at most <see cref="MaxPayloadLength"/> bytes.
/// </summary>
public readonly struct JournalMessageRecord
{
    /// <summary>Gets the maximum allowed ASCII length of <see cref="Channel"/>.</summary>
    public const int MaxChannelLength = 64;

    /// <summary>Gets the maximum allowed byte length of <see cref="Payload"/> (64 KiB).</summary>
    public const int MaxPayloadLength = 65_536;

    /// <summary>Initializes a new instance of the <see cref="JournalMessageRecord"/> struct.</summary>
    /// <param name="channel">The target message channel (1..64 ASCII characters in <c>[a-z0-9._-]</c>).</param>
    /// <param name="payload">The opaque payload bytes, at most <see cref="MaxPayloadLength"/> bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="channel"/> is empty, longer than <see cref="MaxChannelLength"/>, or contains a character outside ASCII <c>[a-z0-9._-]</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> exceeds <see cref="MaxPayloadLength"/> bytes.</exception>
    public JournalMessageRecord(string channel, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ValidateChannel(channel, nameof(channel));
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                string.Create(CultureInfo.InvariantCulture, $"Journal message payload is {payload.Length} bytes, exceeding the {MaxPayloadLength}-byte limit."));
        }

        Channel = channel;
        Payload = payload;
    }

    /// <summary>Gets the target message channel name.</summary>
    public string Channel { get; }

    /// <summary>Gets the opaque payload bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Gets a value indicating whether this record is the uninitialized <see langword="default"/> value.</summary>
    internal bool IsDefault => Channel is null;

    private static void ValidateChannel(string channel, string paramName)
    {
        if (channel.Length is 0 or > MaxChannelLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Journal message channel '{channel}' must be 1 to {MaxChannelLength} characters."),
                paramName);
        }

        for (var i = 0; i < channel.Length; i++)
        {
            var c = channel[i];
            if (c > 0x7F || c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Journal message channel '{channel}' contains invalid character at index {i}; only ASCII [a-z0-9._-] is allowed."),
                    paramName);
            }
        }
    }
}

/// <summary>
/// Extends <see cref="IJournalStorage"/> with atomic journal + message writes that commit one journal update and
/// zero or more <see cref="JournalMessageRecord"/> items in a single quorum write (orleans.md §5.15).
/// </summary>
public interface IMessageJournalStorage : IJournalStorage
{
    /// <summary>
    /// Appends the non-empty journal delta <paramref name="value"/> atomically with <paramref name="messages"/>.
    /// </summary>
    /// <param name="value">The non-empty encoded journal delta bytes.</param>
    /// <param name="messages">The message records to commit atomically with the append.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is empty.</exception>
    /// <exception cref="ArgumentException">A message record is <see langword="default"/>, targets an unregistered channel, or fails channel validation.</exception>
    ValueTask AppendWithMessagesAsync(
        ReadOnlySequence<byte> value,
        ReadOnlyMemory<JournalMessageRecord> messages,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the journal with snapshot <paramref name="value"/> (possibly empty) atomically with <paramref name="messages"/>.
    /// </summary>
    /// <param name="value">The encoded journal snapshot bytes.</param>
    /// <param name="messages">The message records to commit atomically with the replacement.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ArgumentException">A message record is <see langword="default"/>, targets an unregistered channel, or fails channel validation.</exception>
    ValueTask ReplaceWithMessagesAsync(
        ReadOnlySequence<byte> value,
        ReadOnlyMemory<JournalMessageRecord> messages,
        CancellationToken cancellationToken);
}

/// <summary>
/// Internal grain-scoped participant (such as <see cref="Jobs.GrainWakeupCoordinator"/>) notified by
/// <see cref="JournaledStateManager"/> during recovery, write capture, write completion, and reset/failure.
/// </summary>
internal interface IMessageWriteParticipant
{
    /// <summary>Called after journal recovery completes so the participant can seed post-recovery pending state.</summary>
    void OnRecoveryCompleted();

    /// <summary>
    /// Called synchronously after all state machines finish <see cref="IStateMachine.WritePendingEntries"/> or
    /// <see cref="IStateMachine.WriteSnapshot"/> to stage any message records for the current write.
    /// </summary>
    void CaptureMessages(JournaledStateManager stateManager);

    /// <summary>Called after the storage write succeeds and state machines are notified.</summary>
    void OnWriteCompleted();

    /// <summary>Called when a write fails, is fenced/ambiguous, or state is reset/deleted.</summary>
    /// <param name="clearPendingWork">True when journal state was deleted or reset to empty.</param>
    void OnWriteFailedOrReset(bool clearPendingWork);
}
