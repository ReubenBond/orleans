namespace TestGrainInterfaces
{

    /// <summary>
    /// A grain that maintains a number of counters, indexed by a string key
    /// </summary>
    [Alias("TestGrainInterfaces.ICountersGrain")]
    public interface ICountersGrain : Orleans.IGrainWithIntegerKey
    {
        /// <summary> Updates the counter for the given key by the given amount </summary>
        [Alias("Add")]
        Task Add(string key, int amount, bool wait_for_confirmation);

        /// <summary> Resets all counters to zero </summary>
        [Alias("Reset")]
        Task Reset(bool wait_for_confirmation);

        /// <summary> Retrieves the tentative value of the counter for the given key </summary>
        [Alias("GetTentativeCount")]
        Task<int> GetTentativeCount(string key);

        /// <summary> Retrieves the tentative value of all counters </summary>
        [Alias("GetTentativeState")]
        Task<IReadOnlyDictionary<string, int>> GetTentativeState();

        /// <summary> Retrieves the confirmed value of all counters </summary>
        [Alias("GetConfirmedState")]
        Task<IReadOnlyDictionary<string, int>> GetConfirmedState();

        /// <summary> Confirm all events </summary>
        [Alias("ConfirmAllPreviouslyRaisedEvents")]
        Task ConfirmAllPreviouslyRaisedEvents();

    }
}
