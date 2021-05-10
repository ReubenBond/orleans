using System;
using System.Linq;
using System.Net;

namespace Orleans.MetadataStore.Tests
{
    public class MetadataStoreClusteringOptions
    {
        public int MinimumNodes { get; set; }
        public IPEndPoint[] SeedNodes { get; set; }

        public override string ToString() => $"[{nameof(MinimumNodes)}: {this.MinimumNodes}, {nameof(SeedNodes)}: [{string.Join(",", this.SeedNodes?.Select(s => s.ToString()) ?? Array.Empty<string>())}]]";
    }
}
