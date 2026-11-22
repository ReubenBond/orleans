using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Buffers.Tests;

public sealed class ArcBufferAlignedPageTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int PageSize = ArcBufferWriter.AlignedPageSize;

    [Fact]
    public void AlignedPagesAreDisjointAlignedWindowsOfPinnedSlabs()
    {
        var pages = Enumerable.Range(0, 300).Select(static _ => ArcBufferPagePool.Shared.RentAligned()).ToArray();
        try
        {
            foreach (var page in pages)
            {
                Assert.True(page.IsAligned);
                Assert.False(page.IsMinimumSize);
                Assert.Equal(PageSize, page.Capacity);
                Assert.Equal(PageSize, page.WriteCapacity);
                Assert.Equal(0, Address(page) % PageSize);
                Assert.True(page.Origin + PageSize <= page.Array.Length);
                Assert.Equal(PageSize, page.WritableSpan.Length);
                Assert.Equal(PageSize, page.WritableMemory.Length);
            }

            var windows = pages.Select(static page => Address(page)).Order().ToArray();
            for (var index = 1; index < windows.Length; index++)
            {
                Assert.True(windows[index] - windows[index - 1] >= PageSize);
            }
        }
        finally
        {
            foreach (var page in pages)
            {
                page.Pin(page.Version);
                page.Unpin(page.Version);
            }
        }
    }

    [Fact]
    public void AlignedPageViewsAddressOnlyTheirOwnWindow()
    {
        var page = ArcBufferPagePool.Shared.RentAligned();
        page.Pin(page.Version);
        try
        {
            page.AsSpan(0, PageSize).Fill(0xA5);
            page.Advance(100);

            Assert.Equal(page.Origin, page.ReadableArraySegment.Offset);
            Assert.Equal(100, page.ReadableArraySegment.Count);
            Assert.Equal(page.Origin + 100, page.WritableArraySegment.Offset);
            Assert.Equal(PageSize - 100, page.WritableArraySegment.Count);
            Assert.Equal(page.Origin + 7, page.AsArraySegment(7, 3).Offset);
            Assert.Equal(PageSize - 100, page.WriteCapacity);

            page.AsArraySegment(10, 1).AsSpan()[0] = 0x5A;
            Assert.Equal(0x5A, page.ReadableSpan[10]);
            Assert.Equal(0x5A, page.AsMemory(10, 1).Span[0]);
            Assert.True(MemoryMarshal.TryGetArray<byte>(page.AsMemory(0, 1), out var segment));
            Assert.Equal(page.Origin, segment.Offset);

            Assert.Throws<ArgumentOutOfRangeException>(() => page.AsSpan(PageSize - 1, 2).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => page.AsMemory(PageSize - 1, 2));
        }
        finally
        {
            page.SetLength(0, page.Version);
            page.Unpin(page.Version);
        }
    }

    [Fact]
    public void AlignedWriterProducesAlignedPagesWithTheWrittenBytes()
    {
        var expected = new byte[3 * PageSize + 100];
        new Random(7).NextBytes(expected);
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write(expected.AsSpan(0, 10));
        writer.Write(expected.AsSpan(10));

        using var buffer = writer.ConsumeSlice(writer.Length);
        Assert.Equal(expected, buffer.ToArray());
        var pages = buffer.Pages.ToArray();
        Assert.Equal(4, pages.Length);
        Assert.All(pages, static page =>
        {
            Assert.True(page.IsAligned);
            Assert.Equal(0, Address(page) % PageSize);
        });
        Assert.Equal([PageSize, PageSize, PageSize, 100], buffer.PageSegments.Select(static segment => segment.Length));
        Assert.All(buffer.ArraySegments.Zip(pages), static pair => Assert.Equal(pair.Second.Origin, pair.First.Offset));
    }

    [Fact]
    public void AlignedWriterRejectsAHintLargerThanAPage()
    {
        using var writer = ArcBufferWriter.CreateAligned();
        Assert.Equal(PageSize, writer.GetSpan().Length);
        writer.AdvanceWriter(PageSize);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetSpan(PageSize + 1).Length);
        Assert.Equal(PageSize, writer.GetMemory(PageSize).Length);
    }

    [Fact]
    public void ReleasedAlignedPagesReturnToTheAlignedClass()
    {
        ArcBufferPage page;
        int version;
        using (var writer = ArcBufferWriter.CreateAligned())
        {
            writer.Write([1, 2, 3]);
            using var buffer = writer.ConsumeSlice(3);
            page = buffer.First;
            version = page.Version;
            Assert.Equal(2, page.ReferenceCount);
        }

        Assert.Equal(0, page.ReferenceCount);
        Assert.Equal(version + 1, page.Version);
        Assert.Equal(0, page.Length);
        Assert.True(page.IsAligned);
        Assert.True(page.IsValid);
    }

    [Fact]
    public void ResetKeepsTheWriterAligned()
    {
        using var writer = ArcBufferWriter.CreateAligned();
        writer.Write([1]);
        writer.Reset();
        writer.Write([2]);

        using var buffer = writer.ConsumeSlice(1);
        Assert.True(buffer.First.IsAligned);
        Assert.Equal([2], buffer.ToArray());
    }

    [Fact]
    public void ExistingSizeClassesKeepTheirShape()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[ArcBufferWriter.MinimumPageSize + 1]);
        using var buffer = writer.ConsumeSlice(writer.Length);
        var pages = buffer.Pages.ToArray();

        Assert.All(pages, static page =>
        {
            Assert.False(page.IsAligned);
            Assert.Equal(0, page.Origin);
            Assert.Equal(page.Array.Length, page.Capacity);
        });
        Assert.True(pages[0].IsMinimumSize);
    }

    private static long Address(ArcBufferPage page) =>
        Marshal.UnsafeAddrOfPinnedArrayElement(page.Array, page.Origin);
}
