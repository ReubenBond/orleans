using System;
using System.Collections.Generic;
using Orleans;
using Orleans.Legacy;
using Orleans.Runtime;
using Xunit;
using UnitTests.Grains;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using UnitTests.GrainInterfaces;
using Xunit.Abstractions;
using System.Threading.Tasks;

namespace UnitTests.CompatibilityTests
{
    [Collection("default"), TestCategory("BVT")]
    public class LegacyGrainReferenceIdentityTests
    {
        private sealed class GrainReferenceTestCase
        {
            public Func<IGrainFactory, IAddressable> GetNewReference { get; init; }
            public Func<GrainReferenceMapper, LegacyGrainReference> GetOldReference { get; init; }
            public Type GrainClass { get; init; }
        }

        private static readonly List<GrainReferenceTestCase> GrainReferenceTestCases = new()
        {
            new()
            {
                GetNewReference = grainFactory => grainFactory.GetGrain<ISimpleGrain>(0x7FE6662FL),
                GetOldReference = mapper => mapper.GetGrainReference(typeof(ISimpleGrain), 0x7FE6662FL),
                GrainClass = typeof(SimpleGrain)
            },
            new()
            {
                GetNewReference = grainFactory => grainFactory.GetGrain<IGenericGrainWithGenericState<int, List<Guid>, string>>(Guid.Parse("1990193c-859e-4de0-8dbf-14a81746c6a1")),
                GetOldReference = mapper => mapper.GetGrainReference(typeof(IGenericGrainWithGenericState<int, List<Guid>, string>), Guid.Parse("1990193c-859e-4de0-8dbf-14a81746c6a1")),
                GrainClass = typeof(GenericGrainWithGenericState<int, List<Guid>, string>)
            },
            new()
            {
                GetNewReference = grainFactory => grainFactory.GetGrain<IBasicGenericGrain<int, float>>(0),
                GetOldReference = mapper => mapper.GetGrainReference(typeof(IBasicGenericGrain<int, float>), 0),
                GrainClass = typeof(BasicGenericGrain<int, float>)
            },
            new()
            {
                GetNewReference = grainFactory => grainFactory.GetGrain<IGenericGrainWithConstraints<List<int>, int, string>>("d660b400-4c7d-479d-a0f1-47fdce09adfd"),
                GetOldReference = mapper => mapper.GetGrainReference(typeof(IGenericGrainWithConstraints<List<int>, int, string>), "d660b400-4c7d-479d-a0f1-47fdce09adfd"),
                GrainClass = typeof(GenericGrainWithContraints<List<int>, int, string>)
            }
        };

        private readonly TestClusterFixture _fixture;
        private readonly ITestOutputHelper _output;

        public LegacyGrainReferenceIdentityTests(TestClusterFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        public void CanTranslateSimpleGrainReference()
        {
            var server = (InProcessSiloHandle)_fixture.HostedCluster.Primary;
            var mapper = server.SiloHost.Services.GetRequiredService<GrainReferenceMapper>();
            var legacyRef = mapper.GetGrainReference(typeof(ISimpleGrain), 123456L);
            var legacyRefConvertedToNewRef = mapper.ConvertGrainReference(legacyRef, typeof(ISimpleGrain));
            Assert.NotNull(legacyRefConvertedToNewRef);

            var roundTrippedReference = mapper.ConvertGrainReference(legacyRefConvertedToNewRef, typeof(SimpleGrain));
            Assert.Equal(legacyRef, roundTrippedReference);
        }

        [Fact]
        public void ConvertGrainReferences()
        {
            var server = (InProcessSiloHandle)_fixture.HostedCluster.Primary;
            var services = server.SiloHost.Services;
            var mapper = services.GetRequiredService<GrainReferenceMapper>();
            var grainFactory = services.GetRequiredService<IGrainFactory>();
            foreach (var test in GrainReferenceTestCases)
            {
                // Create a legacy reference and a modern reference which point to the same logical grain
                var oldRef = test.GetOldReference(mapper);
                var newRef = test.GetNewReference(grainFactory);
                Assert.NotNull(oldRef);
                Assert.NotNull(newRef);

                // Convert the modern reference into a legacy reference
                var newRefConvertedToOld = mapper.ConvertGrainReference((GrainReference)newRef, test.GrainClass);
                Assert.NotNull(newRefConvertedToOld);

                // Compare the new and old references.
                Assert.Equal(oldRef.GrainId, newRefConvertedToOld.GrainId);
                Assert.Equal(oldRef, newRefConvertedToOld);

                // Obtain a string key (used in persistence) and compare them.
                var expectedKeyString = oldRef.ToKeyString();
                var actualKeyString = newRefConvertedToOld.ToKeyString();
                Assert.Equal(expectedKeyString, actualKeyString);
            }
        }

        /*

        private readonly Guid ClientId = Guid.NewGuid();
        private readonly Guid ObserverId = Guid.NewGuid();

        private static readonly SimpleGrainObserver _simpleGrainObserver = new();
        private class SimpleGrainObserver : ISimpleGrainObserver
        {
            public void StateChanged(int a, int b) => throw new NotImplementedException();
        }
        [Fact]
        public async Task ConvertObserverReferences()
        {
            var server = (InProcessSiloHandle)_fixture.HostedCluster.Primary;
            var services = server.SiloHost.Services;
            var mapper = services.GetRequiredService<GrainReferenceMapper>();
            var grainFactory = services.GetRequiredService<IGrainFactory>();

            var newRef = await grainFactory.CreateObjectReference<ISimpleGrainObserver>(_simpleGrainObserver);
            var convertedNewToOld = mapper.ConvertGrainReference((GrainReference)newRef, typeof(ISimpleGrainObserver));

            var old
            // Create a legacy reference and a modern reference which point to the same logical grain
            var oldRef = test.GetOldReference(mapper);
            var newRef = test.GetNewReference(grainFactory);
            Assert.NotNull(oldRef);
            Assert.NotNull(newRef);

            // Convert the modern reference into a legacy reference
            var newRefConvertedToOld = mapper.ConvertGrainReference((GrainReference)newRef, test.GrainClass);
            Assert.NotNull(newRefConvertedToOld);

            // Compare the new and old references.
            Assert.Equal(oldRef.GrainId, newRefConvertedToOld.GrainId);
            Assert.Equal(oldRef, newRefConvertedToOld);

            // Obtain a string key (used in persistence) and compare them.
            var expectedKeyString = oldRef.ToKeyString();
            var actualKeyString = newRefConvertedToOld.ToKeyString();
            Assert.Equal(expectedKeyString, actualKeyString);
        }
        */
        /*
        [Fact]
        public void CanTranslateGrainReferencesFromKeyString()
        {
            var server = ((InProcessSiloHandle)_fixture.HostedCluster.Primary);
            var services = server.SiloHost.Services;
            var mapper = services.GetRequiredService<GrainReferenceMapper>();
            var grainFactory = services.GetRequiredService<IGrainFactory>();
            foreach (var (func, grainClass, keyString) in ReferenceIdCases)
            {
                _output.WriteLine(keyString);
                var reference = LegacyGrainReference.FromKeyString(keyString);
                Assert.NotNull(reference);

                var newReference = func(grainFactory);
                Assert.NotNull(newReference);

                // Convert the new reference into a legacy reference
                var newRefConvertedToLegacy = mapper.ConvertGrainReference((GrainReference)newReference, grainClass);
                Assert.NotNull(newRefConvertedToLegacy);
                var actualKeyString = newRefConvertedToLegacy.ToKeyString();

                //Assert.Equal(keyString, actualKeyString);

                // Compare the converted reference with the original
            }
        }
        */

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
