namespace TestGrainInterfaces
{
    // The grain supports an operation to reserve a seat
    [Alias("TestGrainInterfaces.ISeatReservationGrain")]
    public interface ISeatReservationGrain : IGrainWithIntegerKey
    {
        // returns a boolean if reservation was successful
        [Alias("Reserve")]
        Task<bool> Reserve(int seatnumber, string userid);
    }



}
