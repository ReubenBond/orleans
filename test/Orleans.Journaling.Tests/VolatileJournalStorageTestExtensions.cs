using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Tests;

internal static class VolatileJournalStorageTestExtensions
{
    public static (int Segments, long RetainedCapacity, int RetainedPages) GetMemoryStatistics(this VolatileJournalStorage storage)
    {
        var store = storage.Storage;
        lock (store.SyncRoot)
        {
            var pages = new HashSet<ArcBufferPage>();
            foreach (var segment in store.Segments)
            {
                foreach (var pageSegment in segment.PageSegments)
                {
                    pages.Add(pageSegment.Page);
                }
            }

            return (store.Segments.Count, pages.Sum(static page => (long)page.Array.Length), pages.Count);
        }
    }
}
