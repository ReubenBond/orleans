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
        public void SegmentsAreEqual()
        {
            var aParent = new TaskIdSegment("foo/bar");
            var a = aParent.CreateChild("baz");
            var b = new TaskIdSegment("foo/bar/baz");
            var aL = new List<string>();
            var bL = new List<string>();
            Console.WriteLine(a);
            Console.WriteLine(b);
            foreach (var seg in a)
            {
                var str = new string(seg);
                aL.Add(str);
                Console.WriteLine(str);
            }
            foreach (var seg in b)
            {
                var str = new string(seg);
                bL.Add(str);
                Console.WriteLine(str);
            }
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }
    }
}
