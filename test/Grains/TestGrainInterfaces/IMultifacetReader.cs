namespace UnitTests.GrainInterfaces

{
    [Alias("UnitTests.GrainInterfaces.IMultifacetReader")]
    public interface IMultifacetReader : IGrainWithIntegerKey
    {
        [Alias("GetValue")]
        Task<int> GetValue();
        //event ValueUpdateEventHandler ValueUpdateEvent;
        //event ValueUpdateEventHandler CommonEvent;
    }
}
