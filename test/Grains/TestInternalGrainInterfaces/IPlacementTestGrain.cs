namespace UnitTests.GrainInterfaces
{
    using System.Threading.Tasks;

    using Orleans;
    using Orleans.Runtime;

    [Alias("UnitTests.GrainInterfaces.IDefaultPlacementGrain")]
    internal interface IDefaultPlacementGrain : IGrainWithIntegerKey
    {
        [Alias("GetDefaultPlacement")]
        Task<PlacementStrategy> GetDefaultPlacement();
    }
}
