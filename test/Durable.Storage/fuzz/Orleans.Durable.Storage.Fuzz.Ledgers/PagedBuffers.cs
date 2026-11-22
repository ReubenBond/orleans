using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Fuzz.Ledgers;

/// <summary>
/// Lays bytes out in an owned <see cref="ArcBuffer"/> over pages of chosen lengths, as a batch may arrive from storage
/// or a transport, so a decoder meets fields which span pages.
/// </summary>
public static class PagedBuffers
{
    /// <summary>
    /// The most one page holds here: a byte less than the largest page a writer rents, 1 MiB (buffers.md §8), so every
    /// page is larger than what it holds.
    /// </summary>
    public const int MaxPageLength = (1 << 20) - 1;

    /// <summary>
    /// Returns an owned buffer of <paramref name="bytes"/> whose pages hold <paramref name="pageLengths"/> bytes in turn,
    /// each at least 1, repeated until the bytes run out, so the division reaches the last byte; the last page holds what
    /// is left. No page holds more than <see cref="MaxPageLength"/>, and no lengths give the fewest pages.
    /// </summary>
    public static ArcBuffer Create(ReadOnlySpan<byte> bytes, ReadOnlySpan<int> pageLengths)
    {
        foreach (var pageLength in pageLengths)
        {
            if (pageLength < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pageLengths), pageLength, "A page holds at least 1 byte.");
            }
        }

        var lengths = new List<int>();
        var rest = bytes.Length;
        for (var turn = 0; !pageLengths.IsEmpty && rest > 0; turn++)
        {
            var pageLength = Math.Min(pageLengths[turn % pageLengths.Length], rest);
            AddPages(lengths, pageLength);
            rest -= pageLength;
        }

        AddPages(lengths, rest);

        // Every page is larger than what it holds, so the current page always has room left, and asking for one byte
        // more than that starts exactly one new page: no page is left empty, and no boundary is skipped.
        using var writer = new ArcBufferWriter(lengths.Count == 0 ? 0 : lengths[0] + 1);
        var offset = 0;
        for (var page = 0; page < lengths.Count; page++)
        {
            if (page > 0)
            {
                _ = writer.GetSpan(Math.Max(writer.GetSpan().Length + 1, lengths[page] + 1));
            }

            writer.Write(bytes.Slice(offset, lengths[page]));
            offset += lengths[page];
        }

        var buffer = writer.ConsumeSlice(bytes.Length);
        CheckPages(buffer, lengths);
        return buffer;
    }

    /// <summary>Returns an owned buffer of <paramref name="bytes"/> in pages of <paramref name="pageLength"/> bytes each.</summary>
    public static ArcBuffer Create(ReadOnlySpan<byte> bytes, int pageLength) => Create(bytes, [pageLength]);

    private static void AddPages(List<int> lengths, int length)
    {
        for (; length > MaxPageLength; length -= MaxPageLength)
        {
            lengths.Add(MaxPageLength);
        }

        if (length > 0)
        {
            lengths.Add(length);
        }
    }

    /// <summary>
    /// Checks that <paramref name="buffer"/> lies in pages of exactly <paramref name="lengths"/>, so a change in how the
    /// writer chooses its pages fails here rather than silently removing the boundaries a decoder is meant to meet.
    /// </summary>
    private static void CheckPages(ArcBuffer buffer, List<int> lengths)
    {
        var pages = new List<int>(lengths.Count);
        foreach (var span in buffer)
        {
            pages.Add(span.Length);
        }

        if (!pages.SequenceEqual(lengths))
        {
            buffer.Dispose();
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The buffer's pages hold [{string.Join(", ", pages)}] bytes, not the [{string.Join(", ", lengths)}] asked for."));
        }
    }
}
