using System.Diagnostics.CodeAnalysis;
using Orleans.Concurrency;
using ProtoBuf;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ValueTypeTestData")]
    public struct ValueTypeTestData
    {
        [Id(0)]
        [Newtonsoft.Json.JsonProperty]
        public int Value { get; set; }

        public ValueTypeTestData(int i)
        {
            Value = i;
        }
    }

    [Serializable]
    [GenerateSerializer]
    public enum TestEnum : byte
    {
        First,
        Second,
        Third
    }

    [Serializable]
    [GenerateSerializer]
    public enum CampaignEnemyTestType : sbyte
    {
        None = -1,
        Brute = 0,
        Enemy1,
        Enemy2,
        Enemy3
    }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ClassWithEnumTestData")]
    public class ClassWithEnumTestData
    {
        [Id(0)]
        public TestEnum EnumValue { get; set; }

        [Id(1)]
        public CampaignEnemyTestType Enemy { get; set; }
    }

    [ProtoContract]
    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.LargeTestData")]
    public class LargeTestData
    {
        [ProtoMember(1)]
        [Id(0)]
        public string TestString { get; set; }
        [ProtoMember(2)]
        [Id(1)]
        private readonly bool[] boolArray;
        [ProtoMember(3)]
        [Id(2)]
        protected Dictionary<string, int> stringIntDict;
        [ProtoMember(4)]
        [Id(3)]
        public TestEnum EnumValue { get; set; }
        [ProtoMember(5)]
        [Id(4)]
        private readonly ClassWithEnumTestData[] classArray;
        [ProtoMember(6)]
        [Id(5)]
        public string Description { get; set; }

        public LargeTestData()
        {
            boolArray = new bool[20];
            stringIntDict = new Dictionary<string, int>();
            classArray = new ClassWithEnumTestData[50];
            for (var i = 0; i < 50; i++)
            {
                classArray[i] = new ClassWithEnumTestData();
            }
        }

        public void SetBit(int n, bool value = true)
        {
            boolArray[n] = value;
        }
        public bool GetBit(int n)
        {
            return boolArray[n];
        }
        public void SetEnemy(int n, CampaignEnemyTestType enemy)
        {
            classArray[n].Enemy = enemy;
        }
        public CampaignEnemyTestType GetEnemy(int n)
        {
            return classArray[n].Enemy;
        }
        public void SetNumber(string name, int value)
        {
            stringIntDict[name] = value;
        }
        public int GetNumber(string name)
        {
            return stringIntDict[name];
        }

        // This class is not actually used anywhere. It is here to test that the serializer generator properly handles
        // nested generic classes. If it doesn't, then the generated serializer for this class will fail to compile.
        [Serializable]
        [GenerateSerializer]
        [Alias("UnitTests.GrainInterfaces.LargeTestData.NestedGeneric`1")]
        public class NestedGeneric<T>
        {
            [Id(0)]
            private T myT;
            [Id(1)]
            private string s;

            public NestedGeneric(T t)
            {
                myT = t;
                s = myT.ToString();
            }

            public override string ToString()
            {
                return s;
            }

            public void SetT(T t)
            {
                myT = t;
                s = myT.ToString();
            }
        }
    }

    [Alias("UnitTests.GrainInterfaces.IValueTypeTestGrain")]
    public interface IValueTypeTestGrain : IGrainWithGuidKey
    {
        [Alias("GetStateData")]
        Task<ValueTypeTestData> GetStateData();

        [Alias("SetStateData")]
        Task SetStateData(ValueTypeTestData d);
    }

    [Alias("UnitTests.GrainInterfaces.IRoundtripSerializationGrain")]
    public interface IRoundtripSerializationGrain : IGrainWithIntegerKey
    {
        [Alias("GetEnemyType")]
        Task<CampaignEnemyTestType> GetEnemyType();

        [Alias("GetClosedGenericValue")]
        Task<object> GetClosedGenericValue();

        [Alias("GetRetValForParamVal")]
        Task<RetVal> GetRetValForParamVal(ParamVal param);
    }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ParamVal")]
    public record ParamVal([field: Id(0)] int Value);

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.RetVal")]
    public record RetVal([field: Id(0)] int Value);

    [Serializable]
    [Immutable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ImmutableType")]
    public class ImmutableType
    {
        [Id(0)]
        private readonly int a;
        [Id(1)]
        private readonly int b;

        public int A { get { return a; } }
        public int B { get { return b; } }

        public ImmutableType(int aval, int bval)
        {
            a = aval;
            b = bval;
        }
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.EmbeddedImmutable")]
    public class EmbeddedImmutable
    {
        [Id(0)]
        public string A { get; set; }

        [Id(1)]
        private readonly Immutable<List<int>> list;
        public Immutable<List<int>> B { get { return list; } }

        public EmbeddedImmutable(string a, params int[] listOfInts)
        {
            A = a;
            var l = new List<int>();
            l.AddRange(listOfInts);
            list = new Immutable<List<int>>(l);
        }
    }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ClassWithEmbeddedImmutable")]
    public sealed class ClassWithEmbeddedImmutable
    {
        [Id(1), Immutable] public IEnumerable<byte> Immutable;
        [Id(2)] public object Mutable;
    }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.StructWithEmbeddedImmutable")]
    public struct StructWithEmbeddedImmutable
    {
        [Id(1), Immutable] public byte[] Immutable;
        [Id(2)] public byte[] Mutable;
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.DefaultActivatorValueTypeWithRequiredField")]
    public struct DefaultActivatorValueTypeWithRequiredField
    {
        [Id(0)] public required int Value;

        [SetsRequiredMembers]
        public DefaultActivatorValueTypeWithRequiredField(int value)
        {
            Value = value;
        }
    }

    [UseActivator]
    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.DefaultActivatorValueTypeWithUseActivator")]
    public struct DefaultActivatorValueTypeWithUseActivator
    {
        [Id(0)] public int Value;

        [SetsRequiredMembers]
        public DefaultActivatorValueTypeWithUseActivator()
        {
            Value = 4;
        }
    }
}