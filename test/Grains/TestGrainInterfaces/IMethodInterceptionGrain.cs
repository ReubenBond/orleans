namespace UnitTests.GrainInterfaces
{
    using System;
    using Orleans;
    using Orleans.Runtime;

    [GrainInterfaceType("method-interception-custom-name")]
    [Alias("UnitTests.GrainInterfaces.IMethodInterceptionGrain")]
    public interface IMethodInterceptionGrain : IGrainWithIntegerKey, IMethodFromAnotherInterface
    {
        [Id(14142)]
        [Alias("One")]
        Task<string> One();

        [Id(4142)]
        [Alias("Echo")]
        Task<string> Echo(string someArg);
        [Alias("NotIntercepted")]
        Task<string> NotIntercepted();
        [Alias("Throw")]
        Task<string> Throw();
        [Alias("IncorrectResultType")]
        Task<string> IncorrectResultType();
        [Alias("FilterThrows")]
        Task FilterThrows();

        [Alias("SystemWideCallFilterMarker")]
        Task SystemWideCallFilterMarker();
    }

    [GrainInterfaceType("custom-outgoing-interception-grain")]
    [Alias("UnitTests.GrainInterfaces.IOutgoingMethodInterceptionGrain")]
    public interface IOutgoingMethodInterceptionGrain : IGrainWithIntegerKey
    {
        [Alias("EchoViaOtherGrain")]
        Task<Dictionary<string, object>> EchoViaOtherGrain(IMethodInterceptionGrain otherGrain, string message);
        [Alias("ThrowIfGreaterThanZero")]
        Task<string> ThrowIfGreaterThanZero(int value);
    }

    [Alias("UnitTests.GrainInterfaces.IGenericMethodInterceptionGrain`1")]
    public interface IGenericMethodInterceptionGrain<in T> : IGrainWithIntegerKey, IMethodFromAnotherInterface
    {
        //[Alias("GetInputAsString")]
        Task<string> GetInputAsString(T input);
    }

    public interface IMethodFromAnotherInterface
    {
        Task<string> SayHello();
    }

    [Alias("UnitTests.GrainInterfaces.ITrickyMethodInterceptionGrain")]
    public interface ITrickyMethodInterceptionGrain : IGenericMethodInterceptionGrain<string>, IGenericMethodInterceptionGrain<bool>
    {
        [Alias("GetBestNumber")]
        Task<int> GetBestNumber();
    }

    public static class GrainCallFilterTestConstants
    {
        public const string Key = "GrainInfo";
    }

    [Alias("UnitTests.GrainInterfaces.IGrainCallFilterTestGrain")]
    public interface IGrainCallFilterTestGrain : IGrainWithIntegerKey
    {
        [Alias("ThrowIfGreaterThanZero")]
        Task<string> ThrowIfGreaterThanZero(int value);
        [Alias("GetRequestContext")]
        Task<string> GetRequestContext();

        [Alias("SumSet")]
        Task<int> SumSet(HashSet<int> numbers);

        [Alias("SystemWideCallFilterMarker")]
        Task SystemWideCallFilterMarker();
        [Alias("GrainSpecificCallFilterMarker")]
        Task GrainSpecificCallFilterMarker();
    }

    [Alias("UnitTests.GrainInterfaces.IHungryGrain`1")]
    public interface IHungryGrain<T> : IGrainWithIntegerKey
    {
        [TestMethodTag("hungry-eat")]
        [Alias("Eat")]
        Task Eat(T food);

        [TestMethodTag("hungry-eatwith")]
        [Alias("EatWith")]
        Task EatWith<U>(T food, U condiment);
    }

    [Alias("UnitTests.GrainInterfaces.IOmnivoreGrain")]
    public interface IOmnivoreGrain : IGrainWithIntegerKey
    {
        [TestMethodTag("omnivore-eat")]
        [Alias("Eat")]
        Task Eat<T>(T food);
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.Apple")]
    public class Apple { }

    [Alias("UnitTests.GrainInterfaces.ICaterpillarGrain")]
    public interface ICaterpillarGrain : IHungryGrain<Apple>, IOmnivoreGrain
    {
        [TestMethodTag("caterpillar-eat")]
        [Alias("Eat")]
        new Task Eat<T>(T food);
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class TestMethodTagAttribute : Attribute
    {
        public TestMethodTagAttribute(string tag) => this.Tag = tag;
        public string Tag { get; }
    }
}
