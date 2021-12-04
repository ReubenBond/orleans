using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Legacy.CodeGeneration;
using Orleans.Legacy;
using Orleans.Runtime;
using TestExtensions;
using Xunit;
using UnitTests.Grains;
using Orleans.TestingHost;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using UnitTests.GrainInterfaces;

namespace UnitTests.CompatibilityTests
{
    [Collection("default"), TestCategory("BVT")]
    public class LegacyGrainReferenceIdentityTests
    {
        private static readonly List<(string, string)> ReferenceIdCases = new List<(string, string)>()
        {
            ("UnitTests.Grains.SimpleGrain,TestGrains", "GrainReference=0000000000000000000000007fe6662f03ffffff901fccd4"),
            ("Orleans.Providers.MemoryStreamQueueGrain,OrleansProviders", "GrainReference=000000001934fcac000000001934fcac0300000051062cef"),
            ("Orleans.Runtime.Development.DevelopmentLeaseProviderGrain,Orleans.Runtime", "GrainReference=0000000000000000000000000000000003000000380f422b"),
            ("Orleans.Runtime.Management.ManagementGrain,Orleans.Runtime", "GrainReference=00000000000000000000000000000000030000007483d9d2"),
            ("Orleans.Runtime.ReminderService.GrainBasedReminderTable,Orleans.Runtime", "GrainReference=0000000000000000000000000000303903fffffffcb3f509"),
            ("Orleans.Runtime.Versions.VersionStoreGrain,Orleans.Runtime", "GrainReference=000000000000000000000000000000000600000013bab4d8+foo"),
            ("Tester.CodeGenTests.GrainWithGenericMethods`1[[System.Int32]],DefaultCluster.Tests", "GrainReference=471c949f1f73b81ae993dc3cc11ab9bc0303c6e2875274eb GenericArguments=[System.Int32, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]"),
            ("UnitTests.GrainInterfaces.GenericGrainWithGenericState`3[[System.Int32],[System.Collections.Generic.List`1[[System.Guid]]],[System.String]],TestGrainInterfaces", "GrainReference=4de0859e1990193ca1c64617a814bf8d03d652501276e394 GenericArguments=[System.Int32, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e],[System.Collections.Generic.List`1[[System.Guid, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e],[System.String, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]"),
            ("UnitTests.Grains.BasicGenericGrain`2[[System.Int32],[System.Single]],TestGrains", "GrainReference=000000000000000000000000000000000354afca96542bd7 GenericArguments=[System.Int32, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e],[System.Single, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]"),
            ("UnitTests.Grains.GenericGrainWithContraints`3[[System.Collections.Generic.List`1[[System.Int32]]],[System.Int32],[System.String]],TestGrains", "GrainReference=0000000000000000000000000000000006826a4e584ff34d+d660b400-4c7d-479d-a0f1-47fdce09adfd GenericArguments=[System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]], System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e],[System.Int32, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e],[System.String, System.Private.CoreLib, Version=6.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]"),
        };

        private readonly TestClusterFixture _fixture;

        public LegacyGrainReferenceIdentityTests(TestClusterFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void CanTranslateGrainReferences()
        {
            var server = ((InProcessSiloHandle)_fixture.HostedCluster.Primary);
            var mapper = server.SiloHost.Services.GetRequiredService<GrainReferenceMapper>();
            var legacyRef = mapper.GetGrainReference(typeof(ISimpleGrain), 123456L);
            var newRef = mapper.ConvertGrainReference(legacyRef, typeof(ISimpleGrain));
            Assert.NotNull(newRef);
        }

        [Fact]
        public void GetGrainClassTypeCode()
        {
            var types = new List<(Type Type, int Expected)> {
                (typeof(SimpleGrain), -1876964140),
                (typeof(BasicGenericGrain<int, float>), 636094395),
                (typeof(BasicGenericGrain<string, string>), -146600331),
                (typeof(MyGrainClassWithTypeCodeOverride), 2)
            };

            foreach (var type in types)
            {
                var computed = LegacyGrainIdHelper.GetTypeCode(type.Type);
                Assert.Equal(type.Expected, computed);
            }
        }

        [Fact]
        public void CanProduceCompatibleLegacyIdentifiers()
        {
            //var newReference = _fixture.GrainFactory.GetGrain<ISimpleGrain>(56);
            //var legacyReference = new LegacyGrainReference();

            // Create a grain reference using the grain factory
            // Convert the grain reference into a legacy grain reference
            // Get the hex key for the legacy grain reference
            // Compare the key string to the input
        }

        [TypeCodeOverride(2)]
        public class MyGrainClassWithTypeCodeOverride
        {
        }
    }
}
