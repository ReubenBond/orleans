namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISiloRoleBasedPlacementGrain")]
    public interface ISiloRoleBasedPlacementGrain : IGrainWithStringKey
    {
        [Alias("Ping")]
        Task<bool> Ping();
    }
}
