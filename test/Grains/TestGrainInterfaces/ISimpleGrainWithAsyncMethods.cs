namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISimpleGrainAsync")]
    public interface ISimpleGrainAsync : IGrainWithIntegerKey
    {
        [Alias("SetA_Async")]
        Task SetA_Async(int a);
        [Alias("SetB_Async")]
        Task SetB_Async(int b);
        [Alias("GetAxB_Async")]
        Task<int> GetAxB_Async();
        [Alias("GetAxB_Async1")]
        Task<int> GetAxB_Async(int a, int b);
        [Alias("GetA_Async")]
        Task<int> GetA_Async();
        [Alias("IncrementA_Async")]
        Task IncrementA_Async();
    }

    [Alias("UnitTests.GrainInterfaces.ISimpleGrainWithAsyncMethods")]
    public interface ISimpleGrainWithAsyncMethods : ISimpleGrainAsync
    {
        [Alias("GetX")]
        Task<int> GetX();
        [Alias("SetX")]
        Task SetX(int x);
    }
}
