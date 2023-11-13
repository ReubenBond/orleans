namespace UnitTests.GrainInterfaces
{
    public interface IImplicitSubscriptionKeyTypeGrain
    {
        Task<int> GetValue();
    }

    [Alias("UnitTests.GrainInterfaces.IImplicitSubscriptionLongKeyGrain")]
    public interface IImplicitSubscriptionLongKeyGrain : IImplicitSubscriptionKeyTypeGrain, IGrainWithIntegerKey
    { }
}