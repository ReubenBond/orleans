using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>An archive store's ID (archive.md §2.4): its stored form is exactly 32 lowercase hexadecimal digits, not all zero.</summary>
[NoPageLeakCheck("Store IDs are values and strings; the tests rent no pages.")]
public sealed class ArchiveStoreIdTests
{
    [Fact]
    public void TheStoredFormIs32LowercaseHexadecimalDigitsAndParsesBack()
    {
        var id = new ArchiveStoreId(UInt128.MaxValue - 0xabc);

        Assert.Matches("^[0-9a-f]{32}$", id.ToString());
        Assert.Equal(id, ArchiveStoreId.TryParse(id.ToString()));
        Assert.Equal("00000000000000000000000000000abc", new ArchiveStoreId(0xabc).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000000000000000000000000000abc")]
    [InlineData("000000000000000000000000000000abc")]
    [InlineData("00000000000000000000000000000ABC")]
    [InlineData("0000000000000000000000000000-abc")]
    [InlineData(" 0000000000000000000000000000abc")]
    [InlineData("00000000000000000000000000000000")]
    public void TextWhichIsNotTheStoredFormParsesToNull(string text) => Assert.Null(ArchiveStoreId.TryParse(text));
}
