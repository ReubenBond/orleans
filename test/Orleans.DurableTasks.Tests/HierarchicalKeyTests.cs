using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Orleans.DurableTasks.Tests
{
    [Trait("Category", "BVT")]
    public class HierarchicalKeyTests
    {
        [Fact]
        public void RepresentationIsInconsequential()
        {
            var aParent = new HierarchicalKey("foo/bar");
            var a = aParent.CreateChildKey("baz");
            var b = new HierarchicalKey("foo/bar/baz");
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
            var aParent = new HierarchicalKey("foo/bar\\/");
            var a = aParent.CreateChildKey("baz");
            var b = new HierarchicalKey("foo/bar\\//baz");
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
            Assert.Throws<ArgumentNullException>(() => new HierarchicalKey(null));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey(""));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("/"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("//"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("a//"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("//a"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("\\//"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("a/b//c/d"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("aaa/bbb//ccc/ddd"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("a/b/c/d//"));
            Assert.Throws<ArgumentException>(() => new HierarchicalKey("//a/b/c/d//"));
            _ = new HierarchicalKey("\\/\\/");
            _ = new HierarchicalKey("aaa/bbb/ccc/ddd");
            _ = new HierarchicalKey("a/b/c/d");
            _ = new HierarchicalKey("\\/\\/a/b/c/d\\/\\/");
        }

        [Fact]
        public void GetParentTest()
        {
            var aKey = new HierarchicalKey("aaa");

            Assert.True(aKey.IsParentOf(new HierarchicalKey(aKey, "bbb")));
            Assert.True(aKey.IsPrefixOf(new HierarchicalKey("aaa/bbb/ccc")));
            Assert.True(aKey.IsParentOf(new HierarchicalKey("aaa/bbb")));
            Assert.False(aKey.IsParentOf(new HierarchicalKey("aaa/bbb/ccc")));
            Assert.False(aKey.IsPrefixOf(new HierarchicalKey("bbb/ccc")));
            Assert.False(new HierarchicalKey("a").IsPrefixOf(new HierarchicalKey("aa")));

            Assert.True(aKey.IsPrefixOf(aKey));
            Assert.False(aKey.IsParentOf(aKey));
            Assert.False(aKey.IsParentOf(new HierarchicalKey("aaa")));
            Assert.False(aKey.IsParentOf(new HierarchicalKey("bbb")));

            Assert.Null(aKey.GetParent());
            Assert.Same(aKey, new HierarchicalKey(aKey, "bbb").GetParent());
            Assert.True(new HierarchicalKey("aaa/bbb").IsChildOf(aKey));
            Assert.False(new HierarchicalKey("aaa/bbb/ccc").IsChildOf(aKey));
            Assert.Equal(new HierarchicalKey(aKey, "bbb"), new HierarchicalKey("aaa/bbb/ccc").GetParent());
            Assert.True(new HierarchicalKey("aaa/bbb").IsParentOf(new HierarchicalKey("aaa/bbb/ccc")));
            Assert.Equal(new HierarchicalKey("aaa/bbb"), new HierarchicalKey("aaa/bbb/ccc").GetParent());

            Assert.Null(new HierarchicalKey("\\/\\/").GetParent());
            Assert.Null(new HierarchicalKey("\\/").GetParent());
            Assert.Equal(new HierarchicalKey("\\/\\/"), new HierarchicalKey("\\/\\//aaa").GetParent());
            Assert.Equal(new HierarchicalKey("\\/"), new HierarchicalKey("\\//\\/").GetParent());
        }
    }
}
