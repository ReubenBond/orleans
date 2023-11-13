namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// A grain used for testing log-consistency providers.
    /// The content of this class is pretty arbitrary and messy;
    /// (don't use this as an introduction on how to use JournaledGrain)
    /// it started from SimpleGrain, but a lot of stuff got added over time 
    /// </summary>
    [Alias("UnitTests.GrainInterfaces.ILogTestGrain")]
    public interface ILogTestGrain: IGrainWithIntegerKey
    {
        // read A

        [Alias("GetAGlobal")]
        Task<int> GetAGlobal();

        [Alias("GetALocal")]
        Task<int> GetALocal();

        // read both

        [Alias("GetBothGlobal")]
        Task<AB> GetBothGlobal();

        [Alias("GetBothLocal")]
        Task<AB> GetBothLocal();

        // reservations

        [Alias("GetReservationsGlobal")]
        Task<int[]> GetReservationsGlobal();

        // version

        [Alias("GetConfirmedVersion")]
        Task<int> GetConfirmedVersion();

        // set or increment A

        [Alias("SetAGlobal")]
        Task SetAGlobal(int a);

        [Alias("SetAConditional")]
        Task<Tuple<int, bool>> SetAConditional(int a);

        [Alias("SetALocal")]
        Task SetALocal(int a);

        [Alias("IncrementALocal")]
        Task IncrementALocal();

        [Alias("IncrementAGlobal")]
        Task IncrementAGlobal();

        // set B

        [Alias("SetBGlobal")]
        Task SetBGlobal(int b);

        [Alias("SetBLocal")]
        Task SetBLocal(int b);

        // reservations

        [Alias("AddReservationLocal")]
        Task AddReservationLocal(int x);

        [Alias("RemoveReservationLocal")]
        Task RemoveReservationLocal(int x);


        [Alias("Read")]
        Task<KeyValuePair<int, object>> Read();
        [Alias("Update")]
        Task<bool> Update(IReadOnlyList<object> updates, int expectedversion);

        [Alias("GetEventLog")]
        Task<IReadOnlyList<object>> GetEventLog();


        // other operations

        [Alias("SynchronizeGlobalState")]
        Task SynchronizeGlobalState();
        [Alias("Deactivate")]
        Task Deactivate();
    }

    /// <summary>
    /// Used by unit tests. 
    /// The fields don't really have any meaning. 
    /// The point of the struct is just that a grain method can return both A and B at the same time.
    /// </summary>
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.AB")]
    public struct AB
    {
        [Id(0)]
        public int A;

        [Id(1)]
        public int B;
    }
}
