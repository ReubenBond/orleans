namespace UnitTests.GrainInterfaces

{
    [Alias("UnitTests.GrainInterfaces.IMultifacetWriter")]
    public interface IMultifacetWriter : IGrainWithIntegerKey
    {
        [Alias("SetValue")]
        Task SetValue(int x);
        //event ValueUpdateEventHandler ValueReadEvent;
        //event ValueUpdateEventHandler CommonEvent;
    }
}
