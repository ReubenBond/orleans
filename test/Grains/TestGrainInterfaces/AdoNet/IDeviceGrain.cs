namespace Orleans.SqlUtils.StorageProvider.GrainInterfaces
{
    [Alias("Orleans.SqlUtils.StorageProvider.GrainInterfaces.IDeviceGrain")]
    public interface IDeviceGrain : IGrainWithGuidKey
    {
        [Alias("GetSerialNumber")]
        Task<string> GetSerialNumber();

        [Alias("SetOwner")]
        Task SetOwner(ICustomerGrain customer);
    }
}