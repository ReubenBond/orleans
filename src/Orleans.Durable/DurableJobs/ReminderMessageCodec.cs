using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Unicode;
using Orleans.Runtime;

namespace Orleans.Durable.Jobs;

/// <summary>
/// A decoded reminder message record (<see cref="ReminderMessageCodec"/>): schedules a reminder for
/// (<see cref="Target"/>, <see cref="JobName"/>) at <see cref="DueTimeUtcTicks"/>, or clears any pending reminder when
/// <see cref="DueTimeUtcTicks"/> is <see langword="null"/>.
/// </summary>
/// <param name="Target">The target grain identifier.</param>
/// <param name="JobName">The non-blank durable job name.</param>
/// <param name="DueTimeUtcTicks">The UTC ticks when the reminder is due, or <see langword="null"/> to clear.</param>
public readonly record struct ReminderRecord(GrainId Target, string JobName, long? DueTimeUtcTicks);

/// <summary>
/// Canonical binary codec for <c>orleans.reminder</c> message records (wire version 1, little-endian).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout:
/// <c>Version (u8 = 1) | Flags (u8: bit 0 = HasDueTime, bits 1..7 = 0) | TargetGrainIdLen (varint 1..1024) |
/// TargetGrainIdUtf8 | JobNameLen (varint 1..256) | JobNameUtf8 | [DueTimeUtcTicks (i64 LE, 0..DateTimeOffset.MaxValue.UtcTicks)]</c>.
/// </para>
/// </remarks>
public static class ReminderMessageCodec
{
    /// <summary>The canonical message channel name for grain reminders.</summary>
    public const string ChannelName = "orleans.reminder";

    /// <summary>Wire format version 1.</summary>
    public const byte Version = 1;

    /// <summary>Flag bit 0 indicating that an 8-byte little-endian <c>DueTimeUtcTicks</c> field follows.</summary>
    public const byte HasDueTimeFlag = 0x01;

    /// <summary>The maximum encoded UTF-8 byte length of <c>TargetGrainId</c>.</summary>
    public const int MaxGrainIdUtf8Length = 1024;

    /// <summary>The maximum encoded UTF-8 byte length of <c>JobName</c>.</summary>
    public const int MaxJobNameUtf8Length = 256;

    /// <summary>The maximum valid wire length of a version 1 reminder message record.</summary>
    public const int MaxEncodedLength = 1 + 1 + 2 + MaxGrainIdUtf8Length + 2 + MaxJobNameUtf8Length + sizeof(long);

    internal const string ReasonKey = "orleans.durable.storage.decode.reason";
    internal const string ReasonTruncated = "truncated";
    internal const string ReasonBadVersion = "bad-version";
    internal const string ReasonBadFlags = "bad-flags";
    internal const string ReasonBadVarint = "bad-varint";
    internal const string ReasonEmptyGrainId = "empty-grain-id";
    internal const string ReasonGrainIdTooLong = "grain-id-too-long";
    internal const string ReasonInvalidUtf8 = "invalid-utf8";
    internal const string ReasonBadGrainId = "bad-grain-id";
    internal const string ReasonEmptyJobName = "empty-job-name";
    internal const string ReasonJobNameTooLong = "job-name-too-long";
    internal const string ReasonBlankJobName = "blank-job-name";
    internal const string ReasonBadTicks = "bad-ticks";
    internal const string ReasonTrailingBytes = "trailing-bytes";

    private const int MaxVarintBytes = 10;
    private static readonly long MaxDueTimeTicks = DateTimeOffset.MaxValue.UtcTicks;

    /// <summary>Encodes <paramref name="record"/> into a newly allocated canonical byte array.</summary>
    public static byte[] Encode(in ReminderRecord record)
    {
        var length = GetEncodedLength(record, out var grainIdUtf8, out var jobNameUtf8);
        var buffer = new byte[length];
        var written = WriteValidated(buffer, grainIdUtf8, jobNameUtf8, record.DueTimeUtcTicks);
        if (written != length)
        {
            throw new InvalidOperationException("Encoded reminder message length did not match computed length.");
        }

        return buffer;
    }

    /// <summary>Encodes <paramref name="record"/> into <paramref name="destination"/> and returns the bytes written.</summary>
    public static int Encode(in ReminderRecord record, Span<byte> destination)
    {
        var length = GetEncodedLength(record, out var grainIdUtf8, out var jobNameUtf8);
        if (destination.Length < length)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Destination span ({destination.Length} bytes) is shorter than the encoded reminder record ({length} bytes)."),
                nameof(destination));
        }

        return WriteValidated(destination, grainIdUtf8, jobNameUtf8, record.DueTimeUtcTicks);
    }

    /// <summary>
    /// Validates and decodes a reminder message record from a contiguous byte span, checking rules in procedural order.
    /// </summary>
    public static InvalidDataException? Decode(ReadOnlySpan<byte> payload, out ReminderRecord record) =>
        Decode(payload, internJobName: null, out record);

    /// <summary>
    /// Validates and decodes a reminder message record from a contiguous byte span, optionally interning <c>JobName</c>
    /// via <paramref name="internJobName"/>.
    /// </summary>
    internal static InvalidDataException? Decode(
        ReadOnlySpan<byte> payload,
        Func<ReadOnlySpan<byte>, string?>? internJobName,
        out ReminderRecord record)
    {
        record = default;
        if (payload.Length < 1)
        {
            return Reject(ReasonTruncated, "Wakeup message is empty.");
        }

        if (payload[0] != Version)
        {
            return Reject(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"Wakeup message version {payload[0]} is unsupported; expected {Version}."));
        }

        if (payload.Length < 2)
        {
            return Reject(ReasonTruncated, "Wakeup message is 1 byte, shorter than the 2-byte header.");
        }

        var flags = payload[1];
        if ((flags & ~HasDueTimeFlag) != 0)
        {
            return Reject(ReasonBadFlags, string.Create(CultureInfo.InvariantCulture, $"Wakeup message flags 0x{flags:X2} set reserved bits outside 0x01."));
        }

        var hasDueTime = (flags & HasDueTimeFlag) != 0;
        var offset = 2;

        if (ReadVarint(payload, ref offset, "TargetGrainIdLen", out var grainIdLen) is { } grainIdLenRejection)
        {
            return grainIdLenRejection;
        }

        if (grainIdLen == 0)
        {
            return Reject(ReasonEmptyGrainId, "Wakeup message TargetGrainId is empty.");
        }

        if (grainIdLen > MaxGrainIdUtf8Length)
        {
            return Reject(ReasonGrainIdTooLong, string.Create(CultureInfo.InvariantCulture, $"Wakeup message TargetGrainId length {grainIdLen} exceeds {MaxGrainIdUtf8Length}."));
        }

        var grainLen = (int)grainIdLen;
        if (payload.Length - offset < grainLen)
        {
            return Reject(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"Wakeup message ends within TargetGrainId ({grainLen} bytes)."));
        }

        var grainUtf8 = payload.Slice(offset, grainLen);
        offset += grainLen;
        if (!Utf8.IsValid(grainUtf8))
        {
            return Reject(ReasonInvalidUtf8, "Wakeup message TargetGrainId is not valid UTF-8.");
        }

        if (!TryParseCanonicalGrainId(grainUtf8, out var target))
        {
            return Reject(ReasonBadGrainId, "Wakeup message TargetGrainId is not a valid canonical GrainId.");
        }

        if (ReadVarint(payload, ref offset, "JobNameLen", out var jobNameLen) is { } jobNameLenRejection)
        {
            return jobNameLenRejection;
        }

        if (jobNameLen == 0)
        {
            return Reject(ReasonEmptyJobName, "Wakeup message JobName is empty.");
        }

        if (jobNameLen > MaxJobNameUtf8Length)
        {
            return Reject(ReasonJobNameTooLong, string.Create(CultureInfo.InvariantCulture, $"Wakeup message JobName length {jobNameLen} exceeds {MaxJobNameUtf8Length}."));
        }

        var nameLen = (int)jobNameLen;
        if (payload.Length - offset < nameLen)
        {
            return Reject(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"Wakeup message ends within JobName ({nameLen} bytes)."));
        }

        var nameUtf8 = payload.Slice(offset, nameLen);
        offset += nameLen;
        if (!Utf8.IsValid(nameUtf8))
        {
            return Reject(ReasonInvalidUtf8, "Wakeup message JobName is not valid UTF-8.");
        }

        var jobName = internJobName?.Invoke(nameUtf8) ?? Encoding.UTF8.GetString(nameUtf8);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return Reject(ReasonBlankJobName, "Wakeup message JobName is whitespace-only.");
        }

        long? dueTimeTicks = null;
        if (hasDueTime)
        {
            if (payload.Length - offset < sizeof(long))
            {
                return Reject(ReasonTruncated, "Wakeup message ends before its 8-byte DueTimeUtcTicks field.");
            }

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));
            offset += sizeof(long);
            if (ticks < 0 || ticks > MaxDueTimeTicks)
            {
                return Reject(ReasonBadTicks, string.Create(CultureInfo.InvariantCulture, $"Wakeup message DueTimeUtcTicks {ticks} is outside [0, {MaxDueTimeTicks}]."));
            }

            dueTimeTicks = ticks;
        }

        if (offset != payload.Length)
        {
            return Reject(ReasonTrailingBytes, string.Create(CultureInfo.InvariantCulture, $"Wakeup message has {payload.Length - offset} trailing bytes."));
        }

        record = new ReminderRecord(target, jobName, dueTimeTicks);
        return null;
    }

    /// <summary>
    /// Validates and decodes a reminder message record from a potentially multi-segment <see cref="ReadOnlySequence{T}"/>.
    /// </summary>
    public static InvalidDataException? Decode(ReadOnlySequence<byte> payload, out ReminderRecord record) =>
        Decode(payload, internJobName: null, out record);

    internal static InvalidDataException? Decode(
        ReadOnlySequence<byte> payload,
        Func<ReadOnlySpan<byte>, string?>? internJobName,
        out ReminderRecord record)
    {
        if (payload.IsSingleSegment)
        {
            return Decode(payload.FirstSpan, internJobName, out record);
        }

        var len = payload.Length;
        if (len <= MaxEncodedLength)
        {
            Span<byte> copy = stackalloc byte[(int)len];
            payload.CopyTo(copy);
            return Decode(copy, internJobName, out record);
        }

        // Oversized multi-segment payloads still enforce exact procedural error ordering before trailing-bytes!
        Span<byte> prefix = stackalloc byte[MaxEncodedLength + 1];
        payload.Slice(0, prefix.Length).CopyTo(prefix);
        return Decode(prefix, internJobName, out record);
    }

    private static int GetEncodedLength(in ReminderRecord record, out byte[] grainIdUtf8, out byte[] jobNameUtf8)
    {
        if (record.Target.IsDefault)
        {
            throw new ArgumentException("Reminder Target GrainId must not be default.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.JobName))
        {
            throw new ArgumentException("Reminder JobName must not be null or whitespace.", nameof(record));
        }

        if (record.DueTimeUtcTicks is { } ticks && (ticks < 0 || ticks > MaxDueTimeTicks))
        {
            throw new ArgumentOutOfRangeException(
                nameof(record),
                ticks,
                string.Create(CultureInfo.InvariantCulture, $"Reminder DueTimeUtcTicks {ticks} must be within [0, {MaxDueTimeTicks}]."));
        }

        var grainText = record.Target.ToString();
        grainIdUtf8 = Encoding.UTF8.GetBytes(grainText);
        if (grainIdUtf8.Length is 0 or > MaxGrainIdUtf8Length)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Target GrainId UTF-8 length ({grainIdUtf8.Length}) exceeds {MaxGrainIdUtf8Length}."),
                nameof(record));
        }

        jobNameUtf8 = Encoding.UTF8.GetBytes(record.JobName);
        if (jobNameUtf8.Length is 0 or > MaxJobNameUtf8Length)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"JobName UTF-8 length ({jobNameUtf8.Length}) exceeds {MaxJobNameUtf8Length}."),
                nameof(record));
        }

        return 2
            + VarintLength((ulong)grainIdUtf8.Length) + grainIdUtf8.Length
            + VarintLength((ulong)jobNameUtf8.Length) + jobNameUtf8.Length
            + (record.DueTimeUtcTicks.HasValue ? sizeof(long) : 0);
    }

    private static int WriteValidated(Span<byte> destination, ReadOnlySpan<byte> grainIdUtf8, ReadOnlySpan<byte> jobNameUtf8, long? dueTimeUtcTicks)
    {
        var offset = 0;
        destination[offset++] = Version;
        destination[offset++] = dueTimeUtcTicks.HasValue ? HasDueTimeFlag : (byte)0;
        offset += WriteVarint(destination[offset..], (ulong)grainIdUtf8.Length);
        grainIdUtf8.CopyTo(destination[offset..]);
        offset += grainIdUtf8.Length;
        offset += WriteVarint(destination[offset..], (ulong)jobNameUtf8.Length);
        jobNameUtf8.CopyTo(destination[offset..]);
        offset += jobNameUtf8.Length;
        if (dueTimeUtcTicks is { } ticks)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], ticks);
            offset += sizeof(long);
        }

        return offset;
    }

    private static bool TryParseCanonicalGrainId(ReadOnlySpan<byte> utf8, out GrainId grainId)
    {
        grainId = default;
        var slash = utf8.IndexOf((byte)'/');
        if (slash <= 0)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(utf8);
        if (!GrainId.TryParse(text, out var candidate) || candidate.IsDefault)
        {
            return false;
        }

        // Canonical check: GrainId.ToString() must round-trip identically to the input string (rejects malformed/non-canonical encodings).
        if (!string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
        {
            return false;
        }

        grainId = candidate;
        return true;
    }

    private static InvalidDataException? ReadVarint(ReadOnlySpan<byte> payload, ref int offset, string fieldName, out ulong value)
    {
        value = 0;
        var shift = 0;
        for (var i = 0; i < MaxVarintBytes; i++)
        {
            if (offset >= payload.Length)
            {
                return Reject(ReasonTruncated, string.Create(CultureInfo.InvariantCulture, $"Wakeup message ends within {fieldName} varint."));
            }

            var b = payload[offset++];
            if (i == MaxVarintBytes - 1 && b > 1)
            {
                return Reject(ReasonBadVarint, string.Create(CultureInfo.InvariantCulture, $"Wakeup message {fieldName} varint overflows u64."));
            }

            if (i > 0 && b == 0)
            {
                return Reject(ReasonBadVarint, string.Create(CultureInfo.InvariantCulture, $"Wakeup message {fieldName} varint is not in canonical shortest form."));
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return null;
            }

            shift += 7;
        }

        return Reject(ReasonBadVarint, string.Create(CultureInfo.InvariantCulture, $"Wakeup message {fieldName} varint exceeds {MaxVarintBytes} bytes."));
    }

    private static int VarintLength(ulong value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    private static int WriteVarint(Span<byte> destination, ulong value)
    {
        var offset = 0;
        while (value >= 0x80)
        {
            destination[offset++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        destination[offset++] = (byte)value;
        return offset;
    }

    private static InvalidDataException Reject(string reason, string message)
    {
        var exception = new InvalidDataException(message);
        exception.Data[ReasonKey] = reason;
        return exception;
    }
}
