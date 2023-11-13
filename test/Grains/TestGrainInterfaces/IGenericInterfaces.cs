namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IGenericGrainWithGenericState`3")]
    public interface IGenericGrainWithGenericState<TFirstTypeParam, TStateType, TLastTypeParam> : IGrainWithGuidKey
    {
        [Alias("GetStateType")]
        Task<Type> GetStateType();
    }

    public class GenericGrainWithGenericState<TFirstTypeParam, TStateType, TLastTypeParam> : Grain<TStateType>,
        IGenericGrainWithGenericState<TFirstTypeParam, TStateType, TLastTypeParam> where TStateType : new()
    {
        public Task<Type> GetStateType() => Task.FromResult(this.State.GetType());
    }

    [Alias("UnitTests.GrainInterfaces.IGenericGrain`2")]
    public interface IGenericGrain<T, U> : IGrainWithIntegerKey
    {
        [Alias("SetT")]
        Task SetT(T a);
        [Alias("MapT2U")]
        Task<U> MapT2U();
    }

    [Alias("UnitTests.GrainInterfaces.ISimpleGenericGrain1`1")]
    public interface ISimpleGenericGrain1<T> : IGrainWithIntegerKey
    {
        [Alias("GetA")]
        Task<T> GetA();
        [Alias("GetAxB")]
        Task<string> GetAxB();
        [Alias("GetAxB1")]
        Task<string> GetAxB(T a, T b);
        [Alias("SetA")]
        Task SetA(T a);
        [Alias("SetB")]
        Task SetB(T b);
    }

    /// <summary>
    /// Long named grain type, which can cause issues in AzureTableStorage
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [Alias("UnitTests.GrainInterfaces.ISimpleGenericGrainUsingAzureStorageAndLongGrainName`1")]
    public interface ISimpleGenericGrainUsingAzureStorageAndLongGrainName<T> : IGrainWithGuidKey
    {
        [Alias("EchoAsync")]
        Task<T> EchoAsync(T entity);

        [Alias("ClearState")]
        Task ClearState();
    }

    /// <summary>
    /// Short named grain type, which shouldn't cause issues in AzureTableStorage
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [Alias("UnitTests.GrainInterfaces.ITinyNameGrain`1")]
    public interface ITinyNameGrain<T> : IGrainWithGuidKey
    {
        [Alias("EchoAsync")]
        Task<T> EchoAsync(T entity);

        [Alias("ClearState")]
        Task ClearState();
    }

    [Alias("UnitTests.GrainInterfaces.ISimpleGenericGrainU`1")]
    public interface ISimpleGenericGrainU<U> : IGrainWithIntegerKey
    {
        [Alias("GetA")]
        Task<U> GetA();
        [Alias("GetAxB")]
        Task<string> GetAxB();
        [Alias("GetAxB1")]
        Task<string> GetAxB(U a, U b);
        [Alias("SetA")]
        Task SetA(U a);
        [Alias("SetB")]
        Task SetB(U b);
    }

    [Alias("UnitTests.GrainInterfaces.ISimpleGenericGrain2`2")]
    public interface ISimpleGenericGrain2<T, in U> : IGrainWithIntegerKey
    {
        [Alias("GetA")]
        Task<T> GetA();
        [Alias("GetAxB")]
        Task<string> GetAxB();
        [Alias("GetAxB1")]
        Task<string> GetAxB(T a, U b);
        [Alias("SetA")]
        Task SetA(T a);
        [Alias("SetB")]
        Task SetB(U b);
    }

    [Alias("UnitTests.GrainInterfaces.IGenericGrainWithNoProperties`1")]
    public interface IGenericGrainWithNoProperties<in T> : IGrainWithIntegerKey
    {
        [Alias("GetAxB")]
        Task<string> GetAxB(T a, T b);
    }

    [Alias("UnitTests.GrainInterfaces.IGrainWithNoProperties")]
    public interface IGrainWithNoProperties : IGrainWithIntegerKey
    {
        [Alias("GetAxB")]
        Task<string> GetAxB(int a, int b);
    }

    [Alias("UnitTests.GrainInterfaces.IGrainWithListFields")]
    public interface IGrainWithListFields : IGrainWithIntegerKey
    {
        [Alias("AddItem")]
        Task AddItem(string item);
        [Alias("GetItems")]
        Task<IList<string>> GetItems();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericGrainWithListFields`1")]
    public interface IGenericGrainWithListFields<T> : IGrainWithIntegerKey
    {
        [Alias("AddItem")]
        Task AddItem(T item);
        [Alias("GetItems")]
        Task<IList<T>> GetItems();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericReader1`1")]
    public interface IGenericReader1<T> : IGrainWithIntegerKey
    {
        [Alias("GetValue")]
        Task<T> GetValue();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericWriter1`1")]
    public interface IGenericWriter1<in T> : IGrainWithIntegerKey
    {
        [Alias("SetValue")]
        Task SetValue(T value);
    }

    [Alias("UnitTests.GrainInterfaces.IGenericReaderWriterGrain1`1")]
    public interface IGenericReaderWriterGrain1<T> : IGenericWriter1<T>, IGenericReader1<T>
    {
    }

    [Alias("UnitTests.GrainInterfaces.IGenericReader2`2")]
    public interface IGenericReader2<TOne, TTwo> : IGrainWithIntegerKey
    {
        [Alias("GetValue1")]
        Task<TOne> GetValue1();
        [Alias("GetValue2")]
        Task<TTwo> GetValue2();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericWriter2`2")]
    public interface IGenericWriter2<in TOne, in TTwo> : IGrainWithIntegerKey
    {
        [Alias("SetValue1")]
        Task SetValue1(TOne value);
        [Alias("SetValue2")]
        Task SetValue2(TTwo value);
    }

    [Alias("UnitTests.GrainInterfaces.IGenericReaderWriterGrain2`2")]
    public interface IGenericReaderWriterGrain2<TOne, TTwo> : IGenericWriter2<TOne, TTwo>, IGenericReader2<TOne, TTwo>
    {
    }

    [Alias("UnitTests.GrainInterfaces.IGenericReader3`3")]
    public interface IGenericReader3<TOne, TTwo, TThree> : IGenericReader2<TOne, TTwo>
    {
        [Alias("GetValue3")]
        Task<TThree> GetValue3();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericWriter3`3")]
    public interface IGenericWriter3<in TOne, in TTwo, in TThree> : IGenericWriter2<TOne, TTwo>
    {
        [Alias("SetValue3")]
        Task SetValue3(TThree value);
    }

    [Alias("UnitTests.GrainInterfaces.IGenericReaderWriterGrain3`3")]
    public interface IGenericReaderWriterGrain3<TOne, TTwo, TThree> : IGenericWriter3<TOne, TTwo, TThree>, IGenericReader3<TOne, TTwo, TThree>
    {
    }

    [Alias("UnitTests.GrainInterfaces.IBasicGenericGrain`2")]
    public interface IBasicGenericGrain<T, U> : IGrainWithIntegerKey
    {
        [Alias("GetA")]
        Task<T> GetA();
        [Alias("GetAxB")]
        Task<string> GetAxB();
        [Alias("GetAxB1")]
        Task<string> GetAxB(T a, U b);
        [Alias("SetA")]
        Task SetA(T a);
        [Alias("SetB")]
        Task SetB(U b);
    }

    [Alias("UnitTests.GrainInterfaces.IHubGrain`3")]
    public interface IHubGrain<TKey, T1, T2> : IGrainWithIntegerKey
    {
        [Alias("Bar")]
        Task Bar(TKey key, T1 message1, T2 message2);

    }

    [Alias("UnitTests.GrainInterfaces.IEchoHubGrain`2")]
    public interface IEchoHubGrain<TKey, TMessage> : IHubGrain<TKey, TMessage, TMessage>
    {
        [Alias("Foo")]
        Task Foo(TKey key, TMessage message, int x);
        [Alias("GetX")]
        Task<int> GetX();
    }

    [Alias("UnitTests.GrainInterfaces.IEchoGenericChainGrain`1")]
    public interface IEchoGenericChainGrain<T> : IGrainWithIntegerKey
    {
        [Alias("Echo")]
        Task<T> Echo(T item);
        [Alias("Echo2")]
        Task<T> Echo2(T item);
        [Alias("Echo3")]
        Task<T> Echo3(T item);
        [Alias("Echo4")]
        Task<T> Echo4(T item);
        [Alias("Echo5")]
        Task<T> Echo5(T item);
        [Alias("Echo6")]
        Task<T> Echo6(T item);
    }

    [Alias("UnitTests.GrainInterfaces.INonGenericBase")]
    public interface INonGenericBase : IGrainWithGuidKey
    {
        [Alias("Ping")]
        Task Ping();
    }

    [Alias("UnitTests.GrainInterfaces.IGeneric1Argument`1")]
    public interface IGeneric1Argument<T> : IGrainWithGuidKey
    {
        [Alias("Ping")]
        Task<T> Ping(T t);
    }

    [Alias("UnitTests.GrainInterfaces.IGeneric2Arguments`2")]
    public interface IGeneric2Arguments<T, U> : IGrainWithIntegerKey
    {
        [Alias("Ping")]
        Task<Tuple<T, U>> Ping(T t, U u);
    }

    [Alias("UnitTests.GrainInterfaces.IDbGrain`1")]
    public interface IDbGrain<T> : IGrainWithIntegerKey
    {
        [Alias("SetValue")]
        Task SetValue(T value);
        [Alias("GetValue")]
        Task<T> GetValue();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericPingSelf`1")]
    public interface IGenericPingSelf<T> : IGrainWithGuidKey
    {
        [Alias("Ping")]
        Task<T> Ping(T t);
        [Alias("PingSelf")]
        Task<T> PingSelf(T t);
        [Alias("PingOther")]
        Task<T> PingOther(IGenericPingSelf<T> target, T t);
        [Alias("PingSelfThroughOther")]
        Task<T> PingSelfThroughOther(IGenericPingSelf<T> target, T t);
        [Alias("GetLastValue")]
        Task<T> GetLastValue();
        [Alias("ScheduleDelayedPing")]
        Task ScheduleDelayedPing(IGenericPingSelf<T> target, T t, TimeSpan delay);
        [Alias("ScheduleDelayedPingToSelfAndDeactivate")]
        Task ScheduleDelayedPingToSelfAndDeactivate(IGenericPingSelf<T> target, T t, TimeSpan delay);
    }

    [Alias("UnitTests.GrainInterfaces.ILongRunningTaskGrain`1")]
    public interface ILongRunningTaskGrain<T> : IGrainWithGuidKey
    {
        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();
        [Alias("GetRuntimeInstanceIdWithDelay")]
        Task<string> GetRuntimeInstanceIdWithDelay(TimeSpan delay);

        [Alias("LongWait")]
        Task LongWait(GrainCancellationToken tc, TimeSpan delay);
        [Alias("LongRunningTask")]
        Task<T> LongRunningTask(T t, TimeSpan delay);
        [Alias("CallOtherLongRunningTask")]
        Task<T> CallOtherLongRunningTask(ILongRunningTaskGrain<T> target, T t, TimeSpan delay);
        [Alias("FanOutOtherLongRunningTask")]
        Task<T> FanOutOtherLongRunningTask(ILongRunningTaskGrain<T> target, T t, TimeSpan delay, int degreeOfParallelism);
        [Alias("CallOtherLongRunningTask1")]
        Task CallOtherLongRunningTask(ILongRunningTaskGrain<T> target, GrainCancellationToken tc, TimeSpan delay);
        [Alias("CallOtherLongRunningTaskWithLocalToken")]
        Task CallOtherLongRunningTaskWithLocalToken(ILongRunningTaskGrain<T> target, TimeSpan delay,
            TimeSpan delayBeforeCancel);
        [Alias("CancellationTokenCallbackResolve")]
        Task<bool> CancellationTokenCallbackResolve(GrainCancellationToken tc);
        [Alias("CallOtherCancellationTokenCallbackResolve")]
        Task<bool> CallOtherCancellationTokenCallbackResolve(ILongRunningTaskGrain<T> target);
        [Alias("CancellationTokenCallbackThrow")]
        Task CancellationTokenCallbackThrow(GrainCancellationToken tc);
        [Alias("GetLastValue")]
        Task<T> GetLastValue();
    }

    [Alias("IGenericGrainWithConstraints`3")]
    public interface IGenericGrainWithConstraints<A, B, C> : IGrainWithStringKey
        where A : ICollection<B>, new() where B : struct where C : class
    {
        [Alias("GetCount")]
        Task<int> GetCount();

        [Alias("Add")]
        Task Add(B item);

        [Alias("RoundTrip")]
        Task<C> RoundTrip(C value);
    }

    [Alias("UnitTests.GrainInterfaces.INonGenericCastableGrain")]
    public interface INonGenericCastableGrain : IGrainWithGuidKey
    {
        [Alias("DoSomething")]
        Task DoSomething();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericCastableGrain`1")]
    public interface IGenericCastableGrain<T> : IGrainWithGuidKey
    { }

    [Alias("UnitTests.GrainInterfaces.IGenericRegisterGrain`1")]
    public interface IGenericRegisterGrain<T> : IGrainWithIntegerKey
    {
        [Alias("Set")]
        Task Set(T value);
        [Alias("Get")]
        Task<T> Get();
    }

    [Alias("UnitTests.GrainInterfaces.IGenericArrayRegisterGrain`1")]
    public interface IGenericArrayRegisterGrain<T> : IGenericRegisterGrain<T[]>
    {
    }

    [Alias("UnitTests.GrainInterfaces.IGrainSayingHello")]
    public interface IGrainSayingHello : IGrainWithGuidKey
    {
        [Alias("Hello")]
        Task<string> Hello();
    }

    [Alias("UnitTests.GrainInterfaces.ISomeGenericGrain`1")]
    public interface ISomeGenericGrain<T> : IGrainSayingHello
    { }

    [Alias("UnitTests.GrainInterfaces.INonGenericCastGrain")]
    public interface INonGenericCastGrain : IGrainSayingHello
    { }

    [Alias("UnitTests.GrainInterfaces.IIndependentlyConcretizedGrain")]
    public interface IIndependentlyConcretizedGrain : ISomeGenericGrain<string>
    { }

    [Alias("UnitTests.GrainInterfaces.IIndependentlyConcretizedGenericGrain`1")]
    public interface IIndependentlyConcretizedGenericGrain<T> : ISomeGenericGrain<T>
    { }


    namespace Generic.EdgeCases
    {
        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IBasicGrain")]
        public interface IBasicGrain : IGrainWithGuidKey
        {
            [Alias("Hello")]
            Task<string> Hello();
            [Alias("ConcreteGenArgTypeNames")]
            Task<string[]> ConcreteGenArgTypeNames();
        }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IGrainWithTwoGenArgs`2")]
        public interface IGrainWithTwoGenArgs<T1, T2> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IGrainWithThreeGenArgs`3")]
        public interface IGrainWithThreeGenArgs<T1, T2, T3> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IGrainReceivingRepeatedGenArgs`2")]
        public interface IGrainReceivingRepeatedGenArgs<T1, T2> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IPartiallySpecifyingInterface`1")]
        public interface IPartiallySpecifyingInterface<T> : IGrainWithTwoGenArgs<T, int>
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IReceivingRepeatedGenArgsAmongstOthers`3")]
        public interface IReceivingRepeatedGenArgsAmongstOthers<T1, T2, T3> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IReceivingRepeatedGenArgsFromOtherInterface`3")]
        public interface IReceivingRepeatedGenArgsFromOtherInterface<T1, T2, T3> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.ISpecifyingGenArgsRepeatedlyToParentInterface`1")]
        public interface ISpecifyingGenArgsRepeatedlyToParentInterface<T> : IReceivingRepeatedGenArgsFromOtherInterface<T, T, T>
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IReceivingRearrangedGenArgs`2")]
        public interface IReceivingRearrangedGenArgs<T1, T2> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IReceivingRearrangedGenArgsViaCast`2")]
        public interface IReceivingRearrangedGenArgsViaCast<T1, T2> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.ISpecifyingRearrangedGenArgsToParentInterface`2")]
        public interface ISpecifyingRearrangedGenArgsToParentInterface<T1, T2> : IReceivingRearrangedGenArgsViaCast<T2, T1>
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IArbitraryInterface`2")]
        public interface IArbitraryInterface<T1, T2> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IInterfaceUnrelatedToConcreteGenArgs`1")]
        public interface IInterfaceUnrelatedToConcreteGenArgs<T> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IInterfaceTakingFurtherSpecializedGenArg`1")]
        public interface IInterfaceTakingFurtherSpecializedGenArg<T> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IAnotherReceivingFurtherSpecializedGenArg`1")]
        public interface IAnotherReceivingFurtherSpecializedGenArg<T> : IBasicGrain
        { }

        [Alias("UnitTests.GrainInterfaces.Generic.EdgeCases.IYetOneMoreReceivingFurtherSpecializedGenArg`1")]
        public interface IYetOneMoreReceivingFurtherSpecializedGenArg<T> : IBasicGrain
        { }
    }

    [Alias("UnitTests.GrainInterfaces.IG2`2")]
    public interface IG2<T1, T2> : IGrainWithGuidKey
    { }

    public class HalfOpenGrain1<T> : IG2<T, int>
    { }
    public class HalfOpenGrain2<T> : IG2<int, T>
    { }

    public class OpenGeneric<T2, T1> : IG2<T2, T1>
    { }

    public class ClosedGeneric : IG2<Dummy1, Dummy2>
    { }

    public class ClosedGenericWithManyInterfaces : IG2<Dummy1, Dummy2>, IG2<Dummy2, Dummy1>
    { }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.Dummy1")]
    public class Dummy1 { }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.Dummy2")]
    public class Dummy2 { }

    [Alias("UnitTests.GrainInterfaces.IG`1")]
    public interface IG<T> : IGrain
    {
    }

    public class G1<T1, T2, T3, T4> : Grain, Root<T1>.IA<T2, T3, T4>
    {
    }

    public class Root<TRoot>
    {
        [Alias("UnitTests.GrainInterfaces.Root.IA`4")]
        public interface IA<T1, T2, T3> : IGrainWithIntegerKey
        {

        }

        public class G<T1, T2, T3> : Grain, IG<IA<T1, T2, T3>>
        {
        }
    }
}
