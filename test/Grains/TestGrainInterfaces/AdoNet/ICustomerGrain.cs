namespace Orleans.SqlUtils.StorageProvider.GrainInterfaces
{
    [Alias("Orleans.SqlUtils.StorageProvider.GrainInterfaces.ICustomerGrain")]
    public interface ICustomerGrain : IGrainWithIntegerKey
    {
        [Alias("IntroduceSelf")]
        Task<string> IntroduceSelf();

        [Alias("Set")]
        Task Set(int customerId, string firstName, string lastName);

        [Alias("AddDevice")]
        Task AddDevice(IDeviceGrain device);

        [Alias("SetRandomState")]
        Task SetRandomState();
    }
}
