using System.Diagnostics;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.S3.Archive;

/// <summary>
/// Maps object names onto a directory-like key hierarchy for Amazon S3 directory buckets (<c>--x-s3</c>), whose
/// <c>ListObjectsV2</c> returns entries unordered, refuses <c>StartAfter</c>, and requires a non-empty <c>Prefix</c>
/// with <c>Delimiter = "/"</c> to end in <c>/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implements the hierarchical directory layout of <c>research/papers/logdrive/paper.md</c> §3.2:
/// </para>
/// <blockquote>
/// "S3Express: This storage service is nearly identical to S3, except for its single-AZ durability and some differences in
/// API semantics. One difference is that we can no longer list keys in lexicographical order [10]. To get around this, we
/// create a directory-like hierarchical structure on the S3Express key space. We then enumerate all entries at each level
/// of this hierarchy to route to the non-contiguous tail; and then scan backwards K entries to find the contiguous tail.
/// By Lemma LD.2 (Window Scan Property), this is sufficient to return a correct weakTail response."
/// </blockquote>
/// <para>
/// Each <see cref="ObjectName"/> is mapped character-for-character with <c>/</c> replaced by <c>.</c> (an order-preserving
/// bijection over the <see cref="ObjectName"/> alphabet, since <c>'-'</c> &lt; <c>'.'</c> &lt; <c>'/'</c> &lt; <c>'0'</c>
/// and <see cref="ObjectName"/> forbids <c>'.'</c>, leading or trailing <c>'/'</c>, and <c>"//"</c>), then split into
/// <see cref="ChunkSize"/>-character directory segments separated by <c>/</c> with a final leaf segment of 1 to
/// <see cref="ChunkSize"/> characters suffixed with <c>L</c>. Because <see cref="ChunkSize"/> is 3, every decimal sequence
/// (such as 20-digit ledger IDs and reverse slot keys) has at most 1,000 entries per directory, fitting in a single
/// <c>ListObjectsV2</c> page; no directory segment can equal <c>"."</c> or <c>".."</c>; and no leaf key can collide with
/// a directory prefix because only leaves carry <c>L</c>.
/// </para>
/// <para>
/// In any directory at logical prefix <c>P</c>, each leaf <c>x</c> (<c>1 &lt;= |x| &lt;= 3</c>) represents the singleton
/// <c>{ P + x }</c> and each subdirectory <c>p/</c> (<c>|p| == 3</c>) represents <c>{ P + p + r : r != "" }</c>. Those
/// intervals are pairwise disjoint, so sorting a single directory's entries by <c>(logicalName, isDirectory)</c> and
/// descending depth-first lazily visits matching objects in exact ordinal order without enumerating pruned subtrees.
/// </para>
/// </remarks>
internal static class S3DirectoryBucketLayout
{
    /// <summary>The reserved suffix of every Amazon S3 Express One Zone directory bucket name.</summary>
    internal const string BucketSuffix = "--x-s3";

    /// <summary>The width of every directory segment in logical object-name characters.</summary>
    internal const int ChunkSize = 3;

    private const char EncodedSlash = '.';
    private const char LeafMarker = 'L';

    /// <summary>Returns whether <paramref name="bucketName"/> names an S3 directory bucket.</summary>
    internal static bool IsDirectoryBucket(string bucketName) =>
        bucketName.Length > BucketSuffix.Length
        && bucketName.EndsWith(BucketSuffix, StringComparison.Ordinal);

    /// <summary>Returns the S3 key of <paramref name="name"/> under <paramref name="storePrefix"/>.</summary>
    internal static string Encode(string storePrefix, ObjectName name)
    {
        var value = name.Value;
        var directories = (value.Length - 1) / ChunkSize;
        return string.Create(
            storePrefix.Length + value.Length + directories + 1,
            (storePrefix, value),
            static (destination, state) =>
            {
                state.storePrefix.AsSpan().CopyTo(destination);
                var cursor = state.storePrefix.Length;
                var source = state.value.AsSpan();
                for (var index = 0; index < source.Length; index++)
                {
                    if (index > 0 && index % ChunkSize == 0)
                    {
                        destination[cursor++] = '/';
                    }

                    destination[cursor++] = EncodeChar(source[index]);
                }

                destination[cursor] = LeafMarker;
            });
    }

    /// <summary>
    /// Returns the deepest directory ancestor every matching <see cref="ObjectName"/> must lie beneath, or <c>false</c>
    /// when no valid <see cref="ObjectName"/> can satisfy both <paramref name="prefix"/> and <paramref name="startAfter"/>.
    /// </summary>
    internal static bool TryGetStartingDirectory(
        string storePrefix,
        string prefix,
        string? startAfter,
        out string logicalPrefix,
        out string s3Prefix)
    {
        if (!CanContainAfter(prefix, startAfter))
        {
            logicalPrefix = string.Empty;
            s3Prefix = string.Empty;
            return false;
        }

        // An ObjectName never ends in '/', and prefix itself is excluded when startAfter >= prefix.
        var minMatchingLength = prefix.Length == 0
            ? 1
            : prefix[^1] == '/' || startAfter is not null && string.CompareOrdinal(prefix, startAfter) <= 0
                ? prefix.Length + 1
                : prefix.Length;

        if (minMatchingLength > ObjectName.MaxLength)
        {
            logicalPrefix = string.Empty;
            s3Prefix = string.Empty;
            return false;
        }

        var fullChunks = (minMatchingLength - 1) / ChunkSize;
        var logicalLength = fullChunks * ChunkSize;
        logicalPrefix = logicalLength == 0 ? string.Empty : prefix[..logicalLength];
        s3Prefix = EncodeDirectoryPrefix(storePrefix, logicalPrefix);
        return true;
    }

    /// <summary>
    /// Decodes a <c>CommonPrefixes</c> entry under <paramref name="s3DirectoryPrefix"/> to its logical prefix, returning
    /// <c>false</c> for foreign directories that do not match the store's layout or cannot contain a valid
    /// <see cref="ObjectName"/>.
    /// </summary>
    internal static bool TryDecodeChildDirectory(
        string s3DirectoryPrefix,
        string currentLogicalPrefix,
        string commonPrefix,
        out string childLogicalPrefix)
    {
        // Any object inside this directory is strictly longer than childLogicalPrefix.
        if (currentLogicalPrefix.Length + ChunkSize >= ObjectName.MaxLength
            || commonPrefix.Length != s3DirectoryPrefix.Length + ChunkSize + 1
            || commonPrefix[^1] != '/'
            || !commonPrefix.StartsWith(s3DirectoryPrefix, StringComparison.Ordinal))
        {
            childLogicalPrefix = string.Empty;
            return false;
        }

        var segment = commonPrefix.AsSpan(s3DirectoryPrefix.Length, ChunkSize);
        if (!TryDecodeSegment(currentLogicalPrefix, segment, out childLogicalPrefix)
            || !ObjectName.IsValidPrefix(childLogicalPrefix))
        {
            childLogicalPrefix = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Decodes a <c>Contents</c> key under <paramref name="s3DirectoryPrefix"/> to its logical <see cref="ObjectName"/>
    /// text, returning <c>false</c> for foreign keys that do not match the store's layout.
    /// </summary>
    internal static bool TryDecodeLeaf(
        string s3DirectoryPrefix,
        string currentLogicalPrefix,
        string key,
        out string logicalName)
    {
        var leafLength = key.Length - s3DirectoryPrefix.Length;
        var segmentLength = leafLength - 1;
        if (leafLength < 2
            || leafLength > ChunkSize + 1
            || currentLogicalPrefix.Length + segmentLength > ObjectName.MaxLength
            || key[^1] != LeafMarker
            || !key.StartsWith(s3DirectoryPrefix, StringComparison.Ordinal))
        {
            logicalName = string.Empty;
            return false;
        }

        var segment = key.AsSpan(s3DirectoryPrefix.Length, segmentLength);
        if (!TryDecodeSegment(currentLogicalPrefix, segment, out logicalName) || !ObjectName.IsValid(logicalName))
        {
            logicalName = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns whether the directory at <paramref name="childLogicalPrefix"/> can contain any <see cref="ObjectName"/>
    /// that starts with <paramref name="prefix"/> and is ordinally greater than <paramref name="startAfter"/>.
    /// </summary>
    internal static bool ShouldDescend(string childLogicalPrefix, string prefix, string? startAfter) =>
        (childLogicalPrefix.StartsWith(prefix, StringComparison.Ordinal)
            || prefix.StartsWith(childLogicalPrefix, StringComparison.Ordinal))
        && CanContainAfter(childLogicalPrefix, startAfter);

    private static bool CanContainAfter(string logicalPrefix, string? startAfter)
    {
        if (startAfter is null || logicalPrefix.Length == 0)
        {
            return true;
        }

        var common = Math.Min(logicalPrefix.Length, startAfter.Length);
        return string.CompareOrdinal(logicalPrefix, 0, startAfter, 0, common) >= 0;
    }

    private static string EncodeDirectoryPrefix(string storePrefix, string logicalPrefix)
    {
        if (logicalPrefix.Length == 0)
        {
            return storePrefix;
        }

        var chunks = logicalPrefix.Length / ChunkSize;
        return string.Create(
            storePrefix.Length + logicalPrefix.Length + chunks,
            (storePrefix, logicalPrefix),
            static (destination, state) =>
            {
                state.storePrefix.AsSpan().CopyTo(destination);
                var cursor = state.storePrefix.Length;
                var source = state.logicalPrefix.AsSpan();
                for (var index = 0; index < source.Length; index++)
                {
                    destination[cursor++] = EncodeChar(source[index]);
                    if ((index + 1) % ChunkSize == 0)
                    {
                        destination[cursor++] = '/';
                    }
                }
            });
    }

    private static bool TryDecodeSegment(string currentLogicalPrefix, ReadOnlySpan<char> encodedSegment, out string decoded)
    {
        Span<char> decodedSegment = stackalloc char[ChunkSize];
        for (var index = 0; index < encodedSegment.Length; index++)
        {
            var ch = encodedSegment[index];
            if (!IsEncodedChar(ch))
            {
                decoded = string.Empty;
                return false;
            }

            decodedSegment[index] = DecodeChar(ch);
        }

        decoded = string.Concat(currentLogicalPrefix, decodedSegment[..encodedSegment.Length]);
        return true;
    }

    private static char EncodeChar(char ch)
    {
        Debug.Assert(ch != EncodedSlash && ch != LeafMarker && (ch == '/' || IsEncodedChar(ch)));
        return ch == '/' ? EncodedSlash : ch;
    }

    private static char DecodeChar(char ch) => ch == EncodedSlash ? '/' : ch;

    private static bool IsEncodedChar(char ch) =>
        ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or EncodedSlash;
}
