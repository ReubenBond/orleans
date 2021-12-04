using System;
using Orleans.Serialization.TypeSystem;
using Xunit;
using Xunit.Abstractions;

namespace BackwardCompatibility.Tests
{
    [TestCategory("BVT")]
    public class GrainReferenceSerializationCompatTests
    {
        private readonly ITestOutputHelper _output;

        public GrainReferenceSerializationCompatTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CanDeserializeGrainReferences()
        {
        }
    }
}
