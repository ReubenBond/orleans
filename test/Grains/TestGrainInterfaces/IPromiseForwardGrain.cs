namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IPromiseForwardGrain")]
    public interface IPromiseForwardGrain : ISimpleGrain, ISimpleGrainAsync
    {
    }
}
