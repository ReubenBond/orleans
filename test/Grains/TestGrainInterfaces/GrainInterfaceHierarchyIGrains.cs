namespace TestGrainInterfaces
{
    public interface IDoSomething
    {
        Task<string> DoIt();

        Task SetA(int a);

        Task IncrementA();

        Task<int> GetA();
    }

    [Alias("TestGrainInterfaces.IDoSomethingWithMoreGrain")]
    public interface IDoSomethingWithMoreGrain : IDoSomething, IGrainWithIntegerKey
    {
        [Alias("DoThat")]
        Task<string> DoThat();

        [Alias("SetB")]
        Task SetB(int a);

        [Alias("IncrementB")]
        Task IncrementB();

        [Alias("GetB")]
        Task<int> GetB();
    }

    [Alias("TestGrainInterfaces.IDoSomethingEmptyGrain")]
    public interface IDoSomethingEmptyGrain : IDoSomething, IGrainWithIntegerKey
    {
    }

    [Alias("TestGrainInterfaces.IDoSomethingEmptyWithMoreGrain")]
    public interface IDoSomethingEmptyWithMoreGrain : IDoSomethingEmptyGrain
    {
        [Alias("DoMore")]
        Task<string> DoMore();
    }

    [Alias("TestGrainInterfaces.IDoSomethingWithMoreEmptyGrain")]
    public interface IDoSomethingWithMoreEmptyGrain : IDoSomethingEmptyWithMoreGrain
    {
    }

    [Alias("TestGrainInterfaces.IDoSomethingCombinedGrain")]
    public interface IDoSomethingCombinedGrain : IDoSomethingWithMoreGrain, IDoSomethingWithMoreEmptyGrain
    {
        [Alias("SetC")]
        Task SetC(int a);

        [Alias("IncrementC")]
        Task IncrementC();

        [Alias("GetC")]
        Task<int> GetC();
    }

}
