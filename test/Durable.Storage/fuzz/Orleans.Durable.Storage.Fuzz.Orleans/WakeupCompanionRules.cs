using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using Orleans.Durable.Jobs;

namespace Orleans.Durable.Storage.Fuzz.Orleans;

/// <summary>
/// Independent reference model of the <c>orleans.wakeup</c> message record format (version 1) for fuzzing
/// <see cref="WakeupMessageCodec"/> (orleans.md §5.15, verification.md §6.1).
/// </summary>
internal static class WakeupCompanionRules
{
    public readonly record struct Parsed(string TargetGrainId, string JobName, long? DueTimeUtcTicks);

    public readonly record struct Reading(Parsed? Value, string? Reason);

    public static Reading Read(ReadOnlySpan<byte> input)
    {
        if (input.Length < 1)
        {
            return new(null, WakeupMessageCodec.ReasonTruncated);
        }

        if (input[0] != WakeupMessageCodec.Version)
        {
            return new(null, WakeupMessageCodec.ReasonBadVersion);
        }

        if (input.Length < 2)
        {
            return new(null, WakeupMessageCodec.ReasonTruncated);
        }

        var flags = input[1];
        if ((flags & ~WakeupMessageCodec.HasDueTimeFlag) != 0)
        {
            return new(null, WakeupMessageCodec.ReasonBadFlags);
        }

        var hasDueTime = (flags & WakeupMessageCodec.HasDueTimeFlag) != 0;
        var pos = 2;

        var grainLenStatus = ReadVarint(input, ref pos, out var grainLen);
        if (grainLenStatus == 1)
        {
            return new(null, WakeupMessageCodec.ReasonTruncated);
        }

        if (grainLenStatus == 2)
        {
            return new(null, WakeupMessageCodec.ReasonBadVarint);
        }

        if (grainLen == 0)
        {
            return new(null, WakeupMessageCodec.ReasonEmptyGrainId);
        }

        if (grainLen > WakeupMessageCodec.MaxGrainIdUtf8Length)
        {
            return new(null, WakeupMessageCodec.ReasonGrainIdTooLong);
        }

        if (input.Length - pos < (int)grainLen)
        {
            return new(null, WakeupMessageCodec.ReasonTruncated);
        }

        var grainBytes = input.Slice(pos, (int)grainLen);
        pos += (int)grainLen;
        if (!Utf8.IsValid(grainBytes))
        {
            return new(null, WakeupMessageCodec.ReasonInvalidUtf8);
        }

        var slash = grainBytes.IndexOf((byte)'/');
        if (slash <= 0)
        {
            return new(null, WakeupMessageCodec.ReasonBadGrainId);
        }

        var grainText = Encoding.UTF8.GetString(grainBytes);

        var jobLenStatus = ReadVarint(input, ref pos, out var jobLen);
        if (jobLenStatus == 1)
        {
            return new(null, WakeupMessageCodec.ReasonTruncated);
        }

        if (jobLenStatus == 2)
        {
            return new(null, WakeupMessageCodec.ReasonBadVarint);
        }

        if (jobLen == 0)
        {
            return new(null, WakeupMessageCodec.ReasonEmptyJobName);
        }

        if (jobLen > WakeupMessageCodec.MaxJobNameUtf8Length)
        {
            return new(null, WakeupMessageCodec.ReasonJobNameTooLong);
        }

        if (input.Length - pos < (int)jobLen)
        {
            return new(null, WakeupMessageCodec.ReasonTruncated);
        }

        var jobBytes = input.Slice(pos, (int)jobLen);
        pos += (int)jobLen;
        if (!Utf8.IsValid(jobBytes))
        {
            return new(null, WakeupMessageCodec.ReasonInvalidUtf8);
        }

        var jobName = Encoding.UTF8.GetString(jobBytes);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return new(null, WakeupMessageCodec.ReasonBlankJobName);
        }

        long? dueTime = null;
        if (hasDueTime)
        {
            if (input.Length - pos < sizeof(long))
            {
                return new(null, WakeupMessageCodec.ReasonTruncated);
            }

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(input.Slice(pos, sizeof(long)));
            pos += sizeof(long);
            if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return new(null, WakeupMessageCodec.ReasonBadTicks);
            }

            dueTime = ticks;
        }

        if (pos < input.Length)
        {
            return new(null, WakeupMessageCodec.ReasonTrailingBytes);
        }

        return new(new Parsed(grainText, jobName, dueTime), null);
    }

    // Returns: 0 = valid, 1 = truncated/unterminated, 2 = invalid/non-canonical/overflow.
    private static int ReadVarint(ReadOnlySpan<byte> source, ref int pos, out ulong value)
    {
        value = 0;
        for (var i = 0; i < 10; i++)
        {
            if (pos >= source.Length)
            {
                return 1;
            }

            var b = source[pos++];
            if (i == 9 && b > 1)
            {
                return 2;
            }

            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                return i > 0 && b == 0 ? 2 : 0;
            }
        }

        return 2;
    }
}
