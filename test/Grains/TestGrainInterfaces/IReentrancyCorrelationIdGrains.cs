namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IFirstGrain")]
    public interface IFirstGrain : IGrainWithGuidKey
    {
        [Alias("Start")]
        Task Start(Guid guid1, Guid guid2);
    }

    [Alias("UnitTests.GrainInterfaces.ISecondGrain")]
    public interface ISecondGrain : IGrainWithGuidKey
    {
        [Alias("SecondGrainMethod")]
        Task SecondGrainMethod(Guid guid);
    }

    [Alias("UnitTests.GrainInterfaces.IThirdGrain")]
    public interface IThirdGrain : IGrainWithStringKey
    {
        [Alias("ThirdGrainMethod")]
        Task ThirdGrainMethod(Guid userId);
    }
}
