using System;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
public sealed class ReferencedObjectCollectionTests
{
    [Fact]
    public void ResetReusesOverflowStorage()
    {
        var references = new ReferencedObjectCollection();
        var values = new object[256];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new object();
        }

        Assert.True(AddReferences(references, values));
        references.Reset();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var allAdded = AddReferences(references, values);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(allAdded);
        Assert.Equal(0, allocated);
        var idTable = references.CopyIdTable();
        Assert.Equal(values.Length, idTable.Count);
        Assert.Equal(129U, idTable[values[128]]);
        Assert.True(references.GetOrAddReference(values[128], out var reference));
        Assert.Equal(129U, reference);
    }

    [Fact]
    public void CopyReferenceTableIncludesReusedOverflowStorage()
    {
        var references = new ReferencedObjectCollection();
        var values = new object[256];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new object();
            references.RecordReferenceField(values[i]);
        }

        references.Reset();
        for (var i = 0; i < values.Length; i++)
        {
            references.RecordReferenceField(values[i]);
        }

        var referenceTable = references.CopyReferenceTable();
        Assert.Equal(values.Length, referenceTable.Count);
        Assert.Same(values[128], referenceTable[129]);
    }

    private static bool AddReferences(ReferencedObjectCollection references, object[] values)
    {
        foreach (var value in values)
        {
            if (references.GetOrAddReference(value, out _))
            {
                return false;
            }
        }

        return true;
    }
}
