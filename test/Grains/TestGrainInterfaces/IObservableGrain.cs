namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// A grain which returns IAsyncEnumerable
    /// </summary>
    [Alias("UnitTests.GrainInterfaces.IObservableGrain")]
    public interface IObservableGrain : IGrainWithGuidKey
    {
        [Alias("Complete")]
        ValueTask Complete();
        [Alias("Fail")]
        ValueTask Fail();
        [Alias("Deactivate")]
        ValueTask Deactivate();
        [Alias("OnNext")]
        ValueTask OnNext(string data);
        [Alias("GetValues")]
        IAsyncEnumerable<string> GetValues();

        [Alias("GetIncomingCalls")]
        ValueTask<List<(string InterfaceName, string MethodName)>> GetIncomingCalls();
    }
}
