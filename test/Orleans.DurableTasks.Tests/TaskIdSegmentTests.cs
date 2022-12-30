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
            var aParent = new TaskId("foo/bar");
            var a = aParent.CreateChild("baz");
            var b = new TaskId("foo/bar/baz");
            Assert.Equal(a, b);
            Assert.Equal(b.ToString(), b.ToString());
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.Equal(a.ToString().Length, a.Length);
            Assert.Equal(b.ToString().Length, b.Length);
            Assert.Equal(a.Length, b.Length);

            var aSegments = new List<string>();
            foreach (var segment in a)
            {
                aSegments.Add(segment.ToString());
            }

            var bSegments = new List<string>();
            foreach (var segment in b)
            {
                bSegments.Add(segment.ToString());
            }

            Assert.Equal(aSegments.Count, bSegments.Count);

            Assert.Equal(aSegments, bSegments);
        }

        [Fact]
        public void SegmentsCanBeEscaped()
        {
            var aParent = new TaskId("foo/bar\\/");
            var a = aParent.CreateChild("baz");
            var b = new TaskId("foo/bar\\//baz");
            var c = new TaskId("foo/bar\\//baz");
            Assert.Equal(a, b);
            Assert.Equal(b.ToString(), b.ToString());
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.Equal(a.ToString().Length, a.Length);
            Assert.Equal(b.ToString().Length, b.Length);
            Assert.Equal(a.Length, b.Length);

            var aSegments = new List<string>();
            foreach (var segment in a)
            {
                aSegments.Add(segment.ToString());
            }

            var bSegments = new List<string>();
            foreach (var segment in b)
            {
                bSegments.Add(segment.ToString());
            }

            Assert.Equal(aSegments.Count, bSegments.Count);

            Assert.Equal(aSegments, bSegments);
        }
        
        [Fact]
        public void OnlyValidValuesAreAllowed()
        {
            Assert.Throws<ArgumentNullException>(() => new TaskId(null));
            Assert.Throws<ArgumentException>(() => new TaskId(""));
            Assert.Throws<ArgumentException>(() => new TaskId("/"));
            Assert.Throws<ArgumentException>(() => new TaskId("//"));
            Assert.Throws<ArgumentException>(() => new TaskId("a//"));
            Assert.Throws<ArgumentException>(() => new TaskId("//a"));
            Assert.Throws<ArgumentException>(() => new TaskId("\\//"));
            Assert.Throws<ArgumentException>(() => new TaskId("a/b//c/d"));
            Assert.Throws<ArgumentException>(() => new TaskId("aaa/bbb//ccc/ddd"));
            Assert.Throws<ArgumentException>(() => new TaskId("a/b/c/d//"));
            Assert.Throws<ArgumentException>(() => new TaskId("//a/b/c/d//"));
            _ = new TaskId("\\/\\/");
            _ = new TaskId("aaa/bbb/ccc/ddd");
            _ = new TaskId("a/b/c/d");
            _ = new TaskId("\\/\\/a/b/c/d\\/\\/");
        }
    }
}
