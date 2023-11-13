// ReSharper disable InconsistentNaming

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IServiceIdGrain")]
    public interface IServiceIdGrain : IGrainWithGuidKey
    {
        [Alias("GetServiceId")]
        Task<string> GetServiceId();
    }

    [Alias("UnitTests.GrainInterfaces.IPersistenceTestGrain")]
    public interface IPersistenceTestGrain : IGrainWithGuidKey
    {
        [Alias("CheckStateInit")]
        Task<bool> CheckStateInit();
        [Alias("CheckProviderType")]
        Task<string> CheckProviderType();
        [Alias("DoSomething")]
        Task DoSomething();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IPersistenceTestGenericGrain`1")]
    public interface IPersistenceTestGenericGrain<T> : IPersistenceTestGrain // IGrainWithGuidKey
    { }

    //    Task<bool> CheckStateInit();
    //    Task<string> CheckProviderType();
    //    Task DoSomething();
    //    Task DoWrite(int val);
    //    Task<int> DoRead();
    //    Task<int> GetValue();
    //    Task DoDelete();
    //}

    [Alias("UnitTests.GrainInterfaces.IMemoryStorageTestGrain")]
    public interface IMemoryStorageTestGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IGrainStorageTestGrain")]
    public interface IGrainStorageTestGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IGrainStorageGenericGrain`1")]
    public interface IGrainStorageGenericGrain<T> : IGrainWithIntegerKey
    {
        [Alias("GetValue")]
        Task<T> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(T val);
        [Alias("DoRead")]
        Task<T> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IGrainStorageTestGrain_GuidExtendedKey")]
    public interface IGrainStorageTestGrain_GuidExtendedKey : IGrainWithGuidCompoundKey
    {
        [Alias("GetExtendedKeyValue")]
        Task<string> GetExtendedKeyValue();
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IGrainStorageTestGrain_LongKey")]
    public interface IGrainStorageTestGrain_LongKey : IGrainWithIntegerKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IGrainStorageTestGrain_LongExtendedKey")]
    public interface IGrainStorageTestGrain_LongExtendedKey : IGrainWithIntegerCompoundKey
    {
        [Alias("GetExtendedKeyValue")]
        Task<string> GetExtendedKeyValue();
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IAWSStorageTestGrain")]
    public interface IAWSStorageTestGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IAWSStorageGenericGrain`1")]
    public interface IAWSStorageGenericGrain<T> : IGrainWithIntegerKey
    {
        [Alias("GetValue")]
        Task<T> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(T val);
        [Alias("DoRead")]
        Task<T> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IAWSStorageTestGrain_GuidExtendedKey")]
    public interface IAWSStorageTestGrain_GuidExtendedKey : IGrainWithGuidCompoundKey
    {
        [Alias("GetExtendedKeyValue")]
        Task<string> GetExtendedKeyValue();
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IAWSStorageTestGrain_LongKey")]
    public interface IAWSStorageTestGrain_LongKey : IGrainWithIntegerKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IAWSStorageTestGrain_LongExtendedKey")]
    public interface IAWSStorageTestGrain_LongExtendedKey : IGrainWithIntegerCompoundKey
    {
        [Alias("GetExtendedKeyValue")]
        Task<string> GetExtendedKeyValue();
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoDelete")]
        Task DoDelete();
    }

    [Alias("UnitTests.GrainInterfaces.IPersistenceErrorGrain")]
    public interface IPersistenceErrorGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoWriteError")]
        Task DoWriteError(int val, bool errorBeforeWrite);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("DoReadError")]
        Task<int> DoReadError(bool errorBeforeRead);
    }

    [Alias("UnitTests.GrainInterfaces.IPersistenceProviderErrorGrain")]
    public interface IPersistenceProviderErrorGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val);
        [Alias("DoRead")]
        Task<int> DoRead();
        [Alias("GetActivationId")]
        Task<string> GetActivationId();
    }

    [Alias("UnitTests.GrainInterfaces.IPersistenceProviderErrorProxyGrain")]
    public interface IPersistenceProviderErrorProxyGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue(IPersistenceProviderErrorGrain other);
        [Alias("DoWrite")]
        Task DoWrite(int val, IPersistenceProviderErrorGrain other);
        [Alias("DoRead")]
        Task<int> DoRead(IPersistenceProviderErrorGrain other);
        [Alias("GetActivationId")]
        Task<string> GetActivationId();
    }

    [Alias("UnitTests.GrainInterfaces.IPersistenceUserHandledErrorGrain")]
    public interface IPersistenceUserHandledErrorGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("DoWrite")]
        Task DoWrite(int val, bool recover);
        [Alias("DoRead")]
        Task<int> DoRead(bool recover);
    }

    [Alias("UnitTests.GrainInterfaces.IBadProviderTestGrain")]
    public interface IBadProviderTestGrain : IGrainWithGuidKey
    {
        [Alias("DoSomething")]
        Task DoSomething();
    }

    [Alias("UnitTests.GrainInterfaces.IPersistenceNoStateTestGrain")]
    public interface IPersistenceNoStateTestGrain : IGrainWithGuidKey
    {
        [Alias("DoSomething")]
        Task DoSomething();
    }

    [Alias("UnitTests.GrainInterfaces.IUser")]
    public interface IUser : IGrainWithGuidKey
    {
        [Alias("GetName")]
        Task<string> GetName();
        [Alias("GetStatus")]
        Task<string> GetStatus();

        [Alias("UpdateStatus")]
        Task UpdateStatus(string status);
        [Alias("SetName")]
        Task SetName(string name);
        [Alias("AddFriend")]
        Task AddFriend(IUser friend);
        [Alias("GetFriends")]
        Task<List<IUser>> GetFriends();
        [Alias("GetFriendsStatuses")]
        Task<string> GetFriendsStatuses();
    }

    [Alias("UnitTests.GrainInterfaces.IReentrentGrainWithState")]
    public interface IReentrentGrainWithState : IGrainWithGuidKey
    {
        [Alias("Setup")]
        Task Setup(IReentrentGrainWithState other);
        [Alias("Test1")]
        Task Test1();
        [Alias("Test2")]
        Task Test2();
        [Alias("SetOne")]
        Task SetOne(int val);
        [Alias("SetTwo")]
        Task SetTwo(int val);
        [Alias("Task_Delay")]
        Task Task_Delay(bool doStart);
    }

    [Alias("UnitTests.GrainInterfaces.INonReentrentStressGrainWithoutState")]
    public interface INonReentrentStressGrainWithoutState : IGrainWithGuidKey
    {
        [Alias("Test1")]
        Task Test1();
        [Alias("Task_Delay")]
        Task Task_Delay(bool doStart);
    }

    [Alias("UnitTests.GrainInterfaces.IInternalGrainWithState")]
    public interface IInternalGrainWithState : IGrainWithIntegerKey
    {
        [Alias("SetOne")]
        Task SetOne(int val);
    }

    [Alias("UnitTests.GrainInterfaces.IStateInheritanceTestGrain")]
    public interface IStateInheritanceTestGrain : IGrainWithGuidKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        [Alias("SetValue")]
        Task SetValue(int val);
    }

    public interface IMyPredicate
    {
        bool FilterFunc(int i);
    }

    [Serializable]
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.MyPredicate")]
    public class MyPredicate : IMyPredicate
    {
        [Id(0)]
        private readonly int filterValue;

        public MyPredicate(int filter)
        {
            this.filterValue = filter;
        }

        public bool FilterFunc(int i)
        {
            return i == filterValue;

        }
    }

    [Alias("UnitTests.GrainInterfaces.ISurrogateStateForTypeWithoutPublicConstructorGrain`1")]
    public interface ISurrogateStateForTypeWithoutPublicConstructorGrain<T> : IGrainWithGuidKey
        where T : class
    {
        [Alias("SetState")]
        Task SetState(T state);
        [Alias("GetState")]
        Task<T> GetState();
    }

    [Alias("UnitTests.GrainInterfaces.IRecordTypeWithoutPublicParameterlessConstructorGrain`1")]
    public interface IRecordTypeWithoutPublicParameterlessConstructorGrain<T> : IGrainWithGuidKey
        where T : class
    {
        [Alias("SetState")]
        Task SetState(T state);
        [Alias("GetState")]
        Task<T> GetState();
    }
}
// ReSharper restore InconsistentNaming
