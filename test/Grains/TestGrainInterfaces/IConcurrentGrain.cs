namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IConcurrentGrain")]
    public interface IConcurrentGrain : IGrainWithIntegerKey
    {
        [Alias("Initialize")]
        Task Initialize(int index);

        //[ReadOnly]
        [Alias("A")]
        Task<int> A();

        //[ReadOnly]
        [Alias("B")]
        Task<int> B(int time);

        [Alias("ModifyReturnList_Test")]
        Task<List<int>> ModifyReturnList_Test();

        [Alias("Initialize_2")]
        Task Initialize_2(int index);
        [Alias("TailCall_Caller")]
        Task<int> TailCall_Caller(IConcurrentReentrantGrain another, bool doCW);
        [Alias("TailCall_Resolver")]
        Task<int> TailCall_Resolver(IConcurrentReentrantGrain another);
    }

    [Alias("UnitTests.GrainInterfaces.IConcurrentReentrantGrain")]
    public interface IConcurrentReentrantGrain : IGrainWithIntegerKey
    {
        [Alias("Initialize_2")]
        Task Initialize_2(int index);
        [Alias("TailCall_Called")]
        Task<int> TailCall_Called();
        [Alias("TailCall_Resolve")]
        Task<int> TailCall_Resolve();
    }
}
