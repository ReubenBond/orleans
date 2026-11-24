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
/// A decoded ledger-local wakeup companion record (<see cref="WakeupCompanionCodec"/>): schedules a wakeup for
/// (<see cref="Target"/>, <see cref="JobName"/>) at <see cref="DueTimeUtcTicks"/>, or clears any pending wakeup when
/// <see cref="DueTimeUtcTicks"/> is <see langword="null"/>.
/// </summary>
/// <param name="Target">The target grain identifier.</param>
/// <param name="JobName">The non-blank durable job name.</param>
/// <param name="DueTimeUtcTicks">The UTC ticks when the wakeup is due, or <see langword="null"/> to clear.</param>
public readonly record struct WakeupRecord(GrainId Target, string JobName, long? DueTimeUtcTicks);

/// <summary>
/// Canonical binary codec for <c>orleans.wakeup</c> ledger-local companion records (wire version 1, little-endian).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout:
/// <c>Version (u8 = 1) | Flags (u8: bit 0 = HasDueTime, bits 1..7 = 0) | TargetGrainIdLen (varint 1..1024) |
/// TargetGrainIdUtf8 | JobNameLen (varint 1..256) | JobNameUtf8 | [DueTimeUtcTicks (i64 LE, 0..DateTimeOffset.MaxValue.UtcTicks)]</c>.
/// </para>
/// </remarks>
public static class WakeupCompanionCodec
{
    /// <summary>The canonical ledger-local stream name for grain wakeups.</summary>
    public const string StreamName = "orleans.wakeup";

    /// <summary>Wire format version 1.</summary>
    public const byte Version = 1;

    /// <summary>Flag bit 0 indicating that an 8-byte little-endian <c>DueTimeUtcTicks</c> field follows.</summary>
    public const byte HasDueTimeFlag = 0x01;

    /// <summary>The maximum encoded UTF-8 byte length of <c>TargetGrainId</c>.</summary>
    public const int MaxGrainIdUtf8Length = 1024;

    /// <summary>The maximum encoded UTF-8 byte length of <c>JobName</c>.</summary>
    public const int MaxJobNameUtf8Length = 256;

    /// <summary>The maximum valid wire length of a version 1 wakeup companion record.</summary>
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

    /// <summary>Returns the canonical wire byte length of <paramref name="record"/>.</summary>
    /// <exception cref="ArgumentException">Any field of <paramref name="record"/> violates the wire format constraints.</exception>
    public static int EncodedLength(WakeupRecord record)
    {
        ValidateForEncode(record, out var grainIdLen, out var jobNameLen);
        return 2 + VarintLength((ulong)grainIdLen) + grainIdLen + VarintLength((ulong)jobNameLen) + jobNameLen + (record.DueTimeUtcTicks.HasValue ? sizeof(long) : 0);
    }

    /// <summary>Encodes <paramref name="record"/> to a newly allocated canonical byte array.</summary>
    /// <exception cref="ArgumentException">Any field of <paramref name="record"/> violates the wire format constraints.</exception>
    public static byte[] Encode(WakeupRecord record)
    {
        var length = EncodedLength(record);
        var buffer = new byte[length];
        _ = Encode(record, buffer);
        return buffer;
    }

    /// <summary>Encodes <paramref name="record"/> into <paramref name="destination"/> and returns the bytes written.</summary>
    /// <exception cref="ArgumentException"><paramref name="record"/> is invalid or <paramref name="destination"/> is too short.</exception>
    public static int Encode(WakeupRecord record, Span<byte> destination)
    {
        ValidateForEncode(record, out var grainIdLen, out var jobNameLen);
        var total = 2 + VarintLength((ulong)grainIdLen) + grainIdLen + VarintLength((ulong)jobNameLen) + jobNameLen + (record.DueTimeUtcTicks.HasValue ? sizeof(long) : 0);
        if (destination.Length < total)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Destination span of length {destination.Length} is too short; {total} bytes are required."),
                nameof(destination));
        }

        var offset = 0;
        destination[offset++] = Version;
        destination[offset++] = record.DueTimeUtcTicks.HasValue ? HasDueTimeFlag : (byte)0;
        offset += WriteVarint(destination[offset..], (ulong)grainIdLen);

        var typeSpan = record.Target.Type.Value.AsSpan();
        typeSpan.CopyTo(destination[offset..]);
        offset += typeSpan.Length;
        destination[offset++] = (byte)'/';
        var keySpan = record.Target.Key.AsSpan();
        keySpan.CopyTo(destination[offset..]);
        offset += keySpan.Length;

        offset += WriteVarint(destination[offset..], (ulong)jobNameLen);
        offset += Encoding.UTF8.GetBytes(record.JobName.AsSpan(), destination[offset..]);

        if (record.DueTimeUtcTicks is { } ticks)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], ticks);
            offset += sizeof(long);
        }

        return offset;
    }

    /// <summary>
    /// Decodes a canonical version 1 wakeup companion record from <paramref name="source"/>, returning
    /// <see langword="null"/> on success or an <see cref="InvalidDataException"/> carrying the first broken rule's
    /// reason code on rejection.
    /// </summary>
    public static InvalidDataException? Decode(ReadOnlySpan<byte> source, out WakeupRecord record)
    {
        record = default;
        if (source.Length < 1)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated before version.");
        }

        var version = source[0];
        if (version != Version)
        {
            return Reject(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record version is {version}, expected {Version}."));
        }

        if (source.Length < 2)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated before flags.");
        }

        var flags = source[1];
        if ((flags & ~HasDueTimeFlag) != 0)
        {
            return Reject(ReasonBadFlags, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record flags 0x{flags:X2} set reserved bits."));
        }

        var hasDueTime = (flags & HasDueTimeFlag) != 0;
        var offset = 2;

        // TargetGrainId length varint.
        var status = ReadVarint(source[offset..], out var grainIdLenU64, out var varintLen);
        if (status == VarintStatus.Unterminated)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in target grain ID length.");
        }

        if (status == VarintStatus.Invalid)
        {
            return Reject(ReasonBadVarint, "Wakeup companion record has an invalid target grain ID length varint.");
        }

        offset += varintLen;
        if (grainIdLenU64 == 0)
        {
            return Reject(ReasonEmptyGrainId, "Wakeup companion record target grain ID is empty.");
        }

        if (grainIdLenU64 > MaxGrainIdUtf8Length)
        {
            return Reject(ReasonGrainIdTooLong, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record target grain ID length {grainIdLenU64} exceeds {MaxGrainIdUtf8Length}."));
        }

        var grainIdLen = (int)grainIdLenU64;
        if (source.Length - offset < grainIdLen)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in target grain ID bytes.");
        }

        var grainIdBytes = source.Slice(offset, grainIdLen);
        offset += grainIdLen;
        if (!Utf8.IsValid(grainIdBytes))
        {
            return Reject(ReasonInvalidUtf8, "Wakeup companion record target grain ID is not valid UTF-8.");
        }

        if (!TryParseCanonicalGrainId(grainIdBytes, out var target))
        {
            return Reject(ReasonBadGrainId, "Wakeup companion record target grain ID is malformed.");
        }

        // JobName length varint.
        status = ReadVarint(source[offset..], out var jobNameLenU64, out varintLen);
        if (status == VarintStatus.Unterminated)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in job name length.");
        }

        if (status == VarintStatus.Invalid)
        {
            return Reject(ReasonBadVarint, "Wakeup companion record has an invalid job name length varint.");
        }

        offset += varintLen;
        if (jobNameLenU64 == 0)
        {
            return Reject(ReasonEmptyJobName, "Wakeup companion record job name is empty.");
        }

        if (jobNameLenU64 > MaxJobNameUtf8Length)
        {
            return Reject(ReasonJobNameTooLong, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record job name length {jobNameLenU64} exceeds {MaxJobNameUtf8Length}."));
        }

        var jobNameLen = (int)jobNameLenU64;
        if (source.Length - offset < jobNameLen)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in job name bytes.");
        }

        var jobNameBytes = source.Slice(offset, jobNameLen);
        offset += jobNameLen;
        if (!Utf8.IsValid(jobNameBytes))
        {
            return Reject(ReasonInvalidUtf8, "Wakeup companion record job name is not valid UTF-8.");
        }

        var jobName = DecodeJobName(jobNameBytes);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return Reject(ReasonBlankJobName, "Wakeup companion record job name is blank.");
        }

        long? dueTimeUtcTicks = null;
        if (hasDueTime)
        {
            if (source.Length - offset < sizeof(long))
            {
                return Reject(ReasonTruncated, "Wakeup companion record is truncated in due time ticks.");
            }

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(offset, sizeof(long)));
            offset += sizeof(long);
            if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return Reject(ReasonBadTicks, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record due time ticks {ticks} is out of DateTimeOffset range."));
            }

            dueTimeUtcTicks = ticks;
        }

        if (offset < source.Length)
        {
            return Reject(ReasonTrailingBytes, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record has {source.Length - offset} trailing byte(s)."));
        }

        record = new WakeupRecord(target, jobName, dueTimeUtcTicks);
        return null;
    }

    /// <summary>
    /// Decodes a canonical version 1 wakeup companion record from a potentially multi-segment
    /// <paramref name="source"/>, returning <see langword="null"/> on success or an <see cref="InvalidDataException"/>
    /// carrying the first broken rule's reason code on rejection.
    /// </summary>
    public static InvalidDataException? Decode(ReadOnlySequence<byte> source, out WakeupRecord record)
    {
        if (source.IsSingleSegment)
        {
            return Decode(source.FirstSpan, out record);
        }

        if (source.Length <= MaxEncodedLength)
        {
            Span<byte> scratch = stackalloc byte[(int)source.Length];
            source.CopyTo(scratch);
            return Decode(scratch, out record);
        }

        // Input exceeds MaxEncodedLength; decode via SequenceReader without allocating so the exact procedural
        // rejection reason matches the contiguous ReadOnlySpan<byte> overload.
        return DecodeOversizedMultiSegment(source, out record);
    }

    private static InvalidDataException? DecodeOversizedMultiSegment(ReadOnlySequence<byte> source, out WakeupRecord record)
    {
        record = default;
        var reader = new SequenceReader<byte>(source);
        if (!reader.TryRead(out var version))
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated before version.");
        }

        if (version != Version)
        {
            return Reject(ReasonBadVersion, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record version is {version}, expected {Version}."));
        }

        if (!reader.TryRead(out var flags))
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated before flags.");
        }

        if ((flags & ~HasDueTimeFlag) != 0)
        {
            return Reject(ReasonBadFlags, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record flags 0x{flags:X2} set reserved bits."));
        }

        var hasDueTime = (flags & HasDueTimeFlag) != 0;

        var status = ReadVarint(ref reader, out var grainIdLenU64);
        if (status == VarintStatus.Unterminated)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in target grain ID length.");
        }

        if (status == VarintStatus.Invalid)
        {
            return Reject(ReasonBadVarint, "Wakeup companion record has an invalid target grain ID length varint.");
        }

        if (grainIdLenU64 == 0)
        {
            return Reject(ReasonEmptyGrainId, "Wakeup companion record target grain ID is empty.");
        }

        if (grainIdLenU64 > MaxGrainIdUtf8Length)
        {
            return Reject(ReasonGrainIdTooLong, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record target grain ID length {grainIdLenU64} exceeds {MaxGrainIdUtf8Length}."));
        }

        var grainIdLen = (int)grainIdLenU64;
        if (reader.Remaining < grainIdLen)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in target grain ID bytes.");
        }

        Span<byte> grainIdBuf = stackalloc byte[grainIdLen];
        _ = reader.TryCopyTo(grainIdBuf);
        reader.Advance(grainIdLen);
        if (!Utf8.IsValid(grainIdBuf))
        {
            return Reject(ReasonInvalidUtf8, "Wakeup companion record target grain ID is not valid UTF-8.");
        }

        if (!TryParseCanonicalGrainId(grainIdBuf, out _))
        {
            return Reject(ReasonBadGrainId, "Wakeup companion record target grain ID is malformed.");
        }

        status = ReadVarint(ref reader, out var jobNameLenU64);
        if (status == VarintStatus.Unterminated)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in job name length.");
        }

        if (status == VarintStatus.Invalid)
        {
            return Reject(ReasonBadVarint, "Wakeup companion record has an invalid job name length varint.");
        }

        if (jobNameLenU64 == 0)
        {
            return Reject(ReasonEmptyJobName, "Wakeup companion record job name is empty.");
        }

        if (jobNameLenU64 > MaxJobNameUtf8Length)
        {
            return Reject(ReasonJobNameTooLong, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record job name length {jobNameLenU64} exceeds {MaxJobNameUtf8Length}."));
        }

        var jobNameLen = (int)jobNameLenU64;
        if (reader.Remaining < jobNameLen)
        {
            return Reject(ReasonTruncated, "Wakeup companion record is truncated in job name bytes.");
        }

        Span<byte> jobNameBuf = stackalloc byte[jobNameLen];
        _ = reader.TryCopyTo(jobNameBuf);
        reader.Advance(jobNameLen);
        if (!Utf8.IsValid(jobNameBuf))
        {
            return Reject(ReasonInvalidUtf8, "Wakeup companion record job name is not valid UTF-8.");
        }

        if (string.IsNullOrWhiteSpace(DecodeJobName(jobNameBuf)))
        {
            return Reject(ReasonBlankJobName, "Wakeup companion record job name is blank.");
        }

        if (hasDueTime)
        {
            if (reader.Remaining < sizeof(long))
            {
                return Reject(ReasonTruncated, "Wakeup companion record is truncated in due time ticks.");
            }

            Span<byte> ticksBuf = stackalloc byte[sizeof(long)];
            _ = reader.TryCopyTo(ticksBuf);
            reader.Advance(sizeof(long));
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(ticksBuf);
            if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return Reject(ReasonBadTicks, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record due time ticks {ticks} is out of DateTimeOffset range."));
            }
        }

        return Reject(ReasonTrailingBytes, string.Create(CultureInfo.InvariantCulture, $"Wakeup companion record has {reader.Remaining} trailing byte(s)."));
    }

    private static string DecodeJobName(ReadOnlySpan<byte> jobNameBytes) =>
        Encoding.UTF8.GetString(jobNameBytes);

    private static bool TryParseCanonicalGrainId(ReadOnlySpan<byte> utf8Bytes, out GrainId target)
    {
        var slash = utf8Bytes.IndexOf((byte)'/');
        if (slash <= 0)
        {
            target = default;
            return false;
        }

        var typeBytes = utf8Bytes[..slash].ToArray();
        var keyBytes = utf8Bytes[(slash + 1)..].ToArray();
        target = new GrainId(new GrainType(typeBytes), new IdSpan(keyBytes));
        return true;
    }

    private static void ValidateForEncode(WakeupRecord record, out int grainIdLen, out int jobNameLen)
    {
        if (record.Target.Type.IsDefault)
        {
            throw new ArgumentException("Wakeup target GrainId must have a non-empty GrainType.", nameof(record));
        }

        var typeSpan = record.Target.Type.Value.AsSpan();
        var keySpan = record.Target.Key.AsSpan();
        if (typeSpan.Contains((byte)'/'))
        {
            throw new ArgumentException("Wakeup target GrainType must not contain '/'.", nameof(record));
        }

        if (!Utf8.IsValid(typeSpan) || !Utf8.IsValid(keySpan))
        {
            throw new ArgumentException("Wakeup target GrainId must be valid UTF-8.", nameof(record));
        }

        grainIdLen = typeSpan.Length + 1 + keySpan.Length;
        if (grainIdLen > MaxGrainIdUtf8Length)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Wakeup target GrainId UTF-8 length {grainIdLen} exceeds {MaxGrainIdUtf8Length}."),
                nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.JobName))
        {
            throw new ArgumentException("Wakeup job name must be non-empty and not blank.", nameof(record));
        }

        jobNameLen = Encoding.UTF8.GetByteCount(record.JobName);
        if (jobNameLen > MaxJobNameUtf8Length)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Wakeup job name UTF-8 length {jobNameLen} exceeds {MaxJobNameUtf8Length}."),
                nameof(record));
        }

        if (record.DueTimeUtcTicks is { } ticks && (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks))
        {
            throw new ArgumentOutOfRangeException(
                nameof(record),
                ticks,
                string.Create(CultureInfo.InvariantCulture, $"Wakeup due time ticks {ticks} is outside [0, {DateTimeOffset.MaxValue.UtcTicks}]."));
        }
    }

    private static InvalidDataException Reject(string reason, string message)
    {
        var exception = new InvalidDataException(message);
        exception.Data[ReasonKey] = reason;
        return exception;
    }

    private enum VarintStatus : byte
    {
        Valid,
        Unterminated,
        Invalid,
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
        var length = 0;
        while (value >= 0x80)
        {
            destination[length++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        destination[length++] = (byte)value;
        return length;
    }

    private static VarintStatus ReadVarint(ReadOnlySpan<byte> source, out ulong value, out int length)
    {
        value = 0;
        for (var index = 0; index < MaxVarintBytes; index++)
        {
            if (index == source.Length)
            {
                length = index;
                return VarintStatus.Unterminated;
            }

            var b = source[index];
            if (index == MaxVarintBytes - 1 && b > 1)
            {
                length = index + 1;
                return VarintStatus.Invalid;
            }

            value |= (ulong)(b & 0x7F) << (7 * index);
            if ((b & 0x80) == 0)
            {
                length = index + 1;
                return index > 0 && b == 0 ? VarintStatus.Invalid : VarintStatus.Valid;
            }
        }

        length = MaxVarintBytes;
        return VarintStatus.Invalid;
    }

    private static VarintStatus ReadVarint(ref SequenceReader<byte> reader, out ulong value)
    {
        value = 0;
        for (var index = 0; index < MaxVarintBytes; index++)
        {
            if (!reader.TryRead(out var b))
            {
                return VarintStatus.Unterminated;
            }

            if (index == MaxVarintBytes - 1 && b > 1)
            {
                return VarintStatus.Invalid;
            }

            value |= (ulong)(b & 0x7F) << (7 * index);
            if ((b & 0x80) == 0)
            {
                return index > 0 && b == 0 ? VarintStatus.Invalid : VarintStatus.Valid;
            }
        }

        return VarintStatus.Invalid;
    }
}
