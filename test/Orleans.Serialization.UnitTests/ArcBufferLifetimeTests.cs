using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ArcBufferLifetimeTests
{
    [Fact]
    public void DefaultAndEmpty_AreOwnerFreeAndReadable()
    {
        ArcBuffer value = default;
        Assert.True(value.IsEmpty);
        Assert.Null(value.First);
        Assert.Empty(value.ToArray());
        Assert.True(value.AsReadOnlySequence().IsEmpty);
        Assert.Equal(0, value.CopyTo(Span<byte>.Empty));
        Assert.False(value.MemorySegments.MoveNext());
        using var retained = value.Slice(0);
        Assert.Null(retained.First);
        value.Pin();
        value.Dispose();
        value.Dispose();
        Assert.Empty(ArcBuffer.Empty.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => value.Slice(1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void OwnerFreeBuffer_RejectsInvalidShape(int offset, int length)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ArcBuffer(null!, 0, offset, length));
        Assert.Equal(offset == 0 ? "length" : "offset", error.ParamName);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(ArcBufferWriter.MinimumPageSize + 1, 0)]
    [InlineData(0, ArcBufferWriter.MinimumPageSize * 3 + 18)]
    [InlineData(16, ArcBufferWriter.MinimumPageSize * 3 + 2)]
    [InlineData(0, int.MaxValue)]
    public void PublicConstructor_RejectsInvalidPageBoundsWithoutAcquiringPins(int offset, int length)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(ArcBufferCodecTests.Bytes(ArcBufferWriter.MinimumPageSize * 3 + 17));
        using var input = writer.PeekSlice(writer.Length);
        var first = Assert.IsType<ArcBufferPage>(input.First);
        var pages = input.Pages.ToArray();
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ArcBuffer(first, first.Version, offset, length));
        Assert.Equal(offset < 0 || offset > first.Length ? "offset" : "length", error.ParamName);
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(ArcBufferWriter.MinimumPageSize)]
    public void PublicConstructor_BorrowsValidMultiPageBytes(int offset)
    {
        using var writer = new ArcBufferWriter();
        var expected = ArcBufferCodecTests.Bytes(ArcBufferWriter.MinimumPageSize * 3 + 17);
        writer.Write(expected);
        using var input = writer.PeekSlice(writer.Length);
        var first = Assert.IsType<ArcBufferPage>(input.First);
        var pages = input.Pages.ToArray();
        var before = pages.Select(page => page.ReferenceCount).ToArray();
        var borrowed = new ArcBuffer(first, first.Version, offset, expected.Length - offset);
        Assert.Equal(expected.AsSpan(offset).ToArray(), borrowed.ToArray());
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
        borrowed.Pin();
        borrowed.Dispose();
        Assert.Equal(before, pages.Select(page => page.ReferenceCount));
    }

    [Fact]
    public void PublicConstructor_RejectsInvalidPageToken()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1 });
        using var input = writer.PeekSlice(1);
        var first = Assert.IsType<ArcBufferPage>(input.First);
        var before = first.ReferenceCount;
        Assert.Throws<InvalidOperationException>(() => new ArcBuffer(first, first.Version + 1, 0, 1));
        Assert.Equal(before, first.ReferenceCount);
    }

    [Fact]
    public void PublicEmptyPageContract_IsNullable()
    {
        var nullability = new NullabilityInfoContext();
        var field = typeof(ArcBuffer).GetField(nameof(ArcBuffer.First))!;
        var constructor = typeof(ArcBuffer).GetConstructors().Single();
        Assert.Equal(NullabilityState.Nullable, nullability.Create(field).ReadState);
        Assert.Equal(NullabilityState.Nullable, nullability.Create(constructor.GetParameters()[0]).ReadState);
        var value = new ArcBuffer(null!, 0, 0, 0);
        Assert.Null(value.First);
        Assert.Empty(value.ToArray());
        value.Dispose();
    }

    [Theory]
    [InlineData(ArcBufferWriter.MinimumPageSize)]
    [InlineData(ArcBufferWriter.MinimumPageSize + 1)]
    [InlineData(ArcBufferWriter.MinimumPageSize * 3 + 17)]
    public void ZeroLengthSlice_KeepsOffsetWithinReferencedPage(int offset)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(ArcBufferCodecTests.Bytes(ArcBufferWriter.MinimumPageSize * 3 + 17));
        using var input = writer.PeekSlice(writer.Length);
        using var empty = input.Slice(offset, 0);
        var page = Assert.IsType<ArcBufferPage>(empty.First);
        Assert.InRange(empty.Offset, 0, page.Length);
        Assert.Empty(empty.ToArray());
        var borrowed = new ArcBuffer(page, page.Version, empty.Offset, empty.Length);
        Assert.Empty(borrowed.ToArray());
        Assert.Equal(empty.Offset, borrowed.Offset);
    }

    [Fact]
    public void ZeroLengthPageBackedSlice_OwnsIndependentPin()
    {
        var writer = new ArcBufferWriter();
        var empty = writer.ConsumeSlice(0);
        var page = Assert.IsType<ArcBufferPage>(empty.First);
        Assert.Equal(2, page.ReferenceCount);
        var retained = empty.Slice(0);
        Assert.Equal(3, page.ReferenceCount);
        writer.Dispose();
        Assert.Equal(2, page.ReferenceCount);
        empty.Dispose();
        Assert.Throws<InvalidOperationException>(() => empty.Dispose());
        Assert.Equal(1, page.ReferenceCount);
        Assert.Throws<InvalidOperationException>(() => empty.ToArray());
        Assert.Throws<InvalidOperationException>(() => empty.AsReadOnlySequence());
        Assert.Empty(retained.ToArray());
        retained.Dispose();
        Assert.Equal(0, page.ReferenceCount);
    }

    [Fact]
    public void RetainedMultiPageSlice_SurvivesWriterDisposalAndPoolReuse()
    {
        var expected = Enumerable.Range(0, ArcBufferWriter.MinimumPageSize * 3 + 91).Select(i => (byte)i).ToArray();
        var writer = new ArcBufferWriter();
        writer.Write(expected);
        var original = writer.ConsumeSlice(writer.Length);
        var pages = original.Pages.ToArray();
        Assert.True(pages.Length > 1);
        using var retained = original.Slice(19, expected.Length - 47);
        writer.Dispose();
        original.Dispose();
        Assert.All(pages, page => Assert.Equal(1, page.ReferenceCount));
        for (var i = 0; i < 8; i++)
        {
            using var reuse = new ArcBufferWriter();
            reuse.Write(new byte[ArcBufferWriter.MinimumPageSize * 4]);
        }

        Assert.Equal(expected.AsSpan(19, expected.Length - 47).ToArray(), retained.ToArray());
    }

    [Fact]
    public void DisposedBuffer_RejectsReadersAndEnumerationEvenWhileAnotherPinExists()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0x80, 0xff });
        var value = writer.PeekSlice(2);
        using var retained = value.Slice(0);
        value.Dispose();
        Assert.Throws<InvalidOperationException>(() => value.ToArray());
        Assert.Throws<InvalidOperationException>(() => value.AsReadOnlySequence());
        Assert.Throws<InvalidOperationException>(() => value.Slice(0));
        Assert.Throws<InvalidOperationException>(() => value.MemorySegments.MoveNext());
        using var services = ArcBufferCodecTests.Services();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = Reader.Create(value, session);
            _ = reader.Length;
        });
        Assert.Equal(new byte[] { 0x80, 0xff }, retained.ToArray());
    }

    [Fact]
    public void DisposedWriter_RejectsMutationAndCanBeReset()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var retained = writer.PeekSlice(writer.Length);
        writer.Dispose();
        writer.Dispose();
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.ToArray());
        Assert.Throws<ObjectDisposedException>(() => writer.GetMemory());
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan());
        Assert.Throws<ObjectDisposedException>(() => writer.AdvanceWriter(1));
        Assert.Throws<ObjectDisposedException>(() => writer.Write(new byte[] { 4 }));
        Assert.Throws<ObjectDisposedException>(() => writer.WriteAt(0, new byte[] { 4 }));
        Assert.Throws<ObjectDisposedException>(() => writer.Truncate(0));
        writer.Reset();
        writer.Write(new byte[] { 5, 6 });
        using var resetSlice = writer.PeekSlice(writer.Length);
        Assert.Equal(new byte[] { 5, 6 }, resetSlice.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.ToArray());
    }
}
