using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.StorageTests.Relational.TestDataSets
{
    internal sealed class StorageDataSetGeneric<TGrainKey, TStateData> : IEnumerable<object[]>
    {
        public record TestData(string GrainType, Func<IInternalGrainFactory, GrainReference> GrainGetter, GrainState<TestStateGeneric1<TStateData>> GrainState);

        public static TestData[] Data { get; } = new TestData[]
        {
            new TestData(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data1", B = 1, C = 4 } }),
            new TestData(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data2", B = 2, C = 5 } }),
            new TestData(
                GrainTypeGenerator.GetGrainType<TGrainKey>(),
                grainFactory => RandomUtilities.GetRandomGrainReference<TGrainKey>(grainFactory),
                new GrainState<TestStateGeneric1<TStateData>> { State = new TestStateGeneric1<TStateData> { SomeData = RandomUtilities.GetRandom<TStateData>(), A = "Data3", B = 3, C = 6 } })
        };

        public IEnumerator<object[]> GetEnumerator() => Enumerable.Range(0, Data.Length).Select(n => new object[] { n }).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
