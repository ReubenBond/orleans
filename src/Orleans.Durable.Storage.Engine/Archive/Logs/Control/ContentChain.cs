using System.Buffers;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Control;

/// <summary>
/// Chains the memory of buffers, without copying it, into one <see cref="ReadOnlySequence{T}"/>, which is valid while the
/// buffers are: a key's write's content over the verified batch pages its rows' bodies retain (archive-logs.md §8.2).
/// </summary>
internal static class ContentChain
{
    /// <summary>Returns the sequence of <paramref name="buffers"/>' bytes, in order, each memory segment its own.</summary>
    public static ReadOnlySequence<byte> Of(IEnumerable<ArcBuffer> buffers)
    {
        Link? first = null;
        Link? last = null;
        long length = 0;
        foreach (var buffer in buffers)
        {
            foreach (var memory in buffer.MemorySegments)
            {
                if (memory.IsEmpty)
                {
                    continue;
                }

                var link = new Link(memory, length);
                if (last is null)
                {
                    first = link;
                }
                else
                {
                    last.SetNext(link);
                }

                last = link;
                length += memory.Length;
            }
        }

        return first is null || last is null ? ReadOnlySequence<byte>.Empty : new(first, 0, last, last.Memory.Length);
    }

    private sealed class Link : ReadOnlySequenceSegment<byte>
    {
        public Link(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(Link next) => Next = next;
    }
}
