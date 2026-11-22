using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

[NoPageLeakCheck("Slots are values; the tests rent no pages.")]
public sealed class SlotTests
{
    [Fact]
    public void ConstructorRejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Slot(-1));
        Assert.Equal(Slot.Zero, default);
    }

    [Fact]
    public void ArithmeticIsCheckedAndCannotProduceNegativeSlots()
    {
        var position = new Slot(4);

        Assert.Equal(new Slot(7), position + 3);
        Assert.Equal(new Slot(2), position - 2);
        Assert.Equal(3, new Slot(7) - position);
        Assert.Throws<ArgumentOutOfRangeException>(() => position - 5);
        Assert.Throws<OverflowException>(() => new Slot(long.MaxValue) + 1);
    }

    [Fact]
    public void ComparisonEqualityAndFormattingUseTheUnderlyingValue()
    {
        var first = new Slot(4);
        var equal = new Slot(4);
        var later = new Slot(5);

        Assert.Equal(first, equal);
        Assert.True(first < later);
        Assert.True(first <= equal);
        Assert.True(later > first);
        Assert.True(later >= first);
        Assert.Equal(0, first.CompareTo(equal));
        Assert.Equal("4", first.ToString());
    }
}
