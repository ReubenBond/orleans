namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISimpleGrain")]
    public interface ISimpleGrain : IGrainWithIntegerKey
    {
        [Alias("SetA")]
        Task SetA(int a);
        [Alias("SetB")]
        Task SetB(int b);
        [Alias("IncrementA")]
        Task IncrementA();
        [Alias("GetAxB")]
        Task<int> GetAxB();
        [Alias("GetAxB1")]
        Task<int> GetAxB(int a, int b);
        [Alias("GetA")]
        Task<int> GetA();
    }
}
