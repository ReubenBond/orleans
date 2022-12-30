using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Orleans.DurableTasks.Tests
{
    [Trait("Category", "BVT")]
    public class TaskIdSegmentTests
    {
        [Fact]
        public void RepresentationIsInconsequential()
        {
            var aParent = new TaskIdSegment("foo/bar");
            var a = aParent.CreateChild("baz");
            var b = new TaskIdSegment("foo/bar/baz");
            Assert.Equal(a, b);
            Assert.Equal(b.ToString(), b.ToString());
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.Equal(a.ToString().Length, a.Length);
            Assert.Equal(b.ToString().Length, b.Length);
            Assert.Equal(a.Length, b.Length);
        }
    }
}
